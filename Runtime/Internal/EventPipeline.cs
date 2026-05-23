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
        // Read on the writer worker thread, written from any thread that
        // calls SetScreen / SetAction (typically Unity main-thread, but
        // wrapper code may call from coroutines or background tasks). Plain
        // reference assignment is atomic in .NET but there's no memory
        // barrier — a write here can be invisible to the writer for several
        // hundred ms, producing events tagged with the previous screen.
        // Volatile fields force the necessary ordering.
        private volatile string _currentScreen = "";
        private volatile string _currentAction = "";
        // Optional dedup buffer for absorbable log levels (debug/info/warning).
        // Wired in by PlayScopeRuntime after construction so the pipeline stays
        // usable in tests without the buffer present. When null, every log
        // record goes straight to the queue — the dedup feature is purely an
        // additive optimisation, never a correctness requirement.
        private LogDedupBuffer? _logDedup;

        internal EventPipeline(EventQueue queue) => _queue = queue;

        internal void SetLogDedupBuffer(LogDedupBuffer? buffer) => _logDedup = buffer;

        internal void SetScreen(string screen) => _currentScreen = screen ?? "";
        internal void SetAction(string action) => _currentAction = action ?? "";

        internal void EnqueueEvent(string eventType, string? operationId = null, string? operationType = null,
            string? metadataJson = null, string? statePatchJson = null)
        {
            // Enforce metadata size cap (spec: 4 KB). Oversized metadata
            // used to drop the entire event — losing the signal that the
            // event happened at all (and its sequence_num / screen /
            // action / operation_id context). Replace with a tiny
            // truncation sentinel so the event still lands; the dashboard
            // can render a "metadata truncated" badge by reading
            // _playscope.metadata_truncated. Sentinel is ~70 bytes and
            // trivially fits the cap.
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
            // Counts DISTINCT top-level keys. A caller passing
            // `{"a":1,"a":2,"a":3,...,"a":70}` shouldn't get 70 — System.Text.Json
            // and the server JSON parser both dedupe last-write-wins, so the
            // effective key count for cap purposes is 1.
            if (string.IsNullOrEmpty(json)) return 0;
            int depth = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool inString = false;
            int keyStringStart = -1;
            string? lastTopLevelKey = null;
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
                            // Capture the key substring (between the opening "
                            // and the closing " — note keyStringStart points
                            // to char AFTER the opening quote).
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

        internal void EnqueueLog(string level, string message, string? stackTrace = null, string? metadataJson = null)
        {
            // Truncate per spec: message 2048, stack_trace 8192.
            // Substring cuts on char index; a slice at a UTF-16 high-surrogate
            // boundary leaves an unpaired surrogate which System.Text.Json
            // (and most strict JSON parsers) reject. Back the cut up to the
            // last full code point before the limit so the JSON line is
            // always valid.
            if (message.Length > 2048) message = SafeTruncate(message, 2048) + "...[truncated]";
            if (stackTrace != null && stackTrace.Length > 8192)
                stackTrace = SafeTruncate(stackTrace, 8192) + "...[truncated]";
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
            // Critical levels (error / exception) bypass dedup entirely —
            // every error matters individually and we never want to delay
            // its arrival by the 5 s dedup window. Records with a stack
            // trace also bypass: dedup keys on (level, message) only, so
            // collapsing two records with different stacks into one would
            // lose information. Everything else (debug/info/warning) is
            // offered to the buffer, which either absorbs the record as a
            // repeat or holds it as the first-of-key sample.
            if (_logDedup != null && !r.IsCritical && string.IsNullOrEmpty(stackTrace))
            {
                _logDedup.Add(r);
                return;
            }
            _queue.Enqueue(r);
        }

        internal void EnqueueMetric(string metricType, double value)
        {
            // Drop NaN / ±Infinity at the door. The backend's MetricRecord
            // DTO is non-nullable `double` and System.Text.Json refuses to
            // deserialize JSON `null` into it → the whole envelope is
            // rejected as invalid_body and the SDK retries forever until
            // dead-letter. One bad value (e.g. accidental div-by-zero in a
            // metric callback) used to kill ~10 000 events + ~5 000 logs.
            // Safer to silently drop the metric than to torpedo the batch.
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
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                MetricType = metricType,
                MetricValue = value
            };
            _queue.Enqueue(r);
        }

        // Hand-rolled JSON producer for the SDK's metadata / state-patch
        // payloads. RFC 8259 compliant for the inputs we accept:
        //   - keys and string values are fully escaped (including the C0
        //     control range  .., which strict parsers reject)
        //   - non-finite doubles (NaN / Infinity) collapse to JSON null
        //     rather than the invalid literals NaN/Infinity that some
        //     parsers reject and others silently treat as undefined
        //   - dict graph depth is bounded; cyclic refs surface as "null"
        //     instead of running off the stack (uncatchable SOE in Unity)
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

        private static string ValueToJson(object? v, int depth)
        {
            if (v == null) return "null";
            if (v is bool b) return b ? "true" : "false";
            if (v is string s) {
                var sb = new StringBuilder(s.Length + 2);
                AppendEscapedString(sb, s);
                return sb.ToString();
            }
            // Integer types — straightforward invariant-culture conversion.
            if (v is int or long or short or sbyte or uint or ulong or ushort or byte)
                return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            // Floating-point — reject NaN / Infinity which aren't valid JSON
            // literals. A single bad sample (div-by-zero, Mathf.Infinity)
            // would otherwise corrupt an entire envelope and the backend
            // would dead-letter all events in the batch.
            if (v is float f)
                return float.IsFinite(f) ? Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture) : "null";
            if (v is double d2)
                return double.IsFinite(d2) ? Convert.ToString(d2, System.Globalization.CultureInfo.InvariantCulture) : "null";
            if (v is decimal dec) return Convert.ToString(dec, System.Globalization.CultureInfo.InvariantCulture);
            // Recursive containers — depth-bounded so cyclic graphs can't SOE.
            if (depth >= MaxJsonDepth) return "null";
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
            // Fallback: ToString() then escape. Same path the old code took,
            // now safe against unusual chars in the resulting string.
            var sbF = new StringBuilder();
            AppendEscapedString(sbF, v.ToString() ?? "");
            return sbF.ToString();
        }

        /// <summary>
        /// Appends <paramref name="s"/> as a properly-escaped JSON string
        /// (including the leading/trailing quote chars). Handles backslash,
        /// quote, the four named whitespace escapes, AND every C0 control
        /// character via \u00XX so the output passes strict RFC 8259
        /// parsers. Shared by key and value paths.
        /// </summary>
        /// <summary>
        /// Truncates to at most <paramref name="maxChars"/> chars without
        /// landing in the middle of a UTF-16 surrogate pair. If the char at
        /// position <c>maxChars-1</c> is a high surrogate, back up by one.
        /// </summary>
        private static string SafeTruncate(string s, int maxChars)
        {
            if (s.Length <= maxChars) return s;
            int cut = maxChars;
            if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
            return s.Substring(0, cut);
        }

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
