using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PlayScopeSdk.Internal
{
    internal sealed class EventQueue
    {
        // Soft cap to prevent runaway memory growth if the writer worker stalls
        // (e.g. disk full). On overflow we drop the new event and emit a rate-limited
        // warning (once per minute).
        internal const int SoftCap = 50_000;

        private readonly ConcurrentQueue<EventRecord> _queue = new();
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

        private int _count;
        private long _lastDropWarnTicks; // DateTime.UtcNow.Ticks of last warning
        private int _droppedSinceWarn;   // count of drops since last warn

        internal void Enqueue(EventRecord record)
        {
            if (Volatile.Read(ref _count) >= SoftCap)
            {
                // Critical events (exceptions, session_end, ANR) MUST land
                // even when the queue is full. Otherwise a stalled writer
                // (disk full, AV scanner) drops the session_end record and
                // the dashboard shows EndStatus=unknown until AbandonedSessionWorker
                // stamps it ~10 days later. Override drop policy for IsCritical:
                // accept the new record and grow the queue past SoftCap. The
                // overshoot is bounded by the rate of critical events
                // (kilobytes per session, not megabytes).
                if (!record.IsCritical)
                {
                    Interlocked.Increment(ref _droppedSinceWarn);
                    MaybeWarnDrops();
                    return;
                }
            }

            _queue.Enqueue(record);
            Interlocked.Increment(ref _count);
            _signal.Release(1);
        }

        private void MaybeWarnDrops()
        {
            const long oneMinuteTicks = TimeSpan.TicksPerMinute;
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastTicks = Volatile.Read(ref _lastDropWarnTicks);
            if (nowTicks - lastTicks < oneMinuteTicks) return;
            // Single-writer wins; collapse the dropped counter.
            if (Interlocked.CompareExchange(ref _lastDropWarnTicks, nowTicks, lastTicks) == lastTicks)
            {
                var dropped = Interlocked.Exchange(ref _droppedSinceWarn, 0);
                if (dropped > 0)
                    PlayScopeLog.Warning($"EventQueue soft cap ({SoftCap}) exceeded — dropped {dropped} record(s) in the last minute.");
            }
        }

        internal bool TryDequeue(out EventRecord record)
        {
            if (_queue.TryDequeue(out record))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }
            return false;
        }

        internal async UniTask WaitAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);
        }

        internal int Count => Volatile.Read(ref _count);

        internal void DrainAll(List<EventRecord> target)
        {
            while (_queue.TryDequeue(out var r))
            {
                Interlocked.Decrement(ref _count);
                target.Add(r);
            }
        }
    }
}
