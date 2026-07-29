using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// A floor pickup: drive through it for a repair or for a mine to drop, and
    /// it comes back on a clock. Copied in shape from <c>ArcadeItemBox</c>,
    /// including its LAN rule — on a client the collider is destroyed at Awake,
    /// because clients render pickups and never grant them.
    ///
    /// Unlike the flag and goal zones this IS a trigger, and for the reason the
    /// arcade box gets away with one: it disables itself on the first touch, so
    /// the "several contacts per car per pass" problem resolves itself.
    ///
    /// One component with a kind rather than two nearly identical ones: the
    /// spin, the respawn clock and the LAN rule are the whole of both.
    /// </summary>
    public sealed class ArenaPickup : MonoBehaviour
    {
        public enum Kind
        {
            /// <summary>The repair wrench: heals on contact.</summary>
            Repair,
            /// <summary>The bomb crate: arms a mine the driver can drop.</summary>
            Mine,
        }

        public Kind kind = Kind.Repair;
        public bool Active { get; private set; } = true;

        private Collider _col;
        private Transform _skin;
        private float _respawnAt;
        private float _spin;

        public static ArenaPickup Create(Transform parent, Vector3 pos, Kind kind)
        {
            var go = new GameObject(kind == Kind.Repair ? "RepairPack" : "MineCrate");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;

            var skin = new GameObject("skin").transform;
            skin.SetParent(go.transform, false);
            BuildSkin(skin, pos, kind);

            var pickup = go.AddComponent<ArenaPickup>();
            pickup.kind = kind;
            pickup._skin = skin;

            var col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = 0.16f;
            pickup._col = col;

            // Kinematic body: required for reliable trigger callbacks.
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            var net = Net.NetSession.Instance;
            if (net != null && !net.IsHost) Destroy(col);
            return pickup;
        }

        /// <summary>Primitive geometry — a fat green cross for the wrench, a dark
        /// puck with a red cap for the bomb. Both read from any angle, which is
        /// all a floor pickup has to do. (Authored props are a clean follow-up;
        /// the arcade items started exactly here too.)</summary>
        private static void BuildSkin(Transform skin, Vector3 pos, Kind kind)
        {
            if (kind == Kind.Repair)
            {
                var mat = TrackBuilder.StandardMat(new Color(0.15f, 0.85f, 0.35f));
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(0.1f, 0.7f, 0.25f));
                TrackBuilder.Box("bar_h", pos, new Vector3(0.16f, 0.05f, 0.05f),
                    Quaternion.identity, mat, skin, collider: false);
                TrackBuilder.Box("bar_v", pos, new Vector3(0.05f, 0.05f, 0.16f),
                    Quaternion.identity, mat, skin, collider: false);
                return;
            }

            var shell = TrackBuilder.StandardMat(new Color(0.14f, 0.14f, 0.16f));
            var cap = TrackBuilder.StandardMat(new Color(0.85f, 0.12f, 0.08f));
            cap.EnableKeyword("_EMISSION");
            cap.SetColor("_EmissionColor", new Color(0.7f, 0.08f, 0.05f));
            TrackBuilder.Box("crate", pos, new Vector3(0.13f, 0.13f, 0.13f),
                Quaternion.Euler(0f, 45f, 0f), shell, skin, collider: false);
            TrackBuilder.Cylinder("fuse", pos + Vector3.up * 0.09f,
                new Vector3(0.04f, 0.02f, 0.04f), Quaternion.identity, cap, skin,
                collider: false);
        }

        private void Update()
        {
            _spin += Time.deltaTime * 90f;
            if (_skin != null) _skin.localRotation = Quaternion.Euler(0f, _spin, 0f);

            if (Active) return;
            var dir = DerbyDirector.Instance;
            if (dir == null || dir.Clock < _respawnAt) return;
            Respawn();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!Active || other.isTrigger) return;
            var dir = DerbyDirector.Instance;
            if (dir == null || !dir.IsAuthority) return;
            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;
            var racer = dir.RacerOf(car);
            if (racer == null || !racer.alive) return;

            if (kind == Kind.Repair)
            {
                if (racer.health >= racer.maxHealth) return;   // nothing to give
                racer.Heal(ModeConfig.HealthPackHeal);
                Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiLevelUp);
            }
            else
            {
                if (racer.heldMines > 0) return;               // one at a time
                racer.heldMines = 1;
                Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiUnlock);
            }
            Deplete(dir.Clock);
        }

        public void Deplete(float clock)
        {
            Active = false;
            _respawnAt = clock + ModeConfig.PickupRespawnSec;
            if (_skin != null) _skin.gameObject.SetActive(false);
            if (_col != null) _col.enabled = false;
        }

        public void Respawn()
        {
            Active = true;
            if (_skin != null) _skin.gameObject.SetActive(true);
            if (_col != null) _col.enabled = true;
        }
    }
}
