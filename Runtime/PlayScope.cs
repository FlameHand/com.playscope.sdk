using System.Collections.Generic;

namespace PlayScopeSdk
{
    /// <summary>
    /// Main entry point for the PlayScope SDK.
    /// All methods are thread-safe and never throw exceptions.
    /// When the SDK is in disabled state (missing or empty API key), all calls become no-ops.
    /// </summary>
    public static class PlayScope
    {
        /// <summary>
        /// Initializes the SDK with the provided configuration.
        /// Must be called once before using any other SDK methods.
        /// Subsequent calls are a warning + no-op — the first call wins.
        /// </summary>
        /// <param name="context">Configuration including API key and optional settings.</param>
        public static void Initialize(PlayScopeContext context)
        {
            Internal.PlayScopeRuntime.Initialize(context);
        }

        /// <summary>
        /// Associates the current session with a user identity.
        /// Safe to call multiple times — each call updates the current identity.
        /// </summary>
        /// <param name="customUserId">Your application-side user identifier.</param>
        /// <param name="metadata">Optional key-value attributes to attach to the user (e.g. plan, region).</param>
        public static void SetUserData(string customUserId, IReadOnlyDictionary<string, object> metadata = null) { }

        /// <summary>
        /// Sets the full initial game state snapshot for the session.
        /// Second call is a warning + ignored — use <see cref="UpdateState"/> for incremental changes.
        /// </summary>
        /// <param name="state">Full state dictionary (e.g. level, currency, inventory).</param>
        public static void SetInitialState(IReadOnlyDictionary<string, object> state) { }

        /// <summary>
        /// Applies a partial patch to the current game state.
        /// Only the provided keys are updated; unmentioned keys remain unchanged.
        /// </summary>
        /// <param name="patch">Dictionary of keys to update in the current state.</param>
        public static void UpdateState(IReadOnlyDictionary<string, object> patch) { }

        /// <summary>
        /// Records a screen/scene navigation event.
        /// Use to track which screen the player is currently viewing.
        /// </summary>
        /// <param name="screenName">Logical name of the screen (e.g. "MainMenu", "GameplayHUD").</param>
        /// <param name="metadata">Optional additional attributes for this screen transition.</param>
        public static void SetScreen(string screenName, IReadOnlyDictionary<string, object> metadata = null) { }

        /// <summary>
        /// Records a discrete player action event (button press, item use, etc.).
        /// </summary>
        /// <param name="actionName">Logical name of the action (e.g. "TapPlayButton", "UseHealthPotion").</param>
        /// <param name="metadata">Optional key-value attributes for this action.</param>
        public static void TrackAction(string actionName, IReadOnlyDictionary<string, object> metadata = null) { }

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
            => string.Empty;

        /// <summary>
        /// Completes a previously started operation and records its outcome.
        /// Passing an unrecognised or empty <paramref name="operationId"/> is a no-op.
        /// </summary>
        /// <param name="operationId">ID returned by the corresponding <see cref="StartOperation"/> call.</param>
        /// <param name="status">Final outcome of the operation.</param>
        /// <param name="metadata">Optional attributes to attach at completion time.</param>
        public static void CompleteOperation(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null) { }

        // ── HTTP helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed HTTP operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.HTTP"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// </summary>
        /// <param name="name">Request name or URL template (e.g. "GET /api/leaderboard").</param>
        /// <param name="metadata">Optional request-time attributes.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartHTTP(string name, IReadOnlyDictionary<string, object> metadata = null)
            => string.Empty;

        /// <summary>
        /// Completes an HTTP operation started with <see cref="StartHTTP"/>.
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartHTTP"/>.</param>
        /// <param name="status">Final outcome of the request.</param>
        /// <param name="metadata">Optional response-time attributes (e.g. status_code).</param>
        public static void EndHTTP(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null) { }

        // ── ResourceLoad helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed resource-load operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.ResourceLoad"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// </summary>
        /// <param name="name">Asset or bundle name being loaded.</param>
        /// <param name="metadata">Optional load-time attributes.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartResourceLoad(string name, IReadOnlyDictionary<string, object> metadata = null)
            => string.Empty;

        /// <summary>
        /// Completes a resource-load operation started with <see cref="StartResourceLoad"/>.
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartResourceLoad"/>.</param>
        /// <param name="status">Final outcome of the load.</param>
        /// <param name="metadata">Optional completion-time attributes.</param>
        public static void EndResourceLoad(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null) { }

        // ── SceneLoad helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed scene-load operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.SceneLoad"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// </summary>
        /// <param name="sceneName">Unity scene name being loaded.</param>
        /// <param name="metadata">Optional load-time attributes.</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartSceneLoad(string sceneName, IReadOnlyDictionary<string, object> metadata = null)
            => string.Empty;

        /// <summary>
        /// Completes a scene-load operation started with <see cref="StartSceneLoad"/>.
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartSceneLoad"/>.</param>
        /// <param name="status">Final outcome of the load.</param>
        /// <param name="metadata">Optional completion-time attributes.</param>
        public static void EndSceneLoad(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null) { }

        // ── Purchase helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Starts a timed purchase operation. Shorthand for <see cref="StartOperation"/> with <see cref="OperationType.Purchase"/>.
        /// Returns <see cref="string.Empty"/> in disabled state.
        /// </summary>
        /// <param name="productId">Store product identifier being purchased.</param>
        /// <param name="metadata">Optional purchase-initiation attributes (e.g. store, currency).</param>
        /// <returns>An opaque operation ID, or <see cref="string.Empty"/> in disabled state.</returns>
        public static string StartPurchase(string productId, IReadOnlyDictionary<string, object> metadata = null)
            => string.Empty;

        /// <summary>
        /// Completes a purchase operation started with <see cref="StartPurchase"/>.
        /// </summary>
        /// <param name="operationId">ID returned by <see cref="StartPurchase"/>.</param>
        /// <param name="status">Final outcome of the purchase (Success, Failure, Cancelled, etc.).</param>
        /// <param name="metadata">Optional completion-time attributes (e.g. transaction_id, price).</param>
        public static void EndPurchase(string operationId, OperationCompletionStatus status, IReadOnlyDictionary<string, object> metadata = null) { }

        // ── Logging ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Manually tracks a log entry with the given level and message.
        /// Use when auto-capture is disabled or you need structured metadata alongside the log.
        /// </summary>
        /// <param name="level">Severity level of the log entry.</param>
        /// <param name="message">Log message text.</param>
        /// <param name="metadata">Optional structured attributes for this log entry.</param>
        public static void TrackLog(LogLevel level, string message, IReadOnlyDictionary<string, object> metadata = null) { }

        /// <summary>
        /// Tracks a caught exception with optional structured metadata.
        /// Does not rethrow — safe to call from any catch block.
        /// </summary>
        /// <param name="exception">The exception to record.</param>
        /// <param name="metadata">Optional contextual attributes (e.g. context, user_action).</param>
        public static void TrackException(System.Exception exception, IReadOnlyDictionary<string, object> metadata = null) { }
    }
}
