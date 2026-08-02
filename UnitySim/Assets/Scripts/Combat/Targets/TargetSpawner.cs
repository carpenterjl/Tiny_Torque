using System.Collections.Generic;
using AIHWSim.Arcade;
using AIHWSim.Audio;
using AIHWSim.Core.Flight;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// Owns the target population: three circling drones, three spline-running
    /// trucks, and a cluster of static butts by the far pylons. One component,
    /// two lifecycles:
    ///
    /// <b>Building.</b> If this transform has no children at Awake, everything
    /// is built from code (the legacy runtime path). The editor scene builder
    /// calls <see cref="BuildAll"/> at EDIT time instead, handing it the same
    /// <see cref="SceneBuildContext"/> the airfield builders take — after which
    /// every drone circuit, every truck waypoint (<c>wp0…</c> handles) and
    /// every barrel is a scene object you can drag before pressing Play.
    ///
    /// <b>Choreographing.</b> Death and respawn are decided HERE, not in the
    /// movers: a drone tumbles for a moment and then bursts, a truck stops and
    /// smokes, a static simply goes — and each comes back after
    /// <see cref="respawnSeconds"/>, because a debug scene with all its targets
    /// dead is a shooting range with nothing left to prove.
    /// </summary>
    public sealed class TargetSpawner : MonoBehaviour
    {
        [Header("Population")]
        public int droneCount = 3;
        public int runnerCount = 3;
        public int staticCount = 6;

        [Header("Drones")]
        public float droneRadius = 180f;
        public float droneAltitude = 60f;
        public float droneSpeed = 18f;

        [Header("Lifecycle")]
        [Tooltip("Seconds from death to respawn. A dead range is a boring range.")]
        public float respawnSeconds = 20f;

        private readonly List<(float at, System.Action act)> _queue = new();

        private void Awake()
        {
            if (transform.childCount == 0) BuildAll(SceneBuildContext.Runtime);
            WireDeaths();
        }

        private void Update()
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
                if (Time.time >= _queue[i].at)
                {
                    var act = _queue[i].act;
                    _queue.RemoveAt(i);
                    act();
                }
        }

        private void Later(float seconds, System.Action act) =>
            _queue.Add((Time.time + seconds, act));

        // ---- building ----------------------------------------------------

        /// <summary>Build the whole population under this transform. Edit-time
        /// callable: every Destroy/paint goes through the context.</summary>
        public void BuildAll(SceneBuildContext ctx)
        {
            for (int i = 0; i < droneCount; i++) BuildDrone(i, ctx);
            for (int i = 0; i < runnerCount; i++) BuildRunner(i, ctx);
            BuildStatics(ctx);
        }

        private void BuildDrone(int i, SceneBuildContext ctx)
        {
            var root = new GameObject("Drone_" + i);
            root.transform.SetParent(transform, false);

            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;

            // One hand-sized box collider over the whole airframe: the drone is
            // a target, and quantising hits to its true silhouette would only
            // punish the shooter for the model being primitives.
            var col = root.AddComponent<BoxCollider>();
            col.size = new Vector3(3.2f, 0.7f, 2.4f);

            Part(root.transform, PrimitiveType.Cube, new Vector3(0f, 0f, 0f),
                 new Vector3(0.4f, 0.4f, 2.3f), new Color(0.95f, 0.45f, 0.10f), ctx);
            Part(root.transform, PrimitiveType.Cube, new Vector3(0f, 0.05f, 0.2f),
                 new Vector3(3.2f, 0.06f, 0.6f), new Color(0.95f, 0.55f, 0.15f), ctx);
            Part(root.transform, PrimitiveType.Cube, new Vector3(0f, 0.18f, -1.05f),
                 new Vector3(1.1f, 0.35f, 0.3f), new Color(0.90f, 0.40f, 0.10f), ctx);

            var wt = root.AddComponent<WeaponTarget>();
            wt.category = WeaponTarget.Category.Air;
            wt.maxHealth = 30f;
            wt.radius = 2.2f;

            var mover = root.AddComponent<DroneCircler>();
            mover.circuitRadius = droneRadius;
            mover.altitude = droneAltitude + i * 14f;   // stacked, not colliding
            mover.speed = droneSpeed;
            mover.phaseDeg = i * 360f / Mathf.Max(1, droneCount);
            mover.direction = i % 2 == 0 ? 1 : -1;
            mover.centre = new Vector3(0f, 0f, 0f);
        }

        private void BuildRunner(int i, SceneBuildContext ctx)
        {
            // The loop group holds the truck AND its waypoint handles, so the
            // handles are siblings the runner can find and a hand can drag.
            var loop = new GameObject("RunnerLoop_" + i);
            loop.transform.SetParent(transform, false);

            // A lumpy oval beside the strip, offset per runner, with an S-kink
            // on each side — the weave is authored INTO the path, and the speed
            // jitter rides on top of it.
            float cx = 90f + i * 55f;
            float cz = -40f + i * 30f;
            Vector3[] wps =
            {
                new Vector3(cx - 35f, 0.6f, cz - 60f),
                new Vector3(cx + 10f, 0.6f, cz - 45f),
                new Vector3(cx - 12f, 0.6f, cz - 15f),
                new Vector3(cx + 30f, 0.6f, cz + 10f),
                new Vector3(cx + 5f, 0.6f, cz + 45f),
                new Vector3(cx + 38f, 0.6f, cz + 70f),
                new Vector3(cx - 25f, 0.6f, cz + 55f),
                new Vector3(cx - 45f, 0.6f, cz + 5f),
            };
            for (int w = 0; w < wps.Length; w++)
            {
                var h = new GameObject("wp" + w);
                h.transform.SetParent(loop.transform, false);
                h.transform.position = wps[w];
            }

            var truck = new GameObject("Runner_" + i);
            truck.transform.SetParent(loop.transform, false);
            truck.transform.position = wps[0];

            var rb = truck.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            var col = truck.AddComponent<BoxCollider>();
            col.size = new Vector3(2.2f, 2.0f, 5.0f);
            col.center = new Vector3(0f, 1.0f, 0f);

            Part(truck.transform, PrimitiveType.Cube, new Vector3(0f, 0.9f, -0.4f),
                 new Vector3(2.1f, 1.5f, 3.6f), new Color(0.35f, 0.42f, 0.50f), ctx);
            Part(truck.transform, PrimitiveType.Cube, new Vector3(0f, 1.05f, 1.7f),
                 new Vector3(1.9f, 1.2f, 1.4f), new Color(0.30f, 0.36f, 0.44f), ctx);

            var wt = truck.AddComponent<WeaponTarget>();
            wt.category = WeaponTarget.Category.Ground;
            wt.maxHealth = 50f;
            wt.radius = 2.6f;

            var mover = truck.AddComponent<SplineRunner>();
            mover.baseSpeed = 12f;
            mover.jitterPhase = i * 2.3f;
        }

        private void BuildStatics(SceneBuildContext ctx)
        {
            var group = new GameObject("Statics");
            group.transform.SetParent(transform, false);

            for (int i = 0; i < staticCount; i++)
            {
                bool barrel = i % 2 == 0;
                var s = GameObject.CreatePrimitive(
                    barrel ? PrimitiveType.Cylinder : PrimitiveType.Cube);
                s.name = barrel ? "Barrel_" + i : "Block_" + i;
                s.transform.SetParent(group.transform, false);
                // Scattered around the far pylons — the SAS target's
                // neighbourhood, so Target mode flies you at the range.
                s.transform.position = new Vector3(
                    -65f + (i % 3) * 12f + (i * 7 % 5), 0.9f,
                    92f + (i / 3) * 10f + (i * 11 % 7));
                s.transform.localScale = barrel
                    ? new Vector3(1.2f, 0.9f, 1.2f)
                    : new Vector3(1.6f, 1.6f, 1.6f);
                ctx.Paint(s.GetComponent<Renderer>(), barrel
                    ? new Color(0.85f, 0.20f, 0.15f)
                    : new Color(0.75f, 0.72f, 0.60f));

                var wt = s.AddComponent<WeaponTarget>();
                wt.category = WeaponTarget.Category.Static;
                wt.maxHealth = 20f;
                wt.radius = 1.4f;
            }
        }

        /// <summary>A collider-less painted primitive under a parent.</summary>
        private static void Part(Transform parent, PrimitiveType kind, Vector3 pos,
                                 Vector3 scale, Color c, SceneBuildContext ctx)
        {
            var g = GameObject.CreatePrimitive(kind);
            g.name = "part";
            ctx.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos;
            g.transform.localScale = scale;
            ctx.Paint(g.GetComponent<Renderer>(), c);
        }

        // ---- death and respawn -------------------------------------------

        /// <summary>Subscribe every target under this spawner, whether the
        /// children were built by code just now or authored into the scene.</summary>
        private void WireDeaths()
        {
            foreach (var wt in GetComponentsInChildren<WeaponTarget>(true))
            {
                var t = wt;   // capture per target
                t.Destroyed += OnTargetDestroyed;
            }
        }

        private void OnTargetDestroyed(WeaponTarget t, Vector3 at)
        {
            var sfx = SfxPlayer.Ensure();
            sfx?.PlayAt(ProceduralAudio.Explosion, at);

            var drone = t.GetComponent<DroneCircler>();
            if (drone != null)
            {
                ArcadeBurst.Spawn(at, 4f, new Color(1f, 0.6f, 0.2f), 0.7f);
                drone.BeginTumble();
                Later(2f, () =>
                {
                    ArcadeBurst.Spawn(t.transform.position, 6f,
                                      new Color(1f, 0.5f, 0.15f), 0.9f);
                    t.gameObject.SetActive(false);
                    Later(respawnSeconds, () =>
                    {
                        t.gameObject.SetActive(true);
                        drone.ResetToCircuit();
                    });
                });
                return;
            }

            var runner = t.GetComponent<SplineRunner>();
            if (runner != null)
            {
                ArcadeBurst.Spawn(at, 5f, new Color(1f, 0.55f, 0.2f), 0.8f);
                runner.Halt();
                // A wreck, not a disappearance: it sits there burning a while.
                Later(0.6f, () => ArcadeBurst.Spawn(
                    t.transform.position + Vector3.up * 1.5f, 2.5f,
                    new Color(0.25f, 0.22f, 0.20f), 1.2f));
                Later(respawnSeconds, () =>
                {
                    runner.ResetToLoop();
                });
                return;
            }

            // Static: gone, then back.
            ArcadeBurst.Spawn(at, 3.5f, new Color(1f, 0.6f, 0.25f), 0.7f);
            t.gameObject.SetActive(false);
            Later(respawnSeconds * 1.5f, () =>
            {
                t.ResetHealth();
                t.gameObject.SetActive(true);
            });
        }
    }
}
