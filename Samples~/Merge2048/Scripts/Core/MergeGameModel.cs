using System;
using System.Collections.Generic;

namespace Merge2048.Core
{
    public sealed class MergeGameModel
    {
        private readonly TileSpawner _tileSpawner;

        public Difficulty Difficulty { get; }
        public Board Board { get; }
        public int Score { get; private set; }
        public int MoveCount { get; private set; }
        public int HighestTile { get; private set; }
        public bool IsGameOver { get; private set; }

        public event Action Started;
        public event Action<MoveResult> MoveApplied;
        public event Action<int> ScoreChanged;
        public event Action<int> HighestTileChanged;
        public event Action GameOver;

        public MergeGameModel(Difficulty difficulty, Random rng)
        {
            Difficulty = difficulty;
            Board = new Board();
            _tileSpawner = new TileSpawner(rng);
        }

        public void Start()
        {
            Score = 0;
            MoveCount = 0;
            HighestTile = 0;
            IsGameOver = false;
            Board.Clear();

            int spawnCount = DifficultyConfig.SpawnCountFor(Difficulty);
            _tileSpawner.SpawnRandom(Board, spawnCount);

            HighestTile = ComputeHighestTile();

            Started?.Invoke();
        }

        public MoveResult ApplyMove(Direction direction)
        {
            var result = MoveResolver.Resolve(Board, direction);

            if (!result.Changed)
            {
                return result;
            }

            MoveCount++;
            Score += result.ScoreGained;

            if (result.ScoreGained > 0)
            {
                ScoreChanged?.Invoke(Score);
            }

            int spawnCount = DifficultyConfig.SpawnCountFor(Difficulty);
            var spawnedCells = new List<(int Row, int Col)>();
            _tileSpawner.SpawnRandom(Board, spawnCount, DifficultyConfig.START_TILE_VALUE, spawnedCells);

            var spawns = new SpawnEvent[spawnedCells.Count];
            for (int i = 0; i < spawnedCells.Count; i++)
            {
                spawns[i] = new SpawnEvent(spawnedCells[i].Row, spawnedCells[i].Col, DifficultyConfig.START_TILE_VALUE);
            }

            result = new MoveResult(
                result.Changed,
                result.ScoreGained,
                result.MergeCount,
                result.HighestTile,
                result.Movements,
                result.Merges,
                spawns);

            int newHighestTile = ComputeHighestTile();
            if (newHighestTile > HighestTile)
            {
                HighestTile = newHighestTile;
                HighestTileChanged?.Invoke(HighestTile);
            }

            MoveApplied?.Invoke(result);

            IsGameOver = Board.IsFull() && !Board.HasAdjacentEqualPair();

            if (IsGameOver)
            {
                GameOver?.Invoke();
            }

            return result;
        }

        public void RestoreSnapshot(int[,] cells, int score, int moveCount, int highestTile)
        {
            Board.LoadSnapshot(cells);
            Score = score;
            MoveCount = moveCount;
            HighestTile = highestTile;
            IsGameOver = false;
        }

        private int ComputeHighestTile()
        {
            int highest = 0;

            for (int row = 0; row < Board.SIZE; row++)
            {
                for (int col = 0; col < Board.SIZE; col++)
                {
                    int value = Board.Get(row, col);
                    if (value > highest)
                    {
                        highest = value;
                    }
                }
            }

            return highest;
        }
    }
}
