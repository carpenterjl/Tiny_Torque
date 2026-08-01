using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Stands an <see cref="AircraftSpec"/> up as a flyable rig, the way
    /// <see cref="DebugVehicleRig"/> does for a car — one place that knows the
    /// rules, so the free-flight scene and the flight tests cannot drift apart.
    ///
    /// <b>The build order is load-bearing and it is the same order as the car's:</b>
    /// <see cref="BuildPlane"/> → build camera → <see cref="AttachRunner"/>. The
    /// camera needs the aircraft's transform to follow; the runner wants the
    /// camera's <see cref="GraphOverlay"/> at the moment it is added; and
    /// <see cref="FlightTelemetry"/> must register its channels inside
    /// AttachRunner's Awake, because <c>CsvLogger.Begin</c> snapshots the column
    /// list once inside <c>SimulationRunner.Start</c>.
    ///
    /// <b>The aircraft is built from primitives</b> — boxes for the surfaces,
    /// spheres for the wheels — and the geometry comes from the same
    /// <see cref="LiftingSurface"/> records the aerodynamics reads. There is no
    /// separate visual description that could disagree with the physics: if the
    /// wing you can see has 5° of dihedral, it is because the wing the model flies
    /// has 5° of dihedral.
    /// </summary>
    public static class DebugPlaneRig
    {
        public struct Rig
        {
            public PlaneVehicle plane;
            public PlaneInput input;
            public GameObject root;
            public SimulationRunner runner;
        }

        private const float SurfaceThickness = 0.010f;

        public static Rig BuildPlane(AircraftSpec spec, Vector3 pos, Quaternion rot)
        {
            var root = new GameObject(spec.name);
            // Deactivate while building so PlaneVehicle.Awake cannot run before the
            // spec is attached — the same trick VehicleFactory uses on the car, and
            // for the same reason: a first FixedUpdate taken with Unity's default
            // box inertia would give a transient nothing later could explain.
            root.SetActive(false);
            root.transform.SetPositionAndRotation(pos, rot);

            var body = root.AddComponent<Rigidbody>();
            BuildFuselage(spec, root.transform);
            foreach (LiftingSurface s in spec.surfaces) BuildSurface(s, root.transform);
            BuildGear(spec, root.transform);
            BuildProp(spec, root.transform);

            var plane = root.AddComponent<PlaneVehicle>();
            plane.spec = spec;

            var input = root.AddComponent<PlaneInput>();
            input.plane = plane;

            root.SetActive(true);
            // Configure AFTER activation so Awake has cached the rigidbody, then
            // apply mass, CG and inertia before anything steps.
            plane.Configure(spec);
            plane.SetSpawn(pos, rot);

            return new Rig { plane = plane, input = input, root = root };
        }

        /// <summary>
        /// The simulation loop and the flight channels. Mirrors
        /// <see cref="DebugVehicleRig.AttachRunner"/> including the two settings
        /// that are not defaults:
        /// <list type="bullet">
        /// <item><c>allowModeToggle = false</c> — M would otherwise flip to
        /// Autonomous, and with no controller DLL loaded the actuators go to zero
        /// and the aircraft falls out of the sky with no message at all.</item>
        /// <item><c>sensorRig = null</c> — an aeroplane has no
        /// <c>SensorComponent</c>s; <c>SimulationRunner</c> null-checks it.</item>
        /// </list>
        /// </summary>
        public static void AttachRunner(ref Rig rig, GraphOverlay graph,
                                        int physicsRateHz, int controlRateHz,
                                        bool logCsv)
        {
            var runnerGo = new GameObject("SimulationRunner");
            var runner = runnerGo.AddComponent<SimulationRunner>();
            runner.physicsRateHz = physicsRateHz;
            runner.controlRateHz = controlRateHz;
            runner.logCsv = logCsv;
            runner.vehicleBehaviour = rig.plane;
            runner.inputBehaviour = rig.input;
            runner.sensorRig = null;
            runner.graph = graph;
            runner.startInManual = true;
            runner.loadControllerDll = false;
            runner.allowModeToggle = false;

            var tele = runnerGo.AddComponent<FlightTelemetry>();
            tele.Bind(runner, rig.plane);

            rig.runner = runner;
        }

        /// <summary>Lowest point of the gear below the body origin — what the
        /// caller needs to put the aircraft on its wheels rather than in the
        /// tarmac.</summary>
        public static float GearHeight(AircraftSpec spec)
        {
            float lowest = 0f;
            foreach (Vector3 g in spec.gearLocal) lowest = Mathf.Min(lowest, g.y);
            return -lowest + spec.gearRadius;
        }

        // ---- primitives --------------------------------------------------

        private static void BuildFuselage(AircraftSpec spec, Transform parent)
        {
            var f = GameObject.CreatePrimitive(PrimitiveType.Cube);
            f.name = "Fuselage";
            f.transform.SetParent(parent, false);
            f.transform.localPosition = spec.fuselageCentre;
            f.transform.localScale = spec.fuselageSize;
            // The fuselage IS the crash body. Lifting surfaces are visual only —
            // a wing-strike model is not in scope and is declared, not implied.
            f.GetComponent<Renderer>().material.color = new Color(0.88f, 0.88f, 0.90f);
        }

        private static void BuildSurface(LiftingSurface s, Transform parent)
        {
            int sides = s.mirrored ? 2 : 1;
            for (int i = 0; i < sides; i++)
            {
                int side = i == 0 ? +1 : -1;
                Vector3 chordDir = Vector3.forward;
                Vector3 normal, spanDir;
                if (s.vertical)
                {
                    normal = Vector3.right;
                    spanDir = Vector3.up;
                }
                else
                {
                    normal = Quaternion.AngleAxis(side * s.dihedralDeg, chordDir) * Vector3.up;
                    spanDir = side * Vector3.Cross(normal, chordDir);
                }

                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                g.name = s.name + (s.mirrored ? (side > 0 ? "_R" : "_L") : "");
                Object.Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(parent, false);

                // LookRotation(chordDir, normal) puts +Z along the chord and +Y
                // along the surface normal, which leaves +X along the span — so a
                // single scale works for a wing, a tailplane and a fin alike.
                g.transform.localRotation = Quaternion.LookRotation(chordDir, normal);
                g.transform.localPosition = s.rootQuarterChord + spanDir * (s.semiSpan * 0.5f);

                float chord = 0.5f * (s.rootChord + s.tipChord);
                g.transform.localScale = new Vector3(s.semiSpan, SurfaceThickness, chord);
                g.GetComponent<Renderer>().material.color = s.vertical
                    ? new Color(0.85f, 0.30f, 0.20f)
                    : new Color(0.92f, 0.72f, 0.20f);
            }
        }

        private static void BuildGear(AircraftSpec spec, Transform parent)
        {
            foreach (Vector3 p in spec.gearLocal)
            {
                var w = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                w.name = "Gear";
                w.transform.SetParent(parent, false);
                w.transform.localPosition = p;
                w.transform.localScale = Vector3.one * (spec.gearRadius * 2f);
                // Sphere colliders roll without a tyre model. Ground handling is a
                // DECLARED simplification: no brakes, no steerable nosewheel, no
                // slip. Scripted tests hand-launch precisely so that none of them
                // depends on it.
                w.GetComponent<Renderer>().material.color = new Color(0.15f, 0.15f, 0.16f);
            }
        }

        private static void BuildProp(AircraftSpec spec, Transform parent)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            p.name = "Propeller";
            Object.Destroy(p.GetComponent<Collider>());
            p.transform.SetParent(parent, false);
            p.transform.localPosition = spec.propPosLocal;
            // Cylinder axis is local Y; lay it along the thrust line.
            p.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            float d = spec.propeller.diameterM;
            p.transform.localScale = new Vector3(d, 0.004f, d);
            p.GetComponent<Renderer>().material.color = new Color(0.22f, 0.22f, 0.24f);
        }
    }
}
