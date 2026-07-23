using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Merge2048.Presentation
{
    // Built procedurally at runtime, same pattern as ScreenFlow — no scene-authored
    // references. Owns its own Canvas so it can render before ScreenFlow exists.
    public sealed class BootScreenView : MonoBehaviour
    {
        private const float REFERENCE_WIDTH = 1080f;
        private const float REFERENCE_HEIGHT = 1920f;
        private const float REFERENCE_MATCH = 0.5f;

        private const float STACK_WIDTH = 700f;
        private const float STACK_SPACING = 40f;
        private const float PROGRESS_BAR_HEIGHT = 32f;

        private Image _progressFillImage;

        private void Awake()
        {
            BuildCanvas();
            BuildBackground();
            BuildContent();
        }

        public void SetProgress(float progress01)
        {
            if (_progressFillImage != null)
            {
                _progressFillImage.fillAmount = Mathf.Clamp01(progress01);
            }
        }

        private void BuildCanvas()
        {
            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
            }

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = gameObject.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                scaler = gameObject.AddComponent<CanvasScaler>();
            }

            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(REFERENCE_WIDTH, REFERENCE_HEIGHT);
            scaler.matchWidthOrHeight = REFERENCE_MATCH;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }
        }

        private void BuildBackground()
        {
            var go = new GameObject("Background", typeof(RectTransform));
            go.transform.SetParent(transform, false);

            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var image = go.AddComponent<Image>();
            image.color = Merge2048Theme.BACKGROUND_COLOR;
            image.raycastTarget = false;
        }

        private void BuildContent()
        {
            var stackGo = new GameObject("BootStack", typeof(RectTransform));
            stackGo.transform.SetParent(transform, false);

            var stackRect = stackGo.GetComponent<RectTransform>();
            stackRect.anchorMin = new Vector2(0.5f, 0.5f);
            stackRect.anchorMax = new Vector2(0.5f, 0.5f);
            stackRect.pivot = new Vector2(0.5f, 0.5f);
            stackRect.sizeDelta = new Vector2(STACK_WIDTH, 0f);

            var verticalLayoutGroup = stackGo.AddComponent<VerticalLayoutGroup>();
            verticalLayoutGroup.childAlignment = TextAnchor.MiddleCenter;
            verticalLayoutGroup.spacing = STACK_SPACING;
            verticalLayoutGroup.childControlWidth = true;
            verticalLayoutGroup.childControlHeight = true;
            verticalLayoutGroup.childForceExpandWidth = true;
            verticalLayoutGroup.childForceExpandHeight = false;

            var contentSizeFitter = stackGo.AddComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            BuildTitleLabel(stackGo.transform);
            BuildProgressBar(stackGo.transform);
        }

        private static void BuildTitleLabel(Transform parent)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = "2048 Merge";
            label.fontSize = Merge2048Theme.TITLE_FONT_SIZE;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Merge2048Theme.TEXT_ON_LIGHT_COLOR;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = Merge2048Theme.TITLE_FONT_SIZE * 1.5f;
        }

        private void BuildProgressBar(Transform parent)
        {
            var barGo = new GameObject("ProgressBar", typeof(RectTransform));
            barGo.transform.SetParent(parent, false);

            var backgroundImage = barGo.AddComponent<Image>();
            backgroundImage.sprite = RoundedRectSprite.Get();
            backgroundImage.type = Image.Type.Sliced;
            backgroundImage.color = Merge2048Theme.BOARD_BACKGROUND_COLOR;

            var barLayoutElement = barGo.AddComponent<LayoutElement>();
            barLayoutElement.preferredWidth = STACK_WIDTH;
            barLayoutElement.preferredHeight = PROGRESS_BAR_HEIGHT;
            barLayoutElement.minHeight = PROGRESS_BAR_HEIGHT;

            var fillGo = new GameObject("ProgressFill", typeof(RectTransform));
            fillGo.transform.SetParent(barGo.transform, false);

            var fillRect = fillGo.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            _progressFillImage = fillGo.AddComponent<Image>();
            _progressFillImage.sprite = RoundedRectSprite.Get();
            _progressFillImage.type = Image.Type.Filled;
            _progressFillImage.fillMethod = Image.FillMethod.Horizontal;
            _progressFillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            _progressFillImage.fillAmount = 0f;
            _progressFillImage.color = Merge2048Theme.BUTTON_NORMAL_COLOR;
        }
    }
}
