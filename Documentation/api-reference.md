# API Reference

All methods are on the `PlayScope` static class in the `PlayScopeSdk` namespace. Every method is thread-safe and never throws — calls while the SDK is uninitialised or disabled are silent no-ops.

---

## Initialisation

### `PlayScope.Initialize(PlayScopeContext context)`

Initialises the SDK. Must be called once before any other method. Subsequent calls are a warning and no-op — the first call wins.

```csharp
PlayScope.Initialize(new PlayScopeContext
{
    ApiKey               = "ps_live_xxxxxxxxxxxx",
    AutoCaptureUnityLogs = true,
    AutoCaptureMinLevel  = LogLevel.Warning,
    Metadata = new Dictionary<string, string>
    {
        ["environment"] = "production",
        ["app_version"] = Application.version
    }
});
```

See [Configuration](configuration.md) for all `PlayScopeContext` fields.

---

## Identity

### `PlayScope.SetUserData(string userId, IReadOnlyDictionary<string, object> metadata = null)`

Associates the current session with a user identity. Safe to call multiple times — each call updates the current identity.

| Parameter  | Description |
|---|---|
| `userId`   | Your application-side user identifier. |
| `metadata` | Optional key-value attributes (e.g. plan, region). Sensitive keys are filtered automatically. |

```csharp
PlayScope.SetUserData("user-123", new Dictionary<string, object>
{
    ["plan"]   = "premium",
    ["region"] = "eu-west"
});
```

---

## State

### `PlayScope.SetInitialState(IReadOnlyDictionary<string, object> state)`

Sets the full initial game state snapshot for the session. The second call is a warning and ignored — use `UpdateState` for incremental changes after the initial snapshot.

```csharp
PlayScope.SetInitialState(new Dictionary<string, object>
{
    ["level"]    = 5,
    ["currency"] = 1200,
    ["items"]    = new List<object> { "sword", "shield" }
});
```

### `PlayScope.UpdateState(IReadOnlyDictionary<string, object> patch)`

Applies a partial patch to the current game state. Only the provided keys are updated; unmentioned keys remain unchanged.

```csharp
PlayScope.UpdateState(new Dictionary<string, object>
{
    ["currency"] = 950   // only currency changes
});
```

---

## Navigation

### `PlayScope.SetScreen(string screenName, IReadOnlyDictionary<string, object> metadata = null)`

Records a screen or scene navigation event.

```csharp
PlayScope.SetScreen("GameplayHUD");
PlayScope.SetScreen("ShopScreen", new Dictionary<string, object>
{
    ["source"] = "main_menu"
});
```

---

## Actions

### `PlayScope.TrackAction(string actionName, IReadOnlyDictionary<string, object> metadata = null)`

Records a discrete player action.

```csharp
PlayScope.TrackAction("TapPlayButton");
PlayScope.TrackAction("UseHealthPotion", new Dictionary<string, object>
{
    ["potion_type"] = "small",
    ["hp_before"]   = 45
});
```

---

## Operations

Operations are timed spans with a start and an end.

### `PlayScope.StartOperation(OperationType type, string operationName, IReadOnlyDictionary<string, object> metadata = null) → string`

Starts a timed operation. Returns an opaque operation ID (empty string when SDK is disabled).

### `PlayScope.CompleteOperation(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null)`

Completes a previously started operation.

```csharp
var opId = PlayScope.StartOperation(OperationType.Custom, "LoadSaveData");
// ... do work ...
PlayScope.CompleteOperation(opId, OperationCompletionStatus.Success);
```

### Typed Shortcuts

| Start | End | OperationType |
|---|---|---|
| `StartHTTP(name, metadata)` | `EndHTTP(id, status, metadata)` | `HTTP` |
| `StartResourceLoad(name, metadata)` | `EndResourceLoad(id, status, metadata)` | `ResourceLoad` |
| `StartSceneLoad(sceneName, metadata)` | `EndSceneLoad(id, status, metadata)` | `SceneLoad` |
| `StartPurchase(productId, metadata)` | `EndPurchase(id, status, metadata)` | `Purchase` |

```csharp
// HTTP example
var id = PlayScope.StartHTTP("GET /api/leaderboard");
var result = await httpClient.GetAsync(url);
PlayScope.EndHTTP(id, result.IsSuccessStatusCode
    ? OperationCompletionStatus.Success
    : OperationCompletionStatus.Failure,
    new Dictionary<string, object> { ["status_code"] = (int)result.StatusCode });

// Scene load example
var sceneOpId = PlayScope.StartSceneLoad("GameplayScene");
await SceneManager.LoadSceneAsync("GameplayScene");
PlayScope.EndSceneLoad(sceneOpId, OperationCompletionStatus.Success);
```

---

## Logging

### `PlayScope.TrackLog(LogLevel level, string message, IReadOnlyDictionary<string, object> metadata = null)`

Manually tracks a log entry. Use when `AutoCaptureUnityLogs` is disabled or you need structured metadata alongside the log.

```csharp
PlayScope.TrackLog(LogLevel.Warning, "Inventory full — item dropped",
    new Dictionary<string, object> { ["item"] = "health_potion" });
```

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
        ["user_id"] = currentUserId
    });
}
```

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
