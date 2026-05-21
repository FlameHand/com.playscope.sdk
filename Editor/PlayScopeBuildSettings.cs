using UnityEditor;
using UnityEngine;

namespace PlayScopeSdk.Editor
{
    /// <summary>
    /// Editor-only settings asset for build-time PlayScope tasks. Today this
    /// is just the IL2CPP symbol auto-uploader. Keep narrow: anything that
    /// has to be configured PER-PLAYTHROUGH belongs in the integrator's own
    /// runtime config (`PlayScopeContext` builder), not here.
    ///
    /// <para>
    /// Stored at <c>Assets/Editor/PlayScopeBuildSettings.asset</c>. Created
    /// on first open of <c>PlayScope ▸ Build Settings</c>. API key sits
    /// here for build-time uploads (CI / local Editor build) — for
    /// runtime ingest the integrator passes the key into
    /// <c>PlayScope.Initialize(...)</c> themselves.
    /// </para>
    ///
    /// <para>
    /// Fall-back chain for the API key the uploader uses: env var
    /// <c>PLAYSCOPE_API_KEY</c> (CI scenario) → this asset (local Editor build).
    /// </para>
    /// </summary>
    internal sealed class PlayScopeBuildSettings : ScriptableObject
    {
        internal const string AssetPath = "Assets/Editor/PlayScopeBuildSettings.asset";

        [Tooltip("Your project's PlayScope API key (ps_live_…). Used to authenticate symbol uploads. " +
                 "Leave blank in CI and set the PLAYSCOPE_API_KEY env var instead.")]
        public string ApiKey = "";

        [Tooltip("Override the backend host. Leave empty to use the default production endpoint.")]
        public string BackendUrl = "https://api.playscope.dev";

        [Tooltip("Automatically upload IL2CPP symbols on every iOS/Android build. " +
                 "Turn off if your CI manages symbols via a separate step.")]
        public bool AutoUploadSymbols = true;

        [Tooltip("Print extra diagnostics during the upload (helpful for debugging the integration).")]
        public bool Verbose = false;

        /// <summary>
        /// Load the singleton settings asset, creating it on first access.
        /// Editor-only — never called from runtime code.
        /// </summary>
        internal static PlayScopeBuildSettings LoadOrCreate()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PlayScopeBuildSettings>(AssetPath);
            if (existing != null) return existing;

            // First open — create the asset so the user has something to
            // edit in the Inspector. We don't auto-set the key; the user
            // pastes it themselves.
            var created = ScriptableObject.CreateInstance<PlayScopeBuildSettings>();
            System.IO.Directory.CreateDirectory("Assets/Editor");
            AssetDatabase.CreateAsset(created, AssetPath);
            AssetDatabase.SaveAssets();
            return created;
        }

        /// <summary>
        /// Resolved API key — env var beats the asset. Empty when neither
        /// is set (uploader should warn + skip in that case).
        /// </summary>
        internal string ResolvedApiKey
        {
            get
            {
                var env = System.Environment.GetEnvironmentVariable("PLAYSCOPE_API_KEY");
                if (!string.IsNullOrEmpty(env)) return env;
                return ApiKey ?? "";
            }
        }
    }

    /// <summary>
    /// Menu hook + settings inspector for <see cref="PlayScopeBuildSettings"/>.
    /// Surfaces under <c>PlayScope ▸ Build Settings</c> next to the existing
    /// Cache / Updates menu entries.
    /// </summary>
    internal static class PlayScopeBuildSettingsMenu
    {
        [MenuItem("PlayScope/Build Settings", priority = 100)]
        private static void Open()
        {
            var asset = PlayScopeBuildSettings.LoadOrCreate();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
