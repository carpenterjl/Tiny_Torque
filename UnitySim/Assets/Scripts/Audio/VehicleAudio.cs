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

        /// <summary>Ghost fallback for the skid: 0-1 slide intensity, pushed in
        /// alongside <see cref="externalSpeed"/>. A ghost has no tyres to read,
        /// but its streamed velocity expressed in its own frame gives a lateral
        /// slip angle that is a perfectly good proxy for "audibly sliding" —
        /// ClientCarView computes it, this component only listens.</summary>
        public float externalSlip01;

        /// <summary>Which horn this car carries (VehicleDesign.hornStyle),
        /// copied on at the attach sites. 0 normal / 1 siren / 2 truck /
        /// 3 musical / 4 clown.</summary>
        public int hornStyle;

        /// <summary>Hold-to-sound, pushed by the owner's CarInput each frame.</summary>
        public bool hornHeld;

        /// <summary>Ghost/remote path: the streamed horn bit, pushed by
        /// ClientCarView (ghosts) or the host's OwnState handler (remote rigs).</summary>
        public bool externalHorn;

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
        /// is already zero until the tyres are genuinely past the grip limit —
        /// the "is the car actually sliding" decision lives with the physics —
        /// but a car driven on the limit crosses that line dozens of times a
        /// lap, and answering every crossing with a chirp is what wore the
        /// sound out. So the gate here asks for a slide, not a touch: this
        /// deadband, HELD for <see cref="SkidOnsetSeconds"/>, at real road
        /// speed (<see cref="SkidMinSpeed"/>).</summary>
        private const float SkidThreshold = 0.10f;
        /// <summary>How long the slip must stay past the threshold before the
        /// squeal opens. One noisy physics step, a kerb strike, a momentary
        /// scrub — none of them survives this, a real slide barely notices it.</summary>
        private const float SkidOnsetSeconds = 0.09f;
        /// <summary>Ground speed below which a slide is silent, and the speed
        /// at which it reaches full voice. Parking-lot scrubs and the first
        /// half-metre of a launch have real slip and no audible drama.</summary>
        private const float SkidMinSpeed = 1.2f;
        private const float SkidFullSpeed = 4.5f;
        private const float ImpactMinSpeed = 0.8f;   // ignore resting contact chatter

        private AudioSource _engine;
        private AudioSource _skid;
        private AudioSource _horn;        // lazy: most cars never honk
        private float _hornVol;
        private float _pitch = 1f;
        private float _engineVol;
        private float _skidVol;
        private float _skidPitch = 1f;
        private float _lastImpact;
        private float _slipHeldFor;
        /// <summary>Per-instance voice: a fixed pitch offset plus a Perlin seed,
        /// so two cars sliding together are two voices, not one loop doubled —
        /// and one car's long slide wanders instead of holding a note.</summary>
        private float _skidPitchBase = 1f;
        private float _skidSeed;

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

            // Deterministic per-instance, not random: the instance ID is stable
            // for the life of the object, which is all a voice needs.
            int id = Mathf.Abs(GetInstanceID());
            _skidPitchBase = Mathf.Lerp(0.93f, 1.08f, (id % 997) / 996f);
            _skidSeed = (id % 4093) * 0.113f;
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
            if (!Enabled || Time.timeScale <= 0f) { Silence(); return; }

            float gain = Mathf.Clamp01(SettingsStore.Current.engineVolume);
            if (gain <= 0f)
            {
                // Engine slider at zero mutes the drivetrain, not the horn —
                // that lives in the SFX bucket (see UpdateHorn).
                if (_engine != null) _engine.volume = 0f;
                if (_skid != null) _skid.volume = 0f;
                UpdateHorn();
                return;
            }

            float targetPitch, targetEngine, targetSkid;
            float targetSkidPitch = 1f;

            if (car != null)
            {
                float omega = FastestMotorOmega();
                float n = Mathf.Clamp01(omega / MotorOmegaFullScale);
                targetPitch = Mathf.Lerp(MinPitch, MaxPitch, n);
                targetEngine = IdleVolume + (1f - IdleVolume) * n;

                float speed = Mathf.Sqrt(car.ForwardSpeed * car.ForwardSpeed +
                                         car.LateralSpeed * car.LateralSpeed);
                ComputeSkid(car.TyreSlip01, speed, out targetSkid, out targetSkidPitch);
            }
            else
            {
                // Ghost: no drivetrain to read, so pitch tracks speed instead.
                float n = Mathf.Clamp01(Mathf.Abs(externalSpeed) / 10f);
                targetPitch = Mathf.Lerp(MinPitch, MaxPitch, n);
                targetEngine = IdleVolume + (1f - IdleVolume) * n;
                // The same skid voice as a real car, fed the pushed-in proxy —
                // a sliding ghost squeals from where it actually is.
                ComputeSkid(externalSlip01, Mathf.Abs(externalSpeed),
                    out targetSkid, out targetSkidPitch);
            }

            // Smooth, so a single noisy frame does not click the pitch.
            float k = 1f - Mathf.Exp(-Time.deltaTime * 12f);
            _pitch = Mathf.Lerp(_pitch, targetPitch, k);
            _engineVol = Mathf.Lerp(_engineVol, targetEngine, k);
            // Asymmetric on the skid: it swells in (so the gate opening is a
            // sound starting, not a switch flipping) and dies quickly (grip
            // returning is instant, and a squeal outliving it reads as lag).
            float ks = 1f - Mathf.Exp(-Time.deltaTime * (targetSkid > _skidVol ? 6f : 14f));
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

            UpdateHorn();
        }

        /// <summary>
        /// The horn voice. Lazily created on the first press (most cars never
        /// honk), then volume-gated like the other loops. Gain bucket is
        /// SFX, not engine — a horn is a deliberate action, and it must not
        /// vanish for players who turn the motor drone down.
        /// </summary>
        private void UpdateHorn()
        {
            bool wants = hornHeld || externalHorn;
            if (_horn == null)
            {
                if (!wants) return;
                _horn = MakeLoop(ProceduralAudio.HornKey(hornStyle));
                if (_horn == null) return;
            }
            // Fast attack, faster release: a horn answers the button.
            float kh = 1f - Mathf.Exp(-Time.deltaTime * (wants ? 30f : 40f));
            _hornVol = Mathf.Lerp(_hornVol, wants ? 0.9f : 0f, kh);
            _horn.volume = _hornVol * SfxPlayer.SfxGain;
        }

        /// <summary>
        /// The one skid voice, shared by the real-car path (TyreSlip01) and the
        /// ghost path (the streamed proxy). The gate wants a SLIDE, not a slip
        /// reading: past the deadband, held for the onset time, at road speed —
        /// each leg kills a different false trigger (noise spikes, kerb taps,
        /// and standing-start scrubs respectively).
        /// </summary>
        private void ComputeSkid(float slip01, float speed, out float vol, out float pitch)
        {
            if (slip01 > SkidThreshold) _slipHeldFor += Time.deltaTime;
            else _slipHeldFor = 0f;

            float depth = slip01 <= SkidThreshold
                ? 0f
                : Mathf.Clamp01((slip01 - SkidThreshold) / (1f - SkidThreshold));
            float speedGain = Mathf.Clamp01(
                (speed - SkidMinSpeed) / (SkidFullSpeed - SkidMinSpeed));

            vol = _slipHeldFor >= SkidOnsetSeconds ? depth * speedGain : 0f;

            // Slow Perlin wander on level and pitch, seeded per car. This is
            // what keeps a long slide from holding one note, and two cars from
            // being the same loop twice — the clip itself only has to be a
            // texture, not a performance.
            vol *= 0.78f + 0.22f * Mathf.PerlinNoise(Time.time * 0.9f, _skidSeed);

            // A deeper slide squeals lower and rougher; a light one is just a
            // chirp. Holding one fixed pitch is what makes a synthesized skid
            // sound like a sample on repeat.
            pitch = Mathf.Lerp(1.15f, 0.78f, depth) * _skidPitchBase *
                (0.96f + 0.08f * Mathf.PerlinNoise(Time.time * 1.4f, _skidSeed + 7.3f));
        }

        private void Silence()
        {
            if (_engine != null) _engine.volume = 0f;
            if (_skid != null) _skid.volume = 0f;
            if (_horn != null) _horn.volume = 0f;
            _hornVol = 0f;
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
