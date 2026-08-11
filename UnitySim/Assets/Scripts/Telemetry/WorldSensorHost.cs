using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// Ticks the world telemetry hub: accumulates FixedUpdate time to a fixed
    /// 50 Hz and samples every registered <see cref="IWorldSensor"/> on its own
    /// world clock. Created by TrackBootstrap during scene composition. Awake
    /// makes a fresh hub; the sensor registry and the signal fields are NOT
    /// cleared here — Awake order against scene-authored props is undefined,
    /// and emitters/sensors already clean themselves up OnDisable at scene
    /// teardown.
    /// </summary>
    public sealed class WorldSensorHost : MonoBehaviour
    {
        private const int ScratchSize = 32;

        private readonly float[] _scratch = new float[ScratchSize];
        private float _accum;
        private float _worldTime;

        private void Awake()
        {
            WorldTelemetry.Reset();
        }

        private void FixedUpdate()
        {
            float period = 1f / WorldTelemetry.WorldRateHz;
            _accum += Time.fixedDeltaTime;
            while (_accum >= period)
            {
                _accum -= period;
                _worldTime += period;
                WorldTelemetry.Tick(period, _worldTime, _scratch);
            }
        }

        private void OnDestroy()
        {
            WorldTelemetry.Shutdown();
        }
    }
}
