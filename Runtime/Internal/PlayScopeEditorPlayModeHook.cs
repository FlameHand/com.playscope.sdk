#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Editor-only safety net for "user clicked Stop in Play Mode" — that flow
    /// does NOT reliably trigger <c>OnApplicationQuit</c> or
    /// <c>Application.quitting</c> in the Editor (despite the docs). Without
    /// this, every Play+Stop cycle leaves a stale session.lock + un-finalised
    /// chunk_current.jsonl, and the next launch's SessionRecovery invents a
    /// synthetic <c>session_abnormal_end</c> for what was a clean exit.
    /// playModeStateChanged is the one transition signal Unity guarantees in
    /// the Editor. Shutdown is idempotent (gated on _initialized) so it's safe
    /// even if Application.quitting also fires — first call wins.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayScopeEditorPlayModeHook
    {
        static PlayScopeEditorPlayModeHook()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Debug.Log (not PlayScopeLog) so diag lines land in Editor.log even at Warning min-level.
            Debug.Log("[PlayScope/diag] PlayScopeEditorPlayModeHook: static ctor — subscribed to playModeStateChanged.");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            Debug.Log($"[PlayScope/diag] PlayScopeEditorPlayModeHook: state={state} " +
                $"isInitialized={PlayScopeRuntime.IsInitialized} isDisabled={PlayScopeRuntime.IsDisabled}");

            // Must be ExitingPlayMode: by EnteringEditMode the MonoBehaviour driver
            // and writer thread are already gone, so Shutdown couldn't flush anything.
            if (state != PlayModeStateChange.ExitingPlayMode) return;

            try
            {
                if (PlayScopeRuntime.IsInitialized && !PlayScopeRuntime.IsDisabled)
                {
                    Debug.Log("[PlayScope/diag] PlayScopeEditorPlayModeHook: calling PlayScopeRuntime.Shutdown()...");
                    PlayScopeRuntime.Shutdown();
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
