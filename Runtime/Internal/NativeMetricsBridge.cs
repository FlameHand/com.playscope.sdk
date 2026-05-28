using System;
using System.IO;
using System.Runtime.InteropServices;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
#elif UNITY_EDITOR
using UnityEngine;
#endif

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Native bridge for OS-level device telemetry: free RAM and available
    /// disk space, in megabytes. iOS uses Mach + NSFileManager; Android uses
    /// ActivityManager + StatFs. All calls are fail-safe — any failure
    /// returns 0 (memory) or -1 (disk, "unavailable") so the pipeline can
    /// skip the sample rather than emit fake zeros.
    /// </summary>
    internal static class NativeMetricsBridge
    {
        private const string LOG_TAG = "[PlayScope.NativeMetricsBridge]";

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern long PlayScopeGetFreeMemoryMb();

        [DllImport("__Internal")]
        private static extern long PlayScopeGetAvailableDiskMb();
#endif

        internal static long GetFreeMemoryMb()
        {
            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                return PlayScopeGetFreeMemoryMb();
#elif UNITY_ANDROID && !UNITY_EDITOR
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var cls = new AndroidJavaClass("com.playscope.sdk.PlayScopeNativeMetrics"))
                {
                    if (activity == null)
                    {
                        return 0L;
                    }
                    return cls.CallStatic<long>("getFreeMemoryMb", activity);
                }
#else
                return 0L;
#endif
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning(LOG_TAG + " GetFreeMemoryMb failed: " + ex.Message);
                return 0L;
            }
        }

        // -1 means "unavailable on this platform / failed" — caller must skip
        // the emit. DriveInfo path stays for Editor only because IL2CPP doesn't
        // implement the GetDriveFormat icall and spams Console every tick.
        internal static long GetAvailableDiskMb()
        {
            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                return PlayScopeGetAvailableDiskMb();
#elif UNITY_ANDROID && !UNITY_EDITOR
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var cls = new AndroidJavaClass("com.playscope.sdk.PlayScopeNativeMetrics"))
                {
                    if (activity == null)
                    {
                        return -1L;
                    }
                    return cls.CallStatic<long>("getAvailableDiskMb", activity);
                }
#elif UNITY_EDITOR
                var root = Path.GetPathRoot(Application.persistentDataPath);
                if (string.IsNullOrEmpty(root)) { return -1L; }
                var info = new DriveInfo(root);
                return info.AvailableFreeSpace / (1024L * 1024L);
#else
                return -1L;
#endif
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning(LOG_TAG + " GetAvailableDiskMb failed: " + ex.Message);
                return -1L;
            }
        }
    }
}
