using System.Text;
using AIHWSim.Core.Flight;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[NAVB] — the navball's bench check.</b> No scene, no aircraft, no
    /// rendering: the marker projection and the texture layout are both pure
    /// functions, and this pins them.
    ///
    /// <b>Why this exists rather than "fly it and look".</b> A navball is made of
    /// sign conventions — which way the ball turns, which side a marker falls on,
    /// which way up the sky is — and every one of them is a coin flip that looks
    /// almost right when it is backwards. Half of them only show themselves in an
    /// attitude you have to fly to reach: a marker that mirrors to the wrong side
    /// of the ball is invisible until the velocity vector passes behind you. The
    /// flight model's own history is full of signs that were wrong for a while
    /// (see the roll-gain note in <c>FlightTest</c>), so these are asserted where
    /// they can be asserted in a second rather than trusted to the eye.
    ///
    /// The expectations are written as the geometric argument that produces them,
    /// so a disagreement says which of the two is wrong.
    /// </summary>
    public static class NavballBench
    {
        private const string Tag = "[NAVB]";

        private static int _checks;
        private static int _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [NAVB] Navball Bench", priority = 121)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            Seam();
            Projection();
            Skin();

            Debug.Log(_log.ToString().TrimEnd());

            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- checks ------------------------------------------------------

        /// <summary>The mesh seam measurement has to be a definite number, and the
        /// same one every run, or the heading band lands somewhere different each
        /// time the scene loads.</summary>
        private static void Seam()
        {
            var probe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            probe.hideFlags = HideFlags.HideAndDontSave;
            Mesh mesh = probe.GetComponent<MeshFilter>().sharedMesh;

            float a = NavballRig.MeasureSeam(mesh);
            float b = NavballRig.MeasureSeam(mesh);
            Object.DestroyImmediate(probe);

            Line($"  seam offset {a:0.000}°");
            Check("seam is finite", float.IsNaN(a) || float.IsInfinity(a) ? 0f : 1f, 1f, 1e-6f,
                  "", "an unmeasurable seam would silently become 0 and skew every heading");
            Check("seam is repeatable", Mathf.Abs(Mathf.DeltaAngle(a, b)), 0f, 1e-4f, "°",
                  "same mesh, same answer — it is read from vertices, nothing random");

            // A sphere's equatorial vertices must actually have produced a reading;
            // if the UV filter rejected them all the routine returns 0, which is a
            // plausible-looking wrong answer. Prove at least it is not the fallback
            // by checking the mesh has equatorial vertices to read at all.
            int usable = 0;
            Vector2[] uv = mesh.uv;
            for (int i = 0; i < uv.Length; i++)
                if (Mathf.Abs(uv[i].y - 0.5f) <= 0.08f && uv[i].x > 0.02f && uv[i].x < 0.98f)
                    usable++;
            Check("equatorial vertices found", usable, 20f, 1e9f, "verts",
                  "the seam is averaged over these; zero of them means a silent 0°");
            Line($"  {usable} usable equatorial vertices");
        }

        /// <summary>The marker projection, against attitudes whose answers follow
        /// from geometry alone.</summary>
        private static void Projection()
        {
            var rig = new NavballRig();
            const float Half = 110f;                       // HUD draws the ball 220 wide
            float unit = Half * NavballRig.BallFill;       // one radius, in GUI pixels

            // 1. The nose is the centre of the ball. This is the definition of a
            //    navball, and it must hold at EVERY attitude, not just level —
            //    which is exactly what a wrong flip quaternion breaks.
            float worstNose = 0f;
            for (int i = 0; i < 24; i++)
            {
                Quaternion att = Quaternion.Euler(i * 37f % 360f, i * 61f % 360f, i * 83f % 360f);
                Vector2? o = rig.MarkerOffset(att * Vector3.forward, att, Half);
                worstNose = Mathf.Max(worstNose, o == null ? 999f : o.Value.magnitude);
            }
            Check("nose sits at ball centre", worstNose, 0f, 1e-3f, "px",
                  "24 attitudes; the aircraft's own forward is what the ball is centred on");

            // 2. Directly astern is on the far side and must be HIDDEN, not
            //    mirrored to the front. This is the bug that only appears in flight.
            Quaternion level = Quaternion.identity;
            Check("astern is hidden", rig.MarkerOffset(Vector3.back, level, Half) == null ? 1f : 0f,
                  1f, 1e-6f, "", "z ≥ 0 is the far hemisphere");

            // 3. Something above the nose draws ABOVE the centre. GUI y counts
            //    downward, so "above" is a negative offset.
            Vector3 up10 = new Vector3(0f, Mathf.Sin(10f * Mathf.Deg2Rad), Mathf.Cos(10f * Mathf.Deg2Rad));
            Vector2 o10 = rig.MarkerOffset(up10, level, Half).Value;
            Check("10° above nose → up-screen", o10.y, -unit * Mathf.Sin(10f * Mathf.Deg2Rad), 1e-3f,
                  "px", "sin 10° of a radius, negative because GUI y grows downward");
            Check("10° above nose → no sideways", o10.x, 0f, 1e-4f, "px", "straight up is not sideways");

            // 4. Nose up, flying level: the prograde marker must sit BELOW the
            //    centre by the pitch angle. Get this backwards and the instrument
            //    tells the pilot to do the opposite of the right thing.
            Quaternion noseUp10 = Quaternion.Euler(-10f, 0f, 0f);   // +x euler is nose DOWN
            Vector2 pro = rig.MarkerOffset(Vector3.forward, noseUp10, Half).Value;
            Check("nose up 10° → prograde below", pro.y, unit * Mathf.Sin(10f * Mathf.Deg2Rad), 1e-3f,
                  "px", "velocity is 10° below the nose, and below is +y in GUI space");

            // 5. Roll about the NOSE swings the markers around the reticle by the
            //    bank angle. Note the multiplication order: `att * AngleAxis` is a
            //    rotation about the aircraft's own axis, `AngleAxis * att` about the
            //    world's. Writing it the second way makes this a test of nothing —
            //    see check 5b for why. Asserted as the ANGLE BETWEEN two offsets,
            //    so it pins the roll axis without depending on the sign convention.
            Quaternion bank45 = noseUp10 * Quaternion.AngleAxis(45f, Vector3.forward);
            Vector2 rolled = rig.MarkerOffset(Vector3.forward, bank45, Half).Value;
            Check("45° of bank swings the marker 45°", Vector2.Angle(pro, rolled), 45f, 0.05f, "°",
                  "the marker is fixed to the world and the ball turns under it");
            Check("bank does not change the offset", rolled.magnitude, pro.magnitude, 1e-3f, "px",
                  "rolling about the nose moves nothing closer to or further from it");

            // 5b. Rolling about the VELOCITY vector must not move the prograde
            //     marker at all — a direction is invariant under rotation about
            //     itself, so its place on the ball cannot change. Worth asserting
            //     because it is the degenerate case that made check 5 pass
            //     vacuously when it was written with the axes the other way round.
            Quaternion aboutVel = Quaternion.AngleAxis(45f, Vector3.forward) * noseUp10;
            Vector2 spun = rig.MarkerOffset(Vector3.forward, aboutVel, Half).Value;
            Check("roll about velocity leaves prograde put", (spun - pro).magnitude, 0f, 1e-3f,
                  "px", "rotating about a vector fixes that vector");

            // 6. Far down the ball, near the limb. Deliberately 80° and not 90°:
            //    at exactly 90° the direction is edge-on, z is exactly zero, and
            //    "visible" is a coin toss between two equally correct answers.
            //    A test has no business standing on that line.
            float s80 = Mathf.Sin(80f * Mathf.Deg2Rad), c80 = Mathf.Cos(80f * Mathf.Deg2Rad);
            Vector2 low = rig.MarkerOffset(new Vector3(0f, -s80, c80), level, Half).Value;
            Check("80° below nose → near the limb", low.y, unit * s80, 1e-3f, "px",
                  "sin 80° of a radius, downward");
            Check("91° below nose is hidden",
                  rig.MarkerOffset(new Vector3(0f, -Mathf.Sin(91f * Mathf.Deg2Rad),
                                               Mathf.Cos(91f * Mathf.Deg2Rad)),
                                   level, Half) == null ? 1f : 0f,
                  1f, 1e-6f, "", "one degree past the terminator is the far side");
        }

        /// <summary>The skin: sky up, ground down, and a horizon you cannot miss.
        /// An inverted V mapping produces a perfectly plausible ball that flies
        /// upside down.</summary>
        private static void Skin()
        {
            Texture2D tex = NavballTexture.Build();
            Check("skin width", tex.width, 512f, 0.5f, "px", "equirectangular, 2:1");
            Check("skin height", tex.height, 256f, 0.5f, "px", "");

            Color sky = tex.GetPixel(40, Mathf.RoundToInt((45f + 90f) / 180f * (tex.height - 1)));
            Color ground = tex.GetPixel(40, Mathf.RoundToInt((-45f + 90f) / 180f * (tex.height - 1)));
            Color horizon = tex.GetPixel(40, Mathf.RoundToInt(0.5f * (tex.height - 1)));

            Check("sky is blue-dominant", sky.b - sky.r, 0.15f, 1e9f, "",
                  "+45° latitude must read as sky");
            Check("ground is red-dominant", ground.r - ground.b, 0.10f, 1e9f, "",
                  "−45° latitude must read as ground — this is what catches a flipped V");
            Check("horizon is bright", horizon.grayscale, 0.80f, 1e9f, "",
                  "the one line that has to be unmistakable");

            Object.DestroyImmediate(tex);
        }

        // ---- plumbing ----------------------------------------------------

        /// <summary>A tolerance of 1e9 means "at least this much" rather than
        /// "equal to" — used where the right answer is a threshold, not a value.</summary>
        private static void Check(string name, float actual, float expected, float tol,
                                  string unit, string why)
        {
            _checks++;
            bool ok = tol >= 1e8f ? actual >= expected : Mathf.Abs(actual - expected) <= tol;
            if (!ok) _failed++;

            string verdict = ok ? "PASS" : "FAIL";
            string cmp = tol >= 1e8f ? $"≥ {expected:0.####}" : $"{expected:0.####}";
            Line($"  {verdict}  {name,-34} {actual,10:0.####} {unit,-3} vs {cmp,-10} {why}");
        }

        private static void Line(string s) => _log.AppendLine(s);
    }
}
