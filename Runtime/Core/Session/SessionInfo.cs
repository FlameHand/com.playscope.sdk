using System;

namespace PlayScopeSdk.Core.Session
{
    internal sealed class SessionInfo
    {
        public string SessionId { get; }
        public string SessionShortId { get; }
        public DateTime StartedAt { get; }
        public string SdkVersion { get; }
        public int SchemaVersion { get; }

        // Short-id length picked to keep collisions astronomically unlikely
        // across a device's lifetime. The orphan-rescue and ownership-check
        // paths in UploaderWorker / SessionRecovery key on this prefix; a
        // collision means new session events get cross-pollinated into an
        // OLD session's manifest (the same 0.1.16 commingling bug re-opened
        // by the birthday paradox).
        //
        // 5 hex chars = 1.05M possibilities → ~50% collision probability
        //                after ~1200 sessions on the same device.
        // 8 hex chars = 4.29B possibilities → ~50% collision probability
        //                after ~65k sessions, ~1-in-4-billion at any single
        //                pair lookup. Acceptable.
        private const int ShortIdLength = 8;

        internal SessionInfo(string sessionId, DateTime startedAt, string sdkVersion, int schemaVersion = 1)
        {
            SessionId = sessionId;
            var dashless = sessionId.Replace("-", "");
            SessionShortId = dashless.Length >= ShortIdLength
                ? dashless.Substring(0, ShortIdLength)
                : dashless; // pathological — should never trigger with Guid.NewGuid()
            StartedAt = startedAt;
            SdkVersion = sdkVersion;
            SchemaVersion = schemaVersion;
        }

        internal static SessionInfo Generate(string sdkVersion)
        {
            return new SessionInfo(Guid.NewGuid().ToString(), DateTime.UtcNow, sdkVersion);
        }
    }
}
