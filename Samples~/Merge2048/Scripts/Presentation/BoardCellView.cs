using UnityEngine;
using UnityEngine.UI;

namespace Merge2048.Presentation
{
    // Static background frame square, one per board cell, laid out by BoardView's
    // GridLayoutGroup. Always shows the empty-cell look; never carries a number —
    // that is TileView's job as a free-floating piece on top of this layer.
    public sealed class BoardCellView : MonoBehaviour
    {
        public static BoardCellView Create(Transform parent)
        {
            var go = new GameObject("BoardCell", typeof(RectTransform));
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var cellView = go.AddComponent<BoardCellView>();
            cellView.BuildView();
            return cellView;
        }

        private void BuildView()
        {
            var backgroundImage = gameObject.AddComponent<Image>();
            backgroundImage.sprite = RoundedRectSprite.Get();
            backgroundImage.type = Image.Type.Sliced;
            backgroundImage.color = Merge2048Theme.EMPTY_CELL_COLOR;
        }
    }
}
