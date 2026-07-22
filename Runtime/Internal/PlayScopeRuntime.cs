using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using PlayScopeSdk.Core.Session;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    internal static class PlayScopeRuntime
    {
        internal const string SdkVersion = "0.6.18";

        // PlayerPrefs key for the last-seen Application.version, so Initialize can
        // emit app_update_detected the first time a different build starts up.
        private const string LastSeenAppVersionPrefKey = "playscope:last_app_version";

        private static volatile bool _initialized;
        private static volatile bool _disabled;
        private static int _initialStateSet; // 0/1 — Interlocked
        // Atomic mutex around Initialize / PerformRotation / TeardownInternal.
        // Unity callbacks are main-thread but the ANR watchdog and workers aren't,
        // and an OnApplicationQuit landing mid-PerformRotation raced two parallel
        // teardowns into a half-initialised state. CompareExchange so the second
        // concurrent caller exits silently rather than spawning a duplicate
        // Writer / Pipeline / Uploader.
        private static int _lifecycleBusy; // 0/1 — Interlocked

        // Lifecycle state machine — feeds duration_in_prev_state_ms on every
        // background_start / foreground transition. Seeded "foreground" on
        // session_start so the first transition out carries a real delta.
        private static string _currentLifecycleState = "foreground";
        // Wall-clock anchor for the metadata UTC timestamp AND a monotonic
        // Stopwatch anchor for duration arithmetic. Durations MUST come from the
        // Stopwatch, never DateTime.UtcNow.Ticks: a device clock change mid-
        // background (NTP, manual, DST) yields negative/inflated deltas that
        // break the 5-min rotation trigger. Same lesson as WriterWorker.cs top.
        private static long _currentLifecycleStateEnteredAtTicks;
        private static long _currentLifecycleStateEnteredAtStopwatchTicks;
        // DateTime.UtcNow.Ticks at the most recent transition INTO background.
        // RecordLifecycle overwrites _currentLifecycleStateEnteredAtTicks with the
        // foreground-return time when scheduling rotation, so this is the only
        // field that still remembers when the player actually backgrounded.
        // PerformRotation backdates session_end here so the old session's duration
        // reflects pure foreground engagement, not foreground + OS sleep time.
        private static long _lastBackgroundStartTicks;

        // Pipeline write gate, closed by RecordLifecycle for the rotation
        // race window so wrapper events can't land in the doomed old session.
        // session_start / session_end bypass it via direct WriterWorker writes
        // — they're contract, not analytics. Volatile: hot-path read in
        // EventPipeline.Enqueue*.
        internal static volatile bool _acceptingEvents = true;

        // Last sampled reachability; a change emits a discrete network_change
        // event (vs the periodic network_reachability metric).
        private static int _lastNetworkReachability = int.MinValue;

        // first_frame_rendered guard — emit exactly once per session.
        private static int _firstFrameEmitted;
        // Zero-point for first_input_latency: measures time from a rendered screen
        // to first tap, a better TTI signal than ms-since-session-start (which
        // includes headless pre-render init).
        private static long _firstFrameTicks;
        // first_input_latency guard — set on the first input tick after first_frame.
        private static int _firstInputEmitted;

        internal static bool IsInitialized => _initialized;
        internal static bool IsDisabled => _disabled;
        // Lets the per-frame input poll fast-path out of the Input API with a
        // single field read instead of a CompareExchange once the event fired.
        internal static bool HasEmittedFirstInputLatency => _firstInputEmitted != 0;

        internal static DeviceIdentity Device { get; private set; }
        internal static SessionInfo CurrentSession { get; private set; }

        internal static EventQueue? Queue { get; private set; }
        internal static EventPipeline? Pipeline { get; private set; }
        internal static UploadQueue? UploadQueue { get; private set; }
        // Repeats-collapsing buffer for absorbable log levels. Ticked by
        // Update; drained on pause/teardown so a backgrounding session doesn't
        // strand the last buffered logs.
        internal static LogDedupBuffer? LogDedupBuffer { get; private set; }
        internal static StatePatchCoalescer StatePatchCoalescer { get; } = new();
        // Independent buffer from profile-state so a periodic device sample never
        // overwrites a gameplay key. Wider window (1 s vs 100 ms) folds init bursts.
        internal static SessionDataCoalescer SessionDataCoalescer { get; } = new();
        private static WriterWorker? _writer;
        private static HeartbeatWorker? _heartbeat;
        internal static UploaderWorker? _uploader;
        // Null when disabled (Editor, or AnrDetectionEnabled = false) so the
        // heartbeat call short-circuits without overhead.
        internal static AnrWatchdog? AnrWatchdog { get; private set; }
        private static GameObject? _driverGo;
        private static bool _quittingSubscribed;
        private static bool _logCaptureSubscribed;
        // Tracked so TeardownInternal only unhooks if we actually subscribed
        // (ForceDisable on partial init may not have).
        private static bool _lowMemorySubscribed;
        private static LogLevel _autoCaptureMinLevel;

        // A play session ends when the player backgrounds longer than this;
        // returning after a longer absence starts a NEW session. Hardcoded across
        // all tiers — per-project config would let a customer set "10 hours" and
        // effectively bill for DAU instead of sessions. See BILLING_AND_PLANS.md §4.
        private const long BackgroundSessionTimeoutMs = 5 * 60 * 1000L; // 5 minutes

        // The Initialize() context, held so RotateSession() rebuilds an identical
        // runtime. NEVER mutated after first Initialize.
        private static PlayScopeContext? _context;

        // Set by RecordLifecycle, consumed by the MonoBehaviour on its next Update
        // so rotation runs OUTSIDE the OnApplicationPause / OnFocusChanged callback
        // chain — TeardownInternal destroys the driver GameObject, which we can't
        // do from inside that GameObject's own callback.
        private static volatile bool _pendingRotation;

        /// <summary>
        /// Called by PlayScope.Initialize if Initialize itself threw — moves SDK to a
        /// permanently disabled state so subsequent API calls are silent no-ops.
        /// Also tears down anything Initialize had wired up before throwing
        /// (workers, log subscription, MonoBehaviour driver) so partial state
        /// can't leak past the failure.
        /// </summary>
        internal static void ForceDisable()
        {
            TeardownInternal(emitSessionEnd: false);
            _disabled = true;
            // Clear the lifecycle gate so a later Initialize (retry with fixed
            // settings, or Editor "Disable Domain Reload") isn't dead-ended by a
            // _lifecycleBusy == 1 left over from the interrupted run.
            Interlocked.Exchange(ref _lifecycleBusy, 0);
            // Do NOT set _initialized — IsInitialized stays false so API guards return early.
        }

        /// <summary>
        /// Atomically marks the initial-state event as having been sent.
        /// Returns true the first time; false on every subsequent call.
        /// </summary>
        internal static bool TryMarkInitialStateSet()
        {
            return Interlocked.CompareExchange(ref _initialStateSet, 1, 0) == 0;
        }

        /// <summary>
        /// Re-arms the initial-state lock so the caller can legally call
        /// <see cref="PlayScope.SetInitialState"/> again. Used by
        /// <see cref="PlayScope.TrackRestart"/> — an in-game restart logically
        /// throws away the current profile snapshot, and the game is expected
        /// to push a fresh one for the post-restart period.
        /// </summary>
        internal static void ResetInitialStateLock()
        {
            Interlocked.Exchange(ref _initialStateSet, 0);
        }

        // Called by PlayScope.Initialize()
        internal static void Initialize(PlayScopeContext context)
        {
            // Atomic admission — only ONE thread proceeds. A plain
            // `if (_initialized || _disabled)` is read-then-act and let two
            // callers (user Initialize racing a mid-flight PerformRotation)
            // both pass and both spin up workers.
            if (Interlocked.CompareExchange(ref _lifecycleBusy, 1, 0) != 0)
            {
                PlayScopeLog.Warning("Initialize called while another lifecycle op is in flight — ignored.");
                return;
            }
            try
            {
                InitializeLocked(context);
            }
            finally
            {
                Interlocked.Exchange(ref _lifecycleBusy, 0);
            }
        }

        private static void InitializeLocked(PlayScopeContext context)
        {
            if (_initialized || _disabled)
            {
                PlayScopeLog.Warning("Initialize called more than once — ignored.");
                return;
            }

            // Reset session-scoped state UP-FRONT, before any driver or worker
            // starts. A re-Initialize after Shutdown (test runners, scene reload)
            // would otherwise see stale flags — _firstFrameEmitted still 1 → event
            // never re-emitted; SequenceCounter mid-stream → mixed sequence numbers.
            SensitiveKeyFilter.ResetWarnings();
            // Default true filter-side too, so a partial Initialize that crashes
            // before here still masks by default.
            SensitiveKeyFilter.SetPiiValueMasksEnabled(context?.PiiValueMasksEnabled ?? true);
            // MinLogLevel=Warning suppresses our chatty Info lines (orphan-chunk
            // rescue, etc.) — useful when debugging the SDK, noise otherwise.
            PlayScopeLog.SetMinLevel(context?.AutoCaptureMinLevel ?? LogLevel.Info);
            _initialStateSet = 0;
            _currentLifecycleState = "foreground";
            _currentLifecycleStateEnteredAtTicks = DateTime.UtcNow.Ticks;
            _currentLifecycleStateEnteredAtStopwatchTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _firstFrameEmitted = 0;
            _firstFrameTicks = 0;
            _firstInputEmitted = 0;
            _lastNetworkReachability = int.MinValue;
            _pendingRotation = false;
            // Reset so an Initialize after a thrown rotation can't strand the gate closed.
            _acceptingEvents = true;
            SequenceCounter.Reset();
            // Both coalescers are static singletons that survive teardown. Without
            // this reset the new session's first patch emits against the OLD
            // session's baseline and leaks old-session keys into the first flush.
            StatePatchCoalescer.ResetForNewSession();
            SessionDataCoalescer.ResetForNewSession();
            PlayScope.ResetSessionScopedState();

            // Step 1: Validate SdkKey
            if (string.IsNullOrWhiteSpace(context?.SdkKey))
            {
                PlayScopeLog.Warning("SdkKey is null or empty — SDK disabled.");
                _disabled = true;
                return;
            }

            // Captured BEFORE subsystem creation so even a partial Initialize
            // still knows how to re-init identically on the next rotation.
            _context = context;

            // Step 2: Ensure directories exist (wrap disk-write sections — never throw to caller)
            try
            {
                PlayScopeDirectory.EnsureRootDirectories();
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Could not create SDK directories — SDK disabled", ex);
                _disabled = true;
                return;
            }

            // Step 3: Load or create device identity
            try
            {
                Device = DeviceIdentity.LoadOrCreate();
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("DeviceIdentity load/create failed — SDK disabled", ex);
                _disabled = true;
                return;
            }

            // Step 3.5: Crash collector PreInit BEFORE SessionRecovery — recovery
            // reads back prior-process crash files as exception records. PreInit
            // ensures the crash dir exists; the Android signal handler installs in
            // OnSessionInitialized below.
            try
            {
                PlayScopeCrashCollector.PreInit();
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("PlayScopeCrashCollector.PreInit failed — native crash capture disabled", ex);
            }

            // Step 4: Recover any stale session from a previous crash (PSDK-12)
            // UploadQueue is created here so recovery can enqueue chunks immediately;
            // the uploader worker is started after the new session is fully initialised.
            UploadQueue = new UploadQueue();
            try
            {
                SessionRecovery.RecoverIfNeeded(UploadQueue);
            }
            catch (Exception ex)
            {
                // Recovery already swallows its own exceptions, but be defensive.
                PlayScopeLog.Warning("SessionRecovery failed", ex);
            }

            // Step 5: Start new session
            try
            {
                CurrentSession = SessionInfo.Generate(SdkVersion);
                SessionFiles.WriteNewSession(CurrentSession, Device.SdkUserId);
                // Seed lifecycle state to "foreground" so that if the very
                // first frame crashes before any OnApplicationPause / Focus
                // callback fires, recovery on the next launch sees
                // state="foreground" and classifies the death as a
                // foreground crash rather than "unknown".
                SessionFiles.WriteLifecycleState("foreground");
                // Install platform-specific lifecycle hook (Android Java
                // ActivityLifecycleCallbacks / iOS WillTerminate
                // notification). Adds the precise "user_close" signal on
                // top of the C# OnApplicationPause-driven state. No-op in
                // Editor / Standalone / WebGL.
                NativeLifecycleBridge.Install();
                // Install/refresh native crash signal handler with the
                // freshly-generated session_id. Android-only at runtime;
                // every other platform is a no-op. Failure inside the
                // collector is swallowed and logged — SDK init MUST NOT
                // abort because crash capture failed to wire.
                PlayScopeCrashCollector.OnSessionInitialized(CurrentSession.SessionId);
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Could not write new session files — SDK disabled", ex);
                _disabled = true;
                return;
            }

            // PSDK-10: start event queue + writer worker.
            // Workers spin up BEFORE the _initialized gate opens so any caller
            // that races through the public API the moment the gate flips can
            // count on Pipeline / Queue being non-null. Without this ordering
            // the early API call would pass IsInitialized but hit a null
            // Pipeline and silently drop the event via null-conditional.
            Queue = new EventQueue();
            // Note: UploadQueue was already created in Step 4 (SessionRecovery)
            Pipeline = new EventPipeline(Queue);
            // Wire the log dedup buffer. Pipeline.EnqueueLog routes absorbable
            // levels (debug/info/warning, no stack trace) through this buffer
            // so repeated identical messages within a 5 s window collapse into
            // a single timeline row carrying repeat_count: N metadata.
            // Critical levels (error/exception) bypass dedup entirely — every
            // error matters individually and must hit the queue immediately.
            LogDedupBuffer = new LogDedupBuffer(Queue);
            Pipeline.SetLogDedupBuffer(LogDedupBuffer);
            _writer = new WriterWorker(Queue, UploadQueue, CurrentSession);
            _uploader = new UploaderWorker(context, CurrentSession, UploadQueue);
            // Wake the uploader the moment any chunk lands in the queue — no
            // longer limited to critical-event finalizations. Covers pause /
            // shutdown / size-split paths so an in-session chunk never sits
            // on disk for ~30 s waiting on the polling tick.
            _writer.OnChunkFinalized = () => _uploader?.TriggerInstantUpload();
            _writer.Start();

            _uploader.Start();
            // Wake the uploader now if recovery just enqueued chunks: otherwise
            // the first pass waits ~30 s, and a swipe-kill within that window
            // stranded the synthetic events (sessions stuck EndStatus=unknown).
            if (UploadQueue.Count > 0) _uploader.TriggerInstantUpload();

            _heartbeat = new HeartbeatWorker();
            _heartbeat.Start();

            // Disabled in Editor (breakpoints = false positives), except batch
            // mode (no debugger). Off entirely when the caller opted out.
            if (context.AnrDetectionEnabled && (!Application.isEditor || Application.isBatchMode))
            {
                try
                {
                    AnrWatchdog = new AnrWatchdog(Pipeline!, context.AnrThresholdMs);
                    AnrWatchdog.Start();
                }
                catch (Exception ex)
                {
                    PlayScopeLog.Warning("Failed to start ANR watchdog", ex);
                    AnrWatchdog = null;
                }
            }

            // Optional: subscribe to Unity log stream
            if (context.AutoCaptureUnityLogs)
            {
                try
                {
                    _autoCaptureMinLevel = context.AutoCaptureMinLevel;
                    Application.logMessageReceivedThreaded += OnUnityLogReceived;
                    _logCaptureSubscribed = true;
                }
                catch (Exception ex)
                {
                    PlayScopeLog.Warning("Failed to subscribe to Unity log stream", ex);
                }
            }

            // Application.lowMemory is cross-platform (Android onTrimMemory, iOS
            // memory warning) with no native plugin, and runs on the main thread
            // (safe for Profiler/SystemInfo). The handler emits a critical
            // memory_warning so the chunk flushes before a possible OS kill.
            try
            {
                Application.lowMemory += OnLowMemoryWarning;
                _lowMemorySubscribed = true;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to subscribe to Application.lowMemory", ex);
            }

            // Wire MonoBehaviour driver + Application.quitting (main thread / play mode only).
            try
            {
                EnsureDriver();
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to install PlayScopeMonoBehaviour driver", ex);
            }

            // Open the gate only now — every subsystem is wired and every field
            // reset. Events emitted below (session_start, app_update_detected,
            // session_data seed) use the Pipeline directly, bypassing the
            // IsInitialized guard, so this ordering is intentional.
            _initialized = true;

            // is_editor / is_development_build are intrinsic (no user override) —
            // the dashboard slices on them to keep editor/dev noise out of CFS%.
            // environment is user-overridable via Metadata; default from isDebugBuild.
            bool isEditor           = UnityEngine.Application.isEditor;
            bool isDevelopmentBuild = UnityEngine.Debug.isDebugBuild;

            int systemMemoryMb;
            try
            {
                int reported = UnityEngine.SystemInfo.systemMemorySize;
                systemMemoryMb = reported > 0 ? reported : 0;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to read SystemInfo.systemMemorySize for session_start", ex);
                systemMemoryMb = 0;
            }

            string env;
            if (context.Metadata != null &&
                context.Metadata.TryGetValue("environment", out var envVal) &&
                envVal is string envStr && !string.IsNullOrEmpty(envStr))
            {
                env = envStr;
            }
            else
            {
                env = isEditor ? "editor" : (isDevelopmentBuild ? "development" : "production");
            }

            var buildInfo = PlayScopeBuildInfo.Load();
            var sessionMeta = new System.Collections.Generic.Dictionary<string, object>
            {
                ["app_version"] = UnityEngine.Application.version,
                ["build_number"] = ResolveBuildNumber(context),
                ["build_commit"] = buildInfo != null ? (buildInfo.BuildCommit ?? "") : "",
                ["build_branch"] = buildInfo != null ? (buildInfo.BuildBranch ?? "") : "",
                ["environment"] = env,
                ["is_editor"] = isEditor,
                ["is_development_build"] = isDevelopmentBuild,
                ["platform"] = GetPlatformString(),
                ["device_model"] = UnityEngine.SystemInfo.deviceModel,
                ["os_version"] = UnityEngine.SystemInfo.operatingSystem,
                ["system_memory_mb"] = systemMemoryMb,
                ["sdk_version"] = SdkVersion,
                // Direct-write path skips envelope sdk_user_id stamping — include here.
                ["sdk_user_id"] = Device.SdkUserId,
                // false → lifecycle_hook_error below carries the reason.
                ["lifecycle_hook_installed"] = NativeLifecycleBridge.IsInstalled,
            };
            if (!NativeLifecycleBridge.IsInstalled && !string.IsNullOrEmpty(NativeLifecycleBridge.LastError))
                sessionMeta["lifecycle_hook_error"] = NativeLifecycleBridge.LastError;

            // Symmetric with session_end — direct-write bypasses Pipeline so the
            // gate / null-Pipeline / unwired-queue can't lose the lifecycle marker.
            if (_writer != null)
            {
                var sessionStartRec = new EventRecord
                {
                    RecordType = RecordType.Event,
                    EventType = "session_start",
                    EventId = UlidGenerator.NewEventId(),
                    SequenceNum = SequenceCounter.Next(),
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    MetadataJson = EventPipeline.DictToJson(sessionMeta),
                    IsCritical = true,
                };
                if (!_writer.WriteCriticalAndFinalizeSync(sessionStartRec))
                {
                    PlayScopeLog.Warning("session_start direct-write failed — Session row metadata will be empty.");
                }
            }
            else
            {
                PlayScopeLog.Warning("session_start skipped — WriterWorker null after init (unreachable).");
            }

            // Guards against emit-on-change metrics (is_charging,
            // network_reachability, available_disk_mb) being suppressed by _prev*
            // sentinels carrying the prior session's value, if the MB ever
            // survives a rotation (today rotation destroys it, so usually no-op).
            _driverGo?.GetComponent<PlayScopeMonoBehaviour>()?.ResetSamplerForNewSession();

            // app_update_detected — emit only on a real version change.
            // First-ever install is silent (no prior version to compare).
            // Persist BEFORE emit so a save failure can't double-emit next launch.
            try
            {
                var currentVersion = UnityEngine.Application.version ?? "";
                var lastSeenVersion = UnityEngine.PlayerPrefs.GetString(LastSeenAppVersionPrefKey, "");
                var versionChanged = lastSeenVersion != currentVersion;
                if (versionChanged)
                {
                    UnityEngine.PlayerPrefs.SetString(LastSeenAppVersionPrefKey, currentVersion);
                    UnityEngine.PlayerPrefs.Save();
                    if (!string.IsNullOrEmpty(lastSeenVersion))
                    {
                        var updateMeta = new Dictionary<string, object>
                        {
                            ["from_version"] = lastSeenVersion,
                            ["to_version"]   = currentVersion,
                        };
                        Pipeline.EnqueueEvent("app_update_detected",
                            metadataJson: EventPipeline.DictToJson(updateMeta));
                    }
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("app_update_detected check failed", ex);
            }

            // Seed session_data with diagnostic fields not in session_start.
            // Through the coalescer so the bootstrap wave folds into one
            // session_data_initial row.
            try
            {
                var systemInfo = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["screen_width"]        = UnityEngine.Screen.width,
                    ["screen_height"]       = UnityEngine.Screen.height,
                    ["screen_dpi"]          = UnityEngine.Screen.dpi,
                    ["device_locale"]       = UnityEngine.Application.systemLanguage.ToString(),
                    ["device_unique_id"]    = Device.SdkUserId,
                    ["processor_count"]     = UnityEngine.SystemInfo.processorCount,
                    ["processor_type"]      = UnityEngine.SystemInfo.processorType,
                    ["system_memory_mb"]    = UnityEngine.SystemInfo.systemMemorySize,
                    ["graphics_device_name"]   = UnityEngine.SystemInfo.graphicsDeviceName,
                    ["graphics_memory_mb"]     = UnityEngine.SystemInfo.graphicsMemorySize,
                    ["graphics_device_type"]   = UnityEngine.SystemInfo.graphicsDeviceType.ToString(),
                    ["operating_system_family"] = UnityEngine.SystemInfo.operatingSystemFamily.ToString(),
                };
                SessionDataCoalescer.Add(systemInfo, reason: "sdk_init");
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to seed session_data with system info", ex);
            }

            PlayScopeLog.Info($"Initialized. session_id={CurrentSession.SessionId}, sdk_user_id={Device.SdkUserId}");
        }

        // Called by PlayScopeMonoBehaviour on application quit / Application.quitting
        internal static void Shutdown()
        {
            Debug.Log($"[PlayScope/diag] Shutdown() entry — _initialized={_initialized} _disabled={_disabled} _lifecycleBusy={_lifecycleBusy}");
            // Brackets teardown with the same lifecycle lock as Initialize /
            // PerformRotation. Spin-wait (not skip) for an in-flight rotation: the
            // post-rotation session has no other path to emit its session_end.
            // Capped at 500 ms because Application.quitting gives <~2 s before
            // SIGKILL on mobile — long enough for a sane rotation, short enough to
            // still write session_end.
            const int ShutdownLockWaitMs = 500;
            const int SpinSleepMs = 5;
            int waited = 0;
            bool acquired = false;
            while (true)
            {
                if (Interlocked.CompareExchange(ref _lifecycleBusy, 1, 0) == 0)
                {
                    acquired = true;
                    break;
                }
                if (waited >= ShutdownLockWaitMs)
                {
                    // Don't force the gate — barging in would run TeardownInternal
                    // concurrently with InitializeLocked and corrupt half the
                    // subsystems. Losing this session_end is the safer trade; it
                    // lands on next launch as a synthetic foreground_crash.
                    PlayScopeLog.Warning(
                        $"Shutdown: lifecycle lock not released within {ShutdownLockWaitMs} ms — " +
                        "skipping teardown to avoid corrupting in-flight rotation. " +
                        "Session_end will be synthesised by SessionRecovery on next launch.");
                    return;
                }
                Thread.Sleep(SpinSleepMs);
                waited += SpinSleepMs;
            }
            Debug.Log($"[PlayScope/diag] Shutdown() lock acquired after {waited} ms — _initialized={_initialized} _disabled={_disabled}");
            try
            {
                if (!_initialized || _disabled)
                {
                    Debug.Log($"[PlayScope/diag] Shutdown() early-return — _initialized={_initialized} _disabled={_disabled} (no session_end will be emitted)");
                    return;
                }
                TeardownInternal(emitSessionEnd: true, endStatus: "normal", reason: "normal");
                Debug.Log("[PlayScope/diag] Shutdown() TeardownInternal returned.");
            }
            finally
            {
                if (acquired) Interlocked.Exchange(ref _lifecycleBusy, 0);
            }
        }

        /// <summary>
        /// Idempotent, fully null-guarded teardown shared by Shutdown (normal),
        /// ForceDisable (partial-init failure), and RotateSession (background
        /// timeout) — safe on a half-initialised SDK.
        /// </summary>
        /// <param name="endStatus">Legacy <c>end_status</c>
        /// ("normal" | "abnormal" | "background_timeout") for the dashboard badge.</param>
        /// <param name="reason">Fine-grained session_end <c>reason</c>:
        /// <c>normal</c> (clean quit), <c>background_timeout</c> (5-min rotation),
        /// <c>background_kill</c> (recovered, was backgrounded — swipe/low-mem
        /// kill, NOT a crash), <c>foreground_crash</c> (recovered, foregrounded
        /// with no clean exit — crash/ANR), <c>unknown</c> (recovered, lifecycle
        /// file missing). Backend's corrected CFS% excludes swipe-kills.</param>
        /// <param name="endTimestampOverride">When non-null, session_end uses this
        /// UTC time instead of now — PerformRotation backdates to
        /// <c>_lastBackgroundStartTicks</c> so duration is pure foreground.</param>
        private static void TeardownInternal(bool emitSessionEnd, string endStatus = "normal", string reason = "normal",
            DateTime? endTimestampOverride = null)
        {
            // Unsubscribe Unity log capture (idempotent guard)
            if (_logCaptureSubscribed)
            {
                try { Application.logMessageReceivedThreaded -= OnUnityLogReceived; }
                catch { /* best-effort */ }
                _logCaptureSubscribed = false;
            }

            if (_lowMemorySubscribed)
            {
                try { Application.lowMemory -= OnLowMemoryWarning; }
                catch { /* best-effort */ }
                _lowMemorySubscribed = false;
            }

            if (_quittingSubscribed)
            {
                try { Application.quitting -= OnApplicationQuittingHandler; }
                catch { /* best-effort */ }
                _quittingSubscribed = false;
            }

            _heartbeat?.Stop();
            _heartbeat = null;

            // Stop the ANR watchdog FIRST so its threadpool timer can't
            // race into a pipeline that's about to be nulled. Stop is
            // idempotent — safe to call when watchdog was never started.
            AnrWatchdog?.Stop();
            AnrWatchdog = null;

            // Flush any buffered state / session-data patches before session_end
            // so the last changes survive — otherwise the coalescing windows
            // would strand them in the buffer when we stop the pipeline below.
            StatePatchCoalescer.FlushNow();
            SessionDataCoalescer.FlushNow();
            // Drain the log dedup buffer for the same reason — its 5 s window
            // would otherwise strand the last buffered first-of-key samples.
            LogDedupBuffer?.FlushNow();

            // Emit session_end + finalize in ONE sync step via
            // WriteCriticalAndFinalizeSync, bypassing Pipeline.EnqueueEvent, so
            // {drain → append → flush → rename} runs under a single lock and
            // RunAsync can't finalize an empty chunk between our flush and rename
            // (that race lost session_end in a past version).
            if (emitSessionEnd)
            {
                Debug.Log($"[PlayScope/diag] TeardownInternal: emitSessionEnd=true endStatus={endStatus} reason={reason} writerIsNull={_writer == null}");
                if (_writer != null)
                {
                    // Background-timeout rotation backdates to when the player
                    // backgrounded; normal shutdown passes null = now.
                    var endTs = endTimestampOverride ?? DateTime.UtcNow;
                    if (endTs.Kind != DateTimeKind.Utc) endTs = endTs.ToUniversalTime();
                    var rec = new EventRecord
                    {
                        RecordType = RecordType.Event,
                        EventType = "session_end",
                        EventId = UlidGenerator.NewEventId(),
                        SequenceNum = SequenceCounter.Next(),
                        Timestamp = endTs.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        MetadataJson = $"{{\"end_status\":\"{endStatus}\",\"reason\":\"{reason}\"}}",
                        IsCritical = true,
                    };
                    var writer = _writer; // capture for closure
                    bool ok;
#if UNITY_WEBGL && !UNITY_EDITOR
                    // WebGL: no thread pool — Task.Run never schedules, so
                    // Wait(500) busy-blocks the tab and loses session_end.
                    // The write is already synchronous and never throws.
                    ok = writer.WriteCriticalAndFinalizeSync(rec);
#else
                    // Bounded wait on a worker — lock + file I/O can hang past the
                    // quit budget on a slow/sandboxed disk. Over 500 ms we abandon;
                    // next launch synthesises abnormal_end. Worst case: a duplicate
                    // session_end if the abandoned task later succeeds.
                    var task = System.Threading.Tasks.Task.Run(() => writer.WriteCriticalAndFinalizeSync(rec));
                    if (task.Wait(TimeSpan.FromMilliseconds(500)))
                    {
                        ok = task.Result;
                    }
                    else
                    {
                        ok = false;
                        PlayScopeLog.Warning(
                            "Shutdown: WriteCriticalAndFinalizeSync exceeded 500 ms timeout — " +
                            "session_end may not have been flushed; SessionRecovery on next launch will synthesise it.");
                    }
#endif
                    PlayScopeLog.Info(
                        $"Shutdown: session_end sync-write (end_status={endStatus}, reason={reason}, success={ok}).");
                    Debug.Log($"[PlayScope/diag] TeardownInternal: WriteCriticalAndFinalizeSync ok={ok}");
                }
                else
                {
                    PlayScopeLog.Warning(
                        "Shutdown: _writer is null — session_end NOT emitted. " +
                        "The session will appear unclosed in the dashboard.");
                }
            }
            else
            {
                // ForceDisable path — still try to flush any pending queued
                // events that DID make it in before init failure. Best-effort.
                try
                {
                    _writer?.DrainAndFinalize();
                }
                catch (Exception ex)
                {
                    PlayScopeLog.Warning("ForceDisable: DrainAndFinalize failed", ex);
                }
            }
            _writer?.Stop();
            _writer = null;

            // Wake the uploader so the just-finalized session_end chunk gets at
            // least one upload shot from this process (RunAsync guarantees one
            // pass after wake even if Stop races in). If the request is killed by
            // the quit deadline, next launch's SessionRecovery re-uploads it.
            _uploader?.TriggerInstantUpload();
            _uploader?.Stop();
            _uploader = null;

            // Without nulling these, a re-Initialize's persistent MonoBehaviour
            // would keep routing Update-ticks through the OLD Pipeline — silent
            // metric loss + unbounded queue growth.
            Pipeline = null;
            Queue = null;
            LogDedupBuffer = null;

            // Destroy the driver — else the DontDestroyOnLoad GameObject persists,
            // EnsureDriver early-returns, and the next Initialize is ticked by a
            // stale MonoBehaviour with a frozen _sampler.
            if (_driverGo != null)
            {
                try { UnityEngine.Object.Destroy(_driverGo); } catch { /* best-effort */ }
                _driverGo = null;
            }

            // Discard in-flight scene-load entries — they pin AsyncOperation
            // references (and their scenes) in memory across sessions.
            SceneLoadProgressTracker.ClearAll();

            try { SessionFiles.DeleteSessionLock(); } catch { /* best-effort */ }
            // Deleted on clean teardown — its presence next launch signals an unclean death.
            try { SessionFiles.DeleteLifecycleState(); } catch { /* best-effort */ }
            _initialized = false;
            PlayScopeLog.Info("Shutdown complete.");
            Debug.Log("[PlayScope/diag] Shutdown complete — session.lock + session.lifecycle deleted, _initialized=false.");
        }

        // isEditor must win: UNITY_ANDROID/IOS are defined whenever the build
        // target is set, so in-Editor play with Android selected would report
        // "android" while device_model shows the desktop name.
        private static string GetPlatformString()
        {
            if (UnityEngine.Application.isEditor) return "editor";
#if UNITY_IOS
            return "ios";
#elif UNITY_ANDROID
            return "android";
#else
            return "standalone";
#endif
        }

        // Must match the build_number the symbol uploader sends with symbols.zip,
        // or symbol resolution can't confirm the symbols belong to the crashing
        // compile. Both read the native versionCode (PlayScopeSymbolUploader).
        // Order: integrator override → native version code → buildGUID fallback.
        private static string ResolveBuildNumber(PlayScopeContext context)
        {
            if (context?.Metadata != null
                && context.Metadata.TryGetValue("build_number", out var provided)
                && provided != null)
            {
                var s = provided.ToString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            var native = TryReadNativeBuildNumber();
            if (!string.IsNullOrEmpty(native)) return native;
            return UnityEngine.Application.buildGUID;
        }

        // Android: PackageManager versionCode (== bundleVersionCode in the APK).
        // iOS CFBundleVersion isn't exposed at runtime, so iOS falls back to
        // buildGUID (symbols still match by app_version).
        private static string TryReadNativeBuildNumber()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new UnityEngine.AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<UnityEngine.AndroidJavaObject>("currentActivity"))
                using (var pm = activity.Call<UnityEngine.AndroidJavaObject>("getPackageManager"))
                {
                    var pkg = activity.Call<string>("getPackageName");
                    using (var info = pm.Call<UnityEngine.AndroidJavaObject>("getPackageInfo", pkg, 0))
                    {
                        int versionCode = info.Get<int>("versionCode");
                        return versionCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("build_number: native versionCode read failed — using buildGUID", ex);
                return null;
            }
#else
            return null;
#endif
        }

        // From OnApplicationPause / focus lost. Flushes coalescers + writer so
        // nothing strands in a buffer through a background period that may end
        // in an OS kill.
        internal static void FlushOnPause()
        {
            StatePatchCoalescer.FlushNow();
            SessionDataCoalescer.FlushNow();
            LogDedupBuffer?.FlushNow();
            _writer?.FlushImmediate();
            // Update() stops in a moment — suspend the watchdog so the
            // backgrounded stretch isn't mis-reported as a multi-minute ANR.
            // Re-armed on foreground via RecordLifecycle.
            AnrWatchdog?.Suspend();
        }

        /// <summary>
        /// Records a lifecycle transition. Called from MonoBehaviour pause/focus callbacks.
        /// Stamps <c>duration_in_prev_state_ms</c> and <c>prev_state</c> so the
        /// dashboard can answer "how long were they in the background before
        /// they came back?" without reconstructing it from a pair of rows.
        /// </summary>
        internal static void RecordLifecycle(LifecycleTransition t)
        {
            if (!_initialized || _disabled) return;
            var nextState = t == LifecycleTransition.BackgroundStart ? "background" : "foreground";
            // Drop no-op transitions: iOS/Android often fire both
            // OnApplicationPause(true) and OnFocusChanged(false) for one
            // backgrounding, which used to emit two near-zero-duration rows.
            if (nextState == _currentLifecycleState) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            // Duration MUST be Stopwatch-derived: a device-clock change mid-
            // background yields a negative delta (skips rotation → player keeps
            // one billing-session forever) or a forward jump (rotates after 30 s).
            var nowStopwatchTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long durationMs;
            if (_currentLifecycleStateEnteredAtStopwatchTicks == 0)
            {
                durationMs = 0;
            }
            else
            {
                var deltaTicks = nowStopwatchTicks - _currentLifecycleStateEnteredAtStopwatchTicks;
                if (deltaTicks < 0) deltaTicks = 0; // monotonic but be safe
                durationMs = deltaTicks * 1000L / System.Diagnostics.Stopwatch.Frequency;
            }
            var prevState = _currentLifecycleState;

            // Background → foreground after > BackgroundSessionTimeoutMs starts a
            // NEW session. Rotation runs on the next Update (can't recreate the
            // driver GameObject from inside its own OnApplicationPause callback).
            // No lifecycle event here — the session_end + new session_start say it.
            if (t == LifecycleTransition.Foreground &&
                prevState == "background" &&
                durationMs >= BackgroundSessionTimeoutMs)
            {
                PlayScopeLog.Info(
                    $"Background timeout ({durationMs} ms ≥ {BackgroundSessionTimeoutMs} ms) — " +
                    "scheduling session rotation.");
                // Close the pipeline first so nothing (lowMemory in the same
                // frame, wrapper code this tick) lands in the doomed session.
                // Restored at the end of PerformRotation.
                _acceptingEvents = false;
                // Advance the state BEFORE the pending flag: if rotation later
                // fails, the next RecordLifecycle must not still see
                // background+over-timeout and re-schedule forever.
                _currentLifecycleState = nextState;
                _currentLifecycleStateEnteredAtTicks = nowTicks;
                _currentLifecycleStateEnteredAtStopwatchTicks = nowStopwatchTicks;
                _pendingRotation = true;
                return;
            }

            var name = t == LifecycleTransition.BackgroundStart ? "background_start" : "foreground";
            var meta = new Dictionary<string, object>
            {
                ["transition"]                  = name,
                ["prev_state"]                  = prevState,
                ["duration_in_prev_state_ms"]   = Math.Max(0L, durationMs),
            };
            Pipeline?.EnqueueEvent("lifecycle", metadataJson: EventPipeline.DictToJson(meta));
            _currentLifecycleState = nextState;
            _currentLifecycleStateEnteredAtTicks = nowTicks;
            _currentLifecycleStateEnteredAtStopwatchTicks = nowStopwatchTicks;
            // The only field that survives for PerformRotation to backdate
            // session_end to — _currentLifecycleStateEnteredAtTicks gets
            // overwritten with the foreground-return time at scheduling.
            if (nextState == "background")
            {
                _lastBackgroundStartTicks = nowTicks;
            }
            // Mirror to disk so next launch's recovery can classify an unclean
            // death: foreground → likely crash/ANR, background → likely swipe/
            // low-mem kill. Best-effort — never throw from the lifecycle hot path.
            try { SessionFiles.WriteLifecycleState(nextState); }
            catch (Exception ex) { PlayScopeLog.Warning("RecordLifecycle: lifecycle persist failed", ex); }

            // Suspend the watchdog while backgrounded (no Update → no heartbeat
            // → would mis-classify as a freeze); re-arm refreshed on foreground.
            if (nextState == "background") AnrWatchdog?.Suspend();
            else                            AnrWatchdog?.Resume();
        }

        /// <summary>
        /// Atomically reads-and-clears the pending-rotation flag. Called by
        /// PlayScopeMonoBehaviour.Update at the top of every frame so the
        /// rotation executes on the FOLLOWING tick after a foreground
        /// transition triggers it — never re-entrantly inside the
        /// OnApplicationPause callback that set the flag.
        /// </summary>
        internal static bool ConsumePendingRotation()
        {
            if (!_pendingRotation) return false;
            _pendingRotation = false;
            return true;
        }

        /// <summary>
        /// Closes the current session (end_status="background_timeout") and
        /// re-runs Initialize() with the stored context — new session_id,
        /// sequence_num reset, billed as a separate session. MUST be called
        /// outside any OnApplicationPause / OnFocusChanged chain (TeardownInternal
        /// destroys the driver GameObject, a destroy-self from inside its own
        /// callback). Invoked from MB.Update via ConsumePendingRotation.
        /// </summary>
        internal static void PerformRotation()
        {
            // Lock the WHOLE rotation — teardown + re-init must be atomic vs any
            // other Initialize/Shutdown. Else an OnApplicationQuit between the two
            // sees _initialized=false, skips, and leaves no live session.
            if (Interlocked.CompareExchange(ref _lifecycleBusy, 1, 0) != 0)
            {
                // ConsumePendingRotation already cleared the flag and the write
                // gate is closed — bailing without re-arming would strand
                // _acceptingEvents=false with no rotation ever coming (silent
                // total event loss). Re-arm so the next Update retries.
                _pendingRotation = true;
                PlayScopeLog.Warning(
                    "PerformRotation deferred — another lifecycle op is in flight; will retry next frame.");
                return;
            }
            try
            {
                if (!_initialized || _disabled) return;
                // background_start is the "real" end of the old session — the
                // suspension after it isn't gameplay. 0 (never backgrounded)
                // falls through to now inside TeardownInternal.
                DateTime? backdatedEnd = _lastBackgroundStartTicks > 0
                    ? new DateTime(_lastBackgroundStartTicks, DateTimeKind.Utc)
                    : (DateTime?)null;
                var ctx = _context;
                if (ctx == null)
                {
                    PlayScopeLog.Warning(
                        "PerformRotation called but no stored context — cannot re-initialize. " +
                        "Falling back to clean shutdown.");
                    TeardownInternal(emitSessionEnd: true, endStatus: "background_timeout", reason: "background_timeout",
                        endTimestampOverride: backdatedEnd);
                    return;
                }
                TeardownInternal(emitSessionEnd: true, endStatus: "background_timeout", reason: "background_timeout",
                    endTimestampOverride: backdatedEnd);
                // Bypass the admission gate — we already hold the lifecycle lock.
                InitializeLocked(ctx);
            }
            catch (Exception ex)
            {
                // Never rethrow into Update — the finally below still re-opens
                // the gate and releases the lifecycle lock.
                PlayScopeLog.Error("PerformRotation failed", ex);
            }
            finally
            {
                // Re-open the gate even if InitializeLocked threw, else a
                // partially-init'd SDK swallows every event forever.
                _acceptingEvents = true;
                Interlocked.Exchange(ref _lifecycleBusy, 0);
            }
        }

        /// <summary>
        /// One-shot <c>first_frame_rendered</c> on the first Update() tick after
        /// Initialize, carrying elapsed ms since Initialize for TTI plotting.
        /// Interlocked-guarded against a rare double-emit.
        /// </summary>
        internal static void EmitFirstFrameRenderedOnce()
        {
            if (!_initialized || _disabled) return;
            if (Interlocked.CompareExchange(ref _firstFrameEmitted, 1, 0) != 0) return;
            // Stamp first-frame so first_input_latency measures from "player saw
            // the screen", not session_start (which folds in headless init).
            _firstFrameTicks = DateTime.UtcNow.Ticks;
            var elapsedMs = _currentLifecycleStateEnteredAtTicks == 0
                ? 0
                : (DateTime.UtcNow.Ticks - _currentLifecycleStateEnteredAtTicks) / TimeSpan.TicksPerMillisecond;
            var meta = new Dictionary<string, object>
            {
                ["ms_since_session_start"] = Math.Max(0L, elapsedMs),
            };
            Pipeline?.EnqueueEvent("first_frame_rendered",
                metadataJson: EventPipeline.DictToJson(meta));
        }

        /// <summary>
        /// One-shot <c>first_input_latency</c> on the first input after
        /// <c>first_frame_rendered</c>, carrying ms-since-first-frame and the
        /// input kind. Polled every Update — cheap no-op once fired or before
        /// first frame. Sessions with no input (Editor/headless) never emit,
        /// which is correct.
        /// </summary>
        internal static void EmitFirstInputLatencyIfFired(string inputKind)
        {
            if (!_initialized || _disabled) return;
            if (_firstFrameEmitted == 0) return; // wait for first frame
            if (Interlocked.CompareExchange(ref _firstInputEmitted, 1, 0) != 0) return;
            var nowTicks = DateTime.UtcNow.Ticks;
            // 0 if first-frame ticks weren't stamped, not an epoch-derived number.
            var latencyMs = _firstFrameTicks == 0
                ? 0L
                : Math.Max(0L, (nowTicks - _firstFrameTicks) / TimeSpan.TicksPerMillisecond);
            var meta = new Dictionary<string, object>
            {
                ["latency_ms"] = latencyMs,
                ["input_kind"] = inputKind ?? "unknown",
            };
            Pipeline?.EnqueueEvent("first_input_latency",
                metadataJson: EventPipeline.DictToJson(meta));
        }

        /// <summary>
        /// Emits <c>network_change</c> when reachability flips. The first sample
        /// just initializes the baseline (no "previous" to compare).
        /// </summary>
        internal static void RecordNetworkReachabilityIfChanged(int currentReachability)
        {
            if (!_initialized || _disabled) return;
            if (_lastNetworkReachability == int.MinValue)
            {
                _lastNetworkReachability = currentReachability;
                return;
            }
            if (_lastNetworkReachability == currentReachability) return;
            var prev = _lastNetworkReachability;
            _lastNetworkReachability = currentReachability;
            var meta = new Dictionary<string, object>
            {
                ["from"] = ReachabilityToString(prev),
                ["to"]   = ReachabilityToString(currentReachability),
            };
            Pipeline?.EnqueueEvent("network_change",
                metadataJson: EventPipeline.DictToJson(meta));
        }

        private static string ReachabilityToString(int r) => r switch
        {
            0 => "none",                  // NetworkReachability.NotReachable
            1 => "carrier",               // ReachableViaCarrierDataNetwork
            2 => "wifi",                  // ReachableViaLocalAreaNetwork
            _ => "unknown",
        };

        // ── MonoBehaviour driver ──────────────────────────────────────────────────

        private static void EnsureDriver()
        {
            // Only create a GameObject driver in play mode; Edit-mode/batch can
            // still invoke Shutdown manually.
            if (!Application.isPlaying)
            {
                // Application.quitting so Editor batch runs still finalize cleanly.
                if (!_quittingSubscribed)
                {
                    try
                    {
                        Application.quitting += OnApplicationQuittingHandler;
                        _quittingSubscribed = true;
                    }
                    catch { /* not all platforms support this — best-effort */ }
                }
                return;
            }

            if (_driverGo != null) return;
            _driverGo = new GameObject("PlayScopeSdk");
            _driverGo.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_driverGo);
            _driverGo.AddComponent<PlayScopeMonoBehaviour>();

            if (!_quittingSubscribed)
            {
                Application.quitting += OnApplicationQuittingHandler;
                _quittingSubscribed = true;
            }
        }

        private static void OnApplicationQuittingHandler()
        {
            // OnApplicationQuit on the MB also calls Shutdown; Shutdown is idempotent via _initialized check.
            Shutdown();
        }

        // ── AutoCaptureUnityLogs handler ──────────────────────────────────────────

        private static void OnUnityLogReceived(string condition, string stackTrace, LogType type)
        {
            try
            {
                // Recursion guard: skip our own [PlayScope] logs.
                if (!string.IsNullOrEmpty(condition) && condition.StartsWith("[PlayScope]", StringComparison.Ordinal))
                    return;

                LogLevel level;
                bool isException = false;
                switch (type)
                {
                    case LogType.Log: level = LogLevel.Debug; break;
                    case LogType.Warning: level = LogLevel.Warning; break;
                    case LogType.Error: level = LogLevel.Error; break;
                    case LogType.Assert: level = LogLevel.Error; break;
                    case LogType.Exception: level = LogLevel.Exception; isException = true; break;
                    default: level = LogLevel.Debug; break;
                }

                if ((int)level < (int)_autoCaptureMinLevel) return;

                if (isException)
                    Pipeline?.EnqueueLog("exception", condition ?? "", stackTrace);
                else
                    Pipeline?.EnqueueLog(level.ToString().ToLower(), condition ?? "");
            }
            catch
            {
                // Never let log capture throw — would re-trigger the log handler.
            }
        }

        // ── Application.lowMemory handler ──────────────────────────────────────
        // Not deduped: a burst of warnings is itself signal (escalating pressure
        // → likely imminent OOM kill).
        private static void OnLowMemoryWarning()
        {
            if (!_initialized || _disabled) return;
            try
            {
                long heapMb     = GC.GetTotalMemory(false) / 1048576L;
                long reservedMb = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576L;
                int  systemMb   = UnityEngine.SystemInfo.systemMemorySize;
                var meta = new Dictionary<string, object>
                {
                    ["heap_mb"]          = heapMb,
                    ["reserved_mb"]      = reservedMb,
                    ["system_memory_mb"] = systemMb,
                };
                Pipeline?.EnqueueEvent("memory_warning",
                    metadataJson: EventPipeline.DictToJson(meta));
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("OnLowMemoryWarning failed", ex);
            }
        }
    }

    internal enum LifecycleTransition
    {
        BackgroundStart,
        Foreground
    }
}
