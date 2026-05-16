using UnityEngine;

namespace PlayScopeSdk.Internal
{
    internal sealed class PlayScopeMonoBehaviour : MonoBehaviour
    {
        private MetricsSampler? _sampler;

        private void Awake()
        {
            Application.focusChanged += OnFocusChanged;
        }

        private void Update()
        {
            if (PlayScopeRuntime.Pipeline != null && _sampler == null)
                _sampler = new MetricsSampler(PlayScopeRuntime.Pipeline);
            _sampler?.Tick();
        }

        private void OnApplicationPause(bool isPaused)
        {
            PlayScopeRuntime.FlushOnPause();
            if (isPaused)
                PlayScopeRuntime.Pipeline?.EnqueueEvent("lifecycle",
                    metadataJson: "{\"transition\":\"background_start\"}");
            else
                PlayScopeRuntime.Pipeline?.EnqueueEvent("lifecycle",
                    metadataJson: "{\"transition\":\"foreground\"}");
        }

        private void OnApplicationQuit()
        {
            PlayScopeRuntime.Shutdown();
        }

        private void OnFocusChanged(bool hasFocus)
        {
            PlayScopeRuntime.Pipeline?.EnqueueEvent("lifecycle",
                metadataJson: hasFocus
                    ? "{\"transition\":\"foreground\"}"
                    : "{\"transition\":\"background_start\"}");
        }

        private void OnDestroy()
        {
            Application.focusChanged -= OnFocusChanged;
        }
    }
}
