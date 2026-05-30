using System;
using System.Collections.Generic;

namespace PlayScopeSdk
{
    /// <summary>
    /// Configuration passed to <see cref="PlayScope.Initialize(PlayScopeContext)"/>.
    /// Usually you don't construct this — the parameterless
    /// <see cref="PlayScope.Initialize()"/> builds it from your
    /// <see cref="PlayScopeSettings"/> ScriptableObject.
    /// </summary>
    public sealed class PlayScopeContext
    {
        /// <summary>
        /// Required. SDK key from the PlayScope dashboard (ps_live_...).
        /// If null or empty, the SDK enters disabled state — all calls no-op.
        /// </summary>
        public string SdkKey { get; set; }

        /// <summary>
        /// Obsolete: use <see cref="SdkKey"/>. Pass-through alias so existing
        /// <c>new PlayScopeContext { ApiKey = "…" }</c> initialisers keep compiling.
        /// </summary>
        [Obsolete("Renamed to SdkKey. The old name remains as an alias for back-compat.")]
        public string ApiKey
        {
            get => SdkKey;
            set => SdkKey = value;
        }

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
        /// Base URL for the PlayScope ingest API. Defaults to the PlayScope production endpoint.
        /// Override only for staging or on-premise deployments.
        /// </summary>
        public string UploadEndpoint { get; set; } = "https://api.playscope.dev";

        /// <summary>
        /// Enable the ANR (Application Not Responding) watchdog. Default true.
        /// When the main thread stalls longer than <see cref="AnrThresholdMs"/>,
        /// the SDK emits an <c>anr</c> event; on recovery, <c>anr_recovered</c>
        /// with the total stuck duration. Auto-disabled in the Unity Editor.
        /// </summary>
        public bool AnrDetectionEnabled { get; set; } = true;

        /// <summary>
        /// Threshold in milliseconds before a main-thread stall is reported as
        /// an ANR. Default 2000 ms. Tune up to 5000 ms to match Android's OS-level
        /// threshold (less GC-spike noise); down to 1000 ms on hard-60-fps games.
        /// Ignored when <see cref="AnrDetectionEnabled"/> is false or in the
        /// Editor (breakpoints cause false positives).
        /// </summary>
        public int AnrThresholdMs { get; set; } = 2000;

        /// <summary>
        /// Enables value-level PII regex masking on metadata and state values
        /// (in addition to the always-on key-name filter). When true, string
        /// values are scanned for emails, JWTs, bearer/basic tokens, well-known
        /// service tokens (GitHub/Stripe/Slack/AWS), Luhn-valid credit cards,
        /// international phone numbers, and public IPv4 addresses; matches are
        /// replaced in-line with placeholders like <c>[redacted-email]</c>.
        /// Default true. Disabling exposes you to GDPR / CCPA risk if user data
        /// accidentally ends up in metadata.
        /// </summary>
        public bool PiiValueMasksEnabled { get; set; } = true;
    }
}
