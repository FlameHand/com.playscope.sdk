using System;
using NUnit.Framework;

namespace PlayScopeSdk.Tests.Editor
{
    /// <summary>
    /// Verifies the 7-day retry-TTL semantics: TTL is anchored to CreatedAt so a chunk
    /// that keeps failing does NOT reset its own TTL on every retry. Legacy state files
    /// missing CreatedAt fall back to LastAttemptAt.
    /// </summary>
    public class RetryTtlTests
    {
        private const double RetryTtlDays = 7.0;

        // The decision branch under test, mirrored from UploaderWorker.GatherRetryablePaths:
        //   var ttlAnchor = state.CreatedAt ?? state.LastAttemptAt;
        //   if (ttlAnchor.HasValue && (now - ttlAnchor.Value).TotalDays >= RetryTtlDays) → dead-letter
        private static bool ShouldDeadLetter(DateTime now, DateTime? createdAt, DateTime? lastAttemptAt)
        {
            var anchor = createdAt ?? lastAttemptAt;
            if (!anchor.HasValue) return false;
            return (now - anchor.Value).TotalDays >= RetryTtlDays;
        }

        [Test]
        public void CreatedAt8DaysAgo_LastAttemptToday_StillDeadLetters()
        {
            var now = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
            var createdAt = now.AddDays(-8);
            var lastAttempt = now.AddMinutes(-5);

            Assert.IsTrue(ShouldDeadLetter(now, createdAt, lastAttempt),
                "TTL anchored to CreatedAt must dead-letter regardless of recent retries");
        }

        [Test]
        public void CreatedAt5DaysAgo_NotYetExpired()
        {
            var now = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
            var createdAt = now.AddDays(-5);
            Assert.IsFalse(ShouldDeadLetter(now, createdAt, now));
        }

        [Test]
        public void LegacyStateWithoutCreatedAt_FallsBackToLastAttempt()
        {
            var now = new DateTime(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);
            // No CreatedAt — use LastAttempt as fallback. 8 days ago → dead letter.
            Assert.IsTrue(ShouldDeadLetter(now, null, now.AddDays(-8)));
            // 1 day ago → not yet
            Assert.IsFalse(ShouldDeadLetter(now, null, now.AddDays(-1)));
        }

        [Test]
        public void NoAnchorAtAll_NeverDeadLetters()
        {
            var now = DateTime.UtcNow;
            Assert.IsFalse(ShouldDeadLetter(now, null, null));
        }
    }
}
