using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
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

            AdShowResult result;

            try
            {
                result = await _fakeAdService.ShowRewardedAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndAd(opId, OperationCompletionStatus.Cancelled);
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndAd(opId, OperationCompletionStatus.Cancelled);
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

            return result.Outcome == AdOutcome.Rewarded;
        }

        public async Task<bool> PurchaseUndoPackAsync(CancellationToken cancellationToken)
        {
            return await PurchaseAsync("undo_pack_3", 1.99m, cancellationToken);
        }

        public async Task<bool> PurchaseRemoveAdsAsync(CancellationToken cancellationToken)
        {
            return await PurchaseAsync("remove_ads", 4.99m, cancellationToken);
        }

        private async Task<bool> PurchaseAsync(string productId, decimal priceAmount, CancellationToken cancellationToken)
        {
            var opId = PlayScope.StartPurchase(productId, PurchaseMetadata.BuildStartMetadata(
                currency: "USD",
                priceAmount: priceAmount));

            PurchaseAttemptResult result;

            try
            {
                result = await _fakeStoreService.PurchaseAsync(productId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndPurchase(opId, OperationCompletionStatus.Cancelled);
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndPurchase(opId, OperationCompletionStatus.Cancelled);
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

            return result.Outcome == PurchaseOutcome.Success;
        }

        public async Task SubmitScoreToLeaderboardAsync(int score, CancellationToken cancellationToken)
        {
            var opId = PlayScope.StartHTTP("POST /leaderboard/submit");

            LeaderboardSubmitResult result;

            try
            {
                result = await _fakeLeaderboardService.SubmitScoreAsync(score, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PlayScope.EndHTTP(opId, OperationCompletionStatus.Cancelled);
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
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                PlayScope.EndHTTP(opId, OperationCompletionStatus.Cancelled);
                return;
            }

            PlayScope.EndHTTP(opId, OperationCompletionStatus.Success, new Dictionary<string, object>
            {
                ["status_code"] = result.StatusCode,
                ["bytes"] = result.ResponseBytes,
            });
        }
    }
}
