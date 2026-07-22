namespace Merge2048.Core
{
    public static class DifficultyConfig
    {
        public const int START_TILE_VALUE = 2;

        public static int SpawnCountFor(Difficulty difficulty)
        {
            switch (difficulty)
            {
                case Difficulty.Easy:
                {
                    return 1;
                }
                case Difficulty.Medium:
                {
                    return 2;
                }
                case Difficulty.Hard:
                {
                    return 4;
                }
                case Difficulty.Unknown:
                default:
                {
                    return 1;
                }
            }
        }
    }
}
