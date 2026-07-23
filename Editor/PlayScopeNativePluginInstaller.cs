using System;
using System.Collections.Generic;
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
    /// Mirrors the package's <c>Plugins/{Android,iOS}/</c> native files into the
    /// consumer's <c>Assets/Plugins/{Android,iOS}/</c> on domain reload.
    ///
    /// <para>
    /// Native files shipped inside a read-only UPM package hit a reliability
    /// problem where Unity intermittently ignores the bundled <c>.meta</c>'s
    /// PluginImporter settings (ClassNotFoundException / EntryPointNotFound at
    /// runtime). Workaround: ship them disabled-everywhere as the source of truth
    /// and mirror into Assets/Plugins/ where the consumer-owned <c>.meta</c>
    /// reliably applies platform settings.
    /// </para>
    /// <para>
    /// Idempotent — the on-load sync re-copies only when source bytes differ;
    /// <c>PlayScope ▸ Reinstall Native Plugins</c> force-overwrites regardless.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayScopeNativePluginInstaller
    {
        // CANONICAL Unity native plugin locations. Anything under
        // Assets/Plugins/Android/ ends up in the Android Gradle Library
        // module; anything under Assets/Plugins/iOS/ ends up linked into
        // the generated Xcode project.
        private static readonly (string SrcSubdir, string DstDir, BuildTarget Platform)[] PLATFORM_DIRS =
        {
            ("Plugins/Android", "Assets/Plugins/Android", BuildTarget.Android),
            ("Plugins/iOS",     "Assets/Plugins/iOS",     BuildTarget.iOS),
        };

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
            var result = SyncCore(verbose: true, forceOverwrite: true);
            string message;
            if (result.Count > 0)
            {
                var listBuilder = new System.Text.StringBuilder();
                foreach (var name in result.Names)
                {
                    listBuilder.Append("  • ").Append(name).Append('\n');
                }
                message =
                    $"Installed / updated {result.Count} native plugin file(s):\n" +
                    listBuilder +
                    "\nRebuild your Android / iOS player for the new files to take effect.";
            }
            else
            {
                message =
                    "No native plugin files found in the SDK package — nothing to install.\n\n" +
                    "If you expected files to be copied, check that the package " +
                    "ships Plugins/Android/ or Plugins/iOS/ folders.";
            }
            EditorUtility.DisplayDialog("PlayScope — Native Plugins", message, "OK");
        }

        private static void SyncSilently()
        {
            try { SyncCore(verbose: false, forceOverwrite: false); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[PlayScope] Native plugin installer failed: " + ex.Message +
                    "\nUse PlayScope ▸ Reinstall Native Plugins to retry.");
            }
        }

        private readonly struct SyncResult
        {
            public readonly int Count;
            public readonly List<string> Names;
            public SyncResult(int count, List<string> names) { Count = count; Names = names; }
        }

        private static SyncResult SyncCore(bool verbose, bool forceOverwrite)
        {
            var installedNames = new List<string>();
            var pkgDir = ResolvePackageDir();
            if (string.IsNullOrEmpty(pkgDir))
            {
                if (verbose)
                {
                    Debug.LogWarning("[PlayScope] Couldn't resolve SDK package path — native plugins not installed.");
                }
                return new SyncResult(0, installedNames);
            }

            int total = 0;
            foreach (var entry in PLATFORM_DIRS)
            {
                var srcDir = Path.Combine(pkgDir, entry.SrcSubdir);
                if (!Directory.Exists(srcDir))
                {
                    continue;
                }

                // Top-level only — frameworks/aar that live in subdirectories
                // are out of scope for this installer.
                var files = Directory.GetFiles(srcDir);
                foreach (var srcFile in files)
                {
                    var fileName = Path.GetFileName(srcFile);
                    if (fileName.StartsWith("."))
                    {
                        continue;
                    }
                    if (fileName.EndsWith(".meta"))
                    {
                        // Consumer-side .meta files are generated fresh by Unity;
                        // copying ours would create GUID conflicts.
                        continue;
                    }

                    int copied = SyncFile(srcFile, entry.DstDir, fileName, entry.Platform, verbose, forceOverwrite);
                    if (copied > 0)
                    {
                        installedNames.Add($"{entry.DstDir}/{fileName}");
                        total += copied;
                    }
                }

                // Android only: recurse into libs/ subtree, preserving
                // libs/{ABI}/ structure. Native .so files require an
                // explicit PluginImporter CPU setting per-ABI on top of
                // the standard "Android platform = true" toggle —
                // without it Unity ships the wrong slice (or none at
                // all) into the APK and the System.loadLibrary call at
                // SDK init fails with UnsatisfiedLinkError.
                if (entry.Platform == BuildTarget.Android)
                {
                    var libsSrcDir = Path.Combine(srcDir, "libs");
                    if (Directory.Exists(libsSrcDir))
                    {
                        total += SyncAndroidLibs(libsSrcDir, entry.DstDir, installedNames, verbose, forceOverwrite);
                    }
                }
            }

            if (total > 0)
            {
                AssetDatabase.Refresh();
            }
            return new SyncResult(total, installedNames);
        }

        // ABI directory name → Unity PluginImporter CPU value. Lockstep with
        // the build-native.yml matrix in the SDK repo. Anything not in the
        // map gets skipped with a warning — better than mis-tagging a slice
        // and shipping a broken APK that loads the wrong arch.
        private static readonly (string AbiDir, string Cpu)[] ANDROID_ABIS =
        {
            ("arm64-v8a",   "ARM64"),
            ("armeabi-v7a", "ARMv7"),
            ("x86_64",      "X86_64"),
        };

        private static int SyncAndroidLibs(
            string libsSrcDir, string dstRootDir, List<string> installedNames,
            bool verbose, bool forceOverwrite)
        {
            int total = 0;
            for (int i = 0; i < ANDROID_ABIS.Length; i++)
            {
                var abi = ANDROID_ABIS[i];
                var abiSrcDir = Path.Combine(libsSrcDir, abi.AbiDir);
                if (!Directory.Exists(abiSrcDir))
                {
                    continue;
                }
                // AssetDatabase paths must be forward-slash relative to the project;
                // Path.Combine emits '\' on Windows, which makes ImportAsset/GetAtPath
                // silently no-op and leaves the PluginImporter unconfigured.
                var abiDstDir = Path.Combine(dstRootDir, "libs", abi.AbiDir).Replace('\\', '/');
                string[] files;
                try
                {
                    files = Directory.GetFiles(abiSrcDir);
                }
                catch (Exception ex)
                {
                    if (verbose)
                    {
                        Debug.LogWarning($"[PlayScope] Native plugin installer: cannot list {abiSrcDir}: {ex.Message}");
                    }
                    continue;
                }
                foreach (var srcFile in files)
                {
                    var fileName = Path.GetFileName(srcFile);
                    if (fileName.StartsWith("."))
                    {
                        continue;
                    }
                    if (fileName.EndsWith(".meta"))
                    {
                        continue;
                    }
                    int copied = SyncAndroidNativeFile(
                        srcFile, abiDstDir, fileName, abi.Cpu, verbose, forceOverwrite);
                    if (copied > 0)
                    {
                        installedNames.Add($"{abiDstDir}/{fileName}");
                        total += copied;
                    }
                }
            }
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
            string src, string dstDir, string fileName, BuildTarget platform, bool verbose, bool forceOverwrite)
        {
            if (!File.Exists(src))
            {
                if (verbose)
                {
                    Debug.LogWarning($"[PlayScope] Native plugin source missing: {src}");
                }
                return 0;
            }

            Directory.CreateDirectory(dstDir);
            // AssetDatabase paths must be forward-slash relative to the project;
            // Path.Combine emits '\' on Windows, which makes ImportAsset/GetAtPath
            // silently no-op and leaves the PluginImporter unconfigured.
            var dst = Path.Combine(dstDir, fileName).Replace('\\', '/');

            // Compare byte-for-byte unless forceOverwrite is on (menu path).
            // The files are a few KB each; cost is negligible vs. the cost
            // of a wrong overwrite breaking the consumer's Editor settings.
            var srcBytes = File.ReadAllBytes(src);
            var changed = forceOverwrite || !File.Exists(dst) || !ByteArraysEqual(srcBytes, File.ReadAllBytes(dst));
            if (!changed)
            {
                EnsurePlatformImporterHealthy(dst, platform);
                return 0;
            }

            File.WriteAllBytes(dst, srcBytes);
            if (verbose)
            {
                Debug.Log($"[PlayScope] Installed native plugin: {dst}");
            }

            // Pull the file into the AssetDatabase so PluginImporter exists
            // for it, then set the right platform compatibility. Unity's
            // default for a bare imported native file is "no platforms" —
            // would silently exclude it from every build.
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(dst) as PluginImporter;
            if (importer != null)
            {
                ConfigurePlatformImporter(importer, platform);
            }
            else
            {
                Debug.LogWarning(
                    $"[PlayScope] Imported {dst} but couldn't read PluginImporter — " +
                    "open the Inspector and set Include Platforms manually.");
            }

            return 1;
        }

        private static void ConfigurePlatformImporter(PluginImporter importer, BuildTarget platform)
        {
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, platform == BuildTarget.Android);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, platform == BuildTarget.iOS);
            importer.SaveAndReimport();
        }

        private static bool IsPlatformImporterHealthy(PluginImporter importer, BuildTarget platform)
        {
            return !importer.GetCompatibleWithAnyPlatform()
                && !importer.GetCompatibleWithEditor()
                && importer.GetCompatibleWithPlatform(BuildTarget.Android) == (platform == BuildTarget.Android)
                && importer.GetCompatibleWithPlatform(BuildTarget.iOS) == (platform == BuildTarget.iOS);
        }

        // Self-heal: a consumer who last synced on Windows before the dst
        // normalization fix above may already have a byte-identical file on
        // disk sitting behind a broken/default PluginImporter — the !changed
        // short-circuit would otherwise never touch it again.
        private static void EnsurePlatformImporterHealthy(string dst, BuildTarget platform)
        {
            var importer = AssetImporter.GetAtPath(dst) as PluginImporter;
            if (importer != null && IsPlatformImporterHealthy(importer, platform))
            {
                return;
            }

            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            importer = AssetImporter.GetAtPath(dst) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning(
                    $"[PlayScope] {dst} exists on disk but has no PluginImporter — " +
                    "open the Inspector and set Include Platforms manually.");
                return;
            }
            ConfigurePlatformImporter(importer, platform);
        }

        /// <summary>
        /// Mirrors <see cref="SyncFile"/> but for ABI-scoped Android
        /// native libraries. Distinct from SyncFile because the
        /// PluginImporter settings need an additional <c>CPU</c>
        /// platform-data slot — without it Unity ships the wrong slice
        /// (or none) into the APK and System.loadLibrary fails with
        /// UnsatisfiedLinkError.
        /// </summary>
        private static int SyncAndroidNativeFile(
            string src, string dstDir, string fileName, string cpu, bool verbose, bool forceOverwrite)
        {
            if (!File.Exists(src))
            {
                if (verbose)
                {
                    Debug.LogWarning($"[PlayScope] Native plugin source missing: {src}");
                }
                return 0;
            }

            Directory.CreateDirectory(dstDir);
            // AssetDatabase paths must be forward-slash relative to the project;
            // Path.Combine emits '\' on Windows, which makes ImportAsset/GetAtPath
            // silently no-op and leaves the PluginImporter unconfigured.
            var dst = Path.Combine(dstDir, fileName).Replace('\\', '/');

            var srcBytes = File.ReadAllBytes(src);
            var changed = forceOverwrite || !File.Exists(dst) || !ByteArraysEqual(srcBytes, File.ReadAllBytes(dst));
            if (!changed)
            {
                EnsureAndroidNativeImporterHealthy(dst, cpu);
                return 0;
            }

            File.WriteAllBytes(dst, srcBytes);
            if (verbose)
            {
                Debug.Log($"[PlayScope] Installed native plugin: {dst} (CPU={cpu})");
            }

            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(dst) as PluginImporter;
            if (importer != null)
            {
                ConfigureAndroidNativeImporter(importer, cpu);
            }
            else
            {
                Debug.LogWarning(
                    $"[PlayScope] Imported {dst} but couldn't read PluginImporter — " +
                    $"open the Inspector and set Include Platforms manually (Android, CPU={cpu}).");
            }

            return 1;
        }

        private static void ConfigureAndroidNativeImporter(PluginImporter importer, string cpu)
        {
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            importer.SetPlatformData(BuildTarget.Android, "CPU", cpu);
            importer.SaveAndReimport();
        }

        private static bool IsAndroidNativeImporterHealthy(PluginImporter importer, string cpu)
        {
            return !importer.GetCompatibleWithAnyPlatform()
                && !importer.GetCompatibleWithEditor()
                && importer.GetCompatibleWithPlatform(BuildTarget.Android)
                && !importer.GetCompatibleWithPlatform(BuildTarget.iOS)
                && importer.GetPlatformData(BuildTarget.Android, "CPU") == cpu;
        }

        // Self-heal: a consumer who last synced on Windows before the dst
        // normalization fix above may already have a byte-identical .so on
        // disk sitting behind a broken/default PluginImporter — the !changed
        // short-circuit would otherwise never touch it again.
        private static void EnsureAndroidNativeImporterHealthy(string dst, string cpu)
        {
            var importer = AssetImporter.GetAtPath(dst) as PluginImporter;
            if (importer != null && IsAndroidNativeImporterHealthy(importer, cpu))
            {
                return;
            }

            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            importer = AssetImporter.GetAtPath(dst) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning(
                    $"[PlayScope] {dst} exists on disk but has no PluginImporter — " +
                    $"open the Inspector and set Include Platforms manually (Android, CPU={cpu}).");
                return;
            }
            ConfigureAndroidNativeImporter(importer, cpu);
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
