using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using TMPro;

namespace Merge2048.Presentation
{
    // Built procedurally at runtime, same pattern as BootScreenView — owns its own Canvas
    // so it can render before ScreenFlow (and its EventSystem) exist in the Game scene.
    // Presentation-only: raises DecisionMade and never calls PlayScope itself — the caller
    // (Integration/PlayScopeBootstrapper) decides what the answer means.
    public sealed class ConsentDialogView : MonoBehaviour
    {
        private const float REFERENCE_WIDTH = 1080f;
        private const float REFERENCE_HEIGHT = 1920f;
        private const float REFERENCE_MATCH = 0.5f;

        private const float STACK_WIDTH = 800f;
        private const float STACK_SPACING = 32f;
        private const float BUTTON_HEIGHT = 140f;
        private const float BUTTON_WIDTH = 700f;
        private const float TITLE_FONT_SIZE = 56f;
        private const float BODY_FONT_SIZE = 32f;

        private const string TITLE_TEXT = "Help improve the game?";
        private const string BODY_TEXT = "Send anonymous telemetry to help us fix bugs and " +
            "balance difficulty? You can change this later from the Diagnostics panel.";

        public event Action<bool> DecisionMade;

        private void Awake()
        {
            EnsureEventSystem();
            BuildCanvas();
            BuildBackground();
            BuildContent();
        }

        // Copied from ScreenFlow.EnsureEventSystem() — the new Input System's UI raycasting
        // needs InputSystemUIInputModule specifically, and ScreenFlow (which normally owns
        // this) hasn't been created yet since the Game scene isn't loaded during consent.
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
            var stackGo = new GameObject("ConsentStack", typeof(RectTransform));
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

            CreateLabel(stackGo.transform, TITLE_TEXT, TITLE_FONT_SIZE);

            CreateLabel(stackGo.transform, BODY_TEXT, BODY_FONT_SIZE);

            var acceptButton = CreateButton(stackGo.transform, "Accept");
            acceptButton.onClick.AddListener(() => DecisionMade?.Invoke(true));

            var declineButton = CreateButton(stackGo.transform, "Decline");
            declineButton.onClick.AddListener(() => DecisionMade?.Invoke(false));
        }

        private static TMP_Text CreateLabel(Transform parent, string text, float fontSize)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text ?? string.Empty;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Merge2048Theme.TEXT_ON_LIGHT_COLOR;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = fontSize * 2.5f;
            layoutElement.preferredWidth = STACK_WIDTH;

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
            layoutElement.preferredWidth = BUTTON_WIDTH;
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
            tmpLabel.fontSize = Merge2048Theme.BUTTON_FONT_SIZE;
            tmpLabel.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            tmpLabel.raycastTarget = false;

            return button;
        }
    }
}
