using System;
using System.Threading;
using System.Threading.Tasks;

namespace Merge2048.Monetization
{
    public readonly struct LeaderboardSubmitResult
    {
        public readonly int StatusCode;
        public readonly int ResponseBytes;

        public LeaderboardSubmitResult(int statusCode, int responseBytes)
        {
            StatusCode = statusCode;
            ResponseBytes = responseBytes;
        }
    }

    public sealed class LeaderboardSubmitException : Exception
    {
        public int StatusCode { get; }

        public LeaderboardSubmitException(string message, int statusCode) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    public sealed class FakeLeaderboardService
    {
        private const float SUCCESS_WEIGHT = 0.85f;
        private const int MIN_RESPONSE_BYTES = 80;
        private const int MAX_RESPONSE_BYTES = 400;

        public async Task<LeaderboardSubmitResult> SubmitScoreAsync(int score, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.6), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return new LeaderboardSubmitResult(0, 0);
            }

            var roll = UnityEngine.Random.value;

            if (roll < SUCCESS_WEIGHT)
            {
                return new LeaderboardSubmitResult(200, UnityEngine.Random.Range(MIN_RESPONSE_BYTES, MAX_RESPONSE_BYTES));
            }

            throw new LeaderboardSubmitException("Simulated leaderboard submit failure", 500);
        }
    }
}
