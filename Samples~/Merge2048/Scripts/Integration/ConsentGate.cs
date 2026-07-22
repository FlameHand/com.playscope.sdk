using UnityEngine;

namespace Merge2048.Integration
{
    // Minimal STUB consent policy. Building an interactive consent-UI was
    // explicitly descoped for this sample — the point is teaching the SDK
    // integration pattern, not UI construction. The auto-grant default below
    // is NOT a real compliance posture; a shipping game must replace
    // ResolveForSession with its own CMP/consent-UI decision.
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

        // Demo default: auto-grants on first run if no decision is stored yet. A real
        // game must replace this with its own CMP/consent-UI decision BEFORE calling
        // Initialize() — call Decline() yourself (e.g. from a debug menu) to exercise
        // the opt-out path, where PlayScope stays a global no-op for the session.
        public static bool ResolveForSession()
        {
            if (!HasDecision)
            {
                Grant();
            }

            return IsGranted;
        }
    }
}
