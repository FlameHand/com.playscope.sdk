using System;
using System.Runtime.InteropServices;
using UnityEngine;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// C# bridge that wires the platform-specific lifecycle hooks into the
    /// SDK. Both implementations write to the SAME file the C# side already
    /// writes (<see cref="PlayScopeDirectory.SessionLifecycle"/>) — the
    /// platform hook just adds an extra <c>"intent":true</c> field with a
    /// more precise state ("user_close") so SessionRecovery on the next
    /// launch can tell the difference between
    ///
    ///   • a real swipe-from-recents (intent=true → user_close) and
    ///   • a backgrounded OS kill (no intent file, state=background → background_kill)
    ///
    /// On Android the work is done by a small Java class compiled into the
    /// consumer's APK from <c>Plugins/Android/PlayScopeLifecycle.java</c>.
    /// On iOS by an Objective-C++ source in
    /// <c>Plugins/iOS/PlayScopeLifecycle.mm</c>.
    ///
    /// Both calls are best-effort and idempotent — failure during install
    /// silently falls back to the C# OnApplicationPause-driven lifecycle
    /// state, which still gives us correct (just less precise) recovery
    /// classification.
    /// </summary>
    internal static class NativeLifecycleBridge
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void _playscope_install_ios_lifecycle(string lifecyclePath);
#endif

        internal static void Install()
        {
            try
            {
                var path = PlayScopeDirectory.SessionLifecycle;

#if UNITY_ANDROID && !UNITY_EDITOR
                InstallAndroid(path);
#elif UNITY_IOS && !UNITY_EDITOR
                _playscope_install_ios_lifecycle(path);
                PlayScopeLog.Info("NativeLifecycleBridge: iOS WillTerminate observer installed.");
#else
                // Editor / Standalone / WebGL — no platform-specific hook needed.
                // C# OnApplicationPause / Application.quitting still drive the
                // managed-level lifecycle file write.
#endif
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("NativeLifecycleBridge.Install failed", ex);
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static void InstallAndroid(string lifecyclePath)
        {
            // Resolve current Activity from UnityPlayer. Wrapped so that if
            // the host app uses a custom UnityPlayerActivity subclass we
            // still pick up the running instance.
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            if (activity == null)
            {
                PlayScopeLog.Warning("NativeLifecycleBridge: UnityPlayer.currentActivity is null — Android lifecycle hook NOT installed.");
                return;
            }
            using var lifecycle = new AndroidJavaClass("com.playscope.sdk.PlayScopeLifecycle");
            lifecycle.CallStatic("install", activity, lifecyclePath);
            PlayScopeLog.Info("NativeLifecycleBridge: Android ActivityLifecycleCallbacks installed.");
        }
#endif
    }
}
