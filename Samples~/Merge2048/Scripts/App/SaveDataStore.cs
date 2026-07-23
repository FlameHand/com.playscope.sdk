using System;
using System.Text;
using UnityEngine;
using Merge2048.Core;

namespace Merge2048.App
{
    // No JSON library available to this sample (no JsonUtility per coding_rules.md,
    // no Newtonsoft reference in Merge2048.Demo.asmdef) — hand-rolled delimited string.
    // Format: version|difficulty|score|moveCount|highestTile|cell0,cell1,...,cell{N-1}
    // where N = Board.SIZE * Board.SIZE, cells in row-major order.
    public static class SaveDataStore
    {
        private const string SAVE_KEY = "Merge2048_SaveGame";
        private const int CURRENT_VERSION = 1;
        private const char FIELD_SEPARATOR = '|';
        private const char CELL_SEPARATOR = ',';
        private const int FIELD_COUNT = 6;
        private const int CELL_COUNT = Board.SIZE * Board.SIZE;

        public enum SaveLoadOutcome
        {
            Unknown = 0,
            Success = 1,
            NotFound = 2,
            Corrupted = 3,
            OldFormat = 4,
        }

        public readonly struct SaveLoadResult
        {
            public readonly SaveLoadOutcome Outcome;
            public readonly SaveData Data;
            public readonly Exception Error;

            public SaveLoadResult(SaveLoadOutcome outcome, SaveData data, Exception error)
            {
                Outcome = outcome;
                Data = data;
                Error = error;
            }
        }

        public static bool HasSave => PlayerPrefs.HasKey(SAVE_KEY);

        public static SaveLoadResult TryLoad()
        {
            if (!PlayerPrefs.HasKey(SAVE_KEY))
            {
                return new SaveLoadResult(SaveLoadOutcome.NotFound, default, null);
            }

            string raw = PlayerPrefs.GetString(SAVE_KEY);

            try
            {
                string[] fields = raw.Split(FIELD_SEPARATOR);
                if (fields.Length != FIELD_COUNT)
                {
                    return new SaveLoadResult(
                        SaveLoadOutcome.Corrupted,
                        default,
                        new FormatException($"Expected {FIELD_COUNT} fields, got {fields.Length}."));
                }

                int version = int.Parse(fields[0]);
                if (version < CURRENT_VERSION)
                {
                    return new SaveLoadResult(SaveLoadOutcome.OldFormat, default, null);
                }

                int difficultyValue = int.Parse(fields[1]);
                if (!Enum.IsDefined(typeof(Difficulty), difficultyValue))
                {
                    return new SaveLoadResult(
                        SaveLoadOutcome.Corrupted,
                        default,
                        new FormatException($"Unrecognized difficulty value {difficultyValue}."));
                }

                var difficulty = (Difficulty)difficultyValue;
                int score = int.Parse(fields[2]);
                int moveCount = int.Parse(fields[3]);
                int highestTile = int.Parse(fields[4]);

                string[] cellTokens = fields[5].Split(CELL_SEPARATOR);
                if (cellTokens.Length != CELL_COUNT)
                {
                    return new SaveLoadResult(
                        SaveLoadOutcome.Corrupted,
                        default,
                        new FormatException($"Expected {CELL_COUNT} cells, got {cellTokens.Length}."));
                }

                var cells = new int[Board.SIZE, Board.SIZE];
                for (int i = 0; i < CELL_COUNT; i++)
                {
                    int row = i / Board.SIZE;
                    int col = i % Board.SIZE;
                    cells[row, col] = int.Parse(cellTokens[i]);
                }

                var data = new SaveData(difficulty, score, moveCount, highestTile, cells);
                return new SaveLoadResult(SaveLoadOutcome.Success, data, null);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException)
            {
                return new SaveLoadResult(SaveLoadOutcome.Corrupted, default, ex);
            }
        }

        public static void Save(SaveData data)
        {
            var builder = new StringBuilder();
            builder.Append(CURRENT_VERSION).Append(FIELD_SEPARATOR);
            builder.Append((int)data.Difficulty).Append(FIELD_SEPARATOR);
            builder.Append(data.Score).Append(FIELD_SEPARATOR);
            builder.Append(data.MoveCount).Append(FIELD_SEPARATOR);
            builder.Append(data.HighestTile).Append(FIELD_SEPARATOR);

            for (int row = 0; row < Board.SIZE; row++)
            {
                for (int col = 0; col < Board.SIZE; col++)
                {
                    if (row > 0 || col > 0)
                    {
                        builder.Append(CELL_SEPARATOR);
                    }

                    builder.Append(data.Cells[row, col]);
                }
            }

            PlayerPrefs.SetString(SAVE_KEY, builder.ToString());
            PlayerPrefs.Save();
        }

        public static void Clear()
        {
            PlayerPrefs.DeleteKey(SAVE_KEY);
        }
    }
}
