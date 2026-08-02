using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Stability assist, on Kerbal Space Program's model: one mode at a time, a
    /// toggle, a momentary override, and a set of directions it will point the nose
    /// at. Six buttons on the HUD, <c>T</c> to switch it on, <c>F</c> to hold it off
    /// while you fly manually, <c>1</c>–<c>6</c> to pick a mode.
    ///
    /// <b>It lives on the input side, and that is deliberate.</b>
    /// <see cref="PlaneVehicle"/> says in its own summary that it has no stability
    /// augmentation — the aeroplane's damping and stiffness are supposed to emerge
    /// from panel geometry, and a hidden autopilot inside the vehicle would make
    /// every measurement in the [AERO] gate a measurement of the autopilot. So this
    /// is a layer over the pilot's commands, applied by <see cref="PlaneInput"/>
    /// after the human has had their say, and it is attached only in the free-flight
    /// scene. The scripted tests never build one.
    ///
    /// <b>The gains are not new.</b> Every one of them is copied from the hold loops
    /// in <c>Core/PhysicsTests/FlightTest.cs</c>, which were tuned against this exact
    /// airframe and whose sign conventions are counter-intuitive enough to be worth
    /// restating here:
    /// <list type="bullet">
    /// <item>+Z is forward, so a POSITIVE z-Euler is banked LEFT, and the correction
    /// is right aileron, which is a positive roll command. Both roll gains are
    /// positive; the intuitive <c>-bank</c> is positive feedback and departs.</item>
    /// <item>A POSITIVE x-Euler is nose DOWN.</item>
    /// <item>Vertical speed is two integrations from the elevator. It is never
    /// closed straight onto the stick — always through a pitch ATTITUDE — because
    /// the flat version rang and pulled −3.9 g the first time anything used it.</item>
    /// </list>
    ///
    /// Two gains ARE new, and are marked where they appear: the bank-per-degree of
    /// heading error, and the vertical-speed-per-metre of altitude error.
    /// </summary>
    public sealed class SasController : MonoBehaviour
    {
        /// <summary>Declaration order is the order the HUD draws the buttons and the
        /// order the number keys select, so the two cannot drift apart.</summary>
        public enum SasMode
        {
            StabilityAssist = 0,
            Prograde = 1,
            Retrograde = 2,
            Target = 3,
            WingsLevel = 4,
            AltitudeHold = 5,
        }

        public PlaneVehicle plane;

        /// <summary>What <see cref="SasMode.Target"/> points at. Null is a fine
        /// state — the mode refuses to engage and the HUD greys its button.</summary>
        public Transform target;

        /// <summary>Degrees of bank per degree of heading error. <b>New gain</b>,
        /// not inherited from the test harness: nothing in the [AERO] suite ever
        /// needed to steer a heading. Kept gentle, and hard-limited to 30° of bank —
        /// this airframe cannot hold a steep turn (it needs about 4.2 N at 60° and
        /// has under 3 N), so asking for one would trade height for nothing.</summary>
        public float headingToBank = 0.8f;

        /// <summary>Metres per second of climb per metre of altitude error.
        /// <b>New gain</b>, and deliberately soft. It feeds the vertical-speed
        /// cascade rather than the elevator, so the loop it closes is a third one
        /// outside two that are already known-good; the way to keep that stable is
        /// to make the outermost the slowest.</summary>
        public float altitudeToClimb = 0.25f;

        /// <summary>Stick deflection above which the pilot owns that axis outright.
        /// Below it a centred stick with a little noise on it would otherwise fight
        /// the autopilot for control.</summary>
        public float stickBreakout = 0.10f;

        // ---- loop gains. The DEFAULTS are the FlightTest hold-loop values,
        // tuned on the SportRc trainer, verbatim — promoting them to fields
        // changes nothing for the trainer (same numbers) and the [AERO] gate
        // never attaches a SAS at all. They are fields so a different airframe
        // (the VTOL jet, whose hover has no aerodynamic damping to lean on) can
        // be given its own values by its bootstrap, marked TUNED at the call.
        /// <summary>Aileron per degree of bank error. FlightTest.HoldWingsLevel.</summary>
        public float bankGain = 0.030f;
        /// <summary>Aileron per deg/s of roll rate. FlightTest.HoldWingsLevel.</summary>
        public float rollRateGain = 0.004f;
        /// <summary>Elevator per degree of pitch-attitude error.
        /// FlightTest.HoldVerticalSpeed's inner loop.</summary>
        public float pitchGain = 0.06f;
        /// <summary>Elevator per deg/s of pitch rate. Same inner loop.</summary>
        public float pitchRateGain = 0.010f;

        private const KeyCode KeyToggle = KeyCode.T;
        private const KeyCode KeyOverride = KeyCode.F;

        public bool Enabled { get; private set; }
        public SasMode Mode { get; private set; } = SasMode.StabilityAssist;

        /// <summary>True while the momentary override is held. The HUD dims itself
        /// so the reason the aeroplane stopped holding attitude is on screen.</summary>
        public bool OverrideHeld { get; private set; }

        /// <summary>Whether a mode can be engaged at all — Target needs a target.</summary>
        public bool Available(SasMode m) => m != SasMode.Target || target != null;

        private Rigidbody _body;
        private float _capBank, _capPitch, _capAlt;

        private void Awake()
        {
            if (plane == null) plane = GetComponent<PlaneVehicle>();
            _body = plane != null ? plane.GetComponent<Rigidbody>() : null;
        }

        // ---- pilot-facing controls (frame rate: these are edges) ----

        private void Update()
        {
            OverrideHeld = KeyTable.Held(KeyOverride);

            if (KeyTable.Pressed(KeyToggle) || PadTable.PressedAny(PadButton.DpadUp))
                Toggle();

            if (PadTable.PressedAny(PadButton.DpadRight)) Cycle(+1);
            if (PadTable.PressedAny(PadButton.DpadLeft)) Cycle(-1);

            for (int i = 0; i < 6; i++)
                if (KeyTable.Pressed(KeyCode.Alpha1 + i))
                    SetMode((SasMode)i);
        }

        public void Toggle()
        {
            Enabled = !Enabled;
            if (Enabled) Capture();
        }

        /// <summary>Engage a mode. Picking the mode that is already running turns SAS
        /// off, which is how a radio button that is also a toggle has to behave.</summary>
        public void SetMode(SasMode m)
        {
            if (!Available(m)) return;
            if (Enabled && Mode == m) { Enabled = false; return; }
            Mode = m;
            Enabled = true;
            Capture();
        }

        private void Cycle(int step)
        {
            for (int i = 1; i <= 6; i++)
            {
                var m = (SasMode)((((int)Mode + step * i) % 6 + 6) % 6);
                if (!Available(m)) continue;
                Mode = m;
                Enabled = true;
                Capture();
                return;
            }
        }

        // ---- the loops (control rate: called from PlaneInput) ----

        /// <summary>
        /// Overwrite the aileron and elevator commands with what the active mode
        /// wants. Called from <see cref="PlaneInput.ReadManualCommands"/>, which the
        /// simulation runner drives inside FixedUpdate — the same cadence the
        /// original hold loops were tuned at.
        ///
        /// <b>The throttle is never touched.</b> KSP's SAS does not fly the throttle
        /// and neither does this: the ratchet is the pilot's, and an autopilot that
        /// quietly moved it would make the gauge a liar. The rudder is not touched
        /// either — there is no tuned yaw loop anywhere in this project, and
        /// inventing one here would be authoring a coefficient by feel, which is the
        /// one thing the flight model does not do.
        /// </summary>
        public void Apply(float[] a)
        {
            if (!Enabled || plane == null || _body == null) return;
            if (!Available(Mode)) { Enabled = false; return; }

            // Which axes the pilot has taken back. Re-capturing every tick while
            // they hold the stick means that the instant they let go, the target IS
            // wherever they left the aeroplane — deflect to steer, release to hold.
            bool pilotRoll = OverrideHeld
                             || Mathf.Abs(a[PlaneVehicle.AileronActuator]) > stickBreakout;
            bool pilotPitch = OverrideHeld
                              || Mathf.Abs(a[PlaneVehicle.ElevatorActuator]) > stickBreakout;
            if (pilotRoll || pilotPitch) Capture();

            switch (Mode)
            {
                case SasMode.StabilityAssist:
                    if (!pilotRoll) a[PlaneVehicle.AileronActuator] = RollToBank(_capBank);
                    if (!pilotPitch) a[PlaneVehicle.ElevatorActuator] = PitchToAttitude(_capPitch);
                    break;

                // Wings level leaves the elevator alone on purpose — that is the
                // whole difference from stability assist, and it is the mode you
                // want while hand-flying a climb or a descent.
                case SasMode.WingsLevel:
                    if (!pilotRoll) a[PlaneVehicle.AileronActuator] = RollToBank(0f);
                    break;

                case SasMode.AltitudeHold:
                    if (!pilotRoll) a[PlaneVehicle.AileronActuator] = RollToBank(0f);
                    if (!pilotPitch)
                    {
                        float climb = Mathf.Clamp((_capAlt - plane.AltitudeAgl) * altitudeToClimb,
                                                  -3f, 3f);
                        a[PlaneVehicle.ElevatorActuator] = PitchForClimb(climb);
                    }
                    break;

                case SasMode.Prograde:
                    PointAt(a, Velocity(), pilotRoll, pilotPitch);
                    break;
                case SasMode.Retrograde:
                    PointAt(a, -Velocity(), pilotRoll, pilotPitch);
                    break;
                case SasMode.Target:
                    PointAt(a, target.position - plane.transform.position, pilotRoll, pilotPitch);
                    break;
            }
        }

        /// <summary>
        /// Fly the nose onto a world direction: bank to swing the heading round, and
        /// pitch to the direction's climb angle.
        ///
        /// <b>An aeroplane cannot truly hold prograde, and this does not pretend
        /// to.</b> Putting the nose exactly on the velocity vector means flying at
        /// zero angle of attack, and a wing at zero alpha is not carrying its
        /// weight — so prograde hold settles into a shallow descent, deepening until
        /// the pitch clamp catches it. That is a real property of a wing rather than
        /// a defect in the loop, and it is the reason the clamp exists.
        /// </summary>
        private void PointAt(float[] a, Vector3 dir, bool pilotRoll, bool pilotPitch)
        {
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();

            if (!pilotRoll)
            {
                // Turn toward the target by banking. SIGN: a positive heading error
                // means we must turn RIGHT, a right bank is a NEGATIVE z-Euler,
                // hence the minus.
                float want = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                Vector3 fwd = plane.transform.forward;
                float have = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                float bank = Mathf.Clamp(-Mathf.DeltaAngle(have, want) * headingToBank, -30f, 30f);
                a[PlaneVehicle.AileronActuator] = RollToBank(bank);
            }

            if (!pilotPitch)
            {
                // Climb angle of the direction, as a pitch attitude. Nose UP is a
                // NEGATIVE x-Euler, hence the minus; the clamp is the same band the
                // vertical-speed loop uses.
                float climbDeg = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
                a[PlaneVehicle.ElevatorActuator] =
                    PitchToAttitude(Mathf.Clamp(-climbDeg, -14f, 10f));
            }
        }

        /// <summary>Aileron to hold a bank angle. Gains from
        /// <c>FlightTest.HoldWingsLevel</c>; both positive, see the class summary.</summary>
        private float RollToBank(float targetBankDeg)
        {
            float bank = Wrap180(plane.transform.eulerAngles.z) - targetBankDeg;
            float rollRate = _body.transform.InverseTransformDirection(_body.angularVelocity).z
                             * Mathf.Rad2Deg;
            return Mathf.Clamp(bank * bankGain + rollRate * rollRateGain, -1f, 1f);
        }

        /// <summary>Elevator to hold a pitch attitude — the INNER loop of
        /// <c>FlightTest.HoldVerticalSpeed</c>, gains unchanged.</summary>
        private float PitchToAttitude(float wantedDeg)
        {
            float pitchDeg = Wrap180(plane.transform.eulerAngles.x);
            float rate = _body.transform.InverseTransformDirection(_body.angularVelocity).x
                         * Mathf.Rad2Deg;
            return Mathf.Clamp((pitchDeg - wantedDeg) * pitchGain + rate * pitchRateGain,
                               -1f, 1f);
        }

        /// <summary>Elevator to hold a climb rate — the full cascade from
        /// <c>FlightTest.HoldVerticalSpeed</c>, outer gain and clamp unchanged.</summary>
        private float PitchForClimb(float targetVs)
        {
            float vsErr = targetVs - plane.VerticalSpeed;
            return PitchToAttitude(Mathf.Clamp(-vsErr * 1.2f, -14f, 10f));
        }

        private Vector3 Velocity() => _body.linearVelocity;

        /// <summary>Freeze the current attitude and height as the thing to hold.</summary>
        private void Capture()
        {
            if (plane == null) return;
            _capBank = Wrap180(plane.transform.eulerAngles.z);
            _capPitch = Mathf.Clamp(Wrap180(plane.transform.eulerAngles.x), -14f, 10f);
            _capAlt = plane.AltitudeAgl;
        }

        private static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
