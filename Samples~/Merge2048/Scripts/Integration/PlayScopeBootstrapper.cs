using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using PlayScopeSdk;

namespace Merge2048.Integration
{
    public sealed class PlayScopeBootstrapper : MonoBehaviour
    {
        private const string GAME_SCENE_NAME = "Merge2048_Game";

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

            LoadGameSceneAsync().Forget();
        }

        private async UniTaskVoid LoadGameSceneAsync()
        {
            var operation = SceneManager.LoadSceneAsync(GAME_SCENE_NAME);
            string operationId = PlayScope.StartSceneLoad(GAME_SCENE_NAME, operation);

            while (operation != null && !operation.isDone)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, this.GetCancellationTokenOnDestroy());
            }

            PlayScope.EndSceneLoad(operationId, OperationCompletionStatus.Success);
        }
    }
}
