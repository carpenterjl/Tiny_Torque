using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>A wheel as the drag estimator sees one: where it is and how big.</summary>
    public readonly struct WheelDisc
    {
        public readonly Vector3 pos;    // hub centre, body-local metres
        public readonly float radius;   // m

        public WheelDisc(Vector3 pos, float radius) { this.pos = pos; this.radius = radius; }
    }

    /// <summary>
    /// What a body's geometry looks like from in front, behind and the side —
    /// measured once, in the shape's own proportions, and scale-free.
    ///
    /// Every number here is a FRACTION of the source bounding box, which is what
    /// lets one measurement answer for a body at any <c>bodySize</c>: stretching
    /// a car changes the metres, not the silhouette's shape.
    /// </summary>
    public sealed class BodySilhouette
    {
        /// <summary>Source-space bounding box (mesh units, or metres at the
        /// nominal size for a primitive compound).</summary>
        public Vector3 boundsMin, boundsMax, boundsSize;

        /// <summary>Fraction of the bounding rectangle the shape actually fills,
        /// seen from the front / the side / above. <see cref="solidFront"/> is
        /// what replaces the old <c>width * height * 0.9</c>: an open frame's
        /// gaps are missing area, not a fudge factor.</summary>
        public float solidFront, solidSide, solidPlan;

        /// <summary>
        /// How much of the frontal OUTLINE is holes rather than car — 0 for a
        /// closed shell, high for a roll cage.
        ///
        /// Measured against the outline's own filled shadow, not against the
        /// bounding box, so a rounded body does not read as porous merely for
        /// having corners.
        /// </summary>
        public float porosity;

        /// <summary>Filled cross-section area at each station, nose to tail, as a
        /// fraction of the frontal bounding rectangle.</summary>
        public float[] station;

        /// <summary>The frontal silhouette itself, one bit per grid cell, row-major
        /// from the bottom of the box up. Kept so a wheel can be asked whether the
        /// body is standing in front of it.</summary>
        public ulong[] frontMask;

        /// <summary>Triangles measured. Diagnostic — a body that reads as 12 is a
        /// body whose FBX did not load and fell back to a box.</summary>
        public int triangles;

        /// <summary>How the silhouette was obtained, for the bench line.</summary>
        public string source;
    }

    /// <summary>
    /// Estimates a body's drag coefficient and frontal area from its actual
    /// geometry, rather than reading a number somebody guessed per shell.
    ///
    /// <b>Why this exists.</b> The old answer was a hand-authored <c>cd</c> per
    /// catalogue row and a frontal area of <c>width * height * 0.9</c> — which
    /// gives an open buggy frame and a solid slab of the same bodySize exactly the
    /// same drag, because neither number ever looked at the car. The whole point
    /// of a geometry-driven answer is that an empty frame comes out lighter than
    /// an enclosed cabin, and a flat wall heavier than either, for the reason it
    /// actually does: less area, and a shape the air has an easier time leaving.
    ///
    /// <b>How it works.</b> Two stages, cached separately because they cost very
    /// different amounts:
    ///
    /// 1. <see cref="SilhouetteFor"/> — project the body's triangles onto the
    ///    frontal plane on a fixed grid, once per body, at
    ///    <see cref="Stations"/> stations along its length. That yields the true
    ///    projected frontal outline, the filled cross-section at each station, and
    ///    side/plan solidity for a wetted-area estimate. All fractions, so the
    ///    measurement is scale-free.
    /// 2. <see cref="TryEstimate"/> — evaluate those fractions at a particular
    ///    bodySize and wheel layout. Cheap: sixteen stations of arithmetic.
    ///
    /// <b>The correlation.</b> Four terms, deliberately few:
    /// <list type="bullet">
    /// <item>a FOREBODY term — area gained abruptly is a face paying near
    /// stagnation pressure — multiplied by a RECOVERY factor for how much of the
    /// body's length is still gaining area. That second half is the one that
    /// matters: a car's bumper is as blunt as a box's front, and the reason a car
    /// is not as draggy as a box is that its flow stays attached over a long hood
    /// and windscreen and gets most of that pressure back. A shape that grabs its
    /// area and stops has nothing to recover on;</item>
    /// <item>a BASE term — the largest single-station area loss, squared. Base
    /// drag is where most of a real car's pressure drag lives, it scales with the
    /// abandoned area, and the base pressure is also less negative when the
    /// afterbody has already tapered, which is what the square is standing in
    /// for. The LARGEST loss rather than the trailing-edge area, because a wrecker
    /// whose boom trails behind a slab-sided cab separates at the cab: the wake
    /// starts where the body stops being a body, not where the mesh runs out;</item>
    /// <item>skin friction over the wetted area, which is what makes a long body
    /// cost more per square metre of frontal area than a stubby one;</item>
    /// <item>a floor for the things a silhouette cannot see — underbody, cooling,
    /// interference.</item>
    /// </list>
    /// Exposed wheels add their own <c>Cd*A</c> on top, because an open-wheel car
    /// drags like one.
    ///
    /// <b>Wheel drag is folded into the returned cd, not applied as a separate
    /// force.</b> That is a hard requirement, not tidiness: <c>P0FreeRollTest</c>
    /// removes all drag by setting <c>dragCdOverride</c> to 1e-9, so every newton
    /// this model produces has to sit behind that one multiplication or the null
    /// test stops being null.
    ///
    /// <b>Lift is not estimated.</b> <c>BodyDef.clA</c> stays authored. A
    /// silhouette genuinely cannot see whether a shape makes downforce, and the
    /// two race cars' built-in <c>clA</c> is a gameplay decision rather than a
    /// measurement.
    ///
    /// <b>Honest about its own accuracy.</b> A silhouette cannot see an underbody
    /// tray, a mirror fairing or an edge radius, which is most of what separates a
    /// modern 0.31 car from a 0.40 one. An explicitly authored figure therefore
    /// still wins — see <c>CarVehicle.EffectiveAero</c> — and the published Tiguan
    /// numbers remain the reference this model is checked against rather than
    /// something it overrides.
    /// </summary>
    public static class DragEstimator
    {
        // ---- measurement resolution -------------------------------------------------

        /// <summary>Cells per axis in the projection grid. 64 puts a 20 cm RC body
        /// at about 3 mm per cell, which resolves a roll hoop.</summary>
        public const int Grid = 64;

        /// <summary>Slices along the body's length. 16 is enough to tell a nose
        /// that builds over a fifth of the car from one that builds at once.</summary>
        public const int Stations = 16;

        // ---- the correlation --------------------------------------------------------
        //
        // Six constants, and they are the whole tunable surface of this model.
        // Fitted against the one car in the game whose drag is PUBLISHED — the
        // Tiguan's 0.31 — with the shipped shells' hand-authored table values as a
        // sanity check on the spread rather than as targets, since replacing those
        // guesses is the entire point. What comes out: a square box near 0.88 (a
        // rectangular prism really is 0.9–1.05), the faired shells 0.33–0.43, and
        // the open-wheel low racer above them on exposed wheels alone.

        /// <summary>Forebody pressure drag at a fully blunt face with no recovery.</summary>
        public const float CNose = 0.65f;

        /// <summary>Base/wake drag behind a body that abandons its whole
        /// cross-section at once.</summary>
        public const float CBase = 0.32f;

        /// <summary>Turbulent flat-plate skin friction at these Reynolds numbers.</summary>
        public const float CFriction = 0.004f;

        /// <summary>What a silhouette cannot see: underbody, cooling, interference.
        /// The floor that stops a teardrop estimating below anything a car is.</summary>
        public const float CFloor = 0.04f;

        /// <summary>How steep a frontal-area buildup has to be to count as a flat
        /// face. 0.15 means "all of it inside 15 % of the length".</summary>
        public const float KappaNose = 0.15f;

        /// <summary>How strongly a long attached forebody wins its pressure back.
        /// A body still growing halfway down its length keeps about a fifth of
        /// what its blunt face cost; a box that is full width immediately keeps
        /// nearly three quarters.</summary>
        public const float KappaRecover = 6f;

        /// <summary>The fraction of maximum cross-section that counts as "the
        /// forebody has finished". Not 1.0: a body that gains its last half a
        /// percent at the very back would otherwise read as one long nose.</summary>
        public const float NoseComplete = 0.95f;

        /// <summary>
        /// Drag coefficient of a shape that is not a body at all — a roll cage, an
        /// exposed A-arm, a bare tube — on its own projected area.
        ///
        /// <b>Why an open frame needs this.</b> Everything above reasons about a
        /// body the air flows AROUND: a gentle area buildup means attached flow and
        /// recovered pressure. A frame is not that. Its members each have their own
        /// stagnation face and their own wake, and there is no recovery to be had —
        /// the "gentle buildup" a sparse silhouette shows is holes, not fairing.
        /// Left uncorrected the estimator read the desert truck as one of the
        /// slipperiest things in the game.
        ///
        /// So the answer is blended toward this by porosity. The frame still comes
        /// out well ahead of an enclosed cabin, which is the point — but it wins on
        /// AREA, the way it actually does, rather than on a coefficient no bundle
        /// of tubes has ever had.
        /// </summary>
        public const float CdMember = 1.0f;

        /// <summary>Drag coefficient of an exposed rotating wheel, on its own
        /// frontal area (Hoerner).</summary>
        public const float CWheel = 0.55f;

        /// <summary>Tyre width as a fraction of diameter. <see cref="WheelSpec"/>
        /// carries no width, so the proxy is stated here rather than hidden in the
        /// arithmetic.</summary>
        public const float WheelWidthPerDiameter = 0.25f;

        /// <summary>The band a car's drag coefficient has to land in: 0.15 is a
        /// teardrop, 1.2 a flat plate broadside. The same band <c>[AKEY]</c> holds
        /// the authored table to.</summary>
        public const float CdMin = 0.15f, CdMax = 1.2f;

        // ---- results ----------------------------------------------------------------

        /// <summary>An estimate, with the intermediate numbers the bench prints so
        /// a surprising cd can be read rather than guessed at.</summary>
        public struct Result
        {
            public float cd;             // what the physics uses
            public float frontalArea;    // m^2, the true projected outline
            public float cdBody;         // before wheels were folded in
            public float wheelCdA;       // m^2 of exposed-wheel Cd*A
            public float bNose;          // bluntness of the area buildup, 0..1
            public float noseFrac;       // length fraction spent still growing
            public float recover;        // forebody pressure recovery factor, 0..1
            public float rBase;          // largest single-station area loss, 0..1
            public float wetOverRef;     // S_wet / A_ref
            public Vector3 extentsM;     // W, H, L in metres
            public BodySilhouette sil;
        }

        // ---- silhouette cache -------------------------------------------------------

        private static readonly Dictionary<string, BodySilhouette> _cache =
            new Dictionary<string, BodySilhouette>();

        /// <summary>
        /// Forget every measured silhouette. The Asset Studio commit pipeline calls
        /// this beside <c>PartMeshLibrary.ResetCache</c> — a body whose FBX has just
        /// been imported was measured as a fallback box a moment ago.
        /// </summary>
        public static void ResetCache() => _cache.Clear();

        /// <summary>
        /// The measured silhouette for a body, or null when its geometry cannot be
        /// read. Cached per body key for the session.
        /// </summary>
        public static BodySilhouette SilhouetteFor(BodyDef def)
        {
            string key = def == null ? "prim:Box" :
                         def.meshKey != null ? "mesh:" + def.meshKey :
                         "prim:" + def.legacy;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            BodySilhouette sil = def != null && def.meshKey != null
                ? MeasureMesh(def.meshKey)
                : null;
            // Same fallback the renderer takes: a catalogue body whose FBX did not
            // ship draws the primitive its own row nominates, so it should drag
            // like one too.
            sil ??= MeasurePrimitive(def == null ? BodyShape.Box : def.legacy);

            _cache[key] = sil;
            return sil;
        }

        /// <summary>
        /// Estimate this body's drag coefficient and frontal area at a given size.
        ///
        /// Returns false when the geometry could not be measured at all, which is
        /// the caller's cue to fall back to the authored table rather than to
        /// invent a number.
        /// </summary>
        public static bool TryEstimate(BodyDef def, Vector3 bodySize,
                                       IReadOnlyList<WheelDisc> wheels, out Result res)
        {
            res = default;
            BodySilhouette sil = SilhouetteFor(def);
            if (sil == null) return false;

            // Metres per source unit — whatever the RENDERER uses, so the drag and
            // the car the player is looking at can never be different sizes.
            Vector3 toM = def != null && def.meshKey != null
                ? CarVehicle.BodyRenderScale(def, bodySize)
                : new Vector3(bodySize.x / CarVehicle.BodyMeshAuthorSize.x,
                              bodySize.y / CarVehicle.BodyMeshAuthorSize.y,
                              bodySize.z / CarVehicle.BodyMeshAuthorSize.z);

            res = Evaluate(sil, toM, wheels);
            return true;
        }

        /// <summary>
        /// Estimate from an explicit triangle soup, already in metres, nose +Z.
        ///
        /// <b>Uncached, deliberately.</b> Every other entry point measures a
        /// catalogue body, whose geometry is fixed for the session and therefore
        /// worth remembering. A soup is a shape somebody has just this second
        /// changed, and the caller is the only thing that knows when that
        /// happened — see <c>BodyEd.BodyDragReadout</c>, which measures once per
        /// committed edit and never per frame. Keying a cache on a mesh that
        /// mutates is how a deformed car ends up quietly dragging like the
        /// catalogue row it started as.
        ///
        /// <c>toM</c> is one because the vertices arrive scaled; the fractions in
        /// <see cref="BodySilhouette"/> are still measured against the soup's own
        /// bounding box, so everything downstream is unchanged.
        /// </summary>
        public static bool TryEstimateSoup(List<Vector3> trisMetres,
                                           IReadOnlyList<WheelDisc> wheels,
                                           string source, out Result res)
        {
            res = default;
            if (trisMetres == null || trisMetres.Count < 3) return false;
            BodySilhouette sil = Rasterise(trisMetres, source);
            if (sil == null) return false;
            res = Evaluate(sil, Vector3.one, wheels);
            return true;
        }

        /// <summary>
        /// The correlation itself: silhouette fractions plus a scale, in; a drag
        /// coefficient and a frontal area, out.
        ///
        /// Split out from <see cref="TryEstimate"/> so a measured-in-place shape
        /// can be priced by exactly the same arithmetic as a catalogue row —
        /// nothing here changed when it moved, and <c>[DRAG]</c>'s pins are what
        /// says so.
        /// </summary>
        private static Result Evaluate(BodySilhouette sil, Vector3 toM,
                                       IReadOnlyList<WheelDisc> wheels)
        {
            Vector3 ext = Vector3.Scale(sil.boundsSize, toM);
            float w = Mathf.Max(1e-4f, Mathf.Abs(ext.x));
            float h = Mathf.Max(1e-4f, Mathf.Abs(ext.y));
            float l = Mathf.Max(1e-4f, Mathf.Abs(ext.z));

            float aBox = w * h;                       // the bounding rectangle
            float aRef = Mathf.Max(1e-6f, sil.solidFront * aBox);

            // Shape terms. The station areas are fractions of the same bounding
            // rectangle, so they can be compared to each other directly and the
            // metres cancel — which is what makes cd scale-free under a uniform
            // stretch and correctly size-dependent under a lopsided one.
            ShapeTerms(sil.station, out float bNose, out float noseFrac, out float rBase);
            float recover = 1f / (1f + KappaRecover * noseFrac);

            // Wetted area from the side and plan outlines. Two faces each, which
            // is the standard box-ish approximation and is what the friction
            // constant was fitted against.
            float sWet = 2f * (sil.solidSide * h * l + sil.solidPlan * w * l);
            float wetOverRef = sWet / aRef;

            float cdSmooth = CNose * bNose * recover
                           + CBase * rBase * rBase
                           + CFriction * wetOverRef + CFloor;

            // A shape with holes in it is not one body with attached flow, it is
            // several bluff ones. Blend accordingly.
            float p = Mathf.Clamp01(sil.porosity);
            float cdBody = (1f - p) * cdSmooth + p * CdMember;

            float wheelCdA = WheelCdA(sil, wheels, toM);

            float cd = Mathf.Clamp((cdBody * aRef + wheelCdA) / aRef, CdMin, CdMax);

            return new Result
            {
                cd = cd, frontalArea = aRef, cdBody = cdBody, wheelCdA = wheelCdA,
                bNose = bNose, noseFrac = noseFrac, recover = recover, rBase = rBase,
                wetOverRef = wetOverRef, extentsM = new Vector3(w, h, l), sil = sil,
            };
        }

        /// <summary>
        /// The three shape numbers, read off the station profile.
        ///
        /// <paramref name="bNose"/> — how bluntly the frontal area arrives, each
        /// increment weighted by its own local ramp rate, so a body that builds its
        /// section over a long run scores near zero and a flat face scores one.
        ///
        /// <paramref name="noseFrac"/> — the fraction of the body's LENGTH spent
        /// still growing. This is the pressure-recovery lever, and it is what makes
        /// a car differ from a box at all: both have a blunt face, but the car goes
        /// on gaining section for half its length, and attached flow over that run
        /// gives the stagnation pressure back.
        ///
        /// <paramref name="rBase"/> — the largest single-station area LOSS, as a
        /// fraction of the maximum section. The profile carries a trailing zero, so
        /// a flat-backed body's trailing edge is one of the candidates and wins; a
        /// body that necks down mid-length and trails an appendage separates at the
        /// neck, and that loss wins instead. Deliberately the largest drop and not
        /// the trailing-edge area, because the wake begins where the body stops
        /// being a body.
        /// </summary>
        private static void ShapeTerms(float[] station, out float bNose,
                                       out float noseFrac, out float rBase)
        {
            bNose = 0f; noseFrac = 1f; rBase = 1f;
            int k = station.Length;
            if (k == 0) return;

            float aMax = 0f;
            for (int i = 0; i < k; i++) if (station[i] > aMax) aMax = station[i];
            if (aMax <= 1e-6f) { bNose = 1f; return; }

            rBase = 0f;
            float prev = 0f;
            for (int i = 0; i <= k; i++)
            {
                float cur = i < k ? station[i] : 0f;      // the trailing zero
                float d = (cur - prev) / aMax;
                prev = cur;
                if (d > 0f)
                {
                    // Ramp rate normalised so "all of it in one station" reads as k.
                    bNose += d * Mathf.Clamp01(d * k * KappaNose);
                }
                else if (-d > rBase)
                {
                    rBase = -d;
                }
            }

            for (int i = 0; i < k; i++)
                if (station[i] >= NoseComplete * aMax) { noseFrac = (i + 1) / (float)k; break; }
        }

        /// <summary>
        /// Cd*A of the wheels the body is not hiding.
        ///
        /// A wheel tucked inside a closed shell is already paying for itself
        /// through the body's own silhouette; a wheel hanging out in the airstream
        /// is not, and is the single biggest difference between an open frame and
        /// a covered one at the same bodySize. Exposure is measured, not assumed:
        /// the wheel's frontal rectangle is sampled against the body outline.
        /// </summary>
        private static float WheelCdA(BodySilhouette sil, IReadOnlyList<WheelDisc> wheels,
                                      Vector3 toM)
        {
            if (wheels == null || wheels.Count == 0) return 0f;

            // The body outline in body-local metres.
            float x0 = sil.boundsMin.x * toM.x, x1 = sil.boundsMax.x * toM.x;
            float y0 = sil.boundsMin.y * toM.y, y1 = sil.boundsMax.y * toM.y;
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (y1 < y0) (y0, y1) = (y1, y0);
            float bw = Mathf.Max(1e-6f, x1 - x0), bh = Mathf.Max(1e-6f, y1 - y0);

            const int N = 8;   // 8x8 samples per wheel: 1.5 % resolution on exposure
            float total = 0f;
            for (int i = 0; i < wheels.Count; i++)
            {
                float r = wheels[i].radius;
                if (r <= 0f) continue;
                float tw = WheelWidthPerDiameter * 2f * r;
                float area = 2f * r * tw;

                int hidden = 0;
                for (int a = 0; a < N; a++)
                    for (int b = 0; b < N; b++)
                    {
                        float px = wheels[i].pos.x + ((a + 0.5f) / N - 0.5f) * tw;
                        float py = wheels[i].pos.y + ((b + 0.5f) / N - 0.5f) * 2f * r;
                        int col = Mathf.FloorToInt((px - x0) / bw * Grid);
                        int row = Mathf.FloorToInt((py - y0) / bh * Grid);
                        if (col < 0 || col >= Grid || row < 0 || row >= Grid) continue;
                        if ((sil.frontMask[row] & (1UL << col)) != 0UL) hidden++;
                    }

                float exposed = 1f - hidden / (float)(N * N);
                total += CWheel * exposed * area;
            }
            return total;
        }

        // ---- measurement ------------------------------------------------------------

        /// <summary>
        /// Measure one of the primitive compounds, at the nominal author size.
        ///
        /// <b>At the nominal size, not at the design's.</b> A piece carries a
        /// rotation, and Unity scales before it rotates, so a stretched compound is
        /// not a stretched picture of the nominal one. Measuring at the authored
        /// proportions and scaling the result afterwards is the same treatment the
        /// mesh path gives a rigid FBX, and it keeps one cached measurement good
        /// for every bodySize. The error it accepts is confined to how a raked
        /// panel's outline changes under an extreme stretch.
        /// </summary>
        private static BodySilhouette MeasurePrimitive(BodyShape shape)
        {
            Vector3 s = CarVehicle.BodyMeshAuthorSize;
            var tris = new List<Vector3>();
            foreach (BodyPiece p in BodyPrimitives.For(shape))
            {
                Vector3 c = Vector3.Scale(p.pos, s);
                Vector3 e = Vector3.Scale(p.scale, s) * 0.5f;
                Quaternion rot = Quaternion.Euler(p.euler);
                // Scale then rotate then translate — Unity's own T*R*S order, so
                // the boxes measured are the boxes drawn.
                var corner = new Vector3[8];
                for (int i = 0; i < 8; i++)
                {
                    var v = new Vector3((i & 1) == 0 ? -e.x : e.x,
                                        (i & 2) == 0 ? -e.y : e.y,
                                        (i & 4) == 0 ? -e.z : e.z);
                    corner[i] = rot * v + c;
                }
                AddBoxTriangles(tris, corner);
            }
            return Rasterise(tris, "primitive " + shape);
        }

        /// <summary>
        /// Measure an authored FBX body by reading the source prefab — never by
        /// instantiating one. This runs in the editor bench and at runtime on first
        /// use, and neither wants a throwaway GameObject on the physics frame it
        /// happens to land on.
        ///
        /// Returns null when the mesh cannot be read, which is a real possibility:
        /// an FBX imported without Read/Write hands back an empty vertex array.
        /// Saying so once and falling back beats reporting a car with no frontal
        /// area.
        /// </summary>
        private static BodySilhouette MeasureMesh(string meshKey)
        {
            var src = Resources.Load<GameObject>(PartMeshLibrary.PartRoot + meshKey);
            if (src == null) return null;

            AssetManifest man = AssetManifests.Load(meshKey, PartMeshLibrary.PartRoot);
            float yaw = man != null ? man.authorYawDeg : 0f;
            Vector3 off = man != null ? man.AuthorOffset : Vector3.zero;
            Quaternion yawRot = Mathf.Abs(yaw) > 0.01f
                ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.identity;

            var tris = new List<Vector3>();
            var filters = src.GetComponentsInChildren<MeshFilter>(true);
            bool unreadable = false;
            foreach (var mf in filters)
            {
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                if (!m.isReadable) { unreadable = true; continue; }

                Matrix4x4 toRoot = LocalToRoot(mf.transform, src.transform);
                Vector3[] verts = m.vertices;
                int[] idx = m.triangles;
                for (int i = 0; i < idx.Length; i++)
                {
                    Vector3 p = toRoot.MultiplyPoint3x4(verts[idx[i]]);
                    tris.Add(yawRot * (off + p));
                }
            }

            if (unreadable)
                Debug.LogWarning($"[DragEstimator] {meshKey} has a mesh with Read/Write " +
                                 "disabled — its geometry cannot be measured, so the drag " +
                                 "estimate falls back to the primitive or the table.");
            if (tris.Count < 3) return null;
            return Rasterise(tris, "mesh " + meshKey);
        }

        /// <summary>Child-to-prefab-root matrix, built by walking parents. Explicit
        /// rather than <c>localToWorldMatrix</c> because a prefab asset's transforms
        /// are not in any scene and "world" there is not a thing worth relying on.</summary>
        private static Matrix4x4 LocalToRoot(Transform t, Transform root)
        {
            Matrix4x4 m = Matrix4x4.identity;
            for (Transform c = t; c != null && c != root; c = c.parent)
                m = Matrix4x4.TRS(c.localPosition, c.localRotation, c.localScale) * m;
            return m;
        }

        private static readonly int[] BoxTri =
        {
            0,2,3, 0,3,1,   4,5,7, 4,7,6,   0,4,6, 0,6,2,
            1,3,7, 1,7,5,   0,1,5, 0,5,4,   2,6,7, 2,7,3,
        };

        private static void AddBoxTriangles(List<Vector3> tris, Vector3[] corner)
        {
            for (int i = 0; i < BoxTri.Length; i++) tris.Add(corner[BoxTri[i]]);
        }

        // ---- the projection ---------------------------------------------------------

        /// <summary>
        /// Project a triangle soup and reduce it to the fractions
        /// <see cref="BodySilhouette"/> carries.
        ///
        /// Iteration order is fixed — triangles in mesh order, stations ascending,
        /// cells row-major — so the same geometry always yields the same bits. That
        /// matters more than it looks: this number reaches the physics, and a
        /// silhouette that measured differently on two runs would be a car that
        /// coasted differently on two runs.
        /// </summary>
        private static BodySilhouette Rasterise(List<Vector3> tris, string source)
        {
            int n = tris.Count / 3;
            if (n == 0) return null;

            Vector3 lo = tris[0], hi = tris[0];
            for (int i = 1; i < tris.Count; i++)
            {
                lo = Vector3.Min(lo, tris[i]);
                hi = Vector3.Max(hi, tris[i]);
            }
            Vector3 size = hi - lo;
            float sx = Mathf.Max(1e-9f, size.x);
            float sy = Mathf.Max(1e-9f, size.y);
            float sz = Mathf.Max(1e-9f, size.z);

            var station = new ulong[Stations][];
            for (int k = 0; k < Stations; k++) station[k] = new ulong[Grid];
            var front = new ulong[Grid];
            var side = new ulong[Grid];
            var plan = new ulong[Grid];
            var scratch = new ulong[Grid];

            for (int t = 0; t < n; t++)
            {
                Vector3 a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];

                // Side (Z across, Y up) and plan (X across, Z up) need no stations.
                Stamp(side, (a.z - lo.z) / sz, (a.y - lo.y) / sy,
                            (b.z - lo.z) / sz, (b.y - lo.y) / sy,
                            (c.z - lo.z) / sz, (c.y - lo.y) / sy);
                Stamp(plan, (a.x - lo.x) / sx, (a.z - lo.z) / sz,
                            (b.x - lo.x) / sx, (b.z - lo.z) / sz,
                            (c.x - lo.x) / sx, (c.z - lo.z) / sz);

                // Frontal, once, then OR into every station the triangle spans.
                System.Array.Clear(scratch, 0, Grid);
                int rLo = Stamp(scratch, (a.x - lo.x) / sx, (a.y - lo.y) / sy,
                                         (b.x - lo.x) / sx, (b.y - lo.y) / sy,
                                         (c.x - lo.x) / sx, (c.y - lo.y) / sy,
                                out int rHi);
                if (rLo > rHi) continue;

                for (int r = rLo; r <= rHi; r++) front[r] |= scratch[r];

                // Nose is +Z, so station 0 is the front of the car.
                float za = (hi.z - a.z) / sz, zb = (hi.z - b.z) / sz, zc = (hi.z - c.z) / sz;
                int kLo = StationOf(Mathf.Min(za, Mathf.Min(zb, zc)));
                int kHi = StationOf(Mathf.Max(za, Mathf.Max(zb, zc)));
                for (int k = kLo; k <= kHi; k++)
                {
                    ulong[] m = station[k];
                    for (int r = rLo; r <= rHi; r++) m[r] |= scratch[r];
                }
            }

            // Filled cross-section per station: what the nose has already covered,
            // intersected with what the tail still covers. Exact for a convex body,
            // and right in spirit for a car shell — a thin closed skin projects to a
            // ring at each station, and front-shadow AND rear-shadow fills the ring.
            var prefix = new ulong[Grid];
            var suffix = new ulong[Stations][];
            var acc = new ulong[Grid];
            for (int k = Stations - 1; k >= 0; k--)
            {
                for (int r = 0; r < Grid; r++) acc[r] |= station[k][r];
                suffix[k] = (ulong[])acc.Clone();
            }

            var areas = new float[Stations];
            const float cells = Grid * (float)Grid;
            for (int k = 0; k < Stations; k++)
            {
                int bits = 0;
                for (int r = 0; r < Grid; r++)
                {
                    prefix[r] |= station[k][r];
                    bits += CountBits(prefix[r] & suffix[k][r]);
                }
                areas[k] = bits / cells;
            }

            int frontBits = Popcount(front);
            int filledBits = Popcount(FillOutline(front));

            return new BodySilhouette
            {
                boundsMin = lo, boundsMax = hi, boundsSize = size,
                solidFront = frontBits / cells,
                solidSide = Popcount(side) / cells,
                solidPlan = Popcount(plan) / cells,
                porosity = filledBits <= 0 ? 0f : 1f - frontBits / (float)filledBits,
                station = areas, frontMask = front,
                triangles = n, source = source,
            };
        }

        /// <summary>
        /// The outline's own filled shadow: every row filled between its outermost
        /// set cells, every column likewise, and the two intersected.
        ///
        /// <b>Not a flood fill, deliberately.</b> A flood fill only finds holes
        /// that are fully enclosed, and a roll cage's gaps are mostly open to the
        /// side — the very shapes this exists to catch would report as solid. The
        /// row/column shadow is the same intersect-two-projections trick the
        /// station areas use, it needs no queue, and it answers the question that
        /// is actually being asked: how much of the space this shape reaches across
        /// does it occupy.
        ///
        /// A closed body scores near 1 against it, so porosity stays near zero for
        /// every car with panels — which is what keeps this inert for the shapes it
        /// is not about.
        /// </summary>
        private static ulong[] FillOutline(ulong[] mask)
        {
            var rowFill = new ulong[Grid];
            for (int r = 0; r < Grid; r++)
            {
                ulong v = mask[r];
                if (v == 0UL) continue;
                int lowBit = 0; while ((v & (1UL << lowBit)) == 0UL) lowBit++;
                int highBit = Grid - 1; while ((v & (1UL << highBit)) == 0UL) highBit--;
                for (int c = lowBit; c <= highBit; c++) rowFill[r] |= 1UL << c;
            }

            var colFill = new ulong[Grid];
            for (int c = 0; c < Grid; c++)
            {
                ulong bit = 1UL << c;
                int lowRow = -1, highRow = -1;
                for (int r = 0; r < Grid; r++)
                    if ((mask[r] & bit) != 0UL) { if (lowRow < 0) lowRow = r; highRow = r; }
                for (int r = lowRow; r >= 0 && r <= highRow; r++) colFill[r] |= bit;
            }

            var filled = new ulong[Grid];
            for (int r = 0; r < Grid; r++) filled[r] = rowFill[r] & colFill[r];
            return filled;
        }

        private static int StationOf(float f01) =>
            Mathf.Clamp(Mathf.FloorToInt(f01 * Stations), 0, Stations - 1);

        private static int Stamp(ulong[] mask, float ax, float ay, float bx, float by,
                                 float cx, float cy) =>
            Stamp(mask, ax, ay, bx, by, cx, cy, out _);

        /// <summary>
        /// Rasterise one projected triangle into a bitmask, returning the row range
        /// touched (<c>lo &gt; hi</c> when nothing was).
        ///
        /// Cell centres decide coverage, with the three vertex cells stamped
        /// unconditionally. That second part is not belt-and-braces: an FBX body is
        /// mostly long thin triangles, and a sliver that passes between two cell
        /// centres would otherwise contribute nothing at all — which on a hollow
        /// shell reads as a hole in the car.
        /// </summary>
        private static int Stamp(ulong[] mask, float ax, float ay, float bx, float by,
                                 float cx, float cy, out int rowHi)
        {
            int rowLo = Grid;
            rowHi = -1;

            ax *= Grid; ay *= Grid; bx *= Grid; by *= Grid; cx *= Grid; cy *= Grid;

            StampCell(mask, ax, ay, ref rowLo, ref rowHi);
            StampCell(mask, bx, by, ref rowLo, ref rowHi);
            StampCell(mask, cx, cy, ref rowLo, ref rowHi);

            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Mathf.Abs(area) < 1e-9f) return rowLo;   // degenerate: vertices only

            int c0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx))));
            int c1 = Mathf.Min(Grid - 1, Mathf.CeilToInt(Mathf.Max(ax, Mathf.Max(bx, cx))));
            int r0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy))));
            int r1 = Mathf.Min(Grid - 1, Mathf.CeilToInt(Mathf.Max(ay, Mathf.Max(by, cy))));

            float inv = 1f / area;
            for (int r = r0; r <= r1; r++)
            {
                float py = r + 0.5f;
                ulong bits = 0UL;
                for (int c = c0; c <= c1; c++)
                {
                    float px = c + 0.5f;
                    float w0 = ((bx - ax) * (py - ay) - (by - ay) * (px - ax)) * inv;
                    float w1 = ((cx - bx) * (py - by) - (cy - by) * (px - bx)) * inv;
                    float w2 = ((ax - cx) * (py - cy) - (ay - cy) * (px - cx)) * inv;
                    if (w0 >= 0f && w1 >= 0f && w2 >= 0f) bits |= 1UL << c;
                }
                if (bits == 0UL) continue;
                mask[r] |= bits;
                if (r < rowLo) rowLo = r;
                if (r > rowHi) rowHi = r;
            }
            return rowLo;
        }

        private static void StampCell(ulong[] mask, float x, float y,
                                      ref int rowLo, ref int rowHi)
        {
            int c = Mathf.FloorToInt(x), r = Mathf.FloorToInt(y);
            if (c < 0) c = 0; else if (c >= Grid) c = Grid - 1;
            if (r < 0) r = 0; else if (r >= Grid) r = Grid - 1;
            mask[r] |= 1UL << c;
            if (r < rowLo) rowLo = r;
            if (r > rowHi) rowHi = r;
        }

        private static int Popcount(ulong[] mask)
        {
            int n = 0;
            for (int i = 0; i < mask.Length; i++) n += CountBits(mask[i]);
            return n;
        }

        private static int CountBits(ulong v)
        {
            // Explicit rather than System.Numerics.BitOperations: this has to
            // compile against the scripting runtime the project actually targets.
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }
    }
}
