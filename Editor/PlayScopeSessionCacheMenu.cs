using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// Editor utility for wiping the on-disk PlayScope cache.
    ///
    /// <para>
    /// Use this when iterating in the Editor and you want a clean slate — e.g. after a
    /// crash left orphan chunks behind, or before re-running a session-recovery test.
    /// The runtime mirror of this lives in PlayScopeRuntime/SessionRecovery; this menu
    /// is purely a dev affordance and is NOT shipped to runtime (file is under
    /// Assets/Editor/ and the asmdef is Editor-only).
    /// </para>
    ///
    /// <para>
    /// What gets removed: everything under <c>Application.persistentDataPath/PlayScope/</c>
    /// — session.json/lock/hb, chunks/, upload_queue/, completed_sessions/, dead_letter/,
    /// and device.json. After confirming, the directory is deleted and re-created empty
    /// so the next Play Mode start lands in a virgin state.
    /// </para>
    /// </summary>
    internal static class PlayScopeSessionCacheMenu
    {
        // Path resolution mirrors PlayScopeDirectory but is Editor-only and avoids
        // pulling in any runtime singleton — keeps the menu safe to invoke when the
        // SDK hasn't been Initialize()d in this Editor session.
        private static string CacheRoot =>
            Path.Combine(Application.persistentDataPath, "PlayScope");

        [MenuItem("PlayScope/Clear Session Cache", priority = 200)]
        private static void ClearSessionCache()
        {
            var root = CacheRoot;

            if (!Directory.Exists(root))
            {
                EditorUtility.DisplayDialog(
                    "PlayScope SDK",
                    $"Nothing to clear — no cache directory at:\n{root}",
                    "OK");
                return;
            }

            // Show the user exactly what they're about to nuke. The size hint helps
            // when chunks accumulated over multiple test runs — visible quantification
            // is the difference between "ok" and "wait, what?" for destructive ops.
            long totalBytes;
            int fileCount;
            try
            {
                (totalBytes, fileCount) = MeasureCache(root);
            }
            catch (Exception ex)
            {
                totalBytes = 0;
                fileCount = 0;
                Debug.LogWarning($"[PlayScope] Couldn't measure cache size: {ex.Message}");
            }

            var confirm = EditorUtility.DisplayDialog(
                "Clear PlayScope Session Cache?",
                $"This will permanently delete the PlayScope on-disk cache:\n\n" +
                $"  {root}\n\n" +
                $"Includes session.json/lock/hb, chunks, upload queue, completed sessions, " +
                $"dead-letter, and device identity.\n\n" +
                $"Size:  {FormatBytes(totalBytes)} across {fileCount} files\n\n" +
                $"This cannot be undone.",
                "Clear cache",
                "Cancel");

            if (!confirm) return;

            // If Play Mode is running, the runtime is holding file handles (chunk_current.jsonl
            // stream is long-lived). Deleting under live handles either silently corrupts the
            // worker state or errors out per-file on Windows. Refuse to proceed loudly instead
            // of half-clearing.
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog(
                    "PlayScope SDK",
                    "Exit Play Mode before clearing the cache — the runtime holds file handles " +
                    "on chunks/ while playing.",
                    "OK");
                return;
            }

            try
            {
                Directory.Delete(root, recursive: true);
                Directory.CreateDirectory(root);
                Debug.Log($"[PlayScope] Session cache cleared: {root}");
                EditorUtility.DisplayDialog(
                    "PlayScope SDK",
                    "Session cache cleared. The next Play Mode start will create a fresh session.",
                    "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayScope] Failed to clear session cache: {ex}");
                EditorUtility.DisplayDialog(
                    "PlayScope SDK",
                    "Couldn't clear the cache:\n\n" + ex.Message +
                    "\n\nA process may still be holding a file open. Close any running builds and retry.",
                    "OK");
            }
        }

        // Disabled when there's literally nothing to clear. Keeps the menu item
        // discoverable but greys it out when the cache root is missing.
        [MenuItem("PlayScope/Clear Session Cache", validate = true)]
        private static bool ClearSessionCacheValidate()
        {
            return Directory.Exists(CacheRoot);
        }

        [MenuItem("PlayScope/Reveal Session Cache in File Browser", priority = 201)]
        private static void RevealCache()
        {
            var root = CacheRoot;
            if (!Directory.Exists(root))
            {
                EditorUtility.DisplayDialog(
                    "PlayScope SDK",
                    $"No cache directory yet at:\n{root}\n\nIt'll be created on the first Play Mode start.",
                    "OK");
                return;
            }
            // EditorUtility.RevealInFinder works on Windows Explorer too despite the name.
            EditorUtility.RevealInFinder(root);
        }

        private static (long bytes, int files) MeasureCache(string root)
        {
            long total = 0;
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                    count++;
                }
                catch { /* skip unreadable */ }
            }
            return (total, count);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return $"{mb:F1} MB";
            double gb = mb / 1024.0;
            return $"{gb:F2} GB";
        }
    }
}
