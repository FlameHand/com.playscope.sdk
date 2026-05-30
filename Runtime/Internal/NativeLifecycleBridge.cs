using System;
using System.Runtime.InteropServices;
using UnityEngine;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Wires platform lifecycle hooks (Android Java, iOS .mm) into the SDK. Both
    /// write to the same <see cref="PlayScopeDirectory.SessionLifecycle"/> file
    /// the C# side does, adding <c>"intent":true</c> ("user_close") so recovery
    /// distinguishes a swipe-from-recents (intent → user_close) from a
    /// backgrounded OS kill (no intent → background_kill). Best-effort and
    /// idempotent — install failure falls back to the C# OnApplicationPause path
    /// (correct, just less precise).
    /// </summary>
    internal static class NativeLifecycleBridge
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void _playscope_install_ios_lifecycle(string lifecyclePath);
#endif

        /// <summary>
        /// True iff Install() wired the platform hook. Stamped into session_start
        /// so the dashboard verifies the hook landed without adb logcat.
        /// </summary>
        internal static bool IsInstalled { get; private set; }

        /// <summary>
        /// Last Install() error (null on success). Surfaced in session_start next
        /// to <c>lifecycle_hook_installed=false</c>.
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
                // No native hook on Editor/Standalone/WebGL — the C# lifecycle
                // path covers it; flag stays false (correctly "not installed").
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
            // Resolve current Activity (works with custom UnityPlayerActivity subclasses).
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
                // Resolve our class explicitly so a missing-from-APK case (PluginImporter
                // Android checkbox off) surfaces a useful error, not a silent miss.
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
            // DllImport resolves here — a missing-from-Xcode .mm throws
            // EntryPointNotFoundException, surfaced as a useful error.
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
