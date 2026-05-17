using System.IO;
using NUnit.Framework;
using PlayScopeSdk.Core.Session;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Tests.Editor
{
    public class DeviceIdentityTests
    {
        private string _origContent;

        [SetUp]
        public void EnsureDirs()
        {
            PlayScopeDirectory.EnsureRootDirectories();
            if (File.Exists(PlayScopeDirectory.DeviceFile))
                _origContent = File.ReadAllText(PlayScopeDirectory.DeviceFile);
            else
                _origContent = null;
        }

        [TearDown]
        public void Restore()
        {
            try
            {
                // Clean up any .corrupt-* backups created during the test
                foreach (var f in Directory.GetFiles(PlayScopeDirectory.Root, "device.json.corrupt-*"))
                    File.Delete(f);
            }
            catch { /* best-effort */ }

            if (_origContent != null)
                File.WriteAllText(PlayScopeDirectory.DeviceFile, _origContent);
            else if (File.Exists(PlayScopeDirectory.DeviceFile))
                File.Delete(PlayScopeDirectory.DeviceFile);
        }

        [Test]
        public void CorruptDeviceJson_IsNotOverwritten()
        {
            // Arrange: write a corrupt device.json
            const string corrupt = "{ this is not valid json";
            File.WriteAllText(PlayScopeDirectory.DeviceFile, corrupt);

            // Act
            var identity = DeviceIdentity.LoadOrCreate();

            // Assert: we got an in-memory id, but the file is unchanged
            Assert.NotNull(identity);
            Assert.IsTrue(identity.SdkUserId.StartsWith("sdk_usr_"));
            var onDisk = File.ReadAllText(PlayScopeDirectory.DeviceFile);
            Assert.AreEqual(corrupt, onDisk, "device.json must not be overwritten when corrupt");
        }

        [Test]
        public void MissingDeviceJson_IsCreated()
        {
            if (File.Exists(PlayScopeDirectory.DeviceFile))
                File.Delete(PlayScopeDirectory.DeviceFile);

            var identity = DeviceIdentity.LoadOrCreate();
            Assert.IsTrue(File.Exists(PlayScopeDirectory.DeviceFile));
            Assert.IsTrue(identity.SdkUserId.StartsWith("sdk_usr_"));
        }

        [Test]
        public void ExistingValidDeviceJson_IsPreserved()
        {
            var existing = "{\"sdk_user_id\":\"sdk_usr_existing\",\"created_at\":\"2026-01-01T00:00:00.000Z\"}";
            File.WriteAllText(PlayScopeDirectory.DeviceFile, existing);

            var identity = DeviceIdentity.LoadOrCreate();
            Assert.AreEqual("sdk_usr_existing", identity.SdkUserId);
            Assert.AreEqual(existing, File.ReadAllText(PlayScopeDirectory.DeviceFile));
        }
    }
}
