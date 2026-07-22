using System;
using UnityEngine;

namespace Merge2048.App
{
    public static class HighScoreStore
    {
        private const string HIGH_SCORE_KEY = "Merge2048_HighScore";

        public static event Action<Exception> LoadFailed;

        public static int Load()
        {
            if (!PlayerPrefs.HasKey(HIGH_SCORE_KEY))
            {
                return 0;
            }

            string raw = PlayerPrefs.GetString(HIGH_SCORE_KEY);

            try
            {
                return int.Parse(raw);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                LoadFailed?.Invoke(ex);
                return 0;
            }
        }

        public static void TrySave(int latestScore)
        {
            int currentBest = Load();

            if (latestScore > currentBest)
            {
                PlayerPrefs.SetString(HIGH_SCORE_KEY, latestScore.ToString());
                PlayerPrefs.Save();
            }
        }
    }
}
