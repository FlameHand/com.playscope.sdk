using UnityEngine;

namespace PlayScopeSdk.Internal
{
    internal sealed class PlayScopeMonoBehaviour : MonoBehaviour
    {
        private void Update()
        {
            // FPS + Unity main-thread metric samplers — implemented in PSDK-13
        }

        private void OnApplicationPause(bool isPaused)
        {
            // TODO(PSDK-13): record lifecycle event (BackgroundStart / Foreground)
            // PlayScopeRuntime.RecordLifecycle(isPaused ? LifecycleTransition.BackgroundStart : LifecycleTransition.Foreground);
        }

        private void OnApplicationQuit()
        {
            PlayScopeRuntime.Shutdown();
        }
    }
}
