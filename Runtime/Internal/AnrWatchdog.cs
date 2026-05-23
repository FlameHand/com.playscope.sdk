using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Main-thread watchdog. Detects "Application Not Responding" stalls —
    /// any stretch of time where the Unity main thread fails to update its
    /// heartbeat for longer than <c>thresholdMs</c>.
    ///
    /// <para>
    /// Design:
    /// </para>
    /// <list type="bullet">
    /// <item>A <see cref="System.Threading.Timer"/> ticks on a threadpool
    ///       worker every <c>PeriodMs</c>; the worker is independent of the
    ///       main thread so it keeps running even when Update() is blocked.</item>
    /// <item><see cref="RecordHeartbeat"/> is called by
    ///       <c>PlayScopeMonoBehaviour.Update</c> every frame. It writes the
    ///       current <c>DateTime.UtcNow.Ticks</c> to a volatile long.</item>
    /// <item>The timer callback computes <c>UtcNow - lastHeartbeat</c>. If
    ///       it exceeds <c>thresholdMs</c> we transition into the "in-anr"
    ///       state and emit the <c>anr</c> event once. When the main thread
    ///       resumes and the next heartbeat arrives, we transition out and
    ///       emit <c>anr_recovered</c> with the total stuck duration.</item>
    /// </list>
    ///
    /// <para>
    /// Why not Time.realtimeSinceStartup or similar: those are main-thread-
    /// only reads. The whole point is to observe stalls from OUTSIDE the
    /// main thread.
    /// </para>
    ///
    /// <para>
    /// Background-pause integration: when <see cref="Suspend"/> is called
    /// (from <c>OnApplicationPause(true)</c>), the watchdog stops sampling
    /// — otherwise every backgrounded app would report itself as ANR.
    /// <see cref="Resume"/> on foreground re-arms it AND refreshes the
    /// heartbeat so the first 500 ms post-resume don't false-positive.
    /// </para>
    /// </summary>
    internal sealed class AnrWatchdog
    {
        // Faster check interval than 1 s — at threshold=2000ms a 1-second
        // poll would mean we observe a stall anywhere between 2.0 and 3.0
        // seconds after it begins. 500 ms tightens that to 2.0–2.5 s,
        // which materially improves the reported stuck_for_ms accuracy.
        private const int PeriodMs = 500;

        private readonly int _thresholdMs;
        private readonly EventPipeline _pipeline;

        // Two heartbeat slots, both written from the main-thread Update tick:
        //   * _heartbeatStopwatchTicks — Stopwatch.GetTimestamp() at the moment
        //     of the heartbeat. ALL elapsed-time arithmetic uses this slot —
        //     Stopwatch is hardware-monotonic and immune to NTP rewinds, DST
        //     jumps, manual clock changes. Using DateTime.UtcNow here would
        //     let a 30-second NTP correction during the session either
        //     suppress all ANR detection (negative elapsed) or report a phantom
        //     ANR on a forward jump.
        //   * _heartbeatWallTicks — DateTime.UtcNow.Ticks. Only used to stamp
        //     human-readable `started_at` / `recovered_at` strings in the
        //     emitted event metadata.
        // Interlocked.Read / Exchange on long for 32-bit safety against torn reads.
        private long _heartbeatStopwatchTicks;
        private long _heartbeatWallTicks;

        private Timer? _timer;

        // Latched state — flipped on enter-anr, cleared on recovery.
        // Volatile so the main-thread Resume read sees the latest writer
        // value without a lock.
        private volatile bool _inAnr;
        // Suspend flag — set when the app is backgrounded so the timer
        // callback skips its check entirely.
        private volatile bool _suspended;
        // Stopwatch / wall-clock ticks at the moment we noticed the stall began.
        // Used to stamp anr_recovered's total_stuck_ms (from Stopwatch) and
        // human-readable started_at (from wall clock).
        private long _stallStartStopwatchTicks;
        private long _stallStartWallTicks;
        // Whether we've already emitted the entry-anr event for the current
        // stall — guards against the timer firing a second time mid-stall.
        private bool _entryEventEmitted;

        internal AnrWatchdog(EventPipeline pipeline, int thresholdMs)
        {
            _pipeline = pipeline;
            _thresholdMs = Math.Max(500, thresholdMs); // sanity floor
        }

        /// <summary>
        /// Start the timer. Caller guarantees Pipeline is non-null and the
        /// SDK is fully initialised — the watchdog assumes it can emit
        /// events the moment it observes a stall.
        /// </summary>
        internal void Start()
        {
            Interlocked.Exchange(ref _heartbeatStopwatchTicks, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _heartbeatWallTicks, DateTime.UtcNow.Ticks);
            _timer = new Timer(TimerTick, state: null,
                dueTime: PeriodMs, period: PeriodMs);
        }

        /// <summary>Stops the timer permanently and disposes the underlying resource.</summary>
        internal void Stop()
        {
            try { _timer?.Dispose(); } catch { /* best-effort */ }
            _timer = null;
        }

        /// <summary>
        /// Updates the heartbeat slot to "now". Called every frame from
        /// the MonoBehaviour driver on the main thread. Cheap — one
        /// Interlocked.Exchange and a DateTime read.
        /// </summary>
        internal void RecordHeartbeat()
        {
            Interlocked.Exchange(ref _heartbeatStopwatchTicks, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _heartbeatWallTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Pause checking. Called when the app goes to background — the OS
        /// freezes Update() entirely and we'd otherwise mis-classify the
        /// backgrounded state as a multi-minute ANR.
        /// </summary>
        internal void Suspend() => _suspended = true;

        /// <summary>
        /// Resume checking. Refreshes the heartbeat before un-suspending so
        /// the half-second between Resume and the first post-foreground
        /// Update() tick doesn't get reported as a 500 ms freeze.
        /// </summary>
        internal void Resume()
        {
            Interlocked.Exchange(ref _heartbeatStopwatchTicks, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _heartbeatWallTicks, DateTime.UtcNow.Ticks);
            _suspended = false;
        }

        // Timer callback on a threadpool worker. Must not touch any
        // UnityEngine API — we're not on the main thread.
        private void TimerTick(object? state)
        {
            if (_suspended) return;
            try
            {
                var lastBeatSwTicks = Interlocked.Read(ref _heartbeatStopwatchTicks);
                var lastBeatWallTicks = Interlocked.Read(ref _heartbeatWallTicks);
                var nowSwTicks = Stopwatch.GetTimestamp();
                var elapsedMs = (nowSwTicks - lastBeatSwTicks) * 1000L / Stopwatch.Frequency;
                if (elapsedMs < 0) elapsedMs = 0;

                if (!_inAnr && elapsedMs >= _thresholdMs)
                {
                    _inAnr = true;
                    _stallStartStopwatchTicks = lastBeatSwTicks;
                    _stallStartWallTicks = lastBeatWallTicks;
                    if (!_entryEventEmitted)
                    {
                        _entryEventEmitted = true;
                        EmitAnrEntry(elapsedMs, lastBeatWallTicks);
                    }
                }
                else if (_inAnr && elapsedMs < _thresholdMs)
                {
                    // Main thread recovered — heartbeat slot was updated
                    // since we last looked, which means a Update() tick
                    // landed. Emit recovery and reset latches.
                    var totalStuckMs = (lastBeatSwTicks - _stallStartStopwatchTicks) * 1000L / Stopwatch.Frequency;
                    if (totalStuckMs < 0) totalStuckMs = 0;
                    EmitAnrRecovered(totalStuckMs, _stallStartWallTicks, lastBeatWallTicks);
                    _inAnr = false;
                    _entryEventEmitted = false;
                    _stallStartStopwatchTicks = 0;
                    _stallStartWallTicks = 0;
                }
            }
            catch
            {
                // Never let a watchdog tick throw — the timer would die
                // silently and we'd lose detection for the session.
            }
        }

        private void EmitAnrEntry(long stuckForMs, long startTicks)
        {
            var meta = new Dictionary<string, object>
            {
                ["stuck_for_ms"] = Math.Max(0L, stuckForMs),
                ["started_at"]   = new DateTime(startTicks, DateTimeKind.Utc)
                                       .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["threshold_ms"] = _thresholdMs,
            };
            _pipeline.EnqueueEvent("anr", metadataJson: EventPipeline.DictToJson(meta));
        }

        private void EmitAnrRecovered(long totalStuckMs, long startTicks, long recoveredTicks)
        {
            var meta = new Dictionary<string, object>
            {
                ["total_stuck_ms"] = Math.Max(0L, totalStuckMs),
                ["started_at"]     = new DateTime(startTicks, DateTimeKind.Utc)
                                         .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["recovered_at"]   = new DateTime(recoveredTicks, DateTimeKind.Utc)
                                         .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            };
            _pipeline.EnqueueEvent("anr_recovered", metadataJson: EventPipeline.DictToJson(meta));
        }
    }
}
