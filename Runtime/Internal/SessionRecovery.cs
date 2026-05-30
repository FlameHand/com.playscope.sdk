using System;
using System.Collections.Generic;
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
                // Leftover chunks must be relocated to completed_sessions/{priorId}/
                // BEFORE the new session takes over chunksDir — else the uploader
                // ships them under the NEW envelope and commingles two sessions
                // under one backend session_id.
                bool hasOrphans = ChunksDirHasOrphans();

                if (!hasStaleLock && !hasOrphans)
                {
                    // Clean state — pick up any orphaned completed_sessions.
                    EnqueueCompletedSessions(uploadQueue);
                    // A native crash can still exist here (process died AFTER
                    // session_end was written) — treat as an orphan.
                    EmitOrphanNativeCrashes(uploadQueue);
                    return;
                }

                PlayScopeLog.Info(
                    $"SessionRecovery: starting (hasStaleLock={hasStaleLock}, hasOrphans={hasOrphans}).");

                // session.json still holds the PRIOR session's identity (new one not generated yet).
                var sessionId = ReadStaleSesionId();
                // Fallback for a corrupt session.json (power-kill mid-write): scan
                // the orphan chunks' session_start for the id. Without it the folder
                // becomes "recovered_TIMESTAMP", the manifest is broken, and the
                // uploader dead-letters every chunk — total data loss for the crash.
                if (string.IsNullOrEmpty(sessionId))
                {
                    sessionId = TryScanChunksForSessionId();
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        PlayScopeLog.Warning(
                            $"SessionRecovery: session.json was unreadable; recovered session_id={sessionId} " +
                            "from chunk metadata. Will reconstruct manifest before upload.");
                    }
                }

                var recoveredFolder = !string.IsNullOrEmpty(sessionId)
                    ? sessionId
                    : $"recovered_{DateTime.UtcNow:yyyyMMddHHmmss}";

                // Always synthesize an abnormal-end when no real session_end is on
                // disk (inner check gates on that). Covers the "unlocked but
                // session_end never written" case that otherwise leaves a session
                // with EndedAt=NULL forever, distorting Crash-Free Sessions.
                ProcessStaleLockSession(sessionId, recoveredFolder, uploadQueue,
                    appendSyntheticAbnormalEnd: true);

                EnqueueCompletedSessions(uploadQueue, excludeSessionId: recoveredFolder);

                // Crash files whose session_id didn't match the recovered session
                // (SDK rotated between crashes, or session_start hadn't flushed) —
                // drain into per-session folders so they upload under the right envelope.
                EmitOrphanNativeCrashes(uploadQueue);
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
                // Strip a surviving UTF-8 BOM — if a tool re-saved with
                // UTF8Encoding(true) it can survive the read and break SimpleJson.
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
        /// Fallback path when session.json is missing or unreadable. Scans
        /// orphan chunk_*.jsonl files for the first record with
        /// <c>event_type == "session_start"</c> and extracts <c>session_id</c>
        /// from its metadata. Returns null when nothing is found — caller
        /// will then dead-letter the orphans with a synthetic folder name.
        /// </summary>
        private static string TryScanChunksForSessionId()
        {
            var chunksDir = PlayScopeDirectory.Chunks;
            if (!Directory.Exists(chunksDir)) return null;
            try
            {
                foreach (var file in Directory.GetFiles(chunksDir, "*.jsonl"))
                {
                    string found = TryScanFileForSessionId(file);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"SessionRecovery: chunk scan for session_id failed: {ex.Message}");
            }
            return null;
        }

        private static string TryScanFileForSessionId(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
                string line;
                int scanned = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    // session_start is the first record; past ~50 it's a mid-session continuation chunk.
                    if (++scanned > 50) break;
                    if (string.IsNullOrEmpty(line) || !line.Contains("session_start")) continue;
                    var dto = SimpleJson.Deserialize(line);
                    if (dto is null) continue;
                    if (!dto.TryGetValue("event_type", out var et) || (et as string) != "session_start") continue;
                    // session_id can live at the top level OR inside metadata
                    // depending on how the SDK serialized the record.
                    if (dto.TryGetValue("session_id", out var sid) && sid is string s1 && !string.IsNullOrEmpty(s1))
                        return s1;
                    if (dto.TryGetValue("metadata", out var meta) && meta is Dictionary<string, object> md &&
                        md.TryGetValue("session_id", out var sid2) && sid2 is string s2 && !string.IsNullOrEmpty(s2))
                        return s2;
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"SessionRecovery: TryScanFileForSessionId({filePath}) failed: {ex.Message}");
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

            // Consume the native crash for THIS session up-front (deletes the file)
            // so the orphan-drain path can't re-emit it. Promotes reason below.
            NativeCrashRecord nativeCrash = null;
            try
            {
                nativeCrash = PlayScopeCrashCollector.TryConsumeCrashFor(sessionId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[PlayScope] SessionRecovery: TryConsumeCrashFor({sessionId}) threw — " +
                    $"continuing without native crash data: {ex.Message}");
                nativeCrash = null;
            }

            if (!sessionEndFound && appendSyntheticAbnormalEnd)
            {
                // Synthetic abnormal-end. The reason (from the lifecycle file)
                // drives the backend's corrected CFS%:
                //   foreground_crash → crashed (real crash / ANR / native kill)
                //   background_kill  → NOT crashed (swipe-kill / OS low-mem)
                //   unknown          → crashed (pessimistic, file missing)
                try
                {
                    // End ts = max chunk timestamp + 10 ms ≈ when the SDK died;
                    // UtcNow only if chunks are unreadable (rare — session_start is direct-written).
                    var maxOnDisk = ScanChunksForMaxTimestamp(chunksDir);
                    var endTs = maxOnDisk.HasValue
                        ? maxOnDisk.Value.AddMilliseconds(10)
                        : DateTime.UtcNow;
                    if (endTs.Kind != DateTimeKind.Utc) endTs = endTs.ToUniversalTime();
                    var timestamp = endTs.ToString("o");
                    var safeSessionId = sessionId ?? "";

                    var (lifecycleState, _, intent) = Core.Session.SessionFiles.TryReadLifecycleState();
                    // Priority: native crash (OS delivered a fatal signal) >
                    // intent user_close (native hook saw an explicit close) > heuristic.
                    string reason;
                    if (nativeCrash != null)
                    {
                        reason = "native_crash";
                    }
                    else if (intent && lifecycleState == "user_close")
                    {
                        reason = "user_close";
                    }
                    else
                    {
                        reason = lifecycleState switch
                        {
                            "foreground" => "foreground_crash",
                            "background" => "background_kill",
                            _ => "unknown",
                        };
                    }

                    // swipe-kill / OS background eviction are NORMAL mobile session
                    // ends — classifying them abnormal floods the dashboard red and
                    // tanks CFS%. Only foreground death + the unknown fallback are
                    // abnormal. event_type drives backend EndStatus; end_status is
                    // also stamped into metadata for raw-line consumers.
                    var isNormalEnd = reason is "user_close" or "background_kill";
                    var eventType = isNormalEnd ? "session_end" : "session_abnormal_end";
                    var endStatus = isNormalEnd ? "normal" : "abnormal";
                    var crashFilePresent = nativeCrash != null;

                    // sequence_num MUST sort strictly after every real event
                    // the session emitted; without an explicit value the JSON
                    // omits the field, the backend DTO defaults to 0, and the
                    // synthetic session_end ties with the real session_start
                    // (also sequence_num=0). The ClickHouse timeline view
                    // orders by sequence_num ASC and the tie placed the
                    // synthetic FIRST, so the dashboard rendered session_end
                    // before session_start. Backend caps sequence_num at
                    // int.MaxValue (rejects anything larger), and the per-
                    // session event cap is 10 000 — int.MaxValue - 1 sits
                    // comfortably above any real event but inside the
                    // accepted range. Mirrors the AbandonedSessionWorker
                    // strategy of using a high baseSeq for synthetic rows.
                    const long SyntheticSequenceNum = int.MaxValue - 1;
                    // Must set event_id — an empty one makes every synthetic event
                    // collide in dashboard queries that dedup on it.
                    var eventId = Guid.NewGuid().ToString("N");

                    var syntheticLine =
                        $"{{\"record_type\":\"event\",\"event_type\":\"{eventType}\"," +
                        $"\"event_id\":\"{eventId}\",\"sequence_num\":{SyntheticSequenceNum}," +
                        $"\"timestamp\":\"{timestamp}\",\"session_id\":\"{safeSessionId}\"," +
                        $"\"metadata\":{{\"end_status\":\"{endStatus}\",\"reason\":\"{reason}\",\"last_lifecycle_state\":\"{lifecycleState ?? "unknown"}\",\"intent\":{(intent ? "true" : "false")},\"crash_file_present\":{(crashFilePresent ? "true" : "false")},\"synthesized_by\":\"session_recovery\"}}}}\n";

                    // Emitted BEFORE the synthetic session_end (sequence just below
                    // it) so the timeline reads "crash → session_end".
                    string nativeCrashLogLine = null;
                    if (nativeCrash != null)
                    {
                        nativeCrashLogLine = BuildNativeCrashLogLine(
                            nativeCrash,
                            sessionIdForLine: safeSessionId,
                            sequenceNum: SyntheticSequenceNum - 1,
                            fallbackTimestampIso: timestamp);
                    }

                    // If the prior process died mid-write, chunk_current may not end
                    // in '\n'; appending directly would corrupt the last JSONL line
                    // and swallow our synthetic event. Prepend '\n' if needed.
                    bool needsLeadingNewline = false;
                    try
                    {
                        if (File.Exists(currentChunk))
                        {
                            using var fs = new FileStream(currentChunk, FileMode.Open, FileAccess.Read);
                            if (fs.Length > 0)
                            {
                                fs.Seek(-1, SeekOrigin.End);
                                int last = fs.ReadByte();
                                needsLeadingNewline = last != '\n';
                            }
                        }
                    }
                    catch { /* best-effort; default to no prepend */ }

                    var prefix = needsLeadingNewline ? "\n" : "";
                    var combinedLines = nativeCrashLogLine != null
                        ? nativeCrashLogLine + syntheticLine
                        : syntheticLine;
                    var toWrite = prefix + combinedLines;
                    File.AppendAllText(currentChunk, toWrite, new System.Text.UTF8Encoding(false));
                    Debug.Log($"[PlayScope] SessionRecovery: classified previous session as {endStatus}/{reason} (last lifecycle state: {lifecycleState ?? "unknown"}, intent: {intent}, native_crash: {crashFilePresent}).");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PlayScope] SessionRecovery: failed to append synthetic event: {ex.Message}");
                }

                // Tidy up the stale lifecycle file regardless of outcome —
                // the new session about to start will write its own.
                try { Core.Session.SessionFiles.DeleteLifecycleState(); } catch { /* best-effort */ }
            }
            else if (nativeCrash != null)
            {
                // session_end already on disk but a crash file too (OS killed us
                // during teardown after a clean close). Still emit the exception line.
                try
                {
                    var safeSessionId = sessionId ?? "";
                    var fallbackTs = nativeCrash.CapturedAtUnixMs > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(nativeCrash.CapturedAtUnixMs).UtcDateTime
                            .ToString("o")
                        : DateTime.UtcNow.ToString("o");
                    const long PostShutdownCrashSeq = int.MaxValue - 1;
                    var line = BuildNativeCrashLogLine(
                        nativeCrash,
                        sessionIdForLine: safeSessionId,
                        sequenceNum: PostShutdownCrashSeq,
                        fallbackTimestampIso: fallbackTs);
                    bool needsLeadingNewline = false;
                    if (File.Exists(currentChunk))
                    {
                        try
                        {
                            using var fs = new FileStream(currentChunk, FileMode.Open, FileAccess.Read);
                            if (fs.Length > 0)
                            {
                                fs.Seek(-1, SeekOrigin.End);
                                needsLeadingNewline = fs.ReadByte() != '\n';
                            }
                        }
                        catch { /* best-effort */ }
                    }
                    File.AppendAllText(currentChunk, (needsLeadingNewline ? "\n" : "") + line,
                        new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[PlayScope] SessionRecovery: failed to append native crash log (post-shutdown path): {ex.Message}");
                }
            }

            // Step 3c/3d (cont.): move ALL .jsonl files (including chunk_current) to destDir
            MoveAllChunksToDir(chunksDir, currentChunk, destDir, sessionId);

            // Snapshot session.json into destDir as a manifest so the uploader
            // preserves the ORIGINAL session_id / sdk_version / schema_version —
            // without it, recovered chunks attribute to the NEW session.
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

            // Rename chunk_current to chunk_{shortId}_recovered.jsonl so the batch_id
            // is UNIQUE per recovery. The default "chunk_current" collides across
            // recoveries → backend (project_id, batch_id) idempotency drops the
            // second, silently losing its synthetic session_abnormal_end (sessions
            // stuck EndStatus=unknown). The shortId prefix matches the ownership scheme.
            if (File.Exists(currentChunk))
            {
                // Skip an empty chunk_current — else we'd upload a 0-byte batch.
                bool isEmpty = false;
                try { isEmpty = new FileInfo(currentChunk).Length == 0; }
                catch { /* size unknown — fall through and try to move */ }

                if (isEmpty)
                {
                    try { File.Delete(currentChunk); } catch { /* best-effort */ }
                }
                else
                {
                    var shortId = ExtractShortId(sessionId);
                    var renamedName = !string.IsNullOrEmpty(shortId)
                        ? $"chunk_{shortId}_recovered.jsonl"
                        : "chunk_current_recovered.jsonl";
                    var destName = Path.Combine(destDir, renamedName);
                    TryMoveFile(currentChunk, destName);
                }
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
        /// First N hex chars of the session id (dashes stripped), mirroring
        /// SessionInfo.SessionShortId. Used for unique recovered-chunk filenames.
        /// </summary>
        private static string ExtractShortId(string sessionId)
        {
            // MUST match SessionInfo.ShortIdLength — a different prefix here than
            // chunks were written under breaks the orphan-rescue ownership check.
            const int ShortIdLength = 8;
            if (string.IsNullOrEmpty(sessionId)) return "";
            var stripped = sessionId.Replace("-", "");
            return stripped.Length >= ShortIdLength ? stripped.Substring(0, ShortIdLength) : stripped;
        }

        /// <summary>
        /// Copies live session.json into the completed-session folder so the
        /// uploader reads the ORIGINAL session_id / sdk_version / schema_version.
        /// Best-effort — on failure the uploader falls back to the current session.
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
        /// Enqueues all .jsonl files in a directory. Skips chunks with a sibling
        /// <c>.uploaded</c> marker (sent OK but the follow-up Delete failed —
        /// Windows lock / AV) and retries the deletion, breaking the re-upload
        /// loop that otherwise grows disk + the "rescued_rescued" log chain.
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
                        // Retry the deletion the upload couldn't do; if still locked, try next launch.
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
            // Stream line-by-line, not File.ReadAllText: the read-then-split
            // allocation OOM'd on a low-memory post-crash device, and the catch
            // returned false → a real session_end was misreported as a crash.
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    // Substring prefilter; the JSON parse below disambiguates from "session_abnormal_end".
                    if (!line.Contains("session_end")) continue;

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
        /// Returns the latest <c>"timestamp":"…"</c> value found across every
        /// .jsonl in <paramref name="chunksDir"/>, including chunk_current.
        /// Used by the synthetic session_end so EndedAt approximates the
        /// moment the SDK actually died, not the moment recovery ran on the
        /// next launch. Streaming line-by-line; substring extraction without
        /// full JSON parse — cheap.
        /// </summary>
        private static DateTime? ScanChunksForMaxTimestamp(string chunksDir)
        {
            if (!Directory.Exists(chunksDir)) return null;
            DateTime? max = null;
            string[] files;
            try { files = Directory.GetFiles(chunksDir, "*.jsonl"); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error listing chunks for ts scan: {ex.Message}");
                return null;
            }
            foreach (var file in files)
            {
                var fileMax = ScanFileForMaxTimestamp(file);
                if (fileMax.HasValue && (!max.HasValue || fileMax.Value > max.Value))
                    max = fileMax;
            }
            return max;
        }

        private static DateTime? ScanFileForMaxTimestamp(string filePath)
        {
            const string TIMESTAMP_MARKER = "\"timestamp\":\"";
            DateTime? max = null;
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    int idx = line.IndexOf(TIMESTAMP_MARKER, StringComparison.Ordinal);
                    if (idx < 0) continue;
                    int valueStart = idx + TIMESTAMP_MARKER.Length;
                    int valueEnd = line.IndexOf('"', valueStart);
                    if (valueEnd <= valueStart) continue;
                    var tsStr = line.Substring(valueStart, valueEnd - valueStart);
                    if (DateTime.TryParse(tsStr, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
                    {
                        if (!max.HasValue || ts > max.Value) max = ts;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: error reading timestamps from '{filePath}': {ex.Message}");
            }
            return max;
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

        /// <summary>
        /// Builds a JSONL log line for one native crash record. Shape
        /// mirrors <see cref="WriterWorker.SerializeLog"/> with the
        /// addition of a top-level <c>session_id</c> field so the line is
        /// self-describing when it rides under a different session's
        /// envelope (orphan path).
        /// </summary>
        private static string BuildNativeCrashLogLine(
            NativeCrashRecord record, string sessionIdForLine, long sequenceNum, string fallbackTimestampIso)
        {
            var sb = new System.Text.StringBuilder(512);
            sb.Append("{\"record_type\":\"log\"");
            sb.Append(",\"event_id\":\"").Append(Guid.NewGuid().ToString("N")).Append('"');
            sb.Append(",\"sequence_num\":").Append(sequenceNum);
            // Use the record's captured_at_unix_ms (the crash happened then),
            // falling back to the caller timestamp if the C++ handler couldn't read the clock.
            string crashIsoTs;
            if (record.CapturedAtUnixMs > 0)
            {
                crashIsoTs = DateTimeOffset.FromUnixTimeMilliseconds(record.CapturedAtUnixMs)
                    .UtcDateTime
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            else
            {
                crashIsoTs = fallbackTimestampIso ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            sb.Append(",\"timestamp\":");
            EventPipeline.AppendEscapedString(sb, crashIsoTs);
            sb.Append(",\"session_id\":");
            EventPipeline.AppendEscapedString(sb, sessionIdForLine ?? string.Empty);
            sb.Append(",\"level\":\"exception\"");
            sb.Append(",\"message\":");
            EventPipeline.AppendEscapedString(sb, PlayScopeCrashCollector.BuildMessage(record));
            var stack = PlayScopeCrashCollector.BuildStackTrace(record);
            if (!string.IsNullOrEmpty(stack))
            {
                sb.Append(",\"stack_trace\":");
                EventPipeline.AppendEscapedString(sb, stack);
            }
            sb.Append(",\"metadata\":")
                .Append(PlayScopeCrashCollector.BuildExceptionMetadataJson(record));
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Drains every crash file the collector still holds (i.e. that
        /// did NOT match a recovered session id) into per-session
        /// recovered folders. Each orphan gets its own
        /// <c>completed_sessions/{sessionId}/</c> directory with a
        /// minimal manifest so UploaderWorker.ResolveEnvelopeIdentity
        /// accepts it. Best-effort: failures are logged and the rest of
        /// recovery continues.
        /// </summary>
        private static void EmitOrphanNativeCrashes(UploadQueue queue)
        {
            IReadOnlyList<NativeCrashRecord> orphans;
            try
            {
                orphans = PlayScopeCrashCollector.DrainOrphans();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] SessionRecovery: DrainOrphans threw: {ex.Message}");
                return;
            }
            if (orphans == null || orphans.Count == 0) return;

            for (int i = 0; i < orphans.Count; i++)
            {
                var record = orphans[i];
                if (record == null || string.IsNullOrEmpty(record.SessionId))
                {
                    PlayScopeLog.Warning(
                        "SessionRecovery: orphan crash record missing session_id — discarding.");
                    continue;
                }
                try
                {
                    EmitOneOrphanCrash(record, queue);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[PlayScope] SessionRecovery: failed to emit orphan crash for session_id={record.SessionId}: {ex.Message}");
                }
            }
        }

        private static void EmitOneOrphanCrash(NativeCrashRecord record, UploadQueue queue)
        {
            var destDir = Path.Combine(PlayScopeDirectory.CompletedSessions, record.SessionId);
            Directory.CreateDirectory(destDir);

            // Don't overwrite an existing manifest (a real recovered session); same-id is fine to reuse.
            var manifestPath = Path.Combine(destDir, "session.json");
            if (!File.Exists(manifestPath))
            {
                var startedAtIso = record.CapturedAtUnixMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(record.CapturedAtUnixMs)
                        .UtcDateTime.ToString("o")
                    : DateTime.UtcNow.ToString("o");
                var shortId = record.SessionId.Replace("-", "");
                if (shortId.Length > 8) shortId = shortId.Substring(0, 8);
                var manifestJson = "{" +
                    "\"session_id\":\"" + record.SessionId + "\"," +
                    "\"session_short_id\":\"" + shortId + "\"," +
                    "\"started_at\":\"" + startedAtIso + "\"," +
                    "\"sdk_version\":\"" + PlayScopeRuntime.SdkVersion + "\"," +
                    "\"schema_version\":1" +
                    "}";
                File.WriteAllText(manifestPath, manifestJson, new System.Text.UTF8Encoding(false));
            }

            var chunkName = "chunk_native_crash_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl";
            var chunkPath = Path.Combine(destDir, chunkName);
            // High enough not to collide with a real event; mirrors the synthetic session_end convention.
            const long OrphanSeq = int.MaxValue - 1;
            var fallbackIso = record.CapturedAtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(record.CapturedAtUnixMs).UtcDateTime.ToString("o")
                : DateTime.UtcNow.ToString("o");
            var line = BuildNativeCrashLogLine(
                record, sessionIdForLine: record.SessionId, sequenceNum: OrphanSeq,
                fallbackTimestampIso: fallbackIso);
            File.WriteAllText(chunkPath, line, new System.Text.UTF8Encoding(false));

            queue.Enqueue(chunkPath);
            PlayScopeLog.Info($"SessionRecovery: emitted orphan native crash for session_id={record.SessionId} → {chunkName}");
        }
    }
}
