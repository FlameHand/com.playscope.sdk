using System;
using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Buffers <c>state_patch</c> calls inside a small time window and emits
    /// one merged patch per window. Per-frame state churn in games is the
    /// common case — without coalescing a 60 fps loop that calls
    /// <c>UpdateState</c> each frame fires 60 patch events / sec, and a
    /// 5-minute session pumps 18 000 near-identical rows through the
    /// pipeline. After coalescing a session settles into the few hundred
    /// patches that actually matter to a reviewer.
    ///
    /// <para>
    /// Rules:
    /// <list type="bullet">
    /// <item>Same-reason patches in the window merge (last write wins per key).</item>
    /// <item>A patch with a different <c>reason</c> than the buffered one
    ///       flushes the current buffer first, then starts a new one — so the
    ///       dashboard sees a clean row boundary on each new cause.</item>
    /// <item>Null values are preserved (they encode key removal in the patch
    ///       protocol).</item>
    /// <item>Flush is automatic on the next Unity Update() tick after the
    ///       window expires, plus explicit on pause / shutdown / abnormal end.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class StatePatchCoalescer
    {
        // 100ms — short enough that user-visible state changes still feel
        // immediate in the dashboard, long enough to fold a 60 fps update
        // storm into a single row.
        private const int WindowMs = 100;

        private readonly object _gate = new();
        private Dictionary<string, object>? _buffer;
        private string? _bufferReason;
        private int _bufferStartTick;

        /// <summary>
        /// Append a patch to the buffer. Safe from any thread. The window timer
        /// is anchored on the FIRST patch in a given buffer, so a burst of
        /// rapid updates still emits in one ~100ms window rather than sliding
        /// indefinitely.
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
                    _bufferStartTick = Environment.TickCount;
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
                var age = Environment.TickCount - _bufferStartTick;
                if (age >= WindowMs) FlushLocked();
            }
        }

        /// <summary>
        /// Force-flush whatever is buffered right now. Called on session_end,
        /// abnormal_end, and lifecycle pause so the last patch isn't lost to
        /// the window timer when the app is about to die / suspend.
        /// </summary>
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
