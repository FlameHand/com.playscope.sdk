using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace PlayScopeSdk.Internal
{
    internal sealed class EventQueue
    {
        private readonly ConcurrentQueue<EventRecord> _queue = new();
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

        internal void Enqueue(EventRecord record)
        {
            _queue.Enqueue(record);
            _signal.Release(1);
        }

        internal bool TryDequeue(out EventRecord record) => _queue.TryDequeue(out record);

        internal async UniTask WaitAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);
        }

        internal int Count => _queue.Count;

        internal void DrainAll(List<EventRecord> target)
        {
            while (_queue.TryDequeue(out var r)) target.Add(r);
        }
    }
}
