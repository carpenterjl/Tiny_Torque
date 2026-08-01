using AIHWSim.Core.PhysicsTests;
using AIHWSim.Telemetry;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// The world an aircraft flies in: a club field with a runway you can read
    /// height and speed against, markers that tell you which way you are pointing,
    /// and a horizon.
    ///
    /// <b>A sibling of <see cref="PhysicsTestEnvironment"/>, not an extension of
    /// it.</b> That file exists so the free-drive scene and the ten car tests
    /// "cannot disagree about the ground they measure against" — and an aircraft
    /// does not measure against the ground at all. It measures against air; the
    /// ground is scenery plus the thing you eventually land on. Forcing both into
    /// one spec would be exactly the drift the original extraction prevented, and
    /// it would put a diff on the file ten validated results depend on.
    ///
    /// What IS shared is the one decision worth sharing:
    /// <see cref="PhysicsTestEnvironment.Frictionless"/>. The ground carries no
    /// friction here for the same reason it carries none there — a measured
    /// landing rollout must come from the aircraft's own model, or the number is a
    /// report on a PhysicsMaterial.
    ///
    /// <b>Why neither existing spec would do.</b> The car's default is a
    /// 2400 × 240 m strip and its pad is 600 × 600; an aircraft turning at 45° of
    /// bank and 18 m/s needs ~33 m of radius and will happily leave either. Worse,
    /// both clear to near-black solid colour, and <b>you cannot fly attitude
    /// against nothing</b> — without a horizon there is no way to tell a shallow
    /// climb from a shallow dive, which is most of what flying is.
    /// </summary>
    public static class FlightTestEnvironment
    {
        public struct EnvSpec
        {
            /// <summary>Half-width of the ground box (m).</summary>
            public float groundHalfExtent;
            public float runwayLength;
            public float runwayWidth;
            /// <summary>Centreline stripe spacing (m). The only thing that gives a
            /// pilot a speed and height reference by eye.</summary>
            public float stripeSpacing;
            /// <summary>Lateral offset of the box-marker pylons (m).</summary>
            public float pylonOffset;
            /// <summary>Radius and count of the distant orientation poles.</summary>
            public float horizonRingRadius;
            public int horizonRingPoles;
            public bool useSkybox;
            public float farClip;

            /// <summary>A club field: 6 km of ground so a departure has room to
            /// end, and a 200 m strip, which is a generous version of the ~100 m
            /// of mown grass a real club flies off.</summary>
            public static EnvSpec Airfield() => new EnvSpec
            {
                groundHalfExtent = 3000f,
                runwayLength = 200f,
                runwayWidth = 15f,
                stripeSpacing = 10f,
                pylonOffset = 50f,
                horizonRingRadius = 300f,
                horizonRingPoles = 12,
                useSkybox = true,
                farClip = 8000f,
            };

            /// <summary>Bare ground and sky — no runway, no markers. What a test
            /// that starts in the air and never lands actually needs, and it keeps
            /// a few hundred colliders out of a measurement scene.</summary>
            public static EnvSpec Bare()
            {
                var s = Airfield();
                s.runwayLength = 0f;
                s.pylonOffset = 0f;
                s.horizonRingPoles = 0;
                return s;
            }
        }

        public static (Camera cam, GraphOverlay graph) Build(EnvSpec spec)
        {
            PhysicsMaterial frictionless = PhysicsTestEnvironment.Frictionless();
            BuildLighting();
            BuildGround(spec, frictionless);
            if (spec.runwayLength > 0f) BuildRunway(spec, frictionless);
            if (spec.pylonOffset > 0f) BuildPylons(spec);
            if (spec.horizonRingPoles > 0) BuildHorizonRing(spec);
            return BuildCameraAndGraph(spec);
        }

        /// <summary>Where the aircraft starts a hand launch: a little way down the
        /// strip, at head height, wings level, pointing along it.</summary>
        public static (Vector3 pos, Quaternion rot) LaunchPose(float altitude = 30f) =>
            (new Vector3(0f, altitude, -60f), Quaternion.identity);

        /// <summary>Where it sits on its wheels at the threshold. The caller drops
        /// it the last few millimetres by raycast rather than guessing gear
        /// compression.</summary>
        public static (Vector3 pos, Quaternion rot) RunwayPose(float gearHeight) =>
            (new Vector3(0f, gearHeight, -80f), Quaternion.identity);

        // ---- pieces ------------------------------------------------------

        private static void BuildLighting()
        {
            if (Object.FindFirstObjectByType<Light>() != null) return;
            var go = new GameObject("Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            go.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            RenderSettings.ambientIntensity = 1f;
        }

        /// <summary>A scaled Cube for the same reason the car's ground is one: an
        /// analytic box collider rather than a stretched quad mesh. Top face at
        /// y = 0 by construction.</summary>
        private static void BuildGround(EnvSpec spec, PhysicsMaterial mat)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = "Ground";
            const float thick = 4f;
            float w = spec.groundHalfExtent * 2f;
            g.transform.localScale = new Vector3(w, thick, w);
            g.transform.position = new Vector3(0f, -thick * 0.5f, 0f);
            var col = g.GetComponent<Collider>();
            col.material = mat;
            g.GetComponent<Renderer>().material.color = new Color(0.26f, 0.34f, 0.22f);
        }

        private static void BuildRunway(EnvSpec spec, PhysicsMaterial mat)
        {
            var r = GameObject.CreatePrimitive(PrimitiveType.Cube);
            r.name = "Runway";
            const float thick = 0.10f;
            r.transform.localScale = new Vector3(spec.runwayWidth, thick, spec.runwayLength);
            // Sits ON the ground, so its top is the surface the wheels meet.
            r.transform.position = new Vector3(0f, thick * 0.5f, 0f);
            var col = r.GetComponent<Collider>();
            col.material = mat;
            r.GetComponent<Renderer>().material.color = new Color(0.20f, 0.20f, 0.21f);

            // Centreline stripes. These are the instrument: without something of
            // known spacing passing underneath, a model at 100 m has no readable
            // speed and no readable height.
            var parent = new GameObject("RunwayStripes").transform;
            int n = Mathf.FloorToInt(spec.runwayLength / spec.stripeSpacing);
            for (int i = 0; i <= n; i++)
            {
                float z = -spec.runwayLength * 0.5f + i * spec.stripeSpacing;
                var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
                s.name = "stripe";
                Object.Destroy(s.GetComponent<Collider>());
                s.transform.SetParent(parent, false);
                s.transform.localScale = new Vector3(0.6f, 0.02f, 4f);
                s.transform.position = new Vector3(0f, thick + 0.01f, z);
                s.GetComponent<Renderer>().material.color = new Color(0.85f, 0.85f, 0.82f);
            }
        }

        /// <summary>The RC "box": two pairs of poles marking the ends of the strip,
        /// which is how a pilot judges where a pass starts and finishes.</summary>
        private static void BuildPylons(EnvSpec spec)
        {
            var parent = new GameObject("Pylons").transform;
            float half = spec.runwayLength * 0.5f;
            Color[] tint = {
                new Color(0.90f, 0.25f, 0.15f),
                new Color(0.95f, 0.80f, 0.15f),
            };
            int k = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    p.name = "pylon";
                    Object.Destroy(p.GetComponent<Collider>());
                    p.transform.SetParent(parent, false);
                    p.transform.localScale = new Vector3(0.35f, 2.5f, 0.35f);
                    p.transform.position = new Vector3(sx * spec.pylonOffset, 2.5f, sz * half);
                    p.GetComponent<Renderer>().material.color = tint[k++ % tint.Length];
                }
        }

        /// <summary>
        /// A ring of tall poles at a few hundred metres. Not decoration: at any
        /// distance a model aeroplane is a silhouette with no depth cue, and a ring
        /// of known objects is what turns "somewhere over there" into a heading and
        /// a range. It is also the only ground reference that stays visible when
        /// the aircraft is above the horizon.
        /// </summary>
        private static void BuildHorizonRing(EnvSpec spec)
        {
            var parent = new GameObject("HorizonRing").transform;
            for (int i = 0; i < spec.horizonRingPoles; i++)
            {
                float a = i * Mathf.PI * 2f / spec.horizonRingPoles;
                var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
                p.name = "pole";
                Object.Destroy(p.GetComponent<Collider>());
                p.transform.SetParent(parent, false);
                p.transform.localScale = new Vector3(1.2f, 14f, 1.2f);
                p.transform.position = new Vector3(
                    Mathf.Sin(a) * spec.horizonRingRadius, 7f,
                    Mathf.Cos(a) * spec.horizonRingRadius);
                // Cardinal poles are lighter, so heading is readable at a glance.
                bool cardinal = i % (Mathf.Max(1, spec.horizonRingPoles / 4)) == 0;
                p.GetComponent<Renderer>().material.color = cardinal
                    ? new Color(0.85f, 0.85f, 0.88f)
                    : new Color(0.45f, 0.47f, 0.50f);
            }
        }

        private static (Camera, GraphOverlay) BuildCameraAndGraph(EnvSpec spec)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            // Far clip must exceed the ground box or the world vanishes from
            // altitude, which reads as the aircraft having fallen out of the level.
            cam.farClipPlane = Mathf.Max(spec.farClip, spec.groundHalfExtent * 1.5f);
            cam.nearClipPlane = 0.05f;

            if (spec.useSkybox)
            {
                // Built-in RP (the project has no custom pipeline asset), so
                // Skybox/Procedural resolves and GraphOverlay's OnPostRender still
                // fires. A gradient sky is what makes attitude readable.
                Shader sky = Shader.Find("Skybox/Procedural");
                if (sky != null)
                {
                    RenderSettings.skybox = new Material(sky);
                    cam.clearFlags = CameraClearFlags.Skybox;
                }
                else
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.45f, 0.62f, 0.80f);
                }
            }
            else
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.45f, 0.62f, 0.80f);
            }

            var graph = cam.gameObject.GetComponent<GraphOverlay>();
            if (graph == null) graph = cam.gameObject.AddComponent<GraphOverlay>();
            return (cam, graph);
        }
    }
}
