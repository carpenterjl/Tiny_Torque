using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Loads authored Blender meshes (exported as FBX under
    /// <c>Assets/Resources/PartModels/</c> for vehicle parts and
    /// <c>Assets/Resources/TrackProps/</c> for track scenery) and instantiates them
    /// as cosmetic-only hierarchies. This is the project's single asset-backed
    /// visual path; everything else is still runtime-procedural. Callers try a
    /// mesh here first and fall back to <see cref="PartVisualFactory"/> primitives
    /// when the asset is absent, so the game runs unchanged before any FBX is
    /// imported and every pre-existing design keeps its procedural look.
    ///
    /// Instances are hard-sanitised on load: every Collider and Rigidbody is
    /// stripped and the whole hierarchy is forced onto <see cref="PartVisualFactory.VizLayer"/>
    /// (Ignore Raycast) so imported geometry can never touch physics, block garage
    /// placement raycasts, or be photographed by the on-car camera sensor (which
    /// culls that layer). Authoring convention: metres, +Y up / -Z forward, and
    /// wheels with their axle along +X — matching the primitive builders.
    /// </summary>
    public static class PartMeshLibrary
    {
        /// <summary>Master switch. Turn off to force the primitive fallback everywhere
        /// (A/B testing, or reverting to the pre-asset look without deleting files).</summary>
        public static bool Enabled = true;

        /// <summary>Vehicle part meshes (wheels, bodies, battery, antenna).</summary>
        public const string PartRoot = "PartModels/";

        /// <summary>Track scenery/arcade prop meshes. Separate folder because props
        /// have no runtime scale contract and are validated on extent + tri budget.</summary>
        public const string PropRoot = "TrackProps/";

        // Cache the loaded source prefabs, keyed root+key. Misses are cached as
        // null so a missing asset is only probed once per session.
        private static readonly Dictionary<string, GameObject> _cache = new Dictionary<string, GameObject>();

        /// <summary>True when an asset for <paramref name="key"/> is present.</summary>
        public static bool Has(string key, string root = PartRoot) => Enabled && Load(key, root) != null;

        /// <summary>
        /// Forget every probed key, hits and misses alike.
        ///
        /// <b>The misses are the point.</b> A miss is cached forever, so a key that
        /// was absent when something first asked for it stays absent for the rest of
        /// the session — and "the rest of the session" includes the moment right
        /// after an editor tool has just imported the FBX that key names. Without
        /// this, committing an asset and then looking at it requires restarting
        /// Unity, and the asset appears broken until someone does.
        ///
        /// <c>TiguanMaterials.ResetCache</c> and
        /// <c>AssetManifests.ResetCache</c> are the other two halves; Asset Studio's
        /// commit pipeline calls all three together.
        /// </summary>
        public static void ResetCache() => _cache.Clear();

        private static GameObject Load(string key, string root)
        {
            string path = root + key;
            if (!_cache.TryGetValue(path, out var src))
            {
                src = Resources.Load<GameObject>(path);
                _cache[path] = src;
                // Say so once. A miss is cached forever, so without this a
                // mistyped or unshipped key is silent for the whole session and
                // shows up only as a part that mysteriously is not there.
                if (src == null)
                    Debug.LogWarning($"[PartMeshLibrary] no mesh at Resources/{path} " +
                                     "— falling back to primitives (or to nothing).");
            }
            return src;
        }

        /// <summary>
        /// Instantiate the mesh for <paramref name="key"/> under <paramref name="parent"/>
        /// (identity local pose), stripped of colliders/rigidbodies. Every child is put
        /// on <paramref name="layer"/> (default the Ignore-Raycast viz layer, matching
        /// the primitive part builders; pass the car's own layer for the body so its
        /// camera-culling behaviour is unchanged, and the parent's layer for track
        /// props so the on-car camera sensor can actually see the scenery). Returns
        /// the instance root, or null when disabled or the asset is missing (caller
        /// should then build primitives).
        ///
        /// When the asset ships a manifest, the instance is stamped with a
        /// <see cref="PartManifestBinding"/> naming the key — <b>this is the whole
        /// of how the manifest path reaches its call sites</b>. The key is known
        /// here and nowhere else; the materials are known at the binding call and
        /// nowhere else. Writing it down where it is known beats threading it
        /// through the eight call sites that instantiate a mesh and then bind it,
        /// any one of which could forget and would then silently bind a manifest
        /// asset by substring. An asset with no manifest is stamped with nothing,
        /// which is why the 207 shipped ones cannot tell this line is here.
        ///
        /// One residual hole, stated rather than papered over: the stamp only
        /// reaches a binder that is actually CALLED, and the scenery sites
        /// (<c>TrackCatalog</c>, <c>ArcadeVfx</c>) skip <see cref="AssignByName"/>
        /// entirely when their token array is empty — a prop shipping a manifest
        /// would be stamped and never bound. Props are out of Asset Studio's v1
        /// scope, so no such asset can exist yet; that path has to make the call
        /// unconditionally before one can. Every VEHICLE site — body, wheel,
        /// battery, antenna, light cluster, cosmetic — binds unconditionally and
        /// is covered.
        ///
        /// <b>The manifest's yaw is applied here, and here only.</b> It is a
        /// property of the ASSET — "this mesh was modelled long-axis along X, and
        /// the game builds every car facing +Z" — not of any one caller, so the
        /// place that knows the key is the place that owes the quarter turn.
        /// Every caller that follows sets <c>localScale</c> and none sets
        /// rotation, except the wheel path's half-turn, which composes with it.
        /// An asset with no manifest, or one with no yaw, keeps the identity
        /// rotation this always wrote.
        /// </summary>
        public static GameObject TryInstantiate(string key, Transform parent,
            int layer = PartVisualFactory.VizLayer, string root = PartRoot)
        {
            if (!Enabled) return null;
            var src = Load(key, root);
            if (src == null) return null;

            var go = Object.Instantiate(src, parent, false);
            go.transform.localPosition = Vector3.zero;

            AssetManifest man = AssetManifests.Load(key, root);
            go.transform.localRotation = man != null && Mathf.Abs(man.authorYawDeg) > 0.01f
                ? Quaternion.Euler(0f, man.authorYawDeg, 0f)
                : Quaternion.identity;

            Sanitise(go, layer);
            AssetManifestBinder.TryStamp(go, key, root);
            return go;
        }

        /// <summary>Strip colliders + rigidbodies and force the given layer, recursively.</summary>
        private static void Sanitise(GameObject root, int layer)
        {
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true)) Object.Destroy(rb);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
        }

        /// <summary>
        /// Assign shared materials to child renderers by object-name token (case-
        /// insensitive substring). Lets a multi-object mesh (e.g. a wheel with a
        /// "tire" and a "rim" object) pick up the shared <see cref="PartVisualFactory"/>
        /// materials so lighting/theme/recolour stay one system; the FBX's own
        /// materials are ignored. Renderers matching no token keep the first mat as a
        /// default when <paramref name="fallback"/> is supplied.
        ///
        /// Returns the <see cref="MaterialBindings"/> the walk produced — which
        /// renderer took which material, and which token won. <b>Every caller
        /// ignores it today</b>; it exists because the manifest path binds by
        /// object name and explicit submesh slot and needs the binder to say
        /// what it touched. Recording is free: the token that won is already in
        /// hand when the assignment happens.
        ///
        /// <b>A manifest asset never reaches the loop below.</b> If the instance
        /// carries a <see cref="PartManifestBinding"/> — stamped by
        /// <see cref="TryInstantiate"/> when the asset ships a manifest — the whole
        /// call goes to <see cref="AssetManifestBinder"/> instead, which binds by
        /// object name and slot index and consults no token at all. The two ways of
        /// deciding a material must not be mixed on one asset: a manifest that has
        /// covered forty of forty-one objects and a token table that quietly covers
        /// the forty-first is exactly the "renders almost right" failure the
        /// manifest exists to end.
        /// </summary>
        public static MaterialBindings AssignByName(GameObject root, Material fallback,
                                                    params (string token, Material mat)[] map)
        {
            var manifest = root != null ? root.GetComponent<PartManifestBinding>() : null;
            // No paint material: a wheel, a cosmetic or a prop has no design colour
            // to tint with, so a paintChannel material there is an ordinary shared
            // one. Only the body path has a _bodyMat to hand over.
            if (manifest != null) return AssetManifestBinder.Bind(manifest, null, null);

            var bindings = new MaterialBindings();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name.ToLowerInvariant();
                Material chosen = fallback;
                string won = null;
                foreach (var (token, mat) in map)
                    if (n.Contains(token)) { chosen = mat; won = token; break; }
                if (chosen != null) r.sharedMaterial = chosen;
                // Slot 0, and only slot 0: sharedMaterial IS slot 0, so a
                // two-material object keeps whatever the import left in its
                // second slot. Recorded as such rather than glossed over —
                // that gap is exactly what the manifest path closes.
                bindings.Add(r, 0, chosen, won,
                             won != null ? BindSource.Token : BindSource.Fallback);
            }
            return bindings;
        }
    }
}
