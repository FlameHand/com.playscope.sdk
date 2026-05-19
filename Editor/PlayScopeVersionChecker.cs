using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// Auto-checks GitHub Releases once per 24 h and prompts when a newer
    /// version is available. Manual entry via <c>PlayScope ▸ Check for Updates</c>
    /// opens a window with a live progress bar — the auto-check stays silent
    /// so it doesn't interrupt editor startup.
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayScopeVersionChecker
    {
        internal const string PackageName      = "com.playscope.sdk";
        internal const string RepoOwner        = "FlameHand";
        internal const string RepoName         = "com.playscope.sdk";
        internal const string LatestReleaseUrl = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
        internal const string InstallUrlBase   = "https://github.com/" + RepoOwner + "/" + RepoName + ".git";

        private const string PrefLastCheck     = "PlayScope_LastVersionCheck";
        private const string PrefSkipVersion   = "PlayScope_SkipVersion";
        private const double CheckIntervalHours = 24.0;

        static PlayScopeVersionChecker()
        {
            EditorApplication.delayCall += AutoCheckSilent;
        }

        // ── Menu entries ──────────────────────────────────────────────────────────

        [MenuItem("PlayScope/Check for Updates")]
        private static void ForceCheck()
        {
            // The window opens immediately in Checking state so the user sees
            // *something* the moment they click — the silent old behaviour was
            // the actual UX bug, not the check itself.
            EditorPrefs.DeleteKey(PrefLastCheck);
            PlayScopeUpdateWindow.Open();
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

        // ── Silent 24 h background check (editor startup) ─────────────────────────
        //
        // Stays a fire-and-forget chain with no UI — only opens a dialog if a
        // new version is found. We don't want to pop a window every time the
        // editor starts; that's noisy.

        private static void AutoCheckSilent()
        {
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

                string installed = null;
                foreach (var pkg in listReq.Result)
                {
                    if (pkg.name == PackageName) { installed = pkg.version; break; }
                }
                if (installed == null) return;

                EditorPrefs.SetString(PrefLastCheck, DateTime.UtcNow.ToString("o"));
                SilentFetchAndPromptIfNewer(installed);
            }
        }

        private static void SilentFetchAndPromptIfNewer(string installed)
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

                if (!TryParseLatestTag(json, out var latest)) return;
                if (!IsNewer(latest, installed)) return;
                if (EditorPrefs.GetString(PrefSkipVersion, "") == latest) return;

                PromptUpdate(installed, latest);
            };
        }

        internal static void PromptUpdate(string installed, string latest)
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "PlayScope SDK — Update Available",
                $"A new version of the PlayScope SDK is available.\n\n" +
                $"  Installed:  {installed}\n" +
                $"  Available:  {latest}\n\n" +
                $"Update now?",
                "Update",            // 0
                "Skip this version", // 1
                "Later"              // 2
            );

            switch (choice)
            {
                case 0: Client.Add($"{InstallUrlBase}#v{latest}"); break;
                case 1: EditorPrefs.SetString(PrefSkipVersion, latest); break;
            }
        }

        // ── Shared helpers used by both flows ─────────────────────────────────────

        internal static string GetInstalledVersion()
        {
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

        internal static bool TryParseLatestTag(string githubJson, out string version)
        {
            var match = Regex.Match(githubJson, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
            if (match.Success)
            {
                version = match.Groups[1].Value.Trim();
                return true;
            }
            version = null;
            return false;
        }

        internal static bool IsNewer(string candidate, string installed)
        {
            return Version.TryParse(candidate, out var c) &&
                   Version.TryParse(installed,  out var i) &&
                   c > i;
        }

        internal static void MarkCheckedNow() =>
            EditorPrefs.SetString(PrefLastCheck, DateTime.UtcNow.ToString("o"));

        internal static void MarkSkippedVersion(string version) =>
            EditorPrefs.SetString(PrefSkipVersion, version);
    }

    /// <summary>
    /// Manual "Check for Updates" UI. Opens the window IMMEDIATELY with a
    /// progress bar so the user knows the click registered, then transitions
    /// to one of three terminal states (Up to date / Update available /
    /// Error). Cancellable — closing the window mid-flight disposes the
    /// in-flight web request.
    /// </summary>
    internal sealed class PlayScopeUpdateWindow : EditorWindow
    {
        private enum Phase { Listing, Fetching, UpToDate, UpdateAvailable, Error }

        private Phase _phase = Phase.Listing;
        private float _progress = 0.33f;
        private string _installedVersion;
        private string _latestVersion;
        private string _errorMessage;

        private ListRequest _listReq;
        private UnityWebRequest _www;
        private double _lastSpinnerTime;
        private int _spinnerFrame;

        // Single-char marquee for the in-flight bar. Same pattern as gh / cargo.
        private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        public static void Open()
        {
            // GetWindow keeps the same instance on repeat clicks — the user can
            // mash the menu and we won't fan out parallel requests.
            var w = GetWindow<PlayScopeUpdateWindow>(true, "PlayScope SDK — Update Check");
            w.minSize = new Vector2(440, 220);
            w.maxSize = new Vector2(440, 220);
            w.StartCheck();
            w.Show();
            w.Focus();
        }

        private void StartCheck()
        {
            // Guard by the actual in-flight handles, not by _phase: _phase
            // defaults to Phase.Listing on a fresh window, which would make
            // the old "is in Listing/Fetching" check skip the very first
            // request and leave the bar bouncing forever.
            if (_listReq != null || _www != null) return;

            _phase = Phase.Listing;
            _progress = 0.33f;
            _installedVersion = null;
            _latestVersion = null;
            _errorMessage = null;

            _listReq = Client.List(offlineMode: false);
            Repaint();
        }

        private void OnDisable()
        {
            if (_www != null)
            {
                try { _www.Dispose(); } catch { /* best-effort */ }
                _www = null;
            }
        }

        private void Update()
        {
            switch (_phase)
            {
                case Phase.Listing:
                    TickListing();
                    break;
                case Phase.Fetching:
                    TickFetching();
                    break;
            }

            // Animate spinner glyph + repaint roughly every 100ms while a
            // request is in flight. Progress value itself stays static at the
            // phase's bucket (0.33 / 0.66 / 1.0) — drifting it created the
            // "fills then resets" illusion that looked like a hang.
            if (_phase == Phase.Listing || _phase == Phase.Fetching)
            {
                if (EditorApplication.timeSinceStartup - _lastSpinnerTime > 0.1)
                {
                    _lastSpinnerTime = EditorApplication.timeSinceStartup;
                    _spinnerFrame++;
                    Repaint();
                }
            }
        }

        private void TickListing()
        {
            if (_listReq == null || !_listReq.IsCompleted) return;

            if (_listReq.Status != StatusCode.Success)
            {
                EnterError("Couldn't read installed packages: " + (_listReq.Error?.message ?? "unknown error"));
                return;
            }

            foreach (var pkg in _listReq.Result)
            {
                if (pkg.name == PlayScopeVersionChecker.PackageName)
                {
                    _installedVersion = pkg.version;
                    break;
                }
            }
            _listReq = null;

            if (_installedVersion == null)
            {
                // Fall back to package.json so we still show something useful
                // even when UPM list doesn't surface the package (rare but
                // happens on first import).
                _installedVersion = PlayScopeVersionChecker.GetInstalledVersion();
            }

            if (_installedVersion == null)
            {
                EnterError("PlayScope SDK package not found in this project.");
                return;
            }

            _phase = Phase.Fetching;
            _progress = 0.66f;

            _www = UnityWebRequest.Get(PlayScopeVersionChecker.LatestReleaseUrl);
            _www.SetRequestHeader("User-Agent", PlayScopeVersionChecker.PackageName + "-version-checker");
            _www.SendWebRequest();
            Repaint();
        }

        private void TickFetching()
        {
            if (_www == null || !_www.isDone) return;

            if (_www.result != UnityWebRequest.Result.Success)
            {
                var err = _www.error ?? "network error";
                _www.Dispose();
                _www = null;
                EnterError("Couldn't reach GitHub: " + err);
                return;
            }

            var json = _www.downloadHandler.text;
            _www.Dispose();
            _www = null;

            if (!PlayScopeVersionChecker.TryParseLatestTag(json, out _latestVersion))
            {
                EnterError("GitHub returned a release JSON we couldn't parse for a tag.");
                return;
            }

            PlayScopeVersionChecker.MarkCheckedNow();

            _progress = 1f;
            _phase = PlayScopeVersionChecker.IsNewer(_latestVersion, _installedVersion)
                ? Phase.UpdateAvailable
                : Phase.UpToDate;
            Repaint();
        }

        private void EnterError(string msg)
        {
            _phase = Phase.Error;
            _errorMessage = msg;
            _progress = 0f;
            if (_www != null) { _www.Dispose(); _www = null; }
            if (_listReq != null) _listReq = null;
            Repaint();
        }

        // Status text shown ON the progress bar, kept as a derived value so
        // there's no _statusLine field to keep in sync as the phase changes.
        private string CurrentStatus()
        {
            var spin = (_phase == Phase.Listing || _phase == Phase.Fetching)
                ? SpinnerFrames[_spinnerFrame % SpinnerFrames.Length] + "  "
                : "";
            switch (_phase)
            {
                case Phase.Listing:         return spin + "Listing installed packages…";
                case Phase.Fetching:        return spin + "Checking GitHub for the latest release…";
                case Phase.UpToDate:        return "Up to date";
                case Phase.UpdateAvailable: return "Update available";
                case Phase.Error:           return "Update check failed";
                default:                    return "";
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("PlayScope SDK", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (_installedVersion != null)
                EditorGUILayout.LabelField("Installed", _installedVersion);
            if (_latestVersion != null)
                EditorGUILayout.LabelField("Latest", _latestVersion);

            EditorGUILayout.Space(10);

            // Progress bar — always rendered so the layout doesn't jump when
            // we transition from in-flight to terminal state. Value is static
            // per phase; the spinner glyph in the status text carries the
            // "things are happening" cue.
            var rect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(rect, _progress, CurrentStatus());

            EditorGUILayout.Space(10);

            switch (_phase)
            {
                case Phase.Listing:
                case Phase.Fetching:
                    DrawInFlightButtons();
                    break;
                case Phase.UpToDate:
                    DrawUpToDateButtons();
                    break;
                case Phase.UpdateAvailable:
                    DrawUpdateAvailableButtons();
                    break;
                case Phase.Error:
                    DrawErrorButtons();
                    break;
            }
        }

        private void DrawInFlightButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(100)))
                {
                    if (_www != null) { _www.Dispose(); _www = null; }
                    Close();
                }
            }
        }

        private void DrawUpToDateButtons()
        {
            EditorGUILayout.HelpBox(
                $"PlayScope SDK is up to date ({_installedVersion}).",
                MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", GUILayout.Width(100))) Close();
            }
        }

        private void DrawUpdateAvailableButtons()
        {
            EditorGUILayout.HelpBox(
                $"A newer release ({_latestVersion}) is available.",
                MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Skip this version", GUILayout.Width(140)))
                {
                    PlayScopeVersionChecker.MarkSkippedVersion(_latestVersion);
                    Close();
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Later", GUILayout.Width(80))) Close();
                if (GUILayout.Button("Update now", GUILayout.Width(120)))
                {
                    Client.Add($"{PlayScopeVersionChecker.InstallUrlBase}#v{_latestVersion}");
                    Close();
                }
            }
        }

        private void DrawErrorButtons()
        {
            EditorGUILayout.HelpBox(_errorMessage ?? "Unknown error.", MessageType.Error);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Retry", GUILayout.Width(80)))
                {
                    // Reuse the canonical start path so re-arm logic stays in
                    // one place — and StartCheck's guard correctly sees that
                    // both handles are null (we cleared them on EnterError).
                    StartCheck();
                }
                if (GUILayout.Button("Close", GUILayout.Width(80))) Close();
            }
        }

    }
}
