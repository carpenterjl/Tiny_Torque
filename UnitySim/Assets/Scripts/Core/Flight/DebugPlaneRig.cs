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

        /// <summary>The Play-mode entry every scripted test calls — a verbatim
        /// forwarder, for the same reason as
        /// <see cref="FlightTestEnvironment.Build(FlightTestEnvironment.EnvSpec)"/>:
        /// with the default context every substitution below reduces to the old
        /// statement.</summary>
        public static Rig BuildPlane(AircraftSpec spec, Vector3 pos, Quaternion rot) =>
            BuildPlane(spec, pos, rot, SceneBuildContext.Runtime);

        public static Rig BuildPlane(AircraftSpec spec, Vector3 pos, Quaternion rot,
                                     SceneBuildContext ctx)
        {
            var root = new GameObject(spec.name);
            // Deactivate while building so PlaneVehicle.Awake cannot run before the
            // spec is attached — the same trick VehicleFactory uses on the car, and
            // for the same reason: a first FixedUpdate taken with Unity's default
            // box inertia would give a transient nothing later could explain.
            root.SetActive(false);
            root.transform.SetPositionAndRotation(pos, rot);

            var body = root.AddComponent<Rigidbody>();
            BuildFuselage(spec, root.transform, ctx);
            foreach (LiftingSurface s in spec.surfaces) BuildSurface(s, root.transform, ctx);
            BuildGear(spec, root.transform, ctx);
            BuildProp(spec, root.transform, ctx);

            var plane = root.AddComponent<PlaneVehicle>();
            plane.spec = spec;

            // The nozzle visuals were built before the vehicle existed; hand
            // them the component they follow. Empty for a propeller aircraft.
            foreach (var nv in root.GetComponentsInChildren<JetNozzleVisual>(true))
                nv.plane = plane;

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
            ConfigureRunner(runner, rig.plane, rig.input, graph,
                            physicsRateHz, controlRateHz, logCsv);

            var tele = runnerGo.AddComponent<FlightTelemetry>();
            tele.Bind(runner, rig.plane);

            rig.runner = runner;
        }

        /// <summary>The field assignments alone, shared with the editor scene
        /// builder so an authored runner and a runtime one cannot drift — any
        /// future setting is added HERE or it exists in only one of the two
        /// worlds. Binding telemetry stays with the caller: channel registration
        /// is a runtime act.</summary>
        public static void ConfigureRunner(SimulationRunner runner,
                                           PlaneVehicle plane, PlaneInput input,
                                           GraphOverlay graph,
                                           int physicsRateHz, int controlRateHz,
                                           bool logCsv)
        {
            runner.physicsRateHz = physicsRateHz;
            runner.controlRateHz = controlRateHz;
            runner.logCsv = logCsv;
            runner.vehicleBehaviour = plane;
            runner.inputBehaviour = input;
            runner.sensorRig = null;
            runner.graph = graph;
            runner.startInManual = true;
            runner.loadControllerDll = false;
            runner.allowModeToggle = false;
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

        private static void BuildFuselage(AircraftSpec spec, Transform parent,
                                          SceneBuildContext ctx)
        {
            var f = GameObject.CreatePrimitive(PrimitiveType.Cube);
            f.name = "Fuselage";
            f.transform.SetParent(parent, false);
            f.transform.localPosition = spec.fuselageCentre;
            f.transform.localScale = spec.fuselageSize;
            // The fuselage IS the crash body. Lifting surfaces are visual only —
            // a wing-strike model is not in scope and is declared, not implied.
            ctx.Paint(f.GetComponent<Renderer>(), new Color(0.88f, 0.88f, 0.90f));
        }

        private static void BuildSurface(LiftingSurface s, Transform parent,
                                         SceneBuildContext ctx)
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
                ctx.Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(parent, false);

                // LookRotation(chordDir, normal) puts +Z along the chord and +Y
                // along the surface normal, which leaves +X along the span — so a
                // single scale works for a wing, a tailplane and a fin alike.
                g.transform.localRotation = Quaternion.LookRotation(chordDir, normal);
                g.transform.localPosition = s.rootQuarterChord + spanDir * (s.semiSpan * 0.5f);

                float chord = 0.5f * (s.rootChord + s.tipChord);
                g.transform.localScale = new Vector3(s.semiSpan, SurfaceThickness, chord);
                ctx.Paint(g.GetComponent<Renderer>(), s.vertical
                    ? new Color(0.85f, 0.30f, 0.20f)
                    : new Color(0.92f, 0.72f, 0.20f));
            }
        }

        private static void BuildGear(AircraftSpec spec, Transform parent,
                                      SceneBuildContext ctx)
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
                ctx.Paint(w.GetComponent<Renderer>(), new Color(0.15f, 0.15f, 0.16f));
            }
        }

        private static void BuildProp(AircraftSpec spec, Transform parent,
                                      SceneBuildContext ctx)
        {
            // A jet has no disc to draw. It gets nozzles instead — and they are
            // wired to the LIVE nozzle state, so the visual cannot disagree with
            // the physics any more than the wings can.
            if (spec.IsJet) { BuildJetVisual(spec, parent, ctx); return; }

            var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            p.name = "Propeller";
            ctx.Destroy(p.GetComponent<Collider>());
            p.transform.SetParent(parent, false);
            p.transform.localPosition = spec.propPosLocal;
            // Cylinder axis is local Y; lay it along the thrust line.
            p.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            float d = spec.propeller.diameterM;
            p.transform.localScale = new Vector3(d, 0.004f, d);
            ctx.Paint(p.GetComponent<Renderer>(), new Color(0.22f, 0.22f, 0.24f));
        }

        /// <summary>Intake cheeks and the two swivelling nozzle pairs, at the
        /// authored nozzle stations. The nozzle cylinders carry
        /// <see cref="JetNozzleVisual"/>, which turns them with the live
        /// <see cref="PlaneVehicle.NozzleDeg"/> once the vehicle exists — the
        /// component's <c>plane</c> is wired by <see cref="BuildPlane"/>'s
        /// caller-visible ordering (the vehicle is added right after the
        /// visuals), via the wire-up at the end of this method's parent call.
        /// At edit time it simply sits at 0° until Play.</summary>
        private static void BuildJetVisual(AircraftSpec spec, Transform parent,
                                           SceneBuildContext ctx)
        {
            // Intake cheeks either side of the fuselage, forward of the wing.
            for (int side = -1; side <= 1; side += 2)
            {
                var intake = GameObject.CreatePrimitive(PrimitiveType.Cube);
                intake.name = side > 0 ? "Intake_R" : "Intake_L";
                ctx.Destroy(intake.GetComponent<Collider>());
                intake.transform.SetParent(parent, false);
                intake.transform.localPosition = new Vector3(
                    side * (spec.fuselageSize.x * 0.5f + 0.18f), 0.05f, 1.10f);
                intake.transform.localScale = new Vector3(0.36f, 0.55f, 1.30f);
                ctx.Paint(intake.GetComponent<Renderer>(), new Color(0.30f, 0.31f, 0.34f));
            }

            // One nozzle pair per authored station, hung outboard of the
            // fuselage like the Pegasus'.
            Vector3[] stations = { spec.jet.nozzleForeLocal, spec.jet.nozzleAftLocal };
            for (int i = 0; i < stations.Length; i++)
                for (int side = -1; side <= 1; side += 2)
                {
                    var n = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    n.name = (i == 0 ? "NozzleFore" : "NozzleAft")
                             + (side > 0 ? "_R" : "_L");
                    ctx.Destroy(n.GetComponent<Collider>());
                    n.transform.SetParent(parent, false);
                    Vector3 p = stations[i];
                    p.x = side * (spec.fuselageSize.x * 0.5f + 0.10f);
                    n.transform.localPosition = p;
                    n.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    n.transform.localScale = new Vector3(0.26f, 0.22f, 0.26f);
                    ctx.Paint(n.GetComponent<Renderer>(), new Color(0.20f, 0.20f, 0.22f));
                    n.AddComponent<JetNozzleVisual>();
                }
        }
    }
}
