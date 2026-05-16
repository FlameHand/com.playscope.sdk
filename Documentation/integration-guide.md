# Integration Guide

Step-by-step guide for integrating PlayScope SDK into a Unity project.

## 1. Install the Package

Add to `Packages/manifest.json`:

```json
"com.playscope.sdk": "git@github.com:FlameHand/com.playscope.sdk.git#v0.1.2"
```

Or use **Window → Package Manager → ＋ → Add package from git URL**.

## 2. Get Your SDK Key

1. Log in to [playscope.dev](https://playscope.dev)
2. Open **Settings → Projects**
3. Copy the SDK key for your project (`ps_live_...`) — each project has one key generated automatically

> To invalidate a compromised key, click **Rotate** next to the project. Update all clients before the old key stops working.

## 3. Initialize the SDK

Initialize as early as possible — before any gameplay code runs. A good location is an `Awake()` method on a `DontDestroyOnLoad` bootstrapper, or the first `IInitializable` in your DI container.

```csharp
using PlayScopeSdk;
using UnityEngine;

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
                ["environment"] = Debug.isDebugBuild ? "development" : "production",
                ["app_version"] = Application.version
            }
        });
    }
}
```

> Never hardcode your API key in source files. Store it in a `ScriptableObject`, `Resources` asset, or remote config and pass it at runtime.

## 4. Identify Users

Call `SetUserData` as soon as the user logs in or their identity is known.

```csharp
PlayScope.SetUserData(user.Id, new Dictionary<string, object>
{
    ["plan"]     = user.SubscriptionPlan,
    ["region"]   = user.Region,
    ["is_guest"] = user.IsGuest
});
```

## 5. Set Initial Game State

Call `SetInitialState` once after the player data is loaded, before the first scene transition.

```csharp
PlayScope.SetInitialState(new Dictionary<string, object>
{
    ["level"]      = playerData.Level,
    ["currency"]   = playerData.Currency,
    ["total_runs"] = playerData.TotalRuns
});
```

## 6. Track Screens

Call `SetScreen` on every significant view transition.

```csharp
// In your scene/screen loading code
PlayScope.SetScreen("MainMenu");
PlayScope.SetScreen("GameplayHUD");
PlayScope.SetScreen("DeathScreen");
```

## 7. Track Actions

Track meaningful player decisions — button presses, purchases, item usage.

```csharp
PlayScope.TrackAction("TapPlayButton");
PlayScope.TrackAction("PurchaseItem", new Dictionary<string, object>
{
    ["item_id"]    = itemId,
    ["item_price"] = price
});
```

## 8. Update State on Changes

Patch only the keys that changed — avoid re-sending the full state.

```csharp
// After the player spends currency
PlayScope.UpdateState(new Dictionary<string, object>
{
    ["currency"] = newCurrencyValue
});

// After the player levels up
PlayScope.UpdateState(new Dictionary<string, object>
{
    ["level"]    = newLevel,
    ["xp"]       = 0   // reset xp after level-up
});
```

## 9. Track Operations

Use operations to time any async work that matters for performance analysis.

```csharp
// Asset loading
var opId = PlayScope.StartResourceLoad("BundleName");
var handle = Addressables.LoadAssetAsync<GameObject>("BundleName");
await handle.Task;
PlayScope.EndResourceLoad(opId,
    handle.Status == AsyncOperationStatus.Succeeded
        ? OperationCompletionStatus.Success
        : OperationCompletionStatus.Failure);

// Network calls
var httpId = PlayScope.StartHTTP("POST /api/save-progress");
var response = await api.SaveProgressAsync(data);
PlayScope.EndHTTP(httpId, response.IsSuccess
    ? OperationCompletionStatus.Success
    : OperationCompletionStatus.Failure,
    new Dictionary<string, object> { ["status_code"] = response.StatusCode });
```

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
        ["user_id"] = userId
    });
    // handle gracefully
}
```

## Zenject / DI Integration

If using Zenject, wrap the SDK behind `IPlayScopeWrapperSystem` (available in the `PlayScopeWrapper` feature for projects that use this project's architecture). The installer binds `PlayScopeWrapperSystem`, which initialises the SDK and logs every call for easier debugging.

```csharp
// In your scene installer
Container.Install<PlayScopeWrapperInstaller>();

// In development builds, also install the debug commands
#if DEVELOPMENT_BUILD
Container.Install<PlayScopeWrapperDebugFeatureInstaller>();
#endif
```

## Automatic Log Capture vs. Manual Tracking

| | Auto Capture | Manual `TrackLog` |
|---|---|---|
| Setup | `AutoCaptureUnityLogs = true` | Call per log |
| Filtering | By `AutoCaptureMinLevel` | Explicit per call |
| Metadata | None | Structured metadata |
| Best for | All warnings/errors globally | Business-logic events |

Both can be used together.
