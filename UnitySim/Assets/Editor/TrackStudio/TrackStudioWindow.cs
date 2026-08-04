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
    /// The Track Studio front end: turn the open scene into a playable track, bake
    /// its road, place and order its gates, and bake a racing line.
    ///
    /// Every button here is also a menu item, because the pipeline has to be
    /// runnable headlessly and from a script. The window exists to put the state of
    /// the current scene in front of the author — what is missing, what is stale —
    /// which a menu cannot do.
    /// </summary>
    public sealed class TrackStudioWindow : EditorWindow
    {
        private Vector2 _scroll;
        private RaceLineSolver.Settings _solver = RaceLineSolver.Settings.Default;

        [MenuItem(TrackStudio.Menu + "Track Studio", priority = TrackStudio.PrioWindow)]
        public static void Open()
        {
            var w = GetWindow<TrackStudioWindow>(false, "Track Studio", true);
            w.minSize = new Vector2(340f, 460f);
            w.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var d = FindFirstObjectByType<SceneTrackDescriptor>();

            EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);
            if (d == null)
            {
                EditorGUILayout.HelpBox(
                    "This scene is not a track yet. Adding a descriptor is what makes " +
                    "the game able to load it.", MessageType.Info);
                if (GUILayout.Button("Make this scene a track", GUILayout.Height(26f)))
                    SceneTrackSetup.MakeCurrentSceneATrack();
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField("Descriptor", d.gameObject.name);
            EditorGUILayout.LabelField("Kind", d.kind.ToString());
            if (d.kind == TrackPresets.TrackKind.Arena && d.playfield == null)
                EditorGUILayout.HelpBox(
                    "This Arena scene track has no Playfield collider. ArenaNav needs " +
                    "the floor's bounds for the arena's centre, radius and containment " +
                    "test — assign the floor collider to the descriptor's Playfield field.",
                    MessageType.Error);
            if (GUILayout.Button("Select descriptor")) Selection.activeGameObject = d.gameObject;

            // ---- markers ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Gates and spawns", EditorStyles.boldLabel);
            var cps = d.Checkpoints();
            var spawns = d.Spawns();
            var finish = d.Finish();
            EditorGUILayout.LabelField("Finish", finish != null ? "present" : "MISSING");
            EditorGUILayout.LabelField("Checkpoints", cps.Count.ToString());

            // Spawns are the start grid: slot N is player N+1, so list them rather
            // than printing a count. A count is exactly what hid the fact that this
            // scene had two markers called "Spawn", one of which was a broken
            // script the collection could not see.
            EditorGUILayout.LabelField("Start grid", spawns.Count == 0
                ? "MISSING" : $"{spawns.Count} slot{(spawns.Count == 1 ? "" : "s")}");
            using (new EditorGUI.IndentLevelScope())
                for (int i = 0; i < spawns.Count; i++)
                    EditorGUILayout.LabelField(
                        TrackSpawnMarker.NameFor(i),
                        spawns[i].gridOrder == i ? spawns[i].name
                                                 : $"{spawns[i].name}  (gridOrder {spawns[i].gridOrder})");
            if (spawns.Count == 1)
                EditorGUILayout.HelpBox(
                    "One spawn is fine — the rest of the field is laid out from it " +
                    "in a staggered row. Add more to author the grid yourself.",
                    MessageType.None);

            string orderProblem = CheckOrders(cps);
            if (orderProblem != null)
            {
                EditorGUILayout.HelpBox(orderProblem, MessageType.Error);
                if (GUILayout.Button("Renumber checkpoints densely"))
                    SceneTrackSetup.RenumberCheckpoints();
            }

            string gridProblem = CheckGrid(spawns);
            if (gridProblem != null)
            {
                EditorGUILayout.HelpBox(gridProblem, MessageType.Error);
                if (GUILayout.Button("Renumber + rename spawns"))
                    SceneTrackSetup.RenumberSpawns();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add checkpoint")) SceneTrackSetup.AddMarker<TrackCheckpointMarker>();
                if (GUILayout.Button("Add player spawn")) SceneTrackSetup.AddMarker<TrackSpawnMarker>();
                if (GUILayout.Button("Add finish")) SceneTrackSetup.AddMarker<TrackFinishMarker>();
            }

            // ---- road ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Road", EditorStyles.boldLabel);
            var authors = FindObjectsByType<TrackSplineAuthoring>(FindObjectsSortMode.None);
            var containers = FindObjectsByType<SplineContainer>(FindObjectsSortMode.None);
            int unadopted = 0;
            foreach (var c in containers)
                if (c.GetComponent<TrackSplineAuthoring>() == null) unadopted++;

            EditorGUILayout.LabelField("Spline roads", authors.Length.ToString());

            // List them rather than counting: selecting a road is how you get to its
            // width/banking/surface channels, and hunting for it in a scene with
            // terrain and props is the slow part.
            using (new EditorGUI.IndentLevelScope())
                foreach (var a in authors)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(a.name,
                            a.contributesCorridor ? "corridor" : "decorative");
                        if (GUILayout.Button("Select", GUILayout.Width(60f)))
                            Selection.activeGameObject = a.gameObject;
                    }

            // Splines that exist but are not roads yet — the common case when the
            // curve was drawn before Track Studio, or with SplineExtrude.
            if (unadopted > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{unadopted} spline(s) in this scene are not roads yet. Converting " +
                    "keeps the curve exactly as drawn — it only adds the component that " +
                    "carries width, banking and surface.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Convert selected spline"))
                        SceneTrackSetup.ConvertSelectionToRoad();
                    if (GUILayout.Button("Adopt all"))
                        SceneTrackSetup.AdoptAllSplines();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add spline road")) SceneTrackSetup.AddSplineRoad();
                using (new EditorGUI.DisabledScope(authors.Length == 0))
                    if (GUILayout.Button("Bake ribbon + corridor")) SceneTrackSetup.BakeRoads();
            }

            // ---- racing line ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Racing line", EditorStyles.boldLabel);
            _solver.shortestPathBlend = EditorGUILayout.Slider(
                new GUIContent("Shortest-path blend",
                    "0 = minimum curvature (runs wide on exits), 1 = shortest path " +
                    "(hugs the inside of everything)."),
                _solver.shortestPathBlend, 0f, 1f);
            _solver.iterations = EditorGUILayout.IntSlider("Iterations", _solver.iterations, 20, 2000);
            _solver.sor = EditorGUILayout.Slider("SOR omega", _solver.sor, 0.2f, 1.9f);
            _solver.muScale = EditorGUILayout.FloatField("Grip scale (seed)", _solver.muScale);
            _solver.accelA0 = EditorGUILayout.FloatField("Accel a0 (m/s^2)", _solver.accelA0);
            _solver.vMax = EditorGUILayout.FloatField("v max (m/s)", _solver.vMax);
            _solver.brakeUse = EditorGUILayout.Slider("Brake use", _solver.brakeUse, 0.2f, 1f);
            _solver.useBanking = EditorGUILayout.Toggle("Use banking", _solver.useBanking);

            using (new EditorGUI.DisabledScope(!d.HasCorridor))
                if (GUILayout.Button("Bake racing line", GUILayout.Height(24f)))
                    RacingLineBaker.Bake(d, _solver);
            if (!d.HasCorridor)
                EditorGUILayout.HelpBox(
                    "No baked corridor. Bake the ribbon first — the line is solved " +
                    "inside the road's width, so there is nothing to solve without it.",
                    MessageType.Info);

            if (d.racingLine != null)
            {
                var rl = d.racingLine;
                EditorGUILayout.LabelField("Nodes", rl.points.Length.ToString());
                EditorGUILayout.LabelField("Predicted lap", $"{rl.predictedLapSec:0.000} s");
                EditorGUILayout.LabelField("Apexes", rl.apexIndices.Length.ToString());
                EditorGUILayout.LabelField("Brake zones", rl.brakeZones.Length.ToString());
                EditorGUILayout.LabelField("Calibrated",
                    rl.calibration.valid
                        ? $"yes — residual {rl.calibration.residualPct:0.0}%"
                        : "no (predicted times are a model, not a measurement)");
            }

            // ---- checks ----
            EditorGUILayout.Space();
            if (GUILayout.Button("Validate this scene", GUILayout.Height(24f)))
                TrackStudioValidator.Report();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Dense 0..n-1 or the track can never complete a lap: LapTimer only
        /// advances on an exact index match and refuses the line until every
        /// checkpoint is hit. This is the check that turns that silent dead end
        /// into a message.
        /// </summary>
        internal static string CheckOrders(List<TrackCheckpointMarker> cps)
        {
            if (cps.Count == 0) return null;
            var seen = new HashSet<int>();
            foreach (var c in cps)
            {
                if (c.order < 0) return $"checkpoint '{c.name}' has a negative order.";
                if (!seen.Add(c.order)) return $"two checkpoints share order {c.order}.";
            }
            for (int i = 0; i < cps.Count; i++)
                if (!seen.Contains(i))
                    return $"checkpoint orders are not a dense 0..{cps.Count - 1} run " +
                           $"({i} is missing). A lap can never be validated.";
            return null;
        }

        /// <summary>
        /// The same density rule for the start grid. A duplicated gridOrder means
        /// two cars are authored into one slot and one of them silently gets the
        /// other's pose; a gap means a slot nobody occupies, and every player above
        /// it drops back to the procedural row.
        /// </summary>
        internal static string CheckGrid(List<TrackSpawnMarker> spawns)
        {
            if (spawns.Count <= 1) return null;
            var seen = new HashSet<int>();
            foreach (var s in spawns)
            {
                if (s.gridOrder < 0) return $"spawn '{s.name}' has a negative gridOrder.";
                if (!seen.Add(s.gridOrder))
                    return $"two spawns share grid slot {s.gridOrder} — one car would " +
                           "start inside the other.";
            }
            for (int i = 0; i < spawns.Count; i++)
                if (!seen.Contains(i))
                    return $"grid slots are not a dense 0..{spawns.Count - 1} run " +
                           $"({i} is missing), so player {i + 1} has no authored start.";
            return null;
        }
    }
}
