# Dashboard Navigator

A field guide to **where to read what** on the PlayScope dashboard. Once events are flowing, this answers "I tracked it, where do I find it now?"

## Top-level pages

The left sidebar gives you four pages per project:

| Page | Purpose | When to open |
|---|---|---|
| **Dashboard** | Per-project overview — health summary, top errors, recent abnormal sessions | First check after a deployment |
| **Sessions** | List of all sessions for the project, filterable by date / user / status | Looking for a specific user's session |
| **Timeline** | The deep-dive view of one session | Investigating a single user complaint |
| **Errors** | Project-wide error fingerprints, ranked by frequency | Triaging the most common crashes |

---

## Timeline page — the workhorse

The Timeline shows one session at a time. Anatomy:

```
┌───────────────────────────────────────────────────────────────────┐
│ Scrubber (clickable mini-map of the whole session)                │
├──────────────┬────────────────────────────────────────────────────┤
│              │                                                    │
│  Filter      │   Event list                                       │
│  sidebar     │   (chronological, virtualised, op-pair markers)    │
│              │                                                    │
├──────────────┼────────────────────────────────────────────────────┤
│              │   Right panel: Details / State / Session /         │
│              │                Open ops / Errors / Similar / AI    │
└──────────────┴────────────────────────────────────────────────────┘
```

### Event list — what each row means

| Row icon | Event | What you see |
|---|---|---|
| `■` (green) | `session_start` / `session_end` | Session boundaries |
| `■` (red) | `session_abnormal_end` | Session that didn't get a clean `session_end` — process killed or crashed |
| `▣` blue | `screen` | UI screen change |
| `▶` indigo | `action` | Player tap / decision |
| `Δ` teal | `state_patch` | Player-profile patch (your `UpdateState` calls) |
| `δ` blue | `session_data_patch` | Environment patch (device / addressables / etc.) |
| `⇄` muted | `operation_start` (HTTP) / `operation_end` | Network request + completion |
| `⤓` muted | `operation_start` (resourceload) | Asset / bundle load |
| `⊞` muted | `operation_start` (sceneload) | Unity scene transition |
| `◇` muted | `operation_start` (purchase) | In-app purchase flow |
| `·` dim | log (info / debug) | Auto-captured or `TrackLog` |
| `⚡` yellow | log (warning) | |
| `✕` red | log (error) | |
| `✦` red | log (exception) + stack trace | |
| `☽` / `☀` | `lifecycle` | Background / foreground transitions |
| `◉` accent | `first_input_latency` | TTI milestone — first player interaction |
| `⚠` yellow | `session_data_partial` | Some session data couldn't fit in the size limit |

### Right panel tabs — what each one answers

| Tab | Answers | Best clicked when |
|---|---|---|
| **Details** | "What exactly is this event?" | Always — first tab |
| **State** | "What did the player's profile look like at this moment?" | Investigating "why did this action affect that value?" |
| **Session** | "What did the device / environment look like?" | Investigating "why did this work on iPhone but not Android?" |
| **Open ops** | "What operations are still un-ended at this seq?" | Investigating leaks — a `start` without a matching `end` |
| **Errors** | "What errors are in this session?" | One-click filtered view of all error/exception rows |
| **Similar** | "Other sessions with this same exception fingerprint" | Triaging — is this a one-off or affecting 1000 users? |
| **AI** | "Summarise this session for me" | When the timeline is too long to skim |

---

## Where each event ends up

Quick lookup: "I'm sending X, where does it appear?"

| You call | Lands as | Visible on |
|---|---|---|
| `Initialize` | `session_start` | Timeline list (`■` green at top) + Session tab seed data |
| `Shutdown` / app quit | `session_end` | Timeline list (`■` green at bottom) |
| (auto) crash on prior launch | `session_abnormal_end` on next launch | Dashboard "Recent abnormal sessions", Sessions filter |
| `SetUserData` | `user_data_update` | Timeline list (`◐` indigo); user id surfaces in Sessions list |
| `SetInitialState` | `state_initial` | State tab reconstruction starts here |
| `UpdateState(patch)` | `state_patch` | Timeline list (`Δ` teal); aggregates in State tab snapshot |
| `UpdateState(patch, reason)` | `state_patch` with `_reason` | Same as above + `reason` column in State diff view |
| `TrackRestart(reason)` | `restart` | Timeline list, *boundary* in State tab (replay restarts after this row) |
| `UpdateSessionData` | `session_data_patch` | Session tab snapshot — separate stream from State |
| `SetScreen` | `screen` | Timeline list (`▣` blue); also scopes actions below |
| `TrackAction` | `action` | Timeline list (`▶` indigo); meta visible in Details |
| `StartHTTP` / `EndHTTP` | `operation_start` / `operation_end` (`HTTP`) | Timeline list — start row links to end row via "→ see completion" |
| `StartResourceLoad` / `End` | `operation_*` (`ResourceLoad`) | Same; Details shows source / deps / bytes |
| `StartSceneLoad` / `End` | `operation_*` (`SceneLoad`) | Same; Details shows the `scene_progress_samples` strip |
| `StartPurchase` / `EndPurchase` | `operation_*` (`Purchase`) | Timeline list — end row shows "USD 4.99" chip when canonical meta is set |
| `TrackLog` | log row | Timeline list — colour-graded by level |
| `TrackException` | log row (level=`exception`) | Timeline list + Errors tab + Errors page (project-wide) |
| (auto) `Application.lowMemory` | `memory_warning` | Timeline list — Details shows heap/reserved/system MB |
| (auto) main-thread stall | `anr` (entry) + `anr_recovered` (exit, if process survived) | Timeline list — Details shows stuck_for_ms |

---

## Errors page — project-wide triage

`Settings → Errors` shows error fingerprints across **all sessions in the project**, ranked by frequency.

Each row:
- Fingerprint hash (deterministic across sessions for the same exception type + message)
- Count of sessions affected
- First seen / last seen timestamps
- Sample message + stack-trace excerpt
- "See sessions" link → Sessions list filtered to this fingerprint

This is the right entry point for triaging: "What's hitting the most users right now?" → start at the top. The Timeline view answers "what happened in *this one session*", the Errors page answers "what's the pattern across the whole project".

---

## Sessions page — finding a specific user's session

Filters:
- **Date range** (default: last 7 days)
- **User ID** — paste the value you passed to `SetUserData`
- **Status** — normal / abnormal / has_errors / has_anr
- **SDK version** — filter to one build
- **Platform** — android / ios / standalone / editor

Each row links into the Timeline for that session.

---

## Dashboard page — at a glance

The default landing for a project. Surfaces:

- **Active sessions today** with delta vs yesterday
- **Top 5 errors** (slice of Errors page)
- **Recent abnormal sessions** — sessions ending without a clean `session_end`
- **Cold-start funnel** — `session_start` → `first_frame_rendered` → `first_input_latency` aggregate timings

If something here is wrong, drill into the specific tab (Errors / Sessions / Timeline).

---

## Reading the periodic metric stream

Metrics aren't on the Timeline row list — they're a separate time-series. Currently they're surfaced minimally on the Timeline scrubber as small overlay charts (when enabled). Detailed metric viewing is on the roadmap.

For now, the most useful metric reads happen via the AI tab — "summarise the perf of this session" pulls the metric stream and reports on jank windows, GC spikes, network drops, etc.

---

## Where things AREN'T

Common "I expected to see this and didn't":

| You expected | What's actually happening | Where to look |
|---|---|---|
| State patch immediately on the timeline | SDK coalesces patches in a 100 ms window — flurries collapse | Wait one frame; the merged patch will appear |
| Every `TrackLog("info", ...)` as its own row | LogDedupBuffer collapses identical messages within 5 s into one row with `repeat_count: N` | Look at Details on the row — `repeat_count` chip |
| Purchase metadata as first-class fields | Only when you used `PurchaseMetadata.Build*Metadata` — raw dicts go to "extra metadata" section | Switch the call site to use the helpers |
| ANR events in the Editor | ANR watchdog auto-disables in Editor (breakpoints would cause false positives) | Run on device or in `-batchmode` |
| Sensitive values like emails / tokens / cards | PII masks replace them in-place with `[redacted-*]` placeholders | This is correct — don't disable `PiiValueMasksEnabled` |

---

## Next steps

- New to PlayScope? [Integration Guide](integration-guide.md)
- Looking for a specific method? [API Reference](api-reference.md)
- Want to tweak the defaults? [Configuration](configuration.md)
