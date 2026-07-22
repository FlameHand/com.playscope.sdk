using System;
using UnityEngine;

namespace Merge2048.Integration
{
    public static class AnonymousPlayerId
    {
        private const string PLAYER_ID_KEY = "Merge2048_AnonymousPlayerId";

        // Never put PII (email, handle, real name) into the value passed to
        // PlayScope.SetUserData — this generated GUID is the only identifier
        // this sample ever sends.
        public static string GetOrCreate()
        {
            if (PlayerPrefs.HasKey(PLAYER_ID_KEY))
            {
                return PlayerPrefs.GetString(PLAYER_ID_KEY);
            }

            string newId = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(PLAYER_ID_KEY, newId);
            PlayerPrefs.Save();

            return newId;
        }
    }
}
