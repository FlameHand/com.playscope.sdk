using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using TMPro;

namespace Merge2048.Presentation
{
    // Acknowledged dev-tool exception — a gear button on the main menu that opens
    // sample-only diagnostics. No PlayScope references: DiagnosticsController (Integration)
    // owns the actual SDK calls and wires these events.
    public sealed class DiagnosticsPanelView : MonoBehaviour
    {
        private const int SORTING_ORDER = 900;

        private const float REFERENCE_WIDTH = 1080f;
        private const float REFERENCE_HEIGHT = 1920f;
        private const float REFERENCE_MATCH = 0.5f;

        private const float GEAR_SIZE = 96f;
        private const float GEAR_MARGIN = 24f;
        private const float GEAR_FONT_SIZE = 48f;

        private const float CARD_WIDTH = 820f;
        private const float CARD_PADDING = 32f;
        private const float STACK_SPACING = 18f;
        private const float BUTTON_HEIGHT = 104f;
        private const float TITLE_FONT_SIZE = 52f;
        private const float STATUS_FONT_SIZE = 28f;
        private const float BUTTON_FONT_SIZE = 32f;

        private GameObject _gearGo;
        private GameObject _overlayGo;
        private TMP_Text _statusLabel;

        public event Action SimulateAnrClicked;
        public event Action SpamLogClicked;
        public event Action SendPiiLogClicked;
        public event Action ResetClicked;
        public event Action ToggleFeedClicked;

        private void Awake()
        {
            EnsureEventSystem();
            BuildCanvas();
            BuildGearButton();
            BuildOverlay();
        }

        public void SetStatus(string text)
        {
            if (_statusLabel != null)
            {
                _statusLabel.text = text ?? string.Empty;
            }
        }

        public void SetGearVisible(bool visible)
        {
            if (_gearGo != null)
            {
                _gearGo.SetActive(visible);
            }
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            DontDestroyOnLoad(go);
        }

        private void BuildCanvas()
        {
            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
            }

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SORTING_ORDER;

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

        private void BuildGearButton()
        {
            _gearGo = new GameObject("GearButton", typeof(RectTransform));
            _gearGo.transform.SetParent(transform, false);

            var rectTransform = _gearGo.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(1f, 1f);
            rectTransform.anchorMax = new Vector2(1f, 1f);
            rectTransform.pivot = new Vector2(1f, 1f);
            rectTransform.anchoredPosition = new Vector2(-GEAR_MARGIN, -GEAR_MARGIN);
            rectTransform.sizeDelta = new Vector2(GEAR_SIZE, GEAR_SIZE);

            var image = _gearGo.AddComponent<Image>();
            image.sprite = RoundedRectSprite.Get();
            image.type = Image.Type.Sliced;

            var button = _gearGo.AddComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.normalColor = Merge2048Theme.BUTTON_NORMAL_COLOR;
            colors.highlightedColor = Merge2048Theme.BUTTON_HIGHLIGHTED_COLOR;
            colors.pressedColor = Merge2048Theme.BUTTON_PRESSED_COLOR;
            colors.disabledColor = Merge2048Theme.BUTTON_DISABLED_COLOR;
            button.colors = colors;
            button.onClick.AddListener(ToggleOverlay);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(_gearGo.transform, false);

            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "⚙";
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = GEAR_FONT_SIZE;
            label.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            label.raycastTarget = false;
        }

        private void ToggleOverlay()
        {
            if (_overlayGo != null)
            {
                _overlayGo.SetActive(!_overlayGo.activeSelf);
            }
        }

        private void CloseOverlay()
        {
            if (_overlayGo != null)
            {
                _overlayGo.SetActive(false);
            }
        }

        private void BuildOverlay()
        {
            _overlayGo = new GameObject("DiagnosticsOverlay", typeof(RectTransform));
            _overlayGo.transform.SetParent(transform, false);

            var overlayRect = _overlayGo.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;

            var scrim = _overlayGo.AddComponent<Image>();
            scrim.color = Merge2048Theme.GAME_OVER_SCRIM_COLOR;

            var card = BuildCard(_overlayGo.transform);

            CreateLabel(card, "Diagnostics", TITLE_FONT_SIZE);

            _statusLabel = CreateLabel(card, string.Empty, STATUS_FONT_SIZE);
            _statusLabel.alignment = TextAlignmentOptions.Left;

            CreateButton(card, "Simulate ANR").onClick.AddListener(() => SimulateAnrClicked?.Invoke());
            CreateButton(card, "Spam log ×20").onClick.AddListener(() => SpamLogClicked?.Invoke());
            CreateButton(card, "Send PII-looking log").onClick.AddListener(() => SendPiiLogClicked?.Invoke());
            CreateButton(card, "Reset consent & save").onClick.AddListener(() => ResetClicked?.Invoke());
            CreateButton(card, "Toggle Event Feed").onClick.AddListener(() => ToggleFeedClicked?.Invoke());
            CreateButton(card, "Close").onClick.AddListener(CloseOverlay);

            _overlayGo.SetActive(false);
        }

        private static RectTransform BuildCard(Transform parent)
        {
            var cardGo = new GameObject("Card", typeof(RectTransform));
            cardGo.transform.SetParent(parent, false);

            var rectTransform = cardGo.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.sizeDelta = new Vector2(CARD_WIDTH, 0f);

            var image = cardGo.AddComponent<Image>();
            image.sprite = RoundedRectSprite.Get();
            image.type = Image.Type.Sliced;
            image.color = Merge2048Theme.BOARD_BACKGROUND_COLOR;

            var verticalLayoutGroup = cardGo.AddComponent<VerticalLayoutGroup>();
            verticalLayoutGroup.childAlignment = TextAnchor.UpperCenter;
            verticalLayoutGroup.spacing = STACK_SPACING;
            verticalLayoutGroup.padding = new RectOffset((int)CARD_PADDING, (int)CARD_PADDING, (int)CARD_PADDING, (int)CARD_PADDING);
            verticalLayoutGroup.childControlWidth = true;
            verticalLayoutGroup.childControlHeight = true;
            verticalLayoutGroup.childForceExpandWidth = true;
            verticalLayoutGroup.childForceExpandHeight = false;

            var contentSizeFitter = cardGo.AddComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return rectTransform;
        }

        private static TMP_Text CreateLabel(Transform parent, string text, float fontSize)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text ?? string.Empty;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            label.enableWordWrapping = true;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = fontSize * 1.5f;

            return label;
        }

        private static Button CreateButton(Transform parent, string labelText)
        {
            var go = new GameObject("Button_" + labelText, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.sprite = RoundedRectSprite.Get();
            image.type = Image.Type.Sliced;

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.normalColor = Merge2048Theme.BUTTON_NORMAL_COLOR;
            colors.highlightedColor = Merge2048Theme.BUTTON_HIGHLIGHTED_COLOR;
            colors.pressedColor = Merge2048Theme.BUTTON_PRESSED_COLOR;
            colors.disabledColor = Merge2048Theme.BUTTON_DISABLED_COLOR;
            button.colors = colors;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = BUTTON_HEIGHT;
            layoutElement.minHeight = BUTTON_HEIGHT;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);

            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var tmpLabel = labelGo.AddComponent<TextMeshProUGUI>();
            tmpLabel.text = labelText ?? string.Empty;
            tmpLabel.alignment = TextAlignmentOptions.Center;
            tmpLabel.fontSize = BUTTON_FONT_SIZE;
            tmpLabel.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            tmpLabel.raycastTarget = false;

            return button;
        }
    }
}
