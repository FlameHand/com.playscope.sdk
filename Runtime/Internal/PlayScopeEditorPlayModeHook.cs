#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

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
            // Diagnostic — use Debug.Log so it lands in Editor.log even when
            // PlayScopeLog's min-level is bumped to Warning. Greppable prefix
            // [PlayScope/diag] so we can isolate from the rest of the log
            // stream. Remove these once we've confirmed the hook is wired.
            Debug.Log("[PlayScope/diag] PlayScopeEditorPlayModeHook: static ctor — subscribed to playModeStateChanged.");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Log EVERY transition so we can see the full sequence in Editor.log:
            //   ExitingEditMode → EnteringPlayMode → ExitingPlayMode → EnteringEditMode
            Debug.Log($"[PlayScope/diag] PlayScopeEditorPlayModeHook: state={state} " +
                $"isInitialized={PlayScopeRuntime.IsInitialized} isDisabled={PlayScopeRuntime.IsDisabled}");

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
                    Debug.Log("[PlayScope/diag] PlayScopeEditorPlayModeHook: calling PlayScopeRuntime.Shutdown(drain=10s)...");
                    // 10 s upload-drain budget. We're not under Unity's ~2 s
                    // mobile quit deadline here — the editor will happily wait.
                    // Without this drain, the chunk that WriteCriticalAndFinalizeSync
                    // just produced (containing session_end) cannot ship: the
                    // uploader's async loop relies on UniTask.Yield, which never
                    // resumes once the player loop is in ExitingPlayMode. Result
                    // before this drain: every editor session showed as "ongoing"
                    // in the dashboard until *another* play session triggered
                    // SessionRecovery to ship the previous one's session_end.
                    PlayScopeRuntime.Shutdown(uploadDrainTimeoutMs: 10000);
                    Debug.Log("[PlayScope/diag] PlayScopeEditorPlayModeHook: Shutdown() returned.");
                }
                else
                {
                    Debug.Log("[PlayScope/diag] PlayScopeEditorPlayModeHook: SKIPPING Shutdown — " +
                        $"IsInitialized={PlayScopeRuntime.IsInitialized}, IsDisabled={PlayScopeRuntime.IsDisabled}.");
                }
            }
            catch (System.Exception ex)
            {
                // Never let an Editor-side hook throw — would block the
                // Editor's play-mode transition itself.
                Debug.LogWarning($"[PlayScope/diag] PlayScopeEditorPlayModeHook: Shutdown threw — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                PlayScopeLog.Warning("PlayScopeEditorPlayModeHook: Shutdown threw", ex);
            }
        }
    }
}
#endif
