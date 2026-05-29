# Configuration

Most projects never construct a `PlayScopeContext` by hand. Create the
**`PlayScopeSettings`** asset via the **PlayScope ▸ Settings** Editor menu
(it writes `Assets/Resources/PlayScopeSettings.asset`), paste your SDK key,
and call the parameterless `PlayScope.Initialize()` — the SDK builds the
context from the asset for you.

Pass an explicit `PlayScopeContext` to `PlayScope.Initialize(context)` only
when you need to pick the key at runtime or attach custom session metadata.
The fields below apply to both paths (the asset field name is noted where it
differs). Every field has a sensible default — the only one that's truly
required is `SdkKey`.

## Fields

### `SdkKey` *(required)*

```csharp
string SdkKey
```

Your project's SDK key from the PlayScope dashboard (**Settings → Projects**),
of the form `ps_live_…`. The SDK enters disabled mode (all calls become silent
no-ops) if the key is missing or empty.

> Renamed from `ApiKey` for parity with the dashboard. The old `ApiKey` name
> is kept as an `[Obsolete]` alias so existing initialisers keep compiling.
> In the settings asset this field is also named `SdkKey`.

---

### `AutoCaptureUnityLogs`

```csharp
bool AutoCaptureUnityLogs = false
```

When `true`, the SDK subscribes to `Application.logMessageReceivedThreaded` and automatically tracks every Unity log as a `TrackLog` call. Filtered by `AutoCaptureMinLevel`. The SDK itself never emits captured logs of its own (recursion guard on `[PlayScope]` prefix).

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

> Info and below are passed through the SDK's `LogDedupBuffer` — identical (level + message) pairs that repeat within a 5 s window collapse into a single row carrying `repeat_count: N`. Errors and exceptions bypass dedup entirely (each one matters individually).

---

### `AnrDetectionEnabled`

```csharp
bool AnrDetectionEnabled = true
```

Enables the main-thread ANR watchdog. The watchdog records a heartbeat in `MonoBehaviour.Update()` and a thread-pool timer fires `anr` when the heartbeat hasn't ticked for `AnrThresholdMs`. On recovery it emits `anr_recovered` with the total stuck duration.

Auto-disables in the Unity Editor (breakpoints would cause false positives) unless running in batch mode. Set to `false` to opt out entirely.

---

### `AnrThresholdMs`

```csharp
int AnrThresholdMs = 2000
```

Milliseconds before a main-thread stall is reported as an ANR. Default 2 s — same threshold Android uses for its OS-level ANR dialog. Tune up to 5000 ms for less-noisy reports on GC-heavy games; tune down to 1000 ms on hard-60-fps games where any visible hitch is critical.

Ignored when `AnrDetectionEnabled` is `false` or when running in the Editor without batch mode.

---

### `PiiValueMasksEnabled`

```csharp
bool PiiValueMasksEnabled = true
```

Enables value-level PII regex scrubbing on metadata and state values, in addition to the always-on key-name filter. When `true`, string values are scanned for:

- Email addresses (`local@host.tld`)
- JWTs (`eyJ…`-anchored, three-segment tokens)
- Bearer / Basic / Token authorization headers
- Well-known service tokens (`ghp_`, `sk_live_`, `sk_test_`, `xoxb_`, `AKIA`, etc.)
- Luhn-validated credit-card numbers (13–19 digits — `4242 4242 4242 4242` style)
- International phone numbers (`+` country code prefix + digits)
- Public IPv4 addresses (private ranges deliberately kept — they're plumbing, not PII)

Matches are replaced *in-line* with placeholders like `[redacted-email]` — surrounding context survives. The first scrub in a session emits a one-time `[PlayScope] PII value-level mask triggered` warning so the integrator knows the filter fired.

> Default `true`. Disabling exposes you to GDPR / CCPA risk if user data ever leaks into metadata — leave on unless you have a compelling reason (e.g. you're testing the masks themselves in CI).

---

### `UploadEndpoint`

```csharp
string UploadEndpoint = "https://api.playscope.dev"
```

Base URL for the ingest API. Override for self-hosted deployments or staging environments. (In the settings asset this field is named `BackendUrl`.)

---

### `Metadata`

```csharp
IReadOnlyDictionary<string, object> Metadata
```

Arbitrary key-value pairs attached to the **session** (carried inside `session_start`). Use for environment tags, build number, etc. `app_version` / `platform` / `device_model` / `os_version` are collected automatically, so this is for *additions*. Not available via the settings asset — use the `Initialize(context)` overload when you need custom metadata.

Reserved key:

| Key | Description |
|---|---|
| `"environment"` | Session environment tag shown in the dashboard (`"production"` by default) |

## Full example

```csharp
PlayScope.Initialize(new PlayScopeContext
{
    SdkKey               = "ps_live_xxxxxxxxxxxx",

    // Logs — capture warnings and above, dedup chatty repeats
    AutoCaptureUnityLogs = true,
    AutoCaptureMinLevel  = LogLevel.Warning,

    // ANR — Android-default threshold
    AnrDetectionEnabled  = true,
    AnrThresholdMs       = 2000,

    // Privacy — default on, document opt-out explicitly if you must
    PiiValueMasksEnabled = true,

    // Self-hosted deployment? Override the ingest URL
    UploadEndpoint       = "https://api.playscope.dev",

    Metadata = new Dictionary<string, object>
    {
        ["environment"]   = Debug.isDebugBuild ? "development" : "production",
        ["build_number"]  = "42",
    },
});
```

## Sensitive Key Filtering

In addition to the value-level masks above, the SDK strips entire keys whose name looks credential-shaped from all metadata and state dictionaries. Filtered substrings (case-insensitive): `password`, `passwd`, `secret`, `token`, `apikey`, `api_key`, `authtoken`, `auth_token`, `authorization`, `accesstoken`, `access_token`, `refreshtoken`, `refresh_token`, `privatekey`, `private_key`, `creditcard`, `credit_card`, `cardnumber`, `card_number`, `cvv`, `ssn`.

Exception: `operation_name` is whitelisted — it contains "name" but isn't sensitive.

The first time a sensitive key is dropped in a session, the SDK emits a one-time warning so the integrator knows to fix the call site rather than rely on the filter.
