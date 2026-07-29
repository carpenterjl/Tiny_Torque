using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// One team's flag: a pole and a cloth on a base plate, in three states —
    /// home on its plinth, carried by a car, or dropped on the floor waiting to
    /// be picked up or returned.
    ///
    /// **Containment is polled by the director, not triggered.** The reasoning
    /// is <c>AreaHazard</c>'s, which this copies deliberately: a car is one
    /// transform with a root box AND four wheel colliders, so a trigger fires
    /// several times per car per pass; re-arming while a car sits inside needs
    /// OnTriggerStay, which stops firing the moment a stationary Rigidbody
    /// sleeps; and a LAN host's copies of client cars are kinematic followers,
    /// which is exactly where trigger callbacks get murky. A distance check in
    /// the director's own loop has none of those problems.
    ///
    /// Visuals only, therefore: no collider, no Rigidbody, nothing to decide.
    /// </summary>
    public sealed class Flag : MonoBehaviour
    {
        public enum State { Home, Carried, Dropped }

        public int team;
        public State state = State.Home;

        /// <summary>Where it lives when nobody has it.</summary>
        public Vector3 home;

        /// <summary>The carrier, or null.</summary>
        public MatchRacer carrier;

        /// <summary>Director clock when it was dropped, for the auto-return.</summary>
        public float droppedAt;

        private float _bobPhase;

        /// <summary>Build the flag's geometry. Procedural rather than an
        /// authored FBX: the mode furniture is a pole, a cloth and a plate, and
        /// the Blender pipeline's cost is not worth three boxes. (Replacing
        /// these with authored props later is a clean follow-up — the arcade
        /// props started exactly here.)</summary>
        public static Flag Create(Transform parent, int team, Vector3 home)
        {
            var go = new GameObject($"Flag_{team}");
            go.transform.SetParent(parent, false);
            go.transform.position = home;

            var tint = ModeDirector.TeamColors[Mathf.Clamp(team, 0, ModeDirector.TeamColors.Length - 1)];
            var poleMat = TrackBuilder.StandardMat(new Color(0.82f, 0.84f, 0.88f));
            var clothMat = TrackBuilder.StandardMat(tint);

            // TrackBuilder's helpers set a WORLD pose, so the offsets are added
            // to home rather than left local. Collider-less: the flag decides
            // nothing and must never shove a car.
            TrackBuilder.Cylinder("pole", home + new Vector3(0f, 0.11f, 0f),
                new Vector3(0.012f, 0.11f, 0.012f), Quaternion.identity, poleMat,
                go.transform, collider: false);
            TrackBuilder.Box("cloth", home + new Vector3(0.055f, 0.185f, 0f),
                new Vector3(0.10f, 0.06f, 0.006f), Quaternion.identity, clothMat,
                go.transform, collider: false);

            var flag = go.AddComponent<Flag>();
            flag.team = team;
            flag.home = home;
            return flag;
        }

        /// <summary>Idle motion so a dropped flag is findable, and so a carried
        /// one reads as being carried. Cosmetic; the director owns the rules.</summary>
        public void Tick(float dt)
        {
            _bobPhase += dt * 2.2f;
            switch (state)
            {
                case State.Carried when carrier?.car != null:
                    transform.position = carrier.car.transform.position + Vector3.up * 0.13f;
                    transform.rotation = Quaternion.Euler(0f, _bobPhase * 90f, 0f);
                    break;
                case State.Dropped:
                    transform.rotation = Quaternion.Euler(0f, _bobPhase * 40f, 0f);
                    break;
                default:
                    transform.position = home;
                    transform.rotation = Quaternion.Euler(0f, _bobPhase * 25f, 0f);
                    break;
            }
        }

        public void SendHome()
        {
            state = State.Home;
            carrier = null;
            transform.position = home;
        }

        public void PickUp(MatchRacer by)
        {
            state = State.Carried;
            carrier = by;
        }

        public void Drop(float clock)
        {
            state = State.Dropped;
            carrier = null;
            droppedAt = clock;
            transform.position = ArenaNav.Drop(transform.position);
        }
    }
}
