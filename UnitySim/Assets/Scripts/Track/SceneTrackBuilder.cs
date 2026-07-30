using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Turns a hand-authored scene into the same <see cref="BuiltTrack"/> a tile
    /// map produces, so everything downstream — <c>BotPath</c>, <c>TrackRespawn</c>,
    /// <c>ArcadeDirector</c>, the race director — consumes one shape and neither
    /// knows nor cares which source it came from.
    ///
    /// The geometry already exists in the scene, so unlike <c>TrackFactory.Build</c>
    /// this creates almost nothing: gate triggers, a <see cref="SurfaceMap"/>, and a
    /// root to hang them off. Its real job is filling the contract completely and
    /// being explicit about the one field it cannot fill.
    /// </summary>
    public static class SceneTrackBuilder
    {
        /// <summary>Cars hover this far above the surface they spawn on, matching
        /// <c>TrackFactory.ResolveSpawn</c>.</summary>
        private const float SpawnLift = 0.08f;

        public static BuiltTrack Build(SceneTrackDescriptor d, bool interactive)
        {
            var built = new BuiltTrack { root = new GameObject("SceneTrack") };
            var rootT = built.root.transform;

            // floorCollider stays NULL, and that is a decision rather than an
            // omission. There is no single slab under a hand-authored scene —
            // there is terrain, road mesh and props, each with its own collider.
            // Of its consumers, TrackFactory.DropToSurface's two callers
            // (ArcadeDirector item-box drops, TrackRespawn) already fall back to
            // Physics.RaycastAll when it returns false, so they cope. ArenaNav.Drop
            // does NOT, which is exactly why TrackKind.Arena is refused here and by
            // the validator rather than half-working.
            built.floorCollider = null;
            built.tileRenderers = null;

            BuildSurfaces(d, built);
            ResolveSpawns(d, built);
            if (interactive) BuildGates(d, rootT, built);

            return built;
        }

        // ---- surfaces -------------------------------------------------------

        /// <summary>
        /// One SurfaceMap for the scene, bound to the descriptor. Placed on the
        /// build root rather than on a collider (a tile map puts it on the floor
        /// slab) because a scene has no single surface object to own it.
        /// </summary>
        private static void BuildSurfaces(SceneTrackDescriptor d, BuiltTrack built)
        {
            var map = built.root.AddComponent<SurfaceMap>();
            map.BindScene(d);
        }

        // ---- gates ----------------------------------------------------------

        /// <summary>
        /// Finish first, then checkpoints — checkpoints need the LapTimer the
        /// finish gate carries, the same ordering constraint TrackFactory has.
        /// </summary>
        private static void BuildGates(SceneTrackDescriptor d, Transform rootT, BuiltTrack built)
        {
            var gatesRoot = new GameObject("Gates");
            gatesRoot.transform.SetParent(rootT, false);

            var finish = d.Finish();
            if (finish != null)
            {
                var trig = TrackFactory.MakeGateTrigger(
                    finish.transform.position, finish.transform.rotation,
                    gatesRoot.transform, Mathf.Max(0.2f, finish.gateWidth));
                built.lapTimer = trig.AddComponent<LapTimer>();
                built.lapTimer.minLapTime = finish.minLapTime;
            }

            var cps = d.Checkpoints();
            for (int i = 0; i < cps.Count; i++)
            {
                var m = cps[i];
                var trig = TrackFactory.MakeGateTrigger(
                    m.transform.position, m.transform.rotation,
                    gatesRoot.transform, Mathf.Max(0.2f, m.gateWidth));
                var cp = trig.AddComponent<Checkpoint>();
                // The DENSE RANK in sorted order, not the authored value. Sparse or
                // duplicated orders still yield a walkable 0..n-1 sequence, matching
                // what TrackFactory does with d.OrderedCheckpoints().
                cp.index = i;
                cp.timer = built.lapTimer;
            }

            if (built.lapTimer != null) built.lapTimer.CheckpointCount = cps.Count;

            // Sector timing rides on the lap timer's own object because it needs the
            // LapCompleted event to roll a lap over. Attached only when the author
            // has actually built a sector set — a track without one pays nothing.
            if (built.lapTimer != null && d.sectors != null && d.sectors.sectors.Length > 0)
            {
                var st = built.lapTimer.gameObject.AddComponent<SectorTimer>();
                st.sectors = d.sectors;
            }
        }

        // ---- spawns ---------------------------------------------------------

        /// <summary>
        /// Every spawn marker, dropped onto whatever collider is beneath it.
        ///
        /// A tile map drops against the floor slab and ribbon colliders only, so a
        /// spawn can never land on a prop. A scene has no such distinction — the
        /// road IS a mesh — so this raycasts the whole scene and simply trusts the
        /// author, which is the same trust the scatter brush places in the geometry
        /// under the cursor.
        /// </summary>
        private static void ResolveSpawns(SceneTrackDescriptor d, BuiltTrack built)
        {
            var marks = d.Spawns();
            foreach (var m in marks)
            {
                built.spawns.Add(new SpawnPoint
                {
                    pos = Drop(m.transform.position),
                    rot = Quaternion.Euler(0f, m.transform.eulerAngles.y, 0f),
                    team = Mathf.Max(0, m.team),
                });
            }

            if (built.spawns.Count > 0)
            {
                built.spawnPos = built.spawns[0].pos;
                built.spawnRot = built.spawns[0].rot;
                return;
            }

            // No spawn marker: fall back to the finish line, then to the origin —
            // the same chain a tile map walks, so a half-authored scene still loads
            // and shows the author where they are instead of dropping a car into
            // the void.
            var finish = d.Finish();
            if (finish != null)
            {
                var rot = Quaternion.Euler(0f, finish.transform.eulerAngles.y, 0f);
                built.spawnPos = Drop(finish.transform.position - (rot * Vector3.forward) * 1.5f);
                built.spawnRot = rot;
                return;
            }

            built.spawnPos = new Vector3(0f, SpawnLift, 0f);
            built.spawnRot = Quaternion.identity;
        }

        /// <summary>
        /// Settle a point onto the surface below it. Starts 3 m up and reaches 60 m
        /// down, matching TrackFactory.DropToSurface's window, so a marker parked
        /// slightly inside geometry still finds the floor rather than falling
        /// through it. A miss keeps the authored height — an author who put a spawn
        /// on a bridge with no collider gets their spawn, not the origin.
        /// </summary>
        private static Vector3 Drop(Vector3 hint)
        {
            var ray = new Ray(hint + Vector3.up * 3f, Vector3.down);
            if (Physics.Raycast(ray, out var hit, 63f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * SpawnLift;
            return hint;
        }
    }
}
