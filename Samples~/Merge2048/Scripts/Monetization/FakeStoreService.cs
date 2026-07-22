using System;
using System.Threading;
using System.Threading.Tasks;

namespace Merge2048.Monetization
{
    public enum PurchaseOutcome
    {
        Unknown = 0,
        Success = 1,
        UserCancelled = 2,
        Failed = 3,
    }

    public readonly struct PurchaseAttemptResult
    {
        public readonly PurchaseOutcome Outcome;
        public readonly string TransactionId;

        public PurchaseAttemptResult(PurchaseOutcome outcome, string transactionId)
        {
            Outcome = outcome;
            TransactionId = transactionId;
        }
    }

    public sealed class FakeStoreService
    {
        private const float SUCCESS_WEIGHT = 0.75f;
        private const float USER_CANCELLED_WEIGHT = 0.15f;

        public async Task<PurchaseAttemptResult> PurchaseAsync(string productId, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return new PurchaseAttemptResult(PurchaseOutcome.Unknown, string.Empty);
            }

            var roll = UnityEngine.Random.value;

            if (roll < SUCCESS_WEIGHT)
            {
                var transactionId = $"txn_{Guid.NewGuid():N}";
                return new PurchaseAttemptResult(PurchaseOutcome.Success, transactionId);
            }

            if (roll < SUCCESS_WEIGHT + USER_CANCELLED_WEIGHT)
            {
                return new PurchaseAttemptResult(PurchaseOutcome.UserCancelled, string.Empty);
            }

            return new PurchaseAttemptResult(PurchaseOutcome.Failed, string.Empty);
        }
    }
}
