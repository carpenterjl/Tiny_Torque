using System.Collections.Generic;
using AIHWSim.Arcade;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// "Put this car back on the track, near where it is now."
    ///
    /// The respawn key used to call <see cref="CarVehicle.ResetVehicle"/>, which
    /// teleports to the start line. On a 100–140 m circuit that costs most of a
    /// lap, so getting stuck against a wall was punished about as hard as taking
    /// a missile — and unlike a missile it was usually the track's fault. The
    /// arcade layer already solved this for wreck recovery; this is the same
    /// solution, lifted somewhere every mode can reach it.
    ///
    /// Static, and composed by <see cref="TrackBootstrap"/> alongside the bot
    /// path, because the alternative is threading a reference through every rig
    /// builder in three scene compositions to reach one key press. It is
    /// overwritten on every scene build and yields nothing when no racing line
    /// exists (a finish-less tile map), which is exactly when falling back to the
    /// spawn point is the only thing left to do.
    /// </summary>
    public static class TrackRespawn
    {
        private static TrackSpine _spine;
        private static BuiltTrack _built;

        /// <summary>Clearance above the surface, so the car lands on its wheels
        /// rather than starting the frame intersecting the ribbon.</summary>
        private const float RideHeight = 0.10f;

        /// <summary>Is there a racing line to respawn onto?</summary>
        public static bool Available => _spine != null;

        /// <summary>Called once per scene build, with whatever the bot path came
        /// out as. A null path disables the whole feature rather than guessing.</summary>
        public static void SetTrack(BuiltTrack built, IReadOnlyList<Vector3> path, bool closed)
        {
            _built = built;
            _spine = TrackSpine.From(path, closed);
        }

        public static void Clear()
        {
            _spine = null;
            _built = null;
        }

        /// <summary>
        /// Nearest point on the racing line to <paramref name="from"/>, as a pose
        /// facing down-track.
        ///
        /// The direction comes from the line, not from the car: a car that needs
        /// respawning is usually nose-first into a wall or upside down, so its own
        /// forward vector is the one thing about it worth discarding.
        ///
        /// <paramref name="hint"/> seeds the spine's local search and is updated
        /// in place — pass the caller's own persistent int so a respawn on a
        /// hairpin does not snap to the other side of it.
        /// </summary>
        public static bool TryPose(Vector3 from, ref int hint, out Vector3 pos, out Quaternion rot)
        {
            pos = from;
            rot = Quaternion.identity;
            if (_spine == null) return false;

            float s = _spine.Project(from, ref hint);
            _spine.Sample(s, out var onLine, out var lineFwd);

            // Drop onto the actual surface: the spine is a centreline through
            // space, and on the elevated sections (Neon Vortex II's bridge,
            // Workshop's plank) it can sit metres above or below the ribbon.
            if (DropToSurface(onLine, out var surf)) onLine = surf;

            pos = onLine + Vector3.up * RideHeight;
            rot = Quaternion.LookRotation(lineFwd, Vector3.up);
            return true;
        }

        /// <summary>
        /// Drop onto the track surface, preferring the factory's floor/ribbon-only
        /// raycast so cars and props are never landed on.
        ///
        /// Deliberately a copy of <c>ArcadeDirector.DropToSurface</c> rather than a
        /// shared call: that one answers from the director's own BuiltTrack, and
        /// making item-box placement depend on whether this static happened to be
        /// composed first would trade twelve obvious lines for an ordering bug.
        /// </summary>
        private static bool DropToSurface(Vector3 hint, out Vector3 pos)
        {
            if (_built != null && TrackFactory.DropToSurface(_built, hint, out pos, out _)) return true;

            pos = hint;
            var hits = Physics.RaycastAll(hint + Vector3.up * 3f, Vector3.down, 60f);
            bool any = false;
            foreach (var h in hits)
            {
                if (h.collider.isTrigger) continue;
                if (h.collider.GetComponentInParent<CarVehicle>() != null) continue;
                if (!any || h.point.y > pos.y) { pos = h.point; any = true; }
            }
            return any;
        }
    }
}
