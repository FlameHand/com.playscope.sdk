namespace Merge2048.Core
{
    public static class MoveResolver
    {
        public static MoveResult Resolve(Board board, Direction direction)
        {
            if (direction == Direction.Unknown)
            {
                return new MoveResult(false, 0, 0, 0);
            }

            int size = Board.SIZE;
            bool anyChanged = false;
            int totalScoreGained = 0;
            int totalMergeCount = 0;

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

            return new MoveResult(anyChanged, totalScoreGained, totalMergeCount, highestTile);
        }

        private static LineResult ProcessLine(int[] line)
        {
            int size = Board.SIZE;
            var compacted = new int[size];
            int count = 0;

            for (int i = 0; i < size; i++)
            {
                if (line[i] != 0)
                {
                    compacted[count] = line[i];
                    count++;
                }
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
                    compacted[i + 1] = 0;
                    i++;
                }
            }

            var result = new int[size];
            int writeIndex = 0;

            for (int i = 0; i < size; i++)
            {
                if (compacted[i] != 0)
                {
                    result[writeIndex] = compacted[i];
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

            return new LineResult(result, scoreGained, mergeCount, changed);
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

        private readonly struct LineResult
        {
            public readonly int[] Line;
            public readonly int ScoreGained;
            public readonly int MergeCount;
            public readonly bool Changed;

            public LineResult(int[] line, int scoreGained, int mergeCount, bool changed)
            {
                Line = line;
                ScoreGained = scoreGained;
                MergeCount = mergeCount;
                Changed = changed;
            }
        }
    }
}
