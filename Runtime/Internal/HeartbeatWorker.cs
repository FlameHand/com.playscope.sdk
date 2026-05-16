using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using PlayScopeSdk.Core.Session;

namespace PlayScopeSdk.Internal
{
    internal sealed class HeartbeatWorker
    {
        private CancellationTokenSource? _cts;

        internal void Start()
        {
            _cts = new CancellationTokenSource();
            RunAsync(_cts.Token).Forget();
        }

        internal void Stop() => _cts?.Cancel();

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await UniTask.Delay(TimeSpan.FromSeconds(30), cancellationToken: ct); }
                catch (OperationCanceledException) { break; }
                try { SessionFiles.UpdateHeartbeat(); }
                catch { /* file IO — swallow */ }
            }
        }
    }
}
