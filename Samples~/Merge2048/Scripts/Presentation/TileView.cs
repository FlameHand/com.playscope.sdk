using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Merge2048.Presentation
{
    public sealed class TileView : MonoBehaviour
    {
        private static readonly Color EMPTY_CELL_COLOR = new Color32(0xCD, 0xC1, 0xB4, 0x59);
        private static readonly Color MAX_TILE_COLOR = new Color32(0x3C, 0x3A, 0x32, 0xFF);
        private static readonly Color DARK_LABEL_COLOR = new Color32(0x77, 0x6E, 0x65, 0xFF);
        private static readonly Color LIGHT_LABEL_COLOR = new Color32(0xF9, 0xF6, 0xF2, 0xFF);

        private const float LABEL_FONT_SIZE = 48f;
        private const float LABEL_FONT_SIZE_MIN = 12f;
        private const float LABEL_FONT_SIZE_MAX = 64f;

        private Image _backgroundImage;
        private TextMeshProUGUI _valueLabel;

        public static TileView Create(Transform parent)
        {
            var go = new GameObject("Tile", typeof(RectTransform));
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var tileView = go.AddComponent<TileView>();
            tileView.BuildView();
            return tileView;
        }

        public void SetValue(int value)
        {
            if (_backgroundImage == null || _valueLabel == null)
            {
                return;
            }

            if (value <= 0)
            {
                _backgroundImage.color = EMPTY_CELL_COLOR;
                _valueLabel.text = string.Empty;
                return;
            }

            _backgroundImage.color = ColorForValue(value);
            _valueLabel.text = value.ToString();
            _valueLabel.color = value <= 4 ? DARK_LABEL_COLOR : LIGHT_LABEL_COLOR;
        }

        private void BuildView()
        {
            _backgroundImage = gameObject.AddComponent<Image>();
            _backgroundImage.color = EMPTY_CELL_COLOR;

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
            _valueLabel.color = DARK_LABEL_COLOR;
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
