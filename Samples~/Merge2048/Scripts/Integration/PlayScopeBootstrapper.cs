using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using PlayScopeSdk;

namespace Merge2048.Integration
{
    public sealed class PlayScopeBootstrapper : MonoBehaviour
    {
        private const string GAME_SCENE_NAME = "Merge2048_Game";

        private readonly CancellationTokenSource _destroyCts = new CancellationTokenSource();

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            bool consentGranted = ConsentGate.ResolveForSession();

            if (consentGranted)
            {
                PlayScope.Initialize();
                PlayScope.SetUserData(AnonymousPlayerId.GetOrCreate(), new Dictionary<string, object>
                {
                    ["is_guest"] = true,
                });
                PlayScope.UpdateSessionData(new Dictionary<string, object>
                {
                    ["board_size"] = Merge2048.Core.Board.SIZE * Merge2048.Core.Board.SIZE,
                }, "boot_complete");
            }
            else
            {
                Debug.LogWarning("[Merge2048] Telemetry consent declined — PlayScope stays disabled (no-op) for this session.");
            }

            LoadGameSceneAsync();
        }

        private void OnDestroy()
        {
            _destroyCts.Cancel();
            _destroyCts.Dispose();
        }

        private async void LoadGameSceneAsync()
        {
            var operation = SceneManager.LoadSceneAsync(GAME_SCENE_NAME);
            string operationId = PlayScope.StartSceneLoad(GAME_SCENE_NAME, operation);
            var cancellationToken = _destroyCts.Token;

            while (operation != null && !operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await Task.Yield();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            PlayScope.EndSceneLoad(operationId, OperationCompletionStatus.Success);
        }
    }
}
