using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// Checks GitHub Releases once per 24 h and prompts when a newer version is available.
    /// Accessible manually via PlayScope ▸ Check for Updates.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayScopeVersionChecker
    {
        private const string PackageName      = "com.playscope.sdk";
        private const string RepoOwner        = "FlameHand";
        private const string RepoName         = "com.playscope.sdk";
        private const string LatestReleaseUrl = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
        private const string InstallUrlBase   = "https://github.com/" + RepoOwner + "/" + RepoName + ".git";

        private const string PrefLastCheck    = "PlayScope_LastVersionCheck";
        private const string PrefSkipVersion  = "PlayScope_SkipVersion";
        private const double CheckIntervalHours = 24.0;

        static PlayScopeVersionChecker()
        {
            EditorApplication.delayCall += RunCheck;
        }

        // ── Menu entry ────────────────────────────────────────────────────────────

        [MenuItem("PlayScope/Check for Updates")]
        private static void ForceCheck()
        {
            EditorPrefs.DeleteKey(PrefLastCheck);
            RunCheck();
        }

        [MenuItem("PlayScope/About PlayScope SDK")]
        private static void About()
        {
            var installed = GetInstalledVersion();
            EditorUtility.DisplayDialog(
                "PlayScope SDK",
                installed != null
                    ? $"Installed version: {installed}\n\nUse PlayScope › Check for Updates to see if a newer release is available."
                    : "PlayScope SDK is not installed via UPM.",
                "OK");
        }

        // ── Core flow ─────────────────────────────────────────────────────────────

        private static void RunCheck()
        {
            // Throttle — skip if checked within the last 24 h
            if (EditorPrefs.HasKey(PrefLastCheck))
            {
                var raw = EditorPrefs.GetString(PrefLastCheck, "");
                if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                    (DateTime.UtcNow - last).TotalHours < CheckIntervalHours)
                    return;
            }

            var listReq = Client.List(offlineMode: false);
            EditorApplication.update += WaitForList;

            void WaitForList()
            {
                if (!listReq.IsCompleted) return;
                EditorApplication.update -= WaitForList;

                if (listReq.Status != StatusCode.Success) return;

                string installedVersion = null;
                foreach (var pkg in listReq.Result)
                {
                    if (pkg.name == PackageName)
                    {
                        installedVersion = pkg.version;
                        break;
                    }
                }

                if (installedVersion == null) return;

                EditorPrefs.SetString(PrefLastCheck, DateTime.UtcNow.ToString("o"));
                FetchLatestAndCompare(installedVersion);
            }
        }

        private static void FetchLatestAndCompare(string installedVersion)
        {
            var www = UnityWebRequest.Get(LatestReleaseUrl);
            www.SetRequestHeader("User-Agent", PackageName + "-version-checker");
            var op = www.SendWebRequest();

            op.completed += _ =>
            {
                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[PlayScope] Version check failed: " + www.error);
                    www.Dispose();
                    return;
                }

                var json = www.downloadHandler.text;
                www.Dispose();

                // Extract tag_name from GitHub API response
                var match = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                if (!match.Success) return;

                var latestVersion = match.Groups[1].Value.Trim();
                if (!IsNewer(latestVersion, installedVersion)) return;

                // User previously chose "Skip this version"
                if (EditorPrefs.GetString(PrefSkipVersion, "") == latestVersion) return;

                ShowUpdateDialog(installedVersion, latestVersion);
            };
        }

        private static void ShowUpdateDialog(string installed, string latest)
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "PlayScope SDK — Update Available",
                $"A new version of the PlayScope SDK is available.\n\n" +
                $"  Installed:  {installed}\n" +
                $"  Available:  {latest}\n\n" +
                $"Update now?",
                "Update",           // 0
                "Skip this version", // 1
                "Later"             // 2
            );

            switch (choice)
            {
                case 0:
                    // Re-add with explicit version tag so the lock file records a stable ref
                    Client.Add($"{InstallUrlBase}#v{latest}");
                    break;

                case 1:
                    EditorPrefs.SetString(PrefSkipVersion, latest);
                    break;

                // case 2 — remind on next editor launch (do nothing)
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string GetInstalledVersion()
        {
            // Read directly from package.json — fast, no async needed
            var guids = AssetDatabase.FindAssets("package", new[] { "Packages/" + PackageName });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
                {
                    var json = System.IO.File.ReadAllText(path);
                    var m = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            return null;
        }

        private static bool IsNewer(string candidate, string installed)
        {
            return Version.TryParse(candidate, out var c) &&
                   Version.TryParse(installed,  out var i) &&
                   c > i;
        }
    }
}
