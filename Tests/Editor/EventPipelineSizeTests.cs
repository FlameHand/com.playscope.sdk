using NUnit.Framework;
using PlayScopeSdk.Internal;

namespace PlayScopeSdk.Tests.Editor
{
    public class EventPipelineSizeTests
    {
        [Test]
        public void CountTopLevelJsonKeys_Flat()
        {
            Assert.AreEqual(0, EventPipeline.CountTopLevelJsonKeys("{}"));
            Assert.AreEqual(1, EventPipeline.CountTopLevelJsonKeys("{\"a\":1}"));
            Assert.AreEqual(3, EventPipeline.CountTopLevelJsonKeys("{\"a\":1,\"b\":2,\"c\":3}"));
        }

        [Test]
        public void CountTopLevelJsonKeys_IgnoresNestedAndStrings()
        {
            // Nested objects must not be counted
            Assert.AreEqual(1, EventPipeline.CountTopLevelJsonKeys("{\"a\":{\"x\":1,\"y\":2}}"));
            // Colons inside strings must not be counted
            Assert.AreEqual(1, EventPipeline.CountTopLevelJsonKeys("{\"a\":\"x:y:z\"}"));
            // Arrays
            Assert.AreEqual(1, EventPipeline.CountTopLevelJsonKeys("{\"a\":[1,2,3]}"));
            // Mix
            Assert.AreEqual(2, EventPipeline.CountTopLevelJsonKeys("{\"a\":{\"x\":1},\"b\":[1,2]}"));
        }
    }
}
