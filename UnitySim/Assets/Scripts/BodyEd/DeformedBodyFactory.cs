using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Builds a customised body for something that is not going to be edited — the
    /// car on the track, a garage preview, a thumbnail.
    ///
    /// <b>The morphs are baked into the vertices, and no SkinnedMeshRenderer is
    /// involved.</b> The editor needs blendshapes because the weights move while
    /// somebody drags a slider; a driving car's shape is fixed the moment it
    /// spawns. Baking gives that car a plain MeshFilter and MeshRenderer with no
    /// per-frame skinning, no bone-less-renderer edge cases on the shipping path,
    /// and — because a blendshape's contribution is defined as
    /// <c>weight/100 × delta</c> summed over frames, which is the sum computed here
    /// — geometry identical to what <c>BakeMesh</c> produces in the studio. The two
    /// paths cannot disagree about the car's shape, which is the claim the whole
    /// test-drive round trip rests on.
    ///
    /// <b>Order matters and matches the editor exactly:</b> free-form offsets are
    /// added to the base vertices FIRST, because a blendshape delta is defined
    /// against the base mesh and is unaffected by them; then the weighted deltas
    /// are added on top. That is the same <c>base + offsets + Σ(wᵢ·Δᵢ)</c> the
    /// editor draws.
    /// </summary>
    public static class DeformedBodyFactory
    {
        /// <summary>
        /// A new mesh in the body's AUTHOR units, with this layout's deformation
        /// baked in and its hidden channels removed. Null when the body's geometry
        /// cannot be read — the caller then falls back to the ordinary shell.
        ///
        /// The caller owns the mesh and must destroy it.
        /// </summary>
        public static Mesh BuildMesh(BodyDef def, VehicleLayoutData layout, out string[] channels)
        {
            channels = System.Array.Empty<string>();
            Mesh src = BodyMeshSource.Build(def, out _);
            if (src == null) return null;

            channels = BodyMeshSource.ChannelsOf(def);
            var mesh = Object.Instantiate(src);
            mesh.name = "deformed_" + (def != null ? def.id : "body");
            mesh.hideFlags = HideFlags.HideAndDontSave;

            if (layout != null && layout.HasDeformation) Deform(mesh, layout);
            if (layout != null) Hide(mesh, channels, layout.hiddenChannels);

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Deform(Mesh mesh, VehicleLayoutData layout)
        {
            Vector3[] baseVerts = mesh.vertices;
            var verts = new Vector3[baseVerts.Length];
            System.Array.Copy(baseVerts, verts, baseVerts.Length);

            // Free-form offsets. A layout whose vertex count no longer matches is
            // refused outright rather than partly applied, for the reason
            // DeformableBody.Apply gives: an index into a re-exported mesh does not
            // address the vertex it used to, and half-applying scatters the body.
            if (layout.offsetIndex != null && layout.offsetValue != null)
            {
                if (layout.baseVertexCount > 0 && layout.baseVertexCount != baseVerts.Length)
                {
                    Debug.LogError($"[DeformedBodyFactory] Layout was sculpted on a mesh with " +
                                   $"{layout.baseVertexCount} vertices; this one has " +
                                   $"{baseVerts.Length}. Vertex offsets dropped; morphs applied.");
                }
                else
                {
                    int n = Mathf.Min(layout.offsetIndex.Length, layout.offsetValue.Length);
                    for (int i = 0; i < n; i++)
                    {
                        int vi = layout.offsetIndex[i];
                        if (vi < 0 || vi >= verts.Length) continue;
                        verts[vi] += layout.offsetValue[i];
                    }
                }
            }

            // Morph weights, matched BY NAME against this build's morph list — the
            // same rule the editor loads by, so a layout that survives a reorder
            // there survives it here.
            if (layout.blendShapeWeights != null)
            {
                Bounds b = BodyMorphs.BoundsOf(baseVerts);
                for (int i = 0; i < layout.blendShapeWeights.Length; i++)
                {
                    float w = layout.blendShapeWeights[i];
                    if (Mathf.Abs(w) < 1e-4f) continue;

                    string name = layout.blendShapeNames != null && i < layout.blendShapeNames.Length
                        ? layout.blendShapeNames[i] : null;
                    if (!TryKind(name, i, out MorphKind kind)) continue;

                    Vector3[] d = BodyMorphs.Deltas(baseVerts, b, kind);
                    float k = w / 100f;
                    for (int v = 0; v < verts.Length && v < d.Length; v++) verts[v] += d[v] * k;
                }
            }

            mesh.vertices = verts;
        }

        private static bool TryKind(string name, int fallbackIndex, out MorphKind kind)
        {
            if (!string.IsNullOrEmpty(name))
                return System.Enum.TryParse(name, out kind);
            // A nameless legacy layout: positional is the only reading available.
            if (fallbackIndex >= 0 && fallbackIndex < BodyMorphs.All.Length)
            {
                kind = BodyMorphs.All[fallbackIndex];
                return true;
            }
            kind = default;
            return false;
        }

        private static void Hide(Mesh mesh, string[] channels, string[] hidden)
        {
            if (hidden == null || hidden.Length == 0 || channels == null) return;
            for (int c = 0; c < channels.Length && c < mesh.subMeshCount; c++)
            {
                if (System.Array.IndexOf(hidden, channels[c]) < 0) continue;
                mesh.SetTriangles(System.Array.Empty<int>(), c, false);
            }
        }

        /// <summary>
        /// Build a customised body as a plain renderer under
        /// <paramref name="parent"/>, scaled to metres, painted, and returned for
        /// the caller to keep.
        ///
        /// <paramref name="paintMat"/> is the material every channel starts on —
        /// the car's tintable body material — so an unpainted deformed car is the
        /// colour its design says it is.
        /// </summary>
        public static GameObject Build(Transform parent, BodyDef def, VehicleLayoutData layout,
                                       Vector3 bodySize, Material paintMat, int layer,
                                       out Mesh owned)
        {
            owned = BuildMesh(def, layout, out string[] channels);
            if (owned == null) return null;

            var go = new GameObject("DeformedBody");
            go.transform.SetParent(parent, false);
            go.layer = layer;

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = owned;

            var mr = go.AddComponent<MeshRenderer>();
            int slots = Mathf.Max(1, owned.subMeshCount);
            var mats = new Material[slots];
            for (int i = 0; i < slots; i++) mats[i] = paintMat;
            mr.sharedMaterials = mats;

            // The design's own size divide, not the nominal one: a deformed body
            // still answers to the design's bodySize the way an undeformed shell
            // does, so stretching a car in the garage keeps working.
            go.transform.localScale = CarVehicle.BodyRenderScale(def, bodySize);

            if (layout?.tints != null && layout.tints.Length > 0)
            {
                FeatureChannels.Binding binding = FeatureChannels.ForSubmeshes(mr, channels);
                binding.Apply(layout.tints);
                // The binding's materials are owned by the renderer from here on;
                // it is the caller's teardown of the GameObject that frees them.
            }
            return go;
        }

        /// <summary>
        /// Mount a layout's parts as cosmetic children of a vehicle root.
        ///
        /// <b>Visual only, exactly like the antennas and light clusters beside
        /// them.</b> No mass, no collider, no aero: a part is decoration until
        /// somebody decides what it should weigh, and quietly changing a car's
        /// inertia because it now wears a spoiler would be a change to how it
        /// drives that no gate would catch.
        /// </summary>
        public static List<GameObject> BuildProps(Transform parent, VehicleLayoutData layout,
                                                  int layer = PartVisualFactory.VizLayer)
        {
            var built = new List<GameObject>();
            if (layout?.props == null) return built;

            foreach (PropPlacement spec in layout.props)
            {
                if (spec == null) continue;
                var go = new GameObject(string.IsNullOrEmpty(spec.label) ? "prop" : spec.label);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = spec.localPos;
                go.transform.localRotation = Quaternion.Euler(spec.euler);
                go.transform.localScale = spec.Scale;
                go.layer = layer;

                GameObject geom = StudioPartLibrary.Build(go.transform, spec.source, layer);
                if (geom != null && spec.tints != null && spec.tints.Length > 0)
                {
                    FeatureChannels.Binding binding = FeatureChannels.ForHierarchy(go.transform);
                    binding.Apply(spec.tints);
                }
                built.Add(go);
            }
            return built;
        }
    }
}
