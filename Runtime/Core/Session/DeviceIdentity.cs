using System;
using System.IO;
using PlayScopeSdk.Internal;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Core.Session
{
    internal sealed class DeviceIdentity
    {
        private const string SdkUserIdPrefix = "sdk_usr_";

        public string SdkUserId { get; private set; }

        private DeviceIdentity(string sdkUserId)
        {
            SdkUserId = sdkUserId;
        }

        // Reads device.json if it exists, or creates a new one. Per spec: device.json is
        // generated ONCE on first SDK initialization and never regenerated. If the file is
        // corrupt we fall back to a session-scoped in-memory id and leave the bad file on
        // disk for diagnostics (with a .corrupt-{timestamp} backup).
        internal static DeviceIdentity LoadOrCreate()
        {
            var path = PlayScopeDirectory.DeviceFile;

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var dto = SimpleJson.Deserialize(json);
                    if (dto != null && dto.TryGetValue("sdk_user_id", out var id) && id is string idStr && !string.IsNullOrEmpty(idStr))
                        return new DeviceIdentity(idStr);

                    // File parsed but had no usable sdk_user_id — treat as corrupt.
                    HandleCorruptDeviceFile(path, "missing or empty sdk_user_id");
                    return CreateRuntimeOnly();
                }
                catch (Exception ex)
                {
                    HandleCorruptDeviceFile(path, ex.Message);
                    return CreateRuntimeOnly();
                }
            }

            return CreateNew(path);
        }

        private static DeviceIdentity CreateNew(string path)
        {
            var sdkUserId = SdkUserIdPrefix + Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow.ToString("o");
            var json = $"{{\"sdk_user_id\":\"{sdkUserId}\",\"created_at\":\"{now}\"}}";

            File.WriteAllText(path, json);
            return new DeviceIdentity(sdkUserId);
        }

        /// <summary>
        /// In-memory only — not persisted to disk. Used when device.json is corrupt so that we
        /// don't overwrite the original file (it may be useful for diagnostics).
        /// </summary>
        private static DeviceIdentity CreateRuntimeOnly()
        {
            var sdkUserId = SdkUserIdPrefix + Guid.NewGuid().ToString("N");
            return new DeviceIdentity(sdkUserId);
        }

        private static void HandleCorruptDeviceFile(string path, string reason)
        {
            PlayScopeLog.Error(
                "device.json is corrupt — falling back to a fresh in-memory device id; " +
                "the corrupt file will remain on disk for diagnostics. Reason: " + reason);
            // Best-effort backup of the bad file so it isn't silently overwritten if a future
            // run accidentally tries to. We do NOT delete or rewrite the original.
            try
            {
                var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var backupPath = path + ".corrupt-" + ts;
                if (!File.Exists(backupPath))
                    File.Copy(path, backupPath);
            }
            catch
            {
                // Best-effort only.
            }
        }
    }
}
