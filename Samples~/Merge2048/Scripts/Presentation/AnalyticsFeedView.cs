using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Merge2048.Integration;

namespace Merge2048.Presentation
{
    // Headline "you can SEE what's sent" overlay — reads AnalyticsFeed (an in-process bus,
    // not PlayScope) and renders it. Owns its own high-sortingOrder Canvas so it floats
    // above ScreenFlow's UI regardless of which screen is showing.
    public sealed class AnalyticsFeedView : MonoBehaviour
    {
        private const int SORTING_ORDER = 1000;
        private const int MAX_VISIBLE = 8;
        private const float DISPLAY_DURATION = 1.4f;
        private const float FADE_DURATION = 0.6f;
        private const float ENTRY_LIFETIME = DISPLAY_DURATION + FADE_DURATION;

        private const float REFERENCE_WIDTH = 1080f;
        private const float REFERENCE_HEIGHT = 1920f;
        private const float REFERENCE_MATCH = 0.5f;

        private const float STACK_WIDTH = 620f;
        private const float STACK_SPACING = 8f;
        private const float ENTRY_FONT_SIZE = 24f;
        private const float ENTRY_PADDING_H = 16f;
        private const float ENTRY_PADDING_V = 8f;
        private const float ENTRY_BACKGROUND_ALPHA = 0.55f;

        private readonly struct FeedEntry
        {
            public readonly GameObject Root;
            public readonly CanvasGroup Group;
            public readonly float SpawnTime;

            public FeedEntry(GameObject root, CanvasGroup group, float spawnTime)
            {
                Root = root;
                Group = group;
                SpawnTime = spawnTime;
            }
        }

        private readonly List<FeedEntry> _activeEntries = new List<FeedEntry>(MAX_VISIBLE);
        private RectTransform _stack;

        private void Awake()
        {
            BuildCanvas();
            BuildStack();
        }

        private void OnEnable()
        {
            AnalyticsFeed.EntryAdded += OnEntryAdded;

            foreach (string message in AnalyticsFeed.Recent)
            {
                AddEntry(message);
            }
        }

        private void OnDisable()
        {
            AnalyticsFeed.EntryAdded -= OnEntryAdded;

            for (int i = 0; i < _activeEntries.Count; i++)
            {
                Destroy(_activeEntries[i].Root);
            }

            _activeEntries.Clear();
        }

        private void Update()
        {
            for (int i = _activeEntries.Count - 1; i >= 0; i--)
            {
                var entry = _activeEntries[i];
                float age = Time.unscaledTime - entry.SpawnTime;

                if (age >= ENTRY_LIFETIME)
                {
                    Destroy(entry.Root);
                    _activeEntries.RemoveAt(i);
                    continue;
                }

                float alpha = age <= DISPLAY_DURATION
                    ? 1f
                    : 1f - ((age - DISPLAY_DURATION) / FADE_DURATION);
                entry.Group.alpha = Mathf.Clamp01(alpha);
            }
        }

        private void OnEntryAdded(string message)
        {
            AddEntry(message);
        }

        private void AddEntry(string message)
        {
            if (_activeEntries.Count >= MAX_VISIBLE)
            {
                Destroy(_activeEntries[0].Root);
                _activeEntries.RemoveAt(0);
            }

            var go = new GameObject("FeedEntry", typeof(RectTransform));
            go.transform.SetParent(_stack, false);
            go.transform.SetAsFirstSibling();

            var background = go.AddComponent<Image>();
            background.sprite = RoundedRectSprite.Get();
            background.type = Image.Type.Sliced;
            background.color = new Color(
                Merge2048Theme.BOARD_BACKGROUND_COLOR.r,
                Merge2048Theme.BOARD_BACKGROUND_COLOR.g,
                Merge2048Theme.BOARD_BACKGROUND_COLOR.b,
                ENTRY_BACKGROUND_ALPHA);
            background.raycastTarget = false;

            var layoutGroup = go.AddComponent<HorizontalLayoutGroup>();
            layoutGroup.padding = new RectOffset((int)ENTRY_PADDING_H, (int)ENTRY_PADDING_H, (int)ENTRY_PADDING_V, (int)ENTRY_PADDING_V);
            layoutGroup.childAlignment = TextAnchor.MiddleRight;
            layoutGroup.childControlWidth = true;
            layoutGroup.childControlHeight = true;
            layoutGroup.childForceExpandWidth = false;
            layoutGroup.childForceExpandHeight = false;

            var contentSizeFitter = go.AddComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = STACK_WIDTH;

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(go.transform, false);

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = message;
            label.fontSize = ENTRY_FONT_SIZE;
            label.alignment = TextAlignmentOptions.MidlineRight;
            label.color = Merge2048Theme.TEXT_ON_DARK_COLOR;
            label.raycastTarget = false;
            label.enableWordWrapping = true;

            var group = go.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = false;
            group.blocksRaycasts = false;

            _activeEntries.Insert(0, new FeedEntry(go, group, Time.unscaledTime));
        }

        private void BuildStack()
        {
            var stackGo = new GameObject("FeedStack", typeof(RectTransform));
            stackGo.transform.SetParent(transform, false);

            var rectTransform = stackGo.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(1f, 1f);
            rectTransform.anchorMax = new Vector2(1f, 1f);
            rectTransform.pivot = new Vector2(1f, 1f);
            rectTransform.anchoredPosition = new Vector2(-24f, -24f);
            rectTransform.sizeDelta = new Vector2(STACK_WIDTH, 0f);

            var verticalLayoutGroup = stackGo.AddComponent<VerticalLayoutGroup>();
            verticalLayoutGroup.childAlignment = TextAnchor.UpperRight;
            verticalLayoutGroup.spacing = STACK_SPACING;
            verticalLayoutGroup.childControlWidth = true;
            verticalLayoutGroup.childControlHeight = true;
            verticalLayoutGroup.childForceExpandWidth = false;
            verticalLayoutGroup.childForceExpandHeight = false;

            var contentSizeFitter = stackGo.AddComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _stack = rectTransform;
        }

        private void BuildCanvas()
        {
            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
            }

            // Parented under ScreenFlow's canvas, this is a NESTED sub-canvas whose
            // RectTransform is not auto-sized to the screen. Stretch it to fill the parent
            // so the corner-anchored feed stack lands in the actual corner, not screen centre.
            var selfRect = (RectTransform)transform;
            selfRect.anchorMin = Vector2.zero;
            selfRect.anchorMax = Vector2.one;
            selfRect.pivot = new Vector2(0.5f, 0.5f);
            selfRect.offsetMin = Vector2.zero;
            selfRect.offsetMax = Vector2.zero;

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

            // No GraphicRaycaster — the feed is purely informational and must never
            // intercept clicks meant for the game UI underneath it.
        }
    }
}
