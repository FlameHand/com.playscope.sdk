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
                bool hasStaleLock = File.Exists(PlayScopeDirectory.SessionLock);
                // Orphan detection: any leftover chunk_*.jsonl in chunksDir (or a non-empty
                // chunk_current.jsonl) means the prior session left data behind. We must
                // relocate it to completed_sessions/{priorSessionId}/ BEFORE the new session
                // takes over chunksDir, otherwise the uploader would upload those chunks
                // under the NEW session's envelope and commingle two sessions' events
                // under one backend session_id (regression seen 2026-05-19).
                bool hasOrphans = ChunksDirHasOrphans();

                if (!hasStaleLock && !hasOrphans)
                {
                    // Clean state — just pick up any orphaned completed_sessions.
                    // No log here unless something to enqueue; otherwise every
                    // startup is noisy.
                    EnqueueCompletedSessions(uploadQueue);
                    return;
                }

                PlayScopeLog.Info(
                    $"SessionRecovery: starting (hasStaleLock={hasStaleLock}, hasOrphans={hasOrphans}).");

                // Step 3a: read session_id from the on-disk session.json (still holds the
                // PRIOR session's identity — the new session hasn't been generated yet).
                var sessionId = ReadStaleSesionId();

                var recoveredFolder = !string.IsNullOrEmpty(sessionId)
                    ? sessionId
                    : $"recovered_{DateTime.UtcNow:yyyyMMddHHmmss}";

                // Synthetic abnormal-end is appended whenever the chunks scan
                // does NOT find a real session_end record (the inner check in
                // ProcessStaleLockSession gates on that). Previously we only
                // appended it when hasStaleLock=true, which left the "lock
                // removed but session_end never written" case (e.g. a clean
                // teardown that crashed AFTER unlock but BEFORE writing
                // session_end) producing a session that lives forever with
                // EndedAt=NULL on the dashboard — exactly the Crash-Free
                // Sessions distortion the abandoned-session sweep on the
                // backend now compensates for. We close it at the source
                // here too: if no real session_end is on disk, we ALWAYS
                // emit a synthetic one, classified by the lifecycle file.
                ProcessStaleLockSession(sessionId, recoveredFolder, uploadQueue,
                    appendSyntheticAbnormalEnd: true);

                EnqueueCompletedSessions(uploadQueue, excludeSessionId: recoveredFolder);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery encountered an unexpected error: {ex.Message}");
            }
        }

        /// <summary>
        /// True if chunksDir contains any finalized chunk_*.jsonl (an orphan from a prior
        /// session that didn't upload before shutdown) or a non-empty chunk_current.jsonl.
        /// </summary>
        private static bool ChunksDirHasOrphans()
        {
            var chunksDir = PlayScopeDirectory.Chunks;
            if (!Directory.Exists(chunksDir)) return false;

            try
            {
                foreach (var file in Directory.GetFiles(chunksDir, "*.jsonl"))
                {
                    var name = Path.GetFileName(file);
                    if (string.Equals(name, "chunk_current.jsonl", StringComparison.OrdinalIgnoreCase))
                    {
                        // Non-empty current chunk also counts — could happen if WriterWorker
                        // didn't get to finalize before the prior process exited.
                        try
                        {
                            if (new FileInfo(file).Length > 0) return true;
                        }
                        catch { /* fall through */ }
                        continue;
                    }
                    // Any finalized chunk is an orphan from a previous session by definition.
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error checking for orphans: {ex.Message}");
            }
            return false;
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
                // Defensive UTF-8 BOM strip. File.ReadAllText with the default
                // UTF-8 encoding usually swallows the BOM, but if the file got
                // re-saved by a tool that explicitly used UTF8Encoding(true) on
                // some platforms the BOM can survive the read and break our
                // tolerant-but-not-permissive SimpleJson parser. We compare
                // against the explicit ﻿ escape instead of a literal BOM
                // char so the source stays readable in editors that hide it.
                if (json.Length > 0 && json[0] == '﻿') json = json.Substring(1);
                var dto = SimpleJson.Deserialize(json);
                if (dto != null && dto.TryGetValue("session_id", out var id) && id is string idStr)
                    return idStr;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"SessionRecovery: failed to read session.json: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Handles the stale locked session:
        /// - If session_end is present in chunks → treat as normal end edge-case.
        /// - Otherwise → abnormal end; append synthetic session_abnormal_end line.
        /// Then moves all chunks to completed_sessions/{sessionId}/ and enqueues them.
        /// </summary>
        private static void ProcessStaleLockSession(string sessionId, string folderName, UploadQueue queue,
            bool appendSyntheticAbnormalEnd = true)
        {
            var chunksDir = PlayScopeDirectory.Chunks;
            var currentChunk = PlayScopeDirectory.CurrentChunkPath;

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

            if (!sessionEndFound && appendSyntheticAbnormalEnd)
            {
                // Step 3d: abnormal end — append synthetic event to chunk_current.jsonl
                //
                // Classify *why* the previous session died by reading the
                // persisted lifecycle state file. The categories drive the
                // backend's corrected CFS% formula:
                //   foreground_crash → counts as crashed (real crash / ANR / native kill in foreground)
                //   background_kill  → does NOT count as crashed (user swipe-kill, OS low-memory in background — not the app's fault)
                //   unknown          → counts as crashed (legacy / lifecycle file missing — pessimistic so we don't hide real crashes)
                try
                {
                    var heartbeat = ReadLastHeartbeat(PlayScopeDirectory.SessionHb);
                    var timestamp = heartbeat ?? DateTime.UtcNow.ToString("o");
                    var safeSessionId = sessionId ?? "";

                    var (lifecycleState, _, intent) = Core.Session.SessionFiles.TryReadLifecycleState();
                    // intent=true means the native (Java / iOS) lifecycle
                    // hook explicitly observed a user-initiated close —
                    // wins over any state-based heuristic. This is the
                    // unambiguous "user swiped from recents" signal.
                    var reason = intent && lifecycleState == "user_close"
                        ? "user_close"
                        : lifecycleState switch
                          {
                              "foreground" => "foreground_crash",
                              "background" => "background_kill",
                              _ => "unknown",
                          };

                    var syntheticLine =
                        $"{{\"record_type\":\"event\",\"event_type\":\"session_abnormal_end\"," +
                        $"\"timestamp\":\"{timestamp}\",\"session_id\":\"{safeSessionId}\"," +
                        $"\"metadata\":{{\"reason\":\"{reason}\",\"last_lifecycle_state\":\"{lifecycleState ?? "unknown"}\",\"intent\":{(intent ? "true" : "false")}}}}}\n";

                    File.AppendAllText(currentChunk, syntheticLine, new System.Text.UTF8Encoding(false));
                    Debug.Log($"[PlayScope] SessionRecovery: classified previous session as {reason} (last lifecycle state: {lifecycleState ?? "unknown"}, intent: {intent}).");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PlayScope] SessionRecovery: failed to append synthetic event: {ex.Message}");
                }

                // Tidy up the stale lifecycle file regardless of outcome —
                // the new session about to start will write its own.
                try { Core.Session.SessionFiles.DeleteLifecycleState(); } catch { /* best-effort */ }
            }

            // Step 3c/3d (cont.): move ALL .jsonl files (including chunk_current) to destDir
            MoveAllChunksToDir(chunksDir, currentChunk, destDir, sessionId);

            // Step 3d.1: snapshot session.json into destDir as a manifest. UploaderWorker reads
            // this when building the envelope for recovered chunks so the original session_id,
            // sdk_version, and schema_version are preserved (without it, recovered chunks would
            // be attributed to the NEW session that runs the upload).
            CopySessionManifest(destDir);

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
        private static void MoveAllChunksToDir(string chunksDir, string currentChunk, string destDir, string sessionId)
        {
            if (!Directory.Exists(chunksDir)) return;

            // Move chunk_current first (if it exists and has content).
            //
            // Rename it so the resulting batch_id (filename without
            // extension) is UNIQUE per recovered session. The default
            // name "chunk_current" collides across every recovery: when
            // two devices/launches each recover a prior session's data,
            // both batches upload with batch_id = "chunk_current" and
            // backend's (project_id, batch_id) idempotency drops the
            // second one as already_processed. The synthetic
            // session_abnormal_end inside the second chunk is then
            // silently lost — observed in production with all recovered
            // sessions stuck at EndStatus=unknown.
            //
            // New name: chunk_{shortId}_recovered.jsonl, where shortId
            // is the first 5 hex chars of the recovered session_id
            // (matching the existing chunk_{shortId}_NNNNNN naming).
            // This keeps the file aligned with the same prefix scheme
            // UploaderWorker uses for ownership checks.
            if (File.Exists(currentChunk))
            {
                var shortId = ExtractShortId(sessionId);
                var renamedName = !string.IsNullOrEmpty(shortId)
                    ? $"chunk_{shortId}_recovered.jsonl"
                    : "chunk_current_recovered.jsonl";
                var destName = Path.Combine(destDir, renamedName);
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

        /// <summary>
        /// First 5 hex chars of a UUID string after dash strip, mirroring
        /// the SessionInfo.SessionShortId convention. Used to derive
        /// unique chunk filenames on recovery. Returns empty string if
        /// the input is missing or too short.
        /// </summary>
        private static string ExtractShortId(string sessionId)
        {
            // MUST stay in lockstep with SessionInfo.ShortIdLength. Hard-coded
            // 5 here pre-dated the 5→8 bump in SessionInfo (collision risk on
            // long-lived devices) — leaving it at 5 would produce a different
            // prefix than the one chunks are written under, breaking the
            // orphan-rescue ownership check the moment the rescue path
            // exercises it.
            const int ShortIdLength = 8;
            if (string.IsNullOrEmpty(sessionId)) return "";
            var stripped = sessionId.Replace("-", "");
            return stripped.Length >= ShortIdLength ? stripped.Substring(0, ShortIdLength) : stripped;
        }

        /// <summary>
        /// Copies the live session.json into the completed-session folder as session.json so that
        /// at upload time we can read the ORIGINAL session_id, sdk_version, and schema_version
        /// for chunks that belong to the crashed session.
        /// Best-effort: failures are logged but never fatal — uploader falls back to current session.
        /// </summary>
        private static void CopySessionManifest(string destDir)
        {
            try
            {
                var src = PlayScopeDirectory.SessionJson;
                if (!File.Exists(src)) return;
                var dest = Path.Combine(destDir, "session.json");
                if (File.Exists(dest)) return; // never overwrite an existing manifest
                File.Copy(src, dest);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: failed to copy session manifest: {ex.Message}");
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
        ///
        /// <para>
        /// Skips chunks that already have a sibling <c>.uploaded</c> marker
        /// file, which means UploaderWorker successfully sent them but the
        /// follow-up File.Delete on Windows failed (Editor file lock,
        /// AV scanner, etc.). Re-enqueueing them would mean uploading the
        /// same data again on every Editor restart — backend is idempotent
        /// so it absorbs the duplicate, but local disk usage and the
        /// "rescued_rescued_rescued" chain in the log keep growing. Skipping
        /// here breaks the cycle and we also retry the deletion: most
        /// transient locks clear between Editor sessions.
        /// </para>
        /// </summary>
        private static void EnqueueJsonlFilesInDir(string dir, UploadQueue queue)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                var files = Directory.GetFiles(dir, "*.jsonl");
                int skipped = 0;
                foreach (var file in files)
                {
                    var markerPath = file + ".uploaded";
                    if (File.Exists(markerPath))
                    {
                        // Retry the deletion the original upload couldn't do.
                        // If it still fails (file is genuinely locked right
                        // now), leave both files and the marker — we'll try
                        // again next launch.
                        try { File.Delete(file); File.Delete(markerPath); skipped++; }
                        catch { /* still locked — try next launch */ }
                        continue;
                    }
                    queue.Enqueue(file);
                }
                if (skipped > 0)
                    PlayScopeLog.Info($"SessionRecovery: cleaned {skipped} already-uploaded chunks left over from a prior restart in '{Path.GetFileName(dir)}'.");
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
