using System;
using System.Collections.Generic;
using System.Text;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Stamps and enqueues events from any thread.
    /// Sensitive key filtering is applied upstream in PlayScope.cs before calling here.
    /// </summary>
    internal sealed class EventPipeline
    {
        // Per-record size limits (spec):
        //   metadata    ≤ 4 KB
        //   state_patch ≤ 8 KB, ≤ 64 keys
        //   log message ≤ 2048 chars (truncated)
        //   stack trace ≤ 8192 chars (truncated)
        internal const int MaxMetadataBytes = 4096;
        internal const int MaxStatePatchBytes = 8192;
        internal const int MaxStatePatchKeys = 64;

        private readonly EventQueue _queue;
        private string _currentScreen = "";
        private string _currentAction = "";

        internal EventPipeline(EventQueue queue) => _queue = queue;

        internal void SetScreen(string screen) => _currentScreen = screen ?? "";
        internal void SetAction(string action) => _currentAction = action ?? "";

        internal void EnqueueEvent(string eventType, string? operationId = null, string? operationType = null,
            string? metadataJson = null, string? statePatchJson = null)
        {
            // Enforce metadata size cap (spec: 4 KB). Oversized metadata = drop the entire event.
            if (!string.IsNullOrEmpty(metadataJson) &&
                Encoding.UTF8.GetByteCount(metadataJson) > MaxMetadataBytes)
            {
                PlayScopeLog.Warning($"event dropped: metadata exceeds 4 KB (event_type={eventType}).");
                return;
            }

            // Enforce state_patch size + key-count cap (spec: 8 KB, 64 keys). On violation we
            // drop the state_patch but still emit the event so the rest of the signal is intact.
            if (!string.IsNullOrEmpty(statePatchJson))
            {
                int patchBytes = Encoding.UTF8.GetByteCount(statePatchJson);
                int keyCount = CountTopLevelJsonKeys(statePatchJson);
                if (patchBytes > MaxStatePatchBytes)
                {
                    PlayScopeLog.Warning(
                        $"state_patch dropped: exceeds 8 KB ({patchBytes} bytes, event_type={eventType}). Event emitted without patch.");
                    statePatchJson = null;
                }
                else if (keyCount > MaxStatePatchKeys)
                {
                    PlayScopeLog.Warning(
                        $"state_patch dropped: exceeds 64 keys ({keyCount}, event_type={eventType}). Event emitted without patch.");
                    statePatchJson = null;
                }
            }

            var r = new EventRecord
            {
                RecordType = RecordType.Event,
                EventType = eventType,
                EventId = UlidGenerator.NewEventId(),
                SequenceNum = SequenceCounter.Next(),
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ScreenName = _currentScreen,
                ActionName = _currentAction,
                OperationId = operationId,
                OperationType = operationType,
                MetadataJson = metadataJson,
                StatePatchJson = statePatchJson,
                IsCritical = CriticalRecords.IsCritical(eventType)
            };
            _queue.Enqueue(r);
        }

        /// <summary>
        /// Counts top-level JSON keys in a flat object literal. Naïve scanner — only counts
        /// quoted keys followed by ':' at brace-depth 1. Good enough for the size guard: it
        /// will not over-count nested object keys.
        /// </summary>
        internal static int CountTopLevelJsonKeys(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            int depth = 0;
            int keys = 0;
            bool inString = false;
            bool keyJustClosed = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\' && i + 1 < json.Length) { i++; continue; }
                    if (c == '"')
                    {
                        inString = false;
                        // peek next non-whitespace
                        if (depth == 1) keyJustClosed = true;
                    }
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{': depth++; break;
                    case '}': depth--; keyJustClosed = false; break;
                    case '[': depth++; break;
                    case ']': depth--; break;
                    case ':':
                        if (depth == 1 && keyJustClosed) { keys++; keyJustClosed = false; }
                        break;
                    case ',':
                        keyJustClosed = false;
                        break;
                    default:
                        if (!char.IsWhiteSpace(c)) keyJustClosed = false;
                        break;
                }
            }
            return keys;
        }

        internal void EnqueueLog(string level, string message, string? stackTrace = null, string? metadataJson = null)
        {
            // Truncate per spec: message 2048, stack_trace 8192
            if (message.Length > 2048) message = message[..2048] + "...[truncated]";
            if (stackTrace != null && stackTrace.Length > 8192) stackTrace = stackTrace[..8192] + "...[truncated]";
            var r = new EventRecord
            {
                RecordType = RecordType.Log,
                EventId = UlidGenerator.NewEventId(),
                SequenceNum = SequenceCounter.Next(),
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Level = level,
                Message = message,
                StackTrace = stackTrace,
                ScreenName = _currentScreen,
                ActionName = _currentAction,
                MetadataJson = metadataJson,
                IsCritical = CriticalRecords.IsLogCritical(level)
            };
            _queue.Enqueue(r);
        }

        internal void EnqueueMetric(string metricType, double value)
        {
            var r = new EventRecord
            {
                RecordType = RecordType.Metric,
                EventId = UlidGenerator.NewEventId(),
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                MetricType = metricType,
                MetricValue = value
            };
            _queue.Enqueue(r);
        }

        // Helpers for building metadata JSON from Dictionary without external libs
        internal static string DictToJson(IReadOnlyDictionary<string, object> dict)
        {
            if (dict == null || dict.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key.Replace("\"", "\\\"")).Append("\":");
                sb.Append(ValueToJson(kv.Value));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string ValueToJson(object? v)
        {
            if (v == null) return "null";
            if (v is bool b) return b ? "true" : "false";
            if (v is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
            if (v is int or long or float or double or decimal) return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            if (v is IReadOnlyDictionary<string, object> d) return DictToJson(d);
            if (v is Dictionary<string, object> dd) return DictToJson(dd);
            if (v is System.Collections.IList list)
            {
                var sb2 = new StringBuilder("[");
                bool firstItem = true;
                foreach (var item in list)
                {
                    if (!firstItem) sb2.Append(',');
                    sb2.Append(ValueToJson(item));
                    firstItem = false;
                }
                sb2.Append(']');
                return sb2.ToString();
            }
            return "\"" + v.ToString()?.Replace("\"", "\\\"") + "\"";
        }
    }
}
