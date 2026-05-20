using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Value-level PII detection — complements <see cref="SensitiveKeyFilter"/>
    /// which only filters by key name. Catches the case where a developer puts
    /// PII inside a value of an innocent-looking key (e.g. an exception message
    /// quoting the user's email, a log line including a JWT in a URL).
    ///
    /// <para>
    /// Strategy is masking, not dropping. If a value matches one of the
    /// patterns below, the matched substring is replaced with a placeholder
    /// like <c>[redacted-email]</c> in-line — the surrounding context survives
    /// so reviewers can still see the shape of the message. Dropping the whole
    /// value would lose far more information than necessary.
    /// </para>
    ///
    /// <para>
    /// Patterns are intentionally conservative — overly aggressive matching
    /// will eat legitimate product analytics (a <c>purchase_amount: 4242424242</c>
    /// must NOT be mistaken for a credit card). The order matters: the more
    /// specific patterns (JWT, bearer tokens) run before generic ones (long
    /// digit runs) so a JWT isn't half-matched as a "long string of base64
    /// junk".
    /// </para>
    ///
    /// <para>
    /// Performance: regexes are compiled and shared statically, so the per-
    /// value cost on the metadata pipeline is a handful of Regex.Replace calls
    /// on what's typically a short string. Not a hot path — events are
    /// pipelined, not on the render loop.
    /// </para>
    /// </summary>
    internal static class PiiValueScanner
    {
        // RFC 5321 simplified — local-part allows letters/digits/dots/+/-/_,
        // domain requires at least one dot. Word-boundary anchors prevent
        // matching mid-token. We intentionally don't try to be RFC-correct
        // (the real RFC accepts quoted strings, IP-literal domains, etc.) —
        // missing some exotic forms is fine, false-positives on common ids
        // are not.
        private static readonly Regex EmailRx = new Regex(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // JWT — three base64url segments separated by dots. The first segment
        // must start with `eyJ` (base64 of `{"`) which gives us a cheap and
        // very specific anchor; without it any "foo.bar.baz" matches.
        private static readonly Regex JwtRx = new Regex(
            @"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Bearer / Basic auth headers — case-insensitive prefix + token chars.
        // Captures whole "Bearer xyz" so the redaction reads naturally.
        private static readonly Regex AuthHeaderRx = new Regex(
            @"\b(Bearer|Basic|Token)\s+[A-Za-z0-9._\-=+/]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Well-known cloud / service token prefixes. These have NO real chance
        // of false positive — if someone's value starts with `ghp_` or
        // `sk_live_`, it's a token. Catches them even when used outside an
        // auth header. Update this list as new patterns become widely-used.
        private static readonly Regex KnownTokenPrefixRx = new Regex(
            @"\b(" +
                @"ghp|gho|ghu|ghs|ghr" +       // GitHub tokens
                @"|npm" +                       // npm tokens
                @"|sk_live|sk_test|pk_live|pk_test" + // Stripe
                @"|xoxb|xoxa|xoxp|xoxs" +       // Slack
                @"|AKIA|ASIA" +                 // AWS access keys
                @")_[A-Za-z0-9]{16,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Credit card — 13-19 digits, optionally separated by spaces or
        // hyphens. We run a Luhn check on the digits-only form to suppress
        // the false-positive avalanche (transaction IDs, sequence numbers,
        // any long integer). Pattern uses non-capturing group to keep the
        // replacement readable.
        private static readonly Regex CreditCardCandidateRx = new Regex(
            @"\b(?:\d[ \-]?){13,19}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // International phone numbers — `+` country code prefix + 7-15 digits
        // with optional separators. The leading `+` anchor cuts out most
        // false positives; bare "555-1234" style numbers are deliberately
        // NOT matched because they collide with version strings, ticket IDs,
        // etc. too easily.
        private static readonly Regex PhoneRx = new Regex(
            @"\+\d[\d \-().]{6,18}\d",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // IPv4 — four dot-separated octets, each 0-255. We don't redact
        // private-range addresses (127., 10., 192.168., 172.16-31.) because
        // those are usually telemetry-side debug info, not user PII.
        private static readonly Regex Ipv4Rx = new Regex(
            @"\b(?<o1>\d{1,3})\.(?<o2>\d{1,3})\.(?<o3>\d{1,3})\.(?<o4>\d{1,3})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Per-process emit-once guards. The first time a value is masked we
        // log a single warning so the integrator knows the filter triggered;
        // we don't want one per value or the console floods on a chatty log.
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
        /// Returns the input string with any detected PII substrings replaced
        /// by category placeholders. Null/empty input returns input unchanged.
        /// The output is reference-equal to the input when nothing matched —
        /// callers can use that as a "did anything get scrubbed?" check
        /// without an extra flag.
        /// </summary>
        internal static string MaskString(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            // Cheap fast-path: most strings have neither `@` nor `eyJ` nor a
            // leading `+` digit nor a 13+ digit run. Skip the regex storm
            // when none of the anchors are present. This is a 100ns check
            // vs a ~10µs regex sweep — pays for itself across every event.
            if (!MightContainPii(value)) return value;

            string original = value;
            string result = value;

            // Order matters — more specific first to avoid partial overlap.
            result = JwtRx.Replace(result, "[redacted-jwt]");
            result = AuthHeaderRx.Replace(result, m => m.Groups[1].Value + " [redacted]");
            result = KnownTokenPrefixRx.Replace(result, "[redacted-token]");
            result = EmailRx.Replace(result, "[redacted-email]");

            // Credit card needs a Luhn gate to avoid eating every long
            // integer. We use a MatchEvaluator that runs Luhn on the digits-
            // only form and only replaces on a Luhn-valid match.
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
                foreach (var kv in dict)
                {
                    // Note: we don't re-check the KEY here — that's
                    // SensitiveKeyFilter's job and it runs before us.
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

        // 100-nanosecond filter — bail out before touching the regex engine.
        // Each char-class check is a single byte comparison; the whole pass
        // is one branch-prediction-friendly loop.
        private static bool MightContainPii(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // Email anchor
                if (c == '@') return true;
                // Phone anchor (+ followed by digit)
                if (c == '+' && i + 1 < s.Length && char.IsDigit(s[i + 1])) return true;
                // Digit — only worth firing the regex when we've seen
                // a substantial digit run (≥10) for card / phone, or
                // a dotted ipv4 pattern.
                // Cheap heuristic: if there's any digit, let the regex
                // pass decide. Filtering more aggressively here would
                // duplicate the regex engine's job.
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
            }
            return false;
        }

        // Standard Luhn — sum digits with every-other digit doubled (and >9
        // wrapped). Result mod 10 == 0 for valid card numbers.
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
