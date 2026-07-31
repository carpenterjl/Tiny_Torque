using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// The circuit palette, as Built-in-RP Standard materials.
    ///
    /// <b>Every number here comes from the manifest.</b> The exporter walks each
    /// Blender material's node tree to its Principled BSDF and reads base
    /// colour, metallic and roughness off it — resolving a two-tone mix to its
    /// mean and a colour ramp to the average of its stops — so this file has no
    /// colour table in it. That is the pack's standing rule ("the generator
    /// clones them; it never retypes a number"), and it is what keeps the
    /// circuits in step when the Blender palette is retuned.
    ///
    /// UNITY_EXPORT.md §6 recommends triplanar URP shaders. This project is
    /// Built-in RP, so that recommendation does not apply as written; these are
    /// flat Standard materials, which is honest about what a probed mean colour
    /// can deliver. What it costs is visible and worth stating: the kerbs are a
    /// red/white serration in Blender and arrive here as one dull red, and the
    /// two-tone ground noise arrives as its average. The road ribbon carries a
    /// UV layer named <c>trk</c> (u = station in metres, v = lateral offset), so
    /// a striped kerb and a racing-line wear mask are both reachable from the
    /// data already in the FBX — see the note in <c>KerbUvHint</c>.
    /// </summary>
    public static class CircuitMaterials
    {
        /// <summary>Colours are probed in Blender's linear space, which is what
        /// Unity wants when the project is linear — and this one is.</summary>
        private static Shader _standard;

        private static Shader Standard =>
            _standard != null ? _standard : (_standard = Shader.Find("Standard"));

        /// <summary>Generate/refresh every material the manifest names. Returns
        /// name → material for the scene builder to bind by.</summary>
        public static Dictionary<string, Material> Generate(CircuitManifest man)
        {
            PackPaths.EnsureFolder(CircuitPaths.MaterialsDir);
            var made = new Dictionary<string, Material>();
            if (Standard == null)
            {
                CircuitPaths.Err("the Standard shader did not resolve — a headless "
                                 + "run needs a graphics device (no -nographics)");
                return made;
            }

            foreach (var e in man.materials)
            {
                if (e == null || string.IsNullOrEmpty(e.name)) continue;
                string path = CircuitPaths.MaterialsDir + "/" + e.name + ".mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(Standard) { name = e.name };
                    AssetDatabase.CreateAsset(m, path);
                }
                m.shader = Standard;
                m.SetColor("_Color", ToColor(e.rgb));
                m.SetFloat("_Metallic", Mathf.Clamp01(e.metallic));
                m.SetFloat("_Glossiness", Mathf.Clamp01(e.smoothness));
                EditorUtility.SetDirty(m);
                made[e.name] = m;
            }
            return made;
        }

        private static Color ToColor(float[] rgb) =>
            rgb == null || rgb.Length < 3
                ? Color.grey
                : new Color(rgb[0], rgb[1], rgb[2], 1f);

        /// <summary>The material list for one exported mesh, in submesh order.
        ///
        /// A missing name is a hole in the palette, not a reason to leave the
        /// renderer with a null slot — a null slot renders as Unity's magenta
        /// error shader and reads as "the import failed", so the fallback is a
        /// neutral grey and a counted warning.</summary>
        public static Material[] Bind(string[] names,
                                      Dictionary<string, Material> table,
                                      ref int missing)
        {
            if (names == null || names.Length == 0)
                return new[] { Fallback(table) };
            var outMats = new Material[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]) &&
                    table.TryGetValue(names[i], out var m) && m != null)
                {
                    outMats[i] = m;
                }
                else
                {
                    outMats[i] = Fallback(table);
                    missing++;
                }
            }
            return outMats;
        }

        private static Material _fallback;

        private static Material Fallback(Dictionary<string, Material> table)
        {
            if (_fallback != null) return _fallback;
            string path = CircuitPaths.MaterialsDir + "/M_TrkMissing.mat";
            _fallback = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (_fallback == null && Standard != null)
            {
                _fallback = new Material(Standard) { name = "M_TrkMissing" };
                _fallback.SetColor("_Color", new Color(0.45f, 0.45f, 0.45f));
                _fallback.SetFloat("_Glossiness", 0.2f);
                AssetDatabase.CreateAsset(_fallback, path);
            }
            return _fallback;
        }

        public static void ResetCaches()
        {
            _standard = null;
            _fallback = null;
        }

        /// <summary>
        /// Not called — a note in the one place someone improving the look will
        /// read. The road, kerb and run-off FBXs carry a UV set named
        /// <c>trk</c> in metres: <c>u</c> is the station along the lap and
        /// <c>v</c> is the lateral offset from the centreline. A kerb texture of
        /// 900 mm red/white blocks is therefore a tiling of 1/0.9 in u with no
        /// unwrapping at all, and a racing-line wear mask is a function of v.
        /// That is real, usable data and the reason the exporter does not strip
        /// UVs from meshes that have no other use for them.
        /// </summary>
        public const string KerbUvHint = "trk";
    }
}
