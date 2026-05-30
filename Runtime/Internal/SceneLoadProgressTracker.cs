using System.Collections.Generic;
using UnityEngine;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Periodic sampler for in-flight scene loads. Callers register an
    /// <see cref="AsyncOperation"/> against an <c>operationId</c>; the
    /// MonoBehaviour driver samples its <see cref="AsyncOperation.progress"/>
    /// every <see cref="SampleIntervalSec"/>s and emits them as
    /// <c>scene_progress_samples</c> on completion / <see cref="DrainSamples"/>.
    /// Polling (not a callback) because AsyncOperation only fires <c>completed</c>
    /// at the end, but the dashboard wants the load's shape (ramp / plateau /
    /// jump). 250 ms is granular enough without burying the payload.
    /// </summary>
    internal static class SceneLoadProgressTracker
    {
        private const float SampleIntervalSec = 0.25f;
        // Cap per operation so a multi-minute load doesn't blow the 4 KB metadata
        // limit. 64 = 16 s at full rate; longer loads degrade to head+tail.
        private const int MaxSamples = 64;
        // Cap on concurrent operations so a forgotten End() can't grow _entries
        // unbounded; over-cap evicts the oldest.
        private const int MaxEntries = 64;

        private sealed class Entry
        {
            internal AsyncOperation Op;
            internal float LastSampleTime;
            internal bool OpDone;       // latched once Op.isDone observed true
            internal readonly List<float> Samples = new();
        }

        // _gate guards against Begin/End on worker threads racing the main-thread sampler tick.
        private static readonly Dictionary<string, Entry> _entries = new();
        private static readonly object _gate = new();

        /// <summary>
        /// Begin sampling progress for <paramref name="operationId"/>. Safe to
        /// call from any thread. <paramref name="op"/> may be null (caller has
        /// no AsyncOperation handle); in that case the entry exists but no
        /// samples are collected — caller can still push manual samples via
        /// <see cref="RecordSample"/>.
        /// </summary>
        internal static void Begin(string operationId, AsyncOperation op)
        {
            if (string.IsNullOrEmpty(operationId)) return;
            lock (_gate)
            {
                EvictIfFull_NoLock();
                _entries[operationId] = new Entry { Op = op, LastSampleTime = -SampleIntervalSec };
            }
        }

        /// <summary>
        /// Record an explicit progress sample for <paramref name="operationId"/>.
        /// Used when the caller wants finer control than the periodic tick
        /// (e.g. tick on Addressables' DownloadStatus inside a custom loop).
        /// </summary>
        internal static void RecordSample(string operationId, float progress)
        {
            if (string.IsNullOrEmpty(operationId)) return;
            lock (_gate)
            {
                if (!_entries.TryGetValue(operationId, out var entry)) return;
                AppendSample_NoLock(entry, progress);
            }
        }

        /// <summary>
        /// Drains the collected samples for <paramref name="operationId"/> and
        /// returns them as a boxed list ready to drop into the operation_end
        /// metadata under <c>scene_progress_samples</c>. <see cref="EventPipeline.DictToJson"/>
        /// already knows how to serialize <see cref="IList"/>s of floats.
        /// Returns <c>null</c> when nothing was sampled — caller skips the key.
        /// </summary>
        internal static List<object> DrainSamples(string operationId)
        {
            if (string.IsNullOrEmpty(operationId)) return null;
            List<float> drained = null;
            lock (_gate)
            {
                if (_entries.TryGetValue(operationId, out var entry))
                {
                    drained = entry.Samples;
                    _entries.Remove(operationId);
                }
            }
            if (drained == null || drained.Count == 0) return null;
            // Box so DictToJson's IList branch serializes them as numbers.
            var boxed = new List<object>(drained.Count);
            for (int i = 0; i < drained.Count; i++) boxed.Add(drained[i]);
            return boxed;
        }

        /// <summary>
        /// Discards every tracked entry on Shutdown so a re-Initialize doesn't
        /// inherit half-finished loads and the AsyncOperation refs can be GCed.
        /// </summary>
        internal static void ClearAll()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        /// <summary>
        /// Per-frame main-thread tick — appends a sample per entry when its timer
        /// elapses. Sampled here because AsyncOperation.progress is main-thread only.
        /// </summary>
        internal static void TickAndMaybeSample()
        {
            // Snapshot under the lock, sample outside it to keep the section tight.
            KeyValuePair<string, Entry>[] snapshot;
            lock (_gate)
            {
                if (_entries.Count == 0) return;
                snapshot = new KeyValuePair<string, Entry>[_entries.Count];
                int i = 0;
                foreach (var kv in _entries) snapshot[i++] = kv;
            }
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < snapshot.Length; i++)
            {
                var opId = snapshot[i].Key;
                var entry = snapshot[i].Value;
                if (entry.Op == null) continue;
                // Latch stops re-appending the final value forever after the op finishes.
                if (entry.OpDone) continue;
                if (now - entry.LastSampleTime < SampleIntervalSec) continue;

                float progress;
                bool opDone;
                try
                {
                    progress = entry.Op.progress;
                    opDone = entry.Op.isDone;
                }
                catch
                {
                    // Native op disposed out from under us — stop sampling this entry.
                    lock (_gate)
                    {
                        if (_entries.TryGetValue(opId, out var live)) live.OpDone = true;
                    }
                    continue;
                }

                // Re-resolve by key inside the lock so a concurrent
                // DrainSamples/Begin that replaced/removed it doesn't get
                // samples leaked into an already-drained Entry.
                lock (_gate)
                {
                    if (!_entries.TryGetValue(opId, out var live)) continue;
                    if (live != entry) continue; // entry was replaced by a new Begin
                    live.LastSampleTime = now;
                    AppendSample_NoLock(live, progress);
                    if (opDone) live.OpDone = true;
                }
            }
        }

        // _gate must be held by the caller.
        private static void AppendSample_NoLock(Entry entry, float progress)
        {
            if (entry.Samples.Count >= MaxSamples)
            {
                // Drop the median (keeps head ramp + tail jump visible, order intact).
                entry.Samples.RemoveAt(entry.Samples.Count / 2);
            }
            // Clamp to 0..1. Keep Unity's 0.9f "ready to activate" plateau, don't mask it as 1.0.
            entry.Samples.Add(Mathf.Clamp01(progress));
        }

        // _gate held by caller. Evicts the oldest-ish entry — not a precise LRU,
        // this is a leak fallback, not a hot path.
        private static void EvictIfFull_NoLock()
        {
            if (_entries.Count < MaxEntries) return;
            string victimKey = null;
            foreach (var kv in _entries) { victimKey = kv.Key; break; }
            if (victimKey != null)
            {
                _entries.Remove(victimKey);
                PlayScopeLog.Warning(
                    $"SceneLoadProgressTracker: evicted '{victimKey}' — too many concurrent " +
                    "tracked operations (cap = " + MaxEntries + "). Likely an EndSceneLoad / " +
                    "CompleteOperation call was missed somewhere upstream.");
            }
        }
    }
}
