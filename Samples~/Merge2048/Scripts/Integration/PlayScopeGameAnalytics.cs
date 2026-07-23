using System;
using System.Collections.Generic;
using UnityEngine;
using Merge2048.App;
using Merge2048.Core;
using Merge2048.Presentation;
using PlayScopeSdk;

namespace Merge2048.Integration
{
    public sealed class PlayScopeGameAnalytics : MonoBehaviour
    {
        private MergeGameController _controller;
        private MergeGameModel _subscribedModel;
        private bool _highestTileFlaggedThisMove;

        private void Awake()
        {
            _controller = gameObject.AddComponent<MergeGameController>();

            var screenFlow = _controller.ScreenFlow;
            screenFlow.ScreenChanged += OnScreenChanged;
            screenFlow.PlayClicked += OnPlayClicked;
            screenFlow.ContinueClicked += OnContinueClicked;
            screenFlow.DifficultySelected += OnDifficultySelected;
            screenFlow.ContinueWithAdClicked += OnContinueWithAdClicked;
            screenFlow.RestartClicked += OnRestartButtonClicked;
            screenFlow.OpenShopClicked += OnOpenShopClicked;
            screenFlow.CloseShopClicked += OnCloseShopClicked;
            screenFlow.BuyUndoPackClicked += OnBuyUndoPackClicked;
            screenFlow.RemoveAdsClicked += OnRemoveAdsClicked;

            _controller.DirectionInput += OnDirectionInput;
            _controller.UndoAttempted += OnUndoAttempted;
            _controller.RestartRequested += OnRestartRequested;
            _controller.SaveLoadAttempted += OnSaveLoadAttempted;
            _controller.ResumedFromSave += OnResumedFromSave;

            HighScoreStore.LoadFailed += OnHighScoreLoadFailed;
        }

        private void OnDestroy()
        {
            if (_controller != null && _controller.ScreenFlow != null)
            {
                var screenFlow = _controller.ScreenFlow;
                screenFlow.ScreenChanged -= OnScreenChanged;
                screenFlow.PlayClicked -= OnPlayClicked;
                screenFlow.ContinueClicked -= OnContinueClicked;
                screenFlow.DifficultySelected -= OnDifficultySelected;
                screenFlow.ContinueWithAdClicked -= OnContinueWithAdClicked;
                screenFlow.RestartClicked -= OnRestartButtonClicked;
                screenFlow.OpenShopClicked -= OnOpenShopClicked;
                screenFlow.CloseShopClicked -= OnCloseShopClicked;
                screenFlow.BuyUndoPackClicked -= OnBuyUndoPackClicked;
                screenFlow.RemoveAdsClicked -= OnRemoveAdsClicked;
            }

            if (_controller != null)
            {
                _controller.DirectionInput -= OnDirectionInput;
                _controller.UndoAttempted -= OnUndoAttempted;
                _controller.RestartRequested -= OnRestartRequested;
                _controller.SaveLoadAttempted -= OnSaveLoadAttempted;
                _controller.ResumedFromSave -= OnResumedFromSave;
            }

            HighScoreStore.LoadFailed -= OnHighScoreLoadFailed;

            RebindModel(null);
        }

        private void OnScreenChanged(ScreenId screen)
        {
            PlayScope.SetScreen(ScreenFlow.ScreenName(screen));
        }

        private void OnPlayClicked()
        {
            PlayScope.TrackAction("TapPlay");
        }

        private void OnDifficultySelected(Difficulty difficulty)
        {
            PlayScope.TrackAction("SelectDifficulty", new Dictionary<string, object>
            {
                ["level"] = difficulty.ToString(),
            });

            RebindModel(_controller.Model);
            SendInitialStateSnapshot();

            PlayScope.UpdateSessionData(new Dictionary<string, object>
            {
                ["difficulty"] = difficulty.ToString(),
                ["spawn_per_turn"] = DifficultyConfig.SpawnCountFor(difficulty),
            }, "difficulty_selected");
        }

        private void OnDirectionInput(Direction direction)
        {
            PlayScope.TrackAction("Swipe", new Dictionary<string, object>
            {
                ["direction"] = direction.ToString(),
            });
        }

        private void OnUndoAttempted(bool success)
        {
            PlayScope.TrackAction("TapUndo", new Dictionary<string, object>
            {
                ["success"] = success,
            });
        }

        private void OnContinueWithAdClicked()
        {
            PlayScope.TrackAction("TapContinueWithAd");
        }

        private void OnRestartButtonClicked()
        {
            PlayScope.TrackAction("TapRestart");
        }

        private void OnOpenShopClicked()
        {
            PlayScope.TrackAction("OpenShop");
        }

        private void OnCloseShopClicked()
        {
            PlayScope.TrackAction("TapCloseShop");
        }

        private void OnBuyUndoPackClicked()
        {
            PlayScope.TrackAction("TapBuyUndoPack");
        }

        private void OnRemoveAdsClicked()
        {
            PlayScope.TrackAction("TapRemoveAds");
        }

        private void OnRestartRequested(string reason)
        {
            PlayScope.TrackRestart(reason);

            RebindModel(_controller.Model);
            SendInitialStateSnapshot();
        }

        private void OnContinueClicked()
        {
            PlayScope.TrackAction("TapContinue");
        }

        private void OnSaveLoadAttempted(SaveDataStore.SaveLoadResult result)
        {
            var opId = PlayScope.StartOperation(OperationType.Custom, "LoadSaveData");

            switch (result.Outcome)
            {
                case SaveDataStore.SaveLoadOutcome.Success:
                {
                    PlayScope.CompleteOperation(opId, OperationCompletionStatus.Success);
                    break;
                }
                case SaveDataStore.SaveLoadOutcome.OldFormat:
                {
                    PlayScope.CompleteOperation(opId, OperationCompletionStatus.Success);
                    PlayScope.TrackLog(LogLevel.Warning, "Save data is an older format; starting a fresh game.", new Dictionary<string, object>
                    {
                        ["outcome"] = "old_format",
                    });
                    break;
                }
                case SaveDataStore.SaveLoadOutcome.Corrupted:
                {
                    PlayScope.CompleteOperation(opId, OperationCompletionStatus.Failure);
                    if (result.Error != null)
                    {
                        PlayScope.TrackException(result.Error, new Dictionary<string, object>
                        {
                            ["context"] = "save_load",
                        });
                    }
                    break;
                }
                case SaveDataStore.SaveLoadOutcome.NotFound:
                {
                    PlayScope.CompleteOperation(opId, OperationCompletionStatus.Abandoned);
                    break;
                }
                case SaveDataStore.SaveLoadOutcome.Unknown:
                default:
                {
                    PlayScope.CompleteOperation(opId, OperationCompletionStatus.Abandoned);
                    break;
                }
            }
        }

        private void OnResumedFromSave()
        {
            SendInitialStateSnapshot();
        }

        private void SendInitialStateSnapshot()
        {
            var model = _controller.Model;
            if (model == null)
            {
                return;
            }

            PlayScope.SetInitialState(new Dictionary<string, object>
            {
                ["difficulty"] = model.Difficulty.ToString(),
                ["score"] = model.Score,
                ["moves"] = model.MoveCount,
                ["highest_tile"] = model.HighestTile,
                ["filled_cells"] = Board.SIZE * Board.SIZE - model.Board.GetEmptyCells().Count,
            });
        }

        private void RebindModel(MergeGameModel newModel)
        {
            if (_subscribedModel != null)
            {
                _subscribedModel.MoveApplied -= OnModelMoveApplied;
                _subscribedModel.HighestTileChanged -= OnModelHighestTileChanged;
            }

            _subscribedModel = newModel;
            _highestTileFlaggedThisMove = false;

            if (_subscribedModel != null)
            {
                _subscribedModel.MoveApplied += OnModelMoveApplied;
                _subscribedModel.HighestTileChanged += OnModelHighestTileChanged;
            }
        }

        private void OnModelHighestTileChanged(int newHighest)
        {
            _highestTileFlaggedThisMove = true;
            SendStatePatch("new_high_tile");

            if (newHighest == 2048 || newHighest == 4096)
            {
                PlayScope.TrackLog(LogLevel.Info, $"Milestone reached: highest tile {newHighest}", new Dictionary<string, object>
                {
                    ["highest_tile"] = newHighest,
                });
            }
        }

        private void OnModelMoveApplied(MoveResult result)
        {
            if (!_highestTileFlaggedThisMove)
            {
                SendStatePatch("move");
            }

            _highestTileFlaggedThisMove = false;
        }

        private void SendStatePatch(string reason)
        {
            if (_subscribedModel == null)
            {
                return;
            }

            PlayScope.UpdateState(new Dictionary<string, object>
            {
                ["score"] = _subscribedModel.Score,
                ["moves"] = _subscribedModel.MoveCount,
                ["highest_tile"] = _subscribedModel.HighestTile,
                ["empty_cells"] = _subscribedModel.Board.GetEmptyCells().Count,
            }, reason);
        }

        private void OnHighScoreLoadFailed(Exception ex)
        {
            PlayScope.TrackException(ex, new Dictionary<string, object>
            {
                ["context"] = "high_score_load",
            });
        }
    }
}
