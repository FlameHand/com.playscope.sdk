using System;
using System.Collections.Generic;
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
        private CancellationTokenSource? _cts;
        private DateTime _lastFlush = DateTime.UtcNow;
        private int _chunkCounter = 0;
        private long _currentChunkSize = 0;
        private const int FlushRecordThreshold = 32;
        private const double FlushIntervalSeconds = 2.0;
        private const long BatchSplitBytes = 1_048_576; // 1 MB

        internal WriterWorker(EventQueue queue, UploadQueue uploadQueue, SessionInfo session)
        {
            _queue = queue;
            _uploadQueue = uploadQueue;
            _session = session;
        }

        internal void Start()
        {
            _cts = new CancellationTokenSource();
            EnsureCurrentChunk();
            RunAsync(_cts.Token).Forget();
        }

        internal void Stop()
        {
            _cts?.Cancel();
        }

        // Called from OnApplicationPause or shutdown — drain + flush synchronously
        internal void FlushImmediate()
        {
            _queue.DrainAll(_buffer);
            if (_buffer.Count > 0) FlushBuffer();
        }

        // Called from shutdown sequence: drain, flush, finalize
        internal void DrainAndFinalize()
        {
            _queue.DrainAll(_buffer);
            FlushBuffer();
            FinalizeChunk();
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

                // Drain all available
                while (_queue.TryDequeue(out var record)) _buffer.Add(record);

                bool hasCritical = false;
                foreach (var r in _buffer) if (r.IsCritical) { hasCritical = true; break; }

                bool timeTriggered = (DateTime.UtcNow - _lastFlush).TotalSeconds >= FlushIntervalSeconds;
                bool sizeTriggered = _buffer.Count >= FlushRecordThreshold;

                if (sizeTriggered || timeTriggered || hasCritical)
                {
                    FlushBuffer();
                    if (hasCritical) FinalizeChunk();
                }
            }
        }

        private void FlushBuffer()
        {
            if (_buffer.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var r in _buffer)
            {
                var line = SerializeRecord(r);
                sb.Append(line).Append('\n');
                _currentChunkSize += Encoding.UTF8.GetByteCount(line) + 1;
            }
            _buffer.Clear();
            _lastFlush = DateTime.UtcNow;

            try
            {
                var chunkPath = PlayScopeDirectory.CurrentChunkPath;
                File.AppendAllText(chunkPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayScope] Failed to write chunk: " + ex.Message);
            }

            // Batch splitting: >1MB → finalize
            if (_currentChunkSize >= BatchSplitBytes) FinalizeChunk();
        }

        private void FinalizeChunk()
        {
            var currentPath = PlayScopeDirectory.CurrentChunkPath;
            if (!File.Exists(currentPath)) return;
            _chunkCounter++;
            var finalName = Path.Combine(PlayScopeDirectory.Chunks,
                $"chunk_{_session.SessionShortId}_{_chunkCounter:D6}.jsonl");
            try
            {
                File.Move(currentPath, finalName);
                _uploadQueue.Enqueue(finalName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PlayScope] Failed to finalize chunk: " + ex.Message);
            }
            EnsureCurrentChunk();
            _currentChunkSize = 0;
        }

        private void EnsureCurrentChunk()
        {
            var path = PlayScopeDirectory.CurrentChunkPath;
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, "", new UTF8Encoding(false)); }
                catch (Exception ex) { Debug.LogWarning("[PlayScope] Could not create chunk: " + ex.Message); }
            }
        }

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
            Append(sb, "message", EscapeString(r.Message ?? ""));
            if (!string.IsNullOrEmpty(r.StackTrace)) Append(sb, "stack_trace", EscapeString(r.StackTrace!));
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
            sb.Append(",\"value\":").Append(r.MetricValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string key, string value)
            => sb.Append(",\"").Append(key).Append("\":\"").Append(value).Append('"');

        private static string EscapeString(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"")
               .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
