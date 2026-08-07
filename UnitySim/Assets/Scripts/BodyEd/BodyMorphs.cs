using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>The four shape families the morph sliders drive.</summary>
    public enum MorphKind
    {
        /// <summary>Widen or narrow the front third about the centreline.</summary>
        NoseWidth,
        /// <summary>Cut the tail down and in — a Kammback, taken too far.</summary>
        TailChop,
        /// <summary>Drop everything above the waistline. A chopped roof.</summary>
        RooflineDrop,
        /// <summary>Pull the flanks in at the waist, leaving nose and tail.</summary>
        SidePinch,
    }

    /// <summary>
    /// The blendshape targets, generated from the body's own geometry.
    ///
    /// <b>Why procedural rather than authored.</b> Nothing in this project ships a
    /// blendshape: <c>PartModelPostprocessor</c> sets <c>importBlendShapes =
    /// false</c> for every model, and the FBX bodies were exported without any.
    /// Turning that on would mean a version bump and a full reimport of the body
    /// set — a change to the shared asset pipeline — to get morphs that only this
    /// editor uses. Generating them instead costs one pass over the vertex array
    /// per body and works on every shell in the catalogue, including the two
    /// primitive compounds that are not FBX at all and never could carry one.
    ///
    /// <b>Every delta is a pure function of vertex POSITION</b>, in the mesh's own
    /// bounding box. Three things follow from that, and all three are load-bearing:
    /// co-located vertices get identical deltas, so a morph can never tear a seam
    /// the way a hand-picked vertex set could; the same base mesh always yields
    /// the same frames, which is what lets a save file store four weights instead
    /// of a megabyte of displacement; and the shapes are proportional to the
    /// body's extents, so one definition reads sensibly on a 0.42 m shell and on
    /// a 4.5 m car.
    ///
    /// <b>Deltas, not positions.</b> Unity stores a blendshape frame relative to
    /// the mesh's base vertices, which is exactly why free-form sculpting can
    /// write into those base vertices without invalidating a single frame — see
    /// <see cref="DeformableBody"/> for the argument in full.
    /// </summary>
    public static class BodyMorphs
    {
        /// <summary>Every morph, in slider order. The blendshape's NAME is the
        /// enum name, and that is the key a save file matches on — so this array
        /// may be reordered or extended without invalidating a layout on disk.</summary>
        public static readonly MorphKind[] All =
        {
            MorphKind.NoseWidth, MorphKind.TailChop,
            MorphKind.RooflineDrop, MorphKind.SidePinch,
        };

        /// <summary>Panel text. Free to change — it is not the save key.</summary>
        public static string Label(MorphKind k)
        {
            switch (k)
            {
                case MorphKind.NoseWidth:    return "Nose width";
                case MorphKind.TailChop:     return "Tail chop";
                case MorphKind.RooflineDrop: return "Roofline";
                case MorphKind.SidePinch:    return "Side pinch";
                default:                     return k.ToString();
            }
        }

        /// <summary>
        /// Add one frame per <see cref="All"/> entry to a mesh, and return the
        /// names in the order they were added — which is the order
        /// <c>SetBlendShapeWeight</c> indexes them by.
        ///
        /// The frames go on at weight 100, so a slider's 0..1 maps onto Unity's
        /// 0..100 with no scaling anywhere else. Normals and tangents are left
        /// null: the renderer recomputes normals after every deformation anyway
        /// (see <see cref="DeformableBody"/>), and a stale interpolated normal
        /// would be worse than a recomputed one.
        /// </summary>
        public static string[] AddTo(Mesh mesh, Vector3[] baseVerts)
        {
            if (mesh == null || baseVerts == null || baseVerts.Length == 0)
                return new string[0];

            Bounds b = BoundsOf(baseVerts);
            var names = new string[All.Length];
            for (int i = 0; i < All.Length; i++)
            {
                names[i] = All[i].ToString();
                mesh.AddBlendShapeFrame(names[i], 100f, Deltas(baseVerts, b, All[i]), null, null);
            }
            return names;
        }

        /// <summary>The bounding box of a vertex array, computed here rather than
        /// read off <c>mesh.bounds</c> so the deltas depend on nothing but the
        /// numbers passed in — a mesh whose bounds have been recalculated after a
        /// sculpt must still regenerate identical frames.</summary>
        public static Bounds BoundsOf(Vector3[] verts)
        {
            if (verts == null || verts.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            Vector3 lo = verts[0], hi = verts[0];
            for (int i = 1; i < verts.Length; i++)
            {
                lo = Vector3.Min(lo, verts[i]);
                hi = Vector3.Max(hi, verts[i]);
            }
            var bounds = new Bounds();
            bounds.SetMinMax(lo, hi);
            return bounds;
        }

        /// <summary>
        /// One morph's displacement field, evaluated per vertex.
        ///
        /// Nose is +Z throughout the project, so <c>nz</c> below runs 0 at the
        /// tail to 1 at the nose. Amplitudes are fractions of the body's own
        /// extents, and each is windowed by a smoothstep so the morph blends into
        /// the untouched part of the shell instead of leaving a step.
        /// </summary>
        public static Vector3[] Deltas(Vector3[] verts, Bounds b, MorphKind kind)
        {
            int n = verts != null ? verts.Length : 0;
            var d = new Vector3[n];
            if (n == 0) return d;

            Vector3 min = b.min, size = b.size, mid = b.center;
            float sx = Mathf.Max(1e-6f, size.x);
            float sy = Mathf.Max(1e-6f, size.y);
            float sz = Mathf.Max(1e-6f, size.z);

            for (int i = 0; i < n; i++)
            {
                Vector3 v = verts[i];
                float nz = (v.z - min.z) / sz;          // 0 tail .. 1 nose
                float ny = (v.y - min.y) / sy;          // 0 floor .. 1 roof
                float ox = v.x - mid.x;                 // signed half-width offset

                switch (kind)
                {
                    case MorphKind.NoseWidth:
                    {
                        // Front third only, scaling about the centreline: a body
                        // widened this way keeps its section shape.
                        float w = Window(nz, 0.62f, 1.0f);
                        d[i] = new Vector3(0.35f * ox * w, 0f, 0f);
                        break;
                    }
                    case MorphKind.TailChop:
                    {
                        // Rear quarter, and only above the floor line — cutting
                        // the underside away would open the body, not chop it.
                        float w = Window(1f - nz, 0.70f, 1.0f);
                        float upper = Mathf.Clamp01((ny - 0.25f) / 0.75f);
                        d[i] = new Vector3(-0.22f * ox * w,
                                           -0.28f * sy * w * upper,
                                           0f);
                        break;
                    }
                    case MorphKind.RooflineDrop:
                    {
                        // Everything above the waist comes down, proportional to
                        // how far above it started, so the waistline stays put.
                        float w = Mathf.Clamp01((ny - 0.45f) / 0.55f);
                        d[i] = new Vector3(0f, -0.45f * sy * w * w * (3f - 2f * w), 0f);
                        break;
                    }
                    case MorphKind.SidePinch:
                    {
                        // Waisted: narrowest at mid-length, easing off toward nose
                        // and tail.
                        //
                        // The 0.35 FLOOR is not decoration. A window that reaches
                        // zero at both ends is exactly zero on a body whose only
                        // vertices are its end caps — which is the primitive box,
                        // the first row in the picker. [BDEF] caught that as a
                        // dead slider on the default body. The floor makes the
                        // morph narrow the whole car a little and its waist a lot,
                        // which is both a shape somebody would want and one that
                        // every body can express.
                        float w = 0.35f + 0.65f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(nz));
                        d[i] = new Vector3(-0.30f * ox * w, 0f, 0f);
                        break;
                    }
                }
            }
            return d;
        }

        /// <summary>Smoothstep from 0 at <paramref name="a"/> to 1 at
        /// <paramref name="c"/>, flat outside.</summary>
        private static float Window(float t, float a, float c)
        {
            float s = Mathf.Clamp01((t - a) / Mathf.Max(1e-6f, c - a));
            return s * s * (3f - 2f * s);
        }
    }
}
