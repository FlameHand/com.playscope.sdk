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
            try { ProcessAsync(report).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                // NEVER fail the build over a symbol upload failure — the
                // store-ready artefact is more important. Just shout into
                // the console so CI logs surface it.
                Debug.LogError($"[PlayScope] Symbol upload errored unexpectedly: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static async Task ProcessAsync(BuildReport report)
        {
            var summary = report.summary;
            var target = summary.platform;

            // Quick exits: only IL2CPP iOS/Android need symbolication.
            if (target != BuildTarget.Android && target != BuildTarget.iOS)
            {
                return;
            }
            var backend = (ScriptingImplementation)PlayerSettings.GetScriptingBackend(
                BuildPipeline.GetBuildTargetGroup(target));
            if (backend != ScriptingImplementation.IL2CPP)
            {
                Debug.Log("[PlayScope] Symbol upload skipped: not an IL2CPP build.");
                return;
            }

            // Resolve config from the runtime settings asset shared with the
            // SDK (Resources/PlayScopeSettings.asset). Build-time fields
            // (AutoUploadSymbols, VerboseEditor) live on the same asset so
            // the integrator has one config surface in the Inspector.
            //
            // The asset path inside the consumer's project, NOT inside the
            // SDK package — AssetDatabase resolves the consumer copy. If
            // it doesn't exist, the integrator hasn't run PlayScope ▸
            // Settings yet and the upload won't work; we warn cleanly.
            var settings = AssetDatabase.LoadAssetAtPath<PlayScopeSettings>(
                "Assets/Resources/PlayScopeSettings.asset");
            if (settings == null)
            {
                Debug.LogWarning(
                    "[PlayScope] Symbol upload skipped: Assets/Resources/PlayScopeSettings.asset " +
                    "not found. Create it via PlayScope ▸ Settings.");
                return;
            }
            if (!settings.AutoUploadSymbols)
            {
                Debug.Log("[PlayScope] Symbol upload skipped: AutoUploadSymbols is off in PlayScope Settings.");
                return;
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
                return;
            }

            string platform = target == BuildTarget.iOS ? "ios" : "android";
            string appVersion = PlayerSettings.bundleVersion ?? "0.0.0";
            string buildNumber = target == BuildTarget.iOS
                ? PlayerSettings.iOS.buildNumber
                : PlayerSettings.Android.bundleVersionCode.ToString();

            // Locate the symbol payload.
            string zipPath = target == BuildTarget.Android
                ? FindAndroidSymbolsZip(report)
                : await PrepareIosDsymZipAsync(report);

            if (zipPath == null)
            {
                Debug.LogWarning(
                    target == BuildTarget.Android
                        ? "[PlayScope] No Android .symbols.zip found. Enable " +
                          "Player Settings ▸ Other Settings ▸ Configuration ▸ Create symbols.zip → 'Public' " +
                          "and rebuild for crash resolution to work."
                        : "[PlayScope] No iOS .dSYM bundles found in the build output. " +
                          "Verify Xcode generated dSYM files (Debug Information Format = DWARF with dSYM File).");
                return;
            }

            var sizeBytes = new FileInfo(zipPath).Length;
            if (settings.VerboseEditor)
            {
                Debug.Log($"[PlayScope] Uploading symbols: platform={platform} version={appVersion} build={buildNumber} size={sizeBytes / 1024}KB");
            }

            var backendUrl = string.IsNullOrEmpty(settings.BackendUrl)
                ? "https://api.playscope.dev"
                : settings.BackendUrl;
            await UploadAsync(
                backendUrl.TrimEnd('/'), sdkKey,
                platform, appVersion, buildNumber, zipPath,
                settings.VerboseEditor);
        }

        // ── Android — Unity already zips for us ─────────────────────────────

        private static string FindAndroidSymbolsZip(BuildReport report)
        {
            // Unity's symbols.zip lands beside the APK / AAB. Look in the
            // output directory plus its parent (Unity's "Build" sometimes
            // puts the zip one level up).
            var artefactPath = report.summary.outputPath;
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

        private static async Task<string> PrepareIosDsymZipAsync(BuildReport report)
        {
            var artefactDir = report.summary.outputPath;
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
            await Task.CompletedTask;
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

        private static async Task UploadAsync(
            string backendBase, string sdkKey,
            string platform, string appVersion, string buildNumber,
            string zipPath, bool verbose)
        {
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

                var resp = await client.PostAsync(url, content);
                var body = await resp.Content.ReadAsStringAsync();

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
