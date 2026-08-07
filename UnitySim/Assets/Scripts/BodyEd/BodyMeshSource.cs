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
    /// and offset, and the child-to-root chain. Flattening loses the per-piece
    /// materials, which is precisely what makes the triplanar body material the
    /// right choice rather than a compromise: there is one surface now.
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
        private static List<BodyDef> _eligible;

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
            string key = def == null ? "prim:Box"
                       : !string.IsNullOrEmpty(def.meshKey) ? "mesh:" + def.meshKey
                       : "prim:" + def.legacy;

            if (_cache.TryGetValue(key, out Mesh cached)) return cached;

            Mesh built = def != null && !string.IsNullOrEmpty(def.meshKey)
                ? BuildFromPrefab(def.meshKey)
                : null;
            // The renderer's own fallback: a catalogue body whose FBX did not ship
            // draws the primitive its row nominates, so it should be sculptable as
            // one too.
            built ??= BuildFromPrimitives(def == null ? BodyShape.Box : def.legacy);

            if (built != null)
            {
                built.name = "bodyed_" + key;
                built.hideFlags = HideFlags.HideAndDontSave;   // runtime-owned, never an asset
            }
            _cache[key] = built;
            return built;
        }

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
        private static Mesh BuildFromPrefab(string meshKey)
        {
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
            long verts = 0;
            bool unreadable = false;

            foreach (MeshFilter mf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                if (!m.isReadable) { unreadable = true; continue; }

                Matrix4x4 toRoot = fix * LocalToRoot(mf.transform, src.transform);
                for (int s = 0; s < m.subMeshCount; s++)
                {
                    combine.Add(new CombineInstance
                    {
                        mesh = m, subMeshIndex = s, transform = toRoot,
                    });
                    verts += m.vertexCount;
                }
            }

            if (unreadable)
                Debug.LogWarning($"[BodyMeshSource] {meshKey} has a mesh with Read/Write " +
                                 "disabled — that part cannot be flattened, so it will be " +
                                 "missing from the deformable body.");
            if (combine.Count == 0) return null;

            return Weld(combine, verts, "mesh " + meshKey);
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

        private static Mesh Weld(List<CombineInstance> combine, long verts, string what)
        {
            var mesh = new Mesh();
            // Set BEFORE combining: the format decides whether the combine can
            // address the result at all, and a shell with accents can pass 65 k.
            if (verts > 60000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.CombineMeshes(combine.ToArray(), true, true);

            if (mesh.vertexCount == 0)
            {
                Debug.LogWarning($"[BodyMeshSource] {what} flattened to an empty mesh.");
                DestroyMesh(mesh);
                return null;
            }

            // Recomputed rather than carried over from the FBX, so the shading a
            // body has before its first sculpt is the same shading every sculpt
            // produces. Splits in the vertex array survive, so hard edges do.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
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
