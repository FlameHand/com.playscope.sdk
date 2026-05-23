using System;
using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Parallel to <see cref="StatePatchCoalescer"/> but for the
    /// <c>session_data_*</c> stream: device / environment / addressables /
    /// disk / memory snapshots that accumulate over the session lifetime
    /// from many different call sites.
    ///
    /// <para>
    /// Why separate from StatePatchCoalescer:
    /// <list type="bullet">
    /// <item>Different semantics — profile state is the player's data model,
    ///       session data is the runtime environment. Mixing them in one
    ///       buffer would let an environment patch overwrite a gameplay key.</item>
    /// <item>Different cadence — profile state can fire per-frame and needs
    ///       a tight 100 ms window. Session data trickles in over seconds
    ///       (Addressables init, periodic disk samples, network change
    ///       callbacks). A wider 1 s window folds a bursty init phase into
    ///       a single row without making per-second sampling laggy.</item>
    /// <item>Different first-emit semantics — the very first push becomes
    ///       <c>session_data_initial</c> (the dashboard treats it as the
    ///       snapshot baseline); subsequent pushes become
    ///       <c>session_data_patch</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class SessionDataCoalescer
    {
        // 1s window — wide enough to fold init / addressables-loaded /
        // first-disk-sample bursts, narrow enough that a single field update
        // is visible on the dashboard within ~a frame at 1 fps zoom.
        private const int WindowMs = 1000;

        private readonly object _gate = new();
        private Dictionary<string, object>? _buffer;
        private string? _bufferReason;
        // Stopwatch-derived monotonic milliseconds. Replaces the old
        // Environment.TickCount (int, wraps after 49.7 days) so a long-
        // running kiosk / TV install doesn't silently disable the
        // time-trigger flush when the counter wraps and `now - start`
        // goes negative.
        private long _bufferStartTick;

        private static long StopwatchMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000L
            / System.Diagnostics.Stopwatch.Frequency;
        // Flips after the first successful flush — drives the
        // session_data_initial → session_data_patch event type choice.
        private bool _initialEmitted;

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
        /// Resets the coalescer for a new session. Drops any buffered patch
        /// (the caller is expected to FlushNow first if the old session's
        /// events must survive) and clears _initialEmitted so the next
        /// session's first flush emits session_data_initial. Called from
        /// PlayScopeRuntime.InitializeLocked alongside SequenceCounter.Reset()
        /// — without it, post-rotation sessions emit session_data_patch
        /// against a non-existent baseline and the dashboard's state view
        /// is corrupt.
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
                // First flush emits as session_data_initial — the dashboard
                // treats that as the snapshot baseline. Everything after is a
                // patch on top, same protocol as state_initial / state_patch.
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
