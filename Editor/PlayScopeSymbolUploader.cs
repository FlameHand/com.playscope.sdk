using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using PlayScopeSdk;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// Post-build hook that uploads IL2CPP symbol files to the PlayScope
    /// backend so iOS / Android exception stack traces can be resolved on
    /// the dashboard.
    ///
    /// <para>
    /// Behaviour:
    /// <list type="bullet">
    ///   <item>Only fires for IL2CPP iOS / Android builds. Mono / Editor /
    ///         Standalone / WebGL: nothing to do, returns immediately.</item>
    ///   <item>Reads config from <see cref="PlayScopeBuildSettings"/>.
    ///         <c>AutoUploadSymbols == false</c> → skip.</item>
    ///   <item>Android: locates Unity's <c>*.symbols.zip</c> (produced when
    ///         Player Settings ▸ Create Symbols Zip is enabled). Uploads
    ///         as-is — Unity already packaged the right .so.sym files.</item>
    ///   <item>iOS: walks the produced Xcode project for <c>*.dSYM</c>
    ///         bundles, zips them, uploads the zip.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Failures are logged loudly but never fail the build — symbols are
    /// recoverable (you can re-upload manually), the build artefact is the
    /// thing the user actually shipped to the store.
    /// </para>
    /// </summary>
    internal sealed class PlayScopeSymbolUploader : IPostprocessBuildWithReport
    {
        // Must run AFTER Unity itself produced the symbols.zip, dSYM, etc.
        // Default IPostprocessBuildWithReport order is 0; positive values
        // run later. 1000 puts us comfortably after all built-in steps.
        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            // Architecture note — TWO threading hazards co-exist here:
            //
            //   (1) Unity Editor APIs (`BuildReport.summary`, `PlayerSettings.*`,
            //       `AssetDatabase.LoadAssetAtPath`) MUST be called on the
            //       main thread; calls from a ThreadPool worker throw
            //       `get_*_Injected: can only be called from the main thread`.
            //
            //   (2) Blocking the main thread on a Task that captured the
            //       main-thread SynchronizationContext deadlocks the build —
            //       the continuation can't run until the main thread is
            //       free, but the main thread is parked on GetResult().
            //
            // Resolution: do EVERYTHING Unity-API on the main thread
            // synchronously, then hand a self-contained payload to a
            // ThreadPool Task.Run for the actual HTTP upload. The Task
            // touches zero Unity API, so its continuation has nothing it
            // needs to send back to the main thread.
            UploadPayload payload;
            try { payload = CollectPayloadOnMainThread(report); }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayScope] Symbol upload setup failed: {ex.Message}\n{ex.StackTrace}");
                return;
            }
            if (payload == null) return; // pre-conditions already logged

            try { Task.Run(() => UploadAsync(payload)).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                // NEVER fail the build over a symbol upload failure — the
                // store-ready artefact is more important. Just shout into
                // the console so CI logs surface it.
                Debug.LogError($"[PlayScope] Symbol upload errored unexpectedly: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Main-thread sync collector ─────────────────────────────────────
        //
        // Reads every Unity API surface we need (BuildReport, PlayerSettings,
        // AssetDatabase) and rolls them into a plain-data payload the
        // ThreadPool can consume without ever calling back into Unity.
        // Returns null when we should skip the upload — callers must check.

        private static UploadPayload CollectPayloadOnMainThread(BuildReport report)
        {
            var summary = report.summary;
            var target = summary.platform;

            if (target != BuildTarget.Android && target != BuildTarget.iOS)
                return null;

            var backend = (ScriptingImplementation)PlayerSettings.GetScriptingBackend(
                BuildPipeline.GetBuildTargetGroup(target));
            if (backend != ScriptingImplementation.IL2CPP)
            {
                Debug.Log("[PlayScope] Symbol upload skipped: not an IL2CPP build.");
                return null;
            }

            // Resolve config from the runtime settings asset shared with the
            // SDK (Resources/PlayScopeSettings.asset). Build-time fields
            // (AutoUploadSymbols, VerboseEditor) live on the same asset so
            // the integrator has one config surface in the Inspector.
            var settings = AssetDatabase.LoadAssetAtPath<PlayScopeSettings>(
                "Assets/Resources/PlayScopeSettings.asset");
            if (settings == null)
            {
                Debug.LogWarning(
                    "[PlayScope] Symbol upload skipped: Assets/Resources/PlayScopeSettings.asset " +
                    "not found. Create it via PlayScope ▸ Settings.");
                return null;
            }
            if (!settings.AutoUploadSymbols)
            {
                Debug.Log("[PlayScope] Symbol upload skipped: AutoUploadSymbols is off in PlayScope Settings.");
                return null;
            }

            // SDK key: settings asset first, env var fallback for CI.
            var sdkKey = settings.SdkKey;
            if (string.IsNullOrEmpty(sdkKey))
            {
                sdkKey = System.Environment.GetEnvironmentVariable("PLAYSCOPE_SDK_KEY")
                    ?? System.Environment.GetEnvironmentVariable("PLAYSCOPE_API_KEY"); // back-compat env name
            }
            if (string.IsNullOrEmpty(sdkKey))
            {
                Debug.LogWarning(
                    "[PlayScope] Symbol upload skipped: no SDK key. " +
                    "Paste your ps_live_… key in PlayScope ▸ Settings, " +
                    "or set the PLAYSCOPE_SDK_KEY env var (CI).");
                return null;
            }

            string platform = target == BuildTarget.iOS ? "ios" : "android";
            string appVersion = PlayerSettings.bundleVersion ?? "0.0.0";
            string buildNumber = target == BuildTarget.iOS
                ? PlayerSettings.iOS.buildNumber
                : PlayerSettings.Android.bundleVersionCode.ToString();

            // Locate / prepare the symbol payload.
            // FindAndroidSymbolsZip and PrepareIosDsymZip are file-system
            // calls only — safe to run on main thread (no Unity API), but
            // zipping dSYMs can take seconds. The cost is acceptable on
            // a build-finished hook; we'd rather keep the threading model
            // simple than save a few seconds.
            string zipPath = target == BuildTarget.Android
                ? FindAndroidSymbolsZip(summary)
                : PrepareIosDsymZip(summary);

            if (zipPath == null)
            {
                Debug.LogWarning(
                    target == BuildTarget.Android
                        ? "[PlayScope] No Android .symbols.zip found. Enable " +
                          "Player Settings ▸ Other Settings ▸ Configuration ▸ Create symbols.zip → 'Public' " +
                          "and rebuild for crash resolution to work."
                        : "[PlayScope] No iOS .dSYM bundles found in the build output. " +
                          "Verify Xcode generated dSYM files (Debug Information Format = DWARF with dSYM File).");
                return null;
            }

            var backendUrl = string.IsNullOrEmpty(settings.BackendUrl)
                ? "https://api.playscope.dev"
                : settings.BackendUrl;

            var payload = new UploadPayload
            {
                BackendBase = backendUrl.TrimEnd('/'),
                SdkKey      = sdkKey,
                Platform    = platform,
                AppVersion  = appVersion,
                BuildNumber = buildNumber,
                ZipPath     = zipPath,
                Verbose     = settings.VerboseEditor,
            };

            if (settings.VerboseEditor)
            {
                var sizeBytes = new FileInfo(zipPath).Length;
                Debug.Log($"[PlayScope] Uploading symbols: platform={platform} version={appVersion} build={buildNumber} size={sizeBytes / 1024}KB");
            }
            return payload;
        }

        // Plain-data snapshot handed from main thread to the upload worker —
        // strings + bool only, no Unity types, no callbacks.
        private sealed class UploadPayload
        {
            public string BackendBase;
            public string SdkKey;
            public string Platform;
            public string AppVersion;
            public string BuildNumber;
            public string ZipPath;
            public bool   Verbose;
        }

        // ── Android — Unity already zips for us ─────────────────────────────

        private static string FindAndroidSymbolsZip(BuildSummary summary)
        {
            // Unity's symbols.zip lands beside the APK / AAB. Look in the
            // output directory plus its parent (Unity's "Build" sometimes
            // puts the zip one level up).
            var artefactPath = summary.outputPath;
            var dirCandidates = new[]
            {
                Path.GetDirectoryName(artefactPath) ?? "",
                Path.GetDirectoryName(Path.GetDirectoryName(artefactPath) ?? "") ?? "",
            };
            foreach (var dir in dirCandidates)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                // Unity names the file like "<productName>-<version>-v<code>.symbols.zip"
                // — fuzzy-match on the suffix.
                var match = Directory.EnumerateFiles(dir, "*.symbols.zip", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
                if (match != null) return match;
            }
            return null;
        }

        // ── iOS — walk the Xcode project and zip the dSYMs ourselves ────────
        //
        // Synchronous: called from main thread inside CollectPayloadOnMainThread,
        // returns plain string path to a temp file. ZipFile.Open is blocking
        // but the artefact is typically a few MB on a debug build; the time
        // cost vs the simplicity of staying single-threaded is worth it.

        private static string PrepareIosDsymZip(BuildSummary summary)
        {
            var artefactDir = summary.outputPath;
            if (!Directory.Exists(artefactDir)) return null;

            // dSYM bundles are *directories* with the .dSYM extension. They
            // typically live in the Xcode product dir; check the root and a
            // standard "Symbols" subdir.
            var dsymDirs = Directory.EnumerateDirectories(artefactDir, "*.dSYM", SearchOption.AllDirectories)
                .ToArray();
            if (dsymDirs.Length == 0) return null;

            // Zip into the system temp dir; uploader deletes after upload.
            var zipPath = Path.Combine(Path.GetTempPath(),
                $"playscope-dsym-{Guid.NewGuid():N}.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var dsym in dsymDirs)
                {
                    AddDirectoryToZip(archive, dsym, Path.GetFileName(dsym));
                }
            }
            return zipPath;
        }

        private static void AddDirectoryToZip(ZipArchive archive, string sourceDir, string entryPrefix)
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                var entryName = $"{entryPrefix}/{relative}";
                archive.CreateEntryFromFile(file, entryName);
            }
        }

        // ── HTTP upload ────────────────────────────────────────────────────
        //
        // Pure HTTP — runs on the ThreadPool, must NOT touch any Unity API.
        // The payload is everything we need, captured from the main thread
        // before this method starts.

        private static async Task UploadAsync(UploadPayload p)
        {
            string backendBase = p.BackendBase;
            string sdkKey      = p.SdkKey;
            string platform    = p.Platform;
            string appVersion  = p.AppVersion;
            string buildNumber = p.BuildNumber;
            string zipPath     = p.ZipPath;
            bool   verbose     = p.Verbose;
            // No retry policy in v1 — Unity Editor builds are interactive
            // and devs notice failures immediately. CI will see a single
            // upload attempt in build logs. Retry can join later if real-
            // world failure rate justifies it.
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sdkKey);

                using var content = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(zipPath);
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                content.Add(fileContent, "file", Path.GetFileName(zipPath));
                content.Add(new StringContent(appVersion), "app_version");
                content.Add(new StringContent(platform), "platform");
                if (!string.IsNullOrEmpty(buildNumber))
                    content.Add(new StringContent(buildNumber), "build_number");

                var url = $"{backendBase}/v1/symbols/upload";
                if (verbose) Debug.Log($"[PlayScope] POST {url}");

                var resp = await client.PostAsync(url, content).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    Debug.Log($"[PlayScope] Symbol upload OK ({platform} {appVersion}). " +
                              $"Server: {Truncate(body, 200)}");
                }
                else
                {
                    Debug.LogWarning($"[PlayScope] Symbol upload failed HTTP {(int)resp.StatusCode} " +
                                     $"{resp.StatusCode}: {Truncate(body, 200)}");
                }
            }
            catch (HttpRequestException ex)
            {
                Debug.LogWarning($"[PlayScope] Symbol upload network error: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                Debug.LogWarning("[PlayScope] Symbol upload timed out after 5 minutes.");
            }
            finally
            {
                // Clean up our temp zip (Android case uses Unity's zip
                // directly — don't delete that one). Heuristic: only delete
                // if the path is under Temp.
                if (zipPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(zipPath); } catch { /* best-effort */ }
                }
            }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s[..max] + "…";
    }
}
