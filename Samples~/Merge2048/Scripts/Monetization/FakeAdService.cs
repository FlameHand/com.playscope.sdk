using System;
using System.Threading;
using System.Threading.Tasks;

namespace Merge2048.Monetization
{
    public enum AdOutcome
    {
        Unknown = 0,
        Rewarded = 1,
        Skipped = 2,
        Failed = 3,
    }

    public readonly struct AdShowResult
    {
        public readonly AdOutcome Outcome;
        public readonly double RevenueUsd;

        public AdShowResult(AdOutcome outcome, double revenueUsd)
        {
            Outcome = outcome;
            RevenueUsd = revenueUsd;
        }
    }

    public enum InterstitialOutcome
    {
        Unknown = 0,
        Closed = 1,
        NoFill = 2,
        Failed = 3,
    }

    public readonly struct InterstitialShowResult
    {
        public readonly InterstitialOutcome Outcome;
        public readonly double RevenueUsd;

        public InterstitialShowResult(InterstitialOutcome outcome, double revenueUsd)
        {
            Outcome = outcome;
            RevenueUsd = revenueUsd;
        }
    }

    public sealed class FakeAdService
    {
        private const float REWARDED_WEIGHT = 0.70f;
        private const float SKIPPED_WEIGHT = 0.15f;
        private const float MIN_REVENUE_USD = 0.01f;
        private const float MAX_REVENUE_USD = 0.08f;

        private const float INTERSTITIAL_CLOSED_WEIGHT = 0.80f;
        private const float INTERSTITIAL_NOFILL_WEIGHT = 0.12f;
        private const float INTERSTITIAL_MIN_REVENUE_USD = 0.01f;
        private const float INTERSTITIAL_MAX_REVENUE_USD = 0.05f;

        public async Task<AdShowResult> ShowRewardedAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return new AdShowResult(AdOutcome.Unknown, 0d);
            }

            var roll = UnityEngine.Random.value;

            if (roll < REWARDED_WEIGHT)
            {
                var revenueUsd = UnityEngine.Random.Range(MIN_REVENUE_USD, MAX_REVENUE_USD);
                return new AdShowResult(AdOutcome.Rewarded, revenueUsd);
            }

            if (roll < REWARDED_WEIGHT + SKIPPED_WEIGHT)
            {
                return new AdShowResult(AdOutcome.Skipped, 0d);
            }

            return new AdShowResult(AdOutcome.Failed, 0d);
        }

        public async Task<InterstitialShowResult> ShowInterstitialAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.0), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return new InterstitialShowResult(InterstitialOutcome.Unknown, 0d);
            }

            var roll = UnityEngine.Random.value;

            if (roll < INTERSTITIAL_CLOSED_WEIGHT)
            {
                var revenueUsd = UnityEngine.Random.Range(INTERSTITIAL_MIN_REVENUE_USD, INTERSTITIAL_MAX_REVENUE_USD);
                return new InterstitialShowResult(InterstitialOutcome.Closed, revenueUsd);
            }

            if (roll < INTERSTITIAL_CLOSED_WEIGHT + INTERSTITIAL_NOFILL_WEIGHT)
            {
                return new InterstitialShowResult(InterstitialOutcome.NoFill, 0d);
            }

            return new InterstitialShowResult(InterstitialOutcome.Failed, 0d);
        }
    }
}
