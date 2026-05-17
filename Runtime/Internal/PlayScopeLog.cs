using System;
using UnityEngine;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Centralised logging helper. All SDK-internal warnings are prefixed with [PlayScope]
    /// so integrations can filter them out (and so the AutoCaptureUnityLogs recursion guard
    /// can drop them).
    /// </summary>
    internal static class PlayScopeLog
    {
        internal static void Warning(string msg, Exception ex = null)
        {
            if (ex == null)
                Debug.LogWarning($"[PlayScope] {msg}");
            else
                Debug.LogWarning($"[PlayScope] {msg}: {ex.Message}");
        }

        internal static void Info(string msg)
        {
            Debug.Log($"[PlayScope] {msg}");
        }

        internal static void Error(string msg, Exception ex = null)
        {
            if (ex == null)
                Debug.LogError($"[PlayScope] {msg}");
            else
                Debug.LogError($"[PlayScope] {msg}: {ex.Message}");
        }
    }
}
