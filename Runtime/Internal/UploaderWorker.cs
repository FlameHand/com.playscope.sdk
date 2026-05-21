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
        private CancellationTokenSource? _cts;

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

                // ALWAYS run ProcessQueueAsync once per wake, even on cancellation.
                // Shutdown's flow is: enqueue session_end → DrainAndFinalize →
                // TriggerInstantUpload → Stop. Without this unconditional drain
                // the cancel could race the wake and skip the final upload —
                // session_end would only land on the NEXT launch via
                // SessionRecovery (when it works), and silently never (when
                // anything in that chain fails). We pass CancellationToken.None
                // so a single in-flight HTTP request gets to finish; the OS
                // will kill us hard if we exceed Unity's quit budget anyway.
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

            foreach (var chunkPath in paths)
            {
                if (ct.IsCancellationRequested) break;
                if (!File.Exists(chunkPath)) continue;
                await UploadChunkAsync(chunkPath, ct);
            }
        }

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

                    // Belt-and-braces ownership check: a leftover state file from a PRIOR
                    // session must not pull its chunk into THIS session's upload loop.
                    // Without this guard, an orphan chunk_{otherShort}_NNN.jsonl in chunks/
                    // would be uploaded under our envelope and cross-session-commingled
                    // under our session_id (regression 2026-05-21 — three Unity runs
                    // squashed into one backend session_id because three prior sessions'
                    // chunks all rode in on this path).
                    //
                    // chunk_id in the state file is the chunk's filename (e.g.
                    // "chunk_4e91a_000001.jsonl"). Anything not matching our short-id
                    // prefix gets removed from the queue dir so we stop checking it
                    // every poll; the chunk itself stays for SessionRecovery to relocate.
                    var chunkId = state.ChunkId ?? "";
                    if (!chunkId.StartsWith(ownPrefix, StringComparison.Ordinal))
                    {
                        PlayScopeLog.Warning(
                            $"UploaderWorker: dropping orphan state file '{Path.GetFileName(stateFile)}' " +
                            $"(does not belong to current session '{_session.SessionShortId}'). " +
                            "SessionRecovery should relocate the chunk on next start.");
                        TryDeleteFile(stateFile);
                        continue;
                    }

                    // Check TTL — based on CreatedAt (NOT LastAttemptAt), otherwise a chunk that
                    // keeps failing would reset its own TTL on every retry. Legacy state files
                    // missing CreatedAt fall back to LastAttemptAt for backwards compatibility.
                    var ttlAnchor = state.CreatedAt ?? state.LastAttemptAt;
                    if (ttlAnchor.HasValue &&
                        (now - ttlAnchor.Value).TotalDays >= RetryTtlDays)
                    {
                        var chunkForState = ChunkPathFromStateFile(stateFile);
                        MoveToDeadLetter(chunkForState, stateFile);
                        continue;
                    }

                    // Check whether retry window has elapsed
                    if (state.NextRetryAt.HasValue && state.NextRetryAt.Value > now)
                        continue; // still waiting

                    var chunk = ChunkPathFromStateFile(stateFile);
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

            // Build envelope
            byte[] payload;
            try
            {
                payload = BuildGzipPayload(chunkPath);
            }
            catch (InvalidOperationException ex)
            {
                // Foreign short-id in chunks/ — SessionRecovery should have moved
                // this chunk to completed_sessions/{prior_session_id}/ at startup
                // but didn't (TryMoveFile is best-effort and swallows IO errors).
                // Try a self-rescue: look for an existing completed_sessions
                // manifest whose session_id starts with the chunk's short-id and
                // relocate the chunk there so the NEXT upload pass picks it up
                // with the correct envelope identity. Only if that fails do we
                // dead-letter the data.
                if (TryRescueOrphanChunk(chunkPath, stateFilePath, out var rescueDest))
                {
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
                PlayScopeLog.Warning($"Failed to build payload for {chunkName}: {ex.Message}");
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
                // Success
                state.IsUploaded = true;
                SaveState(stateFilePath, state);
                TryDeleteFile(chunkPath);
                TryDeleteFile(stateFilePath);
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
            request.SetRequestHeader("Authorization", "Bearer " + _context.ApiKey);

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

            // Recovered chunks live under completed_sessions/{sessionId}/ and carry a manifest
            // (session.json) with the original session's identity. Use it so the envelope
            // attributes the data to the crashed session, not the currently-running one.
            // Throws if a recovered chunk has no usable manifest — see ResolveEnvelopeIdentity.
            ResolveEnvelopeIdentity(chunkPath,
                out var envelopeSessionId, out var envelopeSdkVersion, out var envelopeSchemaVersion);

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"session_id\":\"").Append(EscapeJsonString(envelopeSessionId)).Append("\",");
            sb.Append("\"sdk_version\":\"").Append(EscapeJsonString(envelopeSdkVersion)).Append("\",");
            sb.Append("\"schema_version\":").Append(envelopeSchemaVersion).Append(",");
            sb.Append("\"batch_id\":\"").Append(EscapeJsonString(Path.GetFileNameWithoutExtension(chunkPath))).Append("\",");
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
        /// Determines which session_id / sdk_version / schema_version should be attached to the
        /// envelope for the given chunk.
        ///
        /// <para>
        /// For chunks under <c>chunks/</c> (the live session's directory) we use the live
        /// SessionInfo — by the time a chunk gets here, RecoverPendingChunks has already
        /// filtered out anything that isn't owned by the current session.
        /// </para>
        ///
        /// <para>
        /// For chunks under <c>completed_sessions/{sessionId}/</c> we read the bundled
        /// <c>session.json</c> manifest so the envelope describes the ORIGINAL session, not
        /// the currently-running one. If the manifest is missing or unreadable we cannot
        /// safely attribute the data to ANY session — falling back to the live SessionInfo
        /// would commingle two sessions under one backend session_id, which is the exact
        /// bug we fixed in 0.1.16. In that case we throw <see cref="InvalidOperationException"/>
        /// so the caller can dead-letter the chunk.
        /// </para>
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
                // Live chunk → live identity is ONLY correct when the chunk filename
                // actually belongs to the current session. A chunk with a foreign
                // short-id prefix sitting in chunks/ is an orphan from a prior session
                // that SessionRecovery failed to relocate; uploading it under the live
                // session_id is exactly the cross-session commingling we're trying to
                // prevent (regression 2026-05-21).
                //
                // Refuse to upload — the caller dead-letters the chunk. SessionRecovery
                // remains the official path for re-attributing prior-session data via
                // its bundled manifest.
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

            // Recovered chunk: the manifest is the ONLY source of truth for which session
            // this data belongs to. No fallback allowed.
            var manifestPath = Path.Combine(chunkDir, "session.json");
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException(
                    $"Recovered chunk '{Path.GetFileName(chunkPath)}' has no session.json manifest " +
                    $"in {chunkDir}. Refusing to upload — would risk attributing to wrong session.");

            Dictionary<string, object>? dto;
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
        private static string? ExtractRecordType(string line)
        {
            const string key = "\"record_type\":\"";
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + key.Length;
            int end = line.IndexOf('"', start);
            if (end < 0) return null;
            return line.Substring(start, end - start);
        }

        private static string EscapeJsonString(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // ── State file helpers ────────────────────────────────────────────────────

        private static UploadState? LoadState(string stateFilePath)
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

        private static string ChunkPathFromStateFile(string stateFilePath)
        {
            // stateFilePath: .../upload_queue/chunk_abc_000001.jsonl.state.json
            // chunkPath:     .../chunks/chunk_abc_000001.jsonl
            var stateFileName = Path.GetFileName(stateFilePath);
            // strip ".state.json" suffix
            var chunkFileName = stateFileName.Substring(0, stateFileName.Length - ".state.json".Length);
            return Path.Combine(PlayScopeDirectory.Chunks, chunkFileName);
        }

        // ── Orphan self-rescue ────────────────────────────────────────────────────

        /// <summary>
        /// Last-chance rescue for a chunk with foreign short-id that SessionRecovery
        /// failed to relocate. Scans <c>completed_sessions/*/session.json</c> for a
        /// manifest whose session_id starts with the chunk's short-id; if found, moves
        /// the chunk INTO that folder so the next upload pass attributes it correctly
        /// via the existing completed_sessions code path. Best-effort — returns false
        /// (and leaves the chunk untouched) if no matching manifest exists or any IO
        /// step fails. The caller falls back to dead-letter on false.
        /// </summary>
        private static bool TryRescueOrphanChunk(string chunkPath, string stateFilePath, out string destPath)
        {
            destPath = "";
            try
            {
                var chunkName = Path.GetFileName(chunkPath);
                // Extract the 5-hex short-id from "chunk_{shortid}_NNNNNN.jsonl".
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

                    // session_id is a UUID with dashes; short-id is first 5 chars of
                    // its dash-stripped form. Compare case-insensitively to be safe.
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
                    // Clear the state file — completed_sessions chunks build their own
                    // state on the next attempt. Leaving the stale one around would
                    // mis-anchor the retry TTL.
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
        /// On startup, scan the chunks directory for finalized .jsonl files left from THIS
        /// session (e.g. a crash mid-upload). Enqueue them so the upload loop picks them up.
        ///
        /// <para>
        /// Critically: only files whose name contains the current session's short id are
        /// picked up here. Finalized chunks are named <c>chunk_{shortId}_{counter:D6}.jsonl</c>;
        /// any chunk with a DIFFERENT short id is an orphan from a prior session and must NOT
        /// be uploaded under the current session's envelope (doing so commingles events from
        /// multiple SDK sessions into one backend session_id — see ClickHouse audit
        /// 2026-05-19). Orphans are left for <see cref="SessionRecovery"/> to relocate.
        /// </para>
        ///
        /// Skips chunk_current.jsonl (still being written) and already-uploaded chunks
        /// (tracked via state files marked is_uploaded=true).
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
            catch { /* best-effort */ }
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
                return $"{{\"chunk_id\":\"{EscapeJsonString(ChunkId)}\",\"attempts\":{Attempts}," +
                       $"\"created_at\":{created},\"last_attempt_at\":{last},\"next_retry_at\":{next}," +
                       $"\"is_uploaded\":{(IsUploaded ? "true" : "false")}}}";
            }

            internal static UploadState? FromJson(string json)
            {
                var dict = SimpleJson.Deserialize(json);
                if (dict == null) return null;
                var state = new UploadState();
                if (dict.TryGetValue("chunk_id", out var ci) && ci is string ciStr) state.ChunkId = ciStr;
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

            private static int ParseInt(object? v)
            {
                if (v is string s && int.TryParse(s, out var i)) return i;
                if (v is int i2) return i2;
                return 0;
            }
        }
    }
}
