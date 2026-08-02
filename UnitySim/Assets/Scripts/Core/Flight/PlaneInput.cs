using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Turns pilot input into the actuator vector, the way <see cref="CarInput"/>
    /// does for a car. Manual mode fills the actuators directly through
    /// <see cref="IManualDriver"/>; autonomous mode would expose setpoints through
    /// <see cref="ISetpointSource"/>, and that seam exists here without anything
    /// reading it yet.
    ///
    /// <b>The <c>source ??=</c> appears in BOTH Awake and Update, deliberately.</b>
    /// It is what lets a scripted test assign <see cref="source"/> once and have it
    /// survive every frame, while a human scene gets a default without anyone
    /// wiring one. Scripted flight and hand flight are then the same code path with
    /// one field swapped, which is the only way a test can claim to measure what a
    /// pilot would experience. <c>CarInput</c> does exactly this.
    /// </summary>
    public sealed class PlaneInput : MonoBehaviour, IManualDriver, ISetpointSource
    {
        public PlaneVehicle plane;

        /// <summary>Swap this for a scripted pilot in a test.</summary>
        public IPilotInputSource source;

        /// <summary>Optional stability assist, layered over the pilot's commands.
        /// Attached by the free-flight bootstrap and by nothing else — every
        /// scripted test leaves it null, so the [AERO] gate measures the aeroplane
        /// rather than an autopilot flying it.</summary>
        public SasController sas;

        /// <summary>Reset is a scene-level concern, so the bootstrap subscribes
        /// rather than this class knowing where the runway is.</summary>
        public System.Action ResetRequested;
        public System.Action ViewToggleRequested;

        private readonly float[] _setpoints = new float[4];

        /// <summary>Only the human source has held state to advance; a scripted
        /// pilot is stateless, so this stays null for it.</summary>
        private PilotInputSource _human;

        /// <summary>The human source, or null when a scripted pilot is flying.
        /// The bootstrap uses it to apply preferences like elevator sense without
        /// having to own the object.</summary>
        public PilotInputSource Human
        {
            get { EnsureSource(); return _human; }
        }

        private void Awake() => EnsureSource();

        private void Update()
        {
            EnsureSource();
            _human?.Tick(Time.deltaTime);

            if (source.ResetPressed()) ResetRequested?.Invoke();
            if (source.ViewTogglePressed()) ViewToggleRequested?.Invoke();
        }

        private void EnsureSource()
        {
            if (source == null)
            {
                _human = new PilotInputSource();
                source = _human;
            }
            else if (_human != null && !ReferenceEquals(source, _human))
            {
                // Someone swapped in a scripted pilot after Awake: stop ticking the
                // human one, or its ratcheted throttle keeps climbing underneath a
                // test that believes it commands the aircraft.
                _human = null;
            }
        }

        public void ReadManualCommands(float[] actuatorOut)
        {
            EnsureSource();

            float maxVolts = plane != null && plane.spec != null
                ? plane.spec.motor.maxVoltage : 1f;

            actuatorOut[PlaneVehicle.ThrottleActuator] =
                Mathf.Clamp01(source.Throttle()) * maxVolts;
            actuatorOut[PlaneVehicle.AileronActuator] = Mathf.Clamp(source.Roll(), -1f, 1f);
            actuatorOut[PlaneVehicle.ElevatorActuator] = Mathf.Clamp(source.Pitch(), -1f, 1f);
            actuatorOut[PlaneVehicle.RudderActuator] = Mathf.Clamp(source.Yaw(), -1f, 1f);

            // Stability assist gets the last word on aileron and elevator, and it
            // reads what the pilot asked for to decide whether to take them at all.
            // This runs here rather than in Update because the simulation runner
            // calls ReadManualCommands from FixedUpdate — which is the rate the
            // hold loops these gains came from were tuned at.
            sas?.Apply(actuatorOut);

            // Slots 6 and 7 are named steer and brake by the published C ABI. An
            // aeroplane has neither, and writing anything there would be a claim
            // this vehicle is not entitled to make.
            actuatorOut[6] = 0f;
            actuatorOut[7] = 0f;
        }

        /// <summary>
        /// Declared, unread: target airspeed, roll, pitch and heading — the four an
        /// autopilot would want. Exposed so the seam is visible without an ABI
        /// claim; nothing consumes it while <c>loadControllerDll</c> is false.
        /// </summary>
        public float[] Setpoints
        {
            get
            {
                EnsureSource();
                _setpoints[0] = Mathf.Clamp01(source.Throttle());
                _setpoints[1] = source.Roll();
                _setpoints[2] = source.Pitch();
                _setpoints[3] = source.Yaw();
                return _setpoints;
            }
        }
    }
}
