using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using TMPro;
using Merge2048.Core;

namespace Merge2048.Presentation
{
    public enum ScreenId
    {
        Unknown = 0,
        MainMenu = 1,
        DifficultySelect = 2,
        Gameplay = 3,
        GameOver = 4,
        Shop = 5,
    }

    public sealed class ScreenFlow : MonoBehaviour
    {
        private const float REFERENCE_WIDTH = 1080f;
        private const float REFERENCE_HEIGHT = 1920f;
        private const float REFERENCE_MATCH = 0.5f;

        private const float TITLE_FONT_SIZE = 72f;
        private const float HUD_FONT_SIZE = 42f;
        private const float BUTTON_FONT_SIZE = 40f;

        private const float BUTTON_HEIGHT = 140f;
        private const float BUTTON_WIDTH = 700f;
        private const float STACK_WIDTH = 800f;
        private const float CONTENT_SPACING = 24f;
        private const float HUD_ROW_HEIGHT = 180f;
        private const float HUD_LABEL_WIDTH = 220f;
        private const float HUD_UNDO_CHARGE_WIDTH = 100f;

        private static readonly Color PANEL_LABEL_COLOR = new Color32(0xF2, 0xF2, 0xF2, 0xFF);
        private static readonly Color BUTTON_BACKGROUND_COLOR = new Color32(0x3A, 0x5F, 0x8A, 0xFF);
        private static readonly Color BUTTON_TEXT_COLOR = new Color32(0xFF, 0xFF, 0xFF, 0xFF);

        private GameObject _mainMenuPanel;
        private GameObject _difficultySelectPanel;
        private GameObject _gameplayPanel;
        private GameObject _gameOverPanel;
        private GameObject _shopPanel;

        public ScreenId Current { get; private set; }

        public event Action<ScreenId> ScreenChanged;
        public event Action PlayClicked;
        public event Action<Difficulty> DifficultySelected;
        public event Action UndoClicked;
        public event Action ContinueWithAdClicked;
        public event Action RestartClicked;
        public event Action OpenShopClicked;
        public event Action CloseShopClicked;
        public event Action BuyUndoPackClicked;
        public event Action RemoveAdsClicked;

        public RectTransform BoardContainer { get; private set; }
        public TMP_Text ScoreValueText { get; private set; }
        public TMP_Text HighestTileValueText { get; private set; }
        public TMP_Text UndoChargeText { get; private set; }
        public Button UndoButton { get; private set; }
        public TMP_Text GameOverFinalScoreText { get; private set; }
        public TMP_Text GameOverHighestTileText { get; private set; }
        public Button RemoveAdsButton { get; private set; }

        private void Awake()
        {
            EnsureEventSystem();
            BuildCanvas();
            BuildMainMenuPanel();
            BuildDifficultySelectPanel();
            BuildGameplayPanel();
            BuildGameOverPanel();
            BuildShopPanel();

            Show(ScreenId.MainMenu);
        }

        public void Show(ScreenId screen)
        {
            SetPanelActive(_mainMenuPanel, screen == ScreenId.MainMenu);
            SetPanelActive(_difficultySelectPanel, screen == ScreenId.DifficultySelect);
            SetPanelActive(_gameplayPanel, screen == ScreenId.Gameplay);
            SetPanelActive(_gameOverPanel, screen == ScreenId.GameOver);
            SetPanelActive(_shopPanel, screen == ScreenId.Shop);

            Current = screen;
            ScreenChanged?.Invoke(screen);
        }

        public static string ScreenName(ScreenId screen)
        {
            switch (screen)
            {
                case ScreenId.MainMenu:
                {
                    return "MainMenu";
                }
                case ScreenId.DifficultySelect:
                {
                    return "DifficultySelect";
                }
                case ScreenId.Gameplay:
                {
                    return "Gameplay";
                }
                case ScreenId.GameOver:
                {
                    return "GameOver";
                }
                case ScreenId.Shop:
                {
                    return "Shop";
                }
                case ScreenId.Unknown:
                default:
                {
                    return "";
                }
            }
        }

        private static void SetPanelActive(GameObject panel, bool active)
        {
            if (panel != null)
            {
                panel.SetActive(active);
            }
        }

        // Built at runtime rather than hand-authored in the scene — the new Input
        // System's UI raycasting needs InputSystemUIInputModule specifically
        // (the legacy StandaloneInputModule silently drops clicks with no
        // Input Manager backend enabled, which this project doesn't have).
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

        private GameObject CreatePanel(string panelName)
        {
            var go = new GameObject(panelName, typeof(RectTransform));
            go.transform.SetParent(transform, false);

            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                rectTransform.anchorMin = Vector2.zero;
                rectTransform.anchorMax = Vector2.one;
                rectTransform.offsetMin = Vector2.zero;
                rectTransform.offsetMax = Vector2.zero;
            }

            return go;
        }

        private static RectTransform CreateButtonStack(Transform parent)
        {
            var go = new GameObject("ButtonStack", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.sizeDelta = new Vector2(STACK_WIDTH, 0f);

            var verticalLayoutGroup = go.AddComponent<VerticalLayoutGroup>();
            verticalLayoutGroup.childAlignment = TextAnchor.MiddleCenter;
            verticalLayoutGroup.spacing = CONTENT_SPACING;
            verticalLayoutGroup.childControlWidth = true;
            verticalLayoutGroup.childControlHeight = true;
            verticalLayoutGroup.childForceExpandWidth = true;
            verticalLayoutGroup.childForceExpandHeight = false;

            var contentSizeFitter = go.AddComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return rectTransform;
        }

        private static TMP_Text CreateLabel(Transform parent, string text, float fontSize, float preferredWidth = -1f)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text ?? string.Empty;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = PANEL_LABEL_COLOR;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = fontSize * 1.5f;
            if (preferredWidth > 0f)
            {
                layoutElement.preferredWidth = preferredWidth;
            }

            return label;
        }

        private static Button CreateButton(Transform parent, string labelText, float preferredWidth = BUTTON_WIDTH)
        {
            var go = new GameObject("Button_" + labelText, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = BUTTON_BACKGROUND_COLOR;

            var button = go.AddComponent<Button>();

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = preferredWidth;
            layoutElement.preferredHeight = BUTTON_HEIGHT;
            layoutElement.minHeight = BUTTON_HEIGHT;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);

            var labelRect = labelGo.GetComponent<RectTransform>();
            if (labelRect != null)
            {
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
            }

            var tmpLabel = labelGo.AddComponent<TextMeshProUGUI>();
            tmpLabel.text = labelText ?? string.Empty;
            tmpLabel.alignment = TextAlignmentOptions.Center;
            tmpLabel.fontSize = BUTTON_FONT_SIZE;
            tmpLabel.color = BUTTON_TEXT_COLOR;

            return button;
        }

        private void BuildMainMenuPanel()
        {
            _mainMenuPanel = CreatePanel("MainMenuPanel");
            var stack = CreateButtonStack(_mainMenuPanel.transform);

            CreateLabel(stack, "2048 Merge", TITLE_FONT_SIZE);

            var playButton = CreateButton(stack, "Play");
            playButton.onClick.AddListener(OnPlayButtonClicked);
        }

        private void OnPlayButtonClicked()
        {
            PlayClicked?.Invoke();
            Show(ScreenId.DifficultySelect);
        }

        private void BuildDifficultySelectPanel()
        {
            _difficultySelectPanel = CreatePanel("DifficultySelectPanel");
            var stack = CreateButtonStack(_difficultySelectPanel.transform);

            CreateLabel(stack, "Select Difficulty", TITLE_FONT_SIZE);

            var easyButton = CreateButton(stack, "Easy");
            easyButton.onClick.AddListener(() => DifficultySelected?.Invoke(Difficulty.Easy));

            var mediumButton = CreateButton(stack, "Medium");
            mediumButton.onClick.AddListener(() => DifficultySelected?.Invoke(Difficulty.Medium));

            var hardButton = CreateButton(stack, "Hard");
            hardButton.onClick.AddListener(() => DifficultySelected?.Invoke(Difficulty.Hard));
        }

        private void BuildGameplayPanel()
        {
            _gameplayPanel = CreatePanel("GameplayPanel");

            var rootLayout = _gameplayPanel.AddComponent<VerticalLayoutGroup>();
            rootLayout.childAlignment = TextAnchor.UpperCenter;
            rootLayout.childControlHeight = true;
            rootLayout.childControlWidth = true;
            rootLayout.childForceExpandHeight = true;
            rootLayout.childForceExpandWidth = true;
            rootLayout.spacing = 0f;

            CreateHudRow(_gameplayPanel.transform);

            var boardContainerGo = new GameObject("BoardContainer", typeof(RectTransform));
            boardContainerGo.transform.SetParent(_gameplayPanel.transform, false);

            var boardLayoutElement = boardContainerGo.AddComponent<LayoutElement>();
            boardLayoutElement.flexibleHeight = 1f;
            boardLayoutElement.flexibleWidth = 1f;

            BoardContainer = boardContainerGo.GetComponent<RectTransform>();
        }

        private void CreateHudRow(Transform parent)
        {
            var go = new GameObject("HudRow", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = HUD_ROW_HEIGHT;
            layoutElement.minHeight = HUD_ROW_HEIGHT;
            layoutElement.flexibleHeight = 0f;

            var horizontalLayoutGroup = go.AddComponent<HorizontalLayoutGroup>();
            horizontalLayoutGroup.childAlignment = TextAnchor.MiddleLeft;
            horizontalLayoutGroup.spacing = CONTENT_SPACING;
            horizontalLayoutGroup.padding = new RectOffset(20, 20, 20, 20);
            horizontalLayoutGroup.childControlHeight = true;
            horizontalLayoutGroup.childControlWidth = true;
            horizontalLayoutGroup.childForceExpandHeight = true;
            horizontalLayoutGroup.childForceExpandWidth = false;

            ScoreValueText = CreateLabel(go.transform, "0", HUD_FONT_SIZE, HUD_LABEL_WIDTH);
            HighestTileValueText = CreateLabel(go.transform, "0", HUD_FONT_SIZE, HUD_LABEL_WIDTH);

            var undoButton = CreateButton(go.transform, "Undo", HUD_LABEL_WIDTH);
            UndoButton = undoButton;
            undoButton.onClick.AddListener(() => UndoClicked?.Invoke());

            UndoChargeText = CreateLabel(go.transform, "0", HUD_FONT_SIZE, HUD_UNDO_CHARGE_WIDTH);

            var shopButton = CreateButton(go.transform, "Shop", HUD_LABEL_WIDTH);
            shopButton.onClick.AddListener(() => OpenShopClicked?.Invoke());
        }

        private void BuildGameOverPanel()
        {
            _gameOverPanel = CreatePanel("GameOverPanel");
            var stack = CreateButtonStack(_gameOverPanel.transform);

            CreateLabel(stack, "Game Over", TITLE_FONT_SIZE);

            GameOverFinalScoreText = CreateLabel(stack, "Score: 0", HUD_FONT_SIZE);
            GameOverHighestTileText = CreateLabel(stack, "Highest tile: 0", HUD_FONT_SIZE);

            var continueButton = CreateButton(stack, "Watch Ad to Continue");
            continueButton.onClick.AddListener(() => ContinueWithAdClicked?.Invoke());

            var restartButton = CreateButton(stack, "Restart");
            restartButton.onClick.AddListener(() => RestartClicked?.Invoke());
        }

        private void BuildShopPanel()
        {
            _shopPanel = CreatePanel("ShopPanel");
            var stack = CreateButtonStack(_shopPanel.transform);

            CreateLabel(stack, "Shop", TITLE_FONT_SIZE);

            var buyUndoButton = CreateButton(stack, "Buy Undo Pack");
            buyUndoButton.onClick.AddListener(() => BuyUndoPackClicked?.Invoke());

            var removeAdsButton = CreateButton(stack, "Remove Ads");
            RemoveAdsButton = removeAdsButton;
            removeAdsButton.onClick.AddListener(() => RemoveAdsClicked?.Invoke());

            var closeButton = CreateButton(stack, "Close");
            closeButton.onClick.AddListener(() => CloseShopClicked?.Invoke());
        }
    }
}
