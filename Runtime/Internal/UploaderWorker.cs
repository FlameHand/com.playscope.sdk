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
            while (!ct.IsCancellationRequested)
            {
                // Wait for either a wake signal or the polling timeout
                try
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(PollingIntervalSeconds), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* timeout is fine — proceed to upload cycle */ }

                if (ct.IsCancellationRequested) break;

                await ProcessQueueAsync(ct);
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

            var now = DateTime.UtcNow;
            try
            {
                foreach (var stateFile in Directory.GetFiles(dir, "*.state.json"))
                {
                    var state = LoadState(stateFile);
                    if (state == null) continue;
                    if (state.IsUploaded) continue;

                    // Check TTL — if last_attempt is older than 7 days → dead letter
                    if (state.LastAttemptAt.HasValue &&
                        (now - state.LastAttemptAt.Value).TotalDays >= RetryTtlDays)
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
                Debug.LogWarning("[PlayScope] Error scanning upload queue dir: " + ex.Message);
            }
        }

        // ── Single chunk upload ───────────────────────────────────────────────────

        private async UniTask UploadChunkAsync(string chunkPath, CancellationToken ct)
        {
            var chunkName = Path.GetFileName(chunkPath);
            var stateFilePath = Path.Combine(PlayScopeDirectory.UploadQueue, chunkName + ".state.json");

            // Load or create state
            var state = LoadState(stateFilePath) ?? new UploadState { ChunkId = chunkName };

            // Build envelope
            byte[] payload;
            try
            {
                payload = BuildGzipPayload(chunkPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] Failed to build payload for {chunkName}: {ex.Message}");
                return;
            }

            var endpoint = (_context.UploadEndpoint?.TrimEnd('/') ?? "https://api.playscope.dev") + "/v1/ingest";

            state.Attempts++;
            state.LastAttemptAt = DateTime.UtcNow;
            SaveState(stateFilePath, state);

            int httpStatus = 0;
            bool networkError = false;

            try
            {
                httpStatus = await SendRequestAsync(endpoint, payload, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayScope] Network error uploading {chunkName}: {ex.Message}");
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

            // Non-retryable errors → dead letter
            if (!networkError && (httpStatus == 400 || httpStatus == 401 || httpStatus == 403 || httpStatus == 409))
            {
                Debug.LogWarning($"[PlayScope] Non-retryable HTTP {httpStatus} for {chunkName} — moving to dead letter.");
                MoveToDeadLetter(chunkPath, stateFilePath);
                return;
            }

            // Retryable (429, 5xx, network error) → schedule next retry
            double baseSeconds = BackoffBase[Math.Min(state.Attempts - 1, BackoffBase.Length - 1)];
            double jitter = baseSeconds * 0.2 * (UnityEngine.Random.value * 2.0 - 1.0); // ±20%
            var nextRetry = DateTime.UtcNow.AddSeconds(baseSeconds + jitter);
            state.NextRetryAt = nextRetry;
            SaveState(stateFilePath, state);

            if (!networkError)
                Debug.LogWarning($"[PlayScope] HTTP {httpStatus} for {chunkName}, retry #{state.Attempts} at {nextRetry:o}");
            else
                Debug.LogWarning($"[PlayScope] Network error for {chunkName}, retry #{state.Attempts} at {nextRetry:o}");
        }

        // ── HTTP ──────────────────────────────────────────────────────────────────

        private async UniTask<int> SendRequestAsync(string url, byte[] gzipBody, CancellationToken ct)
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

            return (int)request.responseCode;
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

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"session_id\":\"").Append(EscapeJsonString(_session.SessionId)).Append("\",");
            sb.Append("\"sdk_version\":\"").Append(EscapeJsonString(_session.SdkVersion)).Append("\",");
            sb.Append("\"schema_version\":").Append(_session.SchemaVersion).Append(",");
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
                Debug.LogWarning("[PlayScope] Failed to save upload state: " + ex.Message);
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
                catch (Exception ex) { Debug.LogWarning("[PlayScope] Could not move chunk to dead letter: " + ex.Message); }
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
                Debug.LogWarning("[PlayScope] Error cleaning dead letter dir: " + ex.Message);
            }
        }

        /// <summary>
        /// On startup, scan the chunks directory for finalized .jsonl files left from a previous
        /// session crash (they were never uploaded). Enqueue them so the upload loop picks them up.
        /// Skips chunk_current.jsonl (still being written) and already-uploaded chunks
        /// (tracked via state files marked is_uploaded=true).
        /// </summary>
        private void RecoverPendingChunks()
        {
            var dir = PlayScopeDirectory.Chunks;
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var file in Directory.GetFiles(dir, "chunk_*.jsonl"))
                {
                    // Skip the live current chunk
                    if (string.Equals(Path.GetFileName(file), "chunk_current.jsonl",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

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
                Debug.LogWarning("[PlayScope] Error recovering pending chunks: " + ex.Message);
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
            internal DateTime? LastAttemptAt;
            internal DateTime? NextRetryAt;
            internal bool IsUploaded;

            internal string ToJson()
            {
                var last = LastAttemptAt.HasValue
                    ? "\"" + LastAttemptAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\""
                    : "null";
                var next = NextRetryAt.HasValue
                    ? "\"" + NextRetryAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + "\""
                    : "null";
                return $"{{\"chunk_id\":\"{EscapeJsonString(ChunkId)}\",\"attempts\":{Attempts}," +
                       $"\"last_attempt_at\":{last},\"next_retry_at\":{next}," +
                       $"\"is_uploaded\":{(IsUploaded ? "true" : "false")}}}";
            }

            internal static UploadState? FromJson(string json)
            {
                var dict = SimpleJson.Deserialize(json);
                if (dict == null) return null;
                var state = new UploadState();
                if (dict.TryGetValue("chunk_id", out var ci) && ci is string ciStr) state.ChunkId = ciStr;
                if (dict.TryGetValue("attempts", out var att)) state.Attempts = ParseInt(att);
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
