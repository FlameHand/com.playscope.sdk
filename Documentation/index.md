# PlayScope SDK

PlayScope is a session-recording and diagnostics SDK for Unity games. It captures full player sessions — screens, actions, operations, state, logs, exceptions, performance metrics, ANR and memory pressure events — persists them locally on disk, and uploads them to the PlayScope platform for inspection in the dashboard.

## What you get

| Signal | Captured automatically | Manual API |
|---|---|---|
| **Lifecycle** — session_start / session_end, foreground / background transitions, app updates, first frame, first input | ✅ | — |
| **Screens** | — | `SetScreen()` |
| **Actions** | — | `TrackAction()` |
| **Operations** — HTTP / asset load / scene load / purchases / ads | — | `StartHTTP` / `StartResourceLoad` / `StartSceneLoad` / `StartPurchase` / `StartAd` |
| **Monetisation** — IAP + ad-impression revenue | — | `PurchaseMetadata` / `AdMetadata` helpers |
| **State** — full profile snapshot + incremental patches | — | `SetInitialState()` + `UpdateState()` |
| **Session data** — device, OS, addressables, disk, memory | ✅ | `UpdateSessionData()` for game-specific extras |
| **Logs / exceptions** | ✅ via `AutoCaptureUnityLogs` | `TrackLog()` / `TrackException()` |
| **Crashes & ANR** — main-thread stalls > 2 s | ✅ | — |
| **Memory pressure** — `Application.lowMemory` (Android `onTrimMemory` + iOS memory warning) | ✅ | — |
| **Perf metrics** — fps, frame-time p99, dropped frames, GC alloc/s, heap, battery, charging, thermal state, free disk, free RAM, network reachability | ✅ | — |
| **Privacy** — value-level PII masking (emails, JWTs, cards, phones, tokens, IPs) | ✅ | toggle via `PiiValueMasksEnabled` |

## Requirements

- Unity 2021.3 LTS or later
- [UniTask](https://github.com/Cysharp/UniTask)

## Installation

### Via Unity Package Manager (Git URL)

1. Open **Window → Package Manager**
2. Click **＋ → Add package from git URL…**
3. Enter:

```
https://github.com/FlameHand/com.playscope.sdk.git#v0.6.4
```

Pin to a specific tag (recommended). The SDK auto-versions on every change to `main` — `v0.6.4` is the current release at time of writing; check [GitHub Releases](https://github.com/FlameHand/com.playscope.sdk/releases) for the latest tag.

### Via manifest.json

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.playscope.sdk": "https://github.com/FlameHand/com.playscope.sdk.git#v0.6.4"
  }
}
```

## 60-second quick start

```csharp
using PlayScopeSdk;
using UnityEngine;
using System.Collections.Generic;

public class AppBootstrapper : MonoBehaviour
{
    private void Awake()
    {
        // 1. Initialize — once, as early as possible. Reads
        //    Resources/PlayScopeSettings.asset (created via the
        //    PlayScope ▸ Settings Editor menu — paste your SDK key there).
        PlayScope.Initialize();

        // 2. Identify the user once you know who they are
        PlayScope.SetUserData(user.Id, new Dictionary<string, object>
        {
            ["plan"]  = user.Plan,
            ["is_guest"] = user.IsGuest,
        });

        // 3. Push the full game-state snapshot ONCE
        PlayScope.SetInitialState(new Dictionary<string, object>
        {
            ["level"]    = 1,
            ["currency"] = 500,
        });

        // 4. Track screens + actions as the player navigates
        PlayScope.SetScreen("MainMenu");
        PlayScope.TrackAction("TapPlayButton");

        // 5. Patch state incrementally — only the keys that changed
        PlayScope.UpdateState(new Dictionary<string, object> { ["level"] = 2 });
    }
}
```

Everything else (crash capture, ANR detection, perf metrics, lifecycle events, log capture, automatic operation timing) just works once `Initialize` has been called.

## Topics

- [Integration Guide](integration-guide.md) — step-by-step setup for a new project
- [API Reference](api-reference.md) — every public method
- [Configuration](configuration.md) — every `PlayScopeContext` field
- [Dashboard Navigator](dashboard-navigator.md) — where to read what once data is flowing
- [Changelog](changelog.md)
