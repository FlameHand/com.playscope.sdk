using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Value-level PII detection complementing <see cref="SensitiveKeyFilter"/>
    /// (key-name only) — catches PII in a value under an innocent key (an email
    /// in an exception message, a JWT in a logged URL). Masks in-line with
    /// placeholders like <c>[redacted-email]</c> rather than dropping, so context
    /// survives. Patterns are conservative (a <c>purchase_amount: 4242424242</c>
    /// must NOT match a card) and ordered specific-first (JWT/bearer before long
    /// digit runs). Static compiled regexes; not a hot path.
    /// </summary>
    internal static class PiiValueScanner
    {
        // Simplified, not RFC-correct — missing exotic forms is fine,
        // false-positives on common ids are not.
        private static readonly Regex EmailRx = new Regex(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // JWT — three base64url segments. First must start `eyJ` (base64 of `{"`),
        // a specific anchor; without it any "foo.bar.baz" matches.
        private static readonly Regex JwtRx = new Regex(
            @"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Bearer / Basic auth headers — captures the whole "Bearer xyz" for natural redaction.
        private static readonly Regex AuthHeaderRx = new Regex(
            @"\b(Bearer|Basic|Token)\s+[A-Za-z0-9._\-=+/]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Well-known token prefixes — ~no false-positive chance. Catches tokens
        // used outside an auth header. Extend as new patterns spread.
        private static readonly Regex KnownTokenPrefixRx = new Regex(
            @"\b(?:" +
                @"(?:ghp|gho|ghu|ghs|ghr" +     // GitHub tokens
                @"|npm" +                       // npm tokens
                @"|sk_live|sk_test|pk_live|pk_test" + // Stripe
                @")_[A-Za-z0-9]{16,}" +
                @"|(?:AKIA|ASIA)[A-Z0-9]{16}" + // AWS access keys — no separator, fixed length
                @"|xox[abps]-[A-Za-z0-9][A-Za-z0-9\-]{14,}[A-Za-z0-9]" + // Slack — hyphen-separated
            @")\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Credit card — 13-19 digits with optional space/hyphen separators.
        // Luhn-gated (see MaskString) to suppress the long-integer false-positive avalanche.
        // Last element is a bare \d so the match ends ON a digit — `\d[ \-]?` as
        // the final unit swallows a trailing separator/space into the redaction.
        private static readonly Regex CreditCardCandidateRx = new Regex(
            @"\b(?:\d[ \-]?){12,18}\d\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // International phone — leading `+` anchor cuts false positives. Bare
        // "555-1234" forms NOT matched (collide with versions / ticket IDs).
        private static readonly Regex PhoneRx = new Regex(
            @"\+\d[\d \-().]{6,18}\d",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // IPv4 — private ranges (127./10./192.168./172.16-31.) NOT redacted; they're debug info, not PII.
        private static readonly Regex Ipv4Rx = new Regex(
            @"\b(?<o1>\d{1,3})\.(?<o2>\d{1,3})\.(?<o3>\d{1,3})\.(?<o4>\d{1,3})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Warn once per session on first mask — per-value would flood a chatty log.
        private static int _warningEmitted;

        /// <summary>
        /// Reset the once-per-session warning flag. Called from
        /// <see cref="PlayScopeRuntime.Initialize"/> via
        /// <see cref="SensitiveKeyFilter.ResetWarnings"/>.
        /// </summary>
        internal static void ResetWarnings()
        {
            System.Threading.Interlocked.Exchange(ref _warningEmitted, 0);
        }

        /// <summary>
        /// Returns the input with detected PII substrings replaced by category
        /// placeholders; reference-equal to the input when nothing matched.
        /// </summary>
        internal static string MaskString(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            // Fast-path: skip the ~10µs regex sweep when no anchor char is present (~100ns).
            if (!MightContainPii(value)) return value;

            string original = value;
            string result = value;

            // Order matters — more specific first to avoid partial overlap.
            result = JwtRx.Replace(result, "[redacted-jwt]");
            result = AuthHeaderRx.Replace(result, m => m.Groups[1].Value + " [redacted]");
            result = KnownTokenPrefixRx.Replace(result, "[redacted-token]");
            result = EmailRx.Replace(result, "[redacted-email]");

            // Luhn gate so we don't eat every long integer.
            result = CreditCardCandidateRx.Replace(result, m =>
                IsLuhnValid(StripNonDigits(m.Value))
                    ? "[redacted-card]"
                    : m.Value);

            result = PhoneRx.Replace(result, "[redacted-phone]");

            // IPv4 with the private-range carve-out.
            result = Ipv4Rx.Replace(result, m =>
                IsPrivateIpv4(m) ? m.Value : "[redacted-ip]");

            if (!ReferenceEquals(result, original) && result != original)
            {
                EmitWarningOnce();
            }
            return result;
        }

        /// <summary>
        /// Walks a value of unknown shape (string / dict / list) and returns
        /// a copy with all string leaves run through <see cref="MaskString"/>.
        /// Non-string scalars (int, bool, double, etc.) and nested
        /// dicts/lists are recursed; everything else passes through.
        /// </summary>
        internal static object MaskValueDeep(object value)
        {
            if (value is string s) return MaskString(s);
            if (value is IReadOnlyDictionary<string, object> dict)
            {
                var output = new Dictionary<string, object>(dict.Count);
                // Keys aren't re-checked — SensitiveKeyFilter does that, before us.
                foreach (var kv in dict)
                {
                    output[kv.Key] = MaskValueDeep(kv.Value);
                }
                return output;
            }
            if (value is Dictionary<string, object> mutDict)
            {
                var output = new Dictionary<string, object>(mutDict.Count);
                foreach (var kv in mutDict)
                {
                    output[kv.Key] = MaskValueDeep(kv.Value);
                }
                return output;
            }
            if (value is System.Collections.IList list && !(value is string))
            {
                var output = new List<object>(list.Count);
                foreach (var item in list) output.Add(MaskValueDeep(item));
                return output;
            }
            return value;
        }

        // ── Helpers ──────────────────────────────────────────────────────

        // Cheap single-pass anchor scan — bail before touching the regex engine.
        private static bool MightContainPii(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // Email anchor
                if (c == '@') return true;
                // Phone anchor (+ followed by digit)
                if (c == '+' && i + 1 < s.Length && char.IsDigit(s[i + 1])) return true;
                // Any digit — let the regex decide (card / phone / ipv4).
                if (c >= '0' && c <= '9') return true;
                // JWT anchor — eyJ at start of a token.
                if (c == 'e' && i + 2 < s.Length && s[i + 1] == 'y' && s[i + 2] == 'J')
                    return true;
                // Auth-header anchor.
                if ((c == 'B' || c == 'b') && i + 6 < s.Length)
                {
                    if (string.Compare(s, i, "Bearer", 0, 6, StringComparison.OrdinalIgnoreCase) == 0) return true;
                    if (string.Compare(s, i, "Basic ", 0, 6, StringComparison.OrdinalIgnoreCase) == 0) return true;
                }
                // Known-token-prefix anchor — digit-free tokens (sk_test_aB…)
                // never trip the digit anchor above. Word-start gate first so
                // snake_case state keys (UpdateState hot path, up to 60 Hz)
                // skip the forward check at every '_'-separated segment.
                if ((c == 'g' || c == 'n' || c == 's' || c == 'p' || c == 'x' || c == 'A')
                    && (i == 0 || !IsWordChar(s[i - 1]))
                    && IsKnownTokenPrefixAt(s, i))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        }

        // Forward check for the KnownTokenPrefixRx anchors — single pass, no
        // allocation, no regex. Only called at a word start whose first char
        // matched the dispatch set in MightContainPii.
        private static bool IsKnownTokenPrefixAt(string s, int i)
        {
            char c = s[i];
            if (c == 'g') // ghp_ gho_ ghu_ ghs_ ghr_
            {
                if (i + 3 >= s.Length || s[i + 1] != 'h' || s[i + 3] != '_') return false;
                char k = s[i + 2];
                return k == 'p' || k == 'o' || k == 'u' || k == 's' || k == 'r';
            }
            if (c == 'n') // npm_
            {
                return i + 3 < s.Length && s[i + 1] == 'p' && s[i + 2] == 'm' && s[i + 3] == '_';
            }
            if (c == 's' || c == 'p') // sk_live_ sk_test_ pk_live_ pk_test_
            {
                if (i + 7 >= s.Length || s[i + 1] != 'k' || s[i + 2] != '_' || s[i + 7] != '_') return false;
                if (s[i + 3] == 'l' && s[i + 4] == 'i' && s[i + 5] == 'v' && s[i + 6] == 'e') return true;
                return s[i + 3] == 't' && s[i + 4] == 'e' && s[i + 5] == 's' && s[i + 6] == 't';
            }
            if (c == 'x') // xoxb- xoxa- xoxp- xoxs-
            {
                if (i + 4 >= s.Length || s[i + 1] != 'o' || s[i + 2] != 'x' || s[i + 4] != '-') return false;
                char k = s[i + 3];
                return k == 'b' || k == 'a' || k == 'p' || k == 's';
            }
            // 'A' — AKIA / ASIA
            return i + 3 < s.Length && (s[i + 1] == 'K' || s[i + 1] == 'S') && s[i + 2] == 'I' && s[i + 3] == 'A';
        }

        // Standard Luhn checksum.
        private static bool IsLuhnValid(string digitsOnly)
        {
            if (digitsOnly == null || digitsOnly.Length < 13 || digitsOnly.Length > 19) return false;
            int sum = 0;
            bool doubleIt = false;
            for (int i = digitsOnly.Length - 1; i >= 0; i--)
            {
                int d = digitsOnly[i] - '0';
                if (d < 0 || d > 9) return false;
                if (doubleIt)
                {
                    d *= 2;
                    if (d > 9) d -= 9;
                }
                sum += d;
                doubleIt = !doubleIt;
            }
            return (sum % 10) == 0;
        }

        private static string StripNonDigits(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= '0' && s[i] <= '9') sb.Append(s[i]);
            return sb.ToString();
        }

        // Private-range filter: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16,
        // 127.0.0.0/8 (loopback), 169.254.0.0/16 (link-local). These are
        // never user PII — they're network plumbing debug info.
        private static bool IsPrivateIpv4(Match m)
        {
            if (!int.TryParse(m.Groups["o1"].Value, out int o1)) return false;
            if (!int.TryParse(m.Groups["o2"].Value, out int o2)) return false;
            if (o1 > 255 || o2 > 255) return false;
            if (o1 == 10) return true;
            if (o1 == 127) return true;
            if (o1 == 169 && o2 == 254) return true;
            if (o1 == 172 && o2 >= 16 && o2 <= 31) return true;
            if (o1 == 192 && o2 == 168) return true;
            return false;
        }

        private static void EmitWarningOnce()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _warningEmitted, 1, 0) != 0) return;
            UnityEngine.Debug.LogWarning(
                "[PlayScope] PII value-level mask triggered — one or more strings " +
                "had email / token / card / phone / IP substrings replaced with " +
                "placeholders before recording. Subsequent matches in this session " +
                "are silently scrubbed (this warning fires once per session).");
        }
    }
}
