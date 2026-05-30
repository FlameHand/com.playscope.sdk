using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Samples device/runtime metrics on the Unity main thread (all UnityEngine
    /// calls main-thread only). Tick() every frame from MonoBehaviour.Update().
    /// Stays on the Unity core API — no Addressables / Adaptive Performance /
    /// Analytics — so the package dependency list stays minimal; optional-package
    /// metrics belong in the game-side wrapper, pushed via EnqueueMetric.
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

        // Emit-on-change sentinels; -1 = never sampled (can't collide with valid 0/1/2 or MB counts).
        private int _prevNetwork = -1;
        private double _prevCharging = -1.0;
        private double _prevDiskMb = -1.0;

        // Per-frame delta (ms) ring buffer for the current 1 s window. 128 slots
        // = 2 s @ 60 fps so we never wrap mid-window even at 120 Hz.
        private readonly float[] _frameTimesMs = new float[128];
        private int _frameTimeIdx;
        private int _frameTimeCount;
        // Scratch for the p99 sort so we don't allocate every second.
        private readonly float[] _frameTimesSortScratch = new float[128];

        // Profiler total is cumulative-since-start; we emit the 1 s delta as a
        // kB/s rate (the cumulative value only matters as a derivative).
        private long _lastTotalAllocatedBytes = -1;

        // UnityEngine.Device.SystemInfo.thermalStatus is 2023.1+; on our 2021.3
        // floor the type is absent, so resolve via reflection once. Null = skip emit.
        private PropertyInfo _thermalStatusProperty;
        private bool _thermalReflectionResolved;

        private const float FpsInterval = 1f;
        private const float MemInterval = 5f;
        // Slow device-state cadence (battery/thermal/charging/disk/RAM/network).
        // 10 s so a couple-minute session still gets several near-static samples.
        private const float BatteryInterval = 10f;
        private const double DISK_CHANGE_THRESHOLD_MB = 5.0;
        // "Dropped frame" threshold = ½ of a 60 Hz budget — catches 30 Hz overruns
        // AND 60 Hz doublings without needing the target frame rate. Frame-time +
        // gc-alloc stay on the 1 s cadence; averaging over more would smear the spike.
        private const float DroppedFrameThresholdMs = 33.4f;

        internal MetricsSampler(EventPipeline pipeline)
        {
            _pipeline = pipeline;
            // Prime so the first Tick emits a device-state baseline at t≈0 — else
            // a sub-interval session captures zero device-state samples.
            _batteryTimer = BatteryInterval;
        }

        /// <summary>Called every frame from MonoBehaviour.Update() on main thread.</summary>
        internal void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            float dtMs = dt * 1000f;

            // Push every frame; the 1 s window below drains what accumulated.
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

                // p99 (vs p95/max) is the jank sweet spot — catches the 1-in-100
                // bad frame without being dominated by a non-repeating outlier.
                EmitFrameTimeMetrics();
                EmitGcAllocMetric();

                _fpsAccum = 0; _fpsFrames = 0; _fpsTimer = 0;
                // Reset so the next window writes from slot 0 ([0..count-1] contiguous).
                _frameTimeIdx = 0;
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

            // Battery / thermal / charging / disk / RAM share one slow slot.
            _batteryTimer += dt;
            if (_batteryTimer >= BatteryInterval)
            {
                EmitSlowDeviceMetrics();
                _batteryTimer = 0;
            }
        }

        /// <summary>
        /// Resets emit-on-change sentinels + GC-alloc baseline so the new
        /// session's first metric fires instead of being suppressed by a carried-
        /// over _prev*. Belt-and-suspenders: rotation destroys the MB today, so
        /// fields already start fresh.
        /// </summary>
        internal void ResetForNewSession()
        {
            _prevCharging = -1.0;
            _prevDiskMb = -1.0;
            _prevNetwork = -1;
            _lastTotalAllocatedBytes = -1;
            _batteryTimer = BatteryInterval; // re-prime device-state baseline
            // Frame-time buffer + thermal cache are window/process-scoped, not session — leave them.
        }

        // p99 frame-time + dropped-frame count from the ring-buffer window.
        // Bails under 10 frames/sec — below that, fps alone tells the story and jank metrics are noise.
        private void EmitFrameTimeMetrics()
        {
            int n = _frameTimeCount;
            if (n < 10) return;

            Array.Copy(_frameTimesMs, _frameTimesSortScratch, n);
            Array.Sort(_frameTimesSortScratch, 0, n);

            // p99 index: floor(0.99 * (n-1)).
            int p99Index = (int)Math.Floor(0.99 * (n - 1));
            float p99Ms = _frameTimesSortScratch[p99Index];
            _pipeline.EnqueueMetric("frame_time_p99_ms", p99Ms);

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
                if (charging != _prevCharging) // emit on transition only
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

            // DriveInfo is not implemented in IL2CPP (icall throws every tick),
            // so disk reads go through the native bridge. -1 = unavailable —
            // skip emit rather than poison the metric with a fake 0.
            long diskMb = NativeMetricsBridge.GetAvailableDiskMb();
            if (diskMb >= 0)
            {
                double mb = diskMb;
                if (_prevDiskMb < 0 || Math.Abs(mb - _prevDiskMb) >= DISK_CHANGE_THRESHOLD_MB)
                {
                    _pipeline.EnqueueMetric("available_disk_mb", mb);
                    _prevDiskMb = mb;
                }
            }

            _pipeline.EnqueueMetric("system_free_ram_mb", NativeMetricsBridge.GetFreeMemoryMb());

            try
            {
                var net = (int)Application.internetReachability;
                if (net != _prevNetwork) // emit on transition only
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

        // Emits gc_alloc_kb (delta since last sample). First sample is skipped —
        // the cumulative-since-start total would be a meaningless huge number.
        // GetTotalAllocatedMemoryLong works on every Player (no Dev Build needed);
        // a GC between samples can make it dip, so we clamp the delta to >= 0.
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
