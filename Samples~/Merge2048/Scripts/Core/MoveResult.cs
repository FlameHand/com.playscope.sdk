namespace Merge2048.Core
{
    public readonly struct MoveResult
    {
        public readonly bool Changed;
        public readonly int ScoreGained;
        public readonly int MergeCount;
        public readonly int HighestTile;

        public MoveResult(bool changed, int scoreGained, int mergeCount, int highestTile)
        {
            Changed = changed;
            ScoreGained = scoreGained;
            MergeCount = mergeCount;
            HighestTile = highestTile;
        }
    }
}
