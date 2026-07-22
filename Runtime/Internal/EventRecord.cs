using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    // record_type values
    internal enum RecordType { Event, Log, Metric }

    // Critical records finalize the current chunk immediately (others flush at
    // the size/time threshold) so the signal reaches the server even if the
    // process dies seconds later. anr / memory_warning are critical for the same
    // reason as errors: the app may be killed before any follow-up event arrives.
    internal static class CriticalRecords
    {
        internal static bool IsCritical(string eventType) =>
            eventType is "session_end" or "session_abnormal_end"
                or "error" or "exception"
                or "anr" or "anr_recovered"
                or "memory_warning";
        internal static bool IsLogCritical(string level) =>
            level is "error" or "exception";
    }

    internal sealed class EventRecord
    {
        internal RecordType RecordType;
        internal string EventType = "";       // for record_type=event
        internal string EventId = "";
        internal long SequenceNum;
        internal string Timestamp = "";       // ISO 8601 UTC
        internal string ScreenName;
        internal string ActionName;
        internal string OperationId;
        internal string OperationType;
        internal string MetadataJson;        // pre-serialized JSON string or null
        internal string StatePatchJson;      // for state events
        // For logs:
        internal string Level;
        internal string Message;
        internal string StackTrace;
        // For metrics:
        internal string MetricType;
        internal double MetricValue;
        // Critical flag
        internal bool IsCritical;
    }
}
