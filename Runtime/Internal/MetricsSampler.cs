using System;
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
        private float _networkTimer;

        private const float FpsInterval = 1f;
        private const float MemInterval = 5f;
        private const float BatteryInterval = 30f;
        private const float NetworkInterval = 5f;

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

            // Network reachability — 5s. We emit the periodic metric for
            // dashboards that want a time-series, AND let the runtime emit a
            // discrete network_change event on every transition so the
            // timeline carries a single row at the moment the network flipped
            // instead of forcing the reader to spot a line-crossing.
            _networkTimer += dt;
            if (_networkTimer >= NetworkInterval)
            {
                var net = (int)Application.internetReachability; // 0/1/2
                _pipeline.EnqueueMetric("network_reachability", net);
                PlayScopeRuntime.RecordNetworkReachabilityIfChanged(net);
                _networkTimer = 0;
            }
        }
    }
}
