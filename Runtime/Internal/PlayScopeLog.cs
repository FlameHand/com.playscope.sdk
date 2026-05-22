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
        // Threshold for SDK-internal lines. Defaults to Info so pre-Initialize
        // log lines (e.g. partial-init failure messages) are visible. After
        // Initialize, PlayScopeRuntime calls SetMinLevel with the consumer's
        // PlayScopeSettings.MinLogLevel — Warning-or-higher hides the Info
        // chatter (orphan-chunk rescue, session_end sync-write notice, etc.)
        // from the Editor Console.
        //
        // Error always passes — there is no scenario where suppressing an
        // SDK error is useful to the integrator.
        // Warning always passes too: warnings name actual misconfiguration
        // (missing SDK key, drop-on-quota, asset not found) — suppressing
        // them silently is worse than the noise of seeing them.
        // Only Info is gated.
        private static LogLevel _minLevel = LogLevel.Info;

        /// <summary>
        /// Called by PlayScopeRuntime.Initialize once the settings asset has
        /// been resolved. Cached locally so per-call logging doesn't dip
        /// into Resources every line.
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
            // Suppress when consumer asked for Warning-or-higher — this
            // covers the orphan-chunk rescue spam and other operational
            // chatter that's interesting only when debugging the SDK.
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
