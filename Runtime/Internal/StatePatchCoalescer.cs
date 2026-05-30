using System;
using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Buffers <c>state_patch</c> calls in a small window and emits one merged
    /// patch per window — a 60 fps UpdateState loop would otherwise fire 60
    /// rows/sec. Rules: same-reason patches merge (last write wins per key); a
    /// different <c>reason</c> flushes first then starts a new buffer (clean row
    /// boundary per cause); null values are preserved (key removal). Flushes on
    /// the next Update after the window, plus on pause / shutdown.
    /// </summary>
    internal sealed class StatePatchCoalescer
    {
        // 100ms — immediate enough in the dashboard, long enough to fold a 60 fps storm.
        private const int WindowMs = 100;

        private readonly object _gate = new();
        private Dictionary<string, object>? _buffer;
        private string? _bufferReason;
        // Stopwatch-monotonic ms — Environment.TickCount (int) wraps after 49.7 days.
        private long _bufferStartTick;

        private static long StopwatchMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L
            / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// Append a patch (thread-safe). The window is anchored on the FIRST
        /// patch in a buffer, so a burst emits in one ~100ms window, not sliding.
        /// </summary>
        internal void Add(IReadOnlyDictionary<string, object>? patch, string? reason)
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

        /// <summary>
        /// Called every Unity frame from the runtime MonoBehaviour. Flushes
        /// the buffer if the window has elapsed; otherwise no-op.
        /// </summary>
        internal void TickAndMaybeFlush()
        {
            lock (_gate)
            {
                if (_buffer == null) return;
                var age = StopwatchMs() - _bufferStartTick;
                if (age >= WindowMs) FlushLocked();
            }
        }

        /// <summary>
        /// Drops any buffered patch and starts fresh — called on session rotation
        /// so the new session never inherits state from the dead one.
        /// </summary>
        internal void ResetForNewSession()
        {
            lock (_gate)
            {
                _buffer = null;
                _bufferReason = null;
            }
        }

        internal void FlushNow()
        {
            lock (_gate)
            {
                FlushLocked();
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
                PlayScopeRuntime.Pipeline?.EnqueueEvent("state_patch",
                    statePatchJson: EventPipeline.DictToJson(_buffer));
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("StatePatchCoalescer flush failed", ex);
            }
            _buffer = null;
            _bufferReason = null;
        }
    }
}
