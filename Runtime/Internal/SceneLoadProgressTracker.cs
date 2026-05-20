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

        private sealed class Entry
        {
            internal AsyncOperation Op;
            internal float LastSampleTime;
            internal List<float> Samples = new();
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
                AppendSample(entry, progress);
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
                var entry = snapshot[i].Value;
                if (entry.Op == null) continue;
                if (now - entry.LastSampleTime < SampleIntervalSec) continue;
                entry.LastSampleTime = now;
                lock (_gate)
                {
                    AppendSample(entry, entry.Op.progress);
                }
            }
        }

        private static void AppendSample(Entry entry, float progress)
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
    }
}
