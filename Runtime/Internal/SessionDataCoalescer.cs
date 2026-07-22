using System;
using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Parallel to <see cref="StatePatchCoalescer"/> but for the
    /// <c>session_data_*</c> stream (device / environment / disk / memory).
    /// Separate buffer so an environment patch can't overwrite a gameplay key;
    /// wider 1 s window (vs 100 ms) because session data trickles in over seconds.
    /// First push emits <c>session_data_initial</c> (dashboard baseline),
    /// subsequent pushes <c>session_data_patch</c>.
    /// </summary>
    internal sealed class SessionDataCoalescer
    {
        // 1s window — folds the init burst, still visible within ~a frame.
        private const int WindowMs = 1000;

        private readonly object _gate = new();
        private Dictionary<string, object> _buffer;
        private string _bufferReason;
        // Stopwatch-monotonic ms — Environment.TickCount (int) wraps after 49.7
        // days, which on a kiosk/TV install would go negative and disable the flush.
        private long _bufferStartTick;

        private static long StopwatchMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L
            / System.Diagnostics.Stopwatch.Frequency;
        // Drives the session_data_initial → session_data_patch choice.
        private bool _initialEmitted;

        internal void Add(IReadOnlyDictionary<string, object> patch, string reason)
        {
            lock (_gate)
            {
                if (_buffer != null && !string.Equals(_bufferReason, reason, StringComparison.Ordinal))
                {
                    FlushLocked();
                }
                if (_buffer == null)
                {
                    _buffer = new Dictionary<string, object>();
                    _bufferReason = reason;
                    _bufferStartTick = StopwatchMs();
                }
                if (patch != null)
                {
                    foreach (var kv in patch) _buffer[kv.Key] = kv.Value;
                }
            }
        }

        internal void TickAndMaybeFlush()
        {
            lock (_gate)
            {
                if (_buffer == null) return;
                var age = StopwatchMs() - _bufferStartTick;
                if (age >= WindowMs) FlushLocked();
            }
        }

        internal void FlushNow()
        {
            lock (_gate)
            {
                FlushLocked();
            }
        }

        /// <summary>
        /// Resets for a new session — drops the buffer (FlushNow first if the old
        /// events must survive) and clears _initialEmitted so the next flush is
        /// session_data_initial. Without it, post-rotation sessions patch against
        /// a non-existent baseline and the dashboard state view is corrupt.
        /// </summary>
        internal void ResetForNewSession()
        {
            lock (_gate)
            {
                _buffer = null;
                _bufferReason = null;
                _initialEmitted = false;
            }
        }

        private void FlushLocked()
        {
            if (_buffer == null || _buffer.Count == 0)
            {
                _buffer = null;
                _bufferReason = null;
                return;
            }
            if (!string.IsNullOrEmpty(_bufferReason))
            {
                _buffer["_reason"] = _bufferReason;
            }
            try
            {
                var eventType = _initialEmitted ? "session_data_patch" : "session_data_initial";
                _initialEmitted = true;
                PlayScopeRuntime.Pipeline?.EnqueueEvent(eventType,
                    statePatchJson: EventPipeline.DictToJson(_buffer));
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("SessionDataCoalescer flush failed", ex);
            }
            _buffer = null;
            _bufferReason = null;
        }
    }
}
