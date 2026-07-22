using System;
using System.Threading;
using UnityEngine;
using Merge2048.Core;
using Merge2048.Presentation;

namespace Merge2048.App
{
    public sealed class MergeGameController : MonoBehaviour
    {
        public MergeGameModel Model { get; private set; }
        public ScreenFlow ScreenFlow { get; private set; }
        public MonetizationFlows MonetizationFlows { get; private set; }
        public Difficulty SelectedDifficulty { get; private set; }
        public int UndoCharges { get; private set; }
        public bool AdsRemoved { get; private set; }

        public event Action<bool> UndoAttempted;
        public event Action<string> RestartRequested;
        public event Action<Direction> DirectionInput;

        private InputReader _inputReader;
        private BoardView _boardView;
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private CancellationToken _lifetimeToken;

        private bool _isAnimating;
        private bool _pendingGameOver;
        private bool _hasUndoSnapshot;
        private int[,] _undoSnapshotCells;
        private int _undoSnapshotScore;
        private int _undoSnapshotMoveCount;
        private int _undoSnapshotHighestTile;
        private int _bestScore;

        private void Awake()
        {
            _lifetimeToken = _lifetimeCts.Token;

            ScreenFlow = GetComponent<ScreenFlow>() ?? gameObject.AddComponent<ScreenFlow>();
            _inputReader = gameObject.AddComponent<InputReader>();
            MonetizationFlows = gameObject.AddComponent<MonetizationFlows>();

            ScreenFlow.PlayClicked += OnPlayClicked;
            ScreenFlow.DifficultySelected += OnDifficultySelected;
            ScreenFlow.UndoClicked += OnUndoClicked;
            ScreenFlow.ContinueWithAdClicked += OnContinueWithAdClicked;
            ScreenFlow.RestartClicked += OnRestartClicked;
            ScreenFlow.OpenShopClicked += OnOpenShopClicked;
            ScreenFlow.CloseShopClicked += OnCloseShopClicked;
            ScreenFlow.BuyUndoPackClicked += OnBuyUndoPackClicked;
            ScreenFlow.RemoveAdsClicked += OnRemoveAdsClicked;

            _inputReader.DirectionPerformed += OnDirectionPerformed;

            UpdateUndoHud();
            RefreshBestHud();
        }

        private void OnDestroy()
        {
            if (ScreenFlow != null)
            {
                ScreenFlow.PlayClicked -= OnPlayClicked;
                ScreenFlow.DifficultySelected -= OnDifficultySelected;
                ScreenFlow.UndoClicked -= OnUndoClicked;
                ScreenFlow.ContinueWithAdClicked -= OnContinueWithAdClicked;
                ScreenFlow.RestartClicked -= OnRestartClicked;
                ScreenFlow.OpenShopClicked -= OnOpenShopClicked;
                ScreenFlow.CloseShopClicked -= OnCloseShopClicked;
                ScreenFlow.BuyUndoPackClicked -= OnBuyUndoPackClicked;
                ScreenFlow.RemoveAdsClicked -= OnRemoveAdsClicked;
            }

            if (_inputReader != null)
            {
                _inputReader.DirectionPerformed -= OnDirectionPerformed;
            }

            UnsubscribeFromModel();

            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        }

        private void OnPlayClicked()
        {
        }

        private void OnOpenShopClicked()
        {
            if (ScreenFlow != null)
            {
                ScreenFlow.Show(ScreenId.Shop);
            }
        }

        private void OnCloseShopClicked()
        {
            if (ScreenFlow != null)
            {
                ScreenFlow.Show(ScreenId.Gameplay);
            }
        }

        private void OnDifficultySelected(Difficulty difficulty)
        {
            SelectedDifficulty = difficulty;

            // Gameplay panel must be active BEFORE BoardView.Initialize() reads its
            // RectTransform size — an inactive panel's layout is never rebuilt, so
            // the grid container would still measure 0x0 at init time otherwise.
            if (ScreenFlow != null)
            {
                ScreenFlow.Show(ScreenId.Gameplay);
            }

            StartNewGame(difficulty);
        }

        private void OnRestartClicked()
        {
            if (ScreenFlow != null)
            {
                ScreenFlow.Show(ScreenId.Gameplay);
            }

            StartNewGame(SelectedDifficulty);

            RestartRequested?.Invoke("defeat_restart");
        }

        private void StartNewGame(Difficulty difficulty)
        {
            UnsubscribeFromModel();

            Model = new MergeGameModel(difficulty, new System.Random());

            if (_boardView == null && ScreenFlow != null && ScreenFlow.BoardContainer != null)
            {
                _boardView = ScreenFlow.BoardContainer.gameObject.AddComponent<BoardView>();
                _boardView.Initialize();
            }

            _hasUndoSnapshot = false;
            _undoSnapshotCells = null;

            SubscribeToModel();

            Model.Start();

            RefreshBestHud();
        }

        private void SubscribeToModel()
        {
            if (Model == null)
            {
                return;
            }

            Model.Started += OnModelStarted;
            Model.MoveApplied += OnModelMoveApplied;
            Model.ScoreChanged += OnModelScoreChanged;
            Model.GameOver += OnModelGameOver;
        }

        private void UnsubscribeFromModel()
        {
            if (Model == null)
            {
                return;
            }

            Model.Started -= OnModelStarted;
            Model.MoveApplied -= OnModelMoveApplied;
            Model.ScoreChanged -= OnModelScoreChanged;
            Model.GameOver -= OnModelGameOver;
        }

        private void OnModelStarted()
        {
            RenderBoard();
            UpdateScoreHud(Model.Score);
        }

        private void OnModelMoveApplied(MoveResult result)
        {
            if (_boardView == null || Model == null)
            {
                return;
            }

            _isAnimating = true;
            _boardView.PlayMove(result, Model.Board, HandleMoveAnimationComplete);
        }

        private void HandleMoveAnimationComplete()
        {
            _isAnimating = false;

            if (_pendingGameOver)
            {
                _pendingGameOver = false;
                ShowGameOver();
            }
        }

        private void OnModelScoreChanged(int score)
        {
            UpdateScoreHud(score);
        }

        // MoveApplied (which starts the board animation and sets _isAnimating) always fires
        // before GameOver in MergeGameModel.ApplyMove — defer the scrim/screen transition to
        // HandleMoveAnimationComplete so it doesn't fade in over the triggering move's still-
        // playing slide/merge/spawn animation. Falls through to showing immediately if there's
        // no animation in flight (e.g. _boardView was null and OnModelMoveApplied no-opped).
        private void OnModelGameOver()
        {
            if (_isAnimating)
            {
                _pendingGameOver = true;
                return;
            }

            ShowGameOver();
        }

        private void ShowGameOver()
        {
            if (Model == null || ScreenFlow == null)
            {
                return;
            }

            if (ScreenFlow.GameOverFinalScoreText != null)
            {
                ScreenFlow.GameOverFinalScoreText.text = $"Score: {Model.Score}";
            }

            if (ScreenFlow.GameOverHighestTileText != null)
            {
                ScreenFlow.GameOverHighestTileText.text = $"Highest tile: {Model.HighestTile}";
            }

            ScreenFlow.Show(ScreenId.GameOver);

            HighScoreStore.TrySave(Model.Score);
            if (Model.Score > _bestScore)
            {
                _bestScore = Model.Score;
                UpdateBestHud(_bestScore);
            }

            SubmitScoreAsync(Model.Score);
        }

        private void OnDirectionPerformed(Direction direction)
        {
            if (Model == null || ScreenFlow == null)
            {
                return;
            }

            if (ScreenFlow.Current != ScreenId.Gameplay || Model.IsGameOver || _isAnimating)
            {
                return;
            }

            var preMoveCells = Model.Board.Snapshot();
            int preMoveScore = Model.Score;
            int preMoveMoveCount = Model.MoveCount;
            int preMoveHighestTile = Model.HighestTile;

            var result = Model.ApplyMove(direction);
            DirectionInput?.Invoke(direction);

            if (result.Changed)
            {
                _undoSnapshotCells = preMoveCells;
                _undoSnapshotScore = preMoveScore;
                _undoSnapshotMoveCount = preMoveMoveCount;
                _undoSnapshotHighestTile = preMoveHighestTile;
                _hasUndoSnapshot = true;
            }
        }

        private void OnUndoClicked()
        {
            // _isAnimating gate avoids an abrupt mid-slide teleport if the player taps Undo
            // while the previous move's board animation is still playing.
            if (_isAnimating || UndoCharges <= 0 || !_hasUndoSnapshot || Model == null)
            {
                UndoAttempted?.Invoke(false);
                return;
            }

            UndoCharges--;
            Model.RestoreSnapshot(_undoSnapshotCells, _undoSnapshotScore, _undoSnapshotMoveCount, _undoSnapshotHighestTile);

            _hasUndoSnapshot = false;
            _undoSnapshotCells = null;

            RenderBoard();
            UpdateScoreHud(Model.Score);
            UpdateUndoHud();

            UndoAttempted?.Invoke(true);
        }

        private void OnBuyUndoPackClicked()
        {
            PurchaseUndoPackAsync();
        }

        private async void PurchaseUndoPackAsync()
        {
            if (MonetizationFlows == null)
            {
                return;
            }

            bool success = await MonetizationFlows.PurchaseUndoPackAsync(_lifetimeToken);

            if (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            if (success)
            {
                UndoCharges += 3;
                UpdateUndoHud();
            }
        }

        private void OnRemoveAdsClicked()
        {
            PurchaseRemoveAdsAsync();
        }

        private async void PurchaseRemoveAdsAsync()
        {
            if (MonetizationFlows == null)
            {
                return;
            }

            bool success = await MonetizationFlows.PurchaseRemoveAdsAsync(_lifetimeToken);

            if (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            if (success)
            {
                AdsRemoved = true;

                if (ScreenFlow != null && ScreenFlow.RemoveAdsButton != null)
                {
                    ScreenFlow.RemoveAdsButton.interactable = false;
                }
            }
        }

        private void OnContinueWithAdClicked()
        {
            ContinueWithAdAsync();
        }

        private async void ContinueWithAdAsync()
        {
            if (MonetizationFlows == null)
            {
                return;
            }

            bool rewarded = await MonetizationFlows.ShowRewardedContinueAdAsync(_lifetimeToken);

            if (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            if (!rewarded)
            {
                return;
            }

            ApplyContinueEffect();

            if (ScreenFlow != null)
            {
                ScreenFlow.Show(ScreenId.Gameplay);
            }
        }

        private void ApplyContinueEffect()
        {
            if (Model == null)
            {
                return;
            }

            var cells = Model.Board.Snapshot();
            ClearLowestNonZeroTile(cells);
            ClearLowestNonZeroTile(cells);

            Model.RestoreSnapshot(cells, Model.Score, Model.MoveCount, Model.HighestTile);

            RenderBoard();
            UpdateScoreHud(Model.Score);
        }

        private static void ClearLowestNonZeroTile(int[,] cells)
        {
            int size = Board.SIZE;
            int lowestValue = int.MaxValue;
            int lowestRow = -1;
            int lowestCol = -1;

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    int value = cells[row, col];
                    if (value > 0 && value < lowestValue)
                    {
                        lowestValue = value;
                        lowestRow = row;
                        lowestCol = col;
                    }
                }
            }

            if (lowestRow >= 0)
            {
                cells[lowestRow, lowestCol] = 0;
            }
        }

        private async void SubmitScoreAsync(int score)
        {
            if (MonetizationFlows == null)
            {
                return;
            }

            await MonetizationFlows.SubmitScoreToLeaderboardAsync(score, _lifetimeToken);
        }

        // Instant path (initial start, Undo, ad-continue). BoardView.Render() stops any in-flight
        // move-animation coroutine before snapping to the new state, so any PlayMove() started
        // for the interrupted move will never call its onComplete — reset the flag here instead
        // of leaving input permanently locked.
        private void RenderBoard()
        {
            _isAnimating = false;

            if (_boardView != null && Model != null)
            {
                _boardView.Render(Model.Board);
            }
        }

        private void UpdateScoreHud(int score)
        {
            if (ScreenFlow != null && ScreenFlow.ScoreValueText != null)
            {
                ScreenFlow.ScoreValueText.text = score.ToString();
            }
        }

        private void RefreshBestHud()
        {
            _bestScore = HighScoreStore.Load();
            UpdateBestHud(_bestScore);
        }

        private void UpdateBestHud(int bestScore)
        {
            if (ScreenFlow != null && ScreenFlow.BestValueText != null)
            {
                ScreenFlow.BestValueText.text = bestScore.ToString();
            }
        }

        private void UpdateUndoHud()
        {
            if (ScreenFlow == null)
            {
                return;
            }

            if (ScreenFlow.UndoChargeText != null)
            {
                ScreenFlow.UndoChargeText.text = UndoCharges.ToString();
            }

            if (ScreenFlow.UndoButton != null)
            {
                ScreenFlow.UndoButton.interactable = UndoCharges > 0;
            }
        }
    }
}
