using AIHWSim.Arcade;
using AIHWSim.Audio;
using AIHWSim.Core;
using AIHWSim.Core.Flight;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// The jet's arsenal: homing missiles, a fixed forward cannon, and carpet
    /// bombs, swapped in flight with Tab. Attached only by the flight bootstrap
    /// when the aircraft is a jet — the same standing <c>SasController</c> has,
    /// and for the same reason: nothing test-reachable ever grows a weapon.
    ///
    /// <b>Input is read HERE, off the key/pad tables directly</b> — the
    /// SasController pattern. Weapons never enter the actuator vector (they are
    /// not plant inputs) and never touch <c>IPilotInputSource</c> (whose two
    /// implementors would both need stubs for hardware the trainer doesn't
    /// carry). Space fires the selected weapon: pressed for a missile or a bomb
    /// stick, HELD for the cannon. Pad South mirrors it, pad West swaps.
    ///
    /// The cannon runs on HEAT rather than ammo — hold it too long and it locks
    /// out until it cools past half — while missiles and bombs count rounds.
    /// A debug range with an empty rack forever would be a range you have to
    /// restart, so <see cref="ReloadSeconds"/> quietly restocks.
    /// </summary>
    public sealed class WeaponsController : MonoBehaviour
    {
        public enum Weapon { Missiles = 0, Gun = 1, Bombs = 2 }

        public PlaneVehicle plane;
        public LockOnController lockOn;
        public SasController sas;

        private const KeyCode KeyFire = KeyCode.Space;
        private const KeyCode KeySwap = KeyCode.Tab;

        [Header("Missiles")]
        public int missileRounds = 8;
        public float missileBaseSpeed = 90f;
        [Tooltip("The Hydra's hidden modifier, in the open: air targets get "
                 + "this multiple of the base speed.")]
        public float airSpeedFactor = 2f;
        public float missileCooldown = 0.7f;

        [Header("Cannon")]
        public float roundsPerSecond = 15f;
        public float bulletSpeed = 400f;
        public float heatPerShot = 0.03f;
        public float coolPerSecond = 0.35f;

        [Header("Bombs")]
        public int bombRounds = 12;
        public int stickSize = 6;
        public float stickInterval = 0.15f;

        [Header("Lifecycle")]
        public const float ReloadSeconds = 25f;

        public Weapon Selected { get; private set; } = Weapon.Missiles;
        public int MissilesLeft { get; private set; }
        public int BombsLeft { get; private set; }
        /// <summary>0…1; at 1 the gun locks out until it cools below 0.5.</summary>
        public float GunHeat { get; private set; }
        public bool GunLocked { get; private set; }

        private float _nextMissileAt, _nextRoundAt, _nextBombAt;
        private int _stickRemaining;
        private int _rackSide = 1;      // alternate wings
        private float _restockAt = -1f;

        // Hardpoints, body-local. Authored here rather than on the spec: they
        // are weapon furniture, not flight hardware, and the spec stays aero.
        private static readonly Vector3 GunMuzzle = new Vector3(0f, -0.25f, 3.2f);
        private static readonly Vector3 BombBay = new Vector3(0f, -0.65f, -0.4f);
        private static Vector3 Rack(int side) => new Vector3(side * 1.6f, -0.35f, 0.3f);

        private void Awake()
        {
            MissilesLeft = missileRounds;
            BombsLeft = bombRounds;
        }

        private void Update()
        {
            if (plane == null) return;
            float dt = Time.deltaTime;

            // Swap. The seeker only hunts while missiles are up.
            if (KeyTable.Pressed(KeySwap) || PadTable.PressedAny(PadButton.West))
                Selected = (Weapon)(((int)Selected + 1) % 3);
            if (lockOn != null) lockOn.active = Selected == Weapon.Missiles;

            // Cannon cooling runs whether or not it is selected.
            GunHeat = Mathf.Max(0f, GunHeat - coolPerSecond * dt);
            if (GunLocked && GunHeat < 0.5f) GunLocked = false;

            // The freebie the lock earns: SAS Target mode chases what the
            // seeker is tracking, and gets its pylon back when the lock drops.
            if (sas != null && lockOn != null)
            {
                if (_originalSasTarget == null) _originalSasTarget = sas.target;
                sas.target = lockOn.Target != null && lockOn.State == LockOnController.LockState.Locked
                    ? lockOn.Target.transform : _originalSasTarget;
            }

            bool firePressed = KeyTable.Pressed(KeyFire) || PadTable.PressedAny(PadButton.South);
            bool fireHeld = KeyTable.Held(KeyFire) || PadTable.HeldAny(PadButton.South);

            switch (Selected)
            {
                case Weapon.Missiles:
                    if (firePressed) TryFireMissile();
                    break;
                case Weapon.Gun:
                    if (fireHeld) TryFireGun();
                    break;
                case Weapon.Bombs:
                    if (firePressed) TryStartStick();
                    break;
            }

            // A running stick releases on its own clock regardless of the key.
            if (_stickRemaining > 0 && Time.time >= _nextBombAt) ReleaseBomb();

            // Quiet restock, so the range never goes silent for good.
            if (MissilesLeft == 0 && BombsLeft == 0 && _restockAt < 0f)
                _restockAt = Time.time + ReloadSeconds;
            if (_restockAt > 0f && Time.time >= _restockAt)
            {
                MissilesLeft = missileRounds;
                BombsLeft = bombRounds;
                _restockAt = -1f;
            }
        }

        private Transform _originalSasTarget;

        private void TryFireMissile()
        {
            if (MissilesLeft <= 0 || Time.time < _nextMissileAt) return;
            MissilesLeft--;
            _nextMissileAt = Time.time + missileCooldown;
            _rackSide = -_rackSide;

            WeaponTarget tgt = lockOn != null
                               && lockOn.State == LockOnController.LockState.Locked
                ? lockOn.Target : null;

            var go = new GameObject("AirMissile");
            go.transform.SetPositionAndRotation(
                plane.transform.TransformPoint(Rack(_rackSide)),
                plane.transform.rotation);
            ArcadeVfx.BuildMissile(go.transform);

            var m = go.AddComponent<AirMissile>();
            m.owner = plane.transform;
            m.target = tgt;
            // The hidden modifier: twice the speed at aircraft. An unlocked
            // shot flies at base speed, straight.
            m.speed = tgt != null && tgt.category == WeaponTarget.Category.Air
                ? missileBaseSpeed * airSpeedFactor : missileBaseSpeed;

            SfxPlayer.Ensure()?.PlayAt(ProceduralAudio.MissileFire, go.transform.position);
        }

        private void TryFireGun()
        {
            if (GunLocked || Time.time < _nextRoundAt) return;
            _nextRoundAt = Time.time + 1f / roundsPerSecond;

            GunHeat += heatPerShot;
            if (GunHeat >= 1f) { GunHeat = 1f; GunLocked = true; }

            Vector3 muzzle = plane.transform.TransformPoint(GunMuzzle);
            // A whisker of spread, so the stream is a cone rather than a laser.
            Vector3 dir = Quaternion.Euler(Random.Range(-0.4f, 0.4f),
                                           Random.Range(-0.4f, 0.4f), 0f)
                          * plane.transform.forward;
            Bullet.Fire(plane.transform, muzzle, dir, bulletSpeed,
                        PlaneVelocity());
            SfxPlayer.Ensure()?.PlayAt(ProceduralAudio.CannonFire, muzzle, 0.7f,
                                       0.95f + Random.value * 0.1f);
        }

        private void TryStartStick()
        {
            if (BombsLeft <= 0 || _stickRemaining > 0) return;
            _stickRemaining = Mathf.Min(stickSize, BombsLeft);
            _nextBombAt = Time.time;   // first one now
        }

        private void ReleaseBomb()
        {
            _stickRemaining--;
            BombsLeft--;
            _nextBombAt = Time.time + stickInterval;

            Vector3 bay = plane.transform.TransformPoint(BombBay);
            CarpetBomb.Drop(plane.transform, bay, PlaneVelocity());
            SfxPlayer.Ensure()?.PlayAt(ProceduralAudio.BombDrop, bay, 0.8f);
        }

        private Vector3 PlaneVelocity()
        {
            var rb = plane.GetComponent<Rigidbody>();
            return rb != null ? rb.linearVelocity : Vector3.zero;
        }
    }
}
