using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    // record_type values
    internal enum RecordType { Event, Log, Metric }

    // Indicates whether this record triggers critical flush
    internal static class CriticalRecords
    {
        internal static bool IsCritical(string eventType) =>
            eventType is "session_end" or "session_abnormal_end" or "error" or "exception";
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
        internal string? ScreenName;
        internal string? ActionName;
        internal string? OperationId;
        internal string? OperationType;
        internal string? MetadataJson;        // pre-serialized JSON string or null
        internal string? StatePatchJson;      // for state events
        // For logs:
        internal string? Level;
        internal string? Message;
        internal string? StackTrace;
        // For metrics:
        internal string? MetricType;
        internal double MetricValue;
        // Critical flag
        internal bool IsCritical;
    }
}
