using System.Collections.Generic;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[DRAG] — the geometry drag estimator's bench check.</b> No scene, no
    /// rigidbody, no play mode: it measures every shipped body's silhouette,
    /// evaluates the correlation, and prints the intermediate terms beside the
    /// answer so a surprising drag coefficient can be read rather than guessed at.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.DragBench.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// <b>Why a bench rather than a coastdown.</b> <c>P1CoastdownTest</c> already
    /// measures Cd·A the honest way — accelerate, release, fit the decay — and it
    /// is the right test for the FORCE model. It is the wrong test for this one:
    /// it takes a scene, a car and thirty seconds of simulation to answer a
    /// question that is pure arithmetic over a mesh, and when it disagrees it
    /// cannot say whether the silhouette, the correlation or the integrator was
    /// wrong. This says which.
    ///
    /// <b>What is pinned and what is only printed.</b> The structural properties —
    /// determinism, scale invariance, the physical band — are assertions, because
    /// they must hold whatever the constants are tuned to. The per-body numbers are
    /// pinned once the constants are frozen, and the ORDERING assertions are
    /// written against what the geometry actually measures rather than against what
    /// a shape's name suggests it ought to.
    /// </summary>
    public static class DragBench
    {
        private const string Tag = "[DRAG]";

        private static int _checks;
        private static int _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [DRAG] Drag Estimator Bench", priority = 121)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            // Measured fresh: a cached silhouette from earlier in this editor
            // session would make the bench a test of the cache.
            DragEstimator.ResetCache();

            Survey();
            Tiguan();
            Properties();
            Ordering();

            Debug.Log(_log.ToString().TrimEnd());

            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- the reference car ------------------------------------------------------

        /// <summary>
        /// A fixed four-wheel layout at the nominal RC body size, so the thirteen
        /// bodies are compared as bodies rather than as thirteen different cars.
        /// The numbers are <see cref="WheelSpec"/>'s own defaults.
        /// </summary>
        private static List<WheelDisc> ReferenceWheels()
        {
            const float r = 0.033f;
            var w = new List<WheelDisc>();
            foreach (float z in new[] { 0.152f, -0.152f })
                foreach (float x in new[] { -0.083f, 0.083f })
                    w.Add(new WheelDisc(new Vector3(x, -0.045f, z), r));
            return w;
        }

        private static readonly Vector3 RefSize = CarVehicle.BodyMeshAuthorSize;

        // ---- the frozen answer ------------------------------------------------------

        private readonly struct Pin
        {
            public readonly string id; public readonly float cd, area;
            public Pin(string id, float cd, float area) { this.id = id; this.cd = cd; this.area = area; }
        }

        /// <summary>
        /// What each shipped body measures, with the constants as they stand.
        ///
        /// <b>Not targets — a record.</b> Nothing was tuned to hit these; they are
        /// what the geometry says, written down so that a change to the estimator,
        /// to a body mesh, or to the rasteriser is visible as a diff instead of as
        /// a car that quietly got faster. The bench prints a paste-ready block at
        /// full precision, so re-recording them is deliberate and one step.
        ///
        /// The RC bodies are measured at the nominal author size with the default
        /// four-wheel layout; the Tiguan at its published body box with its own
        /// wheels.
        /// </summary>
        private static readonly Pin[] Pins =
        {
            new Pin("box", 0.928048551f, 0.0200000014f),
            new Pin("wedge", 0.4163933f, 0.0206612144f),
            new Pin("body_buggy", 0.428213626f, 0.0161520783f),
            new Pin("body_shell", 0.436769754f, 0.0134068709f),
            new Pin("body_lowracer", 0.5536576f, 0.008652448f),
            new Pin("body_coupe", 0.435153633f, 0.0198846254f),
            new Pin("body_baja", 0.38997525f, 0.0164368935f),
            new Pin("body_patrol", 0.39604193f, 0.01604055f),
            new Pin("body_rattle", 0.367195517f, 0.0237798113f),
            new Pin("body_redline", 0.347738236f, 0.0218782164f),
            new Pin("body_highwing", 0.362840116f, 0.02169084f),
            new Pin("body_autopia", 0.366817981f, 0.0175478477f),
            new Pin("body_tiguan", 0.364170849f, 2.47953248f),
        };

        // ---- what every body measures -----------------------------------------------

        private static void Survey()
        {
            Line("body            cd    area_m2   sigF  poros bNose noseL recov rBase  wet/A  whlCdA  tris  source");
            foreach (BodyDef def in BodyCatalog.Seed)
            {
                Vector3 size = def.unscaled ? def.nominalSize : RefSize;
                var wheels = def.unscaled ? TiguanWheels() : ReferenceWheels();
                if (!DragEstimator.TryEstimate(def, size, wheels, out var r))
                {
                    _checks++; _failed++;
                    Debug.LogError($"{Tag} FAIL {def.id} could not be measured at all");
                    continue;
                }
                BodySilhouette s = r.sil;
                Line($"{def.id,-15} {r.cd,5:0.000} {r.frontalArea,8:0.0000} " +
                     $"{s.solidFront,5:0.000} {s.porosity,5:0.000} " +
                     $"{r.bNose,5:0.000} {r.noseFrac,5:0.000} {r.recover,5:0.000} " +
                     $"{r.rBase,5:0.000} {r.wetOverRef,6:0.00} " +
                     $"{r.wheelCdA,7:0.00000} {s.triangles,5} {s.source}");

                // The band is what [AKEY] holds the authored table to, and the
                // clamp makes it structurally true — so what this actually catches
                // is the clamp ENGAGING, which means the correlation left the range
                // a car can physically be in.
                Check($"{def.id} in band", r.cd,
                      Mathf.Clamp(r.cd, DragEstimator.CdMin + 1e-4f, DragEstimator.CdMax - 1e-4f),
                      1e-4f, "", "0.15 is a teardrop, 1.2 a flat plate broadside");

                foreach (Pin p in Pins)
                {
                    if (p.id != def.id) continue;
                    Check($"{def.id} cd pinned", r.cd, p.cd, 1e-4f, "", "recorded, not targeted");
                    Check($"{def.id} area pinned", r.frontalArea, p.area,
                          Mathf.Max(1e-5f, p.area * 1e-3f), "m²", "recorded, not targeted");
                }
            }

            Line("");
            Line("pin table, at full precision — paste into Pins[] when the constants move");
            foreach (BodyDef def in BodyCatalog.Seed)
            {
                Vector3 size = def.unscaled ? def.nominalSize : RefSize;
                var wheels = def.unscaled ? TiguanWheels() : ReferenceWheels();
                if (!DragEstimator.TryEstimate(def, size, wheels, out var r)) continue;
                Line($"    new Pin(\"{def.id}\", {r.cd:R}f, {r.frontalArea:R}f),");
            }

            Line("");
            Line("station profiles (filled cross-section, nose → tail, fraction of the w×h box)");
            foreach (BodyDef def in BodyCatalog.Seed)
            {
                BodySilhouette s = DragEstimator.SilhouetteFor(def);
                if (s == null) continue;
                var sb = new StringBuilder();
                foreach (float a in s.station) sb.Append($"{a,6:0.000}");
                Line($"{def.id,-15}{sb}");
            }
            Line("");
        }

        // ---- the calibration anchor -------------------------------------------------

        private static List<WheelDisc> TiguanWheels()
        {
            var d = DebugVehicles.VwTiguan();
            var w = new List<WheelDisc>();
            foreach (WheelSpec s in d.wheels) w.Add(new WheelDisc(s.localPos, s.radius));
            return w;
        }

        /// <summary>
        /// The one body in the game whose real drag is published, measured as the
        /// design actually builds it.
        ///
        /// <b>This is a check on the estimator, not on the car.</b> The Tiguan
        /// authors <c>dragCd</c> and <c>frontalAreaM2</c>, so the game keeps using
        /// 0.31 × 2.47 whatever this says — which is exactly what makes it a usable
        /// reference: the estimate can be wrong here without anything else moving.
        ///
        /// The area tolerance is tight because a projected outline of a 1:1 mesh
        /// really should land on the published frontal area. The cd tolerance is
        /// not, and saying why is more useful than quietly widening it: a
        /// silhouette cannot see an underbody tray, a mirror fairing or an edge
        /// radius, and those are most of what separates a modern 0.31 car from the
        /// 0.40 its outline alone would suggest.
        /// </summary>
        private static void Tiguan()
        {
            BodyDef def = null;
            foreach (BodyDef d in BodyCatalog.Seed) if (d.id == "body_tiguan") def = d;
            if (def == null) { _checks++; _failed++; Debug.LogError($"{Tag} FAIL no body_tiguan row"); return; }

            var design = DebugVehicles.VwTiguan();
            if (!DragEstimator.TryEstimate(def, design.bodySize, TiguanWheels(), out var r))
            {
                _checks++; _failed++;
                Debug.LogError($"{Tag} FAIL the Tiguan mesh could not be measured — " +
                               "without it this model has no published reference at all");
                return;
            }

            Line($"Tiguan extents {r.extentsM.x:0.000} x {r.extentsM.y:0.000} x " +
                 $"{r.extentsM.z:0.000} m (published body box 1.839 x 1.443 x 4.486)");
            Line($"Tiguan Cd·A estimated {r.cd * r.frontalArea:0.0000} m² vs published 0.7657 m²");

            Check("Tiguan frontal area", r.frontalArea, 2.47f, 0.1235f, "m²",
                  "published; ±5 % — the outline carries mirrors and rails, the box does not");
            Check("Tiguan cd", r.cd, 0.31f, 0.062f, "",
                  "published; ±20 % — a silhouette cannot see the underbody detail that " +
                  "is most of the difference between 0.31 and 0.40");
        }

        // ---- properties that must hold whatever the constants are -------------------

        private static void Properties()
        {
            BodyDef box = Find("box");
            var wheels = ReferenceWheels();

            // Determinism. This number reaches the physics, so a silhouette that
            // measured differently on two calls would be a car that coasted
            // differently on two runs.
            DragEstimator.TryEstimate(box, RefSize, wheels, out var a);
            DragEstimator.ResetCache();
            DragEstimator.TryEstimate(box, RefSize, wheels, out var b);
            Check("re-measured cd identical", b.cd - a.cd, 0f, 0f, "",
                  "bit-for-bit, from a cold cache");
            Check("re-measured area identical", b.frontalArea - a.frontalArea, 0f, 0f, "m²",
                  "bit-for-bit, from a cold cache");

            // Uniform scale. A model car and a full-size one of the same shape have
            // the same drag COEFFICIENT and four times the area — that is what a
            // coefficient means, and it is the property that would break first if
            // any metre leaked into the shape terms.
            var big = new List<WheelDisc>();
            foreach (WheelDisc w in wheels) big.Add(new WheelDisc(w.pos * 2f, w.radius * 2f));
            DragEstimator.TryEstimate(box, RefSize * 2f, big, out var twice);
            Check("cd under uniform ×2", twice.cd, a.cd, 1e-5f, "",
                  "a coefficient is scale-free");
            Check("area under uniform ×2", twice.frontalArea, a.frontalArea * 4f, 1e-6f, "m²",
                  "area goes as the square");

            // Non-uniform stretch. Cd is ALLOWED to move here and should: a longer
            // body has the same frontal area and more skin to drag through the
            // air, and for a box — whose nose and base are equally abrupt at any
            // length — friction is the only term that can move at all. What is
            // pinned is that it moves a LITTLE. A metre leaking into one of the
            // shape terms would swing this by tens of percent, which is exactly the
            // bug a scale-free measurement exists to prevent.
            var longer = new Vector3(RefSize.x, RefSize.y, RefSize.z * 1.5f);
            DragEstimator.TryEstimate(box, longer, wheels, out var stretched);
            Line($"box cd at ×1.5 length {stretched.cd:0.0000} vs {a.cd:0.0000} " +
                 $"({(stretched.cd / a.cd - 1f) * 100f:+0.0;-0.0} %)");
            Check("cd under ×1.5 length", stretched.cd / a.cd, 1f, 0.10f, "",
                  "friction may move it; a shape term must not");
            Check("area unchanged by lengthening", stretched.frontalArea, a.frontalArea, 1e-6f,
                  "m²", "frontal area does not know about length");

            // The wheels have to matter, or the open-wheel argument is decoration.
            DragEstimator.TryEstimate(box, RefSize, null, out var noWheels);
            Line($"box cd with wheels {a.cd:0.0000}, without {noWheels.cd:0.0000} " +
                 $"(exposed-wheel Cd·A {a.wheelCdA:0.00000} m²)");
            _checks++;
            if (a.wheelCdA <= 0f)
            {
                _failed++;
                Debug.LogError($"{Tag} FAIL no exposed-wheel drag on a reference car whose " +
                               "wheels stand outside a 0.20 m wide body — the exposure test " +
                               "is not finding them");
            }
        }

        // ---- what the shapes must say relative to each other ------------------------

        /// <summary>
        /// The claim this whole change exists to make: a body's drag follows from
        /// its geometry, so shapes that are obviously different must come out
        /// obviously different, in the right direction.
        ///
        /// Written as Cd·A rather than cd alone wherever the shapes differ in
        /// frontal area, because Cd·A is the number that actually decides how fast
        /// a car goes — an open frame with a low coefficient and a big hole in it
        /// wins on area, and that is the point.
        /// </summary>
        private static void Ordering()
        {
            float cdBox = CdOf("box"), cdWedge = CdOf("wedge");
            float cdaBox = CdaOf("box"), cdaBuggy = CdaOf("body_buggy"),
                  cdaShell = CdaOf("body_shell");

            Line($"cd: box {cdBox:0.000}  wedge {cdWedge:0.000}");
            Line($"Cd·A (m²): box {cdaBox:0.00000}  buggy {cdaBuggy:0.00000}  " +
                 $"shell {cdaShell:0.00000}");

            Greater("a square box drags more than a wedge", cdBox, cdWedge, 0.02f,
                    "same footprint, but the wedge gives its area up gradually");
            Greater("a square box drags more than a faired shell", cdaBox, cdaShell, 1e-4f,
                    "Cd·A: the shell is both smaller in section and cleaner behind");
            Greater("a square box drags more than an open frame", cdaBox, cdaBuggy, 1e-4f,
                    "Cd·A: the frame's silhouette has holes in it, and holes are not area");
        }

        private static BodyDef Find(string id)
        {
            foreach (BodyDef d in BodyCatalog.Seed) if (d.id == id) return d;
            return null;
        }

        private static float CdOf(string id)
        {
            DragEstimator.TryEstimate(Find(id), RefSize, ReferenceWheels(), out var r);
            return r.cd;
        }

        private static float CdaOf(string id)
        {
            DragEstimator.TryEstimate(Find(id), RefSize, ReferenceWheels(), out var r);
            return r.cd * r.frontalArea;
        }

        // ---- harness ----------------------------------------------------------------

        private static void Check(string name, float got, float expect, float tol,
                                  string units, string why)
        {
            _checks++;
            float err = got - expect;
            bool ok = Mathf.Abs(err) <= tol;
            if (!ok) _failed++;

            string line = $"{(ok ? "ok  " : "FAIL")} {name,-32} {got,10:0.#####} {units,-4}"
                          + $" (expect {expect:0.#####} ±{tol:0.#####})  — {why}";
            if (ok) Line(line);
            else Debug.LogError($"{Tag} {line}");
        }

        private static void Greater(string name, float bigger, float smaller, float margin,
                                    string why)
        {
            _checks++;
            bool ok = bigger > smaller + margin;
            string line = $"{(ok ? "ok  " : "FAIL")} {name,-48} {bigger:0.#####} > " +
                          $"{smaller:0.#####} (+{margin:0.#####})  — {why}";
            if (ok) Line(line);
            else { _failed++; Debug.LogError($"{Tag} {line}"); }
        }

        private static void Line(string s) => _log.AppendLine($"{Tag} {s}");
    }
}
