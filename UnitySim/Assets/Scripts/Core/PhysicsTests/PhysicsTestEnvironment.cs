using AIHWSim.Telemetry;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// The world every physics measurement happens in: a long flat straight, an
    /// optional measured slope, lighting, and a chase camera carrying the graph
    /// overlay. Extracted from <see cref="PhysicsDebugBootstrap"/> so the
    /// free-drive scene and the ten test scenes cannot disagree about the ground
    /// they measure against — the same reason the vehicle build lives in
    /// <see cref="DebugVehicleRig"/> rather than in each caller.
    ///
    /// <b>The ground is frictionless, and that is the whole point.</b> All
    /// horizontal force must come from the vehicle's own traction model, so
    /// contact provides normal support only. Friction lives in the tyre, not in
    /// the surface: a ground PhysicsMaterial with grip would add a second,
    /// unmodelled friction path on top of the one being measured, and every
    /// number downstream would be that sum rather than the tyre.
    ///
    /// This is also why a physics test is a scene of its own rather than a mode
    /// of the track: anything measured on a real surface is a driving
    /// impression, not a measurement.
    /// </summary>
    public static class PhysicsTestEnvironment
    {
        /// <summary>
        /// What a given test needs from the world. The defaults are
        /// <see cref="PhysicsDebugBootstrap"/>'s originals, so a test that wants
        /// the ordinary straight states nothing.
        /// </summary>
        public struct EnvSpec
        {
            /// <summary>Straight length (m). P1's coastdown from 32 to 22 m/s
            /// covers ~1150 m, which is what sets the 2.4 km default.</summary>
            public float straightLength;
            /// <summary>Straight width (m). The cornering tests widen this: a
            /// skidpad at 0.9 g and 15 m/s needs ~25 m of radius, and the car
            /// must not run out of ground mid-circle.</summary>
            public float straightWidth;
            /// <summary>Build the park-brake slope. P6b only — it is 200 m of
            /// extra collider that no other test touches.</summary>
            public bool buildSlope;
            /// <summary>Grade of that slope as a fraction (0.10 = 10 %).</summary>
            public float slopeGrade;

            public static EnvSpec Default() => new EnvSpec
            {
                straightLength = 2400f,
                straightWidth = 240f,
                buildSlope = false,
                slopeGrade = 0.10f,
            };

            /// <summary>Square-ish pad for the steady-state cornering tests
            /// (P4, P5, P7), which drive in circles rather than straight lines
            /// and would otherwise leave a 240 m-wide strip sideways.</summary>
            public static EnvSpec Pad()
            {
                var s = Default();
                s.straightLength = 600f;
                s.straightWidth = 600f;
                return s;
            }

            /// <summary>Default straight plus the 10 % slope, for the park hold.</summary>
            public static EnvSpec WithSlope()
            {
                var s = Default();
                s.buildSlope = true;
                return s;
            }
        }

        /// <summary>
        /// Build lighting, ground, optional slope, and the camera + graph. The
        /// camera is returned without a follow target: callers attach
        /// <c>ChaseCamera</c> themselves, because the target does not exist until
        /// the car is built and the car needs this camera's graph to exist first.
        /// </summary>
        public static (Camera cam, GraphOverlay graph) Build(EnvSpec spec)
        {
            var frictionless = Frictionless();
            BuildLighting();
            BuildGround(spec, frictionless);
            if (spec.buildSlope) BuildSlope(spec, frictionless);
            return BuildCameraAndGraph(spec);
        }

        /// <summary>Normal support only — see the class comment. Identical to the
        /// material <c>SimBootstrap</c> builds, for the same reason.</summary>
        public static PhysicsMaterial Frictionless() => new PhysicsMaterial("Frictionless")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine = PhysicsMaterialCombine.Minimum,
        };

        private static void BuildLighting()
        {
            Boot.SceneRig.BuildLighting(1.1f, new Vector3(50f, -30f, 0f));
        }

        /// <summary>
        /// A scaled Cube, not a Plane.
        ///
        /// A Plane primitive is a 10x10 quad mesh behind a MeshCollider; stretched
        /// to 2.4 km that is a raycast into a mesh with 240 m quads, per wheel,
        /// per step, at 400 Hz. A Box is an exact analytic collider, and — the
        /// part that actually matters — it accepts the per-collider contactOffset
        /// the Tiguan needs to pair with its own 2 cm chassis offset.
        ///
        /// Top face lands exactly at y = 0 by construction.
        /// </summary>
        private static void BuildGround(EnvSpec spec, PhysicsMaterial mat)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = "Ground";
            const float thick = 2f;
            g.transform.localScale = new Vector3(spec.straightWidth, thick, spec.straightLength);
            g.transform.position = new Vector3(
                0f, -thick * 0.5f, spec.straightLength * 0.5f - 200f);
            var col = g.GetComponent<Collider>();
            col.material = mat;
            col.contactOffset = 0.02f;   // matched to the Tiguan's chassis box
            g.GetComponent<Renderer>().material.color = new Color(0.20f, 0.22f, 0.25f);
        }

        /// <summary>A measured grade for the park-brake hold test (P6b), which is
        /// the only check on whether PhysX's sticky-tyre constraint can actually
        /// hold 1500 kg. Frictionless like everything else — the holding force
        /// has to come from the tyre model, or the test proves nothing.</summary>
        private static void BuildSlope(EnvSpec spec, PhysicsMaterial mat)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
            s.name = "Slope";
            const float thick = 2f;
            float len = 200f, deg = Mathf.Atan(spec.slopeGrade) * Mathf.Rad2Deg;
            s.transform.localScale = new Vector3(40f, thick, len);
            s.transform.rotation = Quaternion.Euler(-deg, 0f, 0f);
            // Lower end meets y = 0 at z = -150; the surface climbs from there.
            s.transform.position = new Vector3(
                0f, len * 0.5f * spec.slopeGrade - thick * 0.5f, -150f - len * 0.5f);
            var col = s.GetComponent<Collider>();
            col.material = mat;
            col.contactOffset = 0.02f;
            s.GetComponent<Renderer>().material.color = new Color(0.26f, 0.24f, 0.22f);
        }

        private static (Camera, GraphOverlay) BuildCameraAndGraph(EnvSpec spec)
        {
            Camera cam = Boot.SceneRig.CameraOrCreate();
            cam.transform.position = new Vector3(0f, 4f, -10f);
            cam.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
            // Far enough to see the end of whichever surface was built.
            cam.farClipPlane = Mathf.Max(3000f, spec.straightLength * 1.25f);
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            cam.clearFlags = CameraClearFlags.SolidColor;

            var graph = cam.gameObject.GetComponent<GraphOverlay>();
            if (graph == null) graph = cam.gameObject.AddComponent<GraphOverlay>();
            return (cam, graph);
        }
    }
}
