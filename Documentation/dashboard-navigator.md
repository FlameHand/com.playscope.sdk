# Dashboard Navigator

A field guide to **where to read what** on the PlayScope dashboard. Once events are flowing, this answers "I tracked it — where do I find it now?"

## Top-level pages

The left sidebar lists these pages per project:

| Page | Purpose | When to open |
|---|---|---|
| **Dashboard** | Per-project overview — Crash-Free Sessions/Users %, active sessions, top errors, recent abnormal sessions, cold-start funnel | First check after a deployment |
| **Sessions** | List of all sessions, filterable by date / user / status / SDK version / platform | Looking for a specific user's session |
| **Errors** | Project-wide exception fingerprints, ranked by frequency | Triaging the most common crashes |
| **Performance** | Device-class matrix (p50/p95/p99 per `device_model × RAM × OS`) for every auto-sampled metric | "Is the game janky on a specific device tier?" |
| **Revenue** | IAP vs ad-impression revenue, split by network / placement / product | Reading monetisation from `StartPurchase` / `StartAd` |
| **Funnels** | Build ordered step funnels from your `screen` / `action` / event names; conversion + per-segment breakdown | "Where do players drop off?" |
| **Live Ops** | Real-time 5-minute rolling snapshot — errors / exceptions / active sessions / crashes by version × platform | Watching a release go out live |
| **Progression** | Cross-player curves of numeric `UpdateState` values, bucketed by minute-since-session-start | "How fast do players accumulate currency / level?" |
| **Build compare** | Version-vs-version regression view for the Performance metrics | "Did this build make p99 worse?" |
| **Settings** | Account / Projects (SDK keys) / Symbols / Alerts / MCP tokens | Rotate a key, upload symbols, set up alerts |

A single session is opened from **Sessions** (or any "see session" link) into the **Timeline** — the deep-dive view below.

---

## Timeline page — the workhorse

The Timeline shows one session at a time. Anatomy:

```
┌───────────────────────────────────────────────────────────────────┐
│ Scrubber (clickable mini-map of the whole session + metric overlay)│
├──────────────┬────────────────────────────────────────────────────┤
│              │                                                    │
│  Filter      │   Event list                                       │
│  sidebar     │   (chronological, virtualised, op-pair markers)    │
│              │                                                    │
├──────────────┼────────────────────────────────────────────────────┤
│              │   Right panel: Details / Profile / Session /        │
│              │   Conditions / Open ops / Errors / Similar / AI    │
└──────────────┴────────────────────────────────────────────────────┘
```

### Event list — what each row means

The list is colour- and icon-coded by event type (exact glyphs evolve — go by the type, not the symbol):

| Event | What you see |
|---|---|
| `session_start` / `session_end` | Session boundaries (green). `session_end` carries `end_status`: `normal` / `background_timeout` |
| `session_abnormal_end` | Session that never got a clean `session_end` — process killed, crashed, or swipe-killed (red). Carries `end_reason` |
| `screen` | UI screen change |
| `action` | Player tap / decision |
| `state_patch` | Player-profile patch (your `UpdateState` calls) |
| `session_data_patch` | Environment patch (device / addressables / etc.) |
| `operation_start` / `operation_end` | A timed span. The op type — `HTTP` / `ResourceLoad` / `SceneLoad` / `Purchase` / `Ad` / `Custom` — drives the icon and the filter channel |
| log (debug / info) | Auto-captured or `TrackLog` |
| log (warning / error) | Colour-graded by level |
| log (exception) + stack trace | Caught (`TrackException`) or unhandled |
| `lifecycle` | Background / foreground transitions (carries `duration_in_prev_state_ms`) |
| `first_frame_rendered` / `first_input_latency` | Cold-start TTI milestones |
| `memory_warning` | OS low-memory signal |
| `anr` / `anr_recovered` | Main-thread stall detected / resumed |
| `app_update_detected` / `network_change` | Version bump / connectivity flip |

### Right panel tabs — what each one answers

| Tab | Answers | Best clicked when |
|---|---|---|
| **Details** | "What exactly is this event?" | Always — first tab |
| **Profile** | "What did the player's profile (game state) look like at this moment?" | Investigating "why did this action affect that value?" |
| **Session** | "What did the device / environment look like?" | "Why did this work on iPhone but not Android?" |
| **Conditions** | "What were thermal / battery / charging / disk / free-RAM / network at this moment?" | Correlating a hitch with an overheating or low-disk device |
| **Open ops** | "What operations are still un-ended at this seq?" | Investigating leaks — a `start` without a matching `end` |
| **Errors** | "What errors are in this session?" | One-click filtered view of all error/exception rows |
| **Similar** | "Other sessions with this same exception fingerprint" | Triaging — one-off or affecting 1000 users? |
| **AI** | (Stub) AI Investigation lands in M3 | Not functional yet — placeholder only |

When the selected row is an **Ad** operation, **Details** renders the canonical `AdDetails` panel (network / placement / ad_type / result / revenue); a **Purchase** row renders `PurchaseDetails` with the price chip.

---

## Where each event ends up

Quick lookup: "I'm sending X — where does it appear?"

| You call | Lands as | Visible on |
|---|---|---|
| `Initialize` | `session_start` | Timeline top + Session tab seed data |
| (app quit) | `session_end` | Timeline bottom (no manual `Shutdown` call exists) |
| (auto) crash on prior launch | `session_abnormal_end` next launch | Dashboard Crash-Free banner + Sessions status filter + `EndReason` rendering |
| `SetUserData` | `user_data_update` | Timeline; user id surfaces in the Sessions list + filter |
| `SetInitialState` | `state_initial` | Profile tab reconstruction starts here |
| `UpdateState(patch[, reason])` | `state_patch` (+ `_reason`) | Profile tab snapshot; **numeric values aggregate on the Progression page** |
| `TrackRestart(reason)` | `restart` | Timeline *boundary* — Profile tab replay re-bases after this row |
| `UpdateSessionData` | `session_data_patch` | Session tab snapshot — separate stream from Profile |
| `SetScreen` | `screen` | Timeline; usable as a **Funnels** step; scopes actions below it |
| `TrackAction` | `action` | Timeline; **action names feed the Funnels builder** |
| `StartHTTP` / `EndHTTP` | `operation_*` (`HTTP`) | Timeline — start row links to its completion |
| `StartResourceLoad` / `End` | `operation_*` (`ResourceLoad`) | Timeline — Details shows source / deps / bytes |
| `StartSceneLoad` / `End` | `operation_*` (`SceneLoad`) | Timeline — Details shows the `scene_progress_samples` strip |
| `StartPurchase` / `EndPurchase` | `operation_*` (`Purchase`) | Timeline (price chip) + **Revenue page (IAP)** |
| `StartAd` / `EndAd` | `operation_*` (`Ad`) | Timeline (AdDetails panel) + **Revenue page (ads)** + crash-during-ad correlation on **Errors** |
| `TrackLog` | log row | Timeline, colour-graded by level (Free plan drops debug/info/warning server-side) |
| `TrackException` | log row (`exception`) | Timeline + Errors tab + **Errors page** (project-wide) |
| (auto) `Application.lowMemory` | `memory_warning` | Timeline — Details shows heap / reserved / system MB |
| (auto) main-thread stall | `anr` + `anr_recovered` | Timeline — Details shows `stuck_for_ms` / `total_stuck_ms` |
| (auto) periodic metrics | metric stream | **Performance page** (aggregated) + **Conditions tab** (per-session) + scrubber overlay |

---

## Cross-session pages — where SDK data aggregates

The Timeline answers "what happened in *this one session*". These pages roll the same data up across the whole project:

- **Performance** — every auto-sampled metric (`fps`, `frame_time_p99_ms`, `dropped_frames_count`, `gc_alloc_kb`, memory, battery, thermal, disk, free RAM) as a sortable matrix of p50/p95/p99 by `device_model × RAM bucket × OS bucket`. Click a row for the side Drawer drill-down. Include-editor / include-dev-build toggles default off.
- **Revenue** — IAP (`StartPurchase`) and ads (`StartAd`) split out, broken down by network / placement / product / currency, plus an ad-sessions list.
- **Funnels** — define ordered steps from your `screen` / `action` / event names; per-step conversion + drop-off, with a device/perf **segmented breakdown** (worst-converting device class sorted to the top).
- **Live Ops** — a 5-minute rolling snapshot (errors / exceptions / active sessions / crashes) sliced by `app_version × platform`, polled every 5 s, with a recent-alerts strip.
- **Progression** — median / p25 / p75 curves of numeric `UpdateState` values per state-key, bucketed by minute-since-session-start, across all players. Keys prefixed with `_` are excluded from this aggregation.
- **Build compare** — pick two `app_version`s and see the Performance regression (top-N by Δp99, direction-aware tone-coding).

---

## Errors page — project-wide triage

Top-level **Errors** in the sidebar shows exception fingerprints across **all sessions in the project**, ranked by frequency. Each row:

- Fingerprint hash (deterministic for the same exception type + message)
- Count of sessions affected, first-seen / last-seen
- Sample message + stack-trace excerpt (resolved via uploaded IL2CPP symbols on Pro+)
- "See sessions" link → Sessions filtered to this fingerprint
- A **during-ads** filter (`?during_ads=true`) to see only exceptions that fired while an ad operation was open

Start here for "what's hitting the most users right now?"; drop into the Timeline for "what happened in this one session".

---

## Sessions page — finding a specific user's session

Filters:
- **Date range** (default: last 7 days)
- **User ID** — the value you passed to `SetUserData`
- **Status** — normal / abnormal / has_errors / has_anr
- **SDK version** — filter to one build
- **Platform** — android / ios / standalone / editor
- **Hide dev/editor** — exclude development builds and Editor sessions

Each row links into the Timeline; `DEV` / `EDITOR` badges and `EndReason` (e.g. swipe-kill vs crash) are shown inline.

---

## Dashboard page — at a glance

The default landing for a project. Surfaces:

- **Crash-Free Sessions / Users %** — 7-day window with a sparkline and a "breakdown by version" drill-down (worst versions tinted)
- **Active sessions** with delta vs the prior period
- **Top errors** — a slice of the Errors page
- **Recent abnormal sessions** — sessions ending without a clean `session_end`
- **Cold-start funnel** — `session_start` → `first_frame_rendered` → `first_input_latency` aggregate timings

If a number looks wrong here, drill into the specific page (Errors / Sessions / Performance / Timeline).

---

## Reading the periodic metric stream

The auto-sampled metrics aren't individual Timeline rows — they're a time series. You read them in three places:

1. **Performance page** — the aggregated, cross-session view: p50/p95/p99 per device class, with build-over-build comparison on **Build compare**.
2. **Conditions tab** (Timeline right panel) — the per-session device-state view: thermal band, battery + charging overlay, disk / free-RAM sparklines, network-state band reconstructed at any seq.
3. **Scrubber overlay** — small metric charts along the session mini-map for a quick visual.

---

## Where things AREN'T

Common "I expected to see this and didn't":

| You expected | What's actually happening | Where to look |
|---|---|---|
| State patch immediately on the timeline | SDK coalesces patches in a 100 ms window — flurries collapse | Wait one frame; the merged patch appears |
| Every `TrackLog("info", …)` as its own row | `LogDedupBuffer` collapses identical messages within 5 s into one row with `repeat_count: N` | Details on the row → `repeat_count` chip |
| Debug / Info / Warning logs on a Free plan | Free plan persists only Error + Exception — the rest are dropped server-side | Upgrade the plan, or check on Indie+ |
| Purchase / ad metadata as first-class fields | Only when you used `PurchaseMetadata` / `AdMetadata` helpers — raw dicts go to "extra metadata" | Switch the call site to the helpers |
| The AI tab to summarise the session | AI Investigation is an M3 stub — not functional yet | — (planned) |
| ANR events in the Editor | ANR watchdog auto-disables in the Editor (breakpoints cause false positives) | Run on device or in `-batchmode` |
| `thermal_state` on Unity 2021/2022 | The API is Unity 2023.1+ only — the metric is simply omitted on older Unity | Upgrade Unity, or read battery/charging instead |
| Sensitive values like emails / tokens / cards | PII masks replace them in-place with `[redacted-*]` | This is correct — don't disable `PiiValueMasksEnabled` |

---

## Next steps

- New to PlayScope? [Integration Guide](integration-guide.md)
- Looking for a specific method? [API Reference](api-reference.md)
- Want to tweak the defaults? [Configuration](configuration.md)
