using System;
using System.IO;
using UnityEngine;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Recovers any stale session left behind by a previous crash or forced-quit.
    /// Must be called after EnsureRootDirectories() and before WriteNewSession().
    /// </summary>
    internal static class SessionRecovery
    {
        // -------------------------------------------------------------------------
        // Public entry point
        // -------------------------------------------------------------------------

        /// <summary>
        /// Checks for a stale session lock and, if found, recovers it.
        /// Also enqueues any already-completed sessions that were never uploaded.
        /// Never throws — all exceptions are caught and logged as warnings.
        /// </summary>
        public static void RecoverIfNeeded(UploadQueue uploadQueue)
        {
            try
            {
                // Step 1: check for stale lock
                if (!File.Exists(PlayScopeDirectory.SessionLock))
                {
                    // No stale lock — still pick up any orphaned completed_sessions
                    EnqueueCompletedSessions(uploadQueue);
                    return;
                }

                // Step 3a: read session_id from stale session.json
                var sessionId = ReadStaleSesionId();

                // Step 3b-e: move chunks, write synthetic event if needed
                ProcessStaleLockSession(sessionId, uploadQueue);

                // Step 4: pick up any other old completed sessions
                EnqueueCompletedSessions(uploadQueue, excludeSessionId: sessionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery encountered an unexpected error: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Reads the session_id out of the stale session.json.
        /// Returns null if the file is missing or unreadable.
        /// </summary>
        private static string ReadStaleSesionId()
        {
            var path = PlayScopeDirectory.SessionJson;
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                var dto = SimpleJson.Deserialize(json);
                if (dto != null && dto.TryGetValue("session_id", out var id) && id is string idStr)
                    return idStr;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: failed to read session.json: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Handles the stale locked session:
        /// - If session_end is present in chunks → treat as normal end edge-case.
        /// - Otherwise → abnormal end; append synthetic session_abnormal_end line.
        /// Then moves all chunks to completed_sessions/{sessionId}/ and enqueues them.
        /// </summary>
        private static void ProcessStaleLockSession(string sessionId, UploadQueue queue)
        {
            var chunksDir = PlayScopeDirectory.Chunks;
            var currentChunk = PlayScopeDirectory.CurrentChunkPath;

            // Use a placeholder folder name when session_id could not be read
            var folderName = !string.IsNullOrEmpty(sessionId) ? sessionId : $"recovered_{DateTime.UtcNow:yyyyMMddHHmmss}";
            var destDir = Path.Combine(PlayScopeDirectory.CompletedSessions, folderName);

            try
            {
                Directory.CreateDirectory(destDir);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: could not create destination dir '{destDir}': {ex.Message}");
                // Still try to delete the lock so we don't loop forever
                TryDeleteLock();
                return;
            }

            // Step 3b: scan completed (non-current) chunks for session_end
            bool sessionEndFound = ScanChunksForSessionEnd(chunksDir);

            if (!sessionEndFound)
            {
                // Step 3d: abnormal end — append synthetic event to chunk_current.jsonl
                try
                {
                    var heartbeat = ReadLastHeartbeat(PlayScopeDirectory.SessionHb);
                    var timestamp = heartbeat ?? DateTime.UtcNow.ToString("o");
                    var safeSessionId = sessionId ?? "";
                    var syntheticLine =
                        $"{{\"record_type\":\"event\",\"event_type\":\"session_abnormal_end\"," +
                        $"\"timestamp\":\"{timestamp}\",\"session_id\":\"{safeSessionId}\"}}\n";

                    File.AppendAllText(currentChunk, syntheticLine, new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PlayScope] SessionRecovery: failed to append synthetic event: {ex.Message}");
                }
            }

            // Step 3c/3d (cont.): move ALL .jsonl files (including chunk_current) to destDir
            MoveAllChunksToDir(chunksDir, currentChunk, destDir);

            // Step 3e: enqueue all .jsonl files in destDir
            EnqueueJsonlFilesInDir(destDir, queue);

            // Delete stale lock
            TryDeleteLock();

            Debug.Log($"[PlayScope] SessionRecovery: recovered stale session '{folderName}' " +
                      $"(sessionEndFound={sessionEndFound}).");
        }

        /// <summary>
        /// Moves all .jsonl files in chunksDir (both named chunks and chunk_current.jsonl)
        /// to destDir. Files that already exist in destDir get a numeric suffix to avoid collisions.
        /// </summary>
        private static void MoveAllChunksToDir(string chunksDir, string currentChunk, string destDir)
        {
            if (!Directory.Exists(chunksDir)) return;

            // Move chunk_current first (if it exists and has content)
            if (File.Exists(currentChunk))
            {
                var destName = Path.Combine(destDir, "chunk_current.jsonl");
                TryMoveFile(currentChunk, destName);
            }

            // Move all other .jsonl files
            try
            {
                var files = Directory.GetFiles(chunksDir, "*.jsonl");
                foreach (var file in files)
                {
                    // chunk_current was already handled above
                    if (string.Equals(file, currentChunk, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var destName = Path.Combine(destDir, Path.GetFileName(file));
                    TryMoveFile(file, destName);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error listing chunk files: {ex.Message}");
            }
        }

        private static void TryMoveFile(string src, string dest)
        {
            try
            {
                if (!File.Exists(src)) return;

                // Resolve name collision with a numeric suffix
                if (File.Exists(dest))
                {
                    var dir = Path.GetDirectoryName(dest);
                    var nameNoExt = Path.GetFileNameWithoutExtension(dest);
                    var ext = Path.GetExtension(dest);
                    int n = 1;
                    while (File.Exists(dest))
                    {
                        dest = Path.Combine(dir, $"{nameNoExt}_{n}{ext}");
                        n++;
                    }
                }

                File.Move(src, dest);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: failed to move '{src}' → '{dest}': {ex.Message}");
            }
        }

        /// <summary>
        /// Enqueues all .jsonl files found in already-completed session directories,
        /// optionally skipping the session that was just processed (it was already enqueued).
        /// </summary>
        private static void EnqueueCompletedSessions(UploadQueue queue, string excludeSessionId = null)
        {
            var completedDir = PlayScopeDirectory.CompletedSessions;
            if (!Directory.Exists(completedDir)) return;

            try
            {
                var sessionDirs = Directory.GetDirectories(completedDir);
                foreach (var dir in sessionDirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(excludeSessionId) &&
                        string.Equals(dirName, excludeSessionId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    EnqueueJsonlFilesInDir(dir, queue);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error scanning completed_sessions: {ex.Message}");
            }
        }

        /// <summary>
        /// Enqueues all .jsonl files in a single directory into the upload queue.
        /// </summary>
        private static void EnqueueJsonlFilesInDir(string dir, UploadQueue queue)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                var files = Directory.GetFiles(dir, "*.jsonl");
                foreach (var file in files)
                    queue.Enqueue(file);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error enqueuing files in '{dir}': {ex.Message}");
            }
        }

        /// <summary>
        /// Scans all finalized .jsonl files in chunksDir (excludes chunk_current.jsonl)
        /// for any line that contains "session_end" in the event_type field.
        /// Skips truncated last lines (lines with no trailing newline).
        /// </summary>
        private static bool ScanChunksForSessionEnd(string chunksDir)
        {
            if (!Directory.Exists(chunksDir)) return false;

            string[] files;
            try
            {
                files = Directory.GetFiles(chunksDir, "*.jsonl");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error listing chunks dir: {ex.Message}");
                return false;
            }

            var currentChunk = PlayScopeDirectory.CurrentChunkPath;

            foreach (var file in files)
            {
                // Skip chunk_current — we only scan completed (renamed) chunks
                if (string.Equals(file, currentChunk, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ScanFileForSessionEnd(file))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if any complete (newline-terminated) line in the file contains
        /// the string "session_end" as an event_type value.
        /// </summary>
        private static bool ScanFileForSessionEnd(string filePath)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                if (string.IsNullOrEmpty(text)) return false;

                // Split on newlines; the last element is empty or truncated — skip it
                var lines = text.Split('\n');
                // lines.Length - 1 because the last entry after the final '\n' is empty,
                // or if no trailing newline, it's a partial line — either way we skip it.
                var safeCount = lines.Length - 1;
                for (int i = 0; i < safeCount; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    // Quick substring check before paying for JSON parse
                    if (!line.Contains("session_end")) continue;

                    // Confirm it's actually in the event_type field
                    var dto = SimpleJson.Deserialize(line);
                    if (dto != null &&
                        dto.TryGetValue("event_type", out var et) &&
                        et is string etStr &&
                        etStr == "session_end")
                        return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error reading '{filePath}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Reads the last_heartbeat_at timestamp from session.hb.
        /// The file is written as a bare ISO-8601 string by HeartbeatWorker/SessionFiles.
        /// Returns null if the file is missing or unreadable.
        /// </summary>
        private static string ReadLastHeartbeat(string hbPath)
        {
            if (!File.Exists(hbPath)) return null;
            try
            {
                var text = File.ReadAllText(hbPath).Trim();
                if (string.IsNullOrEmpty(text)) return null;

                // The file may be a bare timestamp OR a JSON object — handle both.
                if (text.StartsWith("{"))
                {
                    var dto = SimpleJson.Deserialize(text);
                    if (dto != null &&
                        dto.TryGetValue("last_heartbeat_at", out var hb) &&
                        hb is string hbStr)
                        return hbStr;
                }
                else
                {
                    // Validate it looks like a timestamp
                    if (DateTime.TryParse(text, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out _))
                        return text;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: failed to read session.hb: {ex.Message}");
            }
            return null;
        }

        private static void TryDeleteLock()
        {
            try
            {
                var lockPath = PlayScopeDirectory.SessionLock;
                if (File.Exists(lockPath))
                    File.Delete(lockPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: failed to delete session.lock: {ex.Message}");
            }
        }
    }
}
