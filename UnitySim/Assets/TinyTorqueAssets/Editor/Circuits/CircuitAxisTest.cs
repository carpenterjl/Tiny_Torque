using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// UNITY_EXPORT.md §2, executed.
    ///
    /// Blender exports an L-shaped marker twice: once as a prototype at the
    /// origin, and once with a known rotation and translation baked into its
    /// vertices. This imports both, applies <see cref="CircuitAxis"/> to the
    /// manifest's transform, and asserts the two land on top of each other.
    ///
    /// Two independent things are checked, and the marker is shaped to separate
    /// them:
    ///
    /// <list type="bullet">
    /// <item><b>The mesh conversion.</b> Each arm of the marker has a different
    /// length (10, 4, 6 m) and extends in the positive direction only, so the
    /// prototype's own bounding box says which Unity axis each Blender axis
    /// became and whether any of them got flipped. A mirrored import passes
    /// every "does it look right" test and fails this one.</item>
    /// <item><b>The transform conversion.</b> Prototype × manifest transform
    /// versus the baked copy. This is the half that a hand-derived quaternion
    /// swap gets wrong by a sign, and the failure mode — props reflected about
    /// their own axis — is invisible on a tree and obvious on a marshal's post,
    /// which means it ships.</item>
    /// </list>
    ///
    /// They are tested together because they only ever fail <i>relative</i> to
    /// each other: an FBX importer setting and this file can both be "wrong" in
    /// the same direction and produce a correct scene.
    /// </summary>
    public static class CircuitAxisTest
    {
        private const float Tol = 0.002f;      // 2 mm, well inside FBX float noise

        [Serializable]
        private sealed class MarkerDoc
        {
            public string prototype;
            public string baked;
            public Inst instance;

            [Serializable]
            public sealed class Inst
            {
                public float[] p;
                public float[] q;
                public float yaw_deg;
                public float scale;
            }
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/0. Verify axis convention", priority = 200)]
        public static void RunMenu()
        {
            if (Run(out string report)) CircuitPaths.Log(report);
            else CircuitPaths.Err(report);
        }

        /// <summary>Returns true on pass. <paramref name="report"/> always carries
        /// the measured numbers, because "it failed" without them costs an hour.</summary>
        public static bool Run(out string report)
        {
            if (!CircuitImport.CopyMarker())
            {
                report = "axis test: no marker export. Run\n"
                       + "  blender -b --factory-startup -P scripts/export_unity.py -- --marker";
                return false;
            }
            AssetDatabase.Refresh();

            string dir = CircuitPaths.Root + "/_Marker";
            string json = File.ReadAllText(PackPaths.ToAbsolute(dir + "/marker.json"));
            var doc = JsonUtility.FromJson<MarkerDoc>(json);
            var proto = CircuitImport.LoadMesh(dir + "/marker_proto.fbx");
            var baked = CircuitImport.LoadMesh(dir + "/marker_placed.fbx");
            if (doc == null || proto == null || baked == null)
            {
                report = "axis test: marker assets did not import from " + dir;
                return false;
            }

            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // -- 1. the mesh conversion, read straight off the prototype -------
            // Blender built arms of 10 m along +X, 4 m along +Y and 6 m along +Z,
            // all starting at the origin. After the swap, Unity should see
            // 10 on X, 6 on Y (Blender Z) and 4 on Z (Blender Y), every minimum
            // at zero.
            Bounds b = proto.bounds;
            Vector3 min = b.min, size = b.size;
            var wantSize = new Vector3(10f, 6f, 4f);
            sb.AppendFormat("prototype extent ({0:F3}, {1:F3}, {2:F3}) min ({3:F3}, {4:F3}, {5:F3})",
                            size.x, size.y, size.z, min.x, min.y, min.z);
            if (Vector3.Distance(size, wantSize) > 0.01f)
            {
                ok = false;
                sb.AppendFormat("\n  FAIL extent should be ({0}, {1}, {2}) — Blender's "
                                + "10 m/+X, 4 m/+Y, 6 m/+Z arms did not land on "
                                + "X/Z/Y. The FBX axis settings and the importer "
                                + "disagree.", wantSize.x, wantSize.y, wantSize.z);
            }
            if (min.magnitude > 0.01f)
            {
                ok = false;
                sb.Append("\n  FAIL every arm was built in the POSITIVE direction "
                          + "only, so a non-zero minimum means an axis was flipped "
                          + "— the model is mirrored.");
            }

            // -- 1b. winding ----------------------------------------------------
            // The marker is three closed boxes, so its signed volume is positive
            // if and only if the faces wind outward. An axis conversion with a
            // negative determinant turns a solid inside out, and an inside-out
            // circuit is invisible from outside — you see the far side of the
            // road through the near side and it reads as a lighting bug.
            //
            // "Positive means outward" is a claim about the handedness of the
            // coordinate system, so it is measured rather than reasoned about: a
            // Unity primitive is wound the way Unity wants by definition, and
            // whatever sign it produces is what correct looks like here. This
            // costs one cube and removes the last hand-derived sign in the file.
            var cal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            float unit = SignedVolume(cal.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(cal);
            sb.AppendFormat("\ncalibration: Unity's own cube reads {0:F3} m³", unit);
            if (unit <= 0f)
            {
                ok = false;
                sb.Append("\n  FAIL a correctly wound Unity primitive measured "
                          + "negative, so this formula's sign convention is "
                          + "inverted and every winding verdict below it is "
                          + "backwards. Fix SignedVolume, not the export.");
            }

            float vol = SignedVolume(proto);
            sb.AppendFormat("\nsigned volume {0:F1} m³", vol);
            if (vol * unit <= 0f)
            {
                ok = false;
                sb.Append("\n  FAIL faces wind inward — flip REVERSE_WINDING in "
                          + "scripts/export_unity.py and re-export.\n"
                          + "  Before changing it, note that this marker is only "
                          + "ground truth if build_marker() winds outward itself. "
                          + "It did not once, which is how a flip that inverted "
                          + "all three circuits passed this line.");
            }

            // -- 2. the transform conversion ------------------------------------
            Vector3 p = CircuitAxis.Position(doc.instance.p);
            Quaternion q = CircuitAxis.Rotation(doc.instance.q);
            var trs = Matrix4x4.TRS(p, q, Vector3.one * doc.instance.scale);

            Vector3[] pv = proto.vertices, bv = baked.vertices;
            sb.AppendFormat("\nplaced at ({0:F2}, {1:F2}, {2:F2}) yaw {3:F1}° "
                            + "→ Unity euler ({4:F2}, {5:F2}, {6:F2})",
                            p.x, p.y, p.z, doc.instance.yaw_deg,
                            q.eulerAngles.x, q.eulerAngles.y, q.eulerAngles.z);
            if (pv.Length != bv.Length)
            {
                ok = false;
                sb.AppendFormat("\n  FAIL {0} prototype vertices vs {1} baked",
                                pv.Length, bv.Length);
            }
            else
            {
                // Nearest-neighbour, not index-paired: the two FBX files went
                // through the exporter separately and nothing promises they kept
                // the same vertex order.
                float worst = 0f;
                foreach (var v in pv)
                {
                    Vector3 t = trs.MultiplyPoint3x4(v);
                    float best = float.MaxValue;
                    foreach (var w in bv) best = Mathf.Min(best, Vector3.Distance(t, w));
                    worst = Mathf.Max(worst, best);
                }
                sb.AppendFormat("\nworst vertex disagreement {0:F4} m", worst);
                if (worst > Tol)
                {
                    ok = false;
                    sb.Append("\n  FAIL the manifest transform does not reproduce "
                              + "the placement Blender baked. If the extent check "
                              + "above passed, the mesh conversion is right and "
                              + "the QUATERNION conversion in CircuitAxis is "
                              + "wrong — most likely the sign, which reflects "
                              + "every prop about its own axis.");
                }
            }

            report = "axis test " + (ok ? "PASS" : "FAIL") + " — " + sb;
            return ok;
        }

        /// <summary>Signed volume of a closed mesh, by the divergence theorem
        /// over its triangles. Positive means outward-facing.</summary>
        private static float SignedVolume(Mesh m)
        {
            var v = m.vertices;
            var t = m.triangles;
            double sum = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                sum += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
            return (float)sum;
        }
    }
}
