using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// C# side of native crash capture. Installs the Android signal handler via
    /// the <c>PlayScopeCrash</c> JNI bridge, reads back the JSON crash records the
    /// handler wrote on the prior death, and serializes them into <c>exception</c>
    /// log records for <see cref="SessionRecovery"/> to append.
    /// <see cref="PreInit"/> runs once on init (before recovery);
    /// <see cref="OnSessionInitialized"/> runs after every session_id is set
    /// (init + rotation) so the handler always writes the in-flight session_id.
    /// Android-only at runtime; other platforms create the dir and no-op.
    /// </summary>
    internal static class PlayScopeCrashCollector
    {
        private const string LOG_TAG = "[PlayScope/CrashCollector]";
        private const int SCHEMA_VERSION = 1;
        private const int MAX_FRAMES_PER_RECORD = 64;
        private const int MAX_RAW_FILE_BYTES = 64 * 1024;
        private const int MAX_FILES_PER_DRAIN = 32;

        private static string _crashDir;
        private static bool _initialised;
        private static bool _crashCaptureInstalled;
        private static readonly HashSet<string> _consumedSessionIds =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Crash dir path. Empty string (never null) before <see cref="PreInit"/>
        /// succeeds, so callers can pass it to native APIs unguarded.
        /// </summary>
        internal static string CrashDir => _crashDir ?? string.Empty;

        /// <summary>
        /// Called once during SDK init before any session work. Ensures
        /// the crash dir exists. Non-throwing — on disk failure the
        /// collector stays uninitialised and every subsequent call is a
        /// no-op.
        /// </summary>
        internal static void PreInit()
        {
            if (_initialised)
            {
                return;
            }
            try
            {
                _crashDir = Path.Combine(Application.persistentDataPath, "PlayScope", "crash");
                Directory.CreateDirectory(_crashDir);
                _initialised = true;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"{LOG_TAG} PreInit: failed to create crash dir — native crash capture disabled", ex);
                _crashDir = null;
                _initialised = false;
            }
        }

        /// <summary>
        /// Called after the session_id is determined (initial init AND
        /// every rotation). Installs the native handler if not yet
        /// installed, or refreshes the active session_id on subsequent
        /// calls. Android-only at runtime — every other platform is a
        /// no-op (the Java de-dupe guard handles repeat install calls).
        /// </summary>
        internal static void OnSessionInitialized(string sessionId)
        {
            if (!_initialised)
            {
                return;
            }
            if (string.IsNullOrEmpty(sessionId))
            {
                PlayScopeLog.Warning($"{LOG_TAG} OnSessionInitialized: empty sessionId — skipping handler install/refresh.");
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var cls = new AndroidJavaClass("com.playscope.sdk.PlayScopeCrash"))
                {
                    cls.CallStatic("install", _crashDir, sessionId);
                }
                _crashCaptureInstalled = true;
            }
            catch (Exception ex)
            {
                _crashCaptureInstalled = false;
                PlayScopeLog.Warning(
                    $"{LOG_TAG} OnSessionInitialized: AndroidJavaClass install failed — " +
                    $"native crash capture inactive. crashDir={_crashDir} sessionId={sessionId}",
                    ex);
            }
#endif
        }

        /// <summary>
        /// Returns + deletes the crash record for this session_id, or null if none
        /// matches / it was malformed. Runs single-threaded during recovery.
        /// </summary>
        internal static NativeCrashRecord TryConsumeCrashFor(string sessionId)
        {
            if (!_initialised || string.IsNullOrEmpty(sessionId))
            {
                return null;
            }
            if (!IsSafeSessionIdSegment(sessionId))
            {
                PlayScopeLog.Warning(
                    $"{LOG_TAG} TryConsumeCrashFor: rejecting unsafe sessionId={sessionId}");
                return null;
            }
            string path;
            try
            {
                path = Path.Combine(_crashDir, sessionId + ".json");
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning(
                    $"{LOG_TAG} TryConsumeCrashFor: path build failed for sessionId={sessionId}",
                    ex);
                return null;
            }
            if (!File.Exists(path))
            {
                return null;
            }
            var record = ReadAndDelete(path, expectedSessionId: sessionId);
            // Mark both the filename stem and the parsed JSON session_id so
            // DrainOrphans skips this file even if the two ever diverge.
            _consumedSessionIds.Add(sessionId);
            if (record != null && !string.IsNullOrEmpty(record.SessionId))
            {
                _consumedSessionIds.Add(record.SessionId);
            }
            return record;
        }

        /// <summary>
        /// Returns parsed crash records for every file in the crash dir
        /// that has NOT yet been consumed via
        /// <see cref="TryConsumeCrashFor"/>. Each consumed file is
        /// deleted. Returns an empty list when nothing matched OR the
        /// collector is uninitialised.
        /// </summary>
        internal static IReadOnlyList<NativeCrashRecord> DrainOrphans()
        {
            var results = new List<NativeCrashRecord>();
            if (!_initialised)
            {
                return results;
            }
            string[] files;
            try
            {
                files = Directory.GetFiles(_crashDir, "*.json");
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning(
                    $"{LOG_TAG} DrainOrphans: failed to list crash dir {_crashDir}",
                    ex);
                return results;
            }
            int processed = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (processed >= MAX_FILES_PER_DRAIN)
                {
                    PlayScopeLog.Warning(
                        $"{LOG_TAG} DrainOrphans: cap of {MAX_FILES_PER_DRAIN} reached — " +
                        $"{files.Length - processed} remaining file(s) will be processed next launch.");
                    break;
                }
                var file = files[i];
                var stem = Path.GetFileNameWithoutExtension(file);
                if (_consumedSessionIds.Contains(stem))
                {
                    continue;
                }
                if (!IsSafeSessionIdSegment(stem))
                {
                    PlayScopeLog.Warning(
                        $"{LOG_TAG} DrainOrphans: rejecting unsafe filename stem={stem} — deleting.");
                    TryDelete(file);
                    continue;
                }
                var record = ReadAndDelete(file, expectedSessionId: stem);
                _consumedSessionIds.Add(stem);
                if (record != null)
                {
                    if (!string.IsNullOrEmpty(record.SessionId) && IsSafeSessionIdSegment(record.SessionId))
                    {
                        _consumedSessionIds.Add(record.SessionId);
                        results.Add(record);
                    }
                    else
                    {
                        PlayScopeLog.Warning(
                            $"{LOG_TAG} DrainOrphans: parsed sessionId is unsafe — dropping record. stem={stem}");
                    }
                }
                processed++;
            }
            return results;
        }

        /// <summary>
        /// Guards a session-id-derived path segment: real session ids are GUID
        /// strings, so reject anything with separators or traversal sequences so a
        /// corrupt/malicious crash file can't escape the crash dir. Defense in depth.
        /// </summary>
        private static bool IsSafeSessionIdSegment(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                return false;
            }
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isAlphaNumDash =
                    (c >= '0' && c <= '9') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    c == '-';
                if (!isAlphaNumDash)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Builds the exception-event metadata blob. The
        /// <c>native_source=playscope_crash</c> discriminator separates these from
        /// managed exceptions captured by <see cref="EventPipeline.EnqueueLog"/>.
        /// </summary>
        internal static string BuildExceptionMetadataJson(NativeCrashRecord record)
        {
            if (record == null)
            {
                return "{}";
            }
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendJsonStringPair(sb, "native_source", "playscope_crash", isFirst: true);
            AppendJsonStringPair(sb, "native_signal", SignalName(record.Signal), isFirst: false);
            AppendJsonIntPair(sb, "native_signal_number", record.Signal, isFirst: false);
            AppendJsonIntPair(sb, "native_si_code", record.SiCode, isFirst: false);
            AppendJsonStringPair(sb, "native_fault_addr", record.FaultAddr ?? "0x0", isFirst: false);
            AppendJsonLongPair(sb, "native_thread_tid", record.ThreadTid, isFirst: false);
            AppendJsonLongPair(sb, "native_captured_at_unix_ms", record.CapturedAtUnixMs, isFirst: false);
            sb.Append(",\"native_frames\":");
            AppendFramesArray(sb, record.Frames);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Builds the human-readable summary line that ends up as the log
        /// record's <c>message</c> field. Format mirrors the spec:
        /// <c>"Native crash SIGSEGV at 0x0 — libil2cpp.so+0x1234abc"</c>
        /// when frames exist, otherwise just the signal + addr.
        /// </summary>
        internal static string BuildMessage(NativeCrashRecord record)
        {
            if (record == null)
            {
                return "Native crash";
            }
            var sigName = SignalName(record.Signal);
            var addr = string.IsNullOrEmpty(record.FaultAddr) ? "0x0" : record.FaultAddr;
            if (record.Frames == null || record.Frames.Count == 0)
            {
                return $"Native crash {sigName} at {addr}";
            }
            var top = record.Frames[0];
            var module = string.IsNullOrEmpty(top.Module) ? "<unknown>" : top.Module;
            return $"Native crash {sigName} at {addr} — {module}+0x{top.Offset:x}";
        }

        /// <summary>
        /// Builds the stack_trace blob the backend's SymbolResolverService
        /// reads on the dashboard side. One frame per line, format
        /// <c>"  at {module}+0x{offset:x}  (pc={pc})"</c>.
        /// </summary>
        internal static string BuildStackTrace(NativeCrashRecord record)
        {
            if (record == null || record.Frames == null || record.Frames.Count == 0)
            {
                return string.Empty;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < record.Frames.Count; i++)
            {
                var f = record.Frames[i];
                var module = string.IsNullOrEmpty(f.Module) ? "<unknown>" : f.Module;
                var pc = string.IsNullOrEmpty(f.Pc) ? "0x0" : f.Pc;
                sb.Append("  at ").Append(module).Append("+0x").AppendFormat("{0:x}", f.Offset)
                    .Append("  (pc=").Append(pc).Append(')');
                if (i + 1 < record.Frames.Count)
                {
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps signal number to canonical name (SIGSEGV, SIGABRT, …).
        /// Unknown numbers come back as <c>"SIG{n}"</c>. SIGKILL listed
        /// for completeness — the handler does not (and cannot) trap it.
        /// </summary>
        internal static string SignalName(int signal)
        {
            switch (signal)
            {
                case 4: return "SIGILL";
                case 6: return "SIGABRT";
                case 7: return "SIGBUS";
                case 8: return "SIGFPE";
                case 9: return "SIGKILL";
                case 11: return "SIGSEGV";
                default: return "SIG" + signal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static NativeCrashRecord ReadAndDelete(string path, string expectedSessionId)
        {
            string raw;
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0)
                {
                    PlayScopeLog.Warning($"{LOG_TAG} ReadAndDelete: empty crash file path={path} — deleting.");
                    TryDelete(path);
                    return null;
                }
                if (info.Length > MAX_RAW_FILE_BYTES)
                {
                    PlayScopeLog.Warning(
                        $"{LOG_TAG} ReadAndDelete: oversized crash file ({info.Length} bytes) path={path} — deleting.");
                    TryDelete(path);
                    return null;
                }
                raw = File.ReadAllText(path, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"{LOG_TAG} ReadAndDelete: read failed path={path}", ex);
                TryDelete(path);
                return null;
            }
            var record = TryParse(raw, expectedSessionId, path);
            TryDelete(path);
            return record;
        }

        private static NativeCrashRecord TryParse(string raw, string expectedSessionId, string path)
        {
            try
            {
                var parser = new MiniJsonParser(raw);
                var root = parser.ParseValue() as Dictionary<string, object>;
                if (root == null)
                {
                    PlayScopeLog.Warning(
                        $"{LOG_TAG} TryParse: root is not an object path={path} preview=" +
                        Preview(raw));
                    return null;
                }
                int schema = ReadInt(root, "schema_version", 0);
                if (schema != SCHEMA_VERSION)
                {
                    PlayScopeLog.Info(
                        $"{LOG_TAG} TryParse: skipping crash file from incompatible schema_version={schema} path={path}");
                    return null;
                }
                var fileSessionId = ReadString(root, "session_id", null);
                var sessionId = !string.IsNullOrEmpty(fileSessionId) ? fileSessionId : expectedSessionId;
                if (!string.IsNullOrEmpty(fileSessionId) &&
                    !string.IsNullOrEmpty(expectedSessionId) &&
                    !string.Equals(fileSessionId, expectedSessionId, StringComparison.Ordinal))
                {
                    PlayScopeLog.Info(
                        $"{LOG_TAG} TryParse: filename sessionId={expectedSessionId} differs from " +
                        $"json sessionId={fileSessionId} — trusting json value. path={path}");
                }

                var frames = ReadFrames(root);
                return new NativeCrashRecord
                {
                    SessionId = sessionId ?? string.Empty,
                    Signal = ReadInt(root, "signal", 0),
                    SiCode = ReadInt(root, "si_code", 0),
                    FaultAddr = ReadString(root, "fault_addr", "0x0"),
                    ThreadTid = ReadLong(root, "thread_tid", 0L),
                    CapturedAtUnixMs = ReadLong(root, "captured_at_unix_ms", 0L),
                    Frames = frames,
                };
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning(
                    $"{LOG_TAG} TryParse: malformed crash file path={path} preview=" + Preview(raw),
                    ex);
                return null;
            }
        }

        private static List<NativeCrashFrame> ReadFrames(Dictionary<string, object> root)
        {
            var result = new List<NativeCrashFrame>();
            if (!root.TryGetValue("frames", out var raw) || !(raw is List<object> list))
            {
                return result;
            }
            int count = list.Count > MAX_FRAMES_PER_RECORD ? MAX_FRAMES_PER_RECORD : list.Count;
            for (int i = 0; i < count; i++)
            {
                if (!(list[i] is Dictionary<string, object> frameDict))
                {
                    continue;
                }
                result.Add(new NativeCrashFrame
                {
                    Pc = ReadString(frameDict, "pc", "0x0"),
                    Module = ReadString(frameDict, "module", string.Empty),
                    Offset = ReadLong(frameDict, "offset", 0L),
                });
            }
            return result;
        }

        private static string ReadString(Dictionary<string, object> dict, string key, string fallback)
        {
            if (dict.TryGetValue(key, out var v) && v is string s)
            {
                return s;
            }
            return fallback;
        }

        private static int ReadInt(Dictionary<string, object> dict, string key, int fallback)
        {
            if (dict.TryGetValue(key, out var v))
            {
                if (v is long l)
                {
                    return (int)l;
                }
                if (v is double d)
                {
                    return (int)d;
                }
            }
            return fallback;
        }

        private static long ReadLong(Dictionary<string, object> dict, string key, long fallback)
        {
            if (dict.TryGetValue(key, out var v))
            {
                if (v is long l)
                {
                    return l;
                }
                if (v is double d)
                {
                    return (long)d;
                }
            }
            return fallback;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"{LOG_TAG} TryDelete: failed path={path}", ex);
            }
        }

        private static string Preview(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "<empty>";
            }
            const int MAX_PREVIEW_CHARS = 200;
            return raw.Length <= MAX_PREVIEW_CHARS
                ? raw
                : raw.Substring(0, MAX_PREVIEW_CHARS) + "…";
        }

        private static void AppendFramesArray(StringBuilder sb, IReadOnlyList<NativeCrashFrame> frames)
        {
            if (frames == null || frames.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append('[');
            for (int i = 0; i < frames.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                var f = frames[i];
                sb.Append('{');
                AppendJsonStringPair(sb, "pc", f.Pc ?? "0x0", isFirst: true);
                AppendJsonStringPair(sb, "module", f.Module ?? string.Empty, isFirst: false);
                AppendJsonLongPair(sb, "offset", f.Offset, isFirst: false);
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static void AppendJsonStringPair(StringBuilder sb, string key, string value, bool isFirst)
        {
            if (!isFirst)
            {
                sb.Append(',');
            }
            EventPipeline.AppendEscapedString(sb, key);
            sb.Append(':');
            EventPipeline.AppendEscapedString(sb, value ?? string.Empty);
        }

        private static void AppendJsonIntPair(StringBuilder sb, string key, int value, bool isFirst)
        {
            if (!isFirst)
            {
                sb.Append(',');
            }
            EventPipeline.AppendEscapedString(sb, key);
            sb.Append(':').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void AppendJsonLongPair(StringBuilder sb, string key, long value, bool isFirst)
        {
            if (!isFirst)
            {
                sb.Append(',');
            }
            EventPipeline.AppendEscapedString(sb, key);
            sb.Append(':').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Parsed shape of one native crash file.</summary>
    internal sealed class NativeCrashRecord
    {
        internal string SessionId;
        internal int Signal;
        internal int SiCode;
        internal string FaultAddr;
        internal long ThreadTid;
        internal long CapturedAtUnixMs;
        internal IReadOnlyList<NativeCrashFrame> Frames;
    }

    internal sealed class NativeCrashFrame
    {
        internal string Pc;
        internal string Module;
        internal long Offset;
    }

    /// <summary>
    /// Tiny recursive-descent JSON parser for the crash schema (objects, arrays,
    /// strings, numbers, bools, null). The shared <see cref="SimpleJson"/> is
    /// flat-only so it can't read <c>frames:[…]</c>, and pulling in Newtonsoft
    /// for one cold-path parser would bloat every consumer build.
    /// </summary>
    internal sealed class MiniJsonParser
    {
        private const int MAX_DEPTH = 32;

        private readonly string _s;
        private int _i;

        internal MiniJsonParser(string s)
        {
            _s = s ?? string.Empty;
            _i = 0;
        }

        internal object ParseValue()
        {
            return ParseValueAt(depth: 0);
        }

        private object ParseValueAt(int depth)
        {
            if (depth > MAX_DEPTH)
            {
                throw new FormatException($"json nesting exceeds {MAX_DEPTH}");
            }
            SkipWhitespace();
            if (_i >= _s.Length)
            {
                throw new FormatException("unexpected eof");
            }
            char c = _s[_i];
            if (c == '{')
            {
                return ParseObject(depth);
            }
            if (c == '[')
            {
                return ParseArray(depth);
            }
            if (c == '"')
            {
                return ParseString();
            }
            if (c == 't' || c == 'f')
            {
                return ParseBool();
            }
            if (c == 'n')
            {
                ParseLiteral("null");
                return null;
            }
            return ParseNumber();
        }

        private Dictionary<string, object> ParseObject(int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            Expect('{');
            SkipWhitespace();
            if (Peek() == '}')
            {
                _i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                Expect(':');
                var value = ParseValueAt(depth + 1);
                result[key] = value;
                SkipWhitespace();
                char next = Peek();
                if (next == ',')
                {
                    _i++;
                    continue;
                }
                if (next == '}')
                {
                    _i++;
                    return result;
                }
                throw new FormatException($"unexpected char '{next}' at {_i} (expected , or }})");
            }
        }

        private List<object> ParseArray(int depth)
        {
            var result = new List<object>();
            Expect('[');
            SkipWhitespace();
            if (Peek() == ']')
            {
                _i++;
                return result;
            }
            while (true)
            {
                var value = ParseValueAt(depth + 1);
                result.Add(value);
                SkipWhitespace();
                char next = Peek();
                if (next == ',')
                {
                    _i++;
                    continue;
                }
                if (next == ']')
                {
                    _i++;
                    return result;
                }
                throw new FormatException($"unexpected char '{next}' at {_i} (expected , or ])");
            }
        }

        private string ParseString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    if (_i >= _s.Length)
                    {
                        throw new FormatException("eof inside string escape");
                    }
                    char esc = _s[_i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length)
                            {
                                throw new FormatException("eof inside \\u escape");
                            }
                            var hex = _s.Substring(_i, 4);
                            _i += 4;
                            sb.Append((char)int.Parse(hex, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        default:
                            throw new FormatException($"unknown escape \\{esc}");
                    }
                    continue;
                }
                sb.Append(c);
            }
            throw new FormatException("eof inside string");
        }

        private object ParseNumber()
        {
            int start = _i;
            if (Peek() == '-')
            {
                _i++;
            }
            bool isFloat = false;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c >= '0' && c <= '9')
                {
                    _i++;
                    continue;
                }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                {
                    isFloat = true;
                    _i++;
                    continue;
                }
                break;
            }
            var token = _s.Substring(start, _i - start);
            if (isFloat)
            {
                return double.Parse(token, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            return long.Parse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private bool ParseBool()
        {
            if (Peek() == 't')
            {
                ParseLiteral("true");
                return true;
            }
            ParseLiteral("false");
            return false;
        }

        private void ParseLiteral(string lit)
        {
            if (_i + lit.Length > _s.Length || _s.Substring(_i, lit.Length) != lit)
            {
                throw new FormatException($"expected literal {lit} at {_i}");
            }
            _i += lit.Length;
        }

        private void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    _i++;
                    continue;
                }
                break;
            }
        }

        private void Expect(char c)
        {
            SkipWhitespace();
            if (_i >= _s.Length || _s[_i] != c)
            {
                throw new FormatException($"expected '{c}' at {_i}");
            }
            _i++;
        }

        private char Peek()
        {
            SkipWhitespace();
            return _i < _s.Length ? _s[_i] : '\0';
        }
    }
}
