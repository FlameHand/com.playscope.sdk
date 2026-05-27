using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Samples device/runtime metrics on the Unity main thread.
    /// Call Tick() from MonoBehaviour.Update() every frame.
    /// Threading rule: ALL UnityEngine API calls on main thread only.
    ///
    /// <para>
    /// Deliberately stays on the Unity core API surface — no Addressables,
    /// no Adaptive Performance, no Analytics. Anything that lives in an
    /// optional Unity package belongs in the game-side wrapper layer, which
    /// can push samples to us via <see cref="EventPipeline.EnqueueMetric"/>
    /// or <c>PlayScope.UpdateSessionData</c>. That way the SDK package's
    /// dependency list stays minimal and a project that doesn't use
    /// Addressables can pull us in cleanly.
    /// </para>
    /// </summary>
    internal sealed class MetricsSampler
    {
        private readonly EventPipeline _pipeline;

        // Timers (seconds since last sample)
        private float _fpsAccum;
        private int _fpsFrames;
        private float _fpsTimer;

        private float _memTimer;
        private float _batteryTimer;

        // Sentinels for emit-on-change gating. -1 = "never sampled".
        // Valid emit values are 0/1/2 (network), 0.0/1.0 (charging),
        // and a non-negative MB count (disk) — so -1 can't collide.
        private int _prevNetwork = -1;
        private double _prevCharging = -1.0;
        private double _prevDiskMb = -1.0;

        // Ring buffer of per-frame deltas (ms) for the current 1 s window.
        // Sized for 2 s @ 60 fps so we never wrap during a single sample
        // window — even when the engine hits 120 Hz we still cover a full
        // second's worth of frames without losing samples. Indexed
        // modulo Length; _frameTimeCount tracks how many slots are
        // populated (caps at Length once we've cycled through).
        private readonly float[] _frameTimesMs = new float[128];
        private int _frameTimeIdx;
        private int _frameTimeCount;
        // Scratch buffer used by the p99 sort path so we don't allocate
        // a new array every second. Sized to match _frameTimesMs.
        private readonly float[] _frameTimesSortScratch = new float[128];

        // GC allocation tracking — Profiler reports cumulative bytes
        // allocated to the managed heap since process start. We sample at
        // 1 s and emit the delta so the dashboard sees a KB-per-second
        // rate that's meaningful on its own (vs the cumulative value
        // which only matters as a derivative).
        private long _lastTotalAllocatedBytes = -1;

        // UnityEngine.Device.SystemInfo.thermalStatus exists from 2023.1+.
        // On 2021.3 (our floor) the type isn't present, so we resolve via
        // reflection at first sample and cache the PropertyInfo. Null
        // means "API unavailable on this Unity" → we skip the emit.
        private PropertyInfo _thermalStatusProperty;
        private bool _thermalReflectionResolved;

        private const float FpsInterval = 1f;
        private const float MemInterval = 5f;
        private const float BatteryInterval = 30f;
        private const double DISK_CHANGE_THRESHOLD_MB = 5.0;
        // Frame-time + gc-alloc share the same 1 s cadence as fps. They're
        // jank-class metrics that only mean something on a sub-second
        // window — averaging them over multiple seconds smears the spike
        // we're trying to detect into invisibility.
        // Threshold for "dropped frame": ANY frame longer than this counts
        // against the dropped_frames_count metric for the current window.
        // 33.4 ms = ½ of a 60 Hz budget, captures both 30 Hz targets that
        // overran AND 60 Hz targets that doubled their budget — the only
        // useful definition of "dropped" that doesn't depend on the
        // target frame rate (which we don't try to introspect here).
        private const float DroppedFrameThresholdMs = 33.4f;

        internal MetricsSampler(EventPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>Called every frame from MonoBehaviour.Update() on main thread.</summary>
        internal void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            float dtMs = dt * 1000f;

            // Frame-time ring buffer push — every frame, no rate-limiting.
            // The 1 s sample window below drains whatever's accumulated.
            _frameTimesMs[_frameTimeIdx] = dtMs;
            _frameTimeIdx = (_frameTimeIdx + 1) % _frameTimesMs.Length;
            if (_frameTimeCount < _frameTimesMs.Length) _frameTimeCount++;

            // FPS — 1s rolling average
            _fpsAccum += 1f / Mathf.Max(dt, 0.0001f);
            _fpsFrames++;
            _fpsTimer += dt;
            if (_fpsTimer >= FpsInterval)
            {
                var fps = _fpsAccum / _fpsFrames;
                _pipeline.EnqueueMetric("fps", fps);

                // Frame-time p99 + dropped-frame count for the same 1 s
                // window. p99 (vs p95 or max) is the sweet spot for jank
                // — picks up the single bad frame in 100 without being
                // dominated by an outlier blip that doesn't repeat.
                EmitFrameTimeMetrics();

                // GC allocations for the same window. Profiler counter
                // is cumulative; we emit the delta as a kB/s rate.
                EmitGcAllocMetric();

                _fpsAccum = 0; _fpsFrames = 0; _fpsTimer = 0;
                // Reset the ring buffer's "occupied" count but NOT its
                // index — the next window starts overwriting from where
                // the last one ended, which is fine because we only read
                // _frameTimeCount slots when computing the next p99.
                _frameTimeCount = 0;
            }

            // Memory — 5s
            _memTimer += dt;
            if (_memTimer >= MemInterval)
            {
                var heapMB = GC.GetTotalMemory(false) / 1048576.0;
                var reservedMB = Profiler.GetTotalReservedMemoryLong() / 1048576.0;
                _pipeline.EnqueueMetric("memory_heap", heapMB);
                _pipeline.EnqueueMetric("memory_unity_reserved", reservedMB);
                _memTimer = 0;
            }

            // Battery + thermal + charging + disk + system RAM — 30s.
            // All five share the same slot: they're slow-moving device-
            // state signals where 30 s resolution is plenty and a single
            // tick keeps the timer count small.
            _batteryTimer += dt;
            if (_batteryTimer >= BatteryInterval)
            {
                EmitSlowDeviceMetrics();
                _batteryTimer = 0;
            }
        }

        // Computes p99 frame-time and dropped-frame count from the current
        // ring-buffer window. p99 means "the value at index 0.99 * N after
        // sorting ascending" — so for a 60-frame window that's frame #59
        // (sorted). With a partial window we use the same fraction.
        // Bails when the window is too small for p99 to be meaningful
        // (< 10 frames in a 1 s window means the game was running below
        // 10 fps, at which point jank metrics are noise and the fps
        // metric alone tells the story).
        private void EmitFrameTimeMetrics()
        {
            int n = _frameTimeCount;
            if (n < 10) return;

            // Copy into the scratch buffer and sort. Array.Sort is in-place
            // quicksort under the hood — O(N log N) on a 128-element window
            // is single-digit microseconds, completely fine to run every
            // second on the main thread.
            Array.Copy(_frameTimesMs, _frameTimesSortScratch, n);
            Array.Sort(_frameTimesSortScratch, 0, n);

            // p99 index: floor(0.99 * (n-1)). For n=60 → 58, for n=120 → 117.
            int p99Index = (int)Math.Floor(0.99 * (n - 1));
            float p99Ms = _frameTimesSortScratch[p99Index];
            _pipeline.EnqueueMetric("frame_time_p99_ms", p99Ms);

            // Dropped frame count — walk the unsorted buffer (or the sorted
            // one; the count is order-invariant). Cheap linear scan, no
            // allocation. Using the sorted buffer means we can break out
            // early on the first sample <= threshold from the high end,
            // but the buffer is small enough that a full scan is fine.
            int dropped = 0;
            for (int i = 0; i < n; i++)
            {
                if (_frameTimesMs[i] > DroppedFrameThresholdMs) dropped++;
            }
            _pipeline.EnqueueMetric("dropped_frames_count", dropped);
        }

        private void EmitSlowDeviceMetrics()
        {
            try
            {
                var battery = SystemInfo.batteryLevel;
                if (battery >= 0f)
                {
                    _pipeline.EnqueueMetric("battery_level", battery * 100.0);
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("MetricsSampler: battery_level read failed: " + ex.Message);
            }

            try
            {
                var status = SystemInfo.batteryStatus;
                double charging = status == BatteryStatus.Charging ? 1.0 : 0.0;
                // state-only signal — emit on transition only
                if (charging != _prevCharging)
                {
                    _pipeline.EnqueueMetric("is_charging", charging);
                    _prevCharging = charging;
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("MetricsSampler: is_charging read failed: " + ex.Message);
            }

            var thermal = ReadThermalState();
            if (thermal.HasValue)
            {
                _pipeline.EnqueueMetric("thermal_state", thermal.Value);
            }

            try
            {
                var root = Path.GetPathRoot(Application.persistentDataPath);
                if (!string.IsNullOrEmpty(root))
                {
                    var info = new DriveInfo(root);
                    double mb = info.AvailableFreeSpace / (1024.0 * 1024.0);
                    // slow-drift signal — emit on first sample + when delta crosses threshold
                    if (_prevDiskMb < 0 || Math.Abs(mb - _prevDiskMb) >= DISK_CHANGE_THRESHOLD_MB)
                    {
                        _pipeline.EnqueueMetric("available_disk_mb", mb);
                        _prevDiskMb = mb;
                    }
                }
                else
                {
                    _pipeline.EnqueueMetric("available_disk_mb", 0.0);
                }
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("MetricsSampler: available_disk_mb read failed: " + ex.Message);
                _pipeline.EnqueueMetric("available_disk_mb", 0.0);
            }

            _pipeline.EnqueueMetric("system_free_ram_mb", NativeMetricsBridge.GetFreeMemoryMb());

            // polling cadence collapsed from 5 s; the periodic emit was redundant with the network_change event flow
            try
            {
                var net = (int)Application.internetReachability;
                // state-only signal — emit on transition only
                if (net != _prevNetwork)
                {
                    _pipeline.EnqueueMetric("network_reachability", net);
                    _prevNetwork = net;
                }
                PlayScopeRuntime.RecordNetworkReachabilityIfChanged(net);
            }
            catch (Exception ex)
            {
                PlayScopeLog.Warning("MetricsSampler: network_reachability read failed: " + ex.Message);
            }
        }

        private double? ReadThermalState()
        {
            if (!_thermalReflectionResolved)
            {
                try
                {
                    var t = Type.GetType("UnityEngine.Device.SystemInfo, UnityEngine.CoreModule")
                            ?? Type.GetType("UnityEngine.Device.SystemInfo");
                    _thermalStatusProperty = t?.GetProperty("thermalStatus", BindingFlags.Public | BindingFlags.Static);
                }
                catch
                {
                    _thermalStatusProperty = null;
                }
                _thermalReflectionResolved = true;
            }

            if (_thermalStatusProperty == null)
            {
                return null;
            }

            try
            {
                var value = _thermalStatusProperty.GetValue(null);
                if (value == null)
                {
                    return null;
                }
                return Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        // Emits gc_alloc_kb (delta since the last sample). First sample is
        // skipped — we don't have a previous total to delta against, and
        // emitting "everything allocated since process start" would be
        // a meaningless huge number that drags the dashboard scale.
        //
        // <para>
        // Profiler.GetTotalAllocatedMemoryLong returns the cumulative bytes
        // allocated to the managed heap since the process started. Available
        // on every Unity Player; Development Build is NOT required (unlike
        // some other Profiler APIs). The value can briefly decrease across
        // a sample if a GC ran in-between — we clamp the delta to >= 0 to
        // avoid emitting a negative rate.
        // </para>
        private void EmitGcAllocMetric()
        {
            long currentTotal = Profiler.GetTotalAllocatedMemoryLong();
            if (_lastTotalAllocatedBytes < 0)
            {
                _lastTotalAllocatedBytes = currentTotal;
                return;
            }
            long deltaBytes = Math.Max(0, currentTotal - _lastTotalAllocatedBytes);
            _lastTotalAllocatedBytes = currentTotal;
            double deltaKb = deltaBytes / 1024.0;
            _pipeline.EnqueueMetric("gc_alloc_kb", deltaKb);
        }
    }
}
