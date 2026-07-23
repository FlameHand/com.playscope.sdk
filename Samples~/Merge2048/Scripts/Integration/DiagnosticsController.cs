using System.Text;
using System.Threading;
using UnityEngine;
using Merge2048.App;
using Merge2048.Presentation;
using PlayScopeSdk;

namespace Merge2048.Integration
{
    // Owns the dev-overlay wiring and is the only place here allowed to call PlayScope.*
    // for it — DiagnosticsPanelView / AnalyticsFeedView (Presentation) stay PlayScope-free.
    public sealed class DiagnosticsController : MonoBehaviour
    {
        private const int SPAM_LOG_COUNT = 20;
        private const int SIMULATE_ANR_MS = 3000;

        private ScreenFlow _screenFlow;
        private DiagnosticsPanelView _panel;
        private AnalyticsFeedView _feedView;

        public void Initialize(ScreenFlow screenFlow)
        {
            _screenFlow = screenFlow;

            var panelGo = new GameObject("DiagnosticsPanelView", typeof(RectTransform));
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.AddComponent<DiagnosticsPanelView>();
            _panel.SimulateAnrClicked += OnSimulateAnrClicked;
            _panel.SpamLogClicked += OnSpamLogClicked;
            _panel.SendPiiLogClicked += OnSendPiiLogClicked;
            _panel.ResetClicked += OnResetClicked;
            _panel.ToggleFeedClicked += OnToggleFeedClicked;

            var feedGo = new GameObject("AnalyticsFeedView", typeof(RectTransform));
            feedGo.transform.SetParent(transform, false);
            _feedView = feedGo.AddComponent<AnalyticsFeedView>();

            if (_screenFlow != null)
            {
                _screenFlow.ScreenChanged += OnScreenChanged;
                _panel.SetGearVisible(_screenFlow.Current == ScreenId.MainMenu);
            }

            RefreshStatus();
        }

        private void OnDestroy()
        {
            if (_screenFlow != null)
            {
                _screenFlow.ScreenChanged -= OnScreenChanged;
            }

            if (_panel != null)
            {
                _panel.SimulateAnrClicked -= OnSimulateAnrClicked;
                _panel.SpamLogClicked -= OnSpamLogClicked;
                _panel.SendPiiLogClicked -= OnSendPiiLogClicked;
                _panel.ResetClicked -= OnResetClicked;
                _panel.ToggleFeedClicked -= OnToggleFeedClicked;
            }
        }

        private void OnScreenChanged(ScreenId screen)
        {
            _panel.SetGearVisible(screen == ScreenId.MainMenu);
        }

        private void OnSimulateAnrClicked()
        {
            // The ANR watchdog is auto-disabled in the Editor (breakpoints produce false
            // positives), so this only produces an anr/anr_recovered pair in real builds
            // or batch mode — in the Editor it just blocks the main thread for a moment.
            Thread.Sleep(SIMULATE_ANR_MS);
        }

        private void OnSpamLogClicked()
        {
            for (int i = 0; i < SPAM_LOG_COUNT; i++)
            {
                PlayScope.TrackLog(LogLevel.Info, "Diagnostics: repeated log line");
            }

            AnalyticsFeed.Publish("TrackLog ×20 (dedup)");
        }

        private void OnSendPiiLogClicked()
        {
            PlayScope.TrackLog(LogLevel.Info, "contact me: foo@bar.com");
            AnalyticsFeed.Publish("TrackLog: PII sample (masked server-side)");
        }

        private void OnResetClicked()
        {
            ConsentGate.Reset();
            SaveDataStore.Clear();
            AnalyticsFeed.Publish("Reset consent & save");
            RefreshStatus();
        }

        private void OnToggleFeedClicked()
        {
            if (_feedView != null)
            {
                _feedView.gameObject.SetActive(!_feedView.gameObject.activeSelf);
            }
        }

        private void RefreshStatus()
        {
            if (_panel == null)
            {
                return;
            }

            var settings = PlayScope.Settings;
            var builder = new StringBuilder();
            builder.AppendLine($"IsInitialized: {PlayScope.IsInitialized}");
            builder.AppendLine($"IsDisabled: {PlayScope.IsDisabled}");
            builder.AppendLine($"MinLogLevel: {(settings != null ? settings.MinLogLevel.ToString() : "n/a")}");

            if (settings == null || string.IsNullOrEmpty(settings.SdkKey))
            {
                builder.Append("No SDK key — create Resources/PlayScopeSettings.asset via PlayScope ▸ Settings and paste your ps_live_… key");
            }

            _panel.SetStatus(builder.ToString());
        }
    }
}
