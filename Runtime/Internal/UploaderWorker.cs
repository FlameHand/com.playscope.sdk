using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using PlayScopeSdk.Core.Session;
using PlayScopeSdk.Storage;

namespace PlayScopeSdk.Internal
{
    internal sealed class UploaderWorker
    {
        // ── Configuration ─────────────────────────────────────────────────────────
        private const double PollingIntervalSeconds = 30.0;
        private const double RetryTtlDays = 7.0;
        private const double DeadLetterTtlDays = 7.0;

        // Backoff base seconds indexed by attempt (0-based, clamped at index 4)
        private static readonly double[] BackoffBase = { 5, 30, 120, 600, 1800 };

        // ── State ─────────────────────────────────────────────────────────────────
        private readonly PlayScopeContext _context;
        private readonly SessionInfo _session;
        private readonly UploadQueue _queue;
        private CancellationTokenSource _cts;

        // Thread-local jitter RNG — UnityEngine.Random is main-thread only.
        private static readonly System.Random _jitterRng = new System.Random();

        // Semaphore used to wake the poll loop on-demand (instant upload trigger)
        private readonly SemaphoreSlim _wakeSignal = new SemaphoreSlim(0, 1);

        // ── Constructor ───────────────────────────────────────────────────────────
        internal UploaderWorker(PlayScopeContext context, SessionInfo session, UploadQueue queue)
        {
            _context = context;
            _session = session;
            _queue = queue;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        internal void Start()
        {
            _cts = new CancellationTokenSource();
            CleanDeadLetterOldFiles();
            RecoverPendingChunks();
            RunAsync(_cts.Token).Forget();
        }

        internal void Stop()
        {
            _cts?.Cancel();
            // Wake the loop so it exits cleanly
            TryReleaseSemaphore();
        }

        /// <summary>
        /// Wake the upload loop immediately (skips the 30s polling delay).
        /// Call after enqueuing critical events (exception, error, session_end).
        /// </summary>
        internal void TriggerInstantUpload()
        {
            TryReleaseSemaphore();
        }

        // ── Main loop ─────────────────────────────────────────────────────────────

        private async UniTaskVoid RunAsync(CancellationToken ct)
        {
            while (true)
            {
                // Wait for either a wake signal or the polling timeout (30s ±20% jitter per spec).
                double jitterFactor;
                lock (_jitterRng) { jitterFactor = 0.8 + _jitterRng.NextDouble() * 0.4; }
                var waitSeconds = PollingIntervalSeconds * jitterFactor;
                try
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(waitSeconds), ct);
                }
                catch (OperationCanceledException) { /* fall through to one final drain */ }
                catch (Exception) { /* timeout is fine — proceed to upload cycle */ }

                // ALWAYS drain once per wake, even on cancellation — Shutdown
                // (enqueue session_end → TriggerInstantUpload → Stop) could
                // otherwise race the cancel and skip the final upload. None token
                // so the in-flight request finishes (OS kills us past the budget anyway).
                try { await ProcessQueueAsync(CancellationToken.None); }
                catch (Exception ex) { PlayScopeLog.Warning("Uploader drain error", ex); }

                if (ct.IsCancellationRequested) break;
            }
        }

        // ── Queue processing ──────────────────────────────────────────────────────

        private async UniTask ProcessQueueAsync(CancellationToken ct)
        {
            // Gather all paths from the in-memory queue
            var paths = new List<string>();
            while (_queue.TryDequeue(out var path))
                paths.Add(path!);

            // Also scan the UploadQueueDir for state files that are due for retry
            GatherRetryablePaths(paths);

            if (paths.Count == 0) return; // silent — most ticks have nothing to do

            // Per-pass counters → one summary line instead of N.
            int succeeded = 0, retryScheduled = 0, deadLettered = 0, rescued = 0, alreadyUploaded = 0;
            _passSucceeded = 0; _passRetried = 0; _passDeadLettered = 0; _passRescued = 0; _passAlreadyUploaded = 0;

            PlayScopeLog.Info($"UploaderWorker: pass starting with {paths.Count} chunk(s) to process.");

            foreach (var chunkPath in paths)
            {
                if (ct.IsCancellationRequested) break;
                if (!File.Exists(chunkPath)) continue;
                await UploadChunkAsync(chunkPath, ct);
            }

            succeeded       = _passSucceeded;
            retryScheduled  = _passRetried;
            deadLettered    = _passDeadLettered;
            rescued         = _passRescued;
            alreadyUploaded = _passAlreadyUploaded;

            // Warn if any chunk dead-lettered (permanent loss), else Info.
            var summary = $"UploaderWorker: pass done. " +
                          $"ok={succeeded} retry={retryScheduled} dead={deadLettered} " +
                          $"rescued={rescued} already_uploaded={alreadyUploaded} " +
                          $"total={paths.Count}";
            if (deadLettered > 0) PlayScopeLog.Warning(summary);
            else                  PlayScopeLog.Info(summary);
        }

        // Per-pass tallies. Not thread-safe — uploader passes are strictly sequential.
        private int _passSucceeded;
        private int _passRetried;
        private int _passDeadLettered;
        private int _passRescued;
        private int _passAlreadyUploaded;

        /// <summary>
        /// Scan UploadQueueDir for .jsonl files that have a state file with a past next_retry_at,
        /// or that have no state file yet (freshly added from a previous crash recovery).
        /// </summary>
        private void GatherRetryablePaths(List<string> paths)
        {
            var dir = PlayScopeDirectory.UploadQueue;
            if (!Directory.Exists(dir)) return;

            var ownPrefix = $"chunk_{_session.SessionShortId}_";
            var now = DateTime.UtcNow;
            try
            {
                foreach (var stateFile in Directory.GetFiles(dir, "*.state.json"))
                {
                    var state = LoadState(stateFile);
                    if (state == null) continue;
                    if (state.IsUploaded) continue;

                    var chunk = ChunkPathFromStateFile(stateFile, state);

                    // Ownership check: a PRIOR session's leftover state file must not
                    // pull its chunk into THIS session's loop, or it uploads under our
                    // envelope and commingles sessions. Non-matching prefix → drop the
                    // state file (stop re-checking); the chunk stays for SessionRecovery.
                    // Completed-session chunks are EXEMPT: they attribute via their
                    // bundled manifest, and purging their state would reset Attempts/TTL
                    // on every pass (crash data got one attempt per launch).
                    var chunkId = state.ChunkId ?? "";
                    if (!IsUnderCompletedSessions(chunk) &&
                        !chunkId.StartsWith(ownPrefix, StringComparison.Ordinal))
                    {
                        PlayScopeLog.Warning(
                            $"UploaderWorker: dropping orphan state file '{Path.GetFileName(stateFile)}' " +
                            $"(does not belong to current session '{_session.SessionShortId}'). " +
                            "SessionRecovery should relocate the chunk on next start.");
                        TryDeleteFile(stateFile);
                        continue;
                    }

                    // TTL anchors on CreatedAt, not LastAttemptAt — else a chunk that keeps
                    // failing resets its own TTL each retry. Legacy files fall back to LastAttemptAt.
                    var ttlAnchor = state.CreatedAt ?? state.LastAttemptAt;
                    if (ttlAnchor.HasValue &&
                        (now - ttlAnchor.Value).TotalDays >= RetryTtlDays)
                    {
                        MoveToDeadLetter(chunk, stateFile);
                        continue;
                    }

                    // Check whether retry window has elapsed
                    if (state.NextRetryAt.HasValue && state.NextRetryAt.Value > now)
                        continue; // still waiting

                    if (File.Exists(chunk) && !paths.Contains(chunk))
                        paths.Add(chunk);
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"Error scanning upload queue dir: " + ex.Message);
            }
        }

        // ── Single chunk upload ───────────────────────────────────────────────────

        private async UniTask UploadChunkAsync(string chunkPath, CancellationToken ct)
        {
            var chunkName = Path.GetFileName(chunkPath);
            var stateFilePath = Path.Combine(PlayScopeDirectory.UploadQueue, chunkName + ".state.json");

            // Load or create state. New state files record CreatedAt at first attempt; this
            // anchors the 7-day retry TTL (issue #9).
            var state = LoadState(stateFilePath) ?? new UploadState { ChunkId = chunkName, CreatedAt = DateTime.UtcNow };
            if (!state.CreatedAt.HasValue) state.CreatedAt = DateTime.UtcNow;
            // Record where the chunk actually lives (root-relative) — recovered chunks
            // sit under completed_sessions/, not chunks/, and retry passes must find them there.
            state.ChunkDirRel = ToRootRelativeDir(chunkPath);

            // Build envelope
            byte[] payload;
            try
            {
                payload = BuildGzipPayload(chunkPath);
            }
            catch (InvalidOperationException ex)
            {
                // Foreign short-id that SessionRecovery failed to relocate. Self-rescue:
                // find a completed_sessions manifest matching the chunk's short-id and
                // move it there for the next pass; dead-letter only if that fails.
                if (TryRescueOrphanChunk(chunkPath, stateFilePath, out var rescueDest))
                {
                    _passRescued++;
                    PlayScopeLog.Info(
                        $"Rescued orphan chunk '{chunkName}' to {rescueDest}. " +
                        "It will upload under its original session_id on the next pass.");
                    return;
                }

                PlayScopeLog.Warning(
                    $"Orphan chunk {chunkName}: {ex.Message} " +
                    "SessionRecovery missed it at startup AND no matching completed_sessions " +
                    "manifest was found to attribute it. Moving to dead letter.");
                MoveToDeadLetter(chunkPath, stateFilePath);
                return;
            }
            catch (Exception ex)
            {
                // Other build failure (corrupt UTF-8, IO, gzip). Treat as retryable:
                // bump Attempts + schedule backoff so the 7-day TTL eventually
                // dead-letters it, instead of silently re-trying forever every poll.
                state.Attempts++;
                state.LastAttemptAt = DateTime.UtcNow;
                double buildFailBackoffSeconds = BackoffBase[Math.Min(state.Attempts - 1, BackoffBase.Length - 1)];
                double buildFailRand;
                lock (_jitterRng) { buildFailRand = _jitterRng.NextDouble(); }
                double buildFailJitter = buildFailBackoffSeconds * 0.2 * (buildFailRand * 2.0 - 1.0); // ±20%
                state.NextRetryAt = DateTime.UtcNow.AddSeconds(buildFailBackoffSeconds + buildFailJitter);
                SaveState(stateFilePath, state);
                _passRetried++;
                PlayScopeLog.Warning(
                    $"Failed to build payload for {chunkName} (attempt #{state.Attempts}): {ex.Message} " +
                    $"— next retry at {state.NextRetryAt:o}");
                return;
            }

            var endpoint = (_context.UploadEndpoint?.TrimEnd('/') ?? "https://api.playscope.dev") + "/v1/ingest";

            state.Attempts++;
            state.LastAttemptAt = DateTime.UtcNow;
            SaveState(stateFilePath, state);

            int httpStatus = 0;
            string responseBody = "";
            bool networkError = false;

            try
            {
                (httpStatus, responseBody) = await SendRequestAsync(endpoint, payload, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"Network error uploading {chunkName}: {ex.Message}");
                networkError = true;
            }

            if (!networkError && httpStatus >= 200 && httpStatus < 300)
            {
                // Two-phase to survive a kill between the 200 and the delete:
                // (1) make "done" DURABLE first — state.IsUploaded=true AND a
                // .uploaded marker (both, because a state file can be quota-pruned
                // and a marker can be missed if the chunk never reached the queue
                // dir); (2) only THEN delete chunk + marker + state to free disk.
                state.IsUploaded = true;
                SaveState(stateFilePath, state);
                long sizeBytes = 0;
                try { sizeBytes = new FileInfo(chunkPath).Length; } catch { /* best-effort */ }
                // Show raw bytes under 1 KB — "(0 KB)" rounding looked like an empty
                // chunk for a real ~300-500 byte synthetic session_end line.
                var sizeLabel = sizeBytes >= 1024
                    ? $"{sizeBytes / 1024} KB"
                    : $"{sizeBytes} B";
                bool markerWritten = false;
                try
                {
                    File.WriteAllText(chunkPath + ".uploaded", "1");
                    markerWritten = true;
                }
                catch (Exception ex)
                {
                    PlayScopeLog.Warning($"Could not write .uploaded marker for {chunkName}: {ex.Message}");
                }

                TryDeleteFile(chunkPath);
                // Clear the marker only after the chunk file is gone — if
                // the chunk still exists the marker has to stick around so
                // SessionRecovery sees it on the next launch.
                if (markerWritten && !File.Exists(chunkPath))
                    TryDeleteFile(chunkPath + ".uploaded");
                TryDeleteFile(stateFilePath);

                _passSucceeded++;
                // Per-chunk Info — visible when MinLogLevel=Info. Gives an
                // explicit "the upload pipeline actually works" signal
                // during integration / debugging. Suppressed at Warning+.
                PlayScopeLog.Info($"Uploaded {chunkName} ({sizeLabel}) → HTTP {httpStatus}");
                return;
            }

            // Non-retryable errors → dead letter (spec: 400/401/402/403/422).
            // 409 was removed — it indicates an idempotency/conflict that should retry.
            if (!networkError && (httpStatus == 400 || httpStatus == 401 || httpStatus == 402 ||
                                   httpStatus == 403 || httpStatus == 422))
            {
                var bodySnippet = string.IsNullOrEmpty(responseBody)
                    ? ""
                    : " body=" + (responseBody.Length > 512 ? responseBody.Substring(0, 512) + "…" : responseBody);
                PlayScopeLog.Warning($"Non-retryable HTTP {httpStatus} for {chunkName} — moving to dead letter.{bodySnippet}");
                MoveToDeadLetter(chunkPath, stateFilePath);
                _passDeadLettered++;
                return;
            }

            // Retryable (429, 5xx, network error) → schedule next retry
            double baseSeconds = BackoffBase[Math.Min(state.Attempts - 1, BackoffBase.Length - 1)];
            double rand;
            lock (_jitterRng) { rand = _jitterRng.NextDouble(); }
            double jitter = baseSeconds * 0.2 * (rand * 2.0 - 1.0); // ±20%
            var nextRetry = DateTime.UtcNow.AddSeconds(baseSeconds + jitter);
            state.NextRetryAt = nextRetry;
            SaveState(stateFilePath, state);

            _passRetried++;
            if (!networkError)
                PlayScopeLog.Warning($"HTTP {httpStatus} for {chunkName}, retry #{state.Attempts} at {nextRetry:o}");
            else
                PlayScopeLog.Warning($"Network error for {chunkName}, retry #{state.Attempts} at {nextRetry:o}");
        }

        // ── HTTP ──────────────────────────────────────────────────────────────────

        private async UniTask<(int Status, string Body)> SendRequestAsync(string url, byte[] gzipBody, CancellationToken ct)
        {
            using var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(gzipBody);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Content-Encoding", "gzip");
            request.SetRequestHeader("Authorization", "Bearer " + _context.SdkKey);

            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                ct.ThrowIfCancellationRequested();
                await UniTask.Yield();
            }

#if UNITY_2020_1_OR_NEWER
            if (request.result == UnityWebRequest.Result.ConnectionError ||
                request.result == UnityWebRequest.Result.DataProcessingError)
                throw new Exception(request.error);
#else
            if (request.isNetworkError)
                throw new Exception(request.error);
#endif

            var body = "";
            try { body = request.downloadHandler?.text ?? ""; } catch { /* best-effort */ }
            return ((int)request.responseCode, body);
        }

        // ── Payload building ──────────────────────────────────────────────────────

        private byte[] BuildGzipPayload(string chunkPath)
        {
            var lines = File.ReadAllLines(chunkPath, new UTF8Encoding(false));

            var events = new List<string>();
            var logs = new List<string>();
            var metrics = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var recType = ExtractRecordType(line);
                switch (recType)
                {
                    case "event":   events.Add(line);  break;
                    case "log":     logs.Add(line);    break;
                    case "metric":  metrics.Add(line); break;
                    // Unknown record types are dropped silently
                }
            }

            // Recovered chunks attribute to the crashed session via their bundled
            // manifest, not the live one (throws if it's missing — see below).
            ResolveEnvelopeIdentity(chunkPath,
                out var envelopeSessionId, out var envelopeSdkVersion, out var envelopeSchemaVersion);

            // Backend rejects batch_id > 128 chars; truncate-and-warn (not drop) so a
            // future filename change can't silently lose data — the payload outranks the id.
            var batchId = Path.GetFileNameWithoutExtension(chunkPath);
            if (batchId != null && batchId.Length > 128)
            {
                PlayScopeLog.Warning(
                    $"[Uploader] batch_id length exceeded 128 chars (was {batchId.Length}), truncating. chunk={chunkPath}");
                batchId = batchId.Substring(0, 128);
            }

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"session_id\":\"").Append(EscapeJsonString(envelopeSessionId)).Append("\",");
            sb.Append("\"sdk_version\":\"").Append(EscapeJsonString(envelopeSdkVersion)).Append("\",");
            sb.Append("\"schema_version\":").Append(envelopeSchemaVersion).Append(",");
            sb.Append("\"batch_id\":\"").Append(EscapeJsonString(batchId)).Append("\",");
            sb.Append("\"sent_at\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append("\",");
            sb.Append("\"events\":[").Append(string.Join(",", events)).Append("],");
            sb.Append("\"logs\":[").Append(string.Join(",", logs)).Append("],");
            sb.Append("\"metrics\":[").Append(string.Join(",", metrics)).Append("]");
            sb.Append("}");

            var jsonBytes = Encoding.UTF8.GetBytes(sb.ToString());

            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
                gz.Write(jsonBytes, 0, jsonBytes.Length);
            return ms.ToArray();
        }

        /// <summary>
        /// Resolves the envelope session_id / sdk_version / schema_version.
        /// Live-dir chunks use the live SessionInfo; recovered chunks under
        /// completed_sessions/ read their bundled session.json so the envelope
        /// describes the ORIGINAL session. A missing/unreadable manifest throws
        /// <see cref="InvalidOperationException"/> (so the caller dead-letters)
        /// rather than commingling two sessions under one backend session_id.
        /// </summary>
        private void ResolveEnvelopeIdentity(string chunkPath,
            out string sessionId, out string sdkVersion, out int schemaVersion)
        {
            sessionId = _session.SessionId;
            sdkVersion = _session.SdkVersion;
            schemaVersion = _session.SchemaVersion;

            var chunkDir = Path.GetDirectoryName(chunkPath);
            if (string.IsNullOrEmpty(chunkDir)) return;

            var completedRoot = PlayScopeDirectory.CompletedSessions;
            if (!chunkDir.StartsWith(completedRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Live identity is correct only if the filename belongs to this
                // session. A foreign short-id in chunks/ is an orphan SessionRecovery
                // failed to relocate — refuse it (dead-letter) rather than commingle.
                var chunkName = Path.GetFileName(chunkPath);
                var ownPrefix = $"chunk_{_session.SessionShortId}_";
                if (!chunkName.StartsWith(ownPrefix, StringComparison.Ordinal) &&
                    !string.Equals(chunkName, "chunk_current.jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Live-dir chunk '{chunkName}' has a foreign short-id (expected prefix '{ownPrefix}'). " +
                        "Refusing to upload under the live session — would commingle data from a prior run.");
                }
                return;
            }

            // Recovered chunk: the manifest is the ONLY source of truth — no fallback.
            var manifestPath = Path.Combine(chunkDir, "session.json");
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException(
                    $"Recovered chunk '{Path.GetFileName(chunkPath)}' has no session.json manifest " +
                    $"in {chunkDir}. Refusing to upload — would risk attributing to wrong session.");

            Dictionary<string, object> dto;
            try
            {
                var json = File.ReadAllText(manifestPath, new UTF8Encoding(false));
                dto = SimpleJson.Deserialize(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Recovered chunk '{Path.GetFileName(chunkPath)}' has an unreadable manifest: {ex.Message}. " +
                    "Refusing to upload — would risk attributing to wrong session.", ex);
            }
            if (dto == null || !dto.TryGetValue("session_id", out var sid)
                || sid is not string sidStr || string.IsNullOrEmpty(sidStr))
                throw new InvalidOperationException(
                    $"Recovered chunk '{Path.GetFileName(chunkPath)}' has a manifest with no session_id. " +
                    "Refusing to upload — would risk attributing to wrong session.");

            sessionId = sidStr;
            if (dto.TryGetValue("sdk_version", out var sv) && sv is string svStr && !string.IsNullOrEmpty(svStr))
                sdkVersion = svStr;
            if (dto.TryGetValue("schema_version", out var schv))
            {
                if (schv is int schvInt) schemaVersion = schvInt;
                else if (schv is string schvStr && int.TryParse(schvStr, out var parsed)) schemaVersion = parsed;
            }
        }

        /// <summary>
        /// Fast extraction of the "record_type" field from a JSONL line.
        /// Looks for "record_type":"value" without full deserialization.
        /// </summary>
        private static string ExtractRecordType(string line)
        {
            const string key = "\"record_type\":\"";
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + key.Length;
            int end = line.IndexOf('"', start);
            if (end < 0) return null;
            return line.Substring(start, end - start);
        }

        /// <summary>
        /// JSON escape incl. C0 controls — a stray control char in a corrupt
        /// session_id once produced invalid JSON the backend 400'd on every retry.
        /// Mirrors EventPipeline.AppendEscapedString.
        /// </summary>
        private static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ── State file helpers ────────────────────────────────────────────────────

        private static UploadState LoadState(string stateFilePath)
        {
            if (!File.Exists(stateFilePath)) return null;
            try
            {
                var json = File.ReadAllText(stateFilePath, new UTF8Encoding(false));
                return UploadState.FromJson(json);
            }
            catch { return null; }
        }

        private static void SaveState(string stateFilePath, UploadState state)
        {
            try
            {
                File.WriteAllText(stateFilePath, state.ToJson(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"Failed to save upload state: " + ex.Message);
            }
        }

        private static string ChunkPathFromStateFile(string stateFilePath, UploadState state)
        {
            // stateFilePath: .../upload_queue/chunk_abc_000001.jsonl.state.json
            var stateFileName = Path.GetFileName(stateFilePath);
            // strip ".state.json" suffix
            var chunkFileName = stateFileName.Substring(0, stateFileName.Length - ".state.json".Length);
            return Path.Combine(ResolveChunkDir(state), chunkFileName);
        }

        /// <summary>
        /// Resolves the directory a state file's chunk lives in from the location
        /// recorded at upload time. Legacy state files (no chunk_dir) resolve to the
        /// live chunks/ dir — the pre-recording behavior.
        /// </summary>
        private static string ResolveChunkDir(UploadState state)
        {
            var rel = state?.ChunkDirRel;
            if (string.IsNullOrEmpty(rel)) return PlayScopeDirectory.Chunks;
            try
            {
                var combined = Path.GetFullPath(Path.Combine(PlayScopeDirectory.Root, rel));
                var root = Path.GetFullPath(PlayScopeDirectory.Root);
                // Containment guard — a corrupt chunk_dir must not resolve outside PlayScope storage.
                if (!IsContainedIn(combined, root))
                {
                    PlayScopeLog.Warning(
                        $"UploaderWorker: state chunk_dir '{rel}' escapes the PlayScope root — falling back to chunks/.");
                    return PlayScopeDirectory.Chunks;
                }
                return combined;
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning(
                    $"UploaderWorker: failed to resolve state chunk_dir '{rel}': {ex.Message} — falling back to chunks/.");
                return PlayScopeDirectory.Chunks;
            }
        }

        /// <summary>
        /// Root-relative directory of a chunk, stored with '/' separators so a state
        /// file written on one launch resolves even if the absolute data path moves
        /// (iOS app-container UUID changes across updates). Empty when the chunk is
        /// outside the PlayScope root or the path cannot be resolved.
        /// </summary>
        private static string ToRootRelativeDir(string chunkPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(chunkPath);
                if (string.IsNullOrEmpty(dir)) return "";
                var fullDir = Path.GetFullPath(dir);
                var fullRoot = Path.GetFullPath(PlayScopeDirectory.Root);
                if (!IsContainedIn(fullDir, fullRoot)) return "";
                var rel = fullDir.Substring(fullRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel.Replace('\\', '/');
            }
            catch
            {
                return "";
            }
        }

        private static bool IsUnderCompletedSessions(string path)
        {
            try
            {
                return IsContainedIn(
                    Path.GetFullPath(path),
                    Path.GetFullPath(PlayScopeDirectory.CompletedSessions));
            }
            catch
            {
                return false;
            }
        }

        // Plain StartsWith would let a sibling dir sharing the prefix through
        // ("...\PlayScopeEvil" vs "...\PlayScope") — require the separator.
        private static bool IsContainedIn(string fullPath, string parentDir)
        {
            var parent = parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, parent, StringComparison.OrdinalIgnoreCase)) return true;
            return fullPath.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ── Orphan self-rescue ────────────────────────────────────────────────────

        /// <summary>
        /// Last-chance rescue for a foreign-short-id chunk: finds a
        /// completed_sessions manifest whose session_id matches the short-id and
        /// moves the chunk there for the next pass. Returns false (chunk untouched,
        /// caller dead-letters) when no match or any IO step fails.
        /// </summary>
        private static bool TryRescueOrphanChunk(string chunkPath, string stateFilePath, out string destPath)
        {
            destPath = "";
            try
            {
                var chunkName = Path.GetFileName(chunkPath);
                // short-id from "chunk_{shortid}_NNNNNN.jsonl".
                if (!chunkName.StartsWith("chunk_", StringComparison.Ordinal)) return false;
                var rest = chunkName.Substring("chunk_".Length);
                int us = rest.IndexOf('_');
                if (us <= 0) return false;
                var shortId = rest.Substring(0, us);

                var completedRoot = PlayScopeDirectory.CompletedSessions;
                if (!Directory.Exists(completedRoot)) return false;

                foreach (var sessionDir in Directory.GetDirectories(completedRoot))
                {
                    var manifestPath = Path.Combine(sessionDir, "session.json");
                    if (!File.Exists(manifestPath)) continue;

                    string manifestSid;
                    try
                    {
                        var json = File.ReadAllText(manifestPath, new UTF8Encoding(false));
                        var dto = SimpleJson.Deserialize(json);
                        if (dto == null || !dto.TryGetValue("session_id", out var sid) || sid is not string sidStr)
                            continue;
                        manifestSid = sidStr;
                    }
                    catch { continue; }

                    // Compare against the manifest's dash-stripped session_id (case-insensitive).
                    var manifestShort = manifestSid.Replace("-", "");
                    if (manifestShort.Length < shortId.Length) continue;
                    if (!manifestShort.Substring(0, shortId.Length)
                            .Equals(shortId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Match found — move chunk into this completed_sessions folder.
                    var dest = Path.Combine(sessionDir, chunkName);
                    if (File.Exists(dest))
                    {
                        // Collision: keep both, suffix the newcomer.
                        var nameNoExt = Path.GetFileNameWithoutExtension(dest);
                        var ext = Path.GetExtension(dest);
                        int n = 1;
                        while (File.Exists(dest))
                        {
                            dest = Path.Combine(sessionDir, $"{nameNoExt}_rescued{n}{ext}");
                            n++;
                        }
                    }
                    File.Move(chunkPath, dest);
                    // Clear the stale state file — else it mis-anchors the retry TTL.
                    TryDeleteFile(stateFilePath);
                    destPath = dest;
                    return true;
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("TryRescueOrphanChunk failed", ex);
            }
            return false;
        }

        // ── Dead letter / cleanup ─────────────────────────────────────────────────

        private static void MoveToDeadLetter(string chunkPath, string stateFilePath)
        {
            var deadLetterDir = PlayScopeDirectory.DeadLetter;
            try { Directory.CreateDirectory(deadLetterDir); } catch { /* ok */ }

            if (File.Exists(chunkPath))
            {
                var dest = Path.Combine(deadLetterDir, Path.GetFileName(chunkPath));
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(chunkPath, dest);
                }
                catch (Exception ex) { PlayScopeLog.Warning($"Could not move chunk to dead letter: " + ex.Message); }
            }
            TryDeleteFile(stateFilePath);
        }

        private static void CleanDeadLetterOldFiles()
        {
            var dir = PlayScopeDirectory.DeadLetter;
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-DeadLetterTtlDays);
            try
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"Error cleaning dead letter dir: " + ex.Message);
            }
        }

        /// <summary>
        /// On startup, enqueue finalized chunks left from THIS session (crash mid-
        /// upload). Only files matching the current short-id prefix —
        /// foreign-short-id chunks are prior-session orphans that would commingle
        /// under our session_id, left for <see cref="SessionRecovery"/>. Skips
        /// chunk_current.jsonl and already-uploaded chunks.
        /// </summary>
        private void RecoverPendingChunks()
        {
            var dir = PlayScopeDirectory.Chunks;
            if (!Directory.Exists(dir)) return;
            var ownPrefix = $"chunk_{_session.SessionShortId}_";
            try
            {
                foreach (var file in Directory.GetFiles(dir, "chunk_*.jsonl"))
                {
                    var name = Path.GetFileName(file);

                    // Skip the live current chunk
                    if (string.Equals(name, "chunk_current.jsonl",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Belt-and-braces ownership check: only OUR finalized chunks belong on
                    // this session's upload queue. SessionRecovery should have already
                    // relocated everything else to completed_sessions/, but if anything
                    // slipped through, refuse to claim it.
                    if (!name.StartsWith(ownPrefix, StringComparison.Ordinal))
                    {
                        PlayScopeLog.Warning(
                            $"UploaderWorker: skipping orphan chunk '{name}' " +
                            $"(does not belong to current session '{_session.SessionShortId}'). " +
                            "SessionRecovery should relocate it on next start.");
                        continue;
                    }

                    // Skip if already uploaded (state file says so)
                    var stateFilePath = Path.Combine(PlayScopeDirectory.UploadQueue,
                        Path.GetFileName(file) + ".state.json");
                    var existingState = LoadState(stateFilePath);
                    if (existingState?.IsUploaded == true)
                    {
                        TryDeleteFile(file);
                        TryDeleteFile(stateFilePath);
                        _passAlreadyUploaded++;
                        continue;
                    }

                    _queue.Enqueue(file);
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning($"Error recovering pending chunks: " + ex.Message);
            }
        }

        // ── Misc helpers ──────────────────────────────────────────────────────────

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex)
            {
                // Usually an Editor Windows file-lock. Logged (not swallowed) so the
                // accumulating-chunks case is visible; recovery skips already-uploaded
                // files on requeue in SessionRecovery.EnqueueJsonlFilesInDir.
                PlayScopeLog.Warning($"TryDeleteFile failed for '{Path.GetFileName(path)}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void TryReleaseSemaphore()
        {
            // Only release if no permits are already queued (max count = 1)
            if (_wakeSignal.CurrentCount == 0)
                try { _wakeSignal.Release(); } catch { /* already released */ }
        }

        // ── Nested state type ─────────────────────────────────────────────────────

        private sealed class UploadState
        {
            internal string ChunkId = "";
            // Root-relative dir of the chunk ('/'-separated). Empty on legacy state
            // files → resolves to chunks/.
            internal string ChunkDirRel = "";
            internal int Attempts;
            internal DateTime? CreatedAt;
            internal DateTime? LastAttemptAt;
            internal DateTime? NextRetryAt;
            internal bool IsUploaded;

            internal string ToJson()
            {
                var created = CreatedAt.HasValue
                    ? "\"" + CreatedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\""
                    : "null";
                var last = LastAttemptAt.HasValue
                    ? "\"" + LastAttemptAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\""
                    : "null";
                var next = NextRetryAt.HasValue
                    ? "\"" + NextRetryAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\""
                    : "null";
                return $"{{\"chunk_id\":\"{EscapeJsonString(ChunkId)}\"," +
                       $"\"chunk_dir\":\"{EscapeJsonString(ChunkDirRel)}\",\"attempts\":{Attempts}," +
                       $"\"created_at\":{created},\"last_attempt_at\":{last},\"next_retry_at\":{next}," +
                       $"\"is_uploaded\":{(IsUploaded ? "true" : "false")}}}";
            }

            internal static UploadState FromJson(string json)
            {
                var dict = SimpleJson.Deserialize(json);
                if (dict == null) return null;
                var state = new UploadState();
                if (dict.TryGetValue("chunk_id", out var ci) && ci is string ciStr) state.ChunkId = ciStr;
                if (dict.TryGetValue("chunk_dir", out var cd) && cd is string cdStr) state.ChunkDirRel = cdStr;
                if (dict.TryGetValue("attempts", out var att)) state.Attempts = ParseInt(att);
                if (dict.TryGetValue("created_at", out var ca) && ca is string caStr && caStr != "null" && !string.IsNullOrEmpty(caStr))
                    state.CreatedAt = DateTime.TryParse(caStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var caVal) ? caVal : (DateTime?)null;
                if (dict.TryGetValue("last_attempt_at", out var la) && la is string laStr && laStr != "null" && !string.IsNullOrEmpty(laStr))
                    state.LastAttemptAt = DateTime.TryParse(laStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var laVal) ? laVal : (DateTime?)null;
                if (dict.TryGetValue("next_retry_at", out var nr) && nr is string nrStr && nrStr != "null" && !string.IsNullOrEmpty(nrStr))
                    state.NextRetryAt = DateTime.TryParse(nrStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var nrVal) ? nrVal : (DateTime?)null;
                if (dict.TryGetValue("is_uploaded", out var iu)) state.IsUploaded = iu is bool b ? b : false;
                return state;
            }

            private static int ParseInt(object v)
            {
                if (v is string s && int.TryParse(s, out var i)) return i;
                if (v is int i2) return i2;
                return 0;
            }
        }
    }
}
