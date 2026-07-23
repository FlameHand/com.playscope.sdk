using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Merge2048.Integration;
using Merge2048.Monetization;
using PlayScopeSdk;

namespace Merge2048.App
{
    // Only file in the sample allowed to call PlayScope.* outside Integration/ —
    // each operation span must wrap the actual await (ad show / purchase / leaderboard
    // submit), and that await lives here, not in a decoupled subscriber.
    public sealed class MonetizationFlows : MonoBehaviour
    {
        private FakeAdService _fakeAdService;
        private FakeStoreService _fakeStoreService;
        private FakeLeaderboardService _fakeLeaderboardService;

        private void Awake()
        {
            _fakeAdService = new FakeAdService();
            _fakeStoreService = new FakeStoreService();
            _fakeLeaderboardService = new FakeLeaderboardService();
        }

        public async Task<bool> ShowRewardedContinueAdAsync(CancellationToken cancellationToken)
        {
            var opId = PlayScope.StartAd("Rewarded_GameOver", AdMetadata.BuildStartMetadata(
                network: AdMetadata.Network.Other,
                placement: "Rewarded_GameOver",
                adType: AdMetadata.AdType.Rewarded));
            AnalyticsFeed.Publish("StartAd: Rewarded_GameOver");

            AdShowResult result;

            try
            {
                result = await _fakeAdService.ShowRewardedAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndAd(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndAd: Cancelled");
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndAd(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndAd: Cancelled");
                return false;
            }

            string adResult;
            OperationCompletionStatus status;

            switch (result.Outcome)
            {
                case AdOutcome.Rewarded:
                {
                    adResult = AdMetadata.AdResult.Rewarded;
                    status = OperationCompletionStatus.Success;
                    break;
                }
                case AdOutcome.Skipped:
                {
                    adResult = AdMetadata.AdResult.Skipped;
                    status = OperationCompletionStatus.Cancelled;
                    break;
                }
                case AdOutcome.Failed:
                {
                    adResult = AdMetadata.AdResult.Failed;
                    status = OperationCompletionStatus.Failure;
                    break;
                }
                case AdOutcome.Unknown:
                default:
                {
                    adResult = AdMetadata.AdResult.Unknown;
                    status = OperationCompletionStatus.Abandoned;
                    break;
                }
            }

            PlayScope.EndAd(opId, status, AdMetadata.BuildEndMetadata(
                result: adResult,
                revenue: result.RevenueUsd,
                currency: "USD"));
            AnalyticsFeed.Publish($"EndAd: {adResult}");

            return result.Outcome == AdOutcome.Rewarded;
        }

        public async Task ShowInterstitialBetweenGamesAsync(CancellationToken cancellationToken)
        {
            var opId = PlayScope.StartAd("Interstitial_BetweenGames", AdMetadata.BuildStartMetadata(
                network: AdMetadata.Network.Other,
                placement: "Interstitial_BetweenGames",
                adType: AdMetadata.AdType.Interstitial));
            AnalyticsFeed.Publish("StartAd: Interstitial");

            InterstitialShowResult result;

            try
            {
                result = await _fakeAdService.ShowInterstitialAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndAd(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndAd: Cancelled");
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndAd(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndAd: Cancelled");
                return;
            }

            string adResult;
            OperationCompletionStatus status;
            double? revenue;

            switch (result.Outcome)
            {
                case InterstitialOutcome.Closed:
                {
                    adResult = AdMetadata.AdResult.Closed;
                    status = OperationCompletionStatus.Success;
                    revenue = result.RevenueUsd;
                    break;
                }
                case InterstitialOutcome.NoFill:
                {
                    adResult = AdMetadata.AdResult.NoFill;
                    status = OperationCompletionStatus.Abandoned;
                    revenue = null;
                    break;
                }
                case InterstitialOutcome.Failed:
                {
                    adResult = AdMetadata.AdResult.Failed;
                    status = OperationCompletionStatus.Failure;
                    revenue = null;
                    break;
                }
                case InterstitialOutcome.Unknown:
                default:
                {
                    adResult = AdMetadata.AdResult.Unknown;
                    status = OperationCompletionStatus.Abandoned;
                    revenue = null;
                    break;
                }
            }

            PlayScope.EndAd(opId, status, AdMetadata.BuildEndMetadata(
                result: adResult,
                revenue: revenue,
                currency: revenue.HasValue ? "USD" : null));
            AnalyticsFeed.Publish($"EndAd: {adResult}");
        }

        public async Task<bool> PurchaseUndoPackAsync(CancellationToken cancellationToken)
        {
            return await PurchaseAsync("undo_pack_3", 1.99m, cancellationToken);
        }

        public async Task<bool> PurchaseRemoveAdsAsync(CancellationToken cancellationToken)
        {
            return await PurchaseAsync("remove_ads", 4.99m, cancellationToken);
        }

        public async Task<bool> RestoreRemoveAdsAsync(CancellationToken cancellationToken)
        {
            var opId = PlayScope.StartPurchase("remove_ads", PurchaseMetadata.BuildStartMetadata(
                currency: "USD",
                isRestore: true));
            AnalyticsFeed.Publish("StartPurchase: remove_ads");

            PurchaseAttemptResult result;

            try
            {
                result = await _fakeStoreService.RestorePurchasesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndPurchase(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndPurchase: Cancelled");
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndPurchase(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndPurchase: Cancelled");
                return false;
            }

            if (result.Outcome == PurchaseOutcome.Success)
            {
                PlayScope.EndPurchase(opId, OperationCompletionStatus.Success, PurchaseMetadata.BuildEndMetadata(
                    transactionId: result.TransactionId,
                    validationStatus: PurchaseMetadata.ValidationStatus.Valid));
                AnalyticsFeed.Publish("EndPurchase: Success");
                return true;
            }

            PlayScope.EndPurchase(opId, OperationCompletionStatus.Abandoned);
            AnalyticsFeed.Publish("EndPurchase: Abandoned");
            return false;
        }

        private async Task<bool> PurchaseAsync(string productId, decimal priceAmount, CancellationToken cancellationToken)
        {
            var opId = PlayScope.StartPurchase(productId, PurchaseMetadata.BuildStartMetadata(
                currency: "USD",
                priceAmount: priceAmount));
            AnalyticsFeed.Publish($"StartPurchase: {productId}");

            PurchaseAttemptResult result;

            try
            {
                result = await _fakeStoreService.PurchaseAsync(productId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndPurchase(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndPurchase: Cancelled");
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndPurchase(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndPurchase: Cancelled");
                return false;
            }

            OperationCompletionStatus status;
            string validationStatus = null;
            string failureReason = null;

            switch (result.Outcome)
            {
                case PurchaseOutcome.Success:
                {
                    status = OperationCompletionStatus.Success;
                    validationStatus = PurchaseMetadata.ValidationStatus.Valid;
                    break;
                }
                case PurchaseOutcome.UserCancelled:
                {
                    status = OperationCompletionStatus.Cancelled;
                    failureReason = PurchaseMetadata.FailureReason.UserCancelled;
                    break;
                }
                case PurchaseOutcome.Failed:
                {
                    status = OperationCompletionStatus.Failure;
                    failureReason = PurchaseMetadata.FailureReason.Unknown;
                    break;
                }
                case PurchaseOutcome.Unknown:
                default:
                {
                    status = OperationCompletionStatus.Abandoned;
                    break;
                }
            }

            PlayScope.EndPurchase(opId, status, PurchaseMetadata.BuildEndMetadata(
                transactionId: result.TransactionId,
                validationStatus: validationStatus,
                failureReason: failureReason));
            AnalyticsFeed.Publish($"EndPurchase: {status}");

            return result.Outcome == PurchaseOutcome.Success;
        }

        public async Task SubmitScoreToLeaderboardAsync(int score, CancellationToken cancellationToken)
        {
            var opId = PlayScope.StartHTTP("POST /leaderboard/submit");
            AnalyticsFeed.Publish("StartHTTP: leaderboard");

            LeaderboardSubmitResult result;

            try
            {
                result = await _fakeLeaderboardService.SubmitScoreAsync(score, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndHTTP(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndHTTP: Cancelled");
                return;
            }
            catch (LeaderboardTimeoutException)
            {
                PlayScope.EndHTTP(opId, OperationCompletionStatus.Timeout, new Dictionary<string, object>
                {
                    ["timeout_ms"] = 5000,
                });
                AnalyticsFeed.Publish("EndHTTP: Timeout");
                return;
            }
            catch (LeaderboardSubmitException ex)
            {
                PlayScope.EndHTTP(opId, OperationCompletionStatus.Failure, new Dictionary<string, object>
                {
                    ["status_code"] = ex.StatusCode,
                });
                PlayScope.TrackException(ex, new Dictionary<string, object>
                {
                    ["context"] = "leaderboard_submit",
                });
                AnalyticsFeed.Publish("EndHTTP: Failure");
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndHTTP(opId, OperationCompletionStatus.Cancelled);
                AnalyticsFeed.Publish("EndHTTP: Cancelled");
                return;
            }

            PlayScope.EndHTTP(opId, OperationCompletionStatus.Success, new Dictionary<string, object>
            {
                ["status_code"] = result.StatusCode,
                ["bytes"] = result.ResponseBytes,
            });
            AnalyticsFeed.Publish("EndHTTP: Success");
        }
    }
}
