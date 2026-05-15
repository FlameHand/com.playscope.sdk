using System;
using System.IO;
using UnityEngine;
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

        // Reads device.json if it exists, or creates a new one
        internal static DeviceIdentity LoadOrCreate()
        {
            var path = PlayScopeDirectory.DeviceFile;

            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var dto = Internal.SimpleJson.Deserialize(json);
                    if (dto != null && dto.TryGetValue("sdk_user_id", out var id) && id is string idStr && !string.IsNullOrEmpty(idStr))
                        return new DeviceIdentity(idStr);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PlayScope] Failed to read device.json, regenerating. {ex.Message}");
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
    }
}
