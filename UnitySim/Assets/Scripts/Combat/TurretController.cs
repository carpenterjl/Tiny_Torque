using AIHWSim.Audio;
using AIHWSim.Core;
using AIHWSim.Core.Flight;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// The "passenger" machine gun, flown solo: press C and you climb into a
    /// free-aim belly turret while SAS holds the aeroplane; press C again and
    /// you are the pilot back in your own seat, camera and SAS exactly as you
    /// left them. The seam is deliberately the one a LAN gunner would plug into
    /// later — everything the turret needs (a pivot, an aim, a trigger) lives
    /// here, and nothing about it knows the aimer is also the pilot.
    ///
    /// While gunning, the FLIGHT KEYS STILL WORK — SAS is only holding
    /// attitude, so throttle and nozzles remain the pilot's, which is exactly
    /// how you creep a hover sideways while hosing the range.
    ///
    /// Mouse free-aim, cursor locked; yaw ±160° so the turret cannot shoot the
    /// tail it is bolted to, pitch +10…−80° because a belly gun's job is DOWN.
    /// </summary>
    public sealed class TurretController : MonoBehaviour
    {
        public PlaneVehicle plane;
        public FlightCameraRig cameras;
        public SasController sas;

        private const KeyCode KeyTurret = KeyCode.C;

        public float mouseSensitivity = 2.2f;
        public float roundsPerSecond = 12f;
        public float bulletSpeed = 400f;

        public bool InTurret { get; private set; }
        /// <summary>Turret aim, degrees, relative to the airframe.</summary>
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        private Transform _pivot;
        private FlightCameraRig.View _priorView;
        private bool _priorSasEnabled;
        private SasController.SasMode _priorSasMode;
        private float _nextRoundAt;

        private void Update()
        {
            if (plane == null || cameras == null) return;

            if (KeyTable.Pressed(KeyTurret)
                || PadTable.PressedAny(PadButton.RightStickPress))
            {
                if (InTurret) Exit();
                else Enter();
            }

            if (!InTurret) return;

            // Aim. Old-input mouse axes — the flight scene already reads
            // KeyCode directly, and the turret follows the house style.
            Yaw = Mathf.Clamp(Yaw + Input.GetAxis("Mouse X") * mouseSensitivity,
                              -160f, 160f);
            Pitch = Mathf.Clamp(Pitch - Input.GetAxis("Mouse Y") * mouseSensitivity,
                                -80f, 10f);
            _pivot.localRotation = Quaternion.Euler(-Pitch, Yaw, 0f);

            bool fire = Input.GetMouseButton(0)
                        || KeyTable.Held(KeyCode.Space)
                        || PadTable.HeldAny(PadButton.South);
            if (fire && Time.time >= _nextRoundAt)
            {
                _nextRoundAt = Time.time + 1f / roundsPerSecond;
                Vector3 muzzle = _pivot.position + _pivot.forward * 0.8f;
                Vector3 vel = plane.GetComponent<Rigidbody>() is Rigidbody rb
                    ? rb.linearVelocity : Vector3.zero;
                Bullet.Fire(plane.transform, muzzle, _pivot.forward, bulletSpeed, vel);
                SfxPlayer.Ensure()?.PlayAt(ProceduralAudio.CannonFire, muzzle, 0.6f,
                                           0.9f + Random.value * 0.1f);
            }
        }

        private void Enter()
        {
            if (_pivot == null)
            {
                // The belly pivot, made on first use and kept: under the CG so
                // swinging the view never shows the inside of the fuselage.
                _pivot = new GameObject("TurretPivot").transform;
                _pivot.SetParent(plane.transform, false);
                _pivot.localPosition = new Vector3(0f, -0.75f, -0.55f);
            }

            InTurret = true;
            Yaw = 0f;
            Pitch = -30f;
            _pivot.localRotation = Quaternion.Euler(-Pitch, 0f, 0f);

            // SAS takes the aeroplane. Remember exactly what it was doing so
            // handing back the controls is a restore, not a guess.
            if (sas != null)
            {
                _priorSasEnabled = sas.Enabled;
                _priorSasMode = sas.Mode;
                if (!sas.Enabled || sas.Mode != SasController.SasMode.StabilityAssist)
                    sas.SetMode(SasController.SasMode.StabilityAssist);
            }

            _priorView = cameras.Current;
            cameras.turretPivot = _pivot;
            cameras.Apply(FlightCameraRig.View.Turret);

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Exit()
        {
            InTurret = false;

            cameras.Apply(_priorView);

            if (sas != null)
            {
                // Back exactly as it was — minding that SetMode on the CURRENT
                // mode is a toggle-off, so "already right" must mean touch
                // nothing at all.
                if (_priorSasEnabled)
                {
                    if (sas.Mode != _priorSasMode) sas.SetMode(_priorSasMode);
                    else if (!sas.Enabled) sas.Toggle();
                }
                else if (sas.Enabled) sas.Toggle();
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
