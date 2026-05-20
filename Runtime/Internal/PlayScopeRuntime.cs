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
        internal const string SdkVersion = "0.1.25";

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
        private static GameObject? _driverGo;
        private static bool _quittingSubscribed;
        private static bool _logCaptureSubscribed;
        private static LogLevel _autoCaptureMinLevel;

        /// <summary>
        /// Called by PlayScope.Initialize if Initialize itself threw — moves SDK to a
        /// permanently disabled state so subsequent API calls are silent no-ops.
        /// </summary>
        internal static void ForceDisable()
        {
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

        // Called by PlayScope.Initialize()
        internal static void Initialize(PlayScopeContext context)
        {
            if (_initialized || _disabled)
            {
                PlayScopeLog.Warning("Initialize called more than once — ignored.");
                return;
            }

            // Reset sensitive-key filter warnings for the new session
            SensitiveKeyFilter.ResetWarnings();
            _initialStateSet = 0;

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

            _initialized = true;

            // PSDK-10: start event queue + writer worker
            SequenceCounter.Reset();
            Queue = new EventQueue();
            // Note: UploadQueue was already created in Step 4 (SessionRecovery)
            Pipeline = new EventPipeline(Queue);
            _writer = new WriterWorker(Queue, UploadQueue, CurrentSession);
            _uploader = new UploaderWorker(context, CurrentSession, UploadQueue);
            // When the writer finalizes a chunk due to a critical event, wake the uploader immediately
            _writer.OnCriticalChunkFinalized = () => _uploader?.TriggerInstantUpload();
            _writer.Start();

            _uploader.Start();

            _heartbeat = new HeartbeatWorker();
            _heartbeat.Start();

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

            // Wire MonoBehaviour driver + Application.quitting (main thread / play mode only).
            try
            {
                EnsureDriver();
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to install PlayScopeMonoBehaviour driver", ex);
            }

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

            // Start the lifecycle clock from session_start. The very first
            // background_start / foreground transition will report the time
            // we spent in foreground since launch under duration_in_prev_state_ms.
            _currentLifecycleState = "foreground";
            _currentLifecycleStateEnteredAtTicks = DateTime.UtcNow.Ticks;
            _firstFrameEmitted = 0;
            _lastNetworkReachability = int.MinValue;

            // app_update_detected — compare Application.version with the value
            // PlayerPrefs remembered last time the app ran. We emit only on a
            // real version change (first-ever install is silent — there's no
            // "previous" to compare against and pretending otherwise would
            // pollute the dashboard with a synthetic update for every fresh
            // install). PlayerPrefs is read on the main thread inside Initialize,
            // before any worker spins up, so no threading concern.
            try
            {
                var currentVersion = UnityEngine.Application.version ?? "";
                var lastSeenVersion = UnityEngine.PlayerPrefs.GetString(LastSeenAppVersionPrefKey, "");
                if (!string.IsNullOrEmpty(lastSeenVersion) && lastSeenVersion != currentVersion)
                {
                    var updateMeta = new Dictionary<string, object>
                    {
                        ["from_version"] = lastSeenVersion,
                        ["to_version"]   = currentVersion,
                    };
                    Pipeline.EnqueueEvent("app_update_detected",
                        metadataJson: EventPipeline.DictToJson(updateMeta));
                }
                if (lastSeenVersion != currentVersion)
                {
                    UnityEngine.PlayerPrefs.SetString(LastSeenAppVersionPrefKey, currentVersion);
                    UnityEngine.PlayerPrefs.Save();
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

            // Unsubscribe Unity log capture (idempotent guard)
            if (_logCaptureSubscribed)
            {
                try { Application.logMessageReceivedThreaded -= OnUnityLogReceived; }
                catch { /* best-effort */ }
                _logCaptureSubscribed = false;
            }

            if (_quittingSubscribed)
            {
                try { Application.quitting -= OnApplicationQuittingHandler; }
                catch { /* best-effort */ }
                _quittingSubscribed = false;
            }

            _heartbeat?.Stop();
            _heartbeat = null;

            // Flush any buffered state / session-data patches before session_end
            // so the last changes survive — otherwise the coalescing windows
            // would strand them in the buffer when we stop the pipeline below.
            StatePatchCoalescer.FlushNow();
            SessionDataCoalescer.FlushNow();

            // Enqueue session_end (critical — triggers instant upload)
            Pipeline?.EnqueueEvent("session_end",
                metadataJson: "{\"end_status\":\"normal\"}");

            _writer?.DrainAndFinalize();
            _writer?.Stop();

            // After writer finalizes the session_end chunk it lands in UploadQueue —
            // signal the uploader to flush immediately before we stop it.
            _uploader?.TriggerInstantUpload();
            _uploader?.Stop();
            _uploader = null;

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
            _writer?.FlushImmediate();
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
            var name = t == LifecycleTransition.BackgroundStart ? "background_start" : "foreground";
            // Compute time spent in the previous state. We don't dedup repeated
            // transitions (Unity will sometimes fire focus_changed + pause for
            // the same OS event) — instead, an immediate "same as current"
            // transition reports a near-zero duration which is itself signal.
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
            _currentLifecycleState = name == "background_start" ? "background" : "foreground";
            _currentLifecycleStateEnteredAtTicks = nowTicks;
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
    }

    internal enum LifecycleTransition
    {
        BackgroundStart,
        Foreground
    }
}
