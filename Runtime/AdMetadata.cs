using System.Collections.Generic;
using PlayScopeSdk.Internal;

namespace PlayScopeSdk
{
    /// <summary>
    /// Helpers for building the canonical ad-impression metadata dictionaries
    /// the dashboard's /revenue and /errors renderers surface as first-class
    /// fields. Structural mirror of <see cref="PurchaseMetadata"/> — the
    /// underlying <see cref="PlayScope.StartAd"/> / <see cref="PlayScope.EndAd"/>
    /// API still takes an opaque <c>IReadOnlyDictionary&lt;string, object&gt;</c>,
    /// so these helpers are additive: call them to get a dict with the right
    /// key names rather than typing them by hand.
    ///
    /// <para>
    /// Field schema (must stay in lockstep with web's revenue/errors renderers
    /// and the backend allow-list):
    /// </para>
    /// <list type="bullet">
    /// <item><c>network</c>: ad network identifier — see <see cref="Network"/>.</item>
    /// <item><c>placement</c>: integrator-defined placement ID (echoes the operation name).</item>
    /// <item><c>ad_type</c>: <c>interstitial</c> / <c>rewarded</c> / <c>banner</c> / <c>app_open</c> / <c>native</c> / <c>unknown</c>.</item>
    /// <item><c>result</c>: end-of-impression outcome — see <see cref="AdResult"/>.</item>
    /// <item><c>revenue</c>: estimated revenue for the impression (USD or specified currency, as double). Negative values clamped to 0.</item>
    /// <item><c>currency</c>: ISO 4217 code for <c>revenue</c> (default USD when omitted).</item>
    /// </list>
    /// </summary>
    public static class AdMetadata
    {
        /// <summary>
        /// Canonical ad-network identifiers. snake_case on the wire so the
        /// dashboard can match without normalisation.
        /// </summary>
        public static class Network
        {
            public const string AdMob       = "admob";
            public const string UnityAds    = "unity_ads";
            public const string IronSource  = "ironsource";
            public const string AppLovin    = "applovin";
            public const string Vungle      = "vungle";
            public const string Chartboost  = "chartboost";
            public const string Mintegral   = "mintegral";
            public const string Pangle      = "pangle";
            public const string Other       = "other";
        }

        /// <summary>
        /// Canonical ad-format identifiers.
        /// </summary>
        public static class AdType
        {
            public const string Interstitial = "interstitial";
            public const string Rewarded     = "rewarded";
            public const string Banner       = "banner";
            public const string AppOpen      = "app_open";
            public const string Native       = "native";
            public const string Unknown      = "unknown";
        }

        /// <summary>
        /// Canonical end-of-impression outcomes. Fuels the dashboard's
        /// fill-rate / completion-rate views — stick to this vocabulary
        /// where possible; free-form is accepted but fragments aggregation.
        /// Note: <c>Rewarded</c> here is the outcome (reward granted to player);
        /// <see cref="AdType.Rewarded"/> is the ad format. Same wire string by
        /// industry convention but stored under different metadata keys
        /// (<c>result</c> vs <c>ad_type</c>) — not interchangeable.
        /// </summary>
        public static class AdResult
        {
            public const string Shown         = "shown";
            public const string Rewarded      = "rewarded";
            public const string Skipped       = "skipped";
            public const string Closed        = "closed";
            public const string Failed        = "failed";
            public const string NoFill        = "no_fill";
            public const string UserCancelled = "user_cancelled";
            public const string Unknown       = "unknown";
        }

        /// <summary>
        /// Builds the metadata dict for <see cref="PlayScope.StartAd"/>.
        /// </summary>
        /// <param name="network">Ad network identifier — see <see cref="Network"/>. Pass null/empty to omit.</param>
        /// <param name="placement">Integrator-defined placement ID. Pass null/empty to omit.</param>
        /// <param name="adType">One of <see cref="AdType"/>. Pass null/empty to omit.</param>
        /// <param name="extra">Optional extra keys merged on top. Caller-supplied keys win on collision.</param>
        public static IReadOnlyDictionary<string, object> BuildStartMetadata(
            string network,
            string placement,
            string adType = null,
            IReadOnlyDictionary<string, object> extra = null)
        {
            var dict = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(network))
            {
                dict["network"] = network;
            }
            if (!string.IsNullOrEmpty(placement))
            {
                dict["placement"] = placement;
            }
            if (!string.IsNullOrEmpty(adType))
            {
                dict["ad_type"] = adType;
            }
            MergeExtra(dict, extra);
            return dict;
        }

        /// <summary>
        /// Builds the metadata dict for <see cref="PlayScope.EndAd"/>.
        /// Negative <paramref name="revenue"/> is clamped to 0 and logged.
        /// </summary>
        /// <param name="result">One of <see cref="AdResult"/> (or free-form). Pass null/empty to omit.</param>
        /// <param name="revenue">Estimated revenue for the impression. Negatives clamped to 0. Pass null to omit.</param>
        /// <param name="currency">ISO 4217 code (default USD when omitted by backend). Pass null/empty to omit.</param>
        /// <param name="extra">Optional extra keys merged on top. Caller-supplied keys win on collision.</param>
        public static IReadOnlyDictionary<string, object> BuildEndMetadata(
            string result,
            double? revenue = null,
            string currency = null,
            IReadOnlyDictionary<string, object> extra = null)
        {
            var dict = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(result))
            {
                dict["result"] = result;
            }
            if (revenue.HasValue)
            {
                if (revenue.Value < 0)
                {
                    PlayScopeLog.Warning("AdMetadata: negative revenue clamped to 0");
                    dict["revenue"] = 0.0;
                }
                else
                {
                    dict["revenue"] = revenue.Value;
                }
            }
            if (!string.IsNullOrEmpty(currency))
            {
                dict["currency"] = currency;
            }
            MergeExtra(dict, extra);
            return dict;
        }

        private static void MergeExtra(Dictionary<string, object> dict,
            IReadOnlyDictionary<string, object> extra)
        {
            if (extra == null)
            {
                return;
            }
            foreach (var kv in extra)
            {
                if (string.IsNullOrEmpty(kv.Key))
                {
                    continue;
                }
                // Caller-supplied keys WIN on collision — mirrors PurchaseMetadata.
                dict[kv.Key] = kv.Value;
            }
        }
    }
}
