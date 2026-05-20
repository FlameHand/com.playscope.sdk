using System;
using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Collapses repeated <c>info</c>/<c>warning</c>/<c>debug</c> logs that share
    /// the same <c>(level, message)</c> within a 5-second window into a single
    /// timeline record carrying <c>repeat_count: N</c> metadata.
    ///
    /// <para>
    /// Critical levels (<c>error</c>, <c>exception</c>) bypass the dedup
    /// entirely — every error matters individually and we never want to delay
    /// it by the dedup window. <see cref="EventPipeline.EnqueueLog"/> calls
    /// <see cref="Add"/> only for the absorbable levels and routes criticals
    /// straight to the queue.
    /// </para>
    ///
    /// <para>
    /// Trade-off: the FIRST occurrence of a key is held for up to 5 seconds
    /// before reaching the queue. That delays "what just happened?" visibility
    /// slightly, but it's preferable to the alternative of emitting both the
    /// first record AND a summary record (two rows for one logical event).
    /// </para>
    ///
    /// <para>
    /// Thread-safety: <see cref="Add"/> may be called from any thread (Unity's
    /// log-stream callback runs on a threadpool worker). All buffer state is
    /// guarded by a single monitor; the only main-thread coupling is the
    /// optional periodic <see cref="TickAndMaybeFlush"/> driven by the
    /// MonoBehaviour Update loop.
    /// </para>
    /// </summary>
    internal sealed class LogDedupBuffer
    {
        private const int WindowMs = 5000;
        // Hard cap on buffered entries — keeps a runaway spam scenario from
        // growing the dictionary without bound. Eviction policy: drop the
        // oldest entry (emit it with its current count) to make room.
        private const int MaxEntries = 256;

        private sealed class Entry
        {
            internal EventRecord Sample = null!;
            internal int RepeatCount;
            internal long BufferedAtTicks;
        }

        private readonly EventQueue _queue;
        private readonly Dictionary<string, Entry> _entries = new();
        private readonly object _gate = new();

        internal LogDedupBuffer(EventQueue queue) { _queue = queue; }

        /// <summary>
        /// Offer a log record to the dedup buffer. The buffer either:
        /// <list type="bullet">
        /// <item>absorbs it as a repeat of an existing buffered key
        ///       (RepeatCount++ on the first sample), or</item>
        /// <item>buffers it as the first-of-key sample, to be emitted later
        ///       when the 5 s window expires.</item>
        /// </list>
        /// In both cases the caller MUST NOT also enqueue the record itself —
        /// this method has taken ownership.
        /// </summary>
        internal void Add(EventRecord record)
        {
            var key = Key(record);
            var nowTicks = DateTime.UtcNow.Ticks;
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    existing.RepeatCount++;
                    return;
                }
                EvictIfFull_NoLock();
                _entries[key] = new Entry
                {
                    Sample = record,
                    RepeatCount = 1,
                    BufferedAtTicks = nowTicks,
                };
            }
        }

        /// <summary>
        /// Called from MonoBehaviour.Update every frame. Walks the buffer
        /// and emits any entries whose 5 s window has elapsed. Cheap — the
        /// early-return on empty buffer means the per-frame cost is one
        /// lock + one Count check when nothing is buffered.
        /// </summary>
        internal void TickAndMaybeFlush()
        {
            List<Entry>? toEmit = null;
            var nowTicks = DateTime.UtcNow.Ticks;
            lock (_gate)
            {
                if (_entries.Count == 0) return;
                List<string>? expiredKeys = null;
                foreach (var kv in _entries)
                {
                    var ageMs = (nowTicks - kv.Value.BufferedAtTicks) / TimeSpan.TicksPerMillisecond;
                    if (ageMs < WindowMs) continue;
                    expiredKeys ??= new List<string>();
                    expiredKeys.Add(kv.Key);
                }
                if (expiredKeys == null) return;
                toEmit = new List<Entry>(expiredKeys.Count);
                foreach (var k in expiredKeys)
                {
                    toEmit.Add(_entries[k]);
                    _entries.Remove(k);
                }
            }
            foreach (var e in toEmit) EmitWithCount(e);
        }

        /// <summary>
        /// Drains every buffered entry, regardless of age. Called from
        /// FlushOnPause and TeardownInternal so a backgrounded / shutting-
        /// down session doesn't strand the last few seconds of logs.
        /// </summary>
        internal void FlushNow()
        {
            List<Entry> toEmit;
            lock (_gate)
            {
                if (_entries.Count == 0) return;
                toEmit = new List<Entry>(_entries.Count);
                foreach (var kv in _entries) toEmit.Add(kv.Value);
                _entries.Clear();
            }
            foreach (var e in toEmit) EmitWithCount(e);
        }

        private static string Key(EventRecord r) =>
            (r.Level ?? "") + "" + (r.Message ?? "");

        private void EvictIfFull_NoLock()
        {
            if (_entries.Count < MaxEntries) return;
            // Find the oldest buffered entry and emit it to make room.
            // We don't need precise LRU — this is a safety valve for
            // pathological spam, not a hot path.
            string? oldestKey = null;
            long oldestTicks = long.MaxValue;
            foreach (var kv in _entries)
            {
                if (kv.Value.BufferedAtTicks < oldestTicks)
                {
                    oldestTicks = kv.Value.BufferedAtTicks;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey == null) return;
            var victim = _entries[oldestKey];
            _entries.Remove(oldestKey);
            // Emit OUTSIDE the lock would be cleaner, but the dictionary
            // is small and EmitWithCount only touches the queue (which
            // has its own lock). Safe to call inside.
            EmitWithCount(victim);
            PlayScopeLog.Warning(
                $"LogDedupBuffer: evicted oldest entry to make room (cap {MaxEntries}). " +
                "Some other log family is spamming — consider rate-limiting at the call site.");
        }

        // Emits the buffered sample, splicing repeat_count into its metadata
        // JSON when the count is > 1. For count == 1 the record is emitted
        // verbatim — no metadata mutation, no synthetic "repeat_count: 1"
        // bloat on every dashboard log row.
        private void EmitWithCount(Entry e)
        {
            if (e.RepeatCount > 1)
            {
                e.Sample.MetadataJson = InjectRepeatCount(e.Sample.MetadataJson, e.RepeatCount);
            }
            _queue.Enqueue(e.Sample);
        }

        // Inserts "repeat_count": N as the FIRST key of the existing metadata
        // object. Works whether MetadataJson is null/empty (build a fresh
        // single-key object) or a populated object literal (splice after the
        // opening brace). Hand-rolled rather than re-parsing because the SDK
        // already keeps metadata as serialized JSON throughout the pipeline.
        private static string InjectRepeatCount(string? metadataJson, int count)
        {
            if (string.IsNullOrEmpty(metadataJson) || metadataJson == "{}")
                return "{\"repeat_count\":" + count + "}";
            // Expecting metadataJson to start with '{'. Splice after it.
            if (metadataJson![0] == '{')
                return "{\"repeat_count\":" + count + "," + metadataJson.Substring(1);
            // Unexpected shape — leave as-is, drop repeat_count rather than
            // produce malformed JSON.
            return metadataJson;
        }
    }
}
