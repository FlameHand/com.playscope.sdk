using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PlayScopeSdk.Internal
{
    internal sealed class PlayScopeMonoBehaviour : MonoBehaviour
    {
        private MetricsSampler? _sampler;
        // Warn-once for the per-frame backstop — a component faulting every tick
        // would otherwise spam a warning per frame for the rest of the session.
        private bool _updateFaultWarned;

        private void Awake()
        {
            try
            {
                Application.focusChanged += OnFocusChanged;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("PlayScopeMonoBehaviour.Awake failed", ex);
            }
        }

        private void Update()
        {
            try
            {
                // Rotation runs here the tick after the flag was set. PerformRotation
                // destroys this GameObject, so we MUST return immediately — further
                // code would run against a half-destroyed sampler / pipeline.
                if (PlayScopeRuntime.ConsumePendingRotation())
                {
                    PlayScopeRuntime.PerformRotation();
                    return;
                }

                if (PlayScopeRuntime.Pipeline != null && _sampler == null)
                {
                    _sampler = new MetricsSampler(PlayScopeRuntime.Pipeline);
                }
                if (PlayScopeRuntime.Pipeline == null)
                {
                    _sampler = null;
                }
                // First Update == "player can see the game" (runtime guards once-only).
                PlayScopeRuntime.EmitFirstFrameRenderedOnce();
                PollFirstInputLatency();
                // null-conditional skips when the watchdog is disabled (Editor / opted out).
                PlayScopeRuntime.AnrWatchdog?.RecordHeartbeat();
                _sampler?.Tick();
                // Drive all window timers off the same frame pulse — no worker thread.
                PlayScopeRuntime.StatePatchCoalescer.TickAndMaybeFlush();
                PlayScopeRuntime.SessionDataCoalescer.TickAndMaybeFlush();
                PlayScopeRuntime.LogDedupBuffer?.TickAndMaybeFlush();
                SceneLoadProgressTracker.TickAndMaybeSample();
            }
            catch (Exception ex)
            {
                if (!_updateFaultWarned)
                {
                    _updateFaultWarned = true;
                    PlayScopeLog.Warning("PlayScopeMonoBehaviour.Update failed (warn-once per driver lifetime)", ex);
                }
            }
        }

        /// <summary>
        /// Resets the sampler if it exists. No-op before the first frame (the
        /// lazily-created sampler gets fresh sentinels from field initializers).
        /// </summary>
        internal void ResetSamplerForNewSession()
        {
            _sampler?.ResetForNewSession();
        }

        // Branches on Unity's input-backend defines (legacy / new / Both).
        // Precedence touch > mouse > key > gamepad keeps input_kind stable when
        // multiple sources fire the same frame (mouse-down also registers a touch
        // on some emulators).
        private static void PollFirstInputLatency()
        {
            // Fast-path out before touching any Input API once the event fired.
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
            try
            {
                PlayScopeRuntime.FlushOnPause();
                PlayScopeRuntime.RecordLifecycle(isPaused
                    ? LifecycleTransition.BackgroundStart
                    : LifecycleTransition.Foreground);
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("PlayScopeMonoBehaviour.OnApplicationPause failed", ex);
            }
        }

        private void OnApplicationQuit()
        {
            try
            {
                PlayScopeRuntime.Shutdown();
                _sampler = null;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("PlayScopeMonoBehaviour.OnApplicationQuit failed", ex);
            }
        }

        // Application.focusChanged is a multicast delegate shared with the host
        // game — an exception escaping here aborts the game's own subscribers.
        private void OnFocusChanged(bool hasFocus)
        {
            try
            {
                if (!hasFocus)
                {
                    PlayScopeRuntime.FlushOnPause();
                }
                PlayScopeRuntime.RecordLifecycle(hasFocus
                    ? LifecycleTransition.Foreground
                    : LifecycleTransition.BackgroundStart);
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("PlayScopeMonoBehaviour.OnFocusChanged failed", ex);
            }
        }

        private void OnDestroy()
        {
            try
            {
                Application.focusChanged -= OnFocusChanged;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("PlayScopeMonoBehaviour.OnDestroy failed", ex);
            }
        }
    }
}
