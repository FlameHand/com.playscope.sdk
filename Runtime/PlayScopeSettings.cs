using UnityEngine;

namespace PlayScopeSdk
{
    /// <summary>
    /// Per-project PlayScope configuration loaded from
    /// <c>Resources/PlayScopeSettings.asset</c> at SDK initialise time. Use
    /// the <c>PlayScope ▸ Settings</c> Editor menu to create + edit the
    /// asset; the parameterless <see cref="PlayScope.Initialize()"/>
    /// overload uses these values.
    ///
    /// <para>
    /// Runtime fields (SdkKey, BackendUrl, MinLogLevel) are read every
    /// time the SDK initialises. Build-time fields (AutoUploadSymbols,
    /// VerboseEditor) are read by the Editor build postprocessor and
    /// have zero runtime effect.
    /// </para>
    ///
    /// <para>
    /// The SDK key is intentionally embedded in the built app — it's a
    /// project identifier, not a secret. Same model as Firebase config
    /// or Sentry DSN. Don't commit it to a public repo only if you're
    /// running a non-public dashboard.
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "PlayScopeSettings", menuName = "PlayScope/Settings Asset")]
    public class PlayScopeSettings : ScriptableObject
    {
        // ── Identity ────────────────────────────────────────────────────────

        [Header("Identity")]
        [Tooltip("Your project's PlayScope SDK key (ps_live_…). " +
                 "Get it from the dashboard → Settings → Projects → SDK key. " +
                 "If empty, the SDK refuses to initialise.")]
        public string SdkKey = "";

        [Tooltip("Base URL for the PlayScope backend. Leave default unless you " +
                 "are running a self-hosted / staging deployment.")]
        public string BackendUrl = "https://api.playscope.dev";

        // ── Runtime ─────────────────────────────────────────────────────────

        [Header("Runtime")]
        [Tooltip("Capture Unity's log stream (Application.logMessageReceived). " +
                 "Anything at or above MinLogLevel reaches the dashboard. " +
                 "Internal [PlayScope] logs are always excluded.")]
        public bool AutoCaptureUnityLogs = true;

        [Tooltip("Minimum log level to capture. Drops happen BEFORE upload — " +
                 "Debug/Info on a Free plan are dropped at this layer so the " +
                 "player's mobile data isn't burned on data we'd drop anyway. " +
                 "Wrapper code can read PlayScope.Settings.MinLogLevel and " +
                 "apply the same filter to its own non-SDK logs.")]
        public LogLevel MinLogLevel = LogLevel.Warning;

        [Tooltip("Watchdog for main-thread stalls. Emits 'anr' on stalls longer " +
                 "than the threshold. Auto-disabled in the Editor (breakpoints " +
                 "produce false positives).")]
        public bool AnrDetectionEnabled = true;

        [Tooltip("ANR threshold in milliseconds. Default 2000. Tune up to 5000 " +
                 "to match Android OS-level ANR; tune down to 1000 on hard-60fps " +
                 "titles where any visible hitch matters.")]
        public int AnrThresholdMs = 2000;

        [Tooltip("Enable value-level PII regex masking (emails, JWTs, bearer " +
                 "tokens, Luhn-valid cards, phones, IPv4) on metadata and state. " +
                 "Default on. Disable ONLY if your CI is testing the masks " +
                 "themselves — disabling exposes you to GDPR/CCPA risk.")]
        public bool PiiValueMasksEnabled = true;

        // ── Build-time (Editor only — no runtime cost) ──────────────────────

        [Header("Build-time")]
        [Tooltip("Automatically upload IL2CPP symbol files (Android symbols.zip " +
                 "or iOS dSYM bundle) on every iOS/Android build. Turn off if " +
                 "your CI runs a separate symbol-upload step.")]
        public bool AutoUploadSymbols = true;

        [Tooltip("Print extra console output during the Editor build / symbol " +
                 "upload step. Useful when integrating; leave off after that.")]
        public bool VerboseEditor = false;

        // ── Cached Resources lookup ─────────────────────────────────────────

        private static PlayScopeSettings _cached;

        /// <summary>
        /// Returns the project's <c>PlayScopeSettings.asset</c> from
        /// <c>Resources/</c>, cached after the first call. Null if the
        /// integrator hasn't created the asset yet — call sites should
        /// log a clear "create the asset via PlayScope ▸ Settings" message.
        /// </summary>
        public static PlayScopeSettings Load()
        {
            if (_cached != null) return _cached;
            // ScriptableObject loads as null when no asset exists at the
            // path. Don't throw — let the caller surface a useful message.
            _cached = Resources.Load<PlayScopeSettings>("PlayScopeSettings");
            return _cached;
        }
    }
}
