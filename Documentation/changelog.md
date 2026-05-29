# Changelog

All notable changes to the PlayScope SDK are documented here. Versions follow [Semantic Versioning](https://semver.org/).

CI auto-bumps the patch version on every push to `main`, so intermediate versions carry the same feature set as their nearest documented release. The `0.2`–`0.5` minor bumps tracked roadmap milestones whose work was mostly backend/dashboard-side; the entries below consolidate the **SDK-package** changes per milestone. See [GitHub Releases](https://github.com/FlameHand/com.playscope.sdk/releases) for the per-patch tags.

---

## [0.6.x] — Monetisation analytics

### Added
- **Ad-impression API** — `PlayScope.StartAd(placement, metadata)` / `EndAd(id, status, metadata)` + `OperationType.Ad`. Feeds the dashboard's Revenue page (IAP vs ads split) and the crash-during-ad correlation on Errors.
- **`AdMetadata`** helper — canonical `network` / `placement` / `ad_type` / `result` / `revenue` / `currency` schema, with `Network` / `AdType` / `AdResult` vocab constants. Negative revenue is clamped to 0 before leaving the device.
- Native crash-collector groundwork on Android (`PlayScopeCrashCollector`, `PlayScopeCrash.java`, NDK `.so` delivery via the installer) — capture surface lands in a later milestone.

---

## [0.4.x] — Device-state telemetry

### Added
- **`thermal_state`** metric — `UnityEngine.Device.SystemInfo.thermalStatus` via reflection (Unity 2023.1+; omitted on older Unity), **`is_charging`** (emit-on-change), **`available_disk_mb`** and **`system_free_ram_mb`** via the new `PlayScopeNativeMetrics` Android/iOS pair + `NativeMetricsBridge`.
- Disk free now read through the native bridge (IL2CPP doesn't implement the `DriveInfo` icall); slow device-state cadence cut to 10 s and primed to fire a baseline on the first tick so short sessions still capture a sample.

---

## [0.3.x] — Settings asset, symbols & swipe-kill

### Added
- **`PlayScopeSettings`** ScriptableObject + parameterless **`PlayScope.Initialize()`** — create via the **PlayScope ▸ Settings** Editor menu; no key in source. `PlayScope.Settings` exposes the loaded asset at runtime.
- **`PlayScopeContext.SdkKey`** — renamed from `ApiKey` (old name kept as an `[Obsolete]` alias).
- **Editor IL2CPP symbol uploader** (`PlayScopeSymbolUploader`) — post-build upload of Android `symbols.zip` / iOS dSYM; never fails the build.
- **Native lifecycle bridges** (`PlayScopeLifecycle.java` / `.mm` + `NativeLifecycleBridge`) for swipe-kill detection, delivered into the consumer project by `PlayScopeNativePluginInstaller`.

### Changed
- 5-minute background-timeout session rotation; synchronous `session_end` finalize on shutdown closes a wake-vs-cancel race that could drop the final event.

---

## [0.1.39] — 2026-05-21

### Added — Sprint 5
- **`PiiValueScanner`** — value-level PII regex scrubbing as a complement to the always-on key-name filter. Catches emails, JWTs, Bearer/Basic auth headers, well-known service tokens (`ghp_`, `sk_live_`, `xoxb_`, `AKIA`...), Luhn-validated credit cards, international phone numbers, and public IPv4 addresses. Matches are replaced in-line with `[redacted-*]` placeholders so surrounding context survives.
- `PlayScopeContext.PiiValueMasksEnabled` (default `true`) — toggle for the above.
- 20+ unit tests covering happy paths and false-positive guards (version strings, sequence numbers, private IPs).

### Documentation
- `dashboard-navigator.md` — new doc explaining where each event lands in the dashboard UI.
- `integration-guide.md`, `api-reference.md`, `configuration.md`, `index.md` — full refresh covering everything below.

---

## [0.1.38] — 2026-05-21

### Added — Sprint 3
- **`first_input_latency`** event — fires once per session on the first touch / mouse / key / gamepad input after `first_frame_rendered`. Carries `latency_ms` (delta from first-frame, not session_start) and `input_kind`. Closes the cold-start funnel.
- **`frame_time_p99_ms`** + **`dropped_frames_count`** metrics — sampled every 1 s from a 128-slot ring buffer of per-frame deltas. p99 picks up stutter that average FPS hides; dropped count uses a fixed 33.4 ms threshold.
- **`gc_alloc_kb`** metric — `Profiler.GetTotalAllocatedMemoryLong()` delta per 1 s, clamped >= 0. Available on every Unity Player without Development Build.

---

## [0.1.37] — 2026-05-21

### Added — Sprint 2
- **`PurchaseMetadata`** — static helper class for building canonical purchase metadata dictionaries. `BuildStartMetadata` (store / currency / price_amount / is_restore) and `BuildEndMetadata` (transaction_id_hash / validation_status / failure_reason).
- `transaction_id` is SHA-256-hashed to 16 hex chars before leaving the device.
- Store auto-detect from `Application.platform` (`app_store` / `google_play` / `steam` / `amazon` / `other`).
- Canonical vocab constants: `Store`, `ValidationStatus`, `FailureReason`.

---

## [0.1.36] — 2026-05-20

### Added — Sprint 1 (closing)
- **`LogDedupBuffer`** — collapses repeated `(level, message)` pairs of debug / info / warning logs within a 5 s window into a single timeline row carrying `repeat_count: N`. Critical levels (error, exception) bypass dedup entirely.
- Thread-safe; ticked from `MonoBehaviour.Update`; flushed on pause / shutdown so backgrounded sessions don't strand the tail of the buffer.
- 256-entry safety cap on the buffer, oldest-first eviction with a warning log.

---

## [0.1.35] — 2026-05-20

### Added — Sprint 1
- **`memory_warning`** event — emitted on `Application.lowMemory` (cross-platform: Android `onTrimMemory` + iOS `UIApplicationDidReceiveMemoryWarning`). Carries `heap_mb`, `reserved_mb`, `system_mb`. Critical-priority so it lands server-side even if the OS kills the app moments later.

---

## [0.1.33] — 2026-05-19

### Added — Sprint 1
- **`PlayScope.TrackRestart(reason, metadata)`** — in-game restart marker for player-initiated profile wipes. The dashboard treats this as a boundary for state replay; the post-restart `SetInitialState` becomes the new zero-point.
- `reason` is promoted to a dedicated positional parameter (rather than a metadata key) so the dashboard surfaces it prominently.
- `restart` event whitelisted on backend.

---

## [0.1.32] — 2026-05-18

### Added — Sprint 1
- **`AnrWatchdog`** — main-thread heartbeat + thread-pool timer that emits `anr` when the heartbeat hasn't ticked for `AnrThresholdMs` (default 2 s) and `anr_recovered` when the main thread resumes.
- Auto-disables in the Unity Editor unless running in batch mode (avoids false positives from breakpoints).
- Suspended on background, resumed on foreground.
- `PlayScopeContext.AnrDetectionEnabled` (default `true`) + `PlayScopeContext.AnrThresholdMs` (default `2000`).

---

## [0.1.20–0.1.31] — earlier work

Foundation observability + op-type completion. Highlights:

- **`first_frame_rendered`** + **`app_update_detected`** events — TTI signal start + version-change detection.
- **`network_change`** event — discrete signal on `Application.internetReachability` flip (complements the periodic metric).
- **Lifecycle event** with `duration_in_prev_state_ms` — quantifies how long the user spent in each foreground / background period.
- **Resource-load enrichment** — `source` (remote / local / builtin), `dependency_count`, `total_download_size_bytes`.
- **Scene-load progress sampler** — automatic 250 ms polling of an `AsyncOperation` passed to `StartSceneLoad`, stamped into `scene_progress_samples` on `EndSceneLoad`.
- **Coalescers** — `StatePatchCoalescer` (100 ms window) + `SessionDataCoalescer` (1 s window) — collapse bursts of patches into single events.
- **Open-ops cap** — 256 max open operations, oldest evicted on overflow to prevent leaks from runaway `Start*` calls without matching `End*`.
- **Sensitive Key Filter** — drops keys whose names look credential-shaped (`password`, `token`, `secret`, etc.) from metadata and state dicts.
- **JSON correctness** — RFC 8259-strict escaping, NaN / Infinity → `null`, recursion-depth cap.

---

## [0.1.2] — 2026-05-17

### Fixed
- Add `.meta` files for renamed asmdef files so Unity's immutable package cache can register the assemblies correctly.

---

## [0.1.1] — 2026-05-17

### Changed
- Unified all assembly and namespace names to `PlayScopeSdk` / `PlayScopeSdk.Editor`.

---

## [0.1.0] — 2026-05-17

### Added
- **`UploaderWorker`** — background upload loop with exponential backoff, stable `batch_id`, dead-letter queue, crash-recovery on startup.
- **`StorageQuotaManager`** — 50 MB soft cap / 100 MB hard cap; oldest-first eviction; never drops `session_start`/`session_end`/`exception` records.
- **`SessionRecovery`** — writes `session_abnormal_end` on next launch when prior session was interrupted.
- **`PlayScopeVersionChecker`** (Editor) — daily GitHub Releases check with update dialog.
- **`release.yml`** GitHub Actions workflow — `workflow_dispatch` release that bumps `package.json`, commits, tags, and publishes.

### Fixed
- `WriterWorker.FlushImmediate` now calls `FinalizeChunk()`.
- `WriterWorker`: `screen_name` / `action_name` escaped before JSON serialisation.
- `UploaderWorker`: `batch_id` is the chunk filename (stable across retries).
- `PlayScopeMonoBehaviour`: clears `_sampler` reference on `Shutdown`.
- `StorageQuotaManager.IsChunkCritical`: scans all lines of a chunk.
- `EventPipeline.ValueToJson`: serialises `IList` values as JSON arrays.
- `MetricsSampler`: uses `GC.GetTotalMemory(false)` instead of unavailable-in-release Profiler API.
- `SessionInfo`: `sdkVersion` is now a required constructor parameter.
- `PlayScopeRuntime`: reads `environment` from `context.Metadata["environment"]` with `"production"` fallback.

---

## Pre-release

Internal development versions prior to 0.1.0 are not documented here.
