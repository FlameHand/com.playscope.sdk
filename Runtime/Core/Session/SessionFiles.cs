using System;
using System.IO;
using UnityEngine;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Core.Session
{
    internal static class SessionFiles
    {
        // Full sequence for creating new session files:
        // 1. session.json (before session.lock!)
        // 2. session.lock
        // 3. session.hb
        internal static void WriteNewSession(SessionInfo session, string sdkUserId)
        {
            WriteSessionJson(session, sdkUserId);
            CreateSessionLock();
            UpdateHeartbeat();
        }

        // session.json — contains session_id and start metadata
        internal static void WriteSessionJson(SessionInfo session, string sdkUserId)
        {
            var json = $"{{" +
                $"\"session_id\":\"{session.SessionId}\"," +
                $"\"session_short_id\":\"{session.SessionShortId}\"," +
                $"\"started_at\":\"{session.StartedAt:o}\"," +
                $"\"sdk_version\":\"{session.SdkVersion}\"," +
                $"\"schema_version\":{session.SchemaVersion}" +
                $"}}";

            File.WriteAllText(PlayScopeDirectory.SessionJson, json);
        }

        // session.lock — empty file, signals that session is active
        internal static void CreateSessionLock()
        {
            File.WriteAllText(PlayScopeDirectory.SessionLock, string.Empty);
        }

        // Deletes session.lock on normal session termination
        internal static void DeleteSessionLock()
        {
            var path = PlayScopeDirectory.SessionLock;
            if (File.Exists(path))
                File.Delete(path);
        }

        // session.hb — current UTC timestamp, updated every 30s by heartbeat worker
        internal static void UpdateHeartbeat()
        {
            File.WriteAllText(PlayScopeDirectory.SessionHb, DateTime.UtcNow.ToString("o"));
        }

        // Checks if there is a lock from a previous run (for session recovery)
        internal static bool HasPreviousSessionLock()
        {
            return File.Exists(PlayScopeDirectory.SessionLock);
        }

        // Reads session_id from existing session.json (for recovery)
        internal static string TryReadSessionId()
        {
            var path = PlayScopeDirectory.SessionJson;
            if (!File.Exists(path)) return null;

            try
            {
                var json = File.ReadAllText(path);
                var dto = Internal.SimpleJson.Deserialize(json);
                if (dto != null && dto.TryGetValue("session_id", out var id) && id is string idStr)
                    return idStr;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] Failed to read session.json: {ex.Message}");
            }
            return null;
        }

        // Reads last heartbeat timestamp (for recovery — approximate end time)
        internal static DateTime? TryReadLastHeartbeat()
        {
            var path = PlayScopeDirectory.SessionHb;
            if (!File.Exists(path)) return null;

            try
            {
                var text = File.ReadAllText(path).Trim();
                if (DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    return dt;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] Failed to read session.hb: {ex.Message}");
            }
            return null;
        }
    }
}
