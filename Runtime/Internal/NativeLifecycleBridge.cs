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

        /// <summary>
        /// True iff Install() successfully wired the platform-specific
        /// lifecycle hook (Java ActivityLifecycleCallbacks on Android,
        /// WillTerminate observer on iOS). Stamped into every session_start
        /// metadata so the dashboard can verify the hook landed in the
        /// build without needing adb logcat.
        /// </summary>
        internal static bool IsInstalled { get; private set; }

        /// <summary>
        /// Last error string from the most recent failed Install() attempt.
        /// Null on success. Surfaced in session_start metadata so the
        /// dashboard shows it next to <c>lifecycle_hook_installed=false</c>.
        /// </summary>
        internal static string LastError { get; private set; }

        internal static void Install()
        {
            IsInstalled = false;
            LastError = null;
            try
            {
                var path = PlayScopeDirectory.SessionLifecycle;

#if UNITY_ANDROID && !UNITY_EDITOR
                InstallAndroid(path);
#elif UNITY_IOS && !UNITY_EDITOR
                InstallIOS(path);
#else
                // Editor / Standalone / WebGL — no platform-specific hook
                // needed. C# OnApplicationPause / Application.quitting still
                // drive the managed-level lifecycle file write. Flag stays
                // false so the dashboard shows it correctly as not installed.
                LastError = "no_native_hook_on_this_platform";
                PlayScopeLog.Info("NativeLifecycleBridge: no native hook on this platform (Editor/Standalone/WebGL).");
#endif
            }
            catch (Exception ex)
            {
                IsInstalled = false;
                LastError = "install_exception:" + ex.GetType().Name + ":" + ex.Message;
                PlayScopeLog.Warning("NativeLifecycleBridge.Install failed", ex);
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static void InstallAndroid(string lifecyclePath)
        {
            // Resolve current Activity from UnityPlayer. Wrapped so that if
            // the host app uses a custom UnityPlayerActivity subclass we
            // still pick up the running instance.
            AndroidJavaClass unityPlayer = null;
            AndroidJavaObject activity = null;
            AndroidJavaClass lifecycle = null;
            try
            {
                unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                if (activity == null)
                {
                    LastError = "android_current_activity_null";
                    PlayScopeLog.Warning("NativeLifecycleBridge: UnityPlayer.currentActivity is null — Android lifecycle hook NOT installed.");
                    return;
                }
                // Try to resolve our Java class explicitly so we get a
                // useful error if the .java file wasn't packed into the
                // APK by Unity's Gradle plugin (e.g. PluginImporter
                // platform checkbox left off).
                try
                {
                    lifecycle = new AndroidJavaClass("com.playscope.sdk.PlayScopeLifecycle");
                }
                catch (Exception classEx)
                {
                    LastError = "android_class_not_found:com.playscope.sdk.PlayScopeLifecycle";
                    PlayScopeLog.Warning(
                        "NativeLifecycleBridge: com.playscope.sdk.PlayScopeLifecycle NOT found in APK. " +
                        "Open the .java file in the SDK Plugins/Android folder, check that PluginImporter " +
                        "has Android platform enabled, and rebuild. Underlying error: " + classEx.Message,
                        classEx);
                    return;
                }
                lifecycle.CallStatic("install", activity, lifecyclePath);
                IsInstalled = true;
                PlayScopeLog.Info("NativeLifecycleBridge: Android ActivityLifecycleCallbacks installed. Java logs will tag 'PlayScope/Lifecycle' — `adb logcat -s PlayScope/Lifecycle:* PlayScope:*`.");
            }
            finally
            {
                lifecycle?.Dispose();
                activity?.Dispose();
                unityPlayer?.Dispose();
            }
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        private static void InstallIOS(string lifecyclePath)
        {
            // DllImport resolution at first call site — if the .mm wasn't
            // packed into the Xcode project we'll get an EntryPointNotFoundException
            // and surface it as a useful error rather than a silent miss.
            try
            {
                _playscope_install_ios_lifecycle(lifecyclePath);
                IsInstalled = true;
                PlayScopeLog.Info("NativeLifecycleBridge: iOS WillTerminate observer installed. NSLogs will tag '[PlayScope/Lifecycle]'.");
            }
            catch (EntryPointNotFoundException epnf)
            {
                LastError = "ios_dllimport_not_found:_playscope_install_ios_lifecycle";
                PlayScopeLog.Warning(
                    "NativeLifecycleBridge: _playscope_install_ios_lifecycle symbol NOT found in the iOS binary. " +
                    "Open the .mm file in the SDK Plugins/iOS folder, check that PluginImporter has iOS platform " +
                    "enabled, and rebuild the Xcode project. Underlying error: " + epnf.Message,
                    epnf);
            }
        }
#endif
    }
}
