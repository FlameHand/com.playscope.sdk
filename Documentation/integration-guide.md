# Integration Guide

Step-by-step guide for wiring PlayScope SDK into a new Unity project. After this guide you'll have screens, actions, state, operations, exceptions, ANR detection, memory pressure events, performance metrics, and PII-masked metadata flowing into the dashboard.

## 1. Install the Package

Add to `Packages/manifest.json`:

```json
"com.playscope.sdk": "https://github.com/FlameHand/com.playscope.sdk.git#v0.1.39"
```

Or use **Window → Package Manager → ＋ → Add package from git URL**. Pin to a tag (recommended) — CI bumps the version on every push so the package keeps moving without you noticing if you track a branch.

## 2. Get Your SDK Key

1. Log in to [playscope.dev](https://playscope.dev)
2. Open **Settings → Projects**
3. Copy the SDK key (`ps_live_...` for production, `ps_test_...` for dev) — each project has one of each.

> Rotate the key whenever a build with the old one is no longer in the wild. The dashboard's **Rotate** button generates a new value and keeps both valid for a 24-hour overlap window.

## 3. Initialize the SDK

Initialize **as early as possible** — before any gameplay code runs. A good location is `Awake()` on a `DontDestroyOnLoad` bootstrapper or the first `IInitializable` in your DI container.

```csharp
using PlayScopeSdk;
using UnityEngine;
using System.Collections.Generic;

public class AppBootstrapper : MonoBehaviour
{
    [SerializeField] private string _playscopeApiKey;

    private void Awake()
    {
        PlayScope.Initialize(new PlayScopeContext
        {
            ApiKey               = _playscopeApiKey,
            AutoCaptureUnityLogs = true,
            AutoCaptureMinLevel  = LogLevel.Warning,
            Metadata = new Dictionary<string, string>
            {
                ["environment"]  = Debug.isDebugBuild ? "development" : "production",
                ["app_version"]  = Application.version,
                ["build_number"] = "42",
            },
        });
    }
}
```

> **Never hardcode the API key in source.** Use a `ScriptableObject` under `Resources/`, an environment file outside source control, or your remote-config system. The SDK reads it once at `Initialize`-time, so a runtime override is fine.

See [Configuration](configuration.md) for every `PlayScopeContext` field — ANR threshold, PII mask toggle, ingest endpoint, etc.

## 4. Identify Users

Call `SetUserData` once the player is logged in or their identity is otherwise known.

```csharp
PlayScope.SetUserData(user.Id, new Dictionary<string, object>
{
    ["plan"]     = user.SubscriptionPlan,
    ["region"]   = user.Region,
    ["is_guest"] = user.IsGuest,
});
```

Sensitive values inside the metadata dict (emails, tokens, anything matching the value-level masks) are scrubbed automatically before recording — see [Configuration → `PiiValueMasksEnabled`](configuration.md#piivaluemasksenabled).

## 5. Set Initial Game State

`SetInitialState` is the first complete snapshot of the player's profile. Push it once after the save file is loaded but before the first scene transition.

```csharp
PlayScope.SetInitialState(new Dictionary<string, object>
{
    ["level"]      = playerData.Level,
    ["currency"]   = playerData.Currency,
    ["total_runs"] = playerData.TotalRuns,
    ["items"]      = new List<object> { "sword", "shield" },
});
```

After this, every change is a `UpdateState` patch — see step 8.

## 6. Track Screens

```csharp
PlayScope.SetScreen("MainMenu");
PlayScope.SetScreen("GameplayHUD");
PlayScope.SetScreen("DeathScreen");
```

Screens scope subsequent actions on the timeline — the dashboard groups action rows under the most recent `SetScreen` so it's clear *where* in the UI the player tapped something.

## 7. Track Actions

```csharp
PlayScope.TrackAction("TapPlayButton");

PlayScope.TrackAction("PurchaseItem", new Dictionary<string, object>
{
    ["item_id"]    = itemId,
    ["item_price"] = price,
});
```

## 8. Update State on Changes

Patch only the keys that changed — never re-send the full snapshot. The SDK coalesces patches within a 100 ms window so a flurry of small updates collapses into one event.

```csharp
// After spending currency
PlayScope.UpdateState(new Dictionary<string, object> { ["currency"] = newCurrencyValue });

// After level-up — pass a `reason` so reviewers see WHY the patch fired
PlayScope.UpdateState(
    new Dictionary<string, object>
    {
        ["level"] = newLevel,
        ["xp"]    = 0,
    },
    reason: "level_up");
```

> Keep reasons short and consistent — `"level_up"`, `"purchase_completed"`, `"daily_reset"`. The dashboard groups patches by reason for trend analysis.

## 9. Track Operations

Wrap any async work that matters for performance analysis. The dashboard renders matched `start` / `end` pairs as a single duration row.

### HTTP requests

```csharp
var id = PlayScope.StartHTTP($"{request.Method} {request.Path}", new Dictionary<string, object>
{
    ["method"] = request.Method,
    ["url"]    = request.Path,
});
var response = await api.SendAsync(request);
PlayScope.EndHTTP(id,
    response.IsSuccess
        ? OperationCompletionStatus.Success
        : OperationCompletionStatus.Failure,
    new Dictionary<string, object>
    {
        ["status_code"] = (int)response.StatusCode,
        ["bytes"]       = response.ContentLength,
    });
```

If you have a centralised HTTP client, install the wrap once at the client level and every call gets timed for free.

### Resource loading (Addressables)

```csharp
var opId = PlayScope.StartResourceLoad(addressableKey, new Dictionary<string, object>
{
    ["source"]                = "remote",
    ["dependency_count"]      = handles.Length,
    ["total_download_size_bytes"] = totalBytes,
});
var handle = Addressables.LoadAssetAsync<GameObject>(addressableKey);
await handle.Task;
PlayScope.EndResourceLoad(opId,
    handle.Status == AsyncOperationStatus.Succeeded
        ? OperationCompletionStatus.Success
        : OperationCompletionStatus.Failure);
```

### Scene loading with progress sampling

```csharp
var asyncOp = SceneManager.LoadSceneAsync("GameplayScene");
// Pass the AsyncOperation — the SDK polls .progress every 250 ms and
// stamps the samples into EndSceneLoad's metadata as
// scene_progress_samples. Visible on the dashboard as a 0 % → 100 % strip.
var opId = PlayScope.StartSceneLoad("GameplayScene", asyncOp);
await asyncOp;
PlayScope.EndSceneLoad(opId, OperationCompletionStatus.Success);
```

### Purchases — use `PurchaseMetadata` helpers

The dashboard's PurchaseDetails view surfaces a canonical schema as first-class fields. Build the dicts with `PurchaseMetadata.Build*Metadata` so you don't accidentally typo the key names:

```csharp
using PlayScopeSdk;

// Start
var startMeta = PurchaseMetadata.BuildStartMetadata(
    currency:    product.metadata.isoCurrencyCode,   // from Unity IAP
    priceAmount: product.metadata.localizedPrice);
var opId = PlayScope.StartPurchase(product.definition.id, startMeta);

// On store success
var endMeta = PurchaseMetadata.BuildEndMetadata(
    transactionId:    product.transactionID,
    validationStatus: PurchaseMetadata.ValidationStatus.Valid);
PlayScope.EndPurchase(opId, OperationCompletionStatus.Success, endMeta);

// On user cancel
var cancelMeta = PurchaseMetadata.BuildEndMetadata(
    failureReason: PurchaseMetadata.FailureReason.UserCancelled);
PlayScope.EndPurchase(opId, OperationCompletionStatus.Cancelled, cancelMeta);
```

`transactionId` is hashed (SHA-256, first 16 hex chars) by the helper before it leaves the device — the raw value never lands in metadata.

## 10. Track Caught Exceptions

```csharp
try
{
    await LoadUserProfile(userId);
}
catch (Exception ex)
{
    PlayScope.TrackException(ex, new Dictionary<string, object>
    {
        ["context"] = "profile_load",
        ["user_id"] = userId,
    });
    // Handle the failure UX — Show retry dialog, fallback to cache, etc.
}
```

Unhandled exceptions are captured automatically when `AutoCaptureUnityLogs = true`. Call `TrackException` explicitly when you want structured metadata on the exception or when the exception was actually handled (your `catch` block recovered) but the failure is still interesting to record.

## 11. In-Game Restart Marker

Call `TrackRestart` when the player logically discards their current profile — new-game flow, defeat reset, full progress wipe. The dashboard treats this as a boundary in the profile-state replay so the post-restart snapshot starts fresh.

```csharp
PlayScope.TrackRestart(
    reason: "new_game",
    metadata: new Dictionary<string, object>
    {
        ["from_level"] = playerData.Level,
        ["character"]  = "warrior",
    });
// Push a fresh post-restart snapshot — TrackRestart does NOT reset state on its own
PlayScope.SetInitialState(new Dictionary<string, object>
{
    ["level"]    = 1,
    ["currency"] = 0,
});
```

Common reason vocab: `"new_game"`, `"defeat_restart"`, `"settings_reset"`, `"daily_reset"`.

## 12. Push Game-Specific Session Data

`SetInitialState` / `UpdateState` carry the **player profile**. `UpdateSessionData` carries the **environment** — Addressables catalog version, disk usage, mod state, anything game-specific that's not part of the player's progress.

```csharp
PlayScope.UpdateSessionData(new Dictionary<string, object>
{
    ["addressables_catalog_version"] = catalog.Version,
    ["disk_free_mb"]                  = systemDisk.FreeMb,
    ["mod_count"]                     = enabledMods.Count,
}, reason: "boot_complete");
```

The SDK seeds basic session data automatically on `Initialize` (device model, OS version, system memory, graphics device). Use `UpdateSessionData` for everything that's specific to your game.

## Zenject / DI Integration

If using Zenject, wrap the SDK behind `IPlayScopeWrapperSystem` (available in the `PlayScopeWrapper` feature). The installer binds `PlayScopeWrapperSystem`, which initialises the SDK and logs every call for easier debugging.

```csharp
// In your scene installer
Container.Install<PlayScopeWrapperInstaller>();

// In development builds, also install the debug commands (psdk_* console commands)
#if DEVELOPMENT_BUILD
Container.Install<PlayScopeWrapperDebugFeatureInstaller>();
#endif
```

The wrapper exposes the same surface as `PlayScope.*` via `IPlayScopeWrapperSystem` so call sites get DI-friendly mocking for tests.

## Automatic Log Capture vs. Manual Tracking

|  | Auto Capture | Manual `TrackLog` |
|---|---|---|
| Setup | `AutoCaptureUnityLogs = true` | Call per log |
| Filtering | By `AutoCaptureMinLevel` | Explicit per call |
| Metadata | None | Structured metadata |
| Dedup | Yes (5 s window on info / warning / debug) | Yes (same buffer) |
| Best for | All warnings / errors globally | Business-logic events |

Both can be used together — auto-capture for the catch-all firehose, manual `TrackLog` for the half-dozen business events where structured metadata matters.

## What's automatic, what you have to call

| You don't have to call this — the SDK handles it | You DO have to call this |
|---|---|
| `session_start` / `session_end` | `SetInitialState`, `SetUserData` |
| Lifecycle (foreground / background / quit) | `SetScreen`, `TrackAction` |
| App version update detection | `UpdateState` / `UpdateSessionData` patches |
| First frame rendered / first input latency | `StartHTTP` / `EndHTTP` (and the other op pairs) |
| Periodic perf metrics (fps, frame-time p99, GC, memory, battery, network) | `TrackException` for caught exceptions |
| Network reachability changes | `TrackRestart` on player-initiated profile wipe |
| ANR detection (with `AnrDetectionEnabled = true`) | `Shutdown` is optional — auto-called on app quit |
| Memory warning capture (`Application.lowMemory`) |  |
| PII value scrubbing on metadata |  |
| Crash recovery & session resume |  |

If you don't see something on the dashboard you expected, check [Dashboard Navigator](dashboard-navigator.md) — most events live on specific tabs rather than the main timeline.
