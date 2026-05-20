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
        internal const string SdkVersion = "0.1.37";

        // PlayerPrefs key — stores the Application.version we last saw alive.
        // Read once on Initialize so we can emit app_update_detected the first
        // time a build with a different version starts up.
        private const string LastSeenAppVersionPrefKey = "playscope:last_app_version";

        private static volatile bool _initialized;
        private static volatile bool _disabled;
        private static int _initialStateSet; // 0/1 — Interlocked

        // Lifecycle state machine — feeds duration_in_prev_state_ms on every
        // background_start / foreground transition. Initialized to "foreground"
        // on session_start so the first transition out of it carries a real
        // delta (entire startup spent in foreground, etc.).
        private static string _currentLifecycleState = "foreground";
        private static long _currentLifecycleStateEnteredAtTicks;

        // Network reachability watchdog — sampled by MetricsSampler. When the
        // value changes we emit a network_change event (separate from the
        // periodic network_reachability metric so the timeline carries a
        // discrete signal at the transition moment instead of a sample line
        // crossing).
        private static int _lastNetworkReachability = int.MinValue;

        // first_frame_rendered guard — flipped by MonoBehaviour on its first
        // Update tick so we emit the event exactly once per session.
        private static int _firstFrameEmitted;

        internal static bool IsInitialized => _initialized;
        internal static bool IsDisabled => _disabled;

        internal static DeviceIdentity Device { get; private set; }
        internal static SessionInfo CurrentSession { get; private set; }

        internal static EventQueue? Queue { get; private set; }
        internal static EventPipeline? Pipeline { get; private set; }
        internal static UploadQueue? UploadQueue { get; private set; }
        // Repeats-collapsing buffer for absorbable log levels. Created
        // alongside Pipeline; nulled on teardown so a re-Initialize starts
        // fresh. MonoBehaviour.Update ticks it once per frame; FlushOnPause /
        // TeardownInternal drain it so a backgrounded / shutting-down session
        // can't strand the last few seconds of buffered logs.
        internal static LogDedupBuffer? LogDedupBuffer { get; private set; }
        // Single shared coalescer for state_patch debouncing. Lives for the
        // session lifetime — flushed on pause / shutdown so its buffer never
        // strands a patch.
        internal static StatePatchCoalescer StatePatchCoalescer { get; } = new();
        // Parallel coalescer for session_data_* events. Independent buffer
        // from profile-state so a periodic device sample never overwrites a
        // gameplay key. Wider window (1 s vs 100 ms) folds init-burst patches.
        internal static SessionDataCoalescer SessionDataCoalescer { get; } = new();
        private static WriterWorker? _writer;
        private static HeartbeatWorker? _heartbeat;
        internal static UploaderWorker? _uploader;
        // Main-thread ANR watchdog. Null when disabled (Editor, or via
        // PlayScopeContext.AnrDetectionEnabled = false) so the MonoBehaviour
        // heartbeat call short-circuits without overhead.
        internal static AnrWatchdog? AnrWatchdog { get; private set; }
        private static GameObject? _driverGo;
        private static bool _quittingSubscribed;
        private static bool _logCaptureSubscribed;
        // Application.lowMemory subscription state — tracked separately so
        // TeardownInternal can unhook it without poking the handler if we
        // never subscribed (e.g. ForceDisable on partial init).
        private static bool _lowMemorySubscribed;
        private static LogLevel _autoCaptureMinLevel;

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
            if (_initialized || _disabled)
            {
                PlayScopeLog.Warning("Initialize called more than once — ignored.");
                return;
            }

            // Reset every piece of session-scoped state UP-FRONT, before any
            // MonoBehaviour driver is created or any worker thread is started.
            // Otherwise a re-Initialize after Shutdown (test runners, scene
            // reload tooling) would see stale flag values — e.g.
            // _firstFrameEmitted still 1 from the prior session → first_frame
            // event never re-emitted; SequenceCounter at its end-of-prior-session
            // value → mixed-up sequence numbers on early events.
            SensitiveKeyFilter.ResetWarnings();
            _initialStateSet = 0;
            _currentLifecycleState = "foreground";
            _currentLifecycleStateEnteredAtTicks = DateTime.UtcNow.Ticks;
            _firstFrameEmitted = 0;
            _lastNetworkReachability = int.MinValue;
            SequenceCounter.Reset();
            PlayScope.ResetSessionScopedState();

            // Step 1: Validate ApiKey
            if (string.IsNullOrWhiteSpace(context?.ApiKey))
            {
                PlayScopeLog.Warning("ApiKey is null or empty — SDK disabled.");
                _disabled = true;
                return;
            }

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
            // When the writer finalizes a chunk due to a critical event, wake the uploader immediately
            _writer.OnCriticalChunkFinalized = () => _uploader?.TriggerInstantUpload();
            _writer.Start();

            _uploader.Start();

            _heartbeat = new HeartbeatWorker();
            _heartbeat.Start();

            // ANR watchdog — disabled in Editor by default since IDE
            // breakpoints would generate false positives. Batch mode is
            // OK because there's no debugger attached. Disabled entirely
            // when the caller opted out via PlayScopeContext.
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

            // OS low-memory hook. Application.lowMemory is cross-platform —
            // fires on Android (Activity.onTrimMemory TRIM_MEMORY_RUNNING_*)
            // and iOS (UIApplicationDidReceiveMemoryWarning) without needing
            // a native plugin on either side. The callback runs on the Unity
            // main thread so it's safe to read Profiler / SystemInfo APIs
            // from inside it. We emit a critical-priority memory_warning
            // event so the chunk gets flushed immediately — if the OS kills
            // the app a moment later the signal still lands server-side.
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

            // Open the gate ONLY now — every subsystem above is wired and
            // every state field has been reset. Subsequent public API calls
            // hit a coherent SDK; events emitted from inside this method
            // (session_start, app_update_detected, session_data seed) use the
            // Pipeline directly and don't go through the IsInitialized guard
            // so the ordering here is intentional.
            _initialized = true;

            // Enqueue session_start event
            var env = "production";
            if (context.Metadata != null &&
                context.Metadata.TryGetValue("environment", out var envVal) &&
                envVal is string envStr && !string.IsNullOrEmpty(envStr))
                env = envStr;

            var sessionMeta = new System.Collections.Generic.Dictionary<string, object>
            {
                ["app_version"] = UnityEngine.Application.version,
                ["build_number"] = UnityEngine.Application.buildGUID,
                ["environment"] = env,
                ["platform"] = GetPlatformString(),
                ["device_model"] = UnityEngine.SystemInfo.deviceModel,
                ["os_version"] = UnityEngine.SystemInfo.operatingSystem,
                ["sdk_version"] = SdkVersion
            };
            Pipeline!.EnqueueEvent("session_start",
                metadataJson: EventPipeline.DictToJson(sessionMeta));

            // app_update_detected — compare Application.version with the value
            // PlayerPrefs remembered last time the app ran. We emit only on a
            // real version change (first-ever install is silent — there's no
            // "previous" to compare against and pretending otherwise would
            // pollute the dashboard with a synthetic update for every fresh
            // install). Persist FIRST then emit so a save failure can't
            // double-emit on the next launch.
            try
            {
                var currentVersion = UnityEngine.Application.version ?? "";
                var lastSeenVersion = UnityEngine.PlayerPrefs.GetString(LastSeenAppVersionPrefKey, "");
                var versionChanged = lastSeenVersion != currentVersion;
                if (versionChanged)
                {
                    // Persist the new value BEFORE emitting. If the save throws
                    // here, the catch suppresses the emit too — better to skip
                    // one update event than to repeat it on every launch until
                    // PlayerPrefs recovers.
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

            // Seed session_data with diagnostic fields the dashboard needs but
            // didn't get into session_start. These describe the runtime that
            // sessions can later patch on top of (Addressables locators after
            // their init, periodic disk samples, etc.) — see UpdateSessionData.
            // We push through the coalescer so the first wave of bootstrap
            // pushes from wrappers folds into one session_data_initial row.
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
            if (!_initialized || _disabled) return;
            TeardownInternal(emitSessionEnd: true);
        }

        /// <summary>
        /// Idempotent teardown shared by Shutdown (normal end) and ForceDisable
        /// (partial-init failure). Every operation is null-guarded so it can
        /// safely run on a half-initialised SDK where some subsystems were
        /// constructed and others weren't.
        /// </summary>
        private static void TeardownInternal(bool emitSessionEnd)
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

            if (emitSessionEnd)
            {
                // Enqueue session_end (critical — triggers instant upload).
                // Skipped when called from ForceDisable: we can't honestly say
                // the session "ended normally" if Initialize itself blew up
                // partway through.
                Pipeline?.EnqueueEvent("session_end",
                    metadataJson: "{\"end_status\":\"normal\"}");
            }

            _writer?.DrainAndFinalize();
            _writer?.Stop();
            _writer = null;

            // After writer finalizes the session_end chunk it lands in UploadQueue —
            // signal the uploader to flush immediately before we stop it.
            _uploader?.TriggerInstantUpload();
            _uploader?.Stop();
            _uploader = null;

            // Null out Pipeline/Queue so a subsequent Initialize starts fresh.
            // Without this, a re-Initialize would create a NEW Pipeline but the
            // persistent PlayScopeMonoBehaviour (see below) keeps a reference
            // to the OLD one and routes future Update-ticks through the dead
            // pipeline — silent metric loss + unbounded queue growth.
            Pipeline = null;
            Queue = null;
            LogDedupBuffer = null;

            // Destroy the MonoBehaviour driver. Otherwise the GameObject
            // persists DontDestroyOnLoad and EnsureDriver's early-return on
            // _driverGo != null skips re-attachment. The next Initialize would
            // be ticked by a stale MonoBehaviour with a frozen _sampler.
            if (_driverGo != null)
            {
                try { UnityEngine.Object.Destroy(_driverGo); } catch { /* best-effort */ }
                _driverGo = null;
            }

            // Discard any in-flight scene-load progress entries — they hold
            // AsyncOperation references that keep their target scenes pinned
            // in memory, which we don't want bleeding across sessions.
            SceneLoadProgressTracker.ClearAll();

            try { SessionFiles.DeleteSessionLock(); } catch { /* best-effort */ }
            _initialized = false;
            PlayScopeLog.Info("Shutdown complete.");
        }

        // Runtime check beats the compile-time defines because UNITY_ANDROID /
        // UNITY_IOS are defined whenever the *build target* is set to that
        // platform — including in-Editor play with Android selected. Without
        // the isEditor guard a test run from the Editor would report itself
        // as "android" while device_model still showed the desktop name,
        // producing confusing dashboard rows. Order: editor wins, then the
        // actual deployed platform via the existing compile-time chain.
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

        // Called from OnApplicationPause / focus lost. Flushes both the patch
        // coalescer (so its buffer doesn't sit through a background period
        // that may end with the OS killing the app) and the writer.
        internal static void FlushOnPause()
        {
            StatePatchCoalescer.FlushNow();
            SessionDataCoalescer.FlushNow();
            // Drain buffered log dedup entries before background — the OS may
            // kill us at any point during the pause window and we don't want
            // up to 5 s of buffered first-of-key samples disappearing with it.
            LogDedupBuffer?.FlushNow();
            _writer?.FlushImmediate();
            // The OS will stop running Update() in a moment — suspending
            // the watchdog now keeps it from reporting the backgrounded
            // state as a multi-minute ANR. Re-armed on the matching
            // foreground transition via the RecordLifecycle path.
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
            // Drop no-op transitions. On iOS / Android the OS frequently
            // fires BOTH OnApplicationPause(true) AND OnFocusChanged(false)
            // for the same backgrounding event — the prior implementation
            // emitted two back-to-back rows (background → background_start)
            // with a near-zero duration that was meaningless. Now the
            // second call is silently swallowed.
            if (nextState == _currentLifecycleState) return;

            var name = t == LifecycleTransition.BackgroundStart ? "background_start" : "foreground";
            var nowTicks = DateTime.UtcNow.Ticks;
            var durationMs = _currentLifecycleStateEnteredAtTicks == 0
                ? 0
                : (nowTicks - _currentLifecycleStateEnteredAtTicks) / TimeSpan.TicksPerMillisecond;
            var prevState = _currentLifecycleState;
            var meta = new Dictionary<string, object>
            {
                ["transition"]                  = name,
                ["prev_state"]                  = prevState,
                ["duration_in_prev_state_ms"]   = Math.Max(0L, durationMs),
            };
            Pipeline?.EnqueueEvent("lifecycle", metadataJson: EventPipeline.DictToJson(meta));
            _currentLifecycleState = nextState;
            _currentLifecycleStateEnteredAtTicks = nowTicks;

            // ANR watchdog state follows the OS: suspended while the app
            // is backgrounded (Update() doesn't run, so a long stretch
            // with no heartbeat would mis-classify as a freeze), re-armed
            // on foreground with a refreshed heartbeat so the first
            // half-second post-resume can't false-positive.
            if (nextState == "background") AnrWatchdog?.Suspend();
            else                            AnrWatchdog?.Resume();
        }

        /// <summary>
        /// Emits a one-shot <c>first_frame_rendered</c> event on the first
        /// Update() tick after Initialize. Carries the elapsed ms since
        /// Initialize so the dashboard can plot TTI (time-to-interactive)
        /// across builds. Guard via Interlocked so a worker thread can't
        /// double-emit on a rare second-MB-instantiation path.
        /// </summary>
        internal static void EmitFirstFrameRenderedOnce()
        {
            if (!_initialized || _disabled) return;
            if (Interlocked.CompareExchange(ref _firstFrameEmitted, 1, 0) != 0) return;
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
        /// Emits a <c>network_change</c> event when reachability flips. Called
        /// from MetricsSampler with the current <see cref="Application.internetReachability"/>
        /// value (cast to int). First-ever sample initializes the watchdog
        /// without emitting — we have no "previous" to compare against.
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
            // Only create a real driver when we're running inside Unity play mode.
            // In Edit-mode tests or batch tooling, skip GameObject creation — Shutdown
            // can still be invoked manually if the test wants it.
            if (!Application.isPlaying)
            {
                // Subscribe to Application.quitting so Editor batch runs still finalize cleanly.
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
        //
        // Fired by the OS on Android (Activity.onTrimMemory at TRIM_MEMORY_*
        // levels) and iOS (UIApplicationDidReceiveMemoryWarning). Runs on the
        // Unity main thread so it's safe to read Profiler / SystemInfo here.
        // We don't dedupe: a burst of warnings in quick succession is itself
        // signal (escalating memory pressure → likely OOM kill imminent).
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
