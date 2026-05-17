using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    internal sealed class StorageQuotaManager
    {
        // ── Quota limits ──────────────────────────────────────────────────────────

        private const long DefaultCapBytes = 50L * 1024 * 1024;   // 50 MB
        private const long HardCapBytes    = 100L * 1024 * 1024;  // 100 MB

        // ── Cached storage size (issue #16) ───────────────────────────────────────
        // Avoid rescanning the whole tree on every flush. We track running total via
        // append/delete notifications and re-baseline every 60 seconds for drift.
        private static readonly object _sizeLock = new object();
        private static long _cachedTotalBytes = -1;            // -1 == uninitialised
        private static DateTime _lastFullScanUtc = DateTime.MinValue;
        private static readonly TimeSpan _fullScanInterval = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Called by WriterWorker each time bytes are appended to the live chunk.
        /// </summary>
        internal static void NotifyBytesAppended(long bytes)
        {
            if (bytes <= 0) return;
            lock (_sizeLock)
            {
                if (_cachedTotalBytes < 0) return; // not initialised yet — next scan picks it up
                _cachedTotalBytes += bytes;
            }
        }

        /// <summary>
        /// Called by WriterWorker when a chunk is finalized (renamed). The byte count
        /// is unchanged but the file count changes — included for future use.
        /// </summary>
        internal static void NotifyChunkFinalized(string finalizedPath)
        {
            // Currently a no-op for byte accounting (rename keeps the bytes), but kept as
            // a hook so callers don't need to change later.
            _ = finalizedPath;
        }

        /// <summary>
        /// Invalidate the cache (used by tests).
        /// </summary>
        internal static void InvalidateSizeCache()
        {
            lock (_sizeLock)
            {
                _cachedTotalBytes = -1;
                _lastFullScanUtc = DateTime.MinValue;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Call after each FinalizeChunk(). Scans PlayScope storage and deletes
        /// non-critical chunks if the default cap (50 MB) is exceeded.
        /// Never throws — all file operations are wrapped in try/catch.
        /// </summary>
        internal static void EnforceQuota()
        {
            try
            {
                long total = GetTotalStorageBytes();
                if (total <= DefaultCapBytes) return;

                ApplyDropPolicy(total);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayScope] StorageQuotaManager.EnforceQuota error: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns the total bytes used across all PlayScope storage directories.
        /// Cached and re-baselined every 60 seconds — avoids a full tree scan on every flush.
        /// </summary>
        internal static long GetTotalStorageBytes()
        {
            lock (_sizeLock)
            {
                var now = DateTime.UtcNow;
                if (_cachedTotalBytes < 0 || now - _lastFullScanUtc > _fullScanInterval)
                {
                    _cachedTotalBytes = ScanTotalStorageBytes();
                    _lastFullScanUtc = now;
                }
                return _cachedTotalBytes;
            }
        }

        private static long ScanTotalStorageBytes()
        {
            long total = 0;
            total += SumDirectory(PlayScopeDirectory.Chunks);
            total += SumDirectory(PlayScopeDirectory.UploadQueue);
            total += SumDirectory(PlayScopeDirectory.DeadLetter);
            // Also count root-level files (device.json, etc.)
            total += SumDirectory(PlayScopeDirectory.Root, topLevelOnly: true);
            total += SumDirectory(PlayScopeDirectory.CurrentSession, topLevelOnly: true);
            return total;
        }

        /// <summary>
        /// Returns true if the hard cap (100 MB) is exceeded.
        /// </summary>
        internal static bool IsHardCapExceeded()
        {
            try
            {
                return GetTotalStorageBytes() > HardCapBytes;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Trims buffered EventRecords by dropping lowest-priority records first
        /// until the buffer is reduced enough to stay within reasonable bounds.
        /// Returns the kept records and the count of dropped records.
        /// </summary>
        internal static (List<EventRecord> kept, int dropped) TrimBuffer(List<EventRecord> buffer)
        {
            if (buffer == null || buffer.Count == 0)
                return (buffer ?? new List<EventRecord>(), 0);

            // Drop priority order (lowest first):
            // 1. debug logs
            // 2. info logs
            // 3. warning logs  (emit LogWarning)
            // 4. metric samples
            // 5. error logs    (emit LogWarning)
            // 6. exception/lifecycle/session records — NEVER drop

            int dropped = 0;

            // Pass 1: debug logs
            dropped += DropFromBuffer(buffer, r =>
                r.RecordType == RecordType.Log && string.Equals(r.Level, "debug", StringComparison.OrdinalIgnoreCase));

            // Pass 2: info logs
            dropped += DropFromBuffer(buffer, r =>
                r.RecordType == RecordType.Log && string.Equals(r.Level, "info", StringComparison.OrdinalIgnoreCase));

            // Pass 3: warning logs — emit a UnityEngine warning when dropped
            int warningDropped = DropFromBuffer(buffer, r =>
                r.RecordType == RecordType.Log && string.Equals(r.Level, "warning", StringComparison.OrdinalIgnoreCase));
            if (warningDropped > 0)
                Debug.LogWarning($"[PlayScope] Hard storage cap exceeded — dropped {warningDropped} warning log(s) from in-memory buffer.");
            dropped += warningDropped;

            // Pass 4: metric samples
            dropped += DropFromBuffer(buffer, r => r.RecordType == RecordType.Metric);

            // Pass 5: error logs — emit a UnityEngine warning when dropped
            int errorDropped = DropFromBuffer(buffer, r =>
                r.RecordType == RecordType.Log && string.Equals(r.Level, "error", StringComparison.OrdinalIgnoreCase));
            if (errorDropped > 0)
                Debug.LogWarning($"[PlayScope] Hard storage cap exceeded — dropped {errorDropped} error log(s) from in-memory buffer.");
            dropped += errorDropped;

            // Pass 6: exception/lifecycle/session records are NEVER dropped (r.IsCritical == true or
            // record_type==event with critical event_type). Everything remaining stays.

            return (buffer, dropped);
        }

        // ── Drop policy ───────────────────────────────────────────────────────────

        private static void ApplyDropPolicy(long totalBytes)
        {
            long bytesToFree = totalBytes - DefaultCapBytes;
            int droppedChunkCount = 0;

            bytesToFree = DeleteUploadedChunksFromQueue(bytesToFree, ref droppedChunkCount);
            if (bytesToFree > 0)
            {
                bytesToFree = DeleteOldestChunksInChunksDir(bytesToFree, ref droppedChunkCount);
                if (bytesToFree > 0)
                    DeleteOldestChunksInUploadQueue(bytesToFree, ref droppedChunkCount);
            }

            if (droppedChunkCount > 0)
                WritePartialMarker(PlayScopeDirectory.Chunks, droppedChunkCount);
        }

        /// <summary>
        /// Writes a session_data_partial marker into a completed_sessions/{id}/ directory after
        /// non-critical chunks were dropped from that session. Used by recovery / future callers
        /// that delete from a completed (non-current) session. See issue #10.
        /// </summary>
        internal static void WritePartialMarkerForCompletedSession(string completedSessionDir, int droppedChunks)
        {
            WritePartialMarker(completedSessionDir, droppedChunks);
        }

        // ── Phase 1 ───────────────────────────────────────────────────────────────

        private static long DeleteUploadedChunksFromQueue(long bytesToFree, ref int droppedCount)
        {
            var dir = PlayScopeDirectory.UploadQueue;
            if (!Directory.Exists(dir)) return bytesToFree;

            // Collect uploaded chunk entries sorted oldest-first by last-write time
            var candidates = new List<(string chunkPath, string stateFile, DateTime lastWrite)>();
            try
            {
                foreach (var stateFile in Directory.GetFiles(dir, "*.state.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(stateFile, new UTF8Encoding(false));
                        var dict = SimpleJson.Deserialize(json);
                        if (dict == null) continue;
                        if (!dict.TryGetValue("is_uploaded", out var iu) || iu is not true) continue;

                        var chunkName = Path.GetFileName(stateFile);
                        // strip ".state.json"
                        chunkName = chunkName.Substring(0, chunkName.Length - ".state.json".Length);
                        var chunkPath = Path.Combine(PlayScopeDirectory.Chunks, chunkName);

                        if (!File.Exists(chunkPath) && !File.Exists(stateFile)) continue;

                        var lw = File.Exists(chunkPath)
                            ? File.GetLastWriteTimeUtc(chunkPath)
                            : File.GetLastWriteTimeUtc(stateFile);

                        candidates.Add((chunkPath, stateFile, lw));
                    }
                    catch { /* skip */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayScope] QuotaManager: error scanning upload queue for uploaded chunks: " + ex.Message);
                return bytesToFree;
            }

            candidates.Sort((a, b) => a.lastWrite.CompareTo(b.lastWrite));

            foreach (var (chunkPath, stateFile, _) in candidates)
            {
                if (bytesToFree <= 0) break;
                long freed = 0;
                freed += TryDeleteFileAndGetSize(chunkPath);
                freed += TryDeleteFileAndGetSize(stateFile);
                if (freed > 0)
                {
                    bytesToFree -= freed;
                    droppedCount++;
                }
            }

            return bytesToFree;
        }

        // ── Phase 2 ───────────────────────────────────────────────────────────────

        private static long DeleteOldestChunksInChunksDir(long bytesToFree, ref int droppedCount)
        {
            var dir = PlayScopeDirectory.Chunks;
            if (!Directory.Exists(dir)) return bytesToFree;

            var candidates = new List<(string path, DateTime lastWrite)>();
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
                {
                    // Never delete chunk_current
                    if (IsCurrentChunk(file)) continue;
                    // Never delete partial_marker files (they contain synthetic events, not session data)
                    if (Path.GetFileName(file).StartsWith("partial_marker_", StringComparison.OrdinalIgnoreCase)) continue;
                    // Skip critical chunks
                    if (IsChunkCritical(file)) continue;

                    candidates.Add((file, File.GetLastWriteTimeUtc(file)));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayScope] QuotaManager: error scanning chunks dir: " + ex.Message);
                return bytesToFree;
            }

            candidates.Sort((a, b) => a.lastWrite.CompareTo(b.lastWrite));

            foreach (var (file, _) in candidates)
            {
                if (bytesToFree <= 0) break;
                long freed = TryDeleteFileAndGetSize(file);
                if (freed > 0)
                {
                    bytesToFree -= freed;
                    droppedCount++;
                }
            }

            return bytesToFree;
        }

        // ── Phase 3 ───────────────────────────────────────────────────────────────

        private static void DeleteOldestChunksInUploadQueue(long bytesToFree, ref int droppedCount)
        {
            var dir = PlayScopeDirectory.UploadQueue;
            if (!Directory.Exists(dir)) return;

            // Gather retryable (is_uploaded: false) chunks, oldest-first
            var candidates = new List<(string chunkPath, string stateFile, DateTime lastWrite)>();
            try
            {
                foreach (var stateFile in Directory.GetFiles(dir, "*.state.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(stateFile, new UTF8Encoding(false));
                        var dict = SimpleJson.Deserialize(json);
                        if (dict == null) continue;
                        // Only retryable (is_uploaded == false)
                        if (dict.TryGetValue("is_uploaded", out var iu) && iu is true) continue;

                        var chunkName = Path.GetFileName(stateFile);
                        chunkName = chunkName.Substring(0, chunkName.Length - ".state.json".Length);
                        var chunkPath = Path.Combine(PlayScopeDirectory.Chunks, chunkName);

                        if (!File.Exists(chunkPath)) continue;
                        // Skip critical chunks
                        if (IsChunkCritical(chunkPath)) continue;

                        candidates.Add((chunkPath, stateFile, File.GetLastWriteTimeUtc(chunkPath)));
                    }
                    catch { /* skip */ }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayScope] QuotaManager: error scanning upload queue for retryable chunks: " + ex.Message);
                return;
            }

            candidates.Sort((a, b) => a.lastWrite.CompareTo(b.lastWrite));

            foreach (var (chunkPath, stateFile, _) in candidates)
            {
                if (bytesToFree <= 0) break;
                long freed = 0;
                freed += TryDeleteFileAndGetSize(chunkPath);
                freed += TryDeleteFileAndGetSize(stateFile);
                if (freed > 0)
                {
                    bytesToFree -= freed;
                    droppedCount++;
                }
            }
        }

        // ── Partial marker ────────────────────────────────────────────────────────

        private static void WritePartialMarker(string targetDir, int droppedChunks)
        {
            try
            {
                if (string.IsNullOrEmpty(targetDir)) targetDir = PlayScopeDirectory.Chunks;
                Directory.CreateDirectory(targetDir);

                var now = DateTime.UtcNow;
                var timestampMs = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                var iso = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                var json = "{\"record_type\":\"event\",\"event_type\":\"session_data_partial\",\"timestamp\":\"" + iso + "\",\"metadata\":{\"data_loss_reason\":\"local_quota_exceeded\",\"dropped_chunks\":" + droppedChunks + "}}";

                var fileName = $"partial_marker_{timestampMs}.jsonl";
                var path = Path.Combine(targetDir, fileName);
                File.WriteAllText(path, json + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayScope] QuotaManager: failed to write partial marker: " + ex.Message);
            }
        }

        // ── Chunk classification ──────────────────────────────────────────────────

        /// <summary>
        /// Scans all lines of a chunk file and returns true if any line is a critical record.
        /// Checking all lines (not just the first) catches chunks where session_end or an
        /// exception appears after an initial non-critical record.
        /// If the file cannot be read or parsed, returns false (treat as non-critical → deletable).
        /// </summary>
        private static bool IsChunkCritical(string chunkPath)
        {
            try
            {
                using var sr = new StreamReader(chunkPath, new UTF8Encoding(false));
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (LineIsCritical(line)) return true;
                }
                return false;
            }
            catch
            {
                return false; // corrupted → non-critical
            }
        }

        /// <summary>
        /// Classifies a single JSONL line as critical.
        /// Critical if:
        ///   record_type == "event" AND event_type in {session_start, session_end,
        ///     session_abnormal_end, exception}
        /// OR
        ///   record_type == "log" AND level in {"exception", "error"}
        /// </summary>
        private static bool LineIsCritical(string line)
        {
            var recType = ExtractField(line, "record_type");
            if (recType == null) return false;

            if (string.Equals(recType, "event", StringComparison.Ordinal))
            {
                var eventType = ExtractField(line, "event_type");
                return eventType == "session_start"
                    || eventType == "session_end"
                    || eventType == "session_abnormal_end"
                    || eventType == "exception";
            }

            if (string.Equals(recType, "log", StringComparison.Ordinal))
            {
                var level = ExtractField(line, "level");
                return level == "exception" || level == "error";
            }

            return false;
        }

        // ── Buffer trim helper ────────────────────────────────────────────────────

        /// <summary>
        /// Removes all records matching the predicate from the buffer in-place.
        /// Returns the count removed.
        /// </summary>
        private static int DropFromBuffer(List<EventRecord> buffer, Func<EventRecord, bool> predicate)
        {
            int removed = 0;
            for (int i = buffer.Count - 1; i >= 0; i--)
            {
                if (predicate(buffer[i]))
                {
                    buffer.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        // ── File system helpers ───────────────────────────────────────────────────

        private static long SumDirectory(string dir, bool topLevelOnly = false)
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            try
            {
                var files = topLevelOnly
                    ? Directory.GetFiles(dir)
                    : Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    try { total += new FileInfo(f).Length; }
                    catch { /* skip */ }
                }
            }
            catch { /* skip */ }
            return total;
        }

        private static long TryDeleteFileAndGetSize(string path)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                long size = new FileInfo(path).Length;
                File.Delete(path);
                lock (_sizeLock)
                {
                    if (_cachedTotalBytes >= 0)
                        _cachedTotalBytes = Math.Max(0, _cachedTotalBytes - size);
                }
                return size;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsCurrentChunk(string path)
            => string.Equals(Path.GetFileName(path), "chunk_current.jsonl",
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Fast field extractor: finds "key":"value" in a JSONL line without
        /// full deserialization. Matches the pattern used elsewhere in the SDK.
        /// </summary>
        private static string? ExtractField(string line, string key)
        {
            var search = "\"" + key + "\":\"";
            int idx = line.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + search.Length;
            int end = line.IndexOf('"', start);
            if (end < 0) return null;
            return line.Substring(start, end - start);
        }
    }
}
