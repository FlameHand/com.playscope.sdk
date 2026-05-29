// playscope_crash — Android native crash capture (signal handler).
//
// Contract: at install() time we register sigaction() handlers for the
// fatal signals listed in FATAL_SIGNALS. When one fires, we run in
// interrupt context on a pre-allocated alternate stack with a corrupted
// process state. The handler:
//   1. captures a backtrace via _Unwind_Backtrace,
//   2. resolves each frame to {module, offset} via dladdr,
//   3. writes a single-line JSON record to {crash_dir}/{sid}.json.tmp,
//   4. fsyncs + renames to the final name (atomic),
//   5. restores the previous handler and re-raises so the OS still
//      produces a tombstone.
//
// All paths between signal entry and re-raise MUST be async-signal-safe.
// Allowed: open/write/close/fsync/lseek, clock_gettime, gettid, getpid,
// _Unwind_Backtrace, dladdr (de-facto safe on bionic), memcpy/strlen/
// strncpy. FORBIDDEN: malloc, printf-family, fopen-family, any JNI,
// __android_log_*, pthread mutex, C++ exceptions/RTTI, std::*.
//
// All buffers used in the handler are pre-allocated globals.

#include <jni.h>
#include <signal.h>
#include <ucontext.h>
#include <unistd.h>
#include <fcntl.h>
#include <dlfcn.h>
#include <stdio.h>     // rename(2) prototype
#include <string.h>
#include <stdint.h>
#include <errno.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <time.h>
#include <unwind.h>
#include <android/log.h>

#define LOG_TAG "PlayScope/CrashNative"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace
{
    constexpr size_t CRASH_DIR_MAX = 512;
    constexpr size_t SESSION_ID_MAX = 64;
    constexpr size_t PATH_MAX_LEN = CRASH_DIR_MAX + SESSION_ID_MAX + 16;
    constexpr size_t MAX_FRAMES = 64;
    // Realistic IL2CPP frame on Android 10+ scoped install dirs:
    // /data/app/~~base64==/pkg-base64==/lib/arm64/libil2cpp.so ≈ 110 chars
    // → ~160 B per JSON frame. 64 × 160 + header ≈ 10.4 KB. Pad to 16 KB.
    constexpr size_t SCRATCH_SIZE = 16384;
    // Reserve tail room for closing "]}\n" + safety. Frames stop appending
    // once pos crosses this watermark so the JSON is always well-formed.
    constexpr size_t SCRATCH_CLOSE_RESERVE = 256;
    // SIGSTKSZ on some Android NDK versions resolves to a runtime call
    // (sysconf), so this can't be constexpr. 64 KB matches Crashpad's
    // alt-stack size — comfortable headroom for _Unwind_Backtrace through
    // deep IL2CPP-generated managed trampolines.
    constexpr size_t ALT_STACK_SIZE = 64 * 1024;

    constexpr int FATAL_SIGNALS[] = { SIGSEGV, SIGABRT, SIGBUS, SIGILL, SIGFPE };
    constexpr int NUM_FATAL_SIGNALS = sizeof(FATAL_SIGNALS) / sizeof(FATAL_SIGNALS[0]);

    // ---- install-time state (only read in handler) ----
    char g_crash_dir[CRASH_DIR_MAX];
    // session_id is read in handler context AFTER the C# layer rotates it.
    // We accept a torn read here as a documented best-effort. The race
    // window has THREE observable outcomes if a crash hits mid-rotation:
    //   (a) handler sees old id → file lands under previous session
    //   (b) handler sees new id → file lands under new session
    //   (c) handler sees mix: new chars at start, stale tail bytes, or
    //       (worse) new NUL terminator early with stale leading bytes →
    //       filename labels a session_id that NEVER existed
    // Day 3 recovery MUST NOT assume the labeled session_id always
    // corresponds to a real PG session — orphan crash files emit as
    // standalone exception events keyed by whatever session_id is in
    // the filename. Same posture as Crashpad's session annotation.
    // Mark volatile so the compiler does not cache stale values.
    volatile char g_session_id[SESSION_ID_MAX];

    struct sigaction g_prev[NUM_FATAL_SIGNALS];
    bool g_installed = false;
    stack_t g_alt_stack;
    char g_alt_stack_mem[ALT_STACK_SIZE];

    // ---- handler-only scratch (no malloc in handler) ----
    char g_scratch[SCRATCH_SIZE];
    uintptr_t g_frames[MAX_FRAMES];
    // Raw _Unwind_Backtrace output before we strip the handler/shim prefix
    // and splice in the real fault frame. Separate buffer so the rebuild
    // into g_frames doesn't overwrite entries it still needs to read.
    uintptr_t g_raw_frames[MAX_FRAMES];
    size_t g_frame_count;
    char g_path_buf[PATH_MAX_LEN];
    char g_tmp_path_buf[PATH_MAX_LEN];

    // -------- async-signal-safe helpers --------

    // Strictly safe: only reads/writes the provided buffer.
    size_t safe_strlen(const char* s)
    {
        size_t n = 0;
        while (s[n] != '\0')
        {
            ++n;
        }
        return n;
    }

    // Append src to dst at *pos (bounded). Updates *pos. Signal-safe.
    void append(char* dst, size_t cap, size_t* pos, const char* src)
    {
        size_t n = safe_strlen(src);
        size_t avail = (cap > *pos) ? (cap - *pos - 1) : 0;
        if (n > avail)
        {
            n = avail;
        }
        memcpy(dst + *pos, src, n);
        *pos += n;
        dst[*pos] = '\0';
    }

    void append_bytes(char* dst, size_t cap, size_t* pos, const char* src, size_t n)
    {
        size_t avail = (cap > *pos) ? (cap - *pos - 1) : 0;
        if (n > avail)
        {
            n = avail;
        }
        memcpy(dst + *pos, src, n);
        *pos += n;
        dst[*pos] = '\0';
    }

    // Write unsigned decimal. Signal-safe — no printf. Returns bytes written.
    size_t u64_to_dec(uint64_t v, char* out, size_t cap)
    {
        if (cap == 0)
        {
            return 0;
        }
        char tmp[32];
        size_t n = 0;
        if (v == 0)
        {
            tmp[n++] = '0';
        }
        while (v > 0 && n < sizeof(tmp))
        {
            tmp[n++] = static_cast<char>('0' + (v % 10));
            v /= 10;
        }
        size_t written = 0;
        while (n > 0 && written + 1 < cap)
        {
            out[written++] = tmp[--n];
        }
        out[written] = '\0';
        return written;
    }

    // Write signed decimal. Signal-safe.
    size_t i64_to_dec(int64_t v, char* out, size_t cap)
    {
        if (cap == 0)
        {
            return 0;
        }
        size_t off = 0;
        uint64_t u;
        if (v < 0)
        {
            out[off++] = '-';
            u = static_cast<uint64_t>(-(v + 1)) + 1u;
        }
        else
        {
            u = static_cast<uint64_t>(v);
        }
        return off + u64_to_dec(u, out + off, cap - off);
    }

    // Write "0x" + hex (no leading zeros). Signal-safe.
    size_t u64_to_hex(uint64_t v, char* out, size_t cap)
    {
        if (cap < 4)
        {
            if (cap > 0) { out[0] = '\0'; }
            return 0;
        }
        out[0] = '0';
        out[1] = 'x';
        size_t off = 2;
        char tmp[24];
        size_t n = 0;
        if (v == 0)
        {
            tmp[n++] = '0';
        }
        while (v > 0 && n < sizeof(tmp))
        {
            unsigned d = static_cast<unsigned>(v & 0xFu);
            tmp[n++] = static_cast<char>(d < 10 ? ('0' + d) : ('a' + (d - 10)));
            v >>= 4;
        }
        while (n > 0 && off + 1 < cap)
        {
            out[off++] = tmp[--n];
        }
        out[off] = '\0';
        return off;
    }

    // Append a uint64 as decimal into the scratch JSON.
    void append_u64(char* dst, size_t cap, size_t* pos, uint64_t v)
    {
        char tmp[32];
        size_t n = u64_to_dec(v, tmp, sizeof(tmp));
        append_bytes(dst, cap, pos, tmp, n);
    }

    void append_i64(char* dst, size_t cap, size_t* pos, int64_t v)
    {
        char tmp[32];
        size_t n = i64_to_dec(v, tmp, sizeof(tmp));
        append_bytes(dst, cap, pos, tmp, n);
    }

    void append_hex_quoted(char* dst, size_t cap, size_t* pos, uint64_t v)
    {
        char tmp[32];
        size_t n = u64_to_hex(v, tmp, sizeof(tmp));
        append(dst, cap, pos, "\"");
        append_bytes(dst, cap, pos, tmp, n);
        append(dst, cap, pos, "\"");
    }

    // Minimal JSON-escape: only escapes the chars that MUST be escaped
    // (\" \\ control chars). Module names are filesystem paths so this
    // covers everything we realistically encounter on Android.
    void append_json_string(char* dst, size_t cap, size_t* pos, const char* src)
    {
        append(dst, cap, pos, "\"");
        if (src == nullptr)
        {
            append(dst, cap, pos, "\"");
            return;
        }
        for (size_t i = 0; src[i] != '\0'; ++i)
        {
            char c = src[i];
            if (c == '"' || c == '\\')
            {
                char esc[3] = { '\\', c, '\0' };
                append(dst, cap, pos, esc);
            }
            else if (static_cast<unsigned char>(c) < 0x20)
            {
                append(dst, cap, pos, "\\u00");
                const char hex[] = "0123456789abcdef";
                char hh[3] = { hex[(c >> 4) & 0xF], hex[c & 0xF], '\0' };
                append(dst, cap, pos, hh);
            }
            else
            {
                char ch[2] = { c, '\0' };
                append(dst, cap, pos, ch);
            }
        }
        append(dst, cap, pos, "\"");
    }

    // -------- backtrace --------

    struct UnwindCtx
    {
        size_t count;
        uintptr_t* frames;
        size_t max;
    };

    _Unwind_Reason_Code unwind_cb(struct _Unwind_Context* ctx, void* arg)
    {
        UnwindCtx* uc = static_cast<UnwindCtx*>(arg);
        if (uc->count >= uc->max)
        {
            return _URC_END_OF_STACK;
        }
        uintptr_t pc = _Unwind_GetIP(ctx);
        if (pc != 0)
        {
            uc->frames[uc->count++] = pc;
        }
        return _URC_NO_REASON;
    }

    // Extract the interrupted instruction pointer (the real fault site) from
    // the signal ucontext. Distinct from si_addr, which is the faulting DATA
    // address. Returns 0 when the context is unavailable or the arch is
    // unknown — the caller then keeps the raw unwind unchanged.
    uintptr_t extract_crash_pc(void* uctx)
    {
        if (uctx == nullptr)
        {
            return 0;
        }
        const ucontext_t* uc = static_cast<const ucontext_t*>(uctx);
#if defined(__aarch64__)
        return static_cast<uintptr_t>(uc->uc_mcontext.pc);
#elif defined(__arm__)
        return static_cast<uintptr_t>(uc->uc_mcontext.arm_pc);
#elif defined(__x86_64__)
        return static_cast<uintptr_t>(uc->uc_mcontext.gregs[REG_RIP]);
#elif defined(__i386__)
        return static_cast<uintptr_t>(uc->uc_mcontext.gregs[REG_EIP]);
#else
        (void)uc;
        return 0;
#endif
    }

    // -------- file write --------

    // Build "{crash_dir}/{session_id}.json[.tmp]" into out. Returns false
    // if buffer would overflow.
    bool build_path(char* out, size_t cap, bool tmp)
    {
        size_t pos = 0;
        out[0] = '\0';
        append(out, cap, &pos, g_crash_dir);
        size_t dlen = safe_strlen(g_crash_dir);
        if (dlen == 0 || g_crash_dir[dlen - 1] != '/')
        {
            append(out, cap, &pos, "/");
        }
        // Copy g_session_id manually — it is volatile. memcpy is fine but
        // we want to bound the read at the first NUL or SESSION_ID_MAX.
        char sid_local[SESSION_ID_MAX];
        for (size_t i = 0; i < SESSION_ID_MAX; ++i)
        {
            char c = g_session_id[i];
            sid_local[i] = c;
            if (c == '\0')
            {
                break;
            }
        }
        sid_local[SESSION_ID_MAX - 1] = '\0';
        append(out, cap, &pos, sid_local);
        append(out, cap, &pos, ".json");
        if (tmp)
        {
            append(out, cap, &pos, ".tmp");
        }
        return pos < cap;
    }

    // Write the whole buffer or fail. Signal-safe (raw write loop).
    bool write_all(int fd, const char* buf, size_t n)
    {
        size_t off = 0;
        while (off < n)
        {
            ssize_t w = write(fd, buf + off, n - off);
            if (w < 0)
            {
                if (errno == EINTR)
                {
                    continue;
                }
                return false;
            }
            off += static_cast<size_t>(w);
        }
        return true;
    }

    // -------- the handler --------

    void handler(int sig, siginfo_t* si, void* uctx)
    {
        // 1. Snapshot capture data into static buffers.
        int64_t tid = static_cast<int64_t>(gettid());
        struct timespec ts;
        int64_t unix_ms = 0;
        if (clock_gettime(CLOCK_REALTIME, &ts) == 0)
        {
            unix_ms = static_cast<int64_t>(ts.tv_sec) * 1000
                    + static_cast<int64_t>(ts.tv_nsec / 1000000);
        }
        int si_code_val = (si != nullptr) ? si->si_code : 0;
        uintptr_t fault_addr = (si != nullptr) ? reinterpret_cast<uintptr_t>(si->si_addr) : 0;

        // 2. Capture backtrace.
        //
        // _Unwind_Backtrace runs inside the handler, so its leading frames are
        // the handler itself + the signal-chaining shims (libsigchain, plus any
        // coexisting native crash SDK such as Embrace) + the kernel signal
        // trampoline. The genuine crashing frame only appears AFTER that prefix.
        // Emitting the raw list would make frames[0] always libplayscope_crash.so
        // and collapse every native crash into one fingerprint.
        //
        // Fix: take the real fault PC from the interrupted ucontext, make it
        // frames[0], then append the raw frames that follow the matching
        // interrupted frame (the genuine callers), dropping the handler/shim
        // prefix. If the PC is unavailable or not found in the raw list we still
        // seed frames[0] with it when known (so the fingerprint + headline are
        // correct), accepting a noisier tail.
        uintptr_t crash_pc = extract_crash_pc(uctx);

        size_t raw_count = 0;
        {
            UnwindCtx uc = { 0, g_raw_frames, MAX_FRAMES };
            _Unwind_Backtrace(unwind_cb, &uc);
            raw_count = uc.count;
        }

        g_frame_count = 0;
        size_t copy_start = 0;
        if (crash_pc != 0)
        {
            g_frames[g_frame_count++] = crash_pc;
            for (size_t i = 0; i < raw_count; ++i)
            {
                if (g_raw_frames[i] == crash_pc)
                {
                    copy_start = i + 1;
                    break;
                }
            }
        }
        for (size_t i = copy_start; i < raw_count && g_frame_count < MAX_FRAMES; ++i)
        {
            g_frames[g_frame_count++] = g_raw_frames[i];
        }

        // 3. Build the JSON in g_scratch.
        size_t pos = 0;
        g_scratch[0] = '\0';
        append(g_scratch, SCRATCH_SIZE, &pos, "{\"schema_version\":1,\"signal\":");
        append_i64(g_scratch, SCRATCH_SIZE, &pos, static_cast<int64_t>(sig));
        append(g_scratch, SCRATCH_SIZE, &pos, ",\"si_code\":");
        append_i64(g_scratch, SCRATCH_SIZE, &pos, static_cast<int64_t>(si_code_val));
        append(g_scratch, SCRATCH_SIZE, &pos, ",\"fault_addr\":");
        append_hex_quoted(g_scratch, SCRATCH_SIZE, &pos, fault_addr);
        append(g_scratch, SCRATCH_SIZE, &pos, ",\"thread_tid\":");
        append_i64(g_scratch, SCRATCH_SIZE, &pos, tid);
        append(g_scratch, SCRATCH_SIZE, &pos, ",\"captured_at_unix_ms\":");
        append_i64(g_scratch, SCRATCH_SIZE, &pos, unix_ms);
        append(g_scratch, SCRATCH_SIZE, &pos, ",\"session_id\":");

        // session_id read — same volatile pattern as build_path().
        char sid_local[SESSION_ID_MAX];
        for (size_t i = 0; i < SESSION_ID_MAX; ++i)
        {
            char c = g_session_id[i];
            sid_local[i] = c;
            if (c == '\0')
            {
                break;
            }
        }
        sid_local[SESSION_ID_MAX - 1] = '\0';
        append_json_string(g_scratch, SCRATCH_SIZE, &pos, sid_local);

        append(g_scratch, SCRATCH_SIZE, &pos, ",\"frames\":[");
        for (size_t i = 0; i < g_frame_count; ++i)
        {
            // Hard stop before exhausting the scratch buffer so the closing
            // "]}\n" always fits. Truncated frames are dropped, output stays
            // well-formed JSON the recovery path can parse.
            if (pos > SCRATCH_SIZE - SCRATCH_CLOSE_RESERVE)
            {
                break;
            }
            if (i > 0)
            {
                append(g_scratch, SCRATCH_SIZE, &pos, ",");
            }
            uintptr_t pc = g_frames[i];
            Dl_info info;
            const char* module = "";
            uint64_t offset = 0;
            if (dladdr(reinterpret_cast<void*>(pc), &info) != 0 && info.dli_fname != nullptr)
            {
                module = info.dli_fname;
                offset = static_cast<uint64_t>(pc) - reinterpret_cast<uintptr_t>(info.dli_fbase);
            }
            append(g_scratch, SCRATCH_SIZE, &pos, "{\"pc\":");
            append_hex_quoted(g_scratch, SCRATCH_SIZE, &pos, static_cast<uint64_t>(pc));
            append(g_scratch, SCRATCH_SIZE, &pos, ",\"module\":");
            append_json_string(g_scratch, SCRATCH_SIZE, &pos, module);
            append(g_scratch, SCRATCH_SIZE, &pos, ",\"offset\":");
            append_u64(g_scratch, SCRATCH_SIZE, &pos, offset);
            append(g_scratch, SCRATCH_SIZE, &pos, "}");
        }
        append(g_scratch, SCRATCH_SIZE, &pos, "]}\n");

        // 4. Write to .tmp, fsync, rename.
        if (build_path(g_path_buf, sizeof(g_path_buf), false)
            && build_path(g_tmp_path_buf, sizeof(g_tmp_path_buf), true))
        {
            int fd = open(g_tmp_path_buf, O_WRONLY | O_CREAT | O_TRUNC, 0600);
            if (fd >= 0)
            {
                if (write_all(fd, g_scratch, pos))
                {
                    fsync(fd);
                }
                close(fd);
                // rename(2) is async-signal-safe per POSIX.
                rename(g_tmp_path_buf, g_path_buf);
            }
            else
            {
                // crash dir doesn't exist or perms wrong — emit a breadcrumb
                // to stderr (signal-safe) and continue. Stays out of the
                // way of the tombstone.
                const char msg[] = "PlayScope: open(crash_tmp) failed\n";
                write(2, msg, sizeof(msg) - 1);
            }
        }

        // 5. Restore previous handler + re-raise so the OS still tombstones.
        for (int i = 0; i < NUM_FATAL_SIGNALS; ++i)
        {
            if (FATAL_SIGNALS[i] == sig)
            {
                sigaction(sig, &g_prev[i], nullptr);
                break;
            }
        }
        // Reset signal mask so re-raise isn't blocked.
        sigset_t set;
        sigemptyset(&set);
        sigaddset(&set, sig);
        sigprocmask(SIG_UNBLOCK, &set, nullptr);
        raise(sig);
    }

    // -------- install (NOT signal-safe; called from JNI) --------

    int install_impl(const char* crash_dir, const char* session_id)
    {
        if (g_installed)
        {
            return 0;
        }

        if (crash_dir == nullptr || session_id == nullptr)
        {
            LOGE("install: null arg crash_dir=%p sid=%p", crash_dir, session_id);
            return -1;
        }

        size_t dlen = strlen(crash_dir);
        if (dlen == 0 || dlen >= CRASH_DIR_MAX)
        {
            LOGE("install: crash_dir length %zu out of range", dlen);
            return -2;
        }
        strncpy(g_crash_dir, crash_dir, CRASH_DIR_MAX - 1);
        g_crash_dir[CRASH_DIR_MAX - 1] = '\0';

        size_t slen = strlen(session_id);
        if (slen >= SESSION_ID_MAX)
        {
            slen = SESSION_ID_MAX - 1;
        }
        for (size_t i = 0; i < slen; ++i)
        {
            g_session_id[i] = session_id[i];
        }
        g_session_id[slen] = '\0';

        g_alt_stack.ss_sp = g_alt_stack_mem;
        g_alt_stack.ss_size = ALT_STACK_SIZE;
        g_alt_stack.ss_flags = 0;
        if (sigaltstack(&g_alt_stack, nullptr) != 0)
        {
            LOGW("install: sigaltstack failed errno=%d", errno);
            // Continue anyway — handler may still work on the main stack
            // for everything except deep stack overflows.
        }

        struct sigaction sa;
        memset(&sa, 0, sizeof(sa));
        sa.sa_sigaction = &handler;
        sa.sa_flags = SA_ONSTACK | SA_SIGINFO | SA_RESETHAND;
        sigemptyset(&sa.sa_mask);

        for (int i = 0; i < NUM_FATAL_SIGNALS; ++i)
        {
            if (sigaction(FATAL_SIGNALS[i], &sa, &g_prev[i]) != 0)
            {
                LOGW("install: sigaction(%d) failed errno=%d", FATAL_SIGNALS[i], errno);
            }
        }

        g_installed = true;
        char sid_log[SESSION_ID_MAX];
        for (size_t i = 0; i < SESSION_ID_MAX; ++i)
        {
            sid_log[i] = g_session_id[i];
            if (sid_log[i] == '\0')
            {
                break;
            }
        }
        sid_log[SESSION_ID_MAX - 1] = '\0';
        LOGI("install: ok dir=%s sid=%s", g_crash_dir, sid_log);
        return 0;
    }

    void update_session_id_impl(const char* session_id)
    {
        if (!g_installed || session_id == nullptr)
        {
            return;
        }
        size_t slen = strlen(session_id);
        if (slen >= SESSION_ID_MAX)
        {
            slen = SESSION_ID_MAX - 1;
        }
        // Byte-by-byte write into volatile storage. A concurrent crash
        // handler may observe a torn read; we accept that — see the
        // comment on g_session_id.
        for (size_t i = 0; i < slen; ++i)
        {
            g_session_id[i] = session_id[i];
        }
        g_session_id[slen] = '\0';
    }
} // namespace

extern "C"
{

JNIEXPORT jint JNICALL
Java_com_playscope_sdk_PlayScopeCrash_nativeInstall(
    JNIEnv* env, jclass /*clazz*/, jstring jcrash_dir, jstring jsession_id)
{
    const char* cdir = env->GetStringUTFChars(jcrash_dir, nullptr);
    const char* csid = env->GetStringUTFChars(jsession_id, nullptr);
    int rc = install_impl(cdir, csid);
    if (cdir != nullptr) { env->ReleaseStringUTFChars(jcrash_dir, cdir); }
    if (csid != nullptr) { env->ReleaseStringUTFChars(jsession_id, csid); }
    return rc;
}

JNIEXPORT void JNICALL
Java_com_playscope_sdk_PlayScopeCrash_nativeUpdateSessionId(
    JNIEnv* env, jclass /*clazz*/, jstring jsession_id)
{
    if (jsession_id == nullptr)
    {
        return;
    }
    const char* csid = env->GetStringUTFChars(jsession_id, nullptr);
    update_session_id_impl(csid);
    if (csid != nullptr) { env->ReleaseStringUTFChars(jsession_id, csid); }
}

} // extern "C"
