using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// The JSON document <c>scripts/export_unity.py</c> writes beside each
    /// circuit's FBX files.
    ///
    /// Shaped for <see cref="JsonUtility"/>, which is why there is not a
    /// dictionary or a ragged array anywhere in it: the exporter flattens the
    /// spine into one float array with a stride, and turns every map into a list
    /// of records with a key field. That constraint lives on the Blender side so
    /// that this side needs no JSON parser of its own.
    ///
    /// <b>Every coordinate in here is in Blender space</b> — Z-up, right-handed,
    /// metres. That is deliberate (UNITY_EXPORT.md §7 rule 1): converting axes in
    /// two places is how you get a circuit that is mirrored only in the trees.
    /// <see cref="CircuitAxis"/> is the one place it happens.
    /// </summary>
    [Serializable]
    public sealed class CircuitManifest
    {
        public int schema;
        public string circuit;          // "spa"
        public string title;            // "Circuit de Spa-Francorchamps"
        public string units;            // "metres"
        public string axis;             // "blender_z_up"
        public string generator;
        public string manifest_hash;

        public float lap_length;        // plan length, metres
        public float lap_3d;            // measured along the surface
        public float official_m;        // the published figure it is checked against
        public bool clockwise;
        public float[] elevation;       // [min, max] metres ASL

        public MeshEntry[] world = Array.Empty<MeshEntry>();
        public MeshEntry[] split = Array.Empty<MeshEntry>();
        public ProtoEntry[] prototypes = Array.Empty<ProtoEntry>();
        public Instance[] instances = Array.Empty<Instance>();
        public MaterialEntry[] materials = Array.Empty<MaterialEntry>();
        public string[] tree_leaf_materials = Array.Empty<string>();

        public string[] spine_fields = Array.Empty<string>();
        public int spine_stride;
        public float[] spine = Array.Empty<float>();

        public PitInfo pit;
        public GridSlot[] grid = Array.Empty<GridSlot>();
        public Counts counts;
        public Drop[] drops = Array.Empty<Drop>();
        public Verify verify;

        /// <summary>One exported mesh whose vertices are already where they
        /// belong. Instantiated at the origin with no transform — the pivot is
        /// the world origin by design, which is correct for a road and fatal for
        /// a prop, hence the split between this and <see cref="ProtoEntry"/>.</summary>
        [Serializable]
        public sealed class MeshEntry
        {
            public string name;
            public string fbx;          // relative to the circuit folder
            public string collider;     // "mesh" | "none"
            public string group;        // TRACK, TERRAIN, BARRIER, ...
            public string label;        // grandstand name, when it has one
            public int verts;
            public int tris;
            public string[] materials = Array.Empty<string>();
        }

        /// <summary>A rigid prop, re-centred on its own footprint, placed by
        /// every <see cref="Instance"/> that names it.</summary>
        [Serializable]
        public sealed class ProtoEntry
        {
            public string key;          // "tree_near_conifer_r3", "marshal_L"
            public string fbx;
            public int verts;
            public int tris;
            public string[] materials = Array.Empty<string>();
        }

        [Serializable]
        public sealed class Instance
        {
            public string proto;
            public float[] p;           // Blender position
            public float[] q;           // Blender quaternion, w x y z
            public float s;             // uniform scale
            public int mat;             // foliage tone 1-3 for trees, else 0
        }

        [Serializable]
        public sealed class MaterialEntry
        {
            public string name;         // "M_TrkAsphalt"
            public float[] rgb;         // linear
            public float metallic;
            public float smoothness;
            public bool probed;
        }

        [Serializable]
        public sealed class PitInfo
        {
            public bool present;
            public string side;         // "left" | "right"
            public float s_in, s_out, s_split, s_merge, span_m;
            public float garage_s0, garage_s1, garage_m, standoff_m;
        }

        [Serializable]
        public sealed class GridSlot
        {
            public int slot;
            public float s;
            public float[] p;
            public float heading_deg;
        }

        [Serializable]
        public sealed class Counts
        {
            public int world, split, prototypes, instances;
            public int world_tris, split_tris;
            public PerProto[] per_prototype = Array.Empty<PerProto>();
        }

        [Serializable]
        public sealed class PerProto
        {
            public string proto;
            public int count;
        }

        [Serializable]
        public sealed class Drop
        {
            public string reason;
            public int count;
        }

        [Serializable]
        public sealed class Verify
        {
            public int checked_count;   // see Load(): the JSON field is `checked`
            public float worst_mm;
            public float worst_axial_mm;
            public string worst_at;
        }

        // ---- loading ---------------------------------------------------------

        /// <summary>Read a manifest from an absolute or project-relative path.
        /// Returns null and logs the reason.</summary>
        public static CircuitManifest Load(string path)
        {
            string abs = Path.IsPathRooted(path) ? path : PackPaths.ToAbsolute(path);
            if (!File.Exists(abs))
            {
                CircuitPaths.Err("no manifest at " + abs);
                return null;
            }
            string json = File.ReadAllText(abs);
            // `checked` is a C# keyword, so the field cannot be named after it.
            json = json.Replace("\"checked\":", "\"checked_count\":");
            CircuitManifest m;
            try { m = JsonUtility.FromJson<CircuitManifest>(json); }
            catch (Exception e)
            {
                CircuitPaths.Err(abs + ": " + e.Message);
                return null;
            }
            if (m == null || string.IsNullOrEmpty(m.circuit))
            {
                CircuitPaths.Err(abs + ": parsed to nothing — is it a circuit manifest?");
                return null;
            }
            return m;
        }

        public Dictionary<string, MaterialEntry> MaterialTable()
        {
            var d = new Dictionary<string, MaterialEntry>();
            foreach (var e in materials)
                if (e != null && !string.IsNullOrEmpty(e.name)) d[e.name] = e;
            return d;
        }

        /// <summary>Station count in the flattened spine array.</summary>
        public int SpineCount => spine_stride > 0 ? spine.Length / spine_stride : 0;

        /// <summary>One spine field by station index, e.g. <c>Spine(i, 3)</c> for z.
        /// Field order is in <see cref="spine_fields"/>.</summary>
        public float Spine(int station, int field) =>
            spine[station * spine_stride + field];
    }
}
