using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Loads authored Blender part meshes (exported as FBX under
    /// <c>Assets/Resources/PartModels/</c>) and instantiates them as cosmetic-only
    /// hierarchies for the vehicle. This is the project's single asset-backed
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

        private const string ResourceRoot = "PartModels/";

        // Cache the loaded source prefabs. Misses are cached as null so a missing
        // asset is only probed once per session.
        private static readonly Dictionary<string, GameObject> _cache = new Dictionary<string, GameObject>();

        /// <summary>True when an asset for <paramref name="key"/> is present.</summary>
        public static bool Has(string key) => Enabled && Load(key) != null;

        private static GameObject Load(string key)
        {
            if (!_cache.TryGetValue(key, out var src))
            {
                src = Resources.Load<GameObject>(ResourceRoot + key);
                _cache[key] = src;
            }
            return src;
        }

        /// <summary>
        /// Instantiate the mesh for <paramref name="key"/> under <paramref name="parent"/>
        /// (identity local pose), stripped of colliders/rigidbodies. Every child is put
        /// on <paramref name="layer"/> (default the Ignore-Raycast viz layer, matching
        /// the primitive part builders; pass the car's own layer for the body so its
        /// camera-culling behaviour is unchanged). Returns the instance root, or null
        /// when disabled or the asset is missing (caller should then build primitives).
        /// </summary>
        public static GameObject TryInstantiate(string key, Transform parent, int layer = PartVisualFactory.VizLayer)
        {
            if (!Enabled) return null;
            var src = Load(key);
            if (src == null) return null;

            var go = Object.Instantiate(src, parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            Sanitise(go, layer);
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
        /// </summary>
        public static void AssignByName(GameObject root, Material fallback,
                                        params (string token, Material mat)[] map)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name.ToLowerInvariant();
                Material chosen = fallback;
                foreach (var (token, mat) in map)
                    if (n.Contains(token)) { chosen = mat; break; }
                if (chosen != null) r.sharedMaterial = chosen;
            }
        }
    }
}
