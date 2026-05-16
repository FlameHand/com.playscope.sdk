# Changelog

All notable changes to the PlayScope SDK are documented here.
Versions follow [Semantic Versioning](https://semver.org/).

---

## [0.1.2] — 2026-05-17

### Fixed
- Add `.meta` files for renamed asmdef files so Unity's immutable package cache can register the assemblies correctly.

---

## [0.1.1] — 2026-05-17

### Changed
- Unified all assembly and namespace names to `PlayScopeSdk` / `PlayScopeSdk.Editor` — previously the assembly names used the `PlayScope.Runtime` / `PlayScope.Editor` convention while the C# namespace was `PlayScopeSdk`.

---

## [0.1.0] — 2026-05-17

### Added
- **`UploaderWorker`** — background upload loop with exponential backoff, stable `batch_id` (chunk filename-based), dead-letter queue, and crash-recovery on startup.
- **`StorageQuotaManager`** — enforces a 50 MB soft cap and 100 MB hard cap; drops non-critical chunks oldest-first; never drops session_start/end or exception records.
- **`SessionRecovery`** — detects sessions interrupted by a crash and writes a `session_abnormal_end` event on next launch.
- **`PlayScopeVersionChecker`** (Editor) — checks GitHub Releases daily and shows an update dialog in the Unity editor.
- **`release.yml`** GitHub Actions workflow — `workflow_dispatch`-triggered release that bumps `package.json`, commits, tags, and publishes a GitHub Release.

### Fixed
- `WriterWorker.FlushImmediate` now calls `FinalizeChunk()` — previously flushed data was not committed to a chunk file.
- `WriterWorker`: `screen_name` and `action_name` are now escaped before JSON serialisation — prevented JSON injection via untrusted strings.
- `UploaderWorker`: `batch_id` is now the chunk filename (stable across retries) — was `Guid.NewGuid()` per attempt, causing duplicate ingestion on retry.
- `PlayScopeMonoBehaviour`: clears `_sampler` reference on `Shutdown` and when `Pipeline == null` — prevented a null-ref in `Update` after shutdown.
- `StorageQuotaManager.IsChunkCritical`: scans all lines of a chunk instead of only the first — previously missed critical events (e.g. `session_end`) written after the first record.
- `EventPipeline.ValueToJson`: serialises `IList` values as JSON arrays — previously serialised as `ToString()`.
- `MetricsSampler`: uses `GC.GetTotalMemory(false)` instead of the Profiler API — Profiler API is not available in release builds.
- `SessionInfo`: `sdkVersion` is now a required constructor parameter — eliminates the risk of the version constant and the session record diverging.
- `PlayScopeRuntime`: reads `environment` from `context.Metadata["environment"]` with `"production"` fallback — was a hardcoded string.
- `PlayScope.SetUserData`: removed a dead `metaJson` variable.

---

## Pre-release

Internal development versions prior to 0.1.0 are not documented here.
