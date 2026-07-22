using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Merge2048.Core;

namespace Merge2048.Presentation
{
    public sealed class BoardView : MonoBehaviour
    {
        private const float TILE_SPACING = 6f;
        private const float DEFAULT_CELL_SIZE = 140f;

        private RectTransform _cellLayer;
        private RectTransform _tileLayer;
        private float _cellSize;
        private Vector2 _containerSize;

        private readonly Dictionary<(int Row, int Col), TileView> _liveTiles =
            new Dictionary<(int Row, int Col), TileView>();

        public void Initialize()
        {
            var containerRect = transform as RectTransform;
            if (containerRect == null)
            {
                return;
            }

            Canvas.ForceUpdateCanvases();

            int size = Board.SIZE;
            _containerSize = containerRect.rect.size;

            float cellWidth = (_containerSize.x - TILE_SPACING * (size - 1)) / size;
            float cellHeight = (_containerSize.y - TILE_SPACING * (size - 1)) / size;
            float cellSize = Mathf.Min(cellWidth, cellHeight);

            if (cellSize <= 0f)
            {
                cellSize = DEFAULT_CELL_SIZE;
            }

            _cellSize = cellSize;

            _cellLayer = CreateStretchedLayer("CellLayer");
            _tileLayer = CreateStretchedLayer("TileLayer");

            var gridLayoutGroup = _cellLayer.gameObject.AddComponent<GridLayoutGroup>();
            gridLayoutGroup.cellSize = new Vector2(_cellSize, _cellSize);
            gridLayoutGroup.spacing = new Vector2(TILE_SPACING, TILE_SPACING);
            gridLayoutGroup.childAlignment = TextAnchor.MiddleCenter;
            gridLayoutGroup.startCorner = GridLayoutGroup.Corner.UpperLeft;
            gridLayoutGroup.startAxis = GridLayoutGroup.Axis.Horizontal;
            gridLayoutGroup.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayoutGroup.constraintCount = size;

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    BoardCellView.Create(_cellLayer);
                }
            }
        }

        // Instant snapshot render — initial game start, Undo, ad-continue effect. Clears every
        // live TileView and spawns fresh ones for the current board state, no animation.
        public void Render(Board board)
        {
            if (board == null || _tileLayer == null)
            {
                return;
            }

            StopAllCoroutines();
            ClearAllTiles();

            int size = Board.SIZE;

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    int value = board.Get(row, col);
                    if (value <= 0)
                    {
                        continue;
                    }

                    var tileView = TileView.Create(_tileLayer, _cellSize);
                    tileView.SetValue(value);
                    tileView.RectTransform.anchoredPosition = CellAnchoredPosition(row, col);

                    _liveTiles[(row, col)] = tileView;
                }
            }
        }

        // Animated move: slides tiles along result.Movements, pops+relabels merge landings,
        // scales in spawns, then invokes onComplete. finalBoard is used only for a defensive
        // resync pass at the end (BoardView.PlayMove trajectory bookkeeping is otherwise
        // self-sufficient from result alone).
        public void PlayMove(MoveResult result, Board finalBoard, Action onComplete)
        {
            if (!result.Changed || _tileLayer == null)
            {
                onComplete?.Invoke();
                return;
            }

            StopAllCoroutines();
            StartCoroutine(PlayMoveRoutine(result, finalBoard, onComplete));
        }

        private IEnumerator PlayMoveRoutine(MoveResult result, Board finalBoard, Action onComplete)
        {
            yield return StartCoroutine(SlidePhase(result));

            var trailingRoutines = new List<Coroutine>();
            ApplyMergePhase(result, trailingRoutines);
            ApplySpawnPhase(result, trailingRoutines);

            for (int i = 0; i < trailingRoutines.Count; i++)
            {
                yield return trailingRoutines[i];
            }

            ResyncLiveTiles(finalBoard);

            onComplete?.Invoke();
        }

        private IEnumerator SlidePhase(MoveResult result)
        {
            var movements = result.Movements;
            var slideRoutines = new List<Coroutine>();
            var consumedTiles = new List<TileView>();
            var arrivals = new List<(int Row, int Col, TileView Tile)>();

            for (int i = 0; i < movements.Count; i++)
            {
                var movement = movements[i];

                if (!_liveTiles.TryGetValue((movement.FromRow, movement.FromCol), out var tileView) || tileView == null)
                {
                    continue;
                }

                _liveTiles.Remove((movement.FromRow, movement.FromCol));

                var targetPosition = CellAnchoredPosition(movement.ToRow, movement.ToCol);
                slideRoutines.Add(StartCoroutine(SlideTile(tileView, targetPosition)));

                if (movement.ConsumedByMerge)
                {
                    consumedTiles.Add(tileView);
                }
                else
                {
                    arrivals.Add((movement.ToRow, movement.ToCol, tileView));
                }
            }

            for (int i = 0; i < slideRoutines.Count; i++)
            {
                yield return slideRoutines[i];
            }

            for (int i = 0; i < consumedTiles.Count; i++)
            {
                if (consumedTiles[i] != null)
                {
                    Destroy(consumedTiles[i].gameObject);
                }
            }

            for (int i = 0; i < arrivals.Count; i++)
            {
                var arrival = arrivals[i];
                _liveTiles[(arrival.Row, arrival.Col)] = arrival.Tile;
            }
        }

        private static IEnumerator SlideTile(TileView tileView, Vector2 targetPosition)
        {
            if (tileView == null || tileView.RectTransform == null)
            {
                yield break;
            }

            var rectTransform = tileView.RectTransform;
            var startPosition = rectTransform.anchoredPosition;
            float elapsed = 0f;

            while (elapsed < Merge2048Theme.TILE_SLIDE_DURATION)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / Merge2048Theme.TILE_SLIDE_DURATION);
                rectTransform.anchoredPosition = Vector2.Lerp(startPosition, targetPosition, t);
                yield return null;
            }

            rectTransform.anchoredPosition = targetPosition;
        }

        private void ApplyMergePhase(MoveResult result, List<Coroutine> trailingRoutines)
        {
            var merges = result.Merges;

            for (int i = 0; i < merges.Count; i++)
            {
                var mergeEvent = merges[i];
                var cellPosition = CellAnchoredPosition(mergeEvent.Row, mergeEvent.Col);

                if (_liveTiles.TryGetValue((mergeEvent.Row, mergeEvent.Col), out var tileView) && tileView != null)
                {
                    tileView.SetValue(mergeEvent.ResultValue);
                    trailingRoutines.Add(StartCoroutine(PopScale(tileView)));
                }

                ScorePopup.Spawn(_tileLayer, cellPosition, mergeEvent.ResultValue, _cellSize);
            }
        }

        private static IEnumerator PopScale(TileView tileView)
        {
            if (tileView == null || tileView.RectTransform == null)
            {
                yield break;
            }

            var rectTransform = tileView.RectTransform;
            float half = Merge2048Theme.MERGE_POP_DURATION / 2f;

            yield return LerpScale(rectTransform, 1f, Merge2048Theme.MERGE_POP_SCALE, half);
            yield return LerpScale(rectTransform, Merge2048Theme.MERGE_POP_SCALE, 1f, half);
        }

        private void ApplySpawnPhase(MoveResult result, List<Coroutine> trailingRoutines)
        {
            var spawns = result.Spawns;

            for (int i = 0; i < spawns.Count; i++)
            {
                var spawnEvent = spawns[i];

                var tileView = TileView.Create(_tileLayer, _cellSize);
                tileView.SetValue(spawnEvent.Value);
                tileView.RectTransform.anchoredPosition = CellAnchoredPosition(spawnEvent.Row, spawnEvent.Col);
                tileView.RectTransform.localScale = Vector3.zero;

                _liveTiles[(spawnEvent.Row, spawnEvent.Col)] = tileView;

                trailingRoutines.Add(StartCoroutine(SpawnScaleIn(tileView)));
            }
        }

        private static IEnumerator SpawnScaleIn(TileView tileView)
        {
            if (tileView == null || tileView.RectTransform == null)
            {
                yield break;
            }

            yield return LerpScale(tileView.RectTransform, 0f, 1f, Merge2048Theme.SPAWN_SCALE_DURATION);
        }

        private static IEnumerator LerpScale(
            RectTransform rectTransform,
            float fromScale,
            float toScale,
            float duration)
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = duration > 0f ? Mathf.Clamp01(elapsed / duration) : 1f;
                float scale = Mathf.Lerp(fromScale, toScale, t);
                rectTransform.localScale = new Vector3(scale, scale, 1f);
                yield return null;
            }

            rectTransform.localScale = new Vector3(toScale, toScale, 1f);
        }

        // Defensive safety net (not the primary bookkeeping path — SlidePhase/ApplyMergePhase/
        // ApplySpawnPhase already keep _liveTiles accurate incrementally as the move plays out).
        // Reconciles against the authoritative board so any edge-case desync self-heals instead
        // of silently drifting.
        private void ResyncLiveTiles(Board board)
        {
            if (board == null || _tileLayer == null)
            {
                return;
            }

            int size = Board.SIZE;

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    int value = board.Get(row, col);
                    _liveTiles.TryGetValue((row, col), out var tileView);

                    if (value <= 0)
                    {
                        if (tileView != null)
                        {
                            Destroy(tileView.gameObject);
                            _liveTiles.Remove((row, col));
                        }

                        continue;
                    }

                    if (tileView == null)
                    {
                        tileView = TileView.Create(_tileLayer, _cellSize);
                        tileView.RectTransform.anchoredPosition = CellAnchoredPosition(row, col);
                        _liveTiles[(row, col)] = tileView;
                    }

                    if (tileView.Value != value)
                    {
                        tileView.SetValue(value);
                    }
                }
            }
        }

        // Destroys every child of _tileLayer directly rather than only what _liveTiles
        // currently tracks — a StopAllCoroutines() interruption mid-PlayMove (e.g. Undo
        // tapped while a slide is still animating) can leave a tile removed from
        // _liveTiles (SlidePhase pops the source-cell entry synchronously before the
        // slide coroutine even starts) but not yet destroyed/re-added; walking the
        // dictionary alone would leak that GameObject as an orphaned ghost tile.
        private void ClearAllTiles()
        {
            for (int i = _tileLayer.childCount - 1; i >= 0; i--)
            {
                Destroy(_tileLayer.GetChild(i).gameObject);
            }

            _liveTiles.Clear();
        }

        private RectTransform CreateStretchedLayer(string layerName)
        {
            var go = new GameObject(layerName, typeof(RectTransform));
            go.transform.SetParent(transform, false);

            var rectTransform = go.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            return rectTransform;
        }

        // Top-left-corner-origin grid formula matching GridLayoutGroup's UpperLeft-corner /
        // Horizontal-axis / FixedColumnCount / MiddleCenter-alignment contract, so the free
        // TileLayer aligns pixel-for-pixel with CellLayer's GridLayoutGroup-placed cells.
        private Vector2 CellAnchoredPosition(int row, int col)
        {
            int size = Board.SIZE;
            float totalGridSize = (_cellSize * size) + (TILE_SPACING * (size - 1));
            float startOffsetX = (_containerSize.x - totalGridSize) / 2f;
            float startOffsetY = (_containerSize.y - totalGridSize) / 2f;

            float x = startOffsetX + (col * (_cellSize + TILE_SPACING)) + (_cellSize / 2f);
            float y = -(startOffsetY + (row * (_cellSize + TILE_SPACING)) + (_cellSize / 2f));

            return new Vector2(x, y);
        }
    }
}
