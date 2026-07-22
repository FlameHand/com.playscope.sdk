using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Main-thread ANR watchdog. A threadpool <see cref="System.Threading.Timer"/>
    /// (independent of the main thread, so it runs while Update() is blocked)
    /// compares now against the last heartbeat written by
    /// <see cref="RecordHeartbeat"/> every frame; exceeding <c>thresholdMs</c>
    /// emits <c>anr</c> once, and the next heartbeat emits <c>anr_recovered</c>
    /// with the total stuck duration. Time.realtimeSinceStartup can't be used —
    /// it's a main-thread-only read, and the point is to observe from outside.
    /// <see cref="Suspend"/> stops sampling while backgrounded (else every
    /// backgrounded app reports ANR); <see cref="Resume"/> re-arms + refreshes
    /// the heartbeat so the first 500 ms post-resume don't false-positive.
    /// </summary>
    internal sealed class AnrWatchdog
    {
        // 500 ms (not 1 s): at threshold 2000ms a 1 s poll observes the stall
        // anywhere in 2.0–3.0 s; 500 ms tightens stuck_for_ms accuracy to 2.0–2.5 s.
        private const int PeriodMs = 500;

        private readonly int _thresholdMs;
        private readonly EventPipeline _pipeline;

        // Two slots, both written from the main-thread Update tick.
        // _heartbeatStopwatchTicks drives ALL elapsed arithmetic — Stopwatch is
        // monotonic, so an NTP/DST/manual clock change can't suppress detection
        // (negative elapsed) or fake an ANR (forward jump). _heartbeatWallTicks
        // only stamps human-readable started_at / recovered_at.
        // Interlocked.Read/Exchange for 32-bit torn-read safety.
        private long _heartbeatStopwatchTicks;
        private long _heartbeatWallTicks;

        private Timer _timer;

        private volatile bool _inAnr;
        private volatile bool _suspended;
        private long _stallStartStopwatchTicks;
        private long _stallStartWallTicks;
        // Guards against the timer firing a second time mid-stall.
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

        /// <summary>Updates the heartbeat to now. Called every frame on the main thread.</summary>
        internal void RecordHeartbeat()
        {
            Interlocked.Exchange(ref _heartbeatStopwatchTicks, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _heartbeatWallTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>Pause checking on background — else the frozen Update() reads as a multi-minute ANR.</summary>
        internal void Suspend() => _suspended = true;

        /// <summary>
        /// Resume checking, refreshing the heartbeat first so the gap before the
        /// first post-foreground Update() isn't reported as a freeze.
        /// </summary>
        internal void Resume()
        {
            Interlocked.Exchange(ref _heartbeatStopwatchTicks, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _heartbeatWallTicks, DateTime.UtcNow.Ticks);
            _suspended = false;
        }

        // Timer callback on a threadpool worker. Must not touch any
        // UnityEngine API — we're not on the main thread.
        private void TimerTick(object state)
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
                    // Main thread recovered (a fresh heartbeat landed). Emit recovery, reset latches.
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
