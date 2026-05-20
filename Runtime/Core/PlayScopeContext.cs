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
        /// Base URL for the PlayScope ingest API. Defaults to the PlayScope production endpoint.
        /// Override only for staging or on-premise deployments.
        /// </summary>
        public string UploadEndpoint { get; set; } = "https://api.playscope.dev";

        /// <summary>
        /// Enable the ANR (Application Not Responding) watchdog. Default true.
        /// When the main thread stops responding (e.g. blocked Update, sync
        /// asset load, deadlocked native call) for longer than
        /// <see cref="AnrThresholdMs"/>, the SDK emits an <c>anr</c> event;
        /// on recovery it emits <c>anr_recovered</c> with the total stuck
        /// duration. The watchdog auto-disables in the Unity Editor — see
        /// the comment on AnrThresholdMs.
        /// </summary>
        public bool AnrDetectionEnabled { get; set; } = true;

        /// <summary>
        /// Threshold in milliseconds before a main-thread stall is reported
        /// as an ANR. Default 2000 ms. Tune up to 5000 ms to match Android's
        /// OS-level ANR threshold (less noise on GC spikes); tune down to
        /// 1000 ms on hard-60-fps games where any visible hitch is critical.
        /// Ignored when <see cref="AnrDetectionEnabled"/> is false or when
        /// running in the Unity Editor (false positives from breakpoints).
        /// </summary>
        public int AnrThresholdMs { get; set; } = 2000;

        /// <summary>
        /// Enables value-level PII regex masking on metadata and state values
        /// (in addition to the always-on key-name filter). When true, string
        /// values are scanned for emails, JWTs, bearer/basic tokens,
        /// well-known service tokens (GitHub/Stripe/Slack/AWS), Luhn-valid
        /// credit-card numbers, international phone numbers, and public
        /// IPv4 addresses. Matches are replaced in-line with placeholders
        /// like <c>[redacted-email]</c> — surrounding context is preserved.
        ///
        /// <para>
        /// Default true. Disable only if your game absolutely needs raw
        /// values to flow through (e.g. you're testing the masks themselves
        /// in CI). Disabling exposes you to GDPR / CCPA risk if user data
        /// ends up in metadata accidentally.
        /// </para>
        /// </summary>
        public bool PiiValueMasksEnabled { get; set; } = true;
    }
}
