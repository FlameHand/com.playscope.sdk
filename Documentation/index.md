# PlayScope SDK

PlayScope is a session recording and diagnostics SDK for Unity games. It captures player sessions — screens, actions, operations, state, logs, and exceptions — persists them locally, and uploads them to the PlayScope platform for analysis.

## Requirements

- Unity 2021.3 LTS or later
- [UniTask](https://github.com/Cysharp/UniTask)

## Installation

### Via Unity Package Manager (Git URL)

1. Open **Window → Package Manager**
2. Click **＋ → Add package from git URL…**
3. Enter:

```
git@github.com:FlameHand/com.playscope.sdk.git#v0.1.2
```

### Via manifest.json

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.playscope.sdk": "git@github.com:FlameHand/com.playscope.sdk.git#v0.1.2"
  }
}
```

## Quick Start

```csharp
// 1. Initialize once at app start
PlayScope.Initialize(new PlayScopeContext
{
    ApiKey             = "your-api-key",
    AutoCaptureUnityLogs = true,
    AutoCaptureMinLevel  = LogLevel.Warning
});

// 2. Identify the user (after login)
PlayScope.SetUserData("user-123", new Dictionary<string, object>
{
    ["plan"] = "premium"
});

// 3. Set initial game state
PlayScope.SetInitialState(new Dictionary<string, object>
{
    ["level"] = 1,
    ["currency"] = 500
});

// 4. Track screens and actions
PlayScope.SetScreen("MainMenu");
PlayScope.TrackAction("TapPlayButton");

// 5. Update state incrementally
PlayScope.UpdateState(new Dictionary<string, object>
{
    ["level"] = 2,
    ["currency"] = 350
});
```

## Topics

- [API Reference](api-reference.md)
- [Integration Guide](integration-guide.md)
- [Configuration](configuration.md)
- [Changelog](changelog.md)
