using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using PlayScopeSdk.Core.Session;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    internal sealed class WriterWorker
    {
        private readonly EventQueue _queue;
        private readonly UploadQueue _uploadQueue;
        private readonly SessionInfo _session;
        private readonly List<EventRecord> _buffer = new(64);
        private readonly object _bufferLock = new object();
        private CancellationTokenSource? _cts;
        // Monotonic wall clock for the time-trigger flush. DateTime.UtcNow is
        // subject to NTP rewinds — a backwards step would leave the diff
        // negative and the time-triggered flush silently disabled until the
        // wall clock caught up. Stopwatch is hardware-monotonic everywhere.
        private long _lastFlushStopwatchTicks = Stopwatch.GetTimestamp();
        private int _chunkCounter = 0;
        private long _currentChunkSize = 0;

        // Long-lived stream over chunk_current.jsonl (issue #15).
        private FileStream? _currentStream;
        private StreamWriter? _currentWriter;

        private const int FlushRecordThreshold = 32;
        private const double FlushIntervalSeconds = 2.0;
        private const long BatchSplitBytes = 1_048_576; // 1 MB

        /// <summary>
        /// Optional callback invoked whenever a chunk is finalized due to a critical record.
        /// Used by PlayScopeRuntime to trigger an instant upload cycle.
        /// </summary>
        internal Action? OnCriticalChunkFinalized;

        internal WriterWorker(EventQueue queue, UploadQueue uploadQueue, SessionInfo session)
        {
            _queue = queue;
            _uploadQueue = uploadQueue;
            _session = session;
        }

        internal void Start()
        {
            _cts = new CancellationTokenSource();
            // Defense in depth: if SessionRecovery couldn't relocate the prior session's
            // chunk_current.jsonl (file locked, AV scanner, partial-failure inside
            // TryMoveFile — it swallows exceptions), the file would still be on disk and
            // EnsureCurrentChunk would open it with FileMode.Append, mixing this session's
            // events with the previous session's content. The merged chunk would later
            // rotate to chunk_{ourShortId}_NNNNNN.jsonl and upload under OUR envelope,
            // re-introducing the cross-session commingling we just fixed in 0.1.16.
            // So before we touch the file at all, quarantine any leftover content.
            QuarantineStaleCurrentChunk();
            EnsureCurrentChunk();
            RunAsync(_cts.Token).Forget();
        }

        /// <summary>
        /// If <c>chunk_current.jsonl</c> exists with non-zero size at startup, that content
        /// belongs to a previous session that SessionRecovery failed to relocate. We don't
        /// know which backend session_id it belonged to (the JSONL lines don't carry one —
        /// only the envelope does), so we cannot re-upload it correctly. Move it into the
        /// dead-letter directory under a timestamped name so:
        /// <list type="bullet">
        /// <item>it never gets appended to by the new session,</item>
        /// <item>it never gets uploaded under the wrong session_id,</item>
        /// <item>the bytes are preserved for forensic recovery if anyone ever needs them,</item>
        /// <item>dead-letter TTL eventually reclaims the disk.</item>
        /// </list>
        /// </summary>
        private void QuarantineStaleCurrentChunk()
        {
            var currentPath = PlayScopeDirectory.CurrentChunkPath;
            try
            {
                if (!File.Exists(currentPath)) return;
                if (new FileInfo(currentPath).Length == 0)
                {
                    // Empty file is harmless — just delete it to start clean.
                    try { File.Delete(currentPath); } catch { /* best-effort */ }
                    return;
                }

                var deadLetterDir = PlayScopeDirectory.DeadLetter;
                Directory.CreateDirectory(deadLetterDir);
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                var destName = $"orphaned_chunk_current_{stamp}.jsonl";
                var dest = Path.Combine(deadLetterDir, destName);
                File.Move(currentPath, dest);
                PlayScopeLog.Warning(
                    $"WriterWorker: stale chunk_current.jsonl quarantined to dead_letter/{destName}. " +
                    "SessionRecovery likely could not relocate it (file locked at startup?). " +
                    "Content cannot be re-uploaded — original session_id unknown.");
            }
            catch (Exception ex)
            {
                // Last resort: if we can't even move it, truncate to zero so we don't append
                // foreign data. Data loss, but better than cross-session commingling.
                PlayScopeLog.Warning("WriterWorker: failed to quarantine stale chunk_current; truncating", ex);
                try { File.WriteAllBytes(currentPath, Array.Empty<byte>()); } catch { /* swallow */ }
            }
        }

        internal void Stop()
        {
            // Cancel the loop FIRST so RunAsync drops out of its current
            // iteration; then acquire the buffer lock to wait for any
            // in-progress FlushBufferLocked to finish before we close the
            // stream out from under it.
            _cts?.Cancel();
            lock (_bufferLock)
            {
                // Close the long-lived chunk_current handle. Shutdown's caller
                // already invokes DrainAndFinalize which closes via
                // FinalizeChunkInternal → CloseCurrentWriter, but
                // EnsureCurrentChunk reopens immediately afterwards. Without
                // this explicit close the stream stays open until GC and the
                // next session's Initialize fails with a sharing violation.
                CloseCurrentWriter();
            }
            try { _cts?.Dispose(); } catch { /* best-effort */ }
            _cts = null;
        }

        // Called from OnApplicationPause or shutdown — drain, flush, and finalize the chunk so
        // the data is renamed out of chunk_current.jsonl and queued for upload before the OS
        // can kill a backgrounded app.
        internal void FlushImmediate()
        {
            lock (_bufferLock)
            {
                _queue.DrainAll(_buffer);
                if (_buffer.Count > 0) FlushBufferLocked();
            }
            FinalizeChunk();
        }

        // Called from shutdown sequence: drain, flush, finalize
        internal void DrainAndFinalize()
        {
            lock (_bufferLock)
            {
                _queue.DrainAll(_buffer);
                FlushBufferLocked();
            }
            FinalizeChunk();
            StorageQuotaManager.EnforceQuota();
        }

        /// <summary>
        /// Synchronously write a single critical record AND finalize the chunk —
        /// bypasses the async RunAsync loop entirely. Built for the rotation /
        /// shutdown path where the regular Pipeline.EnqueueEvent → async drain
        /// could race with the worker loop and lose the record (post-mortem on
        /// 2026-05-21: rotation-induced session_end didn't survive the race
        /// between WriterWorker.RunAsync drain and TeardownInternal's
        /// DrainAndFinalize, and the finalized chunk uploaded empty).
        ///
        /// <para>
        /// The single lock acquisition covers drain-queue → append-record →
        /// flush → close-writer → rename → enqueue-for-upload as one atomic
        /// step, so the WorkerWorker.RunAsync loop physically cannot
        /// interleave between flush and finalize. Returns true on success.
        /// </para>
        /// </summary>
        internal bool WriteCriticalAndFinalizeSync(EventRecord record)
        {
            try
            {
                lock (_bufferLock)
                {
                    // First drain any events already queued so they ride along
                    // in the same final chunk — otherwise they get stranded in
                    // _queue when Stop() cancels the worker loop seconds later.
                    while (_queue.TryDequeue(out var queued)) _buffer.Add(queued);
                    _buffer.Add(record);
                    FlushBufferLocked();
                    // Finalize INSIDE the same lock so RunAsync can't sneak in
                    // and finalize an empty chunk between our flush and rename.
                    FinalizeChunkInternal();
                }
                StorageQuotaManager.EnforceQuota();
                return true;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("WriteCriticalAndFinalizeSync failed", ex);
                return false;
            }
        }

        private async UniTask RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _queue.WaitAsync(ct);
                }
                catch (OperationCanceledException) { break; }

                bool hasCritical = false;
                bool shouldFlush = false;

                lock (_bufferLock)
                {
                    // Drain all available
                    while (_queue.TryDequeue(out var record)) _buffer.Add(record);

                    foreach (var r in _buffer) if (r.IsCritical) { hasCritical = true; break; }

                    bool timeTriggered = StopwatchSecondsSince(_lastFlushStopwatchTicks) >= FlushIntervalSeconds;
                    bool sizeTriggered = _buffer.Count >= FlushRecordThreshold;

                    if (sizeTriggered || timeTriggered || hasCritical)
                    {
                        FlushBufferLocked();
                        shouldFlush = true;
                    }
                }

                if (shouldFlush && hasCritical)
                {
                    FinalizeChunk();
                    StorageQuotaManager.EnforceQuota();
                    OnCriticalChunkFinalized?.Invoke();
                }
            }
        }

        // Caller must hold _bufferLock. Serializes buffered records to a single string,
        // releases the buffer contents, then performs file I/O. We keep the I/O inside the
        // lock here because _currentWriter is shared mutable state too (open/close on
        // FinalizeChunk happens from the same lock-aware paths).
        private void FlushBufferLocked()
        {
            if (_buffer.Count == 0) return;

            // Hard-cap check: trim lowest-priority records before writing to disk
            if (StorageQuotaManager.IsHardCapExceeded())
            {
                var (kept, dropped) = StorageQuotaManager.TrimBuffer(_buffer);
                // TrimBuffer mutates the list in-place and returns the same reference;
                // re-assign for clarity in case implementation changes.
                _buffer.Clear();
                _buffer.AddRange(kept);
                if (dropped > 0)
                    PlayScopeLog.Warning($"Hard storage cap exceeded — trimmed {dropped} buffered record(s) before flush.");
            }

            if (_buffer.Count == 0) return;

            var sb = new StringBuilder();
            long bytesWritten = 0;
            foreach (var r in _buffer)
            {
                var line = SerializeRecord(r);
                sb.Append(line).Append('\n');
                bytesWritten += Encoding.UTF8.GetByteCount(line) + 1;
            }
            _buffer.Clear();
            _lastFlushStopwatchTicks = Stopwatch.GetTimestamp();

            try
            {
                if (_currentWriter == null) EnsureCurrentChunk();
                _currentWriter?.Write(sb.ToString());
                _currentWriter?.Flush();
                _currentChunkSize += bytesWritten;
                StorageQuotaManager.NotifyBytesAppended(bytesWritten);
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to write chunk", ex);
            }

            // Batch splitting: >1MB → finalize, then enforce storage quota
            if (_currentChunkSize >= BatchSplitBytes)
            {
                FinalizeChunkInternal();
                StorageQuotaManager.EnforceQuota();
            }
        }

        private void FinalizeChunk()
        {
            lock (_bufferLock)
            {
                FinalizeChunkInternal();
            }
        }

        private void FinalizeChunkInternal()
        {
            var currentPath = PlayScopeDirectory.CurrentChunkPath;
            // Dispose writer FIRST so File.Move can rename without sharing-violation.
            CloseCurrentWriter();
            if (!File.Exists(currentPath))
            {
                EnsureCurrentChunk();
                _currentChunkSize = 0;
                return;
            }
            // Skip empty chunks — nothing to upload, would create empty batches.
            try
            {
                if (new FileInfo(currentPath).Length == 0)
                {
                    EnsureCurrentChunk();
                    _currentChunkSize = 0;
                    return;
                }
            }
            catch { /* fall through */ }

            _chunkCounter++;
            var finalName = Path.Combine(PlayScopeDirectory.Chunks,
                $"chunk_{_session.SessionShortId}_{_chunkCounter:D6}.jsonl");
            try
            {
                File.Move(currentPath, finalName);
                _uploadQueue.Enqueue(finalName);
                StorageQuotaManager.NotifyChunkFinalized(finalName);
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to finalize chunk", ex);
            }
            EnsureCurrentChunk();
            _currentChunkSize = 0;
        }

        private void EnsureCurrentChunk()
        {
            var path = PlayScopeDirectory.CurrentChunkPath;
            try
            {
                // Open in append mode; create if missing. FileShare.Read keeps reader compat.
                _currentStream ??= new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
                if (_currentWriter == null)
                {
                    _currentWriter = new StreamWriter(_currentStream, new UTF8Encoding(false));
                    _currentWriter.AutoFlush = false;
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Could not open current chunk", ex);
            }
        }

        private void CloseCurrentWriter()
        {
            try { _currentWriter?.Flush(); } catch { /* ok */ }
            try { _currentWriter?.Dispose(); } catch { /* ok */ }
            try { _currentStream?.Dispose(); } catch { /* ok */ }
            _currentWriter = null;
            _currentStream = null;
        }

        private static double StopwatchSecondsSince(long start)
            => (double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency;

        private static string SerializeRecord(EventRecord r)
        {
            return r.RecordType switch
            {
                RecordType.Event => SerializeEvent(r),
                RecordType.Log => SerializeLog(r),
                RecordType.Metric => SerializeMetric(r),
                _ => "{}"
            };
        }

        private static string SerializeEvent(EventRecord r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"record_type\":\"event\"");
            Append(sb, "event_type", r.EventType);
            Append(sb, "event_id", r.EventId);
            sb.Append(",\"sequence_num\":").Append(r.SequenceNum);
            Append(sb, "timestamp", r.Timestamp);
            if (!string.IsNullOrEmpty(r.ScreenName)) Append(sb, "screen_name", r.ScreenName!);
            if (!string.IsNullOrEmpty(r.ActionName)) Append(sb, "action_name", r.ActionName!);
            // operation_id / operation_type are SDK-produced today but defensively
            // escaped anyway — any future API that lets callers supply opIds
            // (think MCP-driven sessions or scripted replays) won't be able to
            // poison the JSON line by accident.
            if (!string.IsNullOrEmpty(r.OperationId)) Append(sb, "operation_id", r.OperationId!);
            if (!string.IsNullOrEmpty(r.OperationType)) Append(sb, "operation_type", r.OperationType!);
            if (!string.IsNullOrEmpty(r.MetadataJson)) sb.Append(",\"metadata\":").Append(r.MetadataJson);
            if (!string.IsNullOrEmpty(r.StatePatchJson)) sb.Append(",\"state_patch\":").Append(r.StatePatchJson);
            sb.Append('}');
            return sb.ToString();
        }

        private static string SerializeLog(EventRecord r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"record_type\":\"log\"");
            Append(sb, "event_id", r.EventId);
            sb.Append(",\"sequence_num\":").Append(r.SequenceNum);
            Append(sb, "timestamp", r.Timestamp);
            Append(sb, "level", r.Level ?? "info");
            Append(sb, "message", r.Message ?? "");
            if (!string.IsNullOrEmpty(r.StackTrace)) Append(sb, "stack_trace", r.StackTrace!);
            if (!string.IsNullOrEmpty(r.ScreenName)) Append(sb, "screen_name", r.ScreenName!);
            if (!string.IsNullOrEmpty(r.ActionName)) Append(sb, "action_name", r.ActionName!);
            if (!string.IsNullOrEmpty(r.MetadataJson)) sb.Append(",\"metadata\":").Append(r.MetadataJson);
            sb.Append('}');
            return sb.ToString();
        }

        private static string SerializeMetric(EventRecord r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"record_type\":\"metric\"");
            Append(sb, "event_id", r.EventId);
            Append(sb, "timestamp", r.Timestamp);
            Append(sb, "metric_type", r.MetricType ?? "");
            // Mirror EventPipeline.ValueToJson's non-finite guard so a bad
            // metric sample (div-by-zero, Mathf.Infinity) can't corrupt the
            // entire chunk JSON and force a dead-letter for the batch.
            sb.Append(",\"value\":").Append(
                double.IsFinite(r.MetricValue)
                    ? r.MetricValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture)
                    : "null");
            sb.Append('}');
            return sb.ToString();
        }

        // All string fields go through the same RFC 8259 escaper as
        // EventPipeline.DictToJson so the two paths can't diverge again.
        // Key is bare-key (already safe) — value is fully escaped.
        private static void Append(StringBuilder sb, string key, string value)
        {
            sb.Append(",\"").Append(key).Append("\":");
            EventPipeline.AppendEscapedString(sb, value);
        }
    }
}
