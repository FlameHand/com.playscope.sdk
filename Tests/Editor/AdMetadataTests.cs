using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PlayScopeSdk.Tests.Editor
{
    public class AdMetadataTests
    {
        [Test]
        public void Revenue_NaN_IsDropped()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"non-finite revenue"));

            var metadata = AdMetadata.BuildEndMetadata(AdMetadata.AdResult.Shown, double.NaN);

            Assert.IsFalse(metadata.ContainsKey("revenue"));
        }

        [Test]
        public void Revenue_PositiveInfinity_IsDropped()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"non-finite revenue"));

            var metadata = AdMetadata.BuildEndMetadata(AdMetadata.AdResult.Shown, double.PositiveInfinity);

            Assert.IsFalse(metadata.ContainsKey("revenue"));
        }

        [Test]
        public void Revenue_NegativeInfinity_IsDroppedNotClamped()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"non-finite revenue"));

            var metadata = AdMetadata.BuildEndMetadata(AdMetadata.AdResult.Shown, double.NegativeInfinity);

            Assert.IsFalse(metadata.ContainsKey("revenue"));
        }

        [Test]
        public void Revenue_NegativeFinite_IsClampedToZero()
        {
            LogAssert.Expect(LogType.Warning, new Regex(@"negative revenue clamped"));

            var metadata = AdMetadata.BuildEndMetadata(AdMetadata.AdResult.Shown, -5.0);

            Assert.IsTrue(metadata.ContainsKey("revenue"));
            Assert.AreEqual(0.0, (double)metadata["revenue"]);
        }

        [Test]
        public void Revenue_PositiveFinite_IsPreserved()
        {
            var metadata = AdMetadata.BuildEndMetadata(AdMetadata.AdResult.Shown, 1.23);

            Assert.IsTrue(metadata.ContainsKey("revenue"));
            Assert.AreEqual(1.23, (double)metadata["revenue"]);
        }

        [Test]
        public void Revenue_Null_IsOmitted()
        {
            var metadata = AdMetadata.BuildEndMetadata(AdMetadata.AdResult.Shown);

            Assert.IsFalse(metadata.ContainsKey("revenue"));
        }
    }
}
