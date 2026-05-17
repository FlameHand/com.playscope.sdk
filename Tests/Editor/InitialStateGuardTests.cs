using System.Reflection;
using NUnit.Framework;
using PlayScopeSdk.Internal;

namespace PlayScopeSdk.Tests.Editor
{
    /// <summary>
    /// Verifies the PSDK guard: SetInitialState may only fire once per session.
    /// We exercise the internal latch directly so the test doesn't depend on a live
    /// session/file-system fixture.
    /// </summary>
    public class InitialStateGuardTests
    {
        [SetUp]
        public void ResetLatch()
        {
            // Reflectively zero the flag so test order doesn't matter.
            var field = typeof(PlayScopeRuntime).GetField("_initialStateSet",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field, "_initialStateSet field must exist on PlayScopeRuntime");
            field.SetValue(null, 0);
        }

        [Test]
        public void TryMarkInitialStateSet_FirstCallSucceeds_SecondCallFails()
        {
            Assert.IsTrue(PlayScopeRuntime.TryMarkInitialStateSet(), "first call must claim the slot");
            Assert.IsFalse(PlayScopeRuntime.TryMarkInitialStateSet(), "second call must be rejected");
            Assert.IsFalse(PlayScopeRuntime.TryMarkInitialStateSet(), "third call must still be rejected");
        }
    }
}
