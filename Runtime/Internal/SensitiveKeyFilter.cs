using System.Collections.Generic;
using UnityEngine;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Filters sensitive keys from state and metadata dictionaries before they are written.
    /// Applied at step [1] of the event pipeline (Runtime &amp; Local Storage Spec §4).
    /// </summary>
    internal static class SensitiveKeyFilter
    {
        // Case-insensitive substring match — if a key CONTAINS any of these substrings, it is dropped.
        private static readonly string[] BlacklistSubstrings = new[]
        {
            "password", "passwd", "secret", "token", "apikey", "api_key",
            "authtoken", "auth_token", "authorization",
            "accesstoken", "access_token", "refreshtoken", "refresh_token",
            "privatekey", "private_key", "creditcard", "credit_card",
            "cardnumber", "card_number", "cvv", "ssn"
        };

        // operation_name is explicitly NOT blacklisted — always preserved
        private const string ExemptKey = "operation_name";

        // Per-category warning flags — one warning per call category per session, not per call
        private static bool _stateWarningEmitted;
        private static bool _metadataWarningEmitted;

        // Default true so a Filter call before Initialize masks by default; Initialize overrides.
        private static bool _piiValueMasksEnabled = true;

        internal static void SetPiiValueMasksEnabled(bool enabled)
            => _piiValueMasksEnabled = enabled;

        internal static void ResetWarnings()
        {
            _stateWarningEmitted = false;
            _metadataWarningEmitted = false;
            PiiValueScanner.ResetWarnings();
        }

        /// <summary>
        /// Filters a state dictionary. Returns a new filtered dict (does not mutate input).
        /// </summary>
        internal static IReadOnlyDictionary<string, object> FilterState(IReadOnlyDictionary<string, object> input)
        {
            return Filter(input, ref _stateWarningEmitted, "state");
        }

        /// <summary>
        /// Filters a metadata dictionary. Returns a new filtered dict (does not mutate input).
        /// </summary>
        internal static IReadOnlyDictionary<string, object> FilterMetadata(IReadOnlyDictionary<string, object> input)
        {
            return Filter(input, ref _metadataWarningEmitted, "metadata");
        }

        /// <summary>
        /// Masks PII substrings in free-form log/exception text. Honors the
        /// <c>PiiValueMasksEnabled</c> toggle; reference-equal to the input when
        /// nothing matched. Never throws — on scanner failure the original text
        /// is returned so the record is still enqueued.
        /// </summary>
        internal static string MaskLogText(string text)
        {
            if (!_piiValueMasksEnabled) return text;
            try
            {
                return PiiValueScanner.MaskString(text);
            }
            catch (System.Exception ex)
            {
                PlayScopeLog.Warning("MaskLogText: PII scan failed; recording text unmasked", ex);
                return text;
            }
        }

        internal static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.Equals(ExemptKey, System.StringComparison.OrdinalIgnoreCase)) return false;
            var lower = key.ToLowerInvariant();
            foreach (var sub in BlacklistSubstrings)
                if (lower.Contains(sub)) return true;
            return false;
        }

        private static IReadOnlyDictionary<string, object> Filter(
            IReadOnlyDictionary<string, object> input,
            ref bool warningEmitted,
            string category)
        {
            if (input == null || input.Count == 0)
                return input ?? new Dictionary<string, object>();

            var result = new Dictionary<string, object>(input.Count);
            bool anyDropped = false;
            foreach (var kvp in input)
            {
                if (IsSensitiveKey(kvp.Key))
                {
                    anyDropped = true;
                    continue;
                }
                // Value-level PII pass — reference-equal when nothing matched (no
                // alloc on the common path); MaskValueDeep recurses nested dicts/lists.
                var maybeMasked = _piiValueMasksEnabled
                    ? PiiValueScanner.MaskValueDeep(kvp.Value)
                    : kvp.Value;
                result[kvp.Key] = maybeMasked;
            }
            if (anyDropped && !warningEmitted)
            {
                Debug.LogWarning("[PlayScope] One or more sensitive keys were removed from " + category + " before recording.");
                warningEmitted = true;
            }
            return result;
        }
    }
}
