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
            // Lifecycle events (BackgroundStart / Foreground) — implemented in PSDK-13
        }
    }
}
