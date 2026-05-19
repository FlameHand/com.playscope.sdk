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
            if (PlayScopeRuntime.Pipeline == null)
                _sampler = null;
            _sampler?.Tick();
            // Drive both coalescers' window timers on the same frame pulse —
            // cheap, no worker thread, deterministic with Unity's main loop.
            PlayScopeRuntime.StatePatchCoalescer.TickAndMaybeFlush();
            PlayScopeRuntime.SessionDataCoalescer.TickAndMaybeFlush();
        }

        private void OnApplicationPause(bool isPaused)
        {
            PlayScopeRuntime.FlushOnPause();
            PlayScopeRuntime.RecordLifecycle(isPaused
                ? LifecycleTransition.BackgroundStart
                : LifecycleTransition.Foreground);
        }

        private void OnApplicationQuit()
        {
            PlayScopeRuntime.Shutdown();
            _sampler = null;
        }

        private void OnFocusChanged(bool hasFocus)
        {
            if (!hasFocus)
                PlayScopeRuntime.FlushOnPause();
            PlayScopeRuntime.RecordLifecycle(hasFocus
                ? LifecycleTransition.Foreground
                : LifecycleTransition.BackgroundStart);
        }

        private void OnDestroy()
        {
            Application.focusChanged -= OnFocusChanged;
        }
    }
}
