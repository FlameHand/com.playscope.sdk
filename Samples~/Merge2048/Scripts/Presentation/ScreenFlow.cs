using System;
using System.Collections;
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

        private const float BUTTON_HEIGHT = 140f;
        private const float BUTTON_WIDTH = 700f;
        private const float STACK_WIDTH = 800f;
        private const float CONTENT_SPACING = 24f;
        private const float HUD_ROW_HEIGHT = 180f;
        private const float HUD_CARD_WIDTH = 220f;
        private const float HUD_SHOP_BUTTON_WIDTH = 160f;
        private const float HUD_BADGE_SIZE = 48f;
        private const float HUD_BADGE_MARGIN = 6f;

        private GameObject _safeAreaRoot;
        private GameObject _mainMenuPanel;
        private GameObject _difficultySelectPanel;
        private GameObject _gameplayPanel;
        private GameObject _gameOverPanel;
        private GameObject _shopPanel;

        private GameObject _visiblePanel;
        private GameObject _fadeOutPanel;
        private Coroutine _fadeCoroutine;

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
        public TMP_Text BestValueText { get; private set; }
        public TMP_Text UndoChargeText { get; private set; }
        public Button UndoButton { get; private set; }
        public TMP_Text GameOverFinalScoreText { get; private set; }
        public TMP_Text GameOverHighestTileText { get; private set; }
        public Button RemoveAdsButton { get; private set; }

        private void Awake()
        {
            EnsureEventSystem();
            BuildCanvas();
            BuildBackground();
            BuildSafeAreaRoot();
            BuildMainMenuPanel();
            BuildDifficultySelectPanel();
            BuildGameplayPanel();
            BuildGameOverPanel();
            BuildShopPanel();

            Show(ScreenId.MainMenu);
        }

        public void Show(ScreenId screen)
        {
            var targetPanel = PanelFor(screen);

            Current = screen;
            ScreenChanged?.Invoke(screen);

            if (targetPanel == _visiblePanel)
            {
                return;
            }

            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
                SnapHidden(_fadeOutPanel);
            }

            _fadeOutPanel = _visiblePanel;
            _visiblePanel = targetPanel;

            // Activate synchronously (at alpha 0) rather than at the end of the fade-out —
            // callers like MergeGameController.StartNewGame() read BoardContainer's
            // RectTransform size on the same frame right after Show(), which only works
            // if the panel is already active-in-hierarchy for layout to compute.
            if (targetPanel != null)
            {
                targetPanel.SetActive(true);
            }

            _fadeCoroutine = StartCoroutine(FadeToPanel(_fadeOutPanel, targetPanel));
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

        private GameObject PanelFor(ScreenId screen)
        {
            switch (screen)
            {
                case ScreenId.MainMenu:
                {
                    return _mainMenuPanel;
                }
                case ScreenId.DifficultySelect:
                {
                    return _difficultySelectPanel;
                }
                case ScreenId.Gameplay:
                {
                    return _gameplayPanel;
                }
                case ScreenId.GameOver:
                {
                    return _gameOverPanel;
                }
                case ScreenId.Shop:
                {
                    return _shopPanel;
                }
                case ScreenId.Unknown:
                default:
                {
                    return null;
                }
            }
        }

        // toPanel is already SetActive(true) by Show() (at alpha 0, non-interactable) so its
        // layout is measurable this frame. Both panels fade concurrently (true crossfade) —
        // a sequential fade-out-then-fade-in left a visible blank-background flash between
        // screens.
        //
        // Game Over is a special case (scoped to this one transition, not a general overlay
        // system): the frozen Gameplay board must stay visible underneath GameOverPanel's dark
        // scrim, so the outgoing panel is left active at full alpha instead of faded out and
        // deactivated like every other transition.
        private IEnumerator FadeToPanel(GameObject fromPanel, GameObject toPanel)
        {
            bool enteringGameOver = Current == ScreenId.GameOver;

            var toGroup = toPanel != null ? toPanel.GetComponent<CanvasGroup>() : null;
            var fromGroup = fromPanel != null ? fromPanel.GetComponent<CanvasGroup>() : null;

            if (toGroup != null)
            {
                toGroup.interactable = false;
                toGroup.blocksRaycasts = false;
            }

            if (fromGroup != null)
            {
                fromGroup.interactable = false;
                fromGroup.blocksRaycasts = false;
            }

            float fromStart = fromGroup != null ? fromGroup.alpha : 0f;
            float toStart = toGroup != null ? toGroup.alpha : 0f;
            float elapsed = 0f;

            while (elapsed < Merge2048Theme.FADE_DURATION)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / Merge2048Theme.FADE_DURATION);

                if (fromGroup != null && !enteringGameOver)
                {
                    fromGroup.alpha = Mathf.Lerp(fromStart, 0f, t);
                }

                if (toGroup != null)
                {
                    toGroup.alpha = Mathf.Lerp(toStart, 1f, t);
                }

                yield return null;
            }

            if (fromGroup != null && !enteringGameOver)
            {
                fromGroup.alpha = 0f;
            }

            if (fromPanel != null && !enteringGameOver)
            {
                fromPanel.SetActive(false);
            }

            if (toGroup != null)
            {
                toGroup.alpha = 1f;
                toGroup.interactable = true;
                toGroup.blocksRaycasts = true;
            }

            _fadeCoroutine = null;
        }

        private static void SnapHidden(GameObject panel)
        {
            if (panel == null)
            {
                return;
            }

            var canvasGroup = panel.GetComponent<CanvasGroup>();
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.interactable = false;
                canvasGroup.blocksRaycasts = false;
            }

            panel.SetActive(false);
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

        private void BuildSafeAreaRoot()
        {
            var go = new GameObject("SafeAreaRoot", typeof(RectTransform));
            go.transform.SetParent(transform, false);

            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            go.AddComponent<SafeAreaFitter>();

            _safeAreaRoot = go;
        }

        private GameObject CreatePanel(string panelName)
        {
            var go = new GameObject(panelName, typeof(RectTransform));
            go.transform.SetParent(_safeAreaRoot.transform, false);

            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                rectTransform.anchorMin = Vector2.zero;
                rectTransform.anchorMax = Vector2.one;
                rectTransform.offsetMin = Vector2.zero;
                rectTransform.offsetMax = Vector2.zero;
            }

            var canvasGroup = go.AddComponent<CanvasGroup>();
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            go.SetActive(false);

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
            label.color = Merge2048Theme.TEXT_ON_LIGHT_COLOR;

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
            tmpLabel.fontSize = Merge2048Theme.BUTTON_FONT_SIZE;
            tmpLabel.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            tmpLabel.raycastTarget = false;

            return button;
        }

        private static TMP_Text CreateHudCard(Transform parent, string caption, float width)
        {
            var go = new GameObject("Card_" + caption, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.sprite = RoundedRectSprite.Get();
            image.type = Image.Type.Sliced;
            image.color = Merge2048Theme.BOARD_BACKGROUND_COLOR;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = width;
            layoutElement.flexibleHeight = 1f;

            var verticalLayoutGroup = go.AddComponent<VerticalLayoutGroup>();
            verticalLayoutGroup.childAlignment = TextAnchor.MiddleCenter;
            verticalLayoutGroup.spacing = 4f;
            verticalLayoutGroup.padding = new RectOffset(12, 12, 8, 8);
            verticalLayoutGroup.childControlWidth = true;
            verticalLayoutGroup.childControlHeight = true;
            verticalLayoutGroup.childForceExpandWidth = true;
            verticalLayoutGroup.childForceExpandHeight = false;

            var captionLabel = CreateLabel(go.transform, caption, Merge2048Theme.HUD_CAPTION_FONT_SIZE);
            captionLabel.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            captionLabel.fontStyle = FontStyles.Bold;

            var valueLabel = CreateLabel(go.transform, "0", Merge2048Theme.HUD_FONT_SIZE);
            valueLabel.color = Merge2048Theme.TEXT_ON_DARK_COLOR;

            return valueLabel;
        }

        private static TMP_Text CreateUndoBadge(Transform parent)
        {
            var go = new GameObject("UndoBadge", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(1f, 1f);
            rectTransform.anchorMax = new Vector2(1f, 1f);
            rectTransform.pivot = new Vector2(1f, 1f);
            rectTransform.anchoredPosition = new Vector2(-HUD_BADGE_MARGIN, -HUD_BADGE_MARGIN);
            rectTransform.sizeDelta = new Vector2(HUD_BADGE_SIZE, HUD_BADGE_SIZE);

            var image = go.AddComponent<Image>();
            image.sprite = RoundedRectSprite.Get();
            image.type = Image.Type.Sliced;
            image.color = Merge2048Theme.BUTTON_PRESSED_COLOR;
            image.raycastTarget = false;

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = "0";
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = Merge2048Theme.HUD_BADGE_FONT_SIZE;
            label.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            label.raycastTarget = false;

            return label;
        }

        private void BuildMainMenuPanel()
        {
            _mainMenuPanel = CreatePanel("MainMenuPanel");
            var stack = CreateButtonStack(_mainMenuPanel.transform);

            CreateLabel(stack, "2048 Merge", Merge2048Theme.TITLE_FONT_SIZE);

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

            CreateLabel(stack, "Select Difficulty", Merge2048Theme.TITLE_FONT_SIZE);

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

            var boardBackgroundImage = boardContainerGo.AddComponent<Image>();
            boardBackgroundImage.sprite = RoundedRectSprite.Get();
            boardBackgroundImage.type = Image.Type.Sliced;
            boardBackgroundImage.color = Merge2048Theme.BOARD_BACKGROUND_COLOR;

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

            ScoreValueText = CreateHudCard(go.transform, "SCORE", HUD_CARD_WIDTH);
            BestValueText = CreateHudCard(go.transform, "BEST", HUD_CARD_WIDTH);

            var undoButton = CreateButton(go.transform, "Undo", HUD_CARD_WIDTH);
            UndoButton = undoButton;
            undoButton.onClick.AddListener(() => UndoClicked?.Invoke());

            UndoChargeText = CreateUndoBadge(undoButton.transform);

            var shopButton = CreateButton(go.transform, "Shop", HUD_SHOP_BUTTON_WIDTH);
            shopButton.onClick.AddListener(() => OpenShopClicked?.Invoke());
        }

        private void BuildGameOverPanel()
        {
            _gameOverPanel = CreatePanel("GameOverPanel");

            // Semi-transparent dark scrim, not an opaque page background — GameplayPanel stays
            // active underneath (see FadeToPanel's enteringGameOver branch) and must read through.
            // Built after BuildGameplayPanel() in Awake(), so it's already the later sibling of
            // Gameplay and renders on top of it.
            var scrimImage = _gameOverPanel.AddComponent<Image>();
            scrimImage.color = Merge2048Theme.GAME_OVER_SCRIM_COLOR;

            var stack = CreateButtonStack(_gameOverPanel.transform);

            CreateLabel(stack, "Game Over", Merge2048Theme.TITLE_FONT_SIZE);

            GameOverFinalScoreText = CreateLabel(stack, "Score: 0", Merge2048Theme.HUD_FONT_SIZE);
            GameOverHighestTileText = CreateLabel(stack, "Highest tile: 0", Merge2048Theme.HUD_FONT_SIZE);

            var continueButton = CreateButton(stack, "Watch Ad to Continue");
            continueButton.onClick.AddListener(() => ContinueWithAdClicked?.Invoke());

            var restartButton = CreateButton(stack, "Restart");
            restartButton.onClick.AddListener(() => RestartClicked?.Invoke());
        }

        private void BuildShopPanel()
        {
            _shopPanel = CreatePanel("ShopPanel");
            var stack = CreateButtonStack(_shopPanel.transform);

            CreateLabel(stack, "Shop", Merge2048Theme.TITLE_FONT_SIZE);

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
