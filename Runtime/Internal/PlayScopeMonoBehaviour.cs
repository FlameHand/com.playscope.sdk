using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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
            // Background-timeout session rotation lands here on the tick AFTER
            // OnApplicationPause(false) set the flag. PerformRotation destroys
            // this very GameObject as part of teardown, so we MUST `return`
            // immediately after — any further code in this Update() would run
            // against a half-destroyed sampler / pipeline.
            if (PlayScopeRuntime.ConsumePendingRotation())
            {
                PlayScopeRuntime.PerformRotation();
                return;
            }

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

        /// <summary>
        /// Forwards to MetricsSampler.ResetForNewSession() if the sampler exists.
        /// Sampler is lazily created in Update(), so reset is a no-op when called
        /// before the first frame after init — that's fine, the lazy-created
        /// sampler gets fresh sentinels from field initializers anyway.
        /// </summary>
        internal void ResetSamplerForNewSession()
        {
            _sampler?.ResetForNewSession();
        }

        // First-input poll. Branches on Unity's input-backend defines so we
        // work under either the legacy Input Manager, the new Input System,
        // or the "Both" mode. Touch wins over mouse wins over key wins over
        // gamepad button — keeps the input_kind label stable when more than
        // one source fires on the same frame (e.g. mouse-down also registers
        // a touch on some emulators).
        private static void PollFirstInputLatency()
        {
            // Cheap fast-path: bail before touching any Input API at all
            // once the event has already fired.
            if (!PlayScopeRuntime.IsInitialized) return;
            if (PlayScopeRuntime.HasEmittedFirstInputLatency) return;

            string kind = null;

#if ENABLE_LEGACY_INPUT_MANAGER
            kind = PollLegacyInput();
#endif
#if ENABLE_INPUT_SYSTEM
            if (kind == null) kind = PollNewInputSystem();
#endif

            if (kind != null)
                PlayScopeRuntime.EmitFirstInputLatencyIfFired(kind);
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        private static string PollLegacyInput()
        {
            try
            {
                if (Input.touchCount > 0) return "touch";
            }
            catch { /* some platforms throw if touch is disabled */ }

            if (Input.GetMouseButtonDown(0) ||
                Input.GetMouseButtonDown(1) ||
                Input.GetMouseButtonDown(2))
                return "mouse";

            if (Input.anyKeyDown) return "key_or_gamepad";

            return null;
        }
#endif

#if ENABLE_INPUT_SYSTEM
        private static string PollNewInputSystem()
        {
            // Touch — Touchscreen.current is null on non-touch devices.
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.wasPressedThisFrame)
                return "touch";

            // Mouse — any of the three standard buttons going from up→down.
            var mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame ||
                                  mouse.rightButton.wasPressedThisFrame ||
                                  mouse.middleButton.wasPressedThisFrame))
                return "mouse";

            // Keyboard — anyKey edge.
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame)
                return "key_or_gamepad";

            // Gamepad — walk a handful of common buttons. Cheap and
            // covers the controllers that report through Gamepad.current.
            var pad = Gamepad.current;
            if (pad != null && (pad.buttonSouth.wasPressedThisFrame ||
                                pad.buttonNorth.wasPressedThisFrame ||
                                pad.buttonEast.wasPressedThisFrame ||
                                pad.buttonWest.wasPressedThisFrame ||
                                pad.startButton.wasPressedThisFrame ||
                                pad.selectButton.wasPressedThisFrame ||
                                pad.leftShoulder.wasPressedThisFrame ||
                                pad.rightShoulder.wasPressedThisFrame))
                return "key_or_gamepad";

            return null;
        }
#endif

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
