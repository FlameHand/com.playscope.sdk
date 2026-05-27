using System;
using System.Runtime.InteropServices;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
#endif

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Native bridge for OS-level memory telemetry. Returns the amount of
    /// physical RAM the OS reports as free/available, in megabytes. On iOS
    /// this comes from Mach <c>host_statistics64(HOST_VM_INFO64)</c>; on
    /// Android from <c>ActivityManager.MemoryInfo.availMem</c>. Every call
    /// is fail-safe — any failure (missing symbol, JNI exception, null
    /// activity) returns 0 so the metric pipeline sees an "unavailable"
    /// sample rather than throwing.
    /// </summary>
    internal static class NativeMetricsBridge
    {
        private const string LOG_TAG = "[PlayScope.NativeMetricsBridge]";

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern long PlayScopeGetFreeMemoryMb();
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
    }
}
