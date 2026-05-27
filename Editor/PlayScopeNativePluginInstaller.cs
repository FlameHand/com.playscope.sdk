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
    /// Copies every native plugin shipped under the package's
    /// <c>Plugins/Android/</c> and <c>Plugins/iOS/</c> top-level folders
    /// into the consumer's <c>Assets/Plugins/{Android,iOS}/</c>. Adding a
    /// new native file to the SDK is zero-touch: drop it in
    /// <c>Plugins/{Android,iOS}/</c>, the installer picks it up on the
    /// next domain reload.
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
    /// mirror them into <c>Assets/Plugins/{Android,iOS}/</c> where the
    /// consumer-owned <c>.meta</c> reliably applies PluginImporter
    /// settings.
    /// </para>
    /// <para>
    /// Idempotent. The silent on-load sync re-copies only when the source
    /// bytes differ from the installed copy (catches SDK upgrades that
    /// bring a new helper version). The menu path
    /// <c>PlayScope ▸ Reinstall Native Plugins</c> force-overwrites every
    /// file regardless of byte-equality — "I clicked reinstall, give me
    /// fresh copies" semantics.
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
            }

            if (total > 0)
            {
                AssetDatabase.Refresh();
            }
            return new SyncResult(total, installedNames);
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
            var dst = Path.Combine(dstDir, fileName);

            // Compare byte-for-byte unless forceOverwrite is on (menu path).
            // The files are a few KB each; cost is negligible vs. the cost
            // of a wrong overwrite breaking the consumer's Editor settings.
            var srcBytes = File.ReadAllBytes(src);
            var changed = forceOverwrite || !File.Exists(dst) || !ByteArraysEqual(srcBytes, File.ReadAllBytes(dst));
            if (!changed)
            {
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
