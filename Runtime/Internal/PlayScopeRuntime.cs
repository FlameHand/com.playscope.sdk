using UnityEngine;
using PlayScopeSdk.Core.Session;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    internal static class PlayScopeRuntime
    {
        private static volatile bool _initialized;
        private static volatile bool _disabled;

        internal static bool IsInitialized => _initialized;
        internal static bool IsDisabled => _disabled;

        internal static DeviceIdentity Device { get; private set; }
        internal static SessionInfo CurrentSession { get; private set; }

        internal static EventQueue? Queue { get; private set; }
        internal static EventPipeline? Pipeline { get; private set; }
        internal static UploadQueue? UploadQueue { get; private set; }
        private static WriterWorker? _writer;
        private static HeartbeatWorker? _heartbeat;

        // Called by PlayScope.Initialize()
        internal static void Initialize(PlayScopeContext context)
        {
            if (_initialized)
            {
                Debug.LogWarning("[PlayScope] Initialize called more than once — ignored.");
                return;
            }

            // Reset sensitive-key filter warnings for the new session
            SensitiveKeyFilter.ResetWarnings();

            // Step 1: Validate ApiKey
            if (string.IsNullOrWhiteSpace(context?.ApiKey))
            {
                Debug.LogWarning("[PlayScope] ApiKey is null or empty — SDK disabled.");
                _disabled = true;
                return;
            }

            // Step 2: Ensure directories exist
            PlayScopeDirectory.EnsureRootDirectories();

            // Step 3: Load or create device identity
            Device = DeviceIdentity.LoadOrCreate();

            // Step 4: Check for previous session lock (recovery — PSDK-12 will implement full recovery)
            if (SessionFiles.HasPreviousSessionLock())
            {
                var prevSessionId = SessionFiles.TryReadSessionId();
                var lastHb = SessionFiles.TryReadLastHeartbeat();
                Debug.Log($"[PlayScope] Previous session lock detected (id={prevSessionId}, last_hb={lastHb:o}). Recovery will run in PSDK-12.");
                // TODO(PSDK-12): run full session recovery here
            }

            // Step 5: Start new session
            CurrentSession = SessionInfo.Generate();
            SessionFiles.WriteNewSession(CurrentSession, Device.SdkUserId);

            _initialized = true;

            // PSDK-10: start event queue + writer worker
            SequenceCounter.Reset();
            Queue = new EventQueue();
            UploadQueue = new UploadQueue();
            Pipeline = new EventPipeline(Queue);
            _writer = new WriterWorker(Queue, UploadQueue, CurrentSession);
            _writer.Start();

            _heartbeat = new HeartbeatWorker();
            _heartbeat.Start();

            // Enqueue session_start event
            var sessionMeta = new System.Collections.Generic.Dictionary<string, object>
            {
                ["app_version"] = UnityEngine.Application.version,
                ["build_number"] = UnityEngine.Application.buildGUID,
                ["environment"] = "production",
                ["platform"] = GetPlatformString(),
                ["device_model"] = UnityEngine.SystemInfo.deviceModel,
                ["os_version"] = UnityEngine.SystemInfo.operatingSystem,
                ["sdk_version"] = "0.1.0"
            };
            Pipeline!.EnqueueEvent("session_start",
                metadataJson: EventPipeline.DictToJson(sessionMeta));

            Debug.Log($"[PlayScope] Initialized. session_id={CurrentSession.SessionId}, sdk_user_id={Device.SdkUserId}");

            // TODO(PSDK-11): start uploader worker loop
        }

        // Called by PlayScopeMonoBehaviour on application quit
        internal static void Shutdown()
        {
            if (!_initialized || _disabled) return;
            _heartbeat?.Stop();
            _heartbeat = null;

            // Enqueue session_end
            Pipeline?.EnqueueEvent("session_end",
                metadataJson: "{\"end_status\":\"normal\"}");

            _writer?.DrainAndFinalize();
            _writer?.Stop();
            SessionFiles.DeleteSessionLock();
            _initialized = false;
            Debug.Log("[PlayScope] Shutdown complete.");
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

        // Called from OnApplicationPause
        internal static void FlushOnPause() => _writer?.FlushImmediate();
    }
}
