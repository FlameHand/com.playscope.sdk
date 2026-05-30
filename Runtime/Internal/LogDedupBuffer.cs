using System;
using System.Collections.Generic;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Collapses repeated info/warning/debug logs with the same (level, message)
    /// within a 5 s window into one record carrying <c>repeat_count: N</c>.
    /// Critical levels (error/exception) bypass dedup — every error matters and
    /// must not be delayed by the window. Trade-off: the first occurrence is held
    /// up to 5 s, preferable to emitting both a first record AND a summary (two
    /// rows for one event). <see cref="Add"/> is thread-safe (Unity's log callback
    /// runs on a worker); state is guarded by a single monitor.
    /// </summary>
    internal sealed class LogDedupBuffer
    {
        private const int WindowMs = 5000;
        // Cap against runaway spam; over-cap emits the oldest entry to make room.
        private const int MaxEntries = 256;

        private sealed class Entry
        {
            internal EventRecord Sample = null!;
            internal int RepeatCount;
            internal long BufferedAtTicks;
        }

        private readonly EventQueue _queue;
        private readonly Dictionary<string, Entry> _entries = new();

        // Rate-limit the eviction warning — a flood of unique warnings (e.g. one
        // per missing translation × hundreds of UI elements) would otherwise log
        // the eviction notice on every add. Once per 10 s carries the same signal.
        private long _lastEvictionWarnTicks;
        private long _droppedEvictionWarnings;
        private const long EvictionWarnIntervalMs = 10_000; // log at most once per 10s

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

        /// <summary>Per-frame tick — emits entries whose 5 s window elapsed.</summary>
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
            // Emit the oldest to make room — not precise LRU, just a spam safety valve.
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
            // Safe to emit under the lock — the queue has its own.
            EmitWithCount(victim);

            // Rate-limited warning with the suppressed-count suffix so the
            // integrator sees both the signal and its scale.
            var nowTicks = DateTime.UtcNow.Ticks;
            var elapsedMs = (nowTicks - _lastEvictionWarnTicks) / TimeSpan.TicksPerMillisecond;
            if (_lastEvictionWarnTicks == 0 || elapsedMs >= EvictionWarnIntervalMs)
            {
                var suppressed = _droppedEvictionWarnings;
                _droppedEvictionWarnings = 0;
                _lastEvictionWarnTicks = nowTicks;
                var suffix = suppressed > 0
                    ? $" ({suppressed} similar warnings suppressed in the last {EvictionWarnIntervalMs / 1000}s)"
                    : "";
                PlayScopeLog.Warning(
                    $"LogDedupBuffer: evicted oldest entry to make room (cap {MaxEntries}). " +
                    "Some other log family is spamming — consider rate-limiting at the call site." + suffix);
            }
            else
            {
                _droppedEvictionWarnings++;
            }
        }

        // Splices repeat_count into metadata only when > 1 — count==1 emits
        // verbatim, no "repeat_count: 1" bloat on every log row.
        private void EmitWithCount(Entry e)
        {
            if (e.RepeatCount > 1)
            {
                e.Sample.MetadataJson = InjectRepeatCount(e.Sample.MetadataJson, e.RepeatCount);
            }
            _queue.Enqueue(e.Sample);
        }

        // Splices "repeat_count": N after the opening brace — hand-rolled because
        // the pipeline keeps metadata as serialized JSON throughout.
        private static string InjectRepeatCount(string? metadataJson, int count)
        {
            if (string.IsNullOrEmpty(metadataJson) || metadataJson == "{}")
                return "{\"repeat_count\":" + count + "}";
            if (metadataJson![0] == '{')
                return "{\"repeat_count\":" + count + "," + metadataJson.Substring(1);
            // Unexpected shape — leave as-is rather than produce malformed JSON.
            return metadataJson;
        }
    }
}
