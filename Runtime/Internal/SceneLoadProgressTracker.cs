using System.Collections.Generic;
using UnityEngine;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Periodic sampler for in-flight scene loads. Callers register the
    /// <see cref="AsyncOperation"/> returned by <c>SceneManager.LoadSceneAsync</c>
    /// (or Addressables' scene loader) against a PlayScope <c>operationId</c>;
    /// the MonoBehaviour driver ticks this sampler every frame and appends a
    /// <see cref="AsyncOperation.progress"/> reading once every <see cref="SampleIntervalSec"/>
    /// seconds. When the operation completes (or the caller calls
    /// <see cref="DrainSamples"/> from CompleteOperation), the collected samples
    /// are emitted as <c>scene_progress_samples</c> in metadata.
    ///
    /// <para>
    /// Why a polling sampler rather than a callback: Unity's AsyncOperation
    /// only fires <c>completed</c> at the very end. The dashboard wants to see
    /// the shape of the load — slow ramp on remote scenes, near-instant on
    /// cached, plateau-then-jump on dependency-heavy bundles. Sampling at
    /// 250 ms is granular enough to see the shape without burying the
    /// payload in a hundred values.
    /// </para>
    /// </summary>
    internal static class SceneLoadProgressTracker
    {
        private const float SampleIntervalSec = 0.25f;
        // Hard cap on samples kept per operation. A multi-minute scene load at
        // 250 ms would otherwise push us past the 4 KB metadata limit on its
        // own. 64 samples covers 16 s of load detail at full rate; longer
        // loads gracefully degrade to "head + tail" by dropping middle samples.
        private const int MaxSamples = 64;
        // Hard cap on concurrently-tracked operations. A caller that forgets
        // to End() — or a code path where End() is unreachable — would otherwise
        // grow _entries without bound. 64 is generous (a session almost never
        // has more than a handful of scene loads truly in-flight at once);
        // when exceeded we evict the oldest non-drained entry.
        private const int MaxEntries = 64;

        private sealed class Entry
        {
            internal AsyncOperation Op;
            internal float LastSampleTime;
            internal bool OpDone;       // latched once Op.isDone observed true
            internal readonly List<float> Samples = new();
        }

        // operationId → entry. Synchronized on _gate because Start/End can
        // run on worker threads (the SDK API surface is documented as
        // thread-safe) while the sampler ticks on the Unity main thread.
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
            // Box into object so DictToJson's IList branch sees boxed floats
            // and writes them via the numeric serializer (invariant culture).
            var boxed = new List<object>(drained.Count);
            for (int i = 0; i < drained.Count; i++) boxed.Add(drained[i]);
            return boxed;
        }

        /// <summary>
        /// Discards every tracked entry. Called by <c>PlayScopeRuntime.Shutdown</c>
        /// so a re-Initialize doesn't inherit half-finished loads from the
        /// prior session, and so the underlying AsyncOperation references
        /// (which may keep larger Unity objects alive) can be GCed.
        /// </summary>
        internal static void ClearAll()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }

        /// <summary>
        /// Called from <c>PlayScopeMonoBehaviour.Update</c> every frame on the
        /// Unity main thread. Walks active entries and appends a sample when
        /// the per-entry timer has elapsed. We sample the bound AsyncOperation
        /// directly here because AsyncOperation.progress is main-thread only.
        /// </summary>
        internal static void TickAndMaybeSample()
        {
            // Snapshot under the lock; sample outside the lock to keep the
            // critical section tight even if .progress reads end up taking
            // a few microseconds (they shouldn't, but Unity is Unity).
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
                // Stop sampling once the AsyncOperation has finished. Without
                // this latch the per-frame tick keeps appending the final
                // sample value forever (until DrainSamples is called) and
                // wastes CPU on the head/middle-drop loop in AppendSample.
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
                    // AsyncOperation is a plain managed object so this is the
                    // rare case where the underlying native op got disposed
                    // out from under us. Stop sampling this entry.
                    lock (_gate)
                    {
                        if (_entries.TryGetValue(opId, out var live)) live.OpDone = true;
                    }
                    continue;
                }

                // All state mutation goes inside the lock — re-resolve the
                // entry by key so a concurrent DrainSamples/Begin that
                // replaced or removed it doesn't get its data appended-to
                // here. Without this, samples can leak into an orphaned
                // Entry the caller has already drained from.
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
                // Reservoir-style "drop the middle" to keep the head (boot
                // ramp) and tail (final jump) of the load visible even on
                // exotic long loads. We drop the median index so the kept
                // samples remain ordered.
                entry.Samples.RemoveAt(entry.Samples.Count / 2);
            }
            // Clamp + round to a stable 0..1 range. Unity sometimes reports
            // 0.9f for the "ready to activate" plateau which we want to keep
            // visible rather than mask as 1.0.
            entry.Samples.Add(Mathf.Clamp01(progress));
        }

        // _gate must be held by the caller. Evicts the oldest entry (by
        // insertion order — Dictionary in .NET Core preserves insertion order
        // for enumeration in practice; we don't rely on a precise LRU since
        // this is a fallback for a leak, not a hot path).
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
