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

        // session.lifecycle — last state + entry timestamp, rewritten atomically
        // every transition so recovery knows where the app was when it died.
        // Deleted on clean Shutdown, so its presence next launch signals an
        // unclean exit (the heartbeat alone can't — it updates on backgrounded
        // sessions too and carries no state).
        internal static void WriteLifecycleState(string state)
        {
            var path = PlayScopeDirectory.SessionLifecycle;
            var tmp = path + ".tmp";
            try
            {
                var json = $"{{\"state\":\"{state}\",\"ts\":\"{DateTime.UtcNow:O}\"}}";
                File.WriteAllText(tmp, json);
                // File.Replace is atomic (NTFS/APFS) — no window where the dest is
                // missing. The old Delete+Move left exactly that window and a
                // crash inside it mis-classified a clean exit as "unknown crash".
                // Move fallback for the first write (Replace needs both files).
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] Failed to write session.lifecycle: {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            }
        }

        internal static void DeleteLifecycleState()
        {
            var path = PlayScopeDirectory.SessionLifecycle;
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }

        /// <summary>
        /// Reads the last persisted lifecycle state. Returns (state, ts, intent):
        /// state = foreground / background / user_close / null; intent = true ONLY
        /// when the native hook wrote it on a precise close signal (Android
        /// isFinishing, iOS WillTerminate) — the C# paths always omit it.
        /// Null state (missing or corrupt) is treated as unknown / pessimistic crash.
        /// </summary>
        internal static (string state, DateTime? ts, bool intent) TryReadLifecycleState()
        {
            var path = PlayScopeDirectory.SessionLifecycle;
            if (!File.Exists(path)) return (null, null, false);
            try
            {
                var text = File.ReadAllText(path);
                var dto = Internal.SimpleJson.Deserialize(text);
                if (dto == null) return (null, null, false);
                string state = dto.TryGetValue("state", out var s) && s is string sStr ? sStr : null;
                DateTime? ts = null;
                if (dto.TryGetValue("ts", out var t) && t is string tStr &&
                    DateTime.TryParse(tStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
                    ts = d;
                bool intent = dto.TryGetValue("intent", out var i) && i is bool ib && ib;
                return (state, ts, intent);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] Failed to read session.lifecycle: {ex.Message}");
                return (null, null, false);
            }
        }

        // Reads last heartbeat timestamp (for recovery — approximate end time)
        internal static DateTime? TryReadLastHeartbeat()
        {
            var path = PlayScopeDirectory.SessionHb;
            if (!File.Exists(path)) return null;

            try
            {
                var text = File.ReadAllText(path).Trim();
                if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
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
