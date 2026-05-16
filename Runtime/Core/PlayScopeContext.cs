using System.Collections.Generic;

namespace PlayScopeSdk
{
    /// <summary>
    /// Configuration passed to <see cref="PlayScope.Initialize"/>.
    /// </summary>
    public sealed class PlayScopeContext
    {
        /// <summary>
        /// Required. API key from the PlayScope dashboard (ps_live_...).
        /// If null or empty, SDK enters disabled state — all calls become no-ops.
        /// </summary>
        public string ApiKey { get; set; }

        /// <summary>
        /// Automatically capture Unity log stream. Default: false.
        /// SDK internal logs tagged [PlayScope] are always excluded from auto-capture.
        /// </summary>
        public bool AutoCaptureUnityLogs { get; set; } = false;

        /// <summary>
        /// Minimum log level captured when AutoCaptureUnityLogs is true. Default: Warning.
        /// </summary>
        public LogLevel AutoCaptureMinLevel { get; set; } = LogLevel.Warning;

        /// <summary>
        /// Optional session-start metadata (app_version, build_number, environment, platform, etc.).
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata { get; set; }

        /// <summary>
        /// Base URL for the PlayScope ingest API. Defaults to "https://api.playscope.io".
        /// Override only for on-premise or staging deployments.
        /// </summary>
        public string UploadEndpoint { get; set; } = "https://api.playscope.io";
    }
}
