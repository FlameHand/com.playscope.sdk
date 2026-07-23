using Merge2048.Core;

namespace Merge2048.App
{
    public readonly struct SaveData
    {
        public readonly Difficulty Difficulty;
        public readonly int Score;
        public readonly int MoveCount;
        public readonly int HighestTile;
        public readonly int[,] Cells;

        public SaveData(Difficulty difficulty, int score, int moveCount, int highestTile, int[,] cells)
        {
            Difficulty = difficulty;
            Score = score;
            MoveCount = moveCount;
            HighestTile = highestTile;
            Cells = cells;
        }
    }
}
