using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// Editor-only utility to wipe the on-disk PlayScope cache (everything under
    /// <c>persistentDataPath/PlayScope/</c>) for a clean slate when iterating —
    /// e.g. after a crash left orphans, or before a session-recovery test.
    /// </summary>
    internal static class PlayScopeSessionCacheMenu
    {
        // Mirrors PlayScopeDirectory without a runtime singleton, so the menu works
        // even when the SDK was never Initialize()d this Editor session.
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

            // Size hint in the confirm dialog — a destructive op deserves quantification.
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

            // In Play Mode the runtime holds the chunk_current.jsonl handle — deleting
            // under it corrupts worker state / errors on Windows. Refuse loudly.
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
