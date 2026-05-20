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
            // First Update() after Initialize == "the player can see the game now".
            // Runtime handles the once-only guard internally so a transient
            // Pipeline==null window can't cause us to double-emit.
            PlayScopeRuntime.EmitFirstFrameRenderedOnce();
            // After first-frame, poll once per frame for any input — touch,
            // mouse click, keyboard, gamepad. The runtime helper is a no-op
            // fast-path on subsequent calls once the input event has fired.
            // Anti-flake: skip in Editor batch mode where there's literally
            // no input device — keeps the once-per-frame check cheap and
            // doesn't strand a never-emitted sample in the dashboard.
            PollFirstInputLatency();
            // ANR watchdog heartbeat — null-conditional skips when the
            // watchdog is disabled (Editor or opted-out via context).
            PlayScopeRuntime.AnrWatchdog?.RecordHeartbeat();
            _sampler?.Tick();
            // Drive both coalescers' window timers on the same frame pulse —
            // cheap, no worker thread, deterministic with Unity's main loop.
            PlayScopeRuntime.StatePatchCoalescer.TickAndMaybeFlush();
            PlayScopeRuntime.SessionDataCoalescer.TickAndMaybeFlush();
            // Drive the log dedup buffer's 5 s window from the same pulse.
            // Cheap when the buffer is empty (single lock + Count check),
            // null-safe between Initialize and the first tick where the
            // runtime has finished constructing the buffer.
            PlayScopeRuntime.LogDedupBuffer?.TickAndMaybeFlush();
            // Also drive the sceneload progress sampler — see SceneLoadProgressTracker.
            SceneLoadProgressTracker.TickAndMaybeSample();
        }

        // First-input poll. Walks Unity's legacy Input API (works on both the
        // old Input Manager AND the New Input System when the latter is
        // configured to forward to legacy — Unity's default backwards-
        // compatibility shim). Touch wins over mouse wins over key wins over
        // gamepad button — keeps the input_kind label stable when more than
        // one source fires on the same frame (e.g. mouse-down also registers
        // a touch on some emulators).
        private static void PollFirstInputLatency()
        {
            // Cheap fast-path: bail before touching the Input API at all
            // once the event has already fired. _firstInputEmitted is
            // module-private so we go through the runtime helper which
            // does its own guard — but we still want a public guard here
            // to avoid the per-frame Input.anyKey hit forever.
            if (!PlayScopeRuntime.IsInitialized) return;
            if (PlayScopeRuntime.HasEmittedFirstInputLatency) return;

            string kind = null;
            // Touch — Application.isMobilePlatform avoids a Touch API call
            // on platforms where it's deprecated / not wired. touchCount is
            // 0 on non-touch devices so this is also self-guarding.
            try
            {
                if (Input.touchCount > 0) kind = "touch";
            }
            catch { /* some platforms throw if touch is disabled */ }

            if (kind == null)
            {
                // Mouse — Input.GetMouseButtonDown(0/1/2). Cheap; works on
                // every Standalone target.
                if (Input.GetMouseButtonDown(0) ||
                    Input.GetMouseButtonDown(1) ||
                    Input.GetMouseButtonDown(2))
                    kind = "mouse";
            }
            if (kind == null)
            {
                // Any key (keyboard or gamepad button). anyKeyDown is the
                // narrower edge-triggered variant — we want only the moment
                // a key was pressed, not the whole hold-down window.
                if (Input.anyKeyDown) kind = "key_or_gamepad";
            }

            if (kind != null)
                PlayScopeRuntime.EmitFirstInputLatencyIfFired(kind);
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
