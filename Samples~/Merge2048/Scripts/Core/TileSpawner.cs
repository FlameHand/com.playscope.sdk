namespace Merge2048.Core
{
    public sealed class TileSpawner
    {
        private readonly System.Random _rng;

        public TileSpawner(System.Random rng)
        {
            _rng = rng;
        }

        public int SpawnRandom(Board board, int count, int value = DifficultyConfig.START_TILE_VALUE)
        {
            var emptyCells = board.GetEmptyCells();
            if (emptyCells.Count == 0)
            {
                return 0;
            }

            int spawnCount = count < emptyCells.Count ? count : emptyCells.Count;
            if (spawnCount <= 0)
            {
                return 0;
            }

            int remaining = emptyCells.Count;
            for (int i = 0; i < spawnCount; i++)
            {
                int pickIndex = _rng.Next(remaining);
                var cell = emptyCells[pickIndex];
                board.Set(cell.Row, cell.Col, value);

                remaining--;
                emptyCells[pickIndex] = emptyCells[remaining];
            }

            return spawnCount;
        }
    }
}
