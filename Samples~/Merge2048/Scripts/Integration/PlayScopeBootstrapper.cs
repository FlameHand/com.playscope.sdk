using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Merge2048.Presentation;
using PlayScopeSdk;

namespace Merge2048.Integration
{
    public sealed class PlayScopeBootstrapper : MonoBehaviour
    {
        private const string GAME_SCENE_NAME = "Merge2048_Game";

        // Two-phase boot progress blended into one continuous bar: warmup fills
        // 0..WARMUP_PROGRESS_WEIGHT, the scene load fills the rest.
        private const float WARMUP_PROGRESS_WEIGHT = 0.5f;

        // SceneManager.LoadSceneAsync().progress caps at 0.9 until activation —
        // rescale so the bar still reaches 1.0 when the load actually completes.
        private const float SCENE_LOAD_UNITY_PROGRESS_CAP = 0.9f;

        // Demonstrates the runtime-config path from the integration guide
        // (PlayScopeContext) as an alternative to the default Resources-asset path.
        [SerializeField] private bool _useExplicitContext = false;

        private readonly CancellationTokenSource _destroyCts = new CancellationTokenSource();
        private BootScreenView _bootScreenView;
        private ConsentDialogView _consentDialogView;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            if (ConsentGate.HasDecision)
            {
                ProceedAfterConsent(ConsentGate.IsGranted);
                return;
            }

            _consentDialogView = BuildConsentDialogView();
            _consentDialogView.DecisionMade += OnConsentDecisionMade;
        }

        private void OnDestroy()
        {
            _destroyCts.Cancel();
            _destroyCts.Dispose();
        }

        private ConsentDialogView BuildConsentDialogView()
        {
            var go = new GameObject("ConsentDialogView", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            return go.AddComponent<ConsentDialogView>();
        }

        private void OnConsentDecisionMade(bool granted)
        {
            _consentDialogView.DecisionMade -= OnConsentDecisionMade;

            if (granted)
            {
                ConsentGate.Grant();
            }
            else
            {
                ConsentGate.Decline();
            }

            Destroy(_consentDialogView.gameObject);
            _consentDialogView = null;

            ProceedAfterConsent(granted);
        }

        private void ProceedAfterConsent(bool granted)
        {
            if (granted)
            {
                InitializeSdk();
            }
            else
            {
                Debug.LogWarning("[Merge2048] Telemetry consent declined — PlayScope stays disabled (no-op) for this session.");
            }

            Debug.Log($"[Merge2048] PlayScope state — IsInitialized={PlayScope.IsInitialized}, IsDisabled={PlayScope.IsDisabled}");

            _bootScreenView = BuildBootScreenView();
            _bootScreenView.SetProgress(0f);

            StartCoroutine(RunWarmupThenLoadScene());
        }

        private void InitializeSdk()
        {
            if (_useExplicitContext)
            {
                var settings = PlayScopeSettings.Load();
                var context = new PlayScopeContext
                {
                    SdkKey = settings != null ? settings.SdkKey : string.Empty,
                    AutoCaptureUnityLogs = settings != null && settings.AutoCaptureUnityLogs,
                    AutoCaptureMinLevel = settings != null ? settings.MinLogLevel : LogLevel.Warning,
                    Metadata = new Dictionary<string, object>
                    {
                        ["environment"] = "development",
                        ["build_number"] = Application.version,
                    },
                };
                PlayScope.Initialize(context);
            }
            else
            {
                PlayScope.Initialize();
            }

            AnalyticsFeed.Publish("Initialize");

            PlayScope.SetUserData(AnonymousPlayerId.GetOrCreate(), new Dictionary<string, object>
            {
                ["is_guest"] = true,
            });
            PlayScope.UpdateSessionData(new Dictionary<string, object>
            {
                ["board_size"] = Merge2048.Core.Board.SIZE * Merge2048.Core.Board.SIZE,
            }, "boot_complete");
        }

        private BootScreenView BuildBootScreenView()
        {
            var go = new GameObject("BootScreenView", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            return go.AddComponent<BootScreenView>();
        }

        private IEnumerator RunWarmupThenLoadScene()
        {
            string warmupOperationId = PlayScope.StartResourceLoad("content_warmup");
            var warmup = new FakeContentWarmup();

            yield return warmup.Run(progress01 =>
            {
                PlayScope.RecordSceneLoadProgress(warmupOperationId, progress01);

                if (_bootScreenView != null)
                {
                    _bootScreenView.SetProgress(progress01 * WARMUP_PROGRESS_WEIGHT);
                }
            });

            PlayScope.EndResourceLoad(warmupOperationId, OperationCompletionStatus.Success);

            LoadGameSceneAsync();
        }

        private async void LoadGameSceneAsync()
        {
            var operation = SceneManager.LoadSceneAsync(GAME_SCENE_NAME);
            string operationId = PlayScope.StartSceneLoad(GAME_SCENE_NAME, operation);
            AnalyticsFeed.Publish("StartSceneLoad");
            var cancellationToken = _destroyCts.Token;

            while (operation != null && !operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (_bootScreenView != null)
                {
                    float sceneProgress01 = Mathf.Clamp01(operation.progress / SCENE_LOAD_UNITY_PROGRESS_CAP);
                    _bootScreenView.SetProgress(WARMUP_PROGRESS_WEIGHT + (sceneProgress01 * WARMUP_PROGRESS_WEIGHT));
                }

                await Task.Yield();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            PlayScope.EndSceneLoad(operationId, OperationCompletionStatus.Success);
            AnalyticsFeed.Publish("EndSceneLoad");

            // BootScreenView is owned by this DontDestroyOnLoad object, so it would
            // otherwise persist over the Game scene's own ScreenFlow UI forever.
            if (_bootScreenView != null)
            {
                Destroy(_bootScreenView.gameObject);
                _bootScreenView = null;
            }
        }
    }
}
