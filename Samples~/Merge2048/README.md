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
incremental state (score, moves, highest tile), a scene load (Boot → Game),
monetization operations (rewarded ad, two IAP products), an HTTP-shaped
operation (leaderboard submit) with a real failure branch, and a restart
flow. No feature was added to the game *for* SDK-coverage reasons that
doesn't also make sense as a game feature on its own.

## Method → where → why

| SDK method | Called from | Why here |
|---|---|---|
| `Initialize()` | `Integration/PlayScopeBootstrapper.cs` (Boot scene, `Awake`) | As early as possible; reads `Resources/PlayScopeSettings.asset` |
| `SetUserData` | `PlayScopeBootstrapper.cs`, right after `Initialize` | Anonymous local GUID (`Integration/AnonymousPlayerId.cs`) + `is_guest` — no PII |
| `UpdateSessionData` (×2) | `PlayScopeBootstrapper.cs` (`board_size`, reason `boot_complete`) and `PlayScopeGameAnalytics.cs` (`difficulty` + `spawn_per_turn`, reason `difficulty_selected`) | Split across two calls because difficulty isn't known yet at boot time — sending it at boot would mean guessing |
| `StartSceneLoad` / `EndSceneLoad` | `PlayScopeBootstrapper.cs`, wrapping the Boot→Game `SceneManager.LoadSceneAsync` | The overload that takes the `AsyncOperation` — the SDK auto-samples load progress, no manual polling needed |
| `SetScreen` | `PlayScopeGameAnalytics.cs`, subscribed to `ScreenFlow.ScreenChanged` | One subscription covers all five screens (`MainMenu` / `DifficultySelect` / `Gameplay` / `GameOver` / `Shop`) |
| `TrackAction` | `PlayScopeGameAnalytics.cs`, subscribed to every `ScreenFlow`/`MergeGameController` UI event | `TapPlay`, `SelectDifficulty` (+level), `Swipe` (+direction), `TapUndo` (+success), `TapContinueWithAd`, `TapRestart`, `OpenShop`, `TapCloseShop`, `TapBuyUndoPack`, `TapRemoveAds` |
| `SetInitialState` | `PlayScopeGameAnalytics.cs`, after a new `MergeGameModel` starts (fresh game or restart) | `difficulty`, `score`, `moves`, `highest_tile`, `filled_cells` |
| `UpdateState` | `PlayScopeGameAnalytics.cs`, subscribed to `MergeGameModel.MoveApplied` / `HighestTileChanged` | reason `"move"` for an ordinary move, `"new_high_tile"` when that move also raised the record (never both — see the coalescer note in the code) |
| `StartAd` / `EndAd` | `App/MonetizationFlows.cs` | Rewarded "continue" on Game Over, placement `Rewarded_GameOver` |
| `StartPurchase` / `EndPurchase` | `App/MonetizationFlows.cs` | Shop: "Buy Undo Pack" (`undo_pack_3`) and "Remove Ads" (`remove_ads`) |
| `StartHTTP` / `EndHTTP` | `App/MonetizationFlows.cs` | Simulated leaderboard submit on Game Over, including a real ~15%-chance failure branch |
| `TrackException` | `App/MonetizationFlows.cs` (leaderboard submit failure) and `Integration/PlayScopeGameAnalytics.cs` (`TryLoadHighScore` — a genuine `int.Parse` failure on a corrupted local save value) | Two different exception sources, both realistic, neither invented just to exercise the API |
| `TrackLog` | `PlayScopeGameAnalytics.cs`, on `HighestTileChanged` | Info-level milestone log when the highest tile reaches 2048 or 4096 |
| `TrackRestart` | `PlayScopeGameAnalytics.cs`, subscribed to `MergeGameController.RestartRequested` | Reason `"defeat_restart"` — fires after Game Over → Restart, followed by a fresh `SetInitialState` |

Not demonstrated on purpose: `SetTelemetryEnabled` / per-category
`SetMetricsCategory` — they don't exist in this SDK version yet (see the
roadmap). The only supported opt-out today is the one this sample shows:
skip `Initialize()` entirely and every `PlayScope.*` call becomes a global
no-op for the session.

## Consent — read this before you copy it

`Integration/ConsentGate.cs` is a **stub**, not a compliance feature. It
auto-grants on first run so the sample is playable out of the box. A real
game must replace `ConsentGate.ResolveForSession()` with its own CMP /
consent-UI decision *before* `Initialize()` is ever called. Call
`ConsentGate.Decline()` yourself (e.g. temporarily from a debug button, or
by editing `PlayerPrefs`) to see the opt-out path: `Initialize()` never
runs, and everything downstream — `SetScreen`, `TrackAction`, `UpdateState`,
all of it — silently no-ops, exactly like it would in a real game.

## PII hygiene

`AnonymousPlayerId.cs` generates a local, random GUID and never anything
tied to a real identity. If you're wiring your own game up to
`SetUserData`, `TrackAction` names, `UpdateState` keys, or
`PurchaseMetadata`/`AdMetadata` placement strings: don't put email
addresses, player handles, or account IDs into any of them — they're stored
verbatim and rendered in the dashboard UI.

## Running with no SDK key

`Resources/PlayScopeSettings.asset` ships with an **empty** `SdkKey` (never
commit a real key here). With an empty key the game is fully playable and
the SDK stays a global no-op — you'll see one console warning at startup
and nothing else. Paste your own `ps_live_…` key from the dashboard into
the asset to see real events arrive.

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

This sample was written and reviewed without an interactive Unity Editor
session available — there was no Play-mode pass, no visual check, and no
live-dashboard check of arriving events. Before you rely on it: open the
project, load `Merge2048_Boot.unity`, press Play, and walk through Easy /
Medium / Hard, a game-over, the rewarded-continue flow, and the shop. If
anything doesn't compile or renders oddly, that's the first thing to fix.
