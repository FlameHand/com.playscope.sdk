using UnityEngine;
using UnityEngine.UI;
using Merge2048.Core;

namespace Merge2048.Presentation
{
    public sealed class BoardView : MonoBehaviour
    {
        private const float TILE_SPACING = 6f;
        private const float DEFAULT_CELL_SIZE = 140f;

        private TileView[] _tiles;

        public void Initialize()
        {
            var rectTransform = transform as RectTransform;
            if (rectTransform == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();

            var gridLayoutGroup = gameObject.GetComponent<GridLayoutGroup>();
            if (gridLayoutGroup == null)
            {
                gridLayoutGroup = gameObject.AddComponent<GridLayoutGroup>();
            }

            int size = Board.SIZE;
            var containerSize = rectTransform.rect.size;
            float cellWidth = (containerSize.x - TILE_SPACING * (size - 1)) / size;
            float cellHeight = (containerSize.y - TILE_SPACING * (size - 1)) / size;
            float cellSize = Mathf.Min(cellWidth, cellHeight);

            if (cellSize <= 0f)
            {
                cellSize = DEFAULT_CELL_SIZE;
            }

            gridLayoutGroup.cellSize = new Vector2(cellSize, cellSize);
            gridLayoutGroup.spacing = new Vector2(TILE_SPACING, TILE_SPACING);
            gridLayoutGroup.childAlignment = TextAnchor.MiddleCenter;
            gridLayoutGroup.startCorner = GridLayoutGroup.Corner.UpperLeft;
            gridLayoutGroup.startAxis = GridLayoutGroup.Axis.Horizontal;
            gridLayoutGroup.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayoutGroup.constraintCount = size;

            _tiles = new TileView[size * size];

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    var tile = TileView.Create(transform);
                    _tiles[row * size + col] = tile;
                }
            }
        }

        public void Render(Board board)
        {
            if (board == null || _tiles == null)
            {
                return;
            }

            int size = Board.SIZE;

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    var tile = _tiles[row * size + col];
                    if (tile != null)
                    {
                        tile.SetValue(board.Get(row, col));
                    }
                }
            }
        }
    }
}
