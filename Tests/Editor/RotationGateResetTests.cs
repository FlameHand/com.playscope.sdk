using System.Reflection;
using NUnit.Framework;
using PlayScopeSdk;
using PlayScopeSdk.Internal;

namespace PlayScopeSdk.Tests.Editor
{
    /// <summary>
    /// InitializeLocked must reset <c>_acceptingEvents</c> to true, otherwise a
    /// background-rotation that closed the gate and then threw mid-init would
    /// strand the SDK silently dropping every event of the new session.
    /// Seam: Initialize with an empty SdkKey runs the reset block then bails
    /// out at validation — hermetic, no workers / driver / filesystem state.
    /// </summary>
    public class RotationGateResetTests
    {
        private static FieldInfo AcceptingEventsField =>
            typeof(PlayScopeRuntime).GetField("_acceptingEvents",
                BindingFlags.NonPublic | BindingFlags.Static);

        private static FieldInfo InitializedField =>
            typeof(PlayScopeRuntime).GetField("_initialized",
                BindingFlags.NonPublic | BindingFlags.Static);

        private static FieldInfo DisabledField =>
            typeof(PlayScopeRuntime).GetField("_disabled",
                BindingFlags.NonPublic | BindingFlags.Static);

        private static FieldInfo LifecycleBusyField =>
            typeof(PlayScopeRuntime).GetField("_lifecycleBusy",
                BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp]
        public void ResetStaticState()
        {
            Assert.NotNull(AcceptingEventsField);
            Assert.NotNull(InitializedField);
            Assert.NotNull(DisabledField);
            Assert.NotNull(LifecycleBusyField);

            InitializedField.SetValue(null, false);
            DisabledField.SetValue(null, false);
            LifecycleBusyField.SetValue(null, 0);
        }

        [Test]
        public void Initialize_ResetsAcceptingEventsGate_EvenWhenSdkKeyInvalid()
        {
            AcceptingEventsField.SetValue(null, false);
            Assert.IsFalse((bool)AcceptingEventsField.GetValue(null));

            PlayScopeRuntime.Initialize(new PlayScopeContext { SdkKey = "" });

            Assert.IsTrue((bool)AcceptingEventsField.GetValue(null),
                "InitializeLocked must reset _acceptingEvents to true.");
            Assert.IsTrue((bool)DisabledField.GetValue(null),
                "test seam: empty SdkKey must mark SDK disabled");
        }
    }
}
