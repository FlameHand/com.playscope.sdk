using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Merge2048.Presentation
{
    // Free-floating movable numbered piece — NOT managed by a layout group. BoardView
    // positions it manually via RectTransform.anchoredPosition and drives slide/pop/spawn
    // animations directly against RectTransform. BoardCellView owns the static background.
    public sealed class TileView : MonoBehaviour
    {
        private static readonly Color MAX_TILE_COLOR = new Color32(0x3C, 0x3A, 0x32, 0xFF);

        private const float LABEL_FONT_SIZE = 48f;
        private const float LABEL_FONT_SIZE_MIN = 12f;
        private const float LABEL_FONT_SIZE_MAX = 64f;

        private Image _backgroundImage;
        private TextMeshProUGUI _valueLabel;

        public RectTransform RectTransform { get; private set; }
        public int Value { get; private set; }

        public static TileView Create(Transform parent, float cellSize)
        {
            var go = new GameObject("Tile", typeof(RectTransform));
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var tileView = go.AddComponent<TileView>();
            tileView.BuildView(cellSize);
            return tileView;
        }

        public void SetValue(int value)
        {
            if (_backgroundImage == null || _valueLabel == null || value <= 0)
            {
                return;
            }

            Value = value;
            _backgroundImage.color = ColorForValue(value);
            _valueLabel.text = value.ToString();
            _valueLabel.color = value <= 4 ? Merge2048Theme.TEXT_ON_LIGHT_COLOR : Merge2048Theme.TEXT_ON_DARK_COLOR;
        }

        private void BuildView(float cellSize)
        {
            RectTransform = GetComponent<RectTransform>();

            // Anchored to the parent layer's top-left (0,1) with a centered pivot so
            // BoardView.CellAnchoredPosition() (top-left-corner-origin formula) applies directly.
            RectTransform.anchorMin = new Vector2(0f, 1f);
            RectTransform.anchorMax = new Vector2(0f, 1f);
            RectTransform.pivot = new Vector2(0.5f, 0.5f);
            RectTransform.sizeDelta = new Vector2(cellSize, cellSize);

            _backgroundImage = gameObject.AddComponent<Image>();
            _backgroundImage.sprite = RoundedRectSprite.Get();
            _backgroundImage.type = Image.Type.Sliced;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(transform, false);

            var labelRect = labelGo.GetComponent<RectTransform>();
            if (labelRect != null)
            {
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
            }

            _valueLabel = labelGo.AddComponent<TextMeshProUGUI>();
            _valueLabel.alignment = TextAlignmentOptions.Center;
            _valueLabel.fontSize = LABEL_FONT_SIZE;
            _valueLabel.enableAutoSizing = true;
            _valueLabel.fontSizeMin = LABEL_FONT_SIZE_MIN;
            _valueLabel.fontSizeMax = LABEL_FONT_SIZE_MAX;
            _valueLabel.color = Merge2048Theme.TEXT_ON_LIGHT_COLOR;
            _valueLabel.text = string.Empty;
        }

        private static Color ColorForValue(int value)
        {
            switch (value)
            {
                case 2:
                {
                    return new Color32(0xEE, 0xE4, 0xDA, 0xFF);
                }
                case 4:
                {
                    return new Color32(0xED, 0xE0, 0xC8, 0xFF);
                }
                case 8:
                {
                    return new Color32(0xF2, 0xB1, 0x79, 0xFF);
                }
                case 16:
                {
                    return new Color32(0xF5, 0x95, 0x63, 0xFF);
                }
                case 32:
                {
                    return new Color32(0xF6, 0x7C, 0x5F, 0xFF);
                }
                case 64:
                {
                    return new Color32(0xF6, 0x5E, 0x3B, 0xFF);
                }
                case 128:
                {
                    return new Color32(0xED, 0xCF, 0x72, 0xFF);
                }
                case 256:
                {
                    return new Color32(0xED, 0xCC, 0x61, 0xFF);
                }
                case 512:
                {
                    return new Color32(0xED, 0xC8, 0x50, 0xFF);
                }
                case 1024:
                {
                    return new Color32(0xED, 0xC5, 0x3F, 0xFF);
                }
                case 2048:
                {
                    return new Color32(0xED, 0xC2, 0x2E, 0xFF);
                }
                default:
                {
                    return MAX_TILE_COLOR;
                }
            }
        }
    }
}
