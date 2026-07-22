using System.Collections.Generic;

namespace Merge2048.Core
{
    public static class MoveResolver
    {
        public static MoveResult Resolve(Board board, Direction direction)
        {
            if (direction == Direction.Unknown)
            {
                return new MoveResult(false, 0, 0, 0, null, null, null);
            }

            int size = Board.SIZE;
            bool anyChanged = false;
            int totalScoreGained = 0;
            int totalMergeCount = 0;
            var movements = new List<TileMovement>();
            var merges = new List<MergeEvent>();

            switch (direction)
            {
                case Direction.Left:
                {
                    for (int row = 0; row < size; row++)
                    {
                        var line = new int[size];
                        for (int col = 0; col < size; col++)
                        {
                            line[col] = board.Get(row, col);
                        }

                        var lineResult = ProcessLine(line);
                        WriteRow(board, row, lineResult.Line);
                        anyChanged = anyChanged || lineResult.Changed;
                        totalScoreGained += lineResult.ScoreGained;
                        totalMergeCount += lineResult.MergeCount;
                        AppendLineEvents(direction, row, lineResult, movements, merges);
                    }

                    break;
                }
                case Direction.Right:
                {
                    for (int row = 0; row < size; row++)
                    {
                        var line = new int[size];
                        for (int col = 0; col < size; col++)
                        {
                            line[col] = board.Get(row, size - 1 - col);
                        }

                        var lineResult = ProcessLine(line);
                        WriteRow(board, row, Reverse(lineResult.Line));
                        anyChanged = anyChanged || lineResult.Changed;
                        totalScoreGained += lineResult.ScoreGained;
                        totalMergeCount += lineResult.MergeCount;
                        AppendLineEvents(direction, row, lineResult, movements, merges);
                    }

                    break;
                }
                case Direction.Up:
                {
                    for (int col = 0; col < size; col++)
                    {
                        var line = new int[size];
                        for (int row = 0; row < size; row++)
                        {
                            line[row] = board.Get(row, col);
                        }

                        var lineResult = ProcessLine(line);
                        WriteColumn(board, col, lineResult.Line);
                        anyChanged = anyChanged || lineResult.Changed;
                        totalScoreGained += lineResult.ScoreGained;
                        totalMergeCount += lineResult.MergeCount;
                        AppendLineEvents(direction, col, lineResult, movements, merges);
                    }

                    break;
                }
                case Direction.Down:
                {
                    for (int col = 0; col < size; col++)
                    {
                        var line = new int[size];
                        for (int row = 0; row < size; row++)
                        {
                            line[row] = board.Get(size - 1 - row, col);
                        }

                        var lineResult = ProcessLine(line);
                        WriteColumn(board, col, Reverse(lineResult.Line));
                        anyChanged = anyChanged || lineResult.Changed;
                        totalScoreGained += lineResult.ScoreGained;
                        totalMergeCount += lineResult.MergeCount;
                        AppendLineEvents(direction, col, lineResult, movements, merges);
                    }

                    break;
                }
            }

            int highestTile = 0;
            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    int value = board.Get(row, col);
                    if (value > highestTile)
                    {
                        highestTile = value;
                    }
                }
            }

            return new MoveResult(anyChanged, totalScoreGained, totalMergeCount, highestTile, movements, merges, null);
        }

        // Same fixedIndex/lineIndex -> board cell formula applies whether lineIndex was used
        // to READ the pre-move source value or WRITE the post-move result value (verified by
        // tracing the Reverse()+WriteRow/WriteColumn combination for Right/Down above).
        private static (int Row, int Col) LineIndexToCell(Direction direction, int fixedIndex, int lineIndex)
        {
            switch (direction)
            {
                case Direction.Left:
                {
                    return (fixedIndex, lineIndex);
                }
                case Direction.Right:
                {
                    return (fixedIndex, Board.SIZE - 1 - lineIndex);
                }
                case Direction.Up:
                {
                    return (lineIndex, fixedIndex);
                }
                case Direction.Down:
                {
                    return (Board.SIZE - 1 - lineIndex, fixedIndex);
                }
                default:
                {
                    return (fixedIndex, lineIndex);
                }
            }
        }

        private static void AppendLineEvents(
            Direction direction,
            int fixedIndex,
            LineResult lineResult,
            List<TileMovement> movements,
            List<MergeEvent> merges)
        {
            if (lineResult.Movements != null)
            {
                for (int i = 0; i < lineResult.Movements.Count; i++)
                {
                    var lineMovement = lineResult.Movements[i];
                    var from = LineIndexToCell(direction, fixedIndex, lineMovement.SourceIndex);
                    var to = LineIndexToCell(direction, fixedIndex, lineMovement.DestinationIndex);
                    movements.Add(new TileMovement(from.Row, from.Col, to.Row, to.Col, lineMovement.ConsumedByMerge));
                }
            }

            if (lineResult.Merges != null)
            {
                for (int i = 0; i < lineResult.Merges.Count; i++)
                {
                    var lineMerge = lineResult.Merges[i];
                    var cell = LineIndexToCell(direction, fixedIndex, lineMerge.DestinationIndex);
                    merges.Add(new MergeEvent(cell.Row, cell.Col, lineMerge.ResultValue));
                }
            }
        }

        private static LineResult ProcessLine(int[] line)
        {
            int size = Board.SIZE;
            var compacted = new int[size];
            var sourceIndex = new int[size];
            int count = 0;

            for (int i = 0; i < size; i++)
            {
                if (line[i] != 0)
                {
                    compacted[count] = line[i];
                    sourceIndex[count] = i;
                    count++;
                }
            }

            var mergedFromSourceIndex = new int[size];
            for (int i = 0; i < size; i++)
            {
                mergedFromSourceIndex[i] = -1;
            }

            int scoreGained = 0;
            int mergeCount = 0;

            for (int i = 0; i < count - 1; i++)
            {
                if (compacted[i] != 0 && compacted[i] == compacted[i + 1])
                {
                    compacted[i] *= 2;
                    scoreGained += compacted[i];
                    mergeCount++;
                    mergedFromSourceIndex[i] = sourceIndex[i + 1];
                    compacted[i + 1] = 0;
                    i++;
                }
            }

            var result = new int[size];
            var movements = new List<LineMovement>();
            var merges = new List<LineMerge>();
            int writeIndex = 0;

            for (int i = 0; i < count; i++)
            {
                if (compacted[i] != 0)
                {
                    int destinationIndex = writeIndex;
                    result[writeIndex] = compacted[i];

                    movements.Add(new LineMovement(sourceIndex[i], destinationIndex, false));

                    if (mergedFromSourceIndex[i] >= 0)
                    {
                        movements.Add(new LineMovement(mergedFromSourceIndex[i], destinationIndex, true));
                        merges.Add(new LineMerge(destinationIndex, compacted[i]));
                    }

                    writeIndex++;
                }
            }

            for (int i = writeIndex; i < size; i++)
            {
                result[i] = 0;
            }

            bool changed = false;

            for (int i = 0; i < size; i++)
            {
                if (result[i] != line[i])
                {
                    changed = true;
                    break;
                }
            }

            return new LineResult(result, scoreGained, mergeCount, changed, movements, merges);
        }

        private static int[] Reverse(int[] line)
        {
            int size = line.Length;
            var reversed = new int[size];

            for (int i = 0; i < size; i++)
            {
                reversed[i] = line[size - 1 - i];
            }

            return reversed;
        }

        private static void WriteRow(Board board, int row, int[] line)
        {
            for (int col = 0; col < line.Length; col++)
            {
                board.Set(row, col, line[col]);
            }
        }

        private static void WriteColumn(Board board, int col, int[] line)
        {
            for (int row = 0; row < line.Length; row++)
            {
                board.Set(row, col, line[row]);
            }
        }

        private readonly struct LineMovement
        {
            public readonly int SourceIndex;
            public readonly int DestinationIndex;
            public readonly bool ConsumedByMerge;

            public LineMovement(int sourceIndex, int destinationIndex, bool consumedByMerge)
            {
                SourceIndex = sourceIndex;
                DestinationIndex = destinationIndex;
                ConsumedByMerge = consumedByMerge;
            }
        }

        private readonly struct LineMerge
        {
            public readonly int DestinationIndex;
            public readonly int ResultValue;

            public LineMerge(int destinationIndex, int resultValue)
            {
                DestinationIndex = destinationIndex;
                ResultValue = resultValue;
            }
        }

        private readonly struct LineResult
        {
            public readonly int[] Line;
            public readonly int ScoreGained;
            public readonly int MergeCount;
            public readonly bool Changed;
            public readonly List<LineMovement> Movements;
            public readonly List<LineMerge> Merges;

            public LineResult(
                int[] line,
                int scoreGained,
                int mergeCount,
                bool changed,
                List<LineMovement> movements,
                List<LineMerge> merges)
            {
                Line = line;
                ScoreGained = scoreGained;
                MergeCount = mergeCount;
                Changed = changed;
                Movements = movements;
                Merges = merges;
            }
        }
    }
}
