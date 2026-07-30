using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Step 5 of the pack pipeline: the two debug scenes.
    ///
    /// These follow <c>SceneBuilderMenu</c>'s create-and-save pattern with ONE
    /// deliberate omission: they are never registered in Build Settings. The game
    /// ships five scenes and must keep shipping exactly five — <see
    /// cref="PackValidator"/> asserts it. That is what "don't add it to the game
    /// yet" means in a form a script can check.
    ///
    ///   • <b>TTA_Kit</b> — every prefab in the pack, laid out in labelled rows by
    ///     category with spacing taken from each item's own bounds, so a 5 cm
    ///     hydrant and a 4 m water tower both get the room they need. This is the
    ///     "what do I actually have" scene; regenerate it whenever the kit changes.
    ///   • <b>TTA_Sandbox</b> — a ground plane with a collider (the scatter brush
    ///     raycasts onto colliders, so a scene without one paints nothing), the
    ///     game's key-light angle, and an empty Scatter root. This is the scratch
    ///     scene for building and testing arrangements.
    /// </summary>
    public static class PackSceneMenu
    {
        private const float Gap = 0.15f;        // between items, game metres
        private const float RowGap = 1.2f;      // between category rows

        [MenuItem("Tools/TinyTorque Assets/5. Create debug scenes", priority = 104)]
        public static void CreateDebugScenes()
        {
            PackPaths.EnsureFolder(PackPaths.ScenesRoot);
            CreateKitScene();
            CreateSandboxScene();
        }

        // -------------------------------------------------------------------

        private static void CreateKitScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            SetupLighting();

            var byCategory = new SortedDictionary<string, List<GameObject>>();
            foreach (var e in PackPaths.All())
            {
                string group = Group(e);
                foreach (var p in PrefabsFor(e))
                {
                    if (!byCategory.TryGetValue(group, out var list))
                        byCategory[group] = list = new List<GameObject>();
                    list.Add(p);
                }
            }

            float z = 0f;
            int total = 0;
            foreach (var kv in byCategory)
            {
                var row = new GameObject(kv.Key);
                row.transform.position = new Vector3(0f, 0f, z);

                float x = 0f, deepest = 0f;
                foreach (var prefab in kv.Value)
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, row.transform);
                    if (go == null) continue;
                    var b = LocalBounds(go);
                    // Advance by this item's own width so nothing overlaps and
                    // nothing is lost in a fixed-pitch grid.
                    x += b.extents.x + Gap;
                    go.transform.localPosition = new Vector3(x, 0f, 0f);
                    x += b.extents.x;
                    deepest = Mathf.Max(deepest, b.size.z);
                    total++;
                }
                z -= deepest + RowGap;
            }

            string path = PackPaths.ScenesRoot + "/TTA_Kit.unity";
            EditorSceneManager.SaveScene(scene, path);
            PackPaths.Log($"SCENE {path} ({total} prefabs in {byCategory.Count} rows)");
        }

        private static void CreateSandboxScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            SetupLighting();

            // A big plane with a collider — the brush needs something to hit.
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = Vector3.one * 6f;    // 60 m square
            var mat = AIHWSim.Track.TrackBuilder.StandardMat(new Color(0.32f, 0.34f, 0.33f));
            mat.name = "TTA_Ground";
            ground.GetComponent<Renderer>().sharedMaterial = mat;

            var scatter = new GameObject("Scatter");
            scatter.transform.position = Vector3.zero;

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 2.2f, -3.5f);
                cam.transform.rotation = Quaternion.Euler(22f, 0f, 0f);
                // This world is 1:10 — the default 0.3 near plane clips through
                // anything you lean in to look at.
                cam.nearClipPlane = 0.02f;
            }

            string path = PackPaths.ScenesRoot + "/TTA_Sandbox.unity";
            EditorSceneManager.SaveScene(scene, path);
            PackPaths.Log("SCENE " + path);
        }

        // -------------------------------------------------------------------

        private static void SetupLighting()
        {
            var light = Object.FindFirstObjectByType<Light>();
            if (light != null)
            {
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                light.intensity = 1.1f;
                light.shadows = LightShadows.Soft;
            }
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);
        }

        private static string Group(PackEntry e)
        {
            switch (e.kind)
            {
                case PackKind.VehiclePart: return "Vehicles_" + e.category;
                case PackKind.Cosmetic: return "Cosmetics_" + e.category;
                default: return "Props_" + e.category;
            }
        }

        /// <summary>The prefabs to show for one entry. Props show only the mesh
        /// variant — the box variant is the same silhouette and would double the
        /// scene for nothing. Arena tiles show only the default theme, for the
        /// same reason.</summary>
        private static IEnumerable<GameObject> PrefabsFor(PackEntry e)
        {
            string dir = e.PrefabDir;
            if (e.kind == PackKind.Prop)
            {
                string stem = e.category == PackPaths.ArenaCategory
                    ? e.key + "_" + PackSocData.DefaultTheme
                    : e.key;
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(dir + "/" + stem + "_Mesh.prefab");
                if (p != null) yield return p;
            }
            else
            {
                var p = AssetDatabase.LoadAssetAtPath<GameObject>(dir + "/" + e.key + "_Viz.prefab");
                if (p != null) yield return p;
            }
        }

        /// <summary>Combined renderer bounds in the object's own local space.</summary>
        private static Bounds LocalBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 0.1f);

            var inv = go.transform.worldToLocalMatrix;
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var r in rs)
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x,
                                        (i & 2) == 0 ? b.min.y : b.max.y,
                                        (i & 4) == 0 ? b.min.z : b.max.z);
                    var l = inv.MultiplyPoint3x4(c);
                    lo = Vector3.Min(lo, l);
                    hi = Vector3.Max(hi, l);
                }
            }
            var bounds = new Bounds();
            bounds.SetMinMax(lo, hi);
            return bounds;
        }
    }
}
