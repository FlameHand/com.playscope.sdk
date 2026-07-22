using System;
using System.Collections.Generic;

namespace Merge2048.Core
{
    public readonly struct MoveResult
    {
        public readonly bool Changed;
        public readonly int ScoreGained;
        public readonly int MergeCount;
        public readonly int HighestTile;
        public readonly IReadOnlyList<TileMovement> Movements;
        public readonly IReadOnlyList<MergeEvent> Merges;
        public readonly IReadOnlyList<SpawnEvent> Spawns;

        public MoveResult(
            bool changed,
            int scoreGained,
            int mergeCount,
            int highestTile,
            IReadOnlyList<TileMovement> movements,
            IReadOnlyList<MergeEvent> merges,
            IReadOnlyList<SpawnEvent> spawns)
        {
            Changed = changed;
            ScoreGained = scoreGained;
            MergeCount = mergeCount;
            HighestTile = highestTile;
            Movements = movements ?? Array.Empty<TileMovement>();
            Merges = merges ?? Array.Empty<MergeEvent>();
            Spawns = spawns ?? Array.Empty<SpawnEvent>();
        }
    }
}
