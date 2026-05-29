# API Reference

All methods are on the `PlayScope` static class in the `PlayScopeSdk` namespace. Every method is thread-safe and never throws — calls while the SDK is uninitialised or disabled are silent no-ops, return values default to `string.Empty` / `void`.

---

## Initialisation

### `PlayScope.Initialize()`

Preferred entry point. Reads the `PlayScopeSettings` asset from `Resources/PlayScopeSettings.asset` (created via the **PlayScope ▸ Settings** Editor menu) and initialises from it. Must be called once before any other method. Subsequent calls emit a warning and no-op — the first call wins. A missing asset or empty key leaves the SDK disabled (all calls become silent no-ops).

```csharp
PlayScope.Initialize();
```

### `PlayScope.Initialize(PlayScopeContext context)`

Initialise with an explicit context — use when you select the key at runtime or attach custom session metadata.

```csharp
PlayScope.Initialize(new PlayScopeContext
{
    SdkKey               = "ps_live_xxxxxxxxxxxx",
    AutoCaptureUnityLogs = true,
    AutoCaptureMinLevel  = LogLevel.Warning,
    Metadata = new Dictionary<string, object>
    {
        ["environment"]  = "production",
        ["build_number"] = "421",
    },
});
```

See [Configuration](configuration.md) for all `PlayScopeContext` fields.

### `PlayScope.IsInitialized` / `PlayScope.IsDisabled`

Read-only flags. `IsInitialized` is true once every subsystem is wired up; `IsDisabled` is true after a permanent self-disable (missing/empty key, missing asset, partial-init failure). Probe them after `Initialize` if you want to surface the result to your own boot pipeline.

### `PlayScope.Settings`

The loaded `PlayScopeSettings` asset (or `null`). Lets wrapper / game code mirror `MinLogLevel` in its own logger without re-loading Resources.

> There is **no** public `Shutdown()` method. The SDK flushes buffered events and emits the final `session_end` automatically on `OnApplicationQuit` — you never call it.

---

## Identity

### `PlayScope.SetUserData(string userId, IReadOnlyDictionary<string, object> metadata = null)`

Associates the current session with a user identity. Safe to call multiple times — each call updates the current identity.

| Parameter | Description |
|---|---|
| `userId`   | Your application-side user identifier. |
| `metadata` | Optional key-value attributes (e.g. plan, region). Sensitive keys / values are filtered automatically. |

```csharp
PlayScope.SetUserData("user-123", new Dictionary<string, object>
{
    ["plan"]   = "premium",
    ["region"] = "eu-west",
});
```

---

## State (player profile)

### `PlayScope.SetInitialState(IReadOnlyDictionary<string, object> state)`

Sets the full initial game-state snapshot for the session. Calling it a second time without an intervening `TrackRestart` emits a warning and is ignored — use `UpdateState` for incremental changes.

```csharp
PlayScope.SetInitialState(new Dictionary<string, object>
{
    ["level"]    = 5,
    ["currency"] = 1200,
    ["items"]    = new List<object> { "sword", "shield" },
});
```

### `PlayScope.UpdateState(IReadOnlyDictionary<string, object> patch, string reason = null)`

Applies a partial patch to the current game state. Only the provided keys are updated; unmentioned keys remain unchanged. Patches within a 100 ms window are coalesced by the SDK so a flurry of small updates collapses into one event.

```csharp
PlayScope.UpdateState(new Dictionary<string, object> { ["currency"] = 950 });

// With a reason — surfaces in the dashboard as the "why" column
PlayScope.UpdateState(new Dictionary<string, object> { ["level"] = 6 }, reason: "level_up");
```

### `PlayScope.TrackRestart(string reason = null, IReadOnlyDictionary<string, object> metadata = null)`

In-game restart marker — call when the player discards their current profile (new game, defeat reset, full progress wipe). The dashboard treats this as a boundary for profile-state replay: state shown after a restart starts from the *next* `SetInitialState`.

```csharp
PlayScope.TrackRestart(reason: "new_game", metadata: new Dictionary<string, object>
{
    ["from_level"] = playerData.Level,
});
// Push a fresh post-restart snapshot:
PlayScope.SetInitialState(new Dictionary<string, object> { ["level"] = 1, ["currency"] = 0 });
```

---

## Session data (environment / device)

A parallel stream to profile state — same `initial + patch` protocol, different semantic. Session data carries the *environment* (device model, OS version, addressables version, disk free, memory budget), profile state carries *the player*. Reviewers see them on different dashboard tabs.

### `PlayScope.UpdateSessionData(IReadOnlyDictionary<string, object> patch, string reason = null)`

```csharp
PlayScope.UpdateSessionData(new Dictionary<string, object>
{
    ["addressables_catalog_version"] = "1.4.2",
    ["disk_free_mb"]                  = 8421,
}, reason: "catalog_loaded");
```

The SDK seeds session data on init with `device_model`, `os_version`, `system_memory_mb`, `graphics_device_name`, etc. — call `UpdateSessionData` for anything game-specific.

---

## Navigation

### `PlayScope.SetScreen(string screenName, IReadOnlyDictionary<string, object> metadata = null)`

```csharp
PlayScope.SetScreen("GameplayHUD");
PlayScope.SetScreen("ShopScreen", new Dictionary<string, object> { ["source"] = "main_menu" });
```

### `PlayScope.TrackAction(string actionName, IReadOnlyDictionary<string, object> metadata = null)`

```csharp
PlayScope.TrackAction("TapPlayButton");
PlayScope.TrackAction("UseHealthPotion", new Dictionary<string, object>
{
    ["potion_type"] = "small",
    ["hp_before"]   = 45,
});
```

---

## Operations

Operations are timed spans with a start and an end. Returns an opaque ID from `Start*`; pass it to the matching `End*`.

### `PlayScope.StartOperation(OperationType type, string operationName, ...) → string`
### `PlayScope.CompleteOperation(string operationId, OperationCompletionStatus status, ...)`

```csharp
var opId = PlayScope.StartOperation(OperationType.Custom, "LoadSaveData");
// ... do work ...
PlayScope.CompleteOperation(opId, OperationCompletionStatus.Success);
```

### Typed shortcuts

| Start | End | OperationType |
|---|---|---|
| `StartHTTP(name, metadata)` | `EndHTTP(id, status, metadata)` | `HTTP` |
| `StartResourceLoad(name, metadata)` | `EndResourceLoad(id, status, metadata)` | `ResourceLoad` |
| `StartSceneLoad(sceneName[, asyncOp], metadata)` | `EndSceneLoad(id, status, metadata)` | `SceneLoad` |
| `StartPurchase(productId, metadata)` | `EndPurchase(id, status, metadata)` | `Purchase` |
| `StartAd(placement, metadata)` | `EndAd(id, status, metadata)` | `Ad` |

`RecordSceneLoadProgress(id, progress)` pushes an explicit progress reading for an in-flight scene/resource load when you poll progress on your own loop instead of handing the `AsyncOperation` to `StartSceneLoad`.

### HTTP example

```csharp
var id = PlayScope.StartHTTP("GET /api/leaderboard", new Dictionary<string, object>
{
    ["method"] = "GET",
    ["url"]    = "/api/leaderboard",
});
var result = await httpClient.GetAsync(url);
PlayScope.EndHTTP(id,
    result.IsSuccessStatusCode
        ? OperationCompletionStatus.Success
        : OperationCompletionStatus.Failure,
    new Dictionary<string, object>
    {
        ["status_code"] = (int)result.StatusCode,
    });
```

### Scene load with progress sampling

```csharp
var asyncOp = SceneManager.LoadSceneAsync("GameplayScene");
asyncOp.allowSceneActivation = false;
// Pass the AsyncOperation — the SDK polls .progress every 250 ms on
// the main thread and stamps the samples into the matching EndSceneLoad
// as scene_progress_samples — visible on the dashboard as a 0 % → 100 %
// strip.
var opId = PlayScope.StartSceneLoad("GameplayScene", asyncOp);
await asyncOp;
PlayScope.EndSceneLoad(opId, OperationCompletionStatus.Success);
```

### Purchase with canonical metadata

Use `PurchaseMetadata.BuildStartMetadata` / `BuildEndMetadata` to construct dicts with the canonical schema the dashboard surfaces as first-class fields:

```csharp
using PlayScopeSdk;

// Start — store, currency, price (store auto-detects from Application.platform)
var startMeta = PurchaseMetadata.BuildStartMetadata(
    currency:    "USD",
    priceAmount: 4.99m);
var opId = PlayScope.StartPurchase(product.id, startMeta);

// ... store callback resolves ...

// End — transaction id is SHA-256-16 hashed by the helper before leaving the device
var endMeta = PurchaseMetadata.BuildEndMetadata(
    transactionId:    productReceipt.TransactionId,
    validationStatus: PurchaseMetadata.ValidationStatus.Valid);
PlayScope.EndPurchase(opId, OperationCompletionStatus.Success, endMeta);
```

Canonical schema:
- **start:** `store` (auto: `app_store` / `google_play` / `steam` / `amazon` / `other`), `currency`, `price_amount`, `is_restore`
- **end:** `transaction_id_hash` (sha256-16), `validation_status` (`pending` / `valid` / `invalid` / `error`), `failure_reason` (`user_cancelled` / `payment_declined` / `network_error` / `validation_failed` / …)

### Ad impression with canonical metadata

Call `StartAd` when an ad load/show begins and `EndAd` when it resolves. Build the dicts with `AdMetadata.BuildStartMetadata` / `BuildEndMetadata` — they feed the dashboard's **Revenue** page (IAP vs ads split) and the crash-during-ad correlation on **Errors**. Available on all plan tiers.

```csharp
using PlayScopeSdk;

// Start — placement is the operation name AND echoed into metadata.placement.
var startMeta = AdMetadata.BuildStartMetadata(
    network:   AdMetadata.Network.AdMob,
    placement: "Rewarded_GameOver_v3",
    adType:    AdMetadata.AdType.Rewarded);
var opId = PlayScope.StartAd("Rewarded_GameOver_v3", startMeta);

// End — negative revenue is clamped to 0 by the helper.
PlayScope.EndAd(opId, OperationCompletionStatus.Success,
    AdMetadata.BuildEndMetadata(
        result:   AdMetadata.AdResult.Rewarded,
        revenue:  0.0142,
        currency: "USD"));
```

Canonical schema:
- **start:** `network` (`AdMetadata.Network`: `admob` / `unity_ads` / `ironsource` / `applovin` / …), `placement`, `ad_type` (`interstitial` / `rewarded` / `banner` / `app_open` / `native`)
- **end:** `result` (`shown` / `rewarded` / `skipped` / `closed` / `failed` / `no_fill` / …), `revenue`, `currency`

> Don't embed user-identifiable data in `placement` strings or `productId` values — they're stored verbatim and rendered in dashboard UI. Templated forms like `Rewarded_GameOver_{level}` are fine.

---

## Logging

### `PlayScope.TrackLog(LogLevel level, string message, IReadOnlyDictionary<string, object> metadata = null)`

Manually tracks a log entry. Use when `AutoCaptureUnityLogs` is disabled or you need structured metadata alongside the log.

```csharp
PlayScope.TrackLog(LogLevel.Warning, "Inventory full — item dropped",
    new Dictionary<string, object> { ["item"] = "health_potion" });
```

Identical `(level, message)` pairs within a 5 s window are collapsed by the SDK's `LogDedupBuffer` and the resulting timeline row carries `repeat_count: N`. Error and exception levels bypass dedup entirely.

### `PlayScope.TrackException(Exception exception, IReadOnlyDictionary<string, object> metadata = null)`

Tracks a caught exception. Does not rethrow.

```csharp
try
{
    LoadUserProfile();
}
catch (Exception ex)
{
    PlayScope.TrackException(ex, new Dictionary<string, object>
    {
        ["context"] = "profile_load",
        ["user_id"] = currentUserId,
    });
}
```

---

## Emitted automatically

These events fire without API calls — listed here so you know what to expect on the dashboard.

| Event | Trigger | Carries |
|---|---|---|
| `session_start` | `Initialize` | app_version, platform, device_model, os_version, sdk_version |
| `session_end` | `OnApplicationQuit`, or a clean 5-min background timeout | end_status: `normal` / `background_timeout` |
| `session_abnormal_end` | Recovery on next launch when prior session didn't emit `session_end` | end_status: `abnormal` |
| `lifecycle` | Foreground / background transitions | transition, duration_in_prev_state_ms |
| `app_update_detected` | First launch on a new `Application.version` | from_version, to_version |
| `first_frame_rendered` | First `Update()` after `Initialize` | ms_since_session_start |
| `first_input_latency` | First touch / mouse / key / gamepad after `first_frame_rendered` | latency_ms, input_kind |
| `network_change` | `Application.internetReachability` flips | from, to |
| `anr` | Main thread blocked > `AnrThresholdMs` | stuck_for_ms, threshold_ms, started_at |
| `anr_recovered` | Main thread resumes after an `anr` | total_stuck_ms |
| `memory_warning` | `Application.lowMemory` fires | heap_mb, reserved_mb, system_mb |

Plus a periodic metric stream sampled by `MetricsSampler`:

| Metric | Cadence | Source |
|---|---|---|
| `fps` | 1 s | rolling average |
| `frame_time_p99_ms` | 1 s | ring buffer sorted to p99 |
| `dropped_frames_count` | 1 s | frames > 33.4 ms in the last second |
| `gc_alloc_kb` | 1 s | `Profiler.GetTotalAllocatedMemoryLong()` delta |
| `memory_heap` | 5 s | `GC.GetTotalMemory(false)` |
| `memory_unity_reserved` | 5 s | `Profiler.GetTotalReservedMemoryLong()` |
| `battery_level` | 10 s | `SystemInfo.batteryLevel` |
| `is_charging` | on change | `SystemInfo.batteryStatus` (0 / 1) |
| `thermal_state` | 10 s | `UnityEngine.Device.SystemInfo.thermalStatus` (enum 0–6; Unity 2023.1+ only) |
| `available_disk_mb` | on change (≥ 5 MB) | native bridge; omitted where unsupported |
| `system_free_ram_mb` | 10 s | native bridge |
| `network_reachability` | on change | `Application.internetReachability` (0 none / 1 carrier / 2 wifi) |

---

## Enums

### `OperationType`

| Value | Description |
|---|---|
| `Custom` | Any application-defined operation |
| `HTTP` | Network request |
| `ResourceLoad` | Asset or bundle load |
| `SceneLoad` | Unity scene load |
| `Purchase` | In-app purchase flow |
| `Ad` | Ad-impression flow (rewarded / interstitial / banner) |

### `OperationCompletionStatus`

| Value | Description |
|---|---|
| `Success` | Operation completed successfully |
| `Failure` | Operation failed with an error |
| `Cancelled` | Operation was cancelled by the user or system |
| `Timeout` | Operation exceeded its time budget |
| `Abandoned` | Operation was started but never explicitly ended |

### `LogLevel`

| Value | Description |
|---|---|
| `Debug` | Verbose diagnostic information |
| `Info` | General informational messages |
| `Warning` | Recoverable unexpected conditions |
| `Error` | Errors that affect functionality |
| `Exception` | Unhandled or caught exceptions |
