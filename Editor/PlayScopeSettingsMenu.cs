using System.IO;
using UnityEditor;
using UnityEngine;
using PlayScopeSdk;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// "PlayScope ▸ Settings" menu hook. Finds or creates the project's
    /// <c>Assets/Resources/PlayScopeSettings.asset</c> and selects it so the
    /// integrator can edit it in the Inspector.
    ///
    /// <para>
    /// We deliberately create the asset inside the integrator's project
    /// (not in the SDK package) so each game has its own SDK key, log
    /// level, etc. The SDK only ships the <see cref="PlayScopeSettings"/>
    /// type definition.
    /// </para>
    /// </summary>
    internal static class PlayScopeSettingsMenu
    {
        private const string AssetPath = "Assets/Resources/PlayScopeSettings.asset";

        [MenuItem("PlayScope/Settings", priority = 50)]
        private static void OpenSettings()
        {
            var asset = AssetDatabase.LoadAssetAtPath<PlayScopeSettings>(AssetPath);
            if (asset == null)
            {
                // Ensure Resources/ exists in the consumer's project.
                if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                {
                    AssetDatabase.CreateFolder("Assets", "Resources");
                }
                asset = ScriptableObject.CreateInstance<PlayScopeSettings>();
                AssetDatabase.CreateAsset(asset, AssetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log(
                    $"[PlayScope] Created {AssetPath}. Paste your SDK key " +
                    "(Settings → Projects in the dashboard) and adjust other " +
                    "options as needed.");
            }
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
