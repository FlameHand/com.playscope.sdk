using System;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using PlayScopeSdk.Internal;

namespace PlayScopeSdk.Editor
{
    internal sealed class PlayScopeBuildInfoGenerator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                var commit = ResolveCommit();
                var branch = ResolveBranch();

                const string assetPath = "Assets/Resources/PlayScopeBuildInfo.asset";
                var info = AssetDatabase.LoadAssetAtPath<PlayScopeBuildInfo>(assetPath);
                if (info == null)
                {
                    if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                    {
                        AssetDatabase.CreateFolder("Assets", "Resources");
                    }
                    info = ScriptableObject.CreateInstance<PlayScopeBuildInfo>();
                    AssetDatabase.CreateAsset(info, assetPath);
                }
                info.BuildCommit = commit;
                info.BuildBranch = branch;
                EditorUtility.SetDirty(info);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PlayScope] PlayScopeBuildInfoGenerator failed — build_commit/build_branch will be empty. {ex}");
            }
        }

        private static string ResolveCommit()
        {
            var val = Environment.GetEnvironmentVariable("PLAYSCOPE_BUILD_COMMIT");
            if (!string.IsNullOrEmpty(val)) return val;
            val = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (!string.IsNullOrEmpty(val)) return val;
            return RunGit("rev-parse HEAD");
        }

        private static string ResolveBranch()
        {
            var val = Environment.GetEnvironmentVariable("PLAYSCOPE_BUILD_BRANCH");
            if (!string.IsNullOrEmpty(val)) return val;
            val = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
            if (!string.IsNullOrEmpty(val)) return val;
            return RunGit("rev-parse --abbrev-ref HEAD");
        }

        private static string RunGit(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(Application.dataPath, "..")),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return "";
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(3000);
                    return proc.ExitCode == 0 ? output : "";
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
