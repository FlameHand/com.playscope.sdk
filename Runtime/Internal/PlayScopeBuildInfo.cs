using UnityEngine;

namespace PlayScopeSdk.Internal
{
    internal sealed class PlayScopeBuildInfo : ScriptableObject
    {
        internal const string RESOURCE_NAME = "PlayScopeBuildInfo";

        public string BuildCommit = "";
        public string BuildBranch = "";

        internal static PlayScopeBuildInfo Load()
        {
            return Resources.Load<PlayScopeBuildInfo>(RESOURCE_NAME);
        }
    }
}
