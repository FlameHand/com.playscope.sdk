using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using PlayScopeSdk.Internal;

namespace PlayScopeSdk
{
    /// <summary>
    /// Main entry point for the PlayScope SDK.
    /// All methods are thread-safe and never throw exceptions.
    /// When the SDK is in disabled state (missing or empty API key), all calls become no-ops.
    /// </summary>
    public static class PlayScope
    {
        // Reset on every Initialize so on/off cycles re-warn per cycle, not once per process.
        private static int _disabledCompleteWarned;

        /// <summary>
        /// Wipes session-scoped static state owned by this file. Called from
        /// <see cref="Internal.PlayScopeRuntime.Initialize"/> before the gate
        /// flips so a re-Initialize starts clean. Add new session-scoped statics
        /// here — there's no other safe place to reset them from outside.
        /// </summary>
        internal static void ResetSessionScopedState()
        {
            Interlocked.Exchange(ref _disabledCompleteWarned, 0);
            _openOperationTypes.Clear();
            _openOperationStartTicks.Clear();
            _openOperationStartMetadata.Clear();
        }

        // Safety valve for leaked (forgotten-CompleteOperation) entries: drops
        // the oldest ~10% when the cap is breached. Fast-path Count check first.
        private static void EvictOpenOperationsIfOverflow()
        {
            if (_openOperationStartTicks.Count < MaxOpenOperations) return;
            var ordered = new List<KeyValuePair<string, long>>(_openOperationStartTicks);
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));
            int evictCount = Math.Max(1, ordered.Count / 10);
            for (int i = 0; i < evictCount; i++)
            {
                _openOperationTypes.TryRemove(ordered[i].Key, out _);
                _openOperationStartTicks.TryRemove(ordered[i].Key, out _);
                _openOperationStartMetadata.TryRemove(ordered[i].Key, out _);
            }
            PlayScopeLog.Warning(
                $"PlayScope: open-operation dictionary cap ({MaxOpenOperations}) hit — " +
                $"evicted {evictCount} oldest entry. Some CompleteOperation calls were probably missed.");
        }

        // opId → type, carried onto operation_end so the timeline's per-channel
        // filter (HTTP / ResourceLoad / SceneLoad / Purchase / Custom) classifies
        // the end event; without it every end falls through to "Custom".
        // Concurrent because Start (main thread) and End (worker) commonly race.
        private static readonly ConcurrentDictionary<string, string> _openOperationTypes = new();

        // opId → start tick, so CompleteOperation can stamp duration_ms even when
        // the caller didn't measure it — duration for EVERY tracked op, not just HTTP.
        private static readonly ConcurrentDictionary<string, long> _openOperationStartTicks = new();

        // opId → start-metadata snapshot, merged into operation_end so dimension
        // fields (operation_name / placement / network / ad_type / store) survive
        // forward; otherwise revenue/perf MVs bucket every end row as 'unknown'.
        // End-time caller metadata wins on collision (authoritative).
        private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, object>> _openOperationStartMetadata = new();

        // Cap on outstanding Start-but-not-Completed ops so forgotten/leaked ends
        // don't grow the dicts unbounded. 1024 is generous (real games: a few dozen).
        // Over-cap drops the oldest open op; its End still fires but unstamped.
        private const int MaxOpenOperations = 1024;

        /// <summary>
        /// Initializes the SDK from the project's <see cref="PlayScopeSettings"/>
        /// asset (<c>Resources/PlayScopeSettings.asset</c>) — the typical entry
        /// point. Create the asset via <c>PlayScope ▸ Settings</c>, paste your
        /// SDK key, and call this from a bootstrap script.
        /// If the asset is missing or its <c>SdkKey</c> is empty, the SDK stays
        /// disabled (every call no-ops) and logs a console warning.
        /// </summary>
        public static void Initialize()
        {
            PlayScopeSettings settings;
            try
            {
                // Resources.Load is main-thread-only — honor the never-throws
                // contract. Not ForceDisable: a later main-thread call must work.
                settings = PlayScopeSettings.Load();
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning(
                    "Initialize: PlayScopeSettings.Load threw (called off the main thread?) — " +
                    "SDK not initialized. Call Initialize from the main thread.", ex);
                return;
            }
            if (settings == null)
            {
                PlayScopeLog.Warning(
                    "PlayScopeSettings asset not found at Resources/PlayScopeSettings.asset. " +
                    "Create it via the PlayScope ▸ Settings menu in the Editor (Settings → " +
                    "Projects → SDK key in the dashboard provides the value).");
                PlayScopeRuntime.ForceDisable();
                return;
            }
            Initialize(BuildContextFromSettings(settings));
        }

        /// <summary>
        /// Initializes the SDK with an explicit context — use to override
        /// settings programmatically (e.g. pick the key per environment at
        /// runtime). For the common case prefer the parameterless
        /// <see cref="Initialize()"/>. Calls after the first warn + no-op.
        /// </summary>
        public static void Initialize(PlayScopeContext context)
        {
            try
            {
                PlayScopeRuntime.Initialize(context);
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("Initialize failed — SDK disabled", ex);
                PlayScopeRuntime.ForceDisable();
            }
        }

        // Warn-once guard for off-main-thread Settings reads — wrapper code may poll.
        private static int _settingsLoadThrewWarned;

        /// <summary>
        /// Loaded <see cref="PlayScopeSettings"/> asset, or null if absent.
        /// Lets wrapper code mirror the SDK's <c>MinLogLevel</c> without re-loading Resources.
        /// Returns null (never throws) when read off the main thread before the
        /// asset was first loaded; after a main-thread load the cached instance
        /// is returned from any thread.
        /// </summary>
        public static PlayScopeSettings Settings
        {
            get
            {
                try
                {
                    return PlayScopeSettings.Load();
                }
                catch (Exception ex)
                {
                    if (Interlocked.CompareExchange(ref _settingsLoadThrewWarned, 1, 0) == 0)
                    {
                        PlayScopeLog.Warning(
                            "Settings: PlayScopeSettings.Load threw (read off the main thread?) — returning null.", ex);
                    }
                    return null;
                }
            }
        }

        private static PlayScopeContext BuildContextFromSettings(PlayScopeSettings s)
        {
            return new PlayScopeContext
            {
                SdkKey = s.SdkKey,
                UploadEndpoint = string.IsNullOrEmpty(s.BackendUrl)
                    ? "https://api.playscope.dev"
                    : s.BackendUrl,
                AutoCaptureUnityLogs = s.AutoCaptureUnityLogs,
                AutoCaptureMinLevel = s.MinLogLevel,
                AnrDetectionEnabled = s.AnrDetectionEnabled,
                AnrThresholdMs = s.AnrThresholdMs,
                PiiValueMasksEnabled = s.PiiValueMasksEnabled,
            };
        }

        /// <summary>
        /// True once <see cref="Initialize"/> has wired up every subsystem and
        /// the API is ready. False if never started, or if Initialize failed
        /// and self-disabled. Stays true until Shutdown / process exit.
        /// </summary>
        public static bool IsInitialized => PlayScopeRuntime.IsInitialized;

        /// <summary>
        /// True after the SDK has been permanently disabled — blank key, a boot
        /// step threw, or <see cref="Initialize"/> caught an exception. Probe
        /// AFTER Initialize to surface the failure instead of silently no-opping.
        /// </summary>
        public static bool IsDisabled => PlayScopeRuntime.IsDisabled;

        /// <summary>
        /// Associates the current session with a user identity.
        /// Safe to call multiple times — each call updates the current identity.
        /// </summary>
        /// <param name="customUserId">Your application-side user identifier.</param>
        /// <param name="metadata">Optional key-value attributes to attach to the user (e.g. plan, region).</param>
        public static void SetUserData(string customUserId, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                // Include userId in a merged metadata object
                var userMeta = new Dictionary<string, object> { ["user_id"] = customUserId ?? "" };
                if (metadata != null)
                    foreach (var kv in metadata) userMeta[kv.Key] = kv.Value;
                PlayScopeRuntime.Pipeline?.EnqueueEvent("user_data_update", metadataJson: EventPipeline.DictToJson(userMeta));
            }
            catch (Exception ex) { PlayScopeLog.Warning("SetUserData failed", ex); }
        }

        /// <summary>
        /// Sets the full initial game state snapshot for the session.
        /// Second call is a warning + ignored — use <see cref="UpdateState"/> for incremental changes.
        /// </summary>
        /// <param name="state">Full state dictionary (e.g. level, currency, inventory).</param>
        public static void SetInitialState(IReadOnlyDictionary<string, object> state)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                if (!PlayScopeRuntime.TryMarkInitialStateSet())
                {
                    PlayScopeLog.Warning("SetInitialState called more than once — ignored. Use UpdateState for incremental changes.");
                    return;
                }
                state = SensitiveKeyFilter.FilterState(state);
                PlayScopeRuntime.Pipeline?.EnqueueEvent("state_initial", statePatchJson: EventPipeline.DictToJson(state));
            }
            catch (Exception ex) { PlayScopeLog.Warning("SetInitialState failed", ex); }
        }

        /// <summary>
        /// Applies a partial patch to the current game state.
        /// Only the provided keys are updated; unmentioned keys remain unchanged.
        /// </summary>
        /// <param name="patch">Dictionary of keys to update in the current state.</param>
        public static void UpdateState(IReadOnlyDictionary<string, object> patch)
            => UpdateState(patch, reason: null);

        /// <summary>
        /// Applies a partial patch with an explanatory reason. The reason is
        /// recorded in the patch payload under <c>_reason</c> and shown in the
        /// dashboard so reviewers can answer "why did this state change?"
        /// without digging through call stacks.
        /// </summary>
        /// <param name="patch">Dictionary of keys to update in the current state.</param>
        /// <param name="reason">Short human-readable cause: "level_up", "purchase_completed",
        /// "save_loaded", etc. Free-form but keep it consistent across call sites — the
        /// dashboard groups patches by reason.</param>
        public static void UpdateState(IReadOnlyDictionary<string, object> patch, string reason)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                patch = SensitiveKeyFilter.FilterState(patch);
                // Coalescer folds a per-frame burst into one row per ~100ms window
                // and stamps _reason itself before emit.
                PlayScopeRuntime.StatePatchCoalescer.Add(patch, reason);
            }
            catch (Exception ex) { PlayScopeLog.Warning("UpdateState failed", ex); }
        }

        /// <summary>
        /// Records or patches diagnostic <b>session-level</b> data — device,
        /// environment, addressables, disk, memory, anything <i>about</i> the
        /// runtime rather than about the player. Different from
        /// <see cref="UpdateState"/> which carries the game's profile state.
        /// </summary>
        /// <param name="patch">Dictionary of fields to merge into the session
        /// data snapshot. Last write per key wins; <c>null</c> removes a key.</param>
        public static void UpdateSessionData(IReadOnlyDictionary<string, object> patch)
            => UpdateSessionData(patch, reason: null);

        /// <summary>
        /// Same as <see cref="UpdateSessionData(IReadOnlyDictionary{string,object})"/>
        /// but with an explanatory reason recorded under <c>_reason</c>.
        /// </summary>
        /// <param name="patch">Fields to merge into the session data snapshot.</param>
        /// <param name="reason">Short cause label: "addressables_init",
        /// "periodic_disk_sample", "network_change", etc.</param>
        public static void UpdateSessionData(IReadOnlyDictionary<string, object> patch, string reason)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                patch = SensitiveKeyFilter.FilterMetadata(patch);
                PlayScopeRuntime.SessionDataCoalescer.Add(patch, reason);
            }
            catch (Exception ex) { PlayScopeLog.Warning("UpdateSessionData failed", ex); }
        }

        /// <summary>
        /// Records a screen/scene navigation event.
        /// Use to track which screen the player is currently viewing.
        /// </summary>
        /// <param name="screenName">Logical name of the screen (e.g. "MainMenu", "GameplayHUD").</param>
        /// <param name="metadata">Optional additional attributes for this screen transition.</param>
        public static void SetScreen(string screenName, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                var metaJson = metadata != null && metadata.Count > 0 ? EventPipeline.DictToJson(metadata) : null;
                PlayScopeRuntime.Pipeline?.SetScreen(screenName ?? "");
                PlayScopeRuntime.Pipeline?.EnqueueEvent("screen", metadataJson: metaJson);
            }
            catch (Exception ex) { PlayScopeLog.Warning("SetScreen failed", ex); }
        }

        /// <summary>
        /// Records a discrete player action event (button press, item use, etc.).
        /// </summary>
        /// <param name="actionName">Logical name of the action (e.g. "TapPlayButton", "UseHealthPotion").</param>
        /// <param name="metadata">Optional key-value attributes for this action.</param>
        public static void TrackAction(string actionName, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                var metaJson = metadata != null && metadata.Count > 0 ? EventPipeline.DictToJson(metadata) : null;
                PlayScopeRuntime.Pipeline?.SetAction(actionName ?? "");
                PlayScopeRuntime.Pipeline?.EnqueueEvent("action", metadataJson: metaJson);
            }
            catch (Exception ex) { PlayScopeLog.Warning("TrackAction failed", ex); }
        }

        /// <summary>
        /// Starts a timed operation of the given type.
        /// Returns a unique operation ID to pass to <see cref="CompleteOperation"/>.
        /// Returns <see cref="string.Empty"/> when SDK is in disabled state.
        /// </summary>
        /// <param name="type">Category of the operation.</param>
        /// <param name="operationName">Descriptive name for the operation.</param>
        /// <param name="metadata">Optional attributes to attach at start time.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartOperation(OperationType type, string operationName, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return string.Empty;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                var session = PlayScopeRuntime.CurrentSession;
                var operationId = $"{session?.SessionShortId}-{SequenceCounter.Next()}";
                var typeStr = type.ToString();
                EvictOpenOperationsIfOverflow();
                _openOperationTypes[operationId] = typeStr;
                _openOperationStartTicks[operationId] = DateTime.UtcNow.Ticks;
                var merged = new Dictionary<string, object> { ["operation_name"] = operationName ?? "" };
                if (metadata != null) foreach (var kv in metadata) merged[kv.Key] = kv.Value;
                _openOperationStartMetadata[operationId] = merged;
                PlayScopeRuntime.Pipeline?.EnqueueEvent("operation_start", operationId: operationId,
                    operationType: typeStr, metadataJson: EventPipeline.DictToJson(merged));
                return operationId;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("StartOperation failed", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Completes a previously started operation and records its outcome.
        /// Passing an unrecognised or empty <paramref name="operationId"/> is a no-op.
        /// </summary>
        /// <param name="operationId">ID returned by the corresponding <see cref="StartOperation"/> call.</param>
        /// <param name="status">Final outcome of the operation.</param>
        /// <param name="metadata">Optional attributes to attach at completion time.</param>
        public static void CompleteOperation(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (PlayScopeRuntime.IsDisabled || !PlayScopeRuntime.IsInitialized)
                {
                    // Warn once per session before the IsNullOrEmpty(operationId)
                    // bail below — in disabled state StartOperation returns "",
                    // so gating the warning on that check would make it unreachable.
                    if (Interlocked.CompareExchange(ref _disabledCompleteWarned, 1, 0) == 0)
                    {
                        PlayScopeLog.Warning("CompleteOperation called in disabled state — ignored.");
                    }
                    return;
                }
                if (string.IsNullOrEmpty(operationId)) return;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                _openOperationTypes.TryRemove(operationId, out var typeStr);
                // Only stamp our measured duration if the caller didn't supply one —
                // e.g. the HTTP wrapper's figure includes deserialization, more honest.
                _openOperationStartTicks.TryRemove(operationId, out var startTicks);
                _openOperationStartMetadata.TryRemove(operationId, out var startMetadata);

                var merged = new Dictionary<string, object>();
                if (startMetadata != null)
                {
                    foreach (var kv in startMetadata) merged[kv.Key] = kv.Value;
                }
                merged["status"] = status.ToString();
                if (metadata != null) foreach (var kv in metadata) merged[kv.Key] = kv.Value;
                if (startTicks != 0
                    && !merged.ContainsKey("duration_ms") && !merged.ContainsKey("durationMs"))
                {
                    var durationMs = (DateTime.UtcNow.Ticks - startTicks) / TimeSpan.TicksPerMillisecond;
                    if (durationMs >= 0) merged["duration_ms"] = durationMs;
                }
                // Auto-fill scene_progress_samples from the tracker unless the caller passed its own.
                if (typeStr == "SceneLoad" && !merged.ContainsKey("scene_progress_samples"))
                {
                    var samples = SceneLoadProgressTracker.DrainSamples(operationId);
                    if (samples != null) merged["scene_progress_samples"] = samples;
                }
                PlayScopeRuntime.Pipeline?.EnqueueEvent("operation_end", operationId: operationId,
                    operationType: typeStr, metadataJson: EventPipeline.DictToJson(merged));
            }
            catch (Exception ex) { PlayScopeLog.Warning("CompleteOperation failed", ex); }
        }

        // ── HTTP helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed HTTP operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.HTTP"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// </summary>
        /// <param name="name">Request name or URL template (e.g. "GET /api/leaderboard").</param>
        /// <param name="metadata">Optional request-time attributes.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartHTTP(string name, IReadOnlyDictionary<string, object> metadata = null)
            => StartOperation(OperationType.HTTP, name, metadata);

        /// <summary>
        /// Completes an HTTP operation started with <see cref="StartHTTP"/>.
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartHTTP"/>.</param>
        /// <param name="status">Final outcome of the request.</param>
        /// <param name="metadata">Optional response-time attributes (e.g. status_code).</param>
        public static void EndHTTP(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null)
            => CompleteOperation(operationId, status, metadata);

        // ── ResourceLoad helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed resource-load operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.ResourceLoad"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// </summary>
        /// <param name="name">Asset or bundle name being loaded.</param>
        /// <param name="metadata">Optional load-time attributes.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartResourceLoad(string name, IReadOnlyDictionary<string, object> metadata = null)
            => StartOperation(OperationType.ResourceLoad, name, metadata);

        /// <summary>
        /// Completes a resource-load operation started with <see cref="StartResourceLoad"/>.
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartResourceLoad"/>.</param>
        /// <param name="status">Final outcome of the load.</param>
        /// <param name="metadata">Optional completion-time attributes.</param>
        public static void EndResourceLoad(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null)
            => CompleteOperation(operationId, status, metadata);

        // ── SceneLoad helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed scene-load operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.SceneLoad"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// </summary>
        /// <param name="sceneName">Unity scene name being loaded.</param>
        /// <param name="metadata">Optional load-time attributes.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartSceneLoad(string sceneName, IReadOnlyDictionary<string, object> metadata = null)
            => StartOperation(OperationType.SceneLoad, sceneName, metadata);

        /// <summary>
        /// Convenience overload that also wires up periodic progress sampling.
        /// Pass the <see cref="UnityEngine.AsyncOperation"/> returned by
        /// <c>SceneManager.LoadSceneAsync</c> (or Addressables' equivalent);
        /// the SDK polls <see cref="UnityEngine.AsyncOperation.progress"/> every
        /// 250 ms on the main thread and drains the samples into the matching
        /// <see cref="EndSceneLoad"/> call's metadata as <c>scene_progress_samples</c>.
        /// </summary>
        /// <param name="sceneName">Scene name being loaded.</param>
        /// <param name="op">The async handle whose progress we should sample.</param>
        /// <param name="metadata">Optional load-time attributes.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartSceneLoad(string sceneName, UnityEngine.AsyncOperation op,
            IReadOnlyDictionary<string, object> metadata = null)
        {
            var id = StartOperation(OperationType.SceneLoad, sceneName, metadata);
            if (!string.IsNullOrEmpty(id) && op != null)
            {
                try { SceneLoadProgressTracker.Begin(id, op); }
                catch (Exception ex) { PlayScopeLog.Warning("SceneLoadProgress wire-up failed", ex); }
            }
            return id;
        }

        /// <summary>
        /// Records an explicit progress reading for a previously-started
        /// scene load (or any operation whose progress is being sampled).
        /// Most callers should prefer the overload that takes an
        /// <see cref="UnityEngine.AsyncOperation"/> and let the SDK sample;
        /// this is for code that already polls progress on its own loop.
        /// </summary>
        public static void RecordSceneLoadProgress(string operationId, float progress)
        {
            try { SceneLoadProgressTracker.RecordSample(operationId, progress); }
            catch (Exception ex) { PlayScopeLog.Warning("RecordSceneLoadProgress failed", ex); }
        }

        /// <summary>
        /// Completes a scene-load operation started with <see cref="StartSceneLoad"/>.
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartSceneLoad"/>.</param>
        /// <param name="status">Final outcome of the load.</param>
        /// <param name="metadata">Optional completion-time attributes.</param>
        public static void EndSceneLoad(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null)
            => CompleteOperation(operationId, status, metadata);

        // ── Purchase helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed purchase operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.Purchase"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// <para>
        /// For the canonical metadata schema (<c>store</c> / <c>currency</c> /
        /// <c>price_amount</c> / <c>is_restore</c>) use
        /// <see cref="PurchaseMetadata.BuildStartMetadata"/> — the dashboard
        /// surfaces those keys as first-class fields in PurchaseDetails.
        /// </para>
        /// </summary>
        /// <param name="productId">Store product identifier being purchased.</param>
        /// <param name="metadata">Optional purchase-initiation attributes; build via <see cref="PurchaseMetadata.BuildStartMetadata"/> for the canonical schema.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartPurchase(string productId, IReadOnlyDictionary<string, object> metadata = null)
            => StartOperation(OperationType.Purchase, productId, metadata);

        /// <summary>
        /// Completes a purchase operation started with <see cref="StartPurchase"/>.
        /// <para>
        /// For the canonical end-of-purchase metadata schema
        /// (<c>transaction_id_hash</c> / <c>validation_status</c> /
        /// <c>failure_reason</c>) use <see cref="PurchaseMetadata.BuildEndMetadata"/>.
        /// The helper SHA-256-16-hashes the raw transaction ID before
        /// it ever leaves the device.
        /// </para>
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartPurchase"/>.</param>
        /// <param name="status">Final outcome of the purchase (Success, Failure, Cancelled, etc.).</param>
        /// <param name="metadata">Optional completion-time attributes; build via <see cref="PurchaseMetadata.BuildEndMetadata"/> for the canonical schema.</param>
        public static void EndPurchase(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null)
            => CompleteOperation(operationId, status, metadata);

        // ── Ad-impression helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed ad-impression operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.Ad"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// <para>
        /// For the canonical metadata schema (<c>network</c> / <c>placement</c> /
        /// <c>ad_type</c>) use <see cref="AdMetadata.BuildStartMetadata"/> — the
        /// dashboard surfaces those keys as first-class fields on /revenue and
        /// /errors crash-during-ad correlation.
        /// </para>
        /// </summary>
        /// <param name="placement">Integrator-defined placement ID (e.g. "Rewarded_GameOver_v3"). Used as the operation name.</param>
        /// <param name="metadata">Optional ad-initiation attributes; build via <see cref="AdMetadata.BuildStartMetadata"/> for the canonical schema.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartAd(string placement, IReadOnlyDictionary<string, object> metadata = null)
            => StartOperation(OperationType.Ad, placement, metadata);

        /// <summary>
        /// Completes an ad-impression operation started with <see cref="StartAd"/>.
        /// <para>
        /// For the canonical end-of-impression metadata schema (<c>result</c> /
        /// <c>revenue</c> / <c>currency</c>) use <see cref="AdMetadata.BuildEndMetadata"/>.
        /// Negative revenue is clamped to 0 by the helper before it leaves the device.
        /// </para>
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartAd"/>.</param>
        /// <param name="status">Final outcome of the ad impression (Success, Failure, Cancelled, etc.).</param>
        /// <param name="metadata">Optional completion-time attributes; build via <see cref="AdMetadata.BuildEndMetadata"/> for the canonical schema.</param>
        public static void EndAd(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null)
            => CompleteOperation(operationId, status, metadata);

        // ── Logging ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Manually tracks a log entry with the given level and message.
        /// Use when auto-capture is disabled or you need structured metadata alongside the log.
        /// </summary>
        /// <param name="level">Severity level of the log entry.</param>
        /// <param name="message">Log message text.</param>
        /// <param name="metadata">Optional structured attributes for this log entry.</param>
        public static void TrackLog(LogLevel level, string message, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                var metaJson = metadata != null && metadata.Count > 0 ? EventPipeline.DictToJson(metadata) : null;
                PlayScopeRuntime.Pipeline?.EnqueueLog(level.ToString().ToLower(), message ?? "", metadataJson: metaJson);
            }
            catch (Exception ex) { PlayScopeLog.Warning("TrackLog failed", ex); }
        }

        /// <summary>
        /// Tracks a caught exception with optional structured metadata.
        /// Does not rethrow — safe to call from any catch block.
        /// </summary>
        /// <param name="exception">The exception to record.</param>
        /// <param name="metadata">Optional contextual attributes (e.g. context, user_action).</param>
        public static void TrackException(System.Exception exception, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                if (exception == null) return;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                var metaJson = metadata != null && metadata.Count > 0 ? EventPipeline.DictToJson(metadata) : null;
                PlayScopeRuntime.Pipeline?.EnqueueLog("exception", exception.Message, exception.StackTrace, metadataJson: metaJson);
            }
            catch (Exception ex) { PlayScopeLog.Warning("TrackException failed", ex); }
        }

        // ── Restart ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Records an in-game restart — a point at which the game logically
        /// throws away the current profile (player chose "New game", restart
        /// after defeat, full progress reset, etc.). Distinct from an app
        /// restart, which already produces a new session.
        /// </summary>
        /// <param name="reason">Short human-readable cause: "new_game",
        /// "defeat_restart", "settings_reset", etc. Free-form but keep it
        /// consistent across call sites — the dashboard groups restarts by
        /// reason. Promoted from <c>metadata</c> to its own positional
        /// parameter because it's the field reviewers always want first.</param>
        /// <param name="metadata">Optional additional attributes (e.g.
        /// <c>from_level=12</c>, <c>character_name=guest_1234</c>) — surface
        /// alongside the reason in the dashboard's restart details panel.</param>
        /// <remarks>
        /// Re-arms the SetInitialState lock — push a fresh
        /// <see cref="SetInitialState"/> for the post-restart period; the
        /// dashboard rebuilds from the most recent <c>state_initial</c>.
        /// Session-data (device / environment / addressables) is NOT reset.
        /// </remarks>
        public static void TrackRestart(string reason = null, IReadOnlyDictionary<string, object> metadata = null)
        {
            try
            {
                if (!PlayScopeRuntime.IsInitialized || PlayScopeRuntime.IsDisabled) return;
                metadata = SensitiveKeyFilter.FilterMetadata(metadata);
                // Explicit reason param wins over any "reason" key in metadata.
                Dictionary<string, object> merged = null;
                if (!string.IsNullOrEmpty(reason))
                {
                    merged = new Dictionary<string, object> { ["reason"] = reason };
                    if (metadata != null)
                        foreach (var kv in metadata)
                            if (kv.Key != "reason") merged[kv.Key] = kv.Value;
                }
                else if (metadata != null && metadata.Count > 0)
                {
                    merged = new Dictionary<string, object>(metadata.Count);
                    foreach (var kv in metadata) merged[kv.Key] = kv.Value;
                }
                var metaJson = merged != null && merged.Count > 0
                    ? EventPipeline.DictToJson(merged) : null;
                // Flush in-flight patches before the marker so per-restart replay
                // sees a clean boundary, not patches straddling the divider.
                PlayScopeRuntime.StatePatchCoalescer.FlushNow();
                PlayScopeRuntime.Pipeline?.EnqueueEvent("restart", metadataJson: metaJson);
                PlayScopeRuntime.ResetInitialStateLock();
            }
            catch (Exception ex) { PlayScopeLog.Warning("TrackRestart failed", ex); }
        }
    }
}
