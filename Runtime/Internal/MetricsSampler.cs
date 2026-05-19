using System;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.AddressableAssets;

namespace PlayScopeSdk.Internal
{
    /// <summary>
    /// Samples device/runtime metrics on the Unity main thread.
    /// Call Tick() from MonoBehaviour.Update() every frame.
    /// Threading rule: ALL UnityEngine API calls on main thread only.
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
        private float _networkTimer;
        private float _addressablesTimer;

        private const float FpsInterval = 1f;
        private const float MemInterval = 5f;
        private const float BatteryInterval = 30f;
        private const float NetworkInterval = 5f;
        // Periodic Addressables handle count — leaks show up as a slowly
        // growing line on the dashboard. 15 s is fine: a real leak grows
        // over minutes; sampling faster would just pad ingest for no signal.
        private const float AddressablesInterval = 15f;

        internal MetricsSampler(EventPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>Called every frame from MonoBehaviour.Update() on main thread.</summary>
        internal void Tick()
        {
            float dt = Time.unscaledDeltaTime;

            // FPS — 1s rolling average
            _fpsAccum += 1f / Mathf.Max(dt, 0.0001f);
            _fpsFrames++;
            _fpsTimer += dt;
            if (_fpsTimer >= FpsInterval)
            {
                var fps = _fpsAccum / _fpsFrames;
                _pipeline.EnqueueMetric("fps", fps);
                _fpsAccum = 0; _fpsFrames = 0; _fpsTimer = 0;
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

            // Battery — 30s
            _batteryTimer += dt;
            if (_batteryTimer >= BatteryInterval)
            {
                var battery = SystemInfo.batteryLevel;
                if (battery >= 0f) // -1 = unsupported, discard
                    _pipeline.EnqueueMetric("battery_level", battery * 100.0);
                _batteryTimer = 0;
            }

            // Network reachability — 5s
            _networkTimer += dt;
            if (_networkTimer >= NetworkInterval)
            {
                var net = (int)Application.internetReachability; // 0/1/2
                _pipeline.EnqueueMetric("network_reachability", net);
                _networkTimer = 0;
            }

            // Addressables active operations — 15s. A monotonically growing
            // line means somewhere a load handle was retained without
            // Addressables.Release; classic source of slow OOM crashes in
            // long-running sessions.
            _addressablesTimer += dt;
            if (_addressablesTimer >= AddressablesInterval)
            {
                try
                {
                    int count = Addressables.ResourceManager.OperationCacheCount;
                    _pipeline.EnqueueMetric("addressables_handles", count);
                }
                catch (Exception)
                {
                    // Addressables may not be initialized yet (e.g. during
                    // very early SDK init). Silent skip — next tick will retry.
                }
                _addressablesTimer = 0;
            }
        }
    }
}
