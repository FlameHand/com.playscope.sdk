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

        private static FieldInfo PendingRotationField =>
            typeof(PlayScopeRuntime).GetField("_pendingRotation",
                BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp]
        public void ResetStaticState()
        {
            Assert.NotNull(AcceptingEventsField);
            Assert.NotNull(InitializedField);
            Assert.NotNull(DisabledField);
            Assert.NotNull(LifecycleBusyField);
            Assert.NotNull(PendingRotationField);

            InitializedField.SetValue(null, false);
            DisabledField.SetValue(null, false);
            LifecycleBusyField.SetValue(null, 0);
            PendingRotationField.SetValue(null, false);
        }

        [TearDown]
        public void RestoreStaticState()
        {
            LifecycleBusyField.SetValue(null, 0);
            PendingRotationField.SetValue(null, false);
            AcceptingEventsField.SetValue(null, true);
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

        /// <summary>
        /// CAS-fail skip path: the pending flag was consumed by Update and the
        /// gate is closed — PerformRotation must re-arm the flag (retry next
        /// frame), not return into a permanently event-dropping runtime.
        /// </summary>
        [Test]
        public void PerformRotation_LifecycleBusy_ReArmsPendingRotation()
        {
            LifecycleBusyField.SetValue(null, 1);
            AcceptingEventsField.SetValue(null, false);
            PendingRotationField.SetValue(null, false);

            PlayScopeRuntime.PerformRotation();

            Assert.IsTrue((bool)PendingRotationField.GetValue(null),
                "CAS-fail must re-arm _pendingRotation so the rotation retries next frame.");
            Assert.AreEqual(1, (int)LifecycleBusyField.GetValue(null),
                "CAS-fail must not release a lifecycle lock it does not hold.");
        }

        /// <summary>
        /// Lock-acquired path with nothing to rotate (not initialized): the
        /// finally must re-open the gate and release the lock — no exit from
        /// PerformRotation may leave _acceptingEvents stuck false.
        /// </summary>
        [Test]
        public void PerformRotation_NotInitialized_RestoresGateAndLock()
        {
            AcceptingEventsField.SetValue(null, false);

            PlayScopeRuntime.PerformRotation();

            Assert.IsTrue((bool)AcceptingEventsField.GetValue(null),
                "PerformRotation must restore _acceptingEvents on every exit path.");
            Assert.AreEqual(0, (int)LifecycleBusyField.GetValue(null),
                "PerformRotation must release the lifecycle lock.");
            Assert.IsFalse((bool)PendingRotationField.GetValue(null),
                "Completed (no-op) rotation must not leave a pending retry.");
        }
    }
}
