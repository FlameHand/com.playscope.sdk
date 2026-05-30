using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PlayScopeSdk
{
    /// <summary>
    /// Helpers for the canonical purchase-event metadata the dashboard's
    /// PurchaseDetails renderer surfaces as first-class fields. Additive over the
    /// opaque-dict <c>StartPurchase</c>/<c>EndPurchase</c> API — using them avoids
    /// a typo'd key the dashboard can't render, and hashes the transaction_id.
    ///
    /// <para>
    /// Field schema (keep in lockstep with web's <c>PurchaseDetails</c> renderer
    /// and any backend whitelist):
    /// </para>
    /// <list type="bullet">
    /// <item><c>store</c>: <c>app_store</c> / <c>google_play</c> / <c>steam</c> / <c>amazon</c> / <c>other</c>. Auto-detected from <see cref="Application.platform"/> when <see cref="BuildStartMetadata"/> is called without an explicit override.</item>
    /// <item><c>currency</c>: ISO 4217 code (<c>USD</c>, <c>EUR</c>, …).</item>
    /// <item><c>price_amount</c>: numeric (decimal preserved as double).</item>
    /// <item><c>transaction_id_hash</c>: first 16 hex chars of SHA-256(transaction_id). Never the raw ID — some stores embed user-identifying material in their transaction IDs.</item>
    /// <item><c>is_restore</c>: <c>true</c> when the purchase came from a "restore purchases" flow rather than a fresh buy.</item>
    /// <item><c>validation_status</c>: <c>pending</c> / <c>valid</c> / <c>invalid</c> / <c>error</c>. Driven by the game's receipt-validation outcome.</item>
    /// <item><c>failure_reason</c>: short canonical string for failures — fuels the dashboard's "Top purchase failures" view. Examples: <c>user_cancelled</c>, <c>payment_declined</c>, <c>network_error</c>, <c>validation_failed</c>.</item>
    /// </list>
    /// </summary>
    public static class PurchaseMetadata
    {
        /// <summary>
        /// Canonical store identifiers. Stored as snake_case strings on the
        /// wire so the dashboard can match without normalisation.
        /// </summary>
        public static class Store
        {
            public const string AppStore   = "app_store";
            public const string GooglePlay = "google_play";
            public const string Steam      = "steam";
            public const string Amazon     = "amazon";
            public const string Other      = "other";
        }

        /// <summary>
        /// Canonical receipt-validation outcomes. Most games run validation
        /// asynchronously after EndPurchase — emit <c>pending</c> at end of
        /// purchase, then a follow-up event when validation lands.
        /// </summary>
        public static class ValidationStatus
        {
            public const string Pending = "pending";
            public const string Valid   = "valid";
            public const string Invalid = "invalid";
            public const string Error   = "error";
        }

        /// <summary>
        /// Canonical failure-reason strings for <c>failure_reason</c>. The
        /// dashboard's "Top purchase failures" aggregates exact-match, so
        /// stick to this vocabulary where possible. Free-form strings are
        /// accepted but will fragment the aggregation.
        /// </summary>
        public static class FailureReason
        {
            public const string UserCancelled    = "user_cancelled";
            public const string PaymentDeclined  = "payment_declined";
            public const string NetworkError     = "network_error";
            public const string ValidationFailed = "validation_failed";
            public const string ItemUnavailable  = "item_unavailable";
            public const string AlreadyOwned     = "already_owned";
            public const string Unknown          = "unknown";
        }

        /// <summary>
        /// Builds the metadata dict for <see cref="PlayScope.StartPurchase"/>.
        /// <paramref name="store"/> defaults to a value derived from
        /// <see cref="Application.platform"/> — pass an explicit value to
        /// override (e.g. for cross-promo flows where the actual store
        /// differs from the runtime platform).
        /// </summary>
        /// <param name="currency">ISO 4217 code (e.g. "USD"). Pass null/empty to omit.</param>
        /// <param name="priceAmount">Localised price as a decimal. Pass null to omit.</param>
        /// <param name="store">Override <c>store</c>; null = auto-detect from platform.</param>
        /// <param name="isRestore">True for restore-purchases flows.</param>
        /// <param name="extra">Optional extra keys merged on top. Caller-supplied keys win on collision.</param>
        public static IReadOnlyDictionary<string, object> BuildStartMetadata(
            string currency = null,
            decimal? priceAmount = null,
            string store = null,
            bool isRestore = false,
            IReadOnlyDictionary<string, object> extra = null)
        {
            var dict = new Dictionary<string, object>
            {
                ["store"] = string.IsNullOrEmpty(store) ? DetectStore() : store,
            };
            if (!string.IsNullOrEmpty(currency)) dict["currency"] = currency;
            if (priceAmount.HasValue) dict["price_amount"] = (double)priceAmount.Value;
            if (isRestore) dict["is_restore"] = true;
            MergeExtra(dict, extra);
            return dict;
        }

        /// <summary>
        /// Builds the metadata dict for <see cref="PlayScope.EndPurchase"/>.
        /// Hashes <paramref name="transactionId"/> with SHA-256 and stores the
        /// first 16 hex chars under <c>transaction_id_hash</c>. The raw ID
        /// never leaves the device.
        /// </summary>
        /// <param name="transactionId">Raw store transaction ID, e.g. <c>Product.transactionID</c>. Hashed; never stored raw.</param>
        /// <param name="validationStatus">One of <see cref="ValidationStatus"/>.</param>
        /// <param name="failureReason">One of <see cref="FailureReason"/> (or free-form).</param>
        /// <param name="extra">Optional extra keys merged on top. Caller-supplied keys win on collision.</param>
        public static IReadOnlyDictionary<string, object> BuildEndMetadata(
            string transactionId = null,
            string validationStatus = null,
            string failureReason = null,
            IReadOnlyDictionary<string, object> extra = null)
        {
            var dict = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(transactionId))
                dict["transaction_id_hash"] = HashTransactionId(transactionId);
            if (!string.IsNullOrEmpty(validationStatus))
                dict["validation_status"] = validationStatus;
            if (!string.IsNullOrEmpty(failureReason))
                dict["failure_reason"] = failureReason;
            MergeExtra(dict, extra);
            return dict;
        }

        /// <summary>
        /// SHA-256 the input, return the first 16 hex chars (= 64 bits — far
        /// enough collision-resistance for transaction-grouping but not
        /// reversible to the raw ID). Empty input → empty string.
        /// </summary>
        public static string HashTransactionId(string transactionId)
        {
            if (string.IsNullOrEmpty(transactionId)) return string.Empty;
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(transactionId));
            // 8 bytes = 16 hex chars, lower-case to match dashboard hashed-id convention.
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        // ── Internals ────────────────────────────────────────────────────

        // Editor platform falls through to Other — pass an explicit override
        // when testing a store flow from the Editor so the metric routes to
        // the right dashboard bucket.
        private static string DetectStore()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.tvOS:
                    return Store.AppStore;
                case RuntimePlatform.Android:
                    return Store.GooglePlay;
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.LinuxPlayer:
                    return Store.Steam;
                default:
                    return Store.Other;
            }
        }

        private static void MergeExtra(Dictionary<string, object> dict,
            IReadOnlyDictionary<string, object> extra)
        {
            if (extra == null) return;
            foreach (var kv in extra)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                // Caller keys win — the game may have a more accurate value
                // (e.g. a server-side transaction_id_hash) than the helper default.
                dict[kv.Key] = kv.Value;
            }
        }
    }
}
