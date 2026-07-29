using System.Collections.Generic;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// What a racing line is to a circuit, this is to an arena.
    ///
    /// Five systems in this codebase quietly assume every session has an ordered
    /// centreline — bot steering, the respawn key, arcade item-box placement,
    /// missile targeting and wreck recovery all read `TrackSpine`. An arena has
    /// no line and never will, so those systems do not need a worse line: they
    /// need the two questions they were really asking, answered differently.
    /// Those are "where can a car legally be?" and "where do I put this car?".
    ///
    /// Deliberately NOT a nav mesh. The arenas are open floors ringed by walls,
    /// which means the floor's own bounds plus the authored spawn ring answer
    /// both questions, and a bot avoids walls by looking at them (see
    /// BotDriver's arena mode) rather than by following a graph. A nav mesh here
    /// would be a week of work to reproduce a rectangle.
    /// </summary>
    public static class ArenaNav
    {
        /// <summary>Clearance above the surface, so a placed car lands on its
        /// wheels rather than starting the frame inside the floor.</summary>
        public const float RideHeight = 0.10f;

        private static readonly List<SpawnPoint> _spawns = new List<SpawnPoint>();
        private static BuiltTrack _built;
        private static Bounds _floor;
        private static bool _haveFloor;

        public static bool Available => _spawns.Count > 0;

        /// <summary>Every spawn the arena authored, in map order.</summary>
        public static IReadOnlyList<SpawnPoint> Spawns => _spawns;

        /// <summary>The arena's middle — where a ball is kicked off, where a
        /// respawned car is pointed, and what a bot falls back to.</summary>
        public static Vector3 Centre { get; private set; }

        /// <summary>Half the floor's smaller horizontal extent: a usable "how
        /// big is this place" for spacing pickups and clamping wander.</summary>
        public static float Radius { get; private set; } = 10f;

        public static void SetTrack(BuiltTrack built)
        {
            Clear();
            if (built == null) return;
            _built = built;

            foreach (var s in built.spawns) _spawns.Add(s);

            if (built.floorCollider != null)
            {
                _floor = built.floorCollider.bounds;
                _haveFloor = true;
                Centre = new Vector3(_floor.center.x, _floor.min.y, _floor.center.z);
                Radius = Mathf.Max(1f, Mathf.Min(_floor.extents.x, _floor.extents.z));
            }
            else if (_spawns.Count > 0)
            {
                // No floor slab (a pure spline map): the spawn ring is the only
                // statement about where the arena is, so average it.
                var sum = Vector3.zero;
                foreach (var s in _spawns) sum += s.pos;
                Centre = sum / _spawns.Count;
                float far = 0f;
                foreach (var s in _spawns) far = Mathf.Max(far, Vector3.Distance(s.pos, Centre));
                Radius = Mathf.Max(1f, far);
            }
        }

        public static void Clear()
        {
            _spawns.Clear();
            _built = null;
            _haveFloor = false;
            Centre = Vector3.zero;
            Radius = 10f;
        }

        /// <summary>Is this point inside the arena floor at all? Used to notice a
        /// ball or a flag that has escaped, and to keep bots from chasing one.</summary>
        public static bool Contains(Vector3 p)
        {
            if (!_haveFloor) return Vector3.Distance(Flat(p), Flat(Centre)) <= Radius * 1.2f;
            return p.x >= _floor.min.x && p.x <= _floor.max.x
                && p.z >= _floor.min.z && p.z <= _floor.max.z;
        }

        /// <summary>
        /// The <paramref name="index"/>-th spawn belonging to
        /// <paramref name="team"/>, wrapping when a team has fewer spawns than
        /// players. Falls back to the whole ring when the arena authored no team
        /// split (a derby), and to the arena centre when it authored nothing.
        /// </summary>
        public static bool TrySpawn(int team, int index, out Vector3 pos, out Quaternion rot)
        {
            var pool = new List<SpawnPoint>();
            if (team >= 0)
                foreach (var s in _spawns) if (s.team == team) pool.Add(s);
            if (pool.Count == 0) pool.AddRange(_spawns);

            if (pool.Count == 0)
            {
                pos = Centre + Vector3.up * RideHeight;
                rot = Quaternion.identity;
                return false;
            }

            var pick = pool[((index % pool.Count) + pool.Count) % pool.Count];
            pos = pick.pos + Vector3.up * RideHeight;
            rot = pick.rot;
            return true;
        }

        /// <summary>
        /// The spawn nearest <paramref name="from"/> that no living car is
        /// sitting on, for a respawn that should not cost half the arena.
        /// <paramref name="occupied"/> is the clearance a car needs.
        /// </summary>
        public static bool TryNearestFree(Vector3 from, float occupied,
            out Vector3 pos, out Quaternion rot)
        {
            pos = from;
            rot = Quaternion.identity;
            if (_spawns.Count == 0) return false;

            var cars = Object.FindObjectsByType<CarVehicle>(FindObjectsSortMode.None);
            float best = float.MaxValue;
            bool found = false;
            foreach (var s in _spawns)
            {
                bool blocked = false;
                foreach (var c in cars)
                    if (c != null && Vector3.Distance(Flat(c.transform.position), Flat(s.pos)) < occupied)
                    { blocked = true; break; }
                if (blocked) continue;

                float d = Vector3.SqrMagnitude(Flat(s.pos) - Flat(from));
                if (d >= best) continue;
                best = d;
                pos = s.pos + Vector3.up * RideHeight;
                rot = s.rot;
                found = true;
            }

            if (found) return true;
            // Everything is crowded: take the nearest anyway rather than refuse
            // to respawn, which would strand a car on its roof forever.
            var first = _spawns[0];
            pos = first.pos + Vector3.up * RideHeight;
            rot = first.rot;
            return true;
        }

        /// <summary>Drop a point onto the arena floor, preferring the factory's
        /// floor/ribbon-only raycast so props and cars are never landed on.</summary>
        public static Vector3 Drop(Vector3 hint)
        {
            if (_built != null && TrackFactory.DropToSurface(_built, hint, out var p, out _))
                return p + Vector3.up * RideHeight;
            return new Vector3(hint.x, Centre.y + RideHeight, hint.z);
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
