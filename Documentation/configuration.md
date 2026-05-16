# Configuration

`PlayScopeContext` is the configuration object passed to `PlayScope.Initialize()`.

## Fields

### `ApiKey` (required)

```csharp
string ApiKey
```

Your project API key from the PlayScope dashboard. The SDK enters disabled mode (all calls become silent no-ops) if the key is missing or empty.

---

### `AutoCaptureUnityLogs`

```csharp
bool AutoCaptureUnityLogs = false
```

When `true`, the SDK subscribes to `Application.logMessageReceivedThreaded` and automatically tracks every Unity log as a `TrackLog` call. Filtered by `AutoCaptureMinLevel`.

---

### `AutoCaptureMinLevel`

```csharp
LogLevel AutoCaptureMinLevel = LogLevel.Warning
```

Minimum severity level for auto-captured Unity logs. Only applies when `AutoCaptureUnityLogs` is `true`.

| Value | Captures |
|---|---|
| `Debug` | All Unity logs |
| `Info` | Info, Warning, Error, Exception |
| `Warning` | Warning, Error, Exception |
| `Error` | Error, Exception only |
| `Exception` | Exceptions only |

---

### `UploadEndpoint`

```csharp
string UploadEndpoint = "https://api.playscope.io"
```

Base URL for the ingest API. Override for self-hosted deployments or staging environments.

---

### `Metadata`

```csharp
Dictionary<string, string> Metadata
```

Arbitrary key-value pairs attached to every session. Use for environment tags, app version, build number, etc.

Reserved key:

| Key | Description |
|---|---|
| `"environment"` | Session environment tag shown in the dashboard (`"production"` by default) |

## Example

```csharp
PlayScope.Initialize(new PlayScopeContext
{
    ApiKey               = "ps_live_xxxxxxxxxxxx",
    AutoCaptureUnityLogs = true,
    AutoCaptureMinLevel  = LogLevel.Warning,
    UploadEndpoint       = "https://api.playscope.io",
    Metadata = new Dictionary<string, string>
    {
        ["environment"] = "production",
        ["app_version"] = Application.version,
        ["build_number"] = "42"
    }
});
```

## Sensitive Key Filtering

The SDK automatically strips keys that look like credentials from all metadata and state dictionaries before writing to disk. Filtered key patterns include `password`, `token`, `secret`, `api_key`, `auth`, `credential`, and similar.
