using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Alias to disambiguate from the legacy `UnityEditor.PackageInfo` (the
// old Asset Store package metadata type) which auto-imports via
// `using UnityEditor;`. We want the modern UPM one.
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// Copies the SDK's bundled native plugins
    /// (<c>Plugins/Android/PlayScopeLifecycle.java</c>,
    /// <c>Plugins/iOS/PlayScopeLifecycle.mm</c>) out of the read-only UPM
    /// package and into <c>Assets/Plugins/PlayScope/</c> in the consumer's
    /// project on every domain reload.
    ///
    /// <para>
    /// Why this is needed: native (Java / Obj-C) files shipped inside a
    /// read-only UPM package have a long-standing reliability problem
    /// where Unity's PluginImporter platform settings from the bundled
    /// <c>.meta</c> are intermittently ignored. Symptoms observed on the
    /// 0.1.63 release:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>com.playscope.sdk.PlayScopeLifecycle NOT found in APK
    ///         (ClassNotFoundException)</c> at SDK init</item>
    ///   <item><c>_playscope_install_ios_lifecycle</c> EntryPointNotFound
    ///         on iOS</item>
    /// </list>
    /// <para>
    /// Workaround: ship the native files inside the package as the source
    /// of truth (disabled-everywhere via the package's own <c>.meta</c> so
    /// Unity doesn't try to compile them in-place), and let this installer
    /// mirror them into <c>Assets/Plugins/PlayScope/</c> where the
    /// consumer-owned <c>.meta</c> reliably applies PluginImporter
    /// settings — Android-only for the <c>.java</c>, iOS-only for the
    /// <c>.mm</c>.
    /// </para>
    /// <para>
    /// Idempotent. Re-copies only when the source bytes differ from the
    /// installed copy (catches SDK upgrades that bring a new helper
    /// version). User can force-reinstall via <c>PlayScope ▸ Reinstall
    /// Native Plugins</c>.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayScopeNativePluginInstaller
    {
        private const string DestRoot = "Assets/Plugins/PlayScope";
        private const string AndroidFile = "PlayScopeLifecycle.java";
        private const string IosFile = "PlayScopeLifecycle.mm";

        static PlayScopeNativePluginInstaller()
        {
            // Defer until after the Editor finishes domain init — running
            // file I/O + AssetDatabase calls inside the static ctor is
            // brittle.
            EditorApplication.delayCall += SyncSilently;
        }

        [MenuItem("PlayScope/Reinstall Native Plugins")]
        private static void ReinstallMenu()
        {
            var copied = SyncCore(verbose: true);
            EditorUtility.DisplayDialog(
                "PlayScope — Native Plugins",
                copied > 0
                    ? $"Installed / updated {copied} native plugin file(s) under {DestRoot}/.\n\n" +
                      "Rebuild your Android / iOS player for the new files to take effect."
                    : "Native plugins are already up to date.\n\n" +
                      "If you're still seeing ClassNotFoundException for " +
                      "com.playscope.sdk.PlayScopeLifecycle at runtime, check " +
                      "the PluginImporter Inspector for the file under " +
                      $"{DestRoot}/Android/.",
                "OK");
        }

        private static void SyncSilently()
        {
            try { SyncCore(verbose: false); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[PlayScope] Native plugin installer failed: " + ex.Message +
                    "\nUse PlayScope ▸ Reinstall Native Plugins to retry.");
            }
        }

        /// <summary>
        /// Returns the number of files newly written / overwritten.
        /// </summary>
        private static int SyncCore(bool verbose)
        {
            var pkgDir = ResolvePackageDir();
            if (string.IsNullOrEmpty(pkgDir))
            {
                if (verbose)
                    Debug.LogWarning("[PlayScope] Couldn't resolve SDK package path — native plugins not installed.");
                return 0;
            }

            int total = 0;
            total += SyncFile(
                src: Path.Combine(pkgDir, "Plugins", "Android", AndroidFile),
                dstDir: Path.Combine(DestRoot, "Android"),
                fileName: AndroidFile,
                platform: BuildTarget.Android,
                verbose: verbose);
            total += SyncFile(
                src: Path.Combine(pkgDir, "Plugins", "iOS", IosFile),
                dstDir: Path.Combine(DestRoot, "iOS"),
                fileName: IosFile,
                platform: BuildTarget.iOS,
                verbose: verbose);

            if (total > 0) AssetDatabase.Refresh();
            return total;
        }

        /// <summary>
        /// Finds the on-disk path of the consumer's resolved SDK package.
        /// PackageManager.PackageInfo.FindForAssembly returns the resolved
        /// path (Library/PackageCache/com.playscope.sdk@HASH or, for a
        /// local file: dependency, the local path) so the same logic
        /// covers both UPM cache and local-package development.
        /// </summary>
        private static string ResolvePackageDir()
        {
            try
            {
                var info = UpmPackageInfo.FindForAssembly(typeof(PlayScopeNativePluginInstaller).Assembly);
                return info?.resolvedPath;
            }
            catch { return null; }
        }

        private static int SyncFile(
            string src, string dstDir, string fileName, BuildTarget platform, bool verbose)
        {
            if (!File.Exists(src))
            {
                if (verbose)
                    Debug.LogWarning($"[PlayScope] Native plugin source missing: {src}");
                return 0;
            }

            Directory.CreateDirectory(dstDir);
            var dst = Path.Combine(dstDir, fileName);

            // Compare byte-for-byte. We're only ever syncing two ~5 KB
            // source files; cost is negligible vs. the cost of a wrong
            // overwrite breaking the consumer's Editor settings.
            var srcBytes = File.ReadAllBytes(src);
            var changed = !File.Exists(dst) || !ByteArraysEqual(srcBytes, File.ReadAllBytes(dst));
            if (!changed) return 0;

            File.WriteAllBytes(dst, srcBytes);
            if (verbose)
                Debug.Log($"[PlayScope] Installed native plugin: {dst}");

            // Pull the file into the AssetDatabase so PluginImporter exists
            // for it, then set the right platform compatibility. Unity's
            // default for a bare imported native file is "no platforms" —
            // would silently exclude it from every build.
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(dst) as PluginImporter;
            if (importer != null)
            {
                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(false);
                importer.SetCompatibleWithPlatform(BuildTarget.Android, platform == BuildTarget.Android);
                importer.SetCompatibleWithPlatform(BuildTarget.iOS, platform == BuildTarget.iOS);
                importer.SaveAndReimport();
            }
            else if (verbose)
            {
                Debug.LogWarning(
                    $"[PlayScope] Imported {dst} but couldn't read PluginImporter — " +
                    "open the Inspector and set Include Platforms manually.");
            }

            return 1;
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
