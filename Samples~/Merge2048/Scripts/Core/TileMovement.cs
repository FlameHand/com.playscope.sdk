namespace Merge2048.Core
{
    public readonly struct TileMovement
    {
        public readonly int FromRow;
        public readonly int FromCol;
        public readonly int ToRow;
        public readonly int ToCol;
        public readonly bool ConsumedByMerge;

        public TileMovement(int fromRow, int fromCol, int toRow, int toCol, bool consumedByMerge)
        {
            FromRow = fromRow;
            FromCol = fromCol;
            ToRow = toRow;
            ToCol = toCol;
            ConsumedByMerge = consumedByMerge;
        }
    }

    public readonly struct MergeEvent
    {
        public readonly int Row;
        public readonly int Col;
        public readonly int ResultValue;

        public MergeEvent(int row, int col, int resultValue)
        {
            Row = row;
            Col = col;
            ResultValue = resultValue;
        }
    }

    public readonly struct SpawnEvent
    {
        public readonly int Row;
        public readonly int Col;
        public readonly int Value;

        public SpawnEvent(int row, int col, int value)
        {
            Row = row;
            Col = col;
            Value = value;
        }
    }
}
