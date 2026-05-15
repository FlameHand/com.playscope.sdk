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

        internal SessionInfo(string sessionId, DateTime startedAt, string sdkVersion = "0.1.0", int schemaVersion = 1)
        {
            SessionId = sessionId;
            // short id = first 5 chars of UUID without dashes
            SessionShortId = sessionId.Replace("-", "").Substring(0, 5);
            StartedAt = startedAt;
            SdkVersion = sdkVersion;
            SchemaVersion = schemaVersion;
        }

        internal static SessionInfo Generate()
        {
            return new SessionInfo(Guid.NewGuid().ToString(), DateTime.UtcNow);
        }
    }
}
