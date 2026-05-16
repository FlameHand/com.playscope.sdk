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
        private readonly EventQueue _queue;
        private string _currentScreen = "";
        private string _currentAction = "";

        internal EventPipeline(EventQueue queue) => _queue = queue;

        internal void SetScreen(string screen) => _currentScreen = screen ?? "";
        internal void SetAction(string action) => _currentAction = action ?? "";

        internal void EnqueueEvent(string eventType, string? operationId = null, string? operationType = null,
            string? metadataJson = null, string? statePatchJson = null)
        {
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
            return "\"" + v.ToString()?.Replace("\"", "\\\"") + "\"";
        }
    }
}
