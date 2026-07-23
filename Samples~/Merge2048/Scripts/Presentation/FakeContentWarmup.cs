using System;
using System.Collections;
using UnityEngine;

namespace Merge2048.Presentation
{
    // Simulated "content warmup" — no real asset work, just discrete progress
    // ticks over a fixed duration so the Boot screen has something to show
    // before the actual scene load (phase 2) starts.
    public sealed class FakeContentWarmup
    {
        private const int TICK_COUNT = 8;
        private const float TOTAL_DURATION_SECONDS = 1.5f;

        public IEnumerator Run(Action<float> onProgress)
        {
            float tickDuration = TOTAL_DURATION_SECONDS / TICK_COUNT;

            for (int tick = 1; tick <= TICK_COUNT; tick++)
            {
                yield return new WaitForSeconds(tickDuration);

                float progress01 = (float)tick / TICK_COUNT;
                onProgress?.Invoke(progress01);
            }
        }
    }
}
