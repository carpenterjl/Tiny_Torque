using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;
using UnityEngine.Rendering;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// One deformable mesh per catalogue body — the editor's answer to "what am I
    /// sculpting", built from the same sources the game renders.
    ///
    /// <b>One mesh, not a hierarchy.</b> A shell in the game is a prefab with
    /// several MeshFilters (hull, cabin, glass, accents) or, for the two primitive
    /// compounds, a handful of scaled cubes. Neither shape can carry a blendshape
    /// or a single coherent vertex array, so this flattens the lot into one mesh
    /// in the body's own author units, applying exactly the transforms the
    /// renderer and <see cref="DragEstimator"/> apply — the manifest's author yaw
    /// and offset, and the child-to-root chain.
    ///
    /// <b>One mesh, but not one surface.</b> The flatten keeps each renderer group
    /// as its own SUBMESH, named by <see cref="FeatureChannels"/> — so
    /// <c>paint</c>, <c>chrome</c>, <c>glass</c> and <c>em_tail</c> survive as
    /// separately paintable channels while sharing one vertex array. That sharing
    /// is what matters: a blendshape delta, a weld group and a sculpt offset are
    /// all indices into vertices, and they stay valid however the triangles are
    /// divided up. Merging every group into one surface (which this did first)
    /// would have made per-feature paint impossible for the one body in the editor
    /// that most needs it.
    ///
    /// <b>Author units, with the scale handed back separately.</b> The mesh comes
    /// out at the size the FBX was authored at and
    /// <paramref name="renderScale"/> carries the divide
    /// (<see cref="CarVehicle.BodyRenderScale"/>) that turns it into metres. Same
    /// split the game uses, and the reason is the same: a deformation authored on
    /// a shell has to survive that shell being resized.
    ///
    /// Meshes are cached and handed out by <c>Instantiate</c>, so several editors
    /// (or several rebuilds) share one measurement of each body.
    /// </summary>
    public static class BodyMeshSource
    {
        private static readonly Dictionary<string, Mesh> _cache = new Dictionary<string, Mesh>();
        private static readonly Dictionary<string, string[]> _channels =
            new Dictionary<string, string[]>();
        private static List<BodyDef> _eligible;

        /// <summary>The channel name a body with no separable groups gets — the
        /// two primitive compounds, and any shell whose pieces all fold into
        /// one.</summary>
        public const string WholeBodyChannel = "body";

        /// <summary>
        /// Forget every built mesh, destroying the cached originals.
        ///
        /// Sibling of <c>PartMeshLibrary.ResetCache</c> and
        /// <c>DragEstimator.ResetCache</c>: a body whose FBX has just been
        /// re-imported was flattened from the old one a moment ago. Clones already
        /// handed out are unaffected — they are separate objects.
        /// </summary>
        public static void ResetCache()
        {
            foreach (var kv in _cache) DestroyMesh(kv.Value);
            _cache.Clear();
            _channels.Clear();
            _eligible = null;
        }

        /// <summary>
        /// The bodies this editor can open: every offered catalogue row whose
        /// geometry actually flattened into something. Probed once.
        ///
        /// A row can fail here for one honest reason — its FBX shipped with
        /// Read/Write disabled — and the right response is to leave it out of the
        /// picker rather than to open an empty stand.
        /// </summary>
        public static IReadOnlyList<BodyDef> Eligible()
        {
            if (_eligible != null) return _eligible;

            _eligible = new List<BodyDef>();
            foreach (BodyDef def in BodyCatalog.Offered)
            {
                Mesh m = Build(def, out _);
                if (m != null) _eligible.Add(def);
            }
            if (_eligible.Count == 0)
                Debug.LogError("[BodyMeshSource] No catalogue body could be flattened into a " +
                               "deformable mesh — check that the body FBXs still import with " +
                               "Read/Write enabled.");
            return _eligible;
        }

        /// <summary>
        /// The shared source mesh for a body, in author units, or null when its
        /// geometry cannot be read. Callers that intend to deform it must
        /// <c>Instantiate</c> the result — this one is the cached original.
        /// </summary>
        public static Mesh Build(BodyDef def, out Vector3 renderScale)
        {
            renderScale = RenderScaleOf(def);
            string key = KeyOf(def);
            if (_cache.TryGetValue(key, out Mesh cached)) return cached;

            string[] channels = null;
            Mesh built = def != null && !string.IsNullOrEmpty(def.meshKey)
                ? BuildFromPrefab(def.meshKey, out channels)
                : null;
            // The renderer's own fallback: a catalogue body whose FBX did not ship
            // draws the primitive its row nominates, so it should be sculptable as
            // one too.
            if (built == null)
            {
                built = BuildFromPrimitives(def == null ? BodyShape.Box : def.legacy);
                channels = new[] { WholeBodyChannel };
            }

            if (built != null)
            {
                built.name = "bodyed_" + key;
                built.hideFlags = HideFlags.HideAndDontSave;   // runtime-owned, never an asset
            }
            _cache[key] = built;
            _channels[key] = built != null ? channels : null;
            return built;
        }

        /// <summary>
        /// The paintable channels of a body's flattened mesh, one per submesh and
        /// index-aligned with them. Never null for a body that built; empty only
        /// for one that did not.
        /// </summary>
        public static string[] ChannelsOf(BodyDef def)
        {
            Build(def, out _);   // fills the cache if this body has not been opened yet
            string key = KeyOf(def);
            return _channels.TryGetValue(key, out string[] c) && c != null
                ? c : System.Array.Empty<string>();
        }

        private static string KeyOf(BodyDef def) =>
            def == null ? "prim:Box"
            : !string.IsNullOrEmpty(def.meshKey) ? "mesh:" + def.meshKey
            : "prim:" + def.legacy;

        /// <summary>Metres per author unit for this body at its nominal size —
        /// whatever <see cref="CarVehicle"/> would instantiate it at, so the
        /// editor and the game agree about how big the thing is.</summary>
        public static Vector3 RenderScaleOf(BodyDef def)
        {
            Vector3 nominal = def != null && def.nominalSize.sqrMagnitude > 1e-9f
                ? def.nominalSize
                : CarVehicle.BodyMeshAuthorSize;
            return CarVehicle.BodyRenderScale(def, nominal);
        }

        // ---- the FBX path -----------------------------------------------------------

        /// <summary>
        /// Flatten an authored shell by reading the source prefab — never by
        /// instantiating one, for the same reason <c>DragEstimator.MeasureMesh</c>
        /// does not: this runs on whatever frame the editor opens, and a throwaway
        /// GameObject on that frame buys nothing.
        /// </summary>
        private static Mesh BuildFromPrefab(string meshKey, out string[] channels)
        {
            channels = null;
            var src = Resources.Load<GameObject>(PartMeshLibrary.PartRoot + meshKey);
            if (src == null) return null;

            AssetManifest man = AssetManifests.Load(meshKey, PartMeshLibrary.PartRoot);
            float yaw = man != null ? man.authorYawDeg : 0f;
            Vector3 off = man != null ? man.AuthorOffset : Vector3.zero;
            Quaternion yawRot = Mathf.Abs(yaw) > 0.01f
                ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.identity;
            // Same composition DragEstimator applies: child→root, then the
            // manifest's offset, then its yaw. Written as one matrix because
            // CombineMeshes takes matrices.
            Matrix4x4 fix = Matrix4x4.Rotate(yawRot) * Matrix4x4.Translate(off);

            var combine = new List<CombineInstance>();
            // Channel of each CombineInstance, index-aligned, plus the order the
            // channels were first seen — which is the prefab's own child order, so
            // the picker lists a body's features the way the artist laid them out.
            var instChannel = new List<string>();
            var order = new List<string>();
            long verts = 0;
            bool unreadable = false;

            foreach (MeshFilter mf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                if (!m.isReadable) { unreadable = true; continue; }

                string channel = FeatureChannels.NameOf(mf.gameObject.name);
                if (!order.Contains(channel)) order.Add(channel);

                Matrix4x4 toRoot = fix * LocalToRoot(mf.transform, src.transform);
                for (int s = 0; s < m.subMeshCount; s++)
                {
                    combine.Add(new CombineInstance
                    {
                        mesh = m, subMeshIndex = s, transform = toRoot,
                    });
                    instChannel.Add(channel);
                    verts += m.vertexCount;
                }
            }

            if (unreadable)
                Debug.LogWarning($"[BodyMeshSource] {meshKey} has a mesh with Read/Write " +
                                 "disabled — that part cannot be flattened, so it will be " +
                                 "missing from the deformable body.");
            if (combine.Count == 0) return null;

            Mesh mesh = Weld(combine, verts, "mesh " + meshKey, instChannel, order);
            channels = mesh != null ? order.ToArray() : null;
            return mesh;
        }

        /// <summary>Child-to-prefab-root matrix, walked by hand rather than read
        /// off <c>localToWorldMatrix</c>: a prefab asset's transforms are in no
        /// scene, and "world" there is not a thing worth trusting.</summary>
        private static Matrix4x4 LocalToRoot(Transform t, Transform root)
        {
            Matrix4x4 m = Matrix4x4.identity;
            for (Transform c = t; c != null && c != root; c = c.parent)
                m = Matrix4x4.TRS(c.localPosition, c.localRotation, c.localScale) * m;
            return m;
        }

        /// <summary>
        /// Combine the pieces into one vertex array, then re-cut the triangles into
        /// one submesh per channel.
        ///
        /// <b>Combined WITHOUT merging submeshes, then regrouped</b> — rather than
        /// combining each channel separately and combining those. The difference is
        /// that this way the vertex array is produced exactly once, in one order,
        /// by one call; regrouping only moves index lists about. Combining per
        /// channel and then again would build the vertices twice and make their
        /// final order a property of two nested combines, which is precisely the
        /// thing every sculpt offset in every saved layout is an index into.
        /// </summary>
        private static Mesh Weld(List<CombineInstance> combine, long verts, string what,
                                 List<string> instChannel, List<string> order)
        {
            var mesh = new Mesh();
            // Set BEFORE combining: the format decides whether the combine can
            // address the result at all, and a shell with accents can pass 65 k.
            if (verts > 60000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.CombineMeshes(combine.ToArray(), false, true);

            if (mesh.vertexCount == 0)
            {
                Debug.LogWarning($"[BodyMeshSource] {what} flattened to an empty mesh.");
                DestroyMesh(mesh);
                return null;
            }

            Regroup(mesh, instChannel, order);

            // Recomputed rather than carried over from the FBX, so the shading a
            // body has before its first sculpt is the same shading every sculpt
            // produces. Splits in the vertex array survive, so hard edges do.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Re-cut a just-combined mesh's per-instance submeshes into one submesh
        /// per channel, in first-seen order. Vertices are untouched.
        /// </summary>
        private static void Regroup(Mesh mesh, List<string> instChannel, List<string> order)
        {
            int n = Mathf.Min(mesh.subMeshCount, instChannel.Count);
            var buckets = new List<int>[order.Count];
            for (int c = 0; c < buckets.Length; c++) buckets[c] = new List<int>();

            var scratch = new List<int>(1024);
            for (int s = 0; s < n; s++)
            {
                int c = order.IndexOf(instChannel[s]);
                if (c < 0) continue;
                mesh.GetTriangles(scratch, s);
                buckets[c].AddRange(scratch);
            }

            mesh.subMeshCount = buckets.Length;
            for (int c = 0; c < buckets.Length; c++)
                mesh.SetTriangles(buckets[c], c, false);   // bounds recalculated below
        }

        // ---- the primitive path -----------------------------------------------------

        /// <summary>
        /// Tessellate one of the primitive compounds at the nominal author size,
        /// so the box and the wedge are sculptable like anything else.
        ///
        /// <b>At the nominal size, and then scaled</b> — the same treatment the
        /// FBX path gets, and for the reason <c>DragEstimator.MeasurePrimitive</c>
        /// gives: a piece carries a rotation, Unity scales before it rotates, and
        /// a stretched compound is therefore not a stretched picture of the
        /// nominal one.
        ///
        /// Faces are emitted with their own four vertices rather than eight shared
        /// corners. That is what gives a box crisp edges under
        /// <c>RecalculateNormals</c> — and the weld map in
        /// <see cref="DeformFalloff"/> puts the three copies of each corner back
        /// together for sculpting, so the split costs nothing there.
        /// </summary>
        private static Mesh BuildFromPrimitives(BodyShape shape)
        {
            Vector3 s = CarVehicle.BodyMeshAuthorSize;
            var verts = new List<Vector3>();
            var tris = new List<int>();

            foreach (BodyPiece p in BodyPrimitives.For(shape))
                AddBox(verts, tris,
                       Vector3.Scale(p.pos, s),
                       Vector3.Scale(p.scale, s) * 0.5f,
                       Quaternion.Euler(p.euler));

            if (verts.Count == 0) return null;

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The six axis faces, as (outward normal, u, v) with
        /// <c>Cross(u, v) == n</c> — which makes the winding below front-facing
        /// from outside under Unity's convention.</summary>
        private static readonly Vector3[] FaceAxes =
        {
            new Vector3( 1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1),
            new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0,-1),
            new Vector3(0, 1, 0),  new Vector3(0, 0, 1), new Vector3(1, 0, 0),
            new Vector3(0,-1, 0),  new Vector3(0, 0, 1), new Vector3(-1, 0, 0),
            new Vector3(0, 0, 1),  new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0, 0,-1),  new Vector3(1, 0, 0), new Vector3(0,-1, 0),
        };

        private static void AddBox(List<Vector3> verts, List<int> tris,
                                   Vector3 centre, Vector3 halfExtents, Quaternion rot)
        {
            for (int f = 0; f < 6; f++)
            {
                Vector3 n = FaceAxes[f * 3], u = FaceAxes[f * 3 + 1], v = FaceAxes[f * 3 + 2];
                Vector3 hn = Vector3.Scale(n, halfExtents);
                Vector3 hu = Vector3.Scale(u, halfExtents);
                Vector3 hv = Vector3.Scale(v, halfExtents);

                int b = verts.Count;
                verts.Add(rot * (hn - hu - hv) + centre);
                verts.Add(rot * (hn + hu - hv) + centre);
                verts.Add(rot * (hn + hu + hv) + centre);
                verts.Add(rot * (hn - hu + hv) + centre);

                tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        /// <summary>Destroy a mesh from either an edit-mode bench or a running
        /// scene. <c>Destroy</c> is a no-op outside play mode and would leak the
        /// object; <c>DestroyImmediate</c> is illegal during play.</summary>
        public static void DestroyMesh(Mesh m)
        {
            if (m == null) return;
            if (Application.isPlaying) Object.Destroy(m);
            else Object.DestroyImmediate(m);
        }
    }
}
