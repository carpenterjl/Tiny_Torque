using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Headless check on the Blender-authored part meshes: loads each FBX via
    /// Resources, measures its world-space renderer bounds, and compares them
    /// against the size every asset is contractually authored to. This guards the
    /// two failure modes this pipeline actually hits — the FBX exporter's
    /// metre→centimetre bake importing everything 100x oversized, and an asset
    /// drifting off its authored dimensions during a re-model.
    ///
    /// Run with (editor must be closed):
    ///   Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.PartModelValidator.Report -logFile &lt;log&gt;
    /// then grep the log for "[PMV] RESULT".
    /// </summary>
    public static class PartModelValidator
    {
        private const float TolMetres = 0.002f;   // +-2 mm

        /// <summary>Expected renderer-bounds size in metres plus a triangle budget.
        /// A null axis is deliberately unconstrained: body height is free, wheel
        /// width varies with tread, and the buggy's flares exceed the core width.</summary>
        private readonly struct Spec
        {
            public readonly string Key;
            public readonly float? X, Y, Z;
            public readonly int MaxTris;
            public Spec(string key, float? x, float? y, float? z, int maxTris)
            { Key = key; X = x; Y = y; Z = z; MaxTris = maxTris; }
        }

        private static readonly Spec[] Specs =
        {
            // Wheels: axle along X, outer tyre radius 33 mm -> 66 mm across.
            new Spec("wheel_slick",   null,   0.066f, 0.066f, 4000),
            new Spec("wheel_knobby",  null,   0.066f, 0.066f, 4000),
            new Spec("wheel_rally",   null,   0.066f, 0.066f, 4000),
            // Bodies: width and length are pinned because the runtime divides by
            // CarVehicle.BodyMeshAuthorSize; height is free.
            new Spec("body_shell",    0.200f, null,   0.420f, 6500),
            new Spec("body_lowracer", 0.200f, null,   0.420f, 6500),
            new Spec("body_buggy",    null,   null,   0.420f, 6500),
            // Battery renders at authored size - no runtime scale at all.
            new Spec("battery_stick", 0.047f, 0.025f, 0.138f, 800),
            new Spec("antenna_stub",  null,   null,   null,   600),
        };

        public static void Report()
        {
            int fail = 0;
            foreach (var s in Specs)
            {
                var src = Resources.Load<GameObject>("PartModels/" + s.Key);
                if (src == null)
                {
                    Debug.LogError($"[PMV] FAIL {s.Key}: missing asset");
                    fail++; continue;
                }

                var inst = Object.Instantiate(src);
                var rs = inst.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0)
                {
                    Debug.LogError($"[PMV] FAIL {s.Key}: no renderers");
                    Object.DestroyImmediate(inst); fail++; continue;
                }

                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

                int tris = 0;
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;

                string why = "";
                if (Off(b.size.x, s.X)) why += $" X={b.size.x:0.000}!={s.X:0.000}";
                if (Off(b.size.y, s.Y)) why += $" Y={b.size.y:0.000}!={s.Y:0.000}";
                if (Off(b.size.z, s.Z)) why += $" Z={b.size.z:0.000}!={s.Z:0.000}";
                if (tris > s.MaxTris) why += $" tris={tris}>{s.MaxTris}";

                string line = $"{s.Key}: parts={rs.Length} tris={tris} " +
                              $"size=({b.size.x:0.000},{b.size.y:0.000},{b.size.z:0.000}) " +
                              $"center=({b.center.x:0.000},{b.center.y:0.000},{b.center.z:0.000})";
                if (why.Length == 0) Debug.Log($"[PMV] PASS {line}");
                else { Debug.LogError($"[PMV] FAIL {line} -{why}"); fail++; }

                Object.DestroyImmediate(inst);
            }
            Debug.Log($"[PMV] RESULT {(fail == 0 ? "ALL PASS" : fail + " FAILED")} " +
                      $"({Specs.Length} assets)");
        }

        private static bool Off(float actual, float? expected) =>
            expected.HasValue && Mathf.Abs(actual - expected.Value) > TolMetres;
    }
}
