using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    // record_type values
    internal enum RecordType { Event, Log, Metric }

    // Indicates whether this record triggers critical flush. Critical records
    // finalize the current chunk immediately (WriterWorker calls
    // OnCriticalChunkFinalized → instant upload) so the signal lands
    // server-side even if the process dies seconds later. anr/anr_recovered
    // are critical for the same reason errors are: if the OS kills the app
    // during the freeze the anr_recovered event never comes and we want at
    // least the anr entry event already on the wire.
    internal static class CriticalRecords
    {
        internal static bool IsCritical(string eventType) =>
            eventType is "session_end" or "session_abnormal_end"
                or "error" or "exception"
                or "anr" or "anr_recovered";
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
