using UnityEngine;
using PlayScopeSdk.Core.Session;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    internal static class PlayScopeRuntime
    {
        internal const string SdkVersion = "0.1.5";

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
        internal static UploaderWorker? _uploader;

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

            // Step 4: Recover any stale session from a previous crash (PSDK-12)
            // UploadQueue is created here so recovery can enqueue chunks immediately;
            // the uploader worker is started after the new session is fully initialised.
            UploadQueue = new UploadQueue();
            SessionRecovery.RecoverIfNeeded(UploadQueue);

            // Step 5: Start new session
            CurrentSession = SessionInfo.Generate(SdkVersion);
            SessionFiles.WriteNewSession(CurrentSession, Device.SdkUserId);

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

            Debug.Log($"[PlayScope] Initialized. session_id={CurrentSession.SessionId}, sdk_user_id={Device.SdkUserId}");
        }

        // Called by PlayScopeMonoBehaviour on application quit
        internal static void Shutdown()
        {
            if (!_initialized || _disabled) return;
            _heartbeat?.Stop();
            _heartbeat = null;

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
