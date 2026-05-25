using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using PlayScopeSdk.Internal;

namespace PlayScopeSdk.Tests.Editor
{
    /// <summary>
    /// Synthetic session_end timestamp must be derived from the latest record
    /// found in chunks, not from <c>DateTime.UtcNow</c> at recovery time —
    /// otherwise sessions OS-killed during background inflate their Duration
    /// by the recovery latency (observed 4:28 vs 56-sec real foreground play).
    /// </summary>
    public class SessionRecoveryTimestampTests
    {
        private string _tmpDir;

        private static MethodInfo ScanChunksForMaxTimestamp =>
            typeof(SessionRecovery).GetMethod("ScanChunksForMaxTimestamp",
                BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp]
        public void SetUp()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(),
                "playscope_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
            Assert.NotNull(ScanChunksForMaxTimestamp);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tmpDir, recursive: true); } catch { }
        }

        private static string Record(string ts) =>
            $"{{\"record_type\":\"event\",\"event_type\":\"lifecycle\"," +
            $"\"sequence_num\":5,\"timestamp\":\"{ts}\",\"metadata\":{{}}}}\n";

        [Test]
        public void ReturnsLatestTimestampAcrossChunks()
        {
            File.WriteAllText(Path.Combine(_tmpDir, "chunk_aaaa_000001.jsonl"),
                Record("2026-05-25T15:56:21.716Z") + Record("2026-05-25T15:56:30.000Z"));
            File.WriteAllText(Path.Combine(_tmpDir, "chunk_aaaa_000002.jsonl"),
                Record("2026-05-25T15:57:00.500Z"));
            File.WriteAllText(Path.Combine(_tmpDir, "chunk_current.jsonl"),
                Record("2026-05-25T15:57:17.998Z"));

            var result = (DateTime?)ScanChunksForMaxTimestamp.Invoke(null, new object[] { _tmpDir });

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(
                DateTime.Parse("2026-05-25T15:57:17.998Z",
                    null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                result.Value.ToUniversalTime());
        }

        [Test]
        public void ReturnsNullWhenDirEmpty()
        {
            var result = (DateTime?)ScanChunksForMaxTimestamp.Invoke(null, new object[] { _tmpDir });
            Assert.IsFalse(result.HasValue);
        }

        [Test]
        public void ReturnsNullWhenDirMissing()
        {
            var missing = Path.Combine(_tmpDir, "does_not_exist");
            var result = (DateTime?)ScanChunksForMaxTimestamp.Invoke(null, new object[] { missing });
            Assert.IsFalse(result.HasValue);
        }

        [Test]
        public void IgnoresPartialLineWithoutClosingQuote()
        {
            // Simulate process killed mid-write — last line truncated past
            // the timestamp's closing quote. Good lines before it must still
            // count.
            var truncated =
                Record("2026-05-25T15:56:21.716Z") +
                "{\"record_type\":\"event\",\"timestamp\":\"2026-05-25T15:99";
            File.WriteAllText(Path.Combine(_tmpDir, "chunk_current.jsonl"), truncated);

            var result = (DateTime?)ScanChunksForMaxTimestamp.Invoke(null, new object[] { _tmpDir });

            Assert.IsTrue(result.HasValue);
            Assert.AreEqual(
                DateTime.Parse("2026-05-25T15:56:21.716Z",
                    null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                result.Value.ToUniversalTime());
        }
    }
}
