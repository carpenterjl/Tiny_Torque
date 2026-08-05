using System.Collections.Generic;
using System.IO;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// The pack's regression fence, in the house style of PMV/TPV/COS.
    ///
    ///     Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; ^
    ///         -executeMethod AIHWSim.Pack.PackValidator.Report -logFile pack.log
    ///     ... then grep "[PACK] RESULT"
    ///
    /// Five checks, each guarding a failure this pack can actually have:
    ///   1. <b>completeness</b> — every manifest entry has a mesh and its prefab
    ///      variants, and no prefab renderer is missing a material or wearing the
    ///      magenta MISSING marker.
    ///   2. <b>collider coverage</b> — a <c>_Mesh</c> prefab's collider bounds
    ///      track its renderer bounds, the same tolerances TPV holds the game's
    ///      props to. This is what catches a piece whose collider silently failed
    ///      to cook, which looks fine and drives straight through.
    ///   3. <b>drift</b> — the pack's mesh copies still hash-match their
    ///      <c>Resources/</c> originals. This is the standing cost of shipping the
    ///      pack as a self-contained copy, so it is measured rather than hoped for.
    ///   4. <b>maps</b> — both JSON parse, every item id resolves, every floor
    ///      index is in range and the checkpoint sequence is gapless.
    ///   5. <b>isolation</b> — Build Settings holds every game scene, no pack
    ///      DEBUG scene, and no dangling zero-GUID row. This is "don't add it to
    ///      the game yet" written as an assertion. A pack scene that has since
    ///      been promoted to real game content is listed in
    ///      <see cref="PromotedScenes"/> rather than exempted by loosening it.
    /// </summary>
    public static class PackValidator
    {
        private const float MaxShortfall = 0.05f;   // m, collider inside the mesh
        private const float MaxOvershoot = 0.10f;   // m, collider outside it

        /// <summary>The scenes the shipped game builds. Four, not five: SimMain
        /// exists as a scene asset but is deliberately never registered — only
        /// <c>SceneBuilderMenu.CreateBootstrapScene</c> omits the AddSceneToBuild
        /// call, because nothing loads it by name at runtime.</summary>
        private static readonly string[] GameScenes =
        {
            "Assets/Scenes/TrackScene.unity",
            "Assets/Scenes/GarageScene.unity",
            "Assets/Scenes/MenuScene.unity",
            "Assets/Scenes/TrackBuilderScene.unity",
        };

        /// <summary>Pack scenes that have been PROMOTED to game content and are
        /// therefore expected in Build Settings. TTA_Sandbox stopped being a debug
        /// scene the day it became a hand-authored scene track the game can load;
        /// a scene the player can drive is not "the pack shipping nothing".
        /// The isolation check still fails on every OTHER pack scene, so TTA_Kit
        /// — which really is a browsing lineup and really must not ship — is
        /// still guarded. Promote by listing here, never by weakening the check.</summary>
        private static readonly string[] PromotedScenes =
        {
            "Assets/TinyTorqueAssets/Scenes/TTA_Sandbox.unity",
            // The Simulate Controller screen's default map (SceneTrackCatalog
            // .ControllerMap). Authored in the pack because that is where the
            // road kit and the ProBuilder geometry are; shipped because the
            // player drives it.
            "Assets/TinyTorqueAssets/Scenes/UCSD_TrackScene.unity",
        };

        private static int _fail;

        private static void Fail(string msg)
        {
            _fail++;
            Debug.LogError(PackPaths.Tag + " FAIL " + msg);
        }

        public static void Report()
        {
            _fail = 0;
            var entries = PackPaths.All();

            CheckCompleteness(entries);
            CheckColliderCoverage(entries);
            CheckDrift(entries);
            CheckMaps();
            CheckIsolation();

            if (_fail == 0) PackPaths.Log($"RESULT ALL PASS ({entries.Count} assets)");
            else Debug.LogError($"{PackPaths.Tag} RESULT {_fail} FAILED");
        }

        // -------------------------------------------------------------------

        private static void CheckCompleteness(List<PackEntry> entries)
        {
            int prefabs = 0, badMat = 0;

            foreach (var e in entries)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(e.modelPath) == null)
                {
                    Fail($"no mesh at {e.modelPath}");
                    continue;
                }

                foreach (string p in ExpectedPrefabs(e))
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    if (go == null) { Fail("missing prefab " + p); continue; }
                    prefabs++;

                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null)
                            {
                                badMat++;
                                Fail($"{Path.GetFileName(p)}: renderer '{r.name}' has no material");
                            }
                            else if (m.name.Contains("MISSING"))
                            {
                                badMat++;
                                Fail($"{Path.GetFileName(p)}: renderer '{r.name}' " +
                                     $"wears the MISSING marker '{m.name}'");
                            }
                        }
                    }
                }
            }

            PackPaths.Log($"COMPLETE {entries.Count} meshes, {prefabs} prefabs, " +
                          $"{badMat} material problems");
        }

        private static IEnumerable<string> ExpectedPrefabs(PackEntry e)
        {
            if (e.kind != PackKind.Prop)
            {
                yield return e.PrefabDir + "/" + e.key + "_Viz.prefab";
                yield break;
            }

            if (e.category == PackPaths.ArenaCategory)
            {
                foreach (string theme in PackSocData.Themes)
                {
                    yield return e.PrefabDir + "/" + e.key + "_" + theme + "_Mesh.prefab";
                    yield return e.PrefabDir + "/" + e.key + "_" + theme + "_Box.prefab";
                }
                yield break;
            }

            yield return e.PrefabDir + "/" + e.key + "_Mesh.prefab";
            yield return e.PrefabDir + "/" + e.key + "_Box.prefab";
        }

        // -------------------------------------------------------------------

        private static void CheckColliderCoverage(List<PackEntry> entries)
        {
            int checkedN = 0;

            foreach (var e in entries)
            {
                if (e.kind != PackKind.Prop) continue;

                string stem = e.category == PackPaths.ArenaCategory
                    ? e.key + "_" + PackSocData.DefaultTheme
                    : e.key;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    e.PrefabDir + "/" + stem + "_Mesh.prefab");
                if (prefab == null) continue;

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                try
                {
                    Physics.SyncTransforms();
                    if (!Combined(go.GetComponentsInChildren<Renderer>(true), out var vis)) continue;
                    if (!Combined(go.GetComponentsInChildren<Collider>(true), out var col))
                    {
                        Fail($"{stem}: no colliders at all");
                        continue;
                    }

                    checkedN++;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        float lowGap = col.min[axis] - vis.min[axis];
                        float highGap = vis.max[axis] - col.max[axis];
                        float uncovered = Mathf.Max(lowGap, highGap);
                        float overshoot = Mathf.Max(-lowGap, -highGap);
                        if (uncovered > MaxShortfall)
                            Fail($"{stem}: collider stops {uncovered * 100f:F1} cm short of " +
                                 $"the mesh on {"XYZ"[axis]}");
                        if (overshoot > MaxOvershoot)
                            Fail($"{stem}: collider overshoots the mesh by " +
                                 $"{overshoot * 100f:F1} cm on {"XYZ"[axis]}");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
            }

            PackPaths.Log($"COVER {checkedN} mesh-collider prefabs: colliders track renderers");
        }

        private static bool Combined(Renderer[] rs, out Bounds b)
        {
            b = default;
            bool any = false;
            foreach (var r in rs)
            {
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return any;
        }

        private static bool Combined(Collider[] cs, out Bounds b)
        {
            b = default;
            bool any = false;
            foreach (var c in cs)
            {
                if (c == null || c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; }
                else b.Encapsulate(c.bounds);
            }
            return any;
        }

        // -------------------------------------------------------------------

        private static void CheckDrift(List<PackEntry> entries)
        {
            int compared = 0, drifted = 0;

            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.sourcePath)) continue;   // pack-native
                string src = PackPaths.ToAbsolute(e.sourcePath);
                string dst = PackPaths.ToAbsolute(e.modelPath);
                if (!File.Exists(src) || !File.Exists(dst)) continue;

                compared++;
                if (PackHash.Of(src) != PackHash.Of(dst))
                {
                    drifted++;
                    PackPaths.Warn($"DRIFT {e.key}: pack copy differs from {e.sourcePath} " +
                                   "— re-run 'Copy source meshes'");
                }
            }

            PackPaths.Log($"DRIFT {compared} copies compared, {drifted} stale");
        }

        // -------------------------------------------------------------------

        private static void CheckMaps()
        {
            string dir = PackPaths.ToAbsolute(PackPaths.MapsRoot);
            if (!Directory.Exists(dir)) { Fail("no Maps folder"); return; }

            var files = Directory.GetFiles(dir, "*.json");
            if (files.Length < 2) Fail($"expected 2 maps, found {files.Length}");

            foreach (string f in files)
            {
                TrackDesign d = null;
                try { d = JsonUtility.FromJson<TrackDesign>(File.ReadAllText(f)); }
                catch (System.Exception ex) { Fail($"{Path.GetFileName(f)}: {ex.Message}"); continue; }
                if (d == null) { Fail($"{Path.GetFileName(f)}: parsed to null"); continue; }

                d.EnsureFloor();
                d.EnsureItems();
                d.EnsureSplines();

                int badId = 0;
                foreach (var it in d.items)
                {
                    // TrackFactory silently skips an unknown id by design, so a
                    // typo would just quietly drop a prop off the map.
                    if (TrackCatalog.Item(it.itemId) == null)
                    {
                        badId++;
                        Fail($"{d.name}: unknown item id '{it.itemId}'");
                    }
                }

                foreach (int t in d.floor)
                    if (t < 0 || t >= TrackCatalog.Floors.Length)
                    { Fail($"{d.name}: floor index {t} out of range"); break; }

                var cps = d.OrderedCheckpoints();
                for (int i = 0; i < cps.Count; i++)
                    if (cps[i].order != i)
                    { Fail($"{d.name}: checkpoint sequence has a gap at {i}"); break; }

                PackPaths.Log($"MAP {d.name}: {d.width}x{d.length}, {d.items.Count} items, " +
                              $"{cps.Count} checkpoints, {d.splines.Count} splines, {badId} bad ids");
            }
        }

        // -------------------------------------------------------------------

        private static void CheckIsolation()
        {
            var inBuild = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
                inBuild.Add(s.path.Replace('\\', '/'));

            foreach (string p in inBuild)
                if (p.Contains(PackPaths.Root) && System.Array.IndexOf(PromotedScenes, p) < 0)
                    Fail($"pack scene '{p}' is in Build Settings — the pack must ship nothing");

            foreach (string g in GameScenes)
                if (!inBuild.Contains(g))
                    Fail($"game scene missing from Build Settings: {g}");

            // A dangling row builds an exe that cannot load the scene it names,
            // and Unity writes one whenever a path fails to resolve to an asset.
            foreach (var s in EditorBuildSettings.scenes)
                if (s.guid.Empty())
                    Fail($"Build Settings entry '{s.path}' has an all-zero GUID — " +
                         "the path does not resolve to a scene asset");

            int promoted = 0;
            foreach (string p in inBuild)
                if (System.Array.IndexOf(PromotedScenes, p) >= 0) promoted++;
            PackPaths.Log($"ISOLATION {inBuild.Count} scenes in Build Settings, " +
                          $"{promoted} promoted from the pack, no debug scenes");
        }
    }
}
