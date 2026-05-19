using System;
using System.Threading;
using UnityEngine;
using PlayScopeSdk.Core.Session;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    internal static class PlayScopeRuntime
    {
        internal const string SdkVersion = "0.1.21";

        private static volatile bool _initialized;
        private static volatile bool _disabled;
        private static int _initialStateSet; // 0/1 — Interlocked

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

            // Flush any buffered state patch before session_end so the last
            // changes survive — otherwise the 100ms coalescing window would
            // strand them in the buffer when we stop the pipeline below.
            StatePatchCoalescer.FlushNow();

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

        private static string GetPlatformString()
        {
#if UNITY_IOS
            return "ios";
#elif UNITY_ANDROID
            return "android";
#elif UNITY_EDITOR
            return "editor";
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
            _writer?.FlushImmediate();
        }

        /// <summary>
        /// Records a lifecycle transition. Called from MonoBehaviour pause/focus callbacks.
        /// </summary>
        internal static void RecordLifecycle(LifecycleTransition t)
        {
            if (!_initialized || _disabled) return;
            var name = t == LifecycleTransition.BackgroundStart ? "background_start" : "foreground";
            Pipeline?.EnqueueEvent("lifecycle", metadataJson: "{\"transition\":\"" + name + "\"}");
        }

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
