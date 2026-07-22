using System.Collections;
using UnityEngine;
using TMPro;

namespace Merge2048.Presentation
{
    // Short-lived floating "+N" label. Self-contained: spawns, animates, and destroys
    // itself — BoardView fires-and-forgets it, it does not gate move-animation completion.
    public sealed class ScorePopup : MonoBehaviour
    {
        // Width is a fraction of the board cell size, not a fixed pixel value — a fixed size
        // would overlap between two simultaneous adjacent merges (e.g. a swiped line of four
        // equal tiles produces two merge events one cell apart) once cell size shrinks below
        // it on narrower/notch-constrained layouts.
        private const float WIDTH_TO_CELL_RATIO = 0.7f;
        private const float HEIGHT_TO_CELL_RATIO = 0.32f;

        private RectTransform _rectTransform;
        private TextMeshProUGUI _label;
        private Vector2 _startPosition;

        public static ScorePopup Spawn(Transform parent, Vector2 anchoredPosition, int value, float cellSize)
        {
            var go = new GameObject("ScorePopup", typeof(RectTransform));
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var popup = go.AddComponent<ScorePopup>();
            popup.BuildView(anchoredPosition, value, cellSize);
            return popup;
        }

        private void BuildView(Vector2 anchoredPosition, int value, float cellSize)
        {
            _rectTransform = GetComponent<RectTransform>();
            _rectTransform.anchorMin = new Vector2(0f, 1f);
            _rectTransform.anchorMax = new Vector2(0f, 1f);
            _rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _rectTransform.sizeDelta = new Vector2(cellSize * WIDTH_TO_CELL_RATIO, cellSize * HEIGHT_TO_CELL_RATIO);
            _rectTransform.anchoredPosition = anchoredPosition;

            _startPosition = anchoredPosition;

            _label = gameObject.AddComponent<TextMeshProUGUI>();
            _label.text = "+" + value.ToString();
            _label.alignment = TextAlignmentOptions.Center;
            _label.fontSize = Merge2048Theme.SCORE_POPUP_FONT_SIZE;
            _label.enableAutoSizing = true;
            _label.fontSizeMin = Merge2048Theme.HUD_CAPTION_FONT_SIZE;
            _label.fontSizeMax = Merge2048Theme.SCORE_POPUP_FONT_SIZE;
            _label.color = Merge2048Theme.TEXT_ON_LIGHT_COLOR;
            _label.raycastTarget = false;

            StartCoroutine(PlayAndDestroy());
        }

        private IEnumerator PlayAndDestroy()
        {
            var startColor = _label.color;
            float elapsed = 0f;

            while (elapsed < Merge2048Theme.SCORE_POPUP_DURATION)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / Merge2048Theme.SCORE_POPUP_DURATION);

                _rectTransform.anchoredPosition =
                    _startPosition + new Vector2(0f, Merge2048Theme.SCORE_POPUP_RISE_DISTANCE * t);
                _label.color = new Color(startColor.r, startColor.g, startColor.b, Mathf.Lerp(1f, 0f, t));

                yield return null;
            }

            Destroy(gameObject);
        }
    }
}
