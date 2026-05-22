#if UNITY_EDITOR
using UnityEditor;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Editor-only safety net for "user clicked Stop in Play Mode" — that
    /// flow does NOT reliably trigger <c>OnApplicationQuit</c> or
    /// <c>Application.quitting</c> in Unity Editor (despite the docs
    /// claiming the latter does). Without this, every Editor Play+Stop
    /// cycle leaves a stale session.lock + un-finalised chunk_current.jsonl
    /// on disk; the next launch's SessionRecovery is forced to invent a
    /// synthetic <c>session_abnormal_end</c> for what was actually a
    /// clean exit, and the dashboard mis-reports every dev's session as
    /// "outgoing" or abnormal.
    ///
    /// <para>
    /// EditorApplication.playModeStateChanged is the one signal Unity
    /// guarantees on play-mode transitions in the Editor. Subscribing
    /// from an InitializeOnLoad static ctor wires it up once at Editor
    /// startup; the hook re-fires for every play session for free.
    /// </para>
    ///
    /// <para>
    /// PlayScopeRuntime.Shutdown is idempotent (gated on _initialized),
    /// so it's safe even if Application.quitting also fires on this
    /// transition — first call wins, the rest no-op. The defensive
    /// strategy mirrors PerformBuild's "always run a final drain" — we
    /// pay a single function call to be certain.
    /// </para>
    ///
    /// <para>
    /// Editor-only by file-level #if. Stripped from player builds.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayScopeEditorPlayModeHook
    {
        static PlayScopeEditorPlayModeHook()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // We need ExitingPlayMode, not ExitingEditMode or EnteringEditMode.
            //
            //   EnteringPlayMode  — user just clicked Play
            //   ExitingEditMode   — Editor is about to swap into play
            //   ExitingPlayMode   — user clicked Stop, runtime is about to die ← THIS
            //   EnteringEditMode  — back in edit mode, runtime already dead
            //
            // Shutdown HAS to run on ExitingPlayMode rather than EnteringEditMode
            // because by EnteringEditMode the MonoBehaviour driver and writer
            // thread are already gone — we'd be calling Shutdown on a half-dead
            // SDK that can't actually flush anything.
            if (state != PlayModeStateChange.ExitingPlayMode) return;

            try
            {
                if (PlayScopeRuntime.IsInitialized && !PlayScopeRuntime.IsDisabled)
                {
                    PlayScopeRuntime.Shutdown();
                }
            }
            catch (System.Exception ex)
            {
                // Never let an Editor-side hook throw — would block the
                // Editor's play-mode transition itself.
                PlayScopeLog.Warning("PlayScopeEditorPlayModeHook: Shutdown threw", ex);
            }
        }
    }
}
#endif
