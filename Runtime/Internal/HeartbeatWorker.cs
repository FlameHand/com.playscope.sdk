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
                try
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(30), cancellationToken: ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Don't kill the loop on whatever oddity UniTask.Delay might
                    // throw on exotic platforms. Wait a beat and try again —
                    // a stalled heartbeat means next-launch SessionRecovery
                    // would mark every session as abnormal, which is loud
                    // wrong telemetry.
                    PlayScopeLog.Warning("HeartbeatWorker: Delay threw", ex);
                    try { await UniTask.Delay(TimeSpan.FromSeconds(5), cancellationToken: ct); }
                    catch (OperationCanceledException) { break; }
                    catch { /* if even the recovery delay throws, the next iteration will try once more */ }
                }

                try { SessionFiles.UpdateHeartbeat(); }
                catch { /* file IO — swallow */ }
            }
        }
    }
}
