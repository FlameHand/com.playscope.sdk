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
        private CancellationTokenSource _cts;
        // Monotonic clock for the time-trigger flush: a DateTime.UtcNow rewind
        // (NTP) would go negative and silently disable the flush until the clock
        // caught up. Stopwatch is hardware-monotonic.
        private long _lastFlushStopwatchTicks = Stopwatch.GetTimestamp();
        private int _chunkCounter = 0;
        private long _currentChunkSize = 0;

        // Long-lived stream over chunk_current.jsonl (issue #15).
        private FileStream _currentStream;
        private StreamWriter _currentWriter;

        private const int FlushRecordThreshold = 32;
        private const double FlushIntervalSeconds = 2.0;
        private const long BatchSplitBytes = 1_048_576; // 1 MB

        /// <summary>
        /// Invoked whenever a chunk is finalized (critical record, 1 MB split,
        /// FlushImmediate, DrainAndFinalize). Wired to
        /// <see cref="UploaderWorker.TriggerInstantUpload"/> so the uploader wakes
        /// instead of sleeping out its 30 s window. Idempotent uploader-side.
        /// </summary>
        internal Action OnChunkFinalized;

        internal WriterWorker(EventQueue queue, UploadQueue uploadQueue, SessionInfo session)
        {
            _queue = queue;
            _uploadQueue = uploadQueue;
            _session = session;
        }

        internal void Start()
        {
            _cts = new CancellationTokenSource();
            // If SessionRecovery couldn't relocate the prior session's
            // chunk_current.jsonl (locked, AV scanner, swallowed TryMoveFile
            // failure), FileMode.Append below would commingle two sessions'
            // events under our envelope. Quarantine any leftover first.
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
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
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
            // Cancel FIRST so RunAsync exits, then take the lock to let any
            // in-progress FlushBufferLocked finish before we close the stream.
            _cts?.Cancel();
            lock (_bufferLock)
            {
                // Explicit close — DrainAndFinalize closes but EnsureCurrentChunk
                // reopens right after, so without this the handle lingers until GC
                // and the next Initialize hits a sharing violation.
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
        /// Synchronously write one critical record AND finalize the chunk under a
        /// single lock (drain → append → flush → close → rename → enqueue), so
        /// the RunAsync loop can't interleave and finalize an empty chunk. For the
        /// rotation/shutdown path where the async-drain race once lost session_end.
        /// Returns true on success.
        /// </summary>
        internal bool WriteCriticalAndFinalizeSync(EventRecord record)
        {
            try
            {
                lock (_bufferLock)
                {
                    // Drain queued events so they ride the same final chunk
                    // instead of being stranded when Stop() cancels the loop.
                    while (_queue.TryDequeue(out var queued)) _buffer.Add(queued);
                    _buffer.Add(record);
                    FlushBufferLocked();
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
                    // Wake-up fires inside FinalizeChunkInternal — don't double-fire here.
                }
            }
        }

        // Caller must hold _bufferLock — I/O stays inside the lock because
        // _currentWriter is shared with the lock-aware FinalizeChunk paths.
        private void FlushBufferLocked()
        {
            if (_buffer.Count == 0) return;

            // Hard-cap check: trim lowest-priority records before writing to disk
            if (StorageQuotaManager.IsHardCapExceeded())
            {
                var (kept, dropped) = StorageQuotaManager.TrimBuffer(_buffer);
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
            bool enqueued = false;
            try
            {
                File.Move(currentPath, finalName);
                _uploadQueue.Enqueue(finalName);
                StorageQuotaManager.NotifyChunkFinalized(finalName);
                enqueued = true;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Failed to finalize chunk", ex);
            }
            EnsureCurrentChunk();
            _currentChunkSize = 0;

            // Wake the uploader the instant a chunk lands, not at the next 30 s
            // poll — a swipe-kill within that window otherwise delayed shipping
            // to the next launch. Safe on every path (no-op if a pass is queued).
            if (enqueued)
            {
                try { OnChunkFinalized?.Invoke(); }
                catch (Exception ex) { PlayScopeLog.Warning("OnChunkFinalized handler threw", ex); }
            }
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

        private string SerializeRecord(EventRecord r)
        {
            return r.RecordType switch
            {
                RecordType.Event => SerializeEvent(r),
                RecordType.Log => SerializeLog(r),
                RecordType.Metric => SerializeMetric(r),
                _ => "{}"
            };
        }

        private string SerializeEvent(EventRecord r)
        {
            var sb = new StringBuilder();
            sb.Append("{\"record_type\":\"event\"");
            // Top-level session_id so SessionRecovery's flat SimpleJson parser can
            // read it back if session.json is corrupt — otherwise every chunk
            // dead-letters with no way to learn the session_id. Backend ignores
            // it (envelope SessionId is authoritative).
            Append(sb, "session_id", _session.SessionId);
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

        private string SerializeLog(EventRecord r)
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

        private string SerializeMetric(EventRecord r)
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
