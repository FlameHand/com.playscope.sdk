# 2048 Merge Demo (6×6)

A playable merge-mechanics game that demonstrates an end-to-end, best-practice
PlayScope SDK integration. It exists to answer one question: *"I've read the
integration guide — what does a real, working call site actually look like?"*

Import it from **Package Manager → PlayScope SDK → Samples → 2048 Merge Demo
→ Import**, open `Scenes/Merge2048_Boot.unity`, press Play.

## The one rule this sample enforces

**Game logic never calls `PlayScope.*`.** All telemetry lives in one folder —
`Scripts/Integration/` — which subscribes to plain C# events raised by the
game. Delete the entire `Integration/` folder and the game still compiles and
plays; you just stop sending data. That's the architecture you should copy
into your own project.

```
Scripts/
├── Core/           pure C# (no UnityEngine, no PlayScope) — board, moves, merge rules
├── Presentation/    uGUI + TMP, built procedurally at runtime — zero PlayScope calls
├── Monetization/    simulated ad/IAP/leaderboard services — no external SDKs, no PlayScope calls
├── App/             glues Core + Presentation + Monetization together — zero PlayScope calls,
│                    EXCEPT MonetizationFlows.cs (see "The one exception" below)
└── Integration/     the ONLY place that calls PlayScope.* for observational events
```

### The one exception: `App/MonetizationFlows.cs`

`StartAd`/`StartPurchase`/`StartHTTP` return an operation ID that must wrap
the `await` of the actual async work — that `await` happens inside
`MonetizationFlows`, not in a decoupled subscriber in `Integration/`. This is
the one file outside `Integration/` that calls `PlayScope.*`, and it says so
in a comment at the top of the file. Every other SDK call in the sample is a
subscriber reacting to a C# event.

## Why this game

A 6×6 merge-2048 board isn't the point — it's a vehicle that naturally
produces every kind of event the SDK captures: screen transitions (menu →
difficulty → gameplay → game-over → shop), discrete actions (taps, swipes),
incremental state (score, moves, highest tile), a two-phase boot (a manual
warmup progress bar, then an auto-sampled scene load), a save/continue flow
wrapped in a custom operation span, monetization operations (rewarded ad,
interstitial ad, two IAP products, restore purchases), an HTTP-shaped
operation (leaderboard submit) with real failure and timeout branches, a
restart flow, and a first-run consent decision. No feature was added to the
game *for* SDK-coverage reasons that doesn't also make sense as a game
feature on its own — with one acknowledged exception, the Diagnostics panel
(see below).

## Method → where → why

| SDK method | Called from | Why here |
|---|---|---|
| `Initialize()` / `Initialize(PlayScopeContext)` | `Integration/PlayScopeBootstrapper.cs` (`InitializeSdk()`, right after consent is granted) | Default path reads `Resources/PlayScopeSettings.asset`; when the `_useExplicitContext` inspector flag is set, it builds a `PlayScopeContext` instead (`SdkKey`, `AutoCaptureUnityLogs`, `AutoCaptureMinLevel`, and `Metadata` with `environment`/`build_number`) — the runtime-config path from the integration guide |
| `PlayScope.IsInitialized` / `PlayScope.IsDisabled` | `PlayScopeBootstrapper.cs` (logged once right after the consent decision) and `Integration/DiagnosticsController.cs` (`RefreshStatus()`, shown live in the Diagnostics panel) | Lets you confirm from a running build whether declining consent actually turned the SDK into a no-op |
| `PlayScope.Settings` | `DiagnosticsController.cs` (`RefreshStatus()`) | Reads `MinLogLevel` for display, and shows a "no SDK key" hint in the panel when `SdkKey` is empty |
| `SetUserData` | `PlayScopeBootstrapper.cs`, right after `Initialize` (`is_guest`); called again from `PlayScopeGameAnalytics.cs` on `RemoveAdsEntitlementGranted` (adds `has_remove_ads: true`) | Anonymous local GUID (`Integration/AnonymousPlayerId.cs`) — no PII. The second call demonstrates that re-calling `SetUserData` later to add a field (after a purchase *or* a restore grants the same entitlement) is safe and doesn't clobber the identity |
| `UpdateSessionData` (×2) | `PlayScopeBootstrapper.cs` (`board_size`, reason `boot_complete`) and `PlayScopeGameAnalytics.cs` (`difficulty` + `spawn_per_turn`, reason `difficulty_selected`) | Split across two calls because difficulty isn't known yet at boot time — sending it at boot would mean guessing |
| `StartResourceLoad` / `RecordSceneLoadProgress` / `EndResourceLoad` | `PlayScopeBootstrapper.cs` (`RunWarmupThenLoadScene()`, wrapping a fake `content_warmup` step that runs before the scene load even starts) | The manual-progress path — there's no `AsyncOperation` for a warmup step, so progress is reported by hand each frame instead of auto-sampled |
| `StartSceneLoad(AsyncOperation)` / `EndSceneLoad` | `PlayScopeBootstrapper.cs` (`LoadGameSceneAsync()`, wrapping the Boot→Game `SceneManager.LoadSceneAsync`) | The auto-sampled overload — the SDK reads `AsyncOperation.progress` itself, no manual polling needed; contrast with the manual warmup path above |
| `SetScreen` | `PlayScopeGameAnalytics.cs`, subscribed to `ScreenFlow.ScreenChanged` | One subscription covers all five screens (`MainMenu` / `DifficultySelect` / `Gameplay` / `GameOver` / `Shop`); opening the Shop attaches `{ source: "gameplay_hud" }` or `{ source: "game_over" }` metadata so the dashboard can tell which entry point sent the player there |
| `TrackAction` | `PlayScopeGameAnalytics.cs`, subscribed to every `ScreenFlow`/`MergeGameController` UI event | `TapPlay`, `SelectDifficulty` (+level), `Swipe` (+direction), `TapUndo` (+success), `TapContinue`, `TapContinueWithAd`, `TapRestart`, `OpenShop` (+source), `TapCloseShop`, `TapBuyUndoPack`, `TapRemoveAds`, `TapRestorePurchases` |
| `SetInitialState` | `PlayScopeGameAnalytics.cs`, after a new `MergeGameModel` starts (fresh game, restart, or a successful `Continue`) | `difficulty`, `score`, `moves`, `highest_tile`, `filled_cells` |
| `UpdateState` | `PlayScopeGameAnalytics.cs`, subscribed to `MergeGameModel.MoveApplied` / `HighestTileChanged` | reason `"move"` for an ordinary move, `"new_high_tile"` when that move also raised the record (never both — see the coalescer note in the code) |
| `StartOperation(OperationType.Custom, "LoadSaveData")` / `CompleteOperation` | `PlayScopeGameAnalytics.cs` (`OnSaveLoadAttempted`, wrapping `SaveDataStore.TryLoad()`) | Demonstrates a generic custom-operation span outside the built-in ad/purchase/HTTP/scene helpers; every `SaveDataStore.SaveLoadOutcome` maps to a different completion — see "Save / Continue" below |
| `TrackLog(LogLevel.Warning, ...)` | `PlayScopeGameAnalytics.cs` (`OnSaveLoadAttempted`, `SaveLoadOutcome.OldFormat` branch) | The sample's only Warning-level log — an older-format save is a real, recoverable condition, not an error, so the game starts fresh instead of failing |
| `TrackLog(LogLevel.Info, ...)` | `PlayScopeGameAnalytics.cs` (highest-tile milestone at 2048/4096) and `DiagnosticsController.cs` ("Spam log ×20" and the PII-sample log) | Milestone logging, plus two dev-tool demos: 20 identical calls collapse into one dashboard row with `repeat_count: 20` via `LogDedupBuffer`, and `contact me: foo@bar.com` arrives server-side as `[redacted-email]` |
| `TrackException` | `App/MonetizationFlows.cs` (leaderboard submit failure, context `leaderboard_submit`), `PlayScopeGameAnalytics.cs` (corrupted save load, context `save_load`), and `PlayScopeGameAnalytics.cs` (`HighScoreStore.LoadFailed` — a genuine `int.Parse` failure on a corrupted high-score value, context `high_score_load`) | Three different exception sources, all reachable from real (simulated) failure paths, none invented just to exercise the API |
| `TrackRestart` | `PlayScopeGameAnalytics.cs`, subscribed to `MergeGameController.RestartRequested` | Reason `"defeat_restart"`, with `from_score` / `from_moves` / `from_highest_tile` metadata carried over from the game that just ended, followed by a fresh `SetInitialState` |
| `StartAd` / `EndAd` | `App/MonetizationFlows.cs` | Rewarded "continue" on Game Over (`AdType.Rewarded`, placement `Rewarded_GameOver`); and a fake interstitial between restarts (`AdType.Interstitial`, placement `Interstitial_BetweenGames`) shown every 2nd restart after Game Over, only while Remove Ads isn't owned — results include `Closed` / `NoFill` / `Failed` alongside the rewarded outcomes |
| `StartPurchase` / `EndPurchase` | `App/MonetizationFlows.cs` | Shop: "Buy Undo Pack" (`undo_pack_3`), "Remove Ads" (`remove_ads`), and "Restore Purchases" — the restore call targets `remove_ads` again via `PurchaseMetadata.BuildStartMetadata(..., isRestore: true)`, the sample's only `is_restore: true` flow |
| `StartHTTP` / `EndHTTP` | `App/MonetizationFlows.cs` | Simulated leaderboard submit on Game Over, with three failure-shaped branches: a ~10% `Failure` (HTTP-style status code + a paired `TrackException`), a ~5% `Timeout` (`OperationCompletionStatus.Timeout` with `{ timeout_ms: 5000 }` — the sample's only use of that status), and a `Cancelled` path if torn down mid-flight |

Not demonstrated on purpose: `SetTelemetryEnabled` / per-category
`SetMetricsCategory` — they don't exist in this SDK version yet (see the
roadmap). The only supported opt-out today is the one this sample shows:
decline the consent dialog and `Initialize()` never runs, so every
`PlayScope.*` call becomes a global no-op for the session. Also not manually
triggered anywhere in this sample: `memory_warning`, `network_change`, and
(outside of the Diagnostics panel's "Simulate ANR" button) `anr` /
`anr_recovered` — those are automatic SDK-side captures, not something game
code calls.

## Save / Continue

The game autosaves to PlayerPrefs after every move that changes the board
(`App/SaveDataStore.cs`, hand-rolled delimited format — no JSON library is
referenced by this sample's asmdef). When a save exists, a "Continue" button
appears on the main menu. Loading it is wrapped end-to-end in
`StartOperation(OperationType.Custom, "LoadSaveData")` /
`CompleteOperation(...)`:

- **Success** → `CompleteOperation(Success)`, then `TrackAction("TapContinue")`
  (from the button click) and a fresh `SetInitialState` from the restored
  state.
- **Old format** (an earlier save-format version) → `CompleteOperation(Success)`
  + `TrackLog(LogLevel.Warning, ...)` — recoverable, so the game starts a
  fresh run instead of failing.
- **Corrupted** (malformed field count / cell count / unparsable value) →
  `CompleteOperation(Failure)` + `TrackException` with context `save_load`.
- **Not found** → `CompleteOperation(Abandoned)` — the Continue button simply
  isn't shown, but the codepath is defensive against a race.

A finished game clears its own save immediately so "Continue" can't walk
straight into an already-game-over board.

## Consent — read this before you copy it

The Boot scene shows a real first-run Accept/Decline dialog
(`Presentation/ConsentDialogView.cs`) before `Initialize()` is ever called.
`Integration/ConsentGate.cs` only stores the decision in `PlayerPrefs`; the
dialog is a Presentation component and never calls PlayScope itself —
`Integration/PlayScopeBootstrapper.cs` reads the dialog's `DecisionMade`
event and decides what it means. Once a decision is stored, the dialog is
skipped on later runs; reset it from the Diagnostics panel's "Reset consent &
save" button (or by clearing the `Merge2048_TelemetryConsent` PlayerPrefs
key directly) to see the first-run prompt again.

Decline it to see the opt-out path: `Initialize()` never runs, and
everything downstream — `SetScreen`, `TrackAction`, `UpdateState`, all of
it — silently no-ops, exactly like it would in a real game (the game still
boots normally either way).

This dialog is still **not** a compliance feature — it doesn't do consent
signal forwarding, regional gating, or vendor lists. What it *is* meant to
show is the shape of the integration point: this is where a real CMP
(OneTrust, Google's UMP, etc.) plugs in — its callback becomes the thing
that calls `ConsentGate.Grant()` / `ConsentGate.Decline()` instead of a
button tap, and everything downstream in `PlayScopeBootstrapper` stays the
same.

## Diagnostics panel — dev tool, not a game feature

The gear button on the main menu is the one acknowledged exception to "every
feature also makes sense as a game feature" — it exists purely to make the
SDK's behavior visible while you're integrating, and should not be copied
into a shipping game. It opens an overlay (`Presentation/DiagnosticsPanelView.cs`,
wired by `Integration/DiagnosticsController.cs`) showing:

- **Live SDK status**: `PlayScope.IsInitialized`, `PlayScope.IsDisabled`,
  `PlayScope.Settings.MinLogLevel`, plus a "no SDK key" hint when
  `Resources/PlayScopeSettings.asset` has an empty key.
- **Simulate ANR** — blocks the main thread for ~3 seconds with
  `Thread.Sleep`. This only produces an `anr` / `anr_recovered` pair in real
  builds (or batch mode); the ANR watchdog is auto-disabled in the Editor,
  where debugger breakpoints would otherwise look like false-positive hangs.
- **Spam log ×20** — fires the same `TrackLog(Info, ...)` call 20 times in a
  row to show `LogDedupBuffer` collapsing them into one dashboard row with
  `repeat_count: 20`.
- **Send PII-looking log** — sends `"contact me: foo@bar.com"` so you can
  confirm it arrives server-side as `[redacted-email]`, not the raw address.
- **Reset consent & save** — clears the consent decision and the current
  save, for re-testing the first-run flow without reinstalling.
- **Toggle Event Feed** — shows/hides the event-feed overlay below.

demonstrates SDK diagnostics; do not copy into a production game.

## Event-feed overlay

A small translucent feed in the top-left corner shows the last ~8 SDK calls
made by the sample, each fading out a couple of seconds after it appears.
It's driven by `Integration/AnalyticsFeed.cs`, a static in-process pub/sub
bus with no PlayScope references of its own — call sites across
`Integration/` and `MonetizationFlows.cs` publish a short label to it
immediately alongside their real `PlayScope.*` call, and
`Presentation/AnalyticsFeedView.cs` renders whatever comes through. Toggle
it from the Diagnostics panel. It's a "you can *see* what's being sent"
aid for people reading the code side-by-side with the running game, not
something a real game would ship.

## PII hygiene

`AnonymousPlayerId.cs` generates a local, random GUID and never anything
tied to a real identity. If you're wiring your own game up to
`SetUserData`, `TrackAction` names, `UpdateState` keys, or
`PurchaseMetadata`/`AdMetadata` placement strings: don't put email
addresses, player handles, or account IDs into any of them — they're stored
verbatim and rendered in the dashboard UI. The Diagnostics panel's "Send
PII-looking log" button demonstrates the one safety net that exists
independent of your own discipline: value-level PII masking on the log
pipeline (email-shaped substrings become `[redacted-email]`) — it's a
backstop, not a reason to be careless about what you pass in.

## Running with no SDK key

`Resources/PlayScopeSettings.asset` ships with an **empty** `SdkKey` (never
commit a real key here). With an empty key the game is fully playable and
the SDK stays a global no-op — you'll see one console warning at startup
and nothing else. Paste your own `ps_live_…` key from the dashboard into
the asset (or into the explicit `PlayScopeContext` path, if `_useExplicitContext`
is enabled) to see real events arrive.

## Two design calls the game design doc left open

The plan this sample was built from specified the SDK plumbing around two
features but not their exact game-mechanical effect. Both are implemented
in `App/MergeGameController.cs`:

- **"Watch ad to continue" (Game Over → rewarded ad → keep playing):** clears
  the two lowest-value tiles on the board, which is always enough to break
  the game-over condition (full board, no adjacent equal pair) since it
  frees at least two cells. Score and move count are untouched.
- **Undo (single charge, sold in the Shop as a 3-pack):** the board/score/
  move-count/highest-tile are snapshotted before every move that actually
  changes something; only the single most recent snapshot is kept, so undo
  is exactly one level deep and can't be chained.

## What isn't tested here

This sample now targets full API coverage of the SDK's public surface, but
that coverage claim itself hasn't been exercised in a running Editor yet —
compile and Play-mode verification is being handled separately by the
maintainer, not as part of writing this doc. Before you rely on it, at
minimum smoke-test these on the dashboard against a real SDK key:

- the custom operation around save/continue (`LoadSaveData`, all four outcomes)
- the manual-progress resource-load samples during boot warmup
- the interstitial ad on every 2nd restart
- "Restore Purchases" restoring Remove Ads and re-calling `SetUserData`
- the leaderboard `Timeout` completion status (~5% of submits)
- the Warning-level save-format log
- `repeat_count: 20` from the Diagnostics panel's log-spam button
- `[redacted-email]` from the Diagnostics panel's PII-sample button

**TODO (maintainer):** record the date and scope of the first real Play-mode
pass here once it's run.
