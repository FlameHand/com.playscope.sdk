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

            Debug.Log($"[PlayScope] Initialized. session_id={CurrentSession.SessionId}, sdk_user_id={Device.SdkUserId}");

            // TODO(PSDK-10): start writer worker loop
            // TODO(PSDK-11): start uploader worker loop
            // TODO(PSDK-13): start metrics sampler + heartbeat
        }

        // Called by PlayScopeMonoBehaviour on application quit
        internal static void Shutdown()
        {
            if (!_initialized || _disabled) return;
            // TODO(PSDK-10): flush event queue, write session_end
            // TODO(PSDK-13): stop metrics sampler and heartbeat
            SessionFiles.DeleteSessionLock();
            Debug.Log("[PlayScope] Shutdown complete.");
        }
    }
}
