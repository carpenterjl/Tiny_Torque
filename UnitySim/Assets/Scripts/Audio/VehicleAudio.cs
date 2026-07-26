using AIHWSim.Persistence;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Audio
{
    /// <summary>
    /// The sound one car makes: motor whine, tyre scrub, and a thud when it hits
    /// something.
    ///
    /// Attached per rig by TrackBootstrap and per ghost by ClientCarView — NOT by
    /// VehicleFactory, which also builds the garage preview and the menu attract
    /// cars. Those are deliberately left silent: a garage that hums and a main
    /// menu that revs are worse defaults than quiet ones, and either is a
    /// one-line opt-in later.
    ///
    /// Two drive modes. On a real car it reads <see cref="CarVehicle"/> directly
    /// (motor ω for pitch, TyreSlip01 for scrub). On a LAN ghost there is no
    /// drivetrain at all, so it falls back to a plain speed value the network
    /// layer already estimates.
    ///
    /// It only ever READS the vehicle. Nothing here can influence the simulation,
    /// which is what makes it safe to attach to firmware and mission rigs.
    /// </summary>
    public sealed class VehicleAudio : MonoBehaviour
    {
        /// <summary>Global off switch, matching PartMeshLibrary.Enabled /
        /// TyreModel.Enabled — for A/B and for a silent regression run.</summary>
        public static bool Enabled = true;

        /// <summary>The car to listen to. Null on a LAN ghost.</summary>
        public CarVehicle car;

        /// <summary>Ghost fallback: metres/second, pushed in by the owner each
        /// frame when <see cref="car"/> is null.</summary>
        public float externalSpeed;

        // Idle whine so a stationary car is not silent, and the pitch band the
        // motor sweeps through. Clamped hard: a runaway pitch multiplier on a
        // synthesized loop sounds like a fault, not like speed.
        private const float IdleVolume = 0.12f;
        private const float MinPitch = 0.32f;
        private const float MaxPitch = 2.1f;
        /// <summary>Motor ω (rad/s) mapped to the top of the pitch band. The
        /// stock 2S/540-class drivetrain runs to roughly 2400 rad/s at the motor
        /// shaft.</summary>
        private const float MotorOmegaFullScale = 2400f;
        /// <summary>Slide reading below which nothing is heard. CarVehicle.TyreSlip01
        /// is already zero until the tyres are genuinely past the grip limit, so
        /// this is only a deadband against the last sliver of numerical noise —
        /// the "is the car actually sliding" decision lives with the physics, not
        /// here.</summary>
        private const float SkidThreshold = 0.04f;
        private const float ImpactMinSpeed = 0.8f;   // ignore resting contact chatter

        private AudioSource _engine;
        private AudioSource _skid;
        private float _pitch = 1f;
        private float _engineVol;
        private float _skidVol;
        private float _skidPitch = 1f;
        private float _lastImpact;

        /// <summary>Attach to a car (or to a ghost root with a null car).</summary>
        public static VehicleAudio Attach(GameObject host, CarVehicle vehicle)
        {
            if (!Enabled || host == null) return null;
            var va = host.AddComponent<VehicleAudio>();
            va.car = vehicle;
            return va;
        }

        private void Start()
        {
            _engine = MakeLoop(ProceduralAudio.Engine);
            _skid = MakeLoop(ProceduralAudio.Skid);
        }

        private AudioSource MakeLoop(string key)
        {
            var clip = ProceduralAudio.Get(key);
            if (clip == null) return null;

            var go = new GameObject(key);
            go.transform.SetParent(transform, false);
            var src = SfxPlayer.Configure(go.AddComponent<AudioSource>(), spatial: true);
            src.clip = clip;
            src.loop = true;
            src.volume = 0f;
            src.Play();      // always running; volume is the gate, so there is no
                             // start-up click when the car begins to move
            return src;
        }

        private void Update()
        {
            if (!Enabled) { Silence(); return; }

            float gain = Mathf.Clamp01(SettingsStore.Current.engineVolume);
            if (gain <= 0f || Time.timeScale <= 0f) { Silence(); return; }

            float targetPitch, targetEngine, targetSkid;
            float targetSkidPitch = 1f;

            if (car != null)
            {
                float omega = FastestMotorOmega();
                float n = Mathf.Clamp01(omega / MotorOmegaFullScale);
                targetPitch = Mathf.Lerp(MinPitch, MaxPitch, n);
                targetEngine = IdleVolume + (1f - IdleVolume) * n;

                // TyreSlip01 is already zero until the tyres are past the grip
                // limit, so this is a straight remap, not a decision.
                float slip = car.TyreSlip01;
                targetSkid = slip <= SkidThreshold
                    ? 0f
                    : Mathf.Clamp01((slip - SkidThreshold) / (1f - SkidThreshold));
                // A deeper slide squeals lower and rougher; a light one is just
                // a chirp. Holding one fixed pitch is what makes a synthesized
                // skid sound like a sample on repeat.
                targetSkidPitch = Mathf.Lerp(1.15f, 0.78f, targetSkid);
            }
            else
            {
                // Ghost: no drivetrain to read, so pitch tracks speed instead.
                float n = Mathf.Clamp01(Mathf.Abs(externalSpeed) / 10f);
                targetPitch = Mathf.Lerp(MinPitch, MaxPitch, n);
                targetEngine = IdleVolume + (1f - IdleVolume) * n;
                targetSkid = 0f;
            }

            // Smooth, so a single noisy frame does not click the pitch.
            float k = 1f - Mathf.Exp(-Time.deltaTime * 12f);
            _pitch = Mathf.Lerp(_pitch, targetPitch, k);
            _engineVol = Mathf.Lerp(_engineVol, targetEngine, k);
            float ks = 1f - Mathf.Exp(-Time.deltaTime * 8f);
            _skidVol = Mathf.Lerp(_skidVol, targetSkid, ks);
            _skidPitch = Mathf.Lerp(_skidPitch, targetSkidPitch, ks);

            if (_engine != null)
            {
                _engine.pitch = _pitch;
                _engine.volume = _engineVol * gain;
            }
            if (_skid != null)
            {
                _skid.pitch = _skidPitch;
                _skid.volume = _skidVol * gain * 0.8f;
            }
        }

        private void Silence()
        {
            if (_engine != null) _engine.volume = 0f;
            if (_skid != null) _skid.volume = 0f;
        }

        /// <summary>Loudest motor wins — with independent per-wheel motors, the
        /// one spinning fastest is the one you hear.</summary>
        private float FastestMotorOmega()
        {
            var motors = car.Motors;
            if (motors == null || motors.Count == 0)
                return Mathf.Abs(car.ForwardSpeed) * 100f;   // no motors: fake it from speed

            float best = 0f;
            for (int i = 0; i < motors.Count; i++)
            {
                var m = motors[i];
                if (m == null) continue;
                float w = Mathf.Abs(car.WheelOmega(m.wheelIndex)) * Mathf.Max(1f, m.motor.gearRatio);
                if (w > best) best = w;
            }
            return best;
        }

        /// <summary>
        /// Impact thud. Unity delivers collision callbacks to every component on
        /// the Rigidbody's GameObject, so this needs no cooperation from
        /// CarVehicle.
        /// </summary>
        private void OnCollisionEnter(Collision c)
        {
            if (!Enabled || Time.timeScale <= 0f) return;
            if (Time.time - _lastImpact < 0.08f) return;      // one thud per event
            float speed = c.relativeVelocity.magnitude;
            if (speed < ImpactMinSpeed) return;

            _lastImpact = Time.time;
            var player = SfxPlayer.Ensure();
            if (player == null) return;

            float vol = Mathf.Clamp01(speed / 8f);
            var at = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            // Bigger hits sound lower, the way a heavier impact actually does.
            player.PlayAt(ProceduralAudio.Impact, at, vol, Mathf.Lerp(1.25f, 0.8f, vol));
        }
    }
}
