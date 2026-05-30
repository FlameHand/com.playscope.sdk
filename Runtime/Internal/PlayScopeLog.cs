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
        // Defaults to Info so pre-Initialize lines are visible; after Initialize,
        // PlayScopeRuntime calls SetMinLevel with the consumer's MinLogLevel.
        // Only Info is gated — Error and Warning always pass (warnings name real
        // misconfiguration, so silently suppressing them is worse than the noise).
        private static LogLevel _minLevel = LogLevel.Info;

        /// <summary>
        /// Called by PlayScopeRuntime.Initialize once settings are resolved.
        /// Cached so per-call logging doesn't hit Resources every line.
        /// </summary>
        internal static void SetMinLevel(LogLevel level)
        {
            _minLevel = level;
        }

        internal static void Warning(string msg, Exception ex = null)
        {
            if (ex == null)
                Debug.LogWarning($"[PlayScope] {msg}");
            else
                Debug.LogWarning($"[PlayScope] {msg}: {ex.Message}");
        }

        internal static void Info(string msg)
        {
            if (_minLevel > LogLevel.Info) return;
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
