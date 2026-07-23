using UnityEngine;

namespace Merge2048.Integration
{
    // Decision storage only — no UI, no PlayScope calls. ConsentDialogView (Presentation)
    // renders the first-run prompt and PlayScopeBootstrapper (Integration) decides what to
    // do with the answer, per the "only Integration/ calls PlayScope.*" rule.
    public static class ConsentGate
    {
        private const string CONSENT_KEY = "Merge2048_TelemetryConsent";

        public static bool HasDecision => PlayerPrefs.HasKey(CONSENT_KEY);

        public static bool IsGranted => PlayerPrefs.GetInt(CONSENT_KEY, 0) == 1;

        public static void Grant()
        {
            PlayerPrefs.SetInt(CONSENT_KEY, 1);
            PlayerPrefs.Save();
        }

        public static void Decline()
        {
            PlayerPrefs.SetInt(CONSENT_KEY, 0);
            PlayerPrefs.Save();
        }

        // Diagnostics-only: clears the decision so the first-run dialog shows again next launch.
        public static void Reset()
        {
            PlayerPrefs.DeleteKey(CONSENT_KEY);
            PlayerPrefs.Save();
        }
    }
}
