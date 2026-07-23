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
        // Volatile: written by SetScreen/SetAction (any thread), read on the
        // writer thread. Without the barrier a write can stay invisible for
        // hundreds of ms, tagging events with the previous screen.
        private volatile string _currentScreen = "";
        private volatile string _currentAction = "";
        // Optional — null = every log goes straight to the queue (dedup is purely additive).
        private LogDedupBuffer _logDedup;

        internal EventPipeline(EventQueue queue) => _queue = queue;

        internal void SetLogDedupBuffer(LogDedupBuffer buffer) => _logDedup = buffer;

        internal void SetScreen(string screen) => _currentScreen = screen ?? "";
        internal void SetAction(string action) => _currentAction = action ?? "";

        internal void EnqueueEvent(string eventType, string operationId = null, string operationType = null,
            string metadataJson = null, string statePatchJson = null)
        {
            // Write gate — closed (~1 frame) during the rotation race window so
            // nothing stamps itself into the doomed old session. Silent drop.
            if (!PlayScopeRuntime._acceptingEvents) return;

            // Oversized metadata: emit a small truncation sentinel instead of
            // dropping the whole event, so sequence_num / screen / action /
            // operation_id context survives and the dashboard can badge it.
            if (!string.IsNullOrEmpty(metadataJson))
            {
                int originalSize = Encoding.UTF8.GetByteCount(metadataJson);
                if (originalSize > MaxMetadataBytes)
                {
                    PlayScopeLog.Warning(
                        $"event metadata exceeds 4 KB ({originalSize} bytes, event_type={eventType}). " +
                        "Replacing metadata with truncation sentinel; event will still be emitted.");
                    metadataJson =
                        "{\"_playscope\":{\"metadata_truncated\":true,\"original_size_bytes\":"
                        + originalSize + "}}";
                }
            }

            // state_patch cap (8 KB, 64 keys): on violation drop the patch but still emit the event.
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
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
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
        /// Counts DISTINCT top-level keys at brace-depth 1. Distinct because the
        /// server parser dedupes last-write-wins, so `{"a":1,...,"a":70}` caps as 1.
        /// </summary>
        internal static int CountTopLevelJsonKeys(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            int depth = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool inString = false;
            int keyStringStart = -1;
            string lastTopLevelKey = null;
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
                        if (depth == 1 && keyStringStart >= 0)
                        {
                            // keyStringStart points to the char after the opening quote.
                            lastTopLevelKey = json.Substring(keyStringStart, i - keyStringStart);
                            keyJustClosed = true;
                        }
                        keyStringStart = -1;
                    }
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inString = true;
                        if (depth == 1) keyStringStart = i + 1;
                        break;
                    case '{': depth++; break;
                    case '}': depth--; keyJustClosed = false; break;
                    case '[': depth++; break;
                    case ']': depth--; break;
                    case ':':
                        if (depth == 1 && keyJustClosed && lastTopLevelKey != null)
                        {
                            seen.Add(lastTopLevelKey);
                            keyJustClosed = false;
                            lastTopLevelKey = null;
                        }
                        break;
                    case ',':
                        keyJustClosed = false;
                        break;
                    default:
                        if (!char.IsWhiteSpace(c)) keyJustClosed = false;
                        break;
                }
            }
            return seen.Count;
        }

        internal void EnqueueLog(string level, string message, string stackTrace = null, string metadataJson = null)
        {
            // Write gate (see EnqueueEvent) — drops even error/exception during
            // the rotation window; they belong to the about-to-open new session.
            if (!PlayScopeRuntime._acceptingEvents) return;

            // PII mask at the single choke point — covers TrackLog, TrackException
            // and Unity auto-capture alike. Before the final truncation, so a cut
            // can't split a PII token into an unmatchable (leaked) fragment — but
            // pre-clamped to a safety bound so a pathological multi-MB string
            // doesn't pay 7 regex passes over its full length.
            if (message.Length > 32768)
            {
                message = SafeTruncate(message, 32768);
            }
            if (stackTrace != null && stackTrace.Length > 32768)
            {
                stackTrace = SafeTruncate(stackTrace, 32768);
            }
            message = SensitiveKeyFilter.MaskLogText(message);
            if (stackTrace != null)
            {
                stackTrace = SensitiveKeyFilter.MaskLogText(stackTrace);
            }

            // Truncate (message 2048, stack 8192) — surrogate-safe so a cut at a
            // UTF-16 boundary can't leave an unpaired surrogate that breaks the JSON.
            if (message.Length > 2048) message = SafeTruncate(message, 2048) + "...[truncated]";
            if (stackTrace != null && stackTrace.Length > 8192)
                stackTrace = SafeTruncate(stackTrace, 8192) + "...[truncated]";
            var r = new EventRecord
            {
                RecordType = RecordType.Log,
                EventId = UlidGenerator.NewEventId(),
                SequenceNum = SequenceCounter.Next(),
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                Level = level,
                Message = message,
                StackTrace = stackTrace,
                ScreenName = _currentScreen,
                ActionName = _currentAction,
                MetadataJson = metadataJson,
                IsCritical = CriticalRecords.IsLogCritical(level)
            };
            // Criticals bypass dedup (no 5 s delay). Stack-trace records also
            // bypass: dedup keys on (level, message) only, so collapsing two
            // different stacks would lose information.
            if (_logDedup != null && !r.IsCritical && string.IsNullOrEmpty(stackTrace))
            {
                _logDedup.Add(r);
                return;
            }
            _queue.Enqueue(r);
        }

        internal void EnqueueMetric(string metricType, double value)
        {
            // Pipeline write gate (see comment in EnqueueEvent).
            if (!PlayScopeRuntime._acceptingEvents) return;

            // Drop NaN / ±Infinity here — the backend's non-nullable double DTO
            // rejects JSON null, failing the whole envelope (one bad value once
            // killed ~10k events). Safer to drop the one metric than the batch.
            if (!double.IsFinite(value))
            {
                PlayScopeLog.Warning(
                    $"EnqueueMetric: dropping non-finite value for metric_type={metricType} (NaN/Infinity)");
                return;
            }
            var r = new EventRecord
            {
                RecordType = RecordType.Metric,
                EventId = UlidGenerator.NewEventId(),
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                MetricType = metricType,
                MetricValue = value
            };
            _queue.Enqueue(r);
        }

        // Hand-rolled RFC 8259 JSON producer for metadata / state-patch payloads:
        // full C0-control escaping, non-finite doubles → null, depth-bounded so a
        // cyclic graph surfaces as "null" instead of an uncatchable Unity SOE.
        internal const int MaxJsonDepth = 16;

        internal static string DictToJson(IReadOnlyDictionary<string, object> dict)
            => DictToJson(dict, depth: 0);

        private static string DictToJson(IReadOnlyDictionary<string, object> dict, int depth)
        {
            if (dict == null || dict.Count == 0) return "{}";
            if (depth >= MaxJsonDepth) return "null";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendEscapedString(sb, kv.Key ?? "");
                sb.Append(':');
                sb.Append(ValueToJson(kv.Value, depth + 1));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string ValueToJson(object v, int depth)
        {
            if (v == null) return "null";
            if (v is bool b) return b ? "true" : "false";
            if (v is string s) {
                var sb = new StringBuilder(s.Length + 2);
                AppendEscapedString(sb, s);
                return sb.ToString();
            }
            if (v is int or long or short or sbyte or uint or ulong or ushort or byte)
                return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            // Reject NaN / Infinity → null; one bad sample would dead-letter the batch.
            if (v is float f)
                return float.IsFinite(f) ? Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture) : "null";
            if (v is double d2)
                return double.IsFinite(d2) ? Convert.ToString(d2, System.Globalization.CultureInfo.InvariantCulture) : "null";
            if (v is decimal dec) return Convert.ToString(dec, System.Globalization.CultureInfo.InvariantCulture);
            if (depth >= MaxJsonDepth) return "null"; // depth-bounded vs cyclic-graph SOE
            if (v is IReadOnlyDictionary<string, object> d) return DictToJson(d, depth);
            if (v is Dictionary<string, object> dd) return DictToJson(dd, depth);
            if (v is System.Collections.IList list)
            {
                var sb2 = new StringBuilder("[");
                bool firstItem = true;
                foreach (var item in list)
                {
                    if (!firstItem) sb2.Append(',');
                    sb2.Append(ValueToJson(item, depth + 1));
                    firstItem = false;
                }
                sb2.Append(']');
                return sb2.ToString();
            }
            // Fallback: ToString() then escape (safe against unusual chars).
            var sbF = new StringBuilder();
            AppendEscapedString(sbF, v.ToString() ?? "");
            return sbF.ToString();
        }

        /// <summary>
        /// Truncates to at most <paramref name="maxChars"/> chars without
        /// splitting a UTF-16 surrogate pair.
        /// </summary>
        private static string SafeTruncate(string s, int maxChars)
        {
            if (s.Length <= maxChars) return s;
            int cut = maxChars;
            if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut);
        }

        // Appends s as a quoted, RFC 8259-escaped JSON string (C0 controls via \u00XX). Shared by key + value paths.
        internal static void AppendEscapedString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4",
                                System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
