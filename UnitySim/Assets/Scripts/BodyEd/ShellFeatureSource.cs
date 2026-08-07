using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Lifts one feature out of a body shell so it can be worn by another.
    ///
    /// <b>This is where "geometry addition" gets its geometry.</b> The project
    /// ships thirteen shells and no parts library, but each shell is a hierarchy of
    /// named renderer groups — the police car's push bar, mirrors, spotlights,
    /// wipers and light bar; the buggy's tube frame; every car's headlights, tail
    /// lights and glass. Those pieces were modelled as parts in Blender
    /// (<c>build_lights</c>, <c>build_aero</c>, <c>build_scoop</c>) and joined per
    /// material on the way out, so the group is the finest cut the shipped assets
    /// support. Harvesting them costs nothing to author and gives the parts palette
    /// a hundred entries on day one.
    ///
    /// <b>By culling an instance, not by rebuilding a mesh.</b> The obvious route —
    /// combine the group's meshes into one — would have to reproduce the material
    /// binding, the manifest's author offset and yaw, and the submesh split, and
    /// would then be a second implementation of geometry the game already knows how
    /// to build. Instantiating the shell through the same call the car uses and
    /// deleting what is not wanted cannot disagree with the car about what a push
    /// bar looks like.
    ///
    /// <b>Recentred on itself.</b> A piece keeps its position within the shell as an
    /// offset, and the object it is handed back in has its pivot at the piece's own
    /// centre — so the gizmo grabs the part rather than the origin of the car it
    /// came from, and "put it back where it was" is one remembered vector.
    /// </summary>
    public static class ShellFeatureSource
    {
        /// <summary>One harvestable group.</summary>
        public struct Feature
        {
            /// <summary>The <c>BodyDef.id</c> it lives on.</summary>
            public string bodyKey;
            /// <summary>Its <see cref="FeatureChannels"/> name.</summary>
            public string channel;
            public string label;
            public int triangles;
            /// <summary>Its size in metres, at the body's own render scale — what
            /// the palette shows so a 4 cm badge is not offered as if it were a
            /// roof.</summary>
            public Vector3 sizeM;
            /// <summary>Where its centre sits in the body's local frame, in metres.
            /// Placing a harvested piece here puts it exactly back.</summary>
            public Vector3 homeM;
        }

        private static readonly Dictionary<string, List<Feature>> _cache =
            new Dictionary<string, List<Feature>>();

        /// <summary>Forget every probed shell. Sibling of
        /// <c>BodyMeshSource.ResetCache</c>, for the same reason: a re-imported FBX
        /// was probed from the old one.</summary>
        public static void ResetCache() => _cache.Clear();

        /// <summary>
        /// Every group this shell can give up, largest first.
        ///
        /// Probed once per body by building it and reading the result back. A body
        /// with no FBX (the primitive compounds) has nothing to harvest and returns
        /// an empty list rather than a fabricated one.
        /// </summary>
        public static IReadOnlyList<Feature> Features(BodyDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.meshKey))
                return System.Array.Empty<Feature>();
            if (_cache.TryGetValue(def.id, out var hit)) return hit;

            var list = new List<Feature>();
            var probe = new GameObject("shellfeature_probe")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            probe.SetActive(false);   // never rendered, never ticked

            try
            {
                GameObject inst = Instantiate(def, probe.transform);
                if (inst != null)
                {
                    // Measured through the same rotation and scale the car renders
                    // this shell at, so "home" is a position in the CAR's frame and
                    // the size is the size on the car.
                    inst.transform.localScale = BodyMeshSource.RenderScaleOf(def);
                    var groups = Group(inst.transform);
                    foreach (var kv in groups)
                    {
                        Bounds b = BoundsOf(inst.transform, kv.Value);
                        int tris = 0;
                        foreach (Renderer r in kv.Value) tris += TrianglesOf(r);
                        list.Add(new Feature
                        {
                            bodyKey = def.id,
                            channel = kv.Key,
                            label = FeatureChannels.Label(kv.Key),
                            sizeM = Abs(PartToRoot(inst.transform, b.size)),
                            homeM = PartToRoot(inst.transform, b.center),
                            triangles = tris,
                        });
                    }
                }
            }
            finally
            {
                Kill(probe);
            }

            list.Sort((a, b) => b.triangles.CompareTo(a.triangles));
            _cache[def.id] = list;
            return list;
        }

        /// <summary>
        /// Build one feature under <paramref name="parent"/>, at the identity local
        /// pose with its pivot at its own centre. Null when the shell or the
        /// channel is not there — the caller then draws nothing, the same
        /// never-a-crash rule the cosmetics and the track props follow.
        /// </summary>
        public static GameObject Build(Transform parent, string bodyKey, string channel, int layer)
        {
            BodyDef def = BodyCatalog.ById(bodyKey);
            if (def == null || string.IsNullOrEmpty(def.meshKey) || string.IsNullOrEmpty(channel))
                return null;

            var root = new GameObject("feature_" + channel);
            root.transform.SetParent(parent, false);
            root.layer = layer;

            GameObject inst = Instantiate(def, root.transform, layer);
            if (inst == null) { Kill(root); return null; }

            // Collect what is kept BEFORE deleting anything: Destroy defers to the
            // end of the frame in play mode, so a bounds pass taken after it would
            // still see every piece of the car.
            var keep = new List<Renderer>();
            var drop = new List<GameObject>();
            foreach (Renderer r in inst.GetComponentsInChildren<Renderer>(true))
            {
                if (FeatureChannels.NameOf(r.gameObject.name) == channel) keep.Add(r);
                else drop.Add(r.gameObject);
            }

            if (keep.Count == 0)
            {
                Debug.LogWarning($"[ShellFeatureSource] '{bodyKey}' has no feature named " +
                                 $"'{channel}' — nothing to build.");
                Kill(root);
                return null;
            }

            Bounds local = BoundsOf(inst.transform, keep);
            foreach (GameObject go in drop) Kill(go);

            // Scale to metres the way the car renders this shell, then slide the
            // instance so the kept piece straddles the root's origin.
            //
            // <b>Through the instance's own ROTATION, not just its scale.</b>
            // PartMeshLibrary.TryInstantiate applies the manifest's author yaw to
            // this transform — "this mesh was modelled long-axis along X, and the
            // game builds every car facing +Z" — so local→parent is R·S·p, and an
            // offset that only divided out S lands the pivot a quarter turn away
            // from the part. [BDEF] caught exactly that on the police car, whose
            // yaw is the only non-zero one in the catalogue.
            root.transform.localScale = Vector3.one;
            inst.transform.localScale = BodyMeshSource.RenderScaleOf(def);
            inst.transform.localPosition = -PartToRoot(inst.transform, local.center);
            return root;
        }

        /// <summary>Where a feature sits on its own body, in metres — so "reset to
        /// where it came from" is exact rather than eyeballed.</summary>
        public static bool TryHome(string bodyKey, string channel, out Vector3 homeM)
        {
            homeM = Vector3.zero;
            BodyDef def = BodyCatalog.ById(bodyKey);
            if (def == null) return false;
            foreach (Feature f in Features(def))
                if (f.channel == channel) { homeM = f.homeM; return true; }
            return false;
        }

        // ---- internals ---------------------------------------------------------------

        /// <summary>
        /// A point in the instance's local space, expressed in its parent's — the
        /// composition Unity applies, scale then rotation, and the one thing a
        /// hand-written pivot correction has to get right.
        /// </summary>
        private static Vector3 PartToRoot(Transform inst, Vector3 local) =>
            inst.localRotation * Vector3.Scale(local, inst.localScale);

        /// <summary>A SIZE through a rotation can come out with negative
        /// components — a 90° yaw swaps two extents and flips a sign. An extent is
        /// a magnitude, so the sign is noise.</summary>
        private static Vector3 Abs(Vector3 v) =>
            new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static GameObject Instantiate(BodyDef def, Transform parent,
                                              int layer = PartVisualFactory.VizLayer)
        {
            GameObject inst = PartMeshLibrary.TryInstantiate(def.meshKey, parent, layer);
            if (inst == null) return null;
            // The car's own binder, so a harvested piece wears the material the car
            // would have given it. A shell with a tintable channel gets the studio's
            // neutral paint there; everything else keeps its authored accent.
            CarVehicle.BindBodyMesh(inst, def, BodyEdMaterials.ShellPaint(), null);
            return inst;
        }

        private static Dictionary<string, List<Renderer>> Group(Transform root)
        {
            var map = new Dictionary<string, List<Renderer>>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                string c = FeatureChannels.NameOf(r.gameObject.name);
                if (!map.TryGetValue(c, out var list)) map[c] = list = new List<Renderer>();
                list.Add(r);
            }
            return map;
        }

        /// <summary>
        /// Bounds of a renderer list in <paramref name="frame"/>'s local space.
        ///
        /// Its own pass rather than <c>PartVisualFactory.LocalRendererBounds</c>
        /// for two reasons. That one takes a subtree and would measure the whole
        /// car, where the question here is about a subset of one — and it reads
        /// <c>Renderer.bounds</c>, which is a rendering-pipeline value that a
        /// renderer sitting under a deactivated probe object has never been asked
        /// to produce. This walks the MESH bounds through the transform chain
        /// instead, which is arithmetic and gives the same answer whether the
        /// object is in a scene, disabled, or three frames from being destroyed.
        /// </summary>
        private static Bounds BoundsOf(Transform frame, List<Renderer> renderers)
        {
            bool any = false;
            var result = new Bounds();
            Matrix4x4 toFrame = frame.worldToLocalMatrix;

            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                       : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (m == null) continue;

                Bounds lb = m.bounds;
                Matrix4x4 mtx = toFrame * r.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? lb.min.x : lb.max.x,
                        (c & 2) == 0 ? lb.min.y : lb.max.y,
                        (c & 4) == 0 ? lb.min.z : lb.max.z);
                    Vector3 p = mtx.MultiplyPoint3x4(corner);
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                    else result.Encapsulate(p);
                }
            }
            return result;
        }

        private static int TrianglesOf(Renderer r)
        {
            Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                   : r.GetComponent<MeshFilter>()?.sharedMesh;
            if (m == null) return 0;
            int n = 0;
            for (int s = 0; s < m.subMeshCount; s++) n += (int)(m.GetIndexCount(s) / 3);
            return n;
        }

        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
