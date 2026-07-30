using System.Collections.Generic;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// The scene-editing operations behind Track Studio: promoting a scene to a
    /// track, adding markers and spline roads, baking the road and the corridor,
    /// and renumbering checkpoints.
    ///
    /// Split from the window so each is a menu item and a callable static — the
    /// pipeline has to run without a window open.
    /// </summary>
    public static class SceneTrackSetup
    {
        [MenuItem(TrackStudio.Menu + "1. Make this scene a track", priority = TrackStudio.PrioSetup)]
        public static void MakeCurrentSceneATrack()
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null)
            {
                var go = new GameObject("TrackDescriptor");
                Undo.RegisterCreatedObjectUndo(go, "Make scene a track");
                d = Undo.AddComponent<SceneTrackDescriptor>(go);
                d.displayName = EditorSceneManager.GetActiveScene().name;
                Selection.activeGameObject = go;
            }

            // A track with no spawn drops the car at the world origin, which on a
            // terrain scene is usually underground. Seeding one costs nothing and
            // makes the scene immediately drivable.
            if (d.Spawns().Count == 0) AddMarker<TrackSpawnMarker>();

            // Terrains with no table drive as the 1.0 baseline no matter what they
            // are painted with, so seed a table from the layers actually present.
            if (d.terrainFloors == null &&
                Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None).Length > 0)
            {
                d.terrainFloors = CreateTerrainTable(d);
                EditorUtility.SetDirty(d);
            }

            MarkSceneDirty();
            TrackStudio.Log($"SETUP '{d.displayName}' is a {d.kind} scene track. " +
                "Remaining by hand: register it in SceneTrackCatalog.All and add the " +
                "scene to Build Settings.");
        }

        /// <summary>
        /// Build a TerrainFloorTable seeded from the layers the scene's terrains
        /// actually use, guessing a floor type from each layer's name.
        ///
        /// The guess is a starting point, not an answer — a layer called "Rock" could
        /// reasonably be dirt, obsidian or paving, and only the author knows. The
        /// validator does not accept the guess as authoritative; it only insists that
        /// every layer HAS a row, so an unreviewed default is visible in the
        /// inspector rather than silently applied.
        /// </summary>
        private static TerrainFloorTable CreateTerrainTable(SceneTrackDescriptor d)
        {
            var layers = new List<TerrainLayer>();
            foreach (var t in Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None))
            {
                if (t.terrainData == null) continue;
                foreach (var l in t.terrainData.terrainLayers)
                    if (l != null && !layers.Contains(l)) layers.Add(l);
            }

            // Deterministic row order regardless of terrain enumeration order.
            layers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            var rows = new TerrainFloorTable.Row[layers.Count];
            for (int i = 0; i < layers.Count; i++)
                rows[i] = new TerrainFloorTable.Row
                {
                    layer = layers[i],
                    floorType = GuessFloor(layers[i].name),
                };

            System.IO.Directory.CreateDirectory(TrackStudio.DataRoot);
            AssetDatabase.Refresh();
            string scene = EditorSceneManager.GetActiveScene().name;
            string path = $"{TrackStudio.DataRoot}/{scene}_TerrainFloors.asset";

            var table = AssetDatabase.LoadAssetAtPath<TerrainFloorTable>(path);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<TerrainFloorTable>();
                AssetDatabase.CreateAsset(table, path);
            }
            table.rows = rows;
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();

            TrackStudio.Log($"TERRAIN table '{path}' seeded with {rows.Length} layer(s) — " +
                            "review the floor types before trusting them.");
            return table;
        }

        private static int GuessFloor(string layerName)
        {
            string n = layerName.ToLowerInvariant();
            for (int i = 0; i < TrackCatalog.Floors.Length; i++)
                if (n.Contains(TrackCatalog.Floors[i].id)) return i;
            if (n.Contains("road") || n.Contains("tarmac")) return 1;   // asphalt
            if (n.Contains("gravel") || n.Contains("rock")) return 0;   // dirt
            return 0;
        }

        /// <summary>
        /// Headless promotion, so a scene can be turned into a track from a script.
        /// <code>
        /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
        ///   -executeMethod AIHWSim.TrackTools.SceneTrackSetup.PromoteHeadless
        ///   -logFile p.log -trkScene TTA_Sandbox
        /// </code>
        /// </summary>
        public static void PromoteHeadless()
        {
            string arg = null, kindArg = null;
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-trkScene", System.StringComparison.OrdinalIgnoreCase))
                    arg = args[i + 1];
                else if (string.Equals(args[i], "-trkKind", System.StringComparison.OrdinalIgnoreCase))
                    kindArg = args[i + 1];
            }

            string path = null;
            foreach (string guid in AssetDatabase.FindAssets("t:Scene " + arg))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(p) == arg) { path = p; break; }
            }
            if (path == null) { TrackStudio.Error($"FAIL no scene named '{arg}'"); return; }

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            MakeCurrentSceneATrack();

            if (!string.IsNullOrEmpty(kindArg) &&
                System.Enum.TryParse<TrackPresets.TrackKind>(kindArg, true, out var kind))
            {
                var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
                if (d != null && d.kind != kind)
                {
                    d.kind = kind;
                    EditorUtility.SetDirty(d);
                    TrackStudio.Log($"KIND '{arg}' set to {kind}");
                }
            }

            RepairMarkers();
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            TrackStudio.Log($"PROMOTE saved '{path}'");
        }

        /// <summary>
        /// Strip marker shells left behind by a component whose script asset could
        /// not be resolved, then renumber what survives.
        ///
        /// Such a component reloads as Missing Script: it is invisible to
        /// <c>GetComponentsInChildren</c>, so <see cref="MakeCurrentSceneATrack"/>
        /// saw no spawn and seeded another one on every run — which is how
        /// TTA_Sandbox ended up with two GameObjects called "Spawn", only one of
        /// which the Track Studio window could count. Safe to run on a healthy
        /// scene: it only deletes a direct child of the descriptor that has nothing
        /// left on it but a Transform.
        /// </summary>
        [MenuItem(TrackStudio.Menu + "Repair markers", priority = TrackStudio.PrioRenumber + 2)]
        public static void RepairMarkers()
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null) { TrackStudio.Warn("no SceneTrackDescriptor in this scene."); return; }

            int stripped = 0, removed = 0;
            for (int i = d.transform.childCount - 1; i >= 0; i--)
            {
                var child = d.transform.GetChild(i).gameObject;
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child);
                if (missing > 0)
                {
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child);
                    stripped += missing;
                }

                // An empty shell: no marker, no geometry, no children of its own.
                if (child.transform.childCount == 0 &&
                    child.GetComponents<Component>().Length == 1)
                {
                    TrackStudio.Log($"REPAIR removing empty marker shell '{child.name}'");
                    Undo.DestroyObjectImmediate(child);
                    removed++;
                }
            }

            if (stripped > 0 || removed > 0)
            {
                TrackStudio.Log($"REPAIR stripped {stripped} missing script(s), " +
                                $"removed {removed} empty marker(s)");
                MarkSceneDirty();
            }

            // Re-seed only after the shells are gone, or the seed check counts a
            // spawn that is not there.
            if (d.Spawns().Count == 0) AddMarker<TrackSpawnMarker>();
            RenumberSpawns();
            RenumberCheckpoints();
        }

        // -------------------------------------------------------------------
        // markers
        // -------------------------------------------------------------------

        /// <summary>
        /// Drop a marker in front of the Scene view camera, on the ground if there
        /// is any. Placing at the world origin instead would bury it under the track
        /// on most scenes and make the first thing an author does be "find it".
        /// </summary>
        public static void AddMarker<T>() where T : TrackMarker
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null) { TrackStudio.Warn("no SceneTrackDescriptor in this scene."); return; }

            var go = new GameObject("TrackMarker");
            Undo.RegisterCreatedObjectUndo(go, "Add track marker");
            var added = Undo.AddComponent<T>(go);

            // A component whose script asset could not be resolved lands as a
            // Missing Script: invisible to GetComponentsInChildren, so the seeding
            // in MakeCurrentSceneATrack would add ANOTHER one on the next run, and
            // another after that. That is how TTA_Sandbox collected two spawns.
            // Refuse loudly instead of leaving a duplicate behind.
            if (added == null)
            {
                TrackStudio.Error($"could not add {typeof(T).Name} — no MonoScript " +
                    "asset. Every MonoBehaviour authored into a saved scene needs " +
                    "its own file, named after the class.");
                Undo.DestroyObjectImmediate(go);
                return;
            }

            go.transform.SetParent(d.transform, false);
            go.transform.SetPositionAndRotation(PlacementPoint(out var rot), rot);

            // Name from the slot, not the type: "Player 3 Spawn" and "Checkpoint 2"
            // read as a grid and a lap in the hierarchy, where three objects called
            // "Spawn" read as a mistake.
            if (added is TrackCheckpointMarker cp)
            {
                cp.order = d.Checkpoints().Count - 1;   // it is already in the list
                go.name = TrackCheckpointMarker.NameFor(cp.order);
            }
            else if (added is TrackSpawnMarker sp)
            {
                sp.gridOrder = d.Spawns().Count - 1;
                go.name = TrackSpawnMarker.NameFor(sp.gridOrder);
            }
            else
            {
                go.name = "Finish";
            }

            Selection.activeGameObject = go;
            MarkSceneDirty();
        }

        private static Vector3 PlacementPoint(out Quaternion rot)
        {
            rot = Quaternion.identity;
            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null) return Vector3.zero;

            var cam = view.camera;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (Physics.Raycast(ray, out var hit, 500f, ~0, QueryTriggerInteraction.Ignore))
            {
                // Face along the camera, flattened: the author is looking down the
                // road, and a gate's +Z is the direction cars travel through it.
                var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
                if (fwd.sqrMagnitude > 1e-4f) rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                return hit.point;
            }
            return view.pivot;
        }

        [MenuItem(TrackStudio.Menu + "3. Renumber checkpoints", priority = TrackStudio.PrioRenumber)]
        public static void RenumberCheckpoints()
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null) { TrackStudio.Warn("no SceneTrackDescriptor in this scene."); return; }

            var cps = d.Checkpoints();   // already sorted by current order
            for (int i = 0; i < cps.Count; i++)
            {
                if (cps[i].order != i)
                {
                    Undo.RecordObject(cps[i], "Renumber checkpoints");
                    cps[i].order = i;
                    EditorUtility.SetDirty(cps[i]);
                }
                Rename(cps[i].gameObject, TrackCheckpointMarker.NameFor(i));
            }
            MarkSceneDirty();
            TrackStudio.Log($"RENUMBER {cps.Count} checkpoints -> dense 0..{cps.Count - 1}");
        }

        /// <summary>
        /// Make <c>gridOrder</c> a dense <c>0..n-1</c> run and rename each spawn to
        /// its slot. Same job "Renumber checkpoints" does for the lap: a grid with
        /// a gap silently leaves a starting slot nobody occupies, because
        /// <c>TrackBootstrap</c> asks for the marker whose gridOrder equals the
        /// player's index and falls back to the procedural row when there is none.
        /// </summary>
        [MenuItem(TrackStudio.Menu + "3b. Renumber spawns", priority = TrackStudio.PrioRenumber + 1)]
        public static void RenumberSpawns()
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null) { TrackStudio.Warn("no SceneTrackDescriptor in this scene."); return; }

            var sps = d.Spawns();        // already sorted by current gridOrder
            for (int i = 0; i < sps.Count; i++)
            {
                if (sps[i].gridOrder != i)
                {
                    Undo.RecordObject(sps[i], "Renumber spawns");
                    sps[i].gridOrder = i;
                    EditorUtility.SetDirty(sps[i]);
                }
                Rename(sps[i].gameObject, TrackSpawnMarker.NameFor(i));
            }
            MarkSceneDirty();
            TrackStudio.Log($"RENUMBER {sps.Count} spawns -> grid slots 1..{sps.Count}");
        }

        /// <summary>Undo-recorded rename, skipped when it would be a no-op so a
        /// renumber that changes nothing does not dirty the scene.</summary>
        private static void Rename(GameObject go, string name)
        {
            if (go == null || go.name == name) return;
            Undo.RecordObject(go, "Rename track marker");
            go.name = name;
            EditorUtility.SetDirty(go);
        }

        // -------------------------------------------------------------------
        // roads
        // -------------------------------------------------------------------

        /// <summary>
        /// Adopt splines that already exist. A <see cref="TrackSplineAuthoring"/>
        /// [RequireComponent]s a <c>SplineContainer</c>, so converting a spline you
        /// drew earlier is nothing more than adding the component to it — the curve,
        /// its knots and its tangents are kept exactly as they are.
        ///
        /// Operates on the selection, walking up and down from each selected object
        /// so clicking either the container or a child of it does the same thing.
        /// </summary>
        [MenuItem(TrackStudio.Menu + "Convert selected spline to road",
                  priority = TrackStudio.PrioBakeRibbon - 1)]
        public static void ConvertSelectionToRoad()
        {
            var containers = new List<SplineContainer>();
            foreach (var go in Selection.gameObjects)
            {
                if (go == null) continue;
                var c = go.GetComponent<SplineContainer>()
                     ?? go.GetComponentInParent<SplineContainer>()
                     ?? go.GetComponentInChildren<SplineContainer>();
                if (c != null && !containers.Contains(c)) containers.Add(c);
            }

            if (containers.Count == 0)
            {
                int loose = Object.FindObjectsByType<SplineContainer>(
                    FindObjectsSortMode.None).Length;
                TrackStudio.Warn(loose == 0
                    ? "no SplineContainer selected, and none in this scene."
                    : $"no SplineContainer selected. This scene has {loose} — select one " +
                      "(or use 'Adopt every spline in scene').");
                return;
            }

            Adopt(containers);
        }

        /// <summary>
        /// Same conversion across every spline in the scene, for the case where the
        /// road was drawn before Track Studio existed and is spread over several
        /// containers.
        /// </summary>
        [MenuItem(TrackStudio.Menu + "Adopt every spline in scene",
                  priority = TrackStudio.PrioBakeRibbon - 1)]
        public static void AdoptAllSplines()
        {
            var all = new List<SplineContainer>(
                Object.FindObjectsByType<SplineContainer>(FindObjectsSortMode.None));
            if (all.Count == 0) { TrackStudio.Warn("no SplineContainer in this scene."); return; }
            Adopt(all);
        }

        private static void Adopt(List<SplineContainer> containers)
        {
            int added = 0, already = 0, empty = 0;
            GameObject last = null;

            foreach (var c in containers)
            {
                // A container with no curve would bake nothing and read as a silent
                // failure, so say which one.
                if (c.Splines.Count == 0 || c.Spline == null || c.Spline.Count < 2)
                {
                    TrackStudio.Warn($"'{c.name}' has fewer than 2 knots — nothing to bake.");
                    empty++;
                    continue;
                }

                if (c.GetComponent<TrackSplineAuthoring>() != null) { already++; continue; }

                var a = Undo.AddComponent<TrackSplineAuthoring>(c.gameObject);
                if (a == null) { TrackStudio.Error($"could not add authoring to '{c.name}'"); continue; }
                a.spacing = UnitySplineSampler.DefaultSpacing;
                EditorUtility.SetDirty(a);
                added++;
                last = c.gameObject;

                // An adopted spline very often already draws itself with SplineExtrude
                // — that is how a road gets made before this tool exists. Baking on top
                // leaves two overlapping surfaces fighting for the same wheel raycasts,
                // and the symptom is grip that changes depending on which one wins.
                foreach (var extra in c.GetComponents<MonoBehaviour>())
                {
                    if (extra == null || extra == a) continue;
                    if (extra.GetType().Name != "SplineExtrude") continue;
                    TrackStudio.Warn($"'{c.name}' also has a SplineExtrude generating its " +
                        "own mesh. Disable or remove it before baking, or the road will " +
                        "exist twice.");
                }
            }

            if (last != null) Selection.activeGameObject = last;
            MarkSceneDirty();
            TrackStudio.Log($"ADOPT {added} spline(s) converted to roads" +
                            (already > 0 ? $", {already} already were" : "") +
                            (empty > 0 ? $", {empty} skipped as empty" : "") +
                            ". Set width/banking/surface on the component, then bake.");
        }

        public static void AddSplineRoad()
        {
            var go = new GameObject("SplineRoad");
            Undo.RegisterCreatedObjectUndo(go, "Add spline road");
            var container = Undo.AddComponent<SplineContainer>(go);
            Undo.AddComponent<TrackSplineAuthoring>(go);

            // A container with no knots gives the spline tool nothing to grab, so
            // seed a short straight the author can immediately extend.
            var spline = container.Spline;
            spline.Add(new BezierKnot(new Unity.Mathematics.float3(0f, 0f, 0f)));
            spline.Add(new BezierKnot(new Unity.Mathematics.float3(0f, 0f, 5f)));

            Selection.activeGameObject = go;
            MarkSceneDirty();
            TrackStudio.Log("added a spline road — shape it with the Spline tool, then bake.");
        }

        [MenuItem(TrackStudio.Menu + "2. Bake ribbon + corridor", priority = TrackStudio.PrioBakeRibbon)]
        public static void BakeRoads()
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            var authors = Object.FindObjectsByType<TrackSplineAuthoring>(FindObjectsSortMode.None);
            if (authors.Length == 0) { TrackStudio.Warn("no TrackSplineAuthoring in this scene."); return; }

            // Deterministic order. FindObjectsByType makes no ordering promise, and
            // the corridor picked below must not depend on which road Unity happened
            // to enumerate first.
            System.Array.Sort(authors, (a, b) =>
                string.CompareOrdinal(a.gameObject.name, b.gameObject.name));

            int built = 0;
            SplineSpec corridorSpec = null;
            foreach (var a in authors)
            {
                var spec = a.BuildSpec();
                if (spec.Count < 2)
                {
                    TrackStudio.Warn($"'{a.gameObject.name}' has fewer than 2 knots — skipped.");
                    continue;
                }
                a.Bake();
                built++;

                // The corridor comes from the LONGEST contributing road, matching
                // BotPath's rule for tile maps (it takes the spline with the most
                // control points). One rule, both track sources.
                if (a.contributesCorridor &&
                    (corridorSpec == null || spec.Count > corridorSpec.Count))
                    corridorSpec = spec;
            }

            if (d != null && corridorSpec != null)
            {
                Undo.RecordObject(d, "Bake corridor");
                UnitySplineSampler.ToCorridor(corridorSpec, out var pts, out var half, out bool closed);
                d.centerline = pts;
                d.halfWidths = half;
                d.corridorClosed = closed;
                EditorUtility.SetDirty(d);
                TrackStudio.Log($"BAKE {built} road(s), corridor {pts.Length} nodes, " +
                                $"{(closed ? "closed" : "open")}");
            }
            else
            {
                TrackStudio.Log($"BAKE {built} road(s), no corridor " +
                    (d == null ? "(no descriptor)" : "(no contributing road)"));
            }

            MarkSceneDirty();
        }

        internal static void MarkSceneDirty()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
