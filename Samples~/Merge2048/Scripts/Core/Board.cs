using System.Collections.Generic;

namespace Merge2048.Core
{
    public sealed class Board
    {
        public const int SIZE = 6;

        private readonly int[,] _cells;

        public Board()
        {
            _cells = new int[SIZE, SIZE];
        }

        public int Get(int row, int col)
        {
            return _cells[row, col];
        }

        public void Set(int row, int col, int value)
        {
            _cells[row, col] = value;
        }

        public bool IsFull()
        {
            for (int row = 0; row < SIZE; row++)
            {
                for (int col = 0; col < SIZE; col++)
                {
                    if (_cells[row, col] == 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public List<(int Row, int Col)> GetEmptyCells()
        {
            var result = new List<(int Row, int Col)>();

            for (int row = 0; row < SIZE; row++)
            {
                for (int col = 0; col < SIZE; col++)
                {
                    if (_cells[row, col] == 0)
                    {
                        result.Add((row, col));
                    }
                }
            }

            return result;
        }

        public int[,] Snapshot()
        {
            var copy = new int[SIZE, SIZE];

            for (int row = 0; row < SIZE; row++)
            {
                for (int col = 0; col < SIZE; col++)
                {
                    copy[row, col] = _cells[row, col];
                }
            }

            return copy;
        }

        public bool HasAdjacentEqualPair()
        {
            for (int row = 0; row < SIZE; row++)
            {
                for (int col = 0; col < SIZE - 1; col++)
                {
                    int left = _cells[row, col];
                    int right = _cells[row, col + 1];

                    if (left != 0 && left == right)
                    {
                        return true;
                    }
                }
            }

            for (int col = 0; col < SIZE; col++)
            {
                for (int row = 0; row < SIZE - 1; row++)
                {
                    int top = _cells[row, col];
                    int bottom = _cells[row + 1, col];

                    if (top != 0 && top == bottom)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void LoadSnapshot(int[,] snapshot)
        {
            for (int row = 0; row < SIZE; row++)
            {
                for (int col = 0; col < SIZE; col++)
                {
                    _cells[row, col] = snapshot[row, col];
                }
            }
        }

        public void Clear()
        {
            for (int row = 0; row < SIZE; row++)
            {
                for (int col = 0; col < SIZE; col++)
                {
                    _cells[row, col] = 0;
                }
            }
        }
    }
}
