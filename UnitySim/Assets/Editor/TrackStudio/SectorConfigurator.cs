using System.Collections.Generic;
using System.IO;
using AIHWSim.Track;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Slices a baked racing line into sectors and derives each one's target time
    /// from the velocity profile.
    ///
    /// The targets are integrated from the same profile the line was baked with, so
    /// they sum to the predicted lap by construction rather than by coincidence —
    /// which is what makes "sector 2 is 0.3 s off target" a statement about driving
    /// rather than about arithmetic.
    /// </summary>
    public sealed class SectorConfigurator : EditorWindow
    {
        private int _count = 3;
        private Vector2 _scroll;

        [MenuItem(TrackStudio.Menu + "6. Sector configurator", priority = TrackStudio.PrioSectors)]
        public static void Open()
        {
            var w = GetWindow<SectorConfigurator>(false, "Sectors", true);
            w.minSize = new Vector2(320f, 300f);
            w.Show();
        }

        private void OnGUI()
        {
            var d = FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null || d.racingLine == null || !d.racingLine.IsUsable)
            {
                EditorGUILayout.HelpBox(
                    "Needs a scene track with a baked racing line — sectors are " +
                    "distances along that line, and their targets come from its " +
                    "speed profile.", MessageType.Info);
                return;
            }

            var line = d.racingLine;
            EditorGUILayout.LabelField("Line", $"{line.lineLength:0.0} m, " +
                $"predicted {line.predictedLapSec:0.000} s");
            if (!line.calibration.valid)
                EditorGUILayout.HelpBox(
                    "This line is not calibrated, so the targets below are a physics " +
                    "model's prediction rather than a measurement. Run the calibration " +
                    "pass before treating them as achievable.", MessageType.Warning);

            EditorGUILayout.Space();
            _count = EditorGUILayout.IntSlider("Sectors", _count, 2, 12);

            if (GUILayout.Button("Create evenly spaced sectors", GUILayout.Height(24f)))
                Build(d, EvenBoundaries(line, _count));

            if (GUILayout.Button("Create sectors at apexes"))
            {
                var bounds = ApexBoundaries(line);
                if (bounds.Count < 2)
                    TrackStudio.Warn("fewer than two apexes — nothing to slice at.");
                else Build(d, bounds);
            }

            var set = FindSet(d);
            if (set == null) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current set", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < set.sectors.Length; i++)
            {
                var s = set.sectors[i];
                EditorGUILayout.LabelField(
                    $"S{i + 1}",
                    $"from {s.sStart:0.0} m — target {s.targetSec:0.000} s");
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Sum of targets",
                $"{set.TotalTarget:0.000} s vs predicted lap {line.predictedLapSec:0.000} s");
            EditorGUILayout.EndScrollView();
        }

        private static List<float> EvenBoundaries(RacingLineAsset line, int count)
        {
            var list = new List<float>(count);
            for (int i = 0; i < count; i++) list.Add(line.lineLength * i / count);
            return list;
        }

        /// <summary>
        /// Sector boundaries just BEFORE each apex, which is where a real timing
        /// loop goes: a split taken at the apex measures half of the corner into the
        /// previous sector and half into the next, and tells you nothing about
        /// either.
        /// </summary>
        private static List<float> ApexBoundaries(RacingLineAsset line)
        {
            var cum = PathCurvature.Cumulative(line.points);
            var list = new List<float>();
            foreach (int a in line.apexIndices)
            {
                if (a < 0 || a >= cum.Length) continue;
                float s = Mathf.Max(0f, cum[a] - 3f);
                if (list.Count == 0 || s - list[list.Count - 1] > 2f) list.Add(s);
            }
            if (list.Count > 0 && list[0] > 0.01f) list.Insert(0, 0f);
            return list;
        }

        private void Build(SceneTrackDescriptor d, List<float> boundaries)
        {
            var line = d.racingLine;
            boundaries.Sort();
            var set = FindSet(d) ?? Create(d);

            var sectors = new TrackSectorSet.Sector[boundaries.Count];
            for (int i = 0; i < boundaries.Count; i++)
            {
                float from = boundaries[i];
                float to = i + 1 < boundaries.Count ? boundaries[i + 1] : line.lineLength;
                sectors[i] = new TrackSectorSet.Sector
                {
                    sStart = from,
                    targetSec = IntegrateTime(line, from, to),
                    label = $"S{i + 1}",
                };
            }

            Undo.RecordObject(set, "Build sectors");
            set.line = line;
            set.sectors = sectors;
            EditorUtility.SetDirty(set);

            // Point the descriptor at it, or the scene build has no way to know a
            // sector set exists and SectorTimer would never be attached.
            if (d.sectors != set)
            {
                Undo.RecordObject(d, "Assign sectors");
                d.sectors = set;
                EditorUtility.SetDirty(d);
                SceneTrackSetup.MarkSceneDirty();
            }
            AssetDatabase.SaveAssets();

            TrackStudio.Log($"SECTORS {sectors.Length}, targets sum {set.TotalTarget:0.000} s " +
                            $"vs predicted lap {line.predictedLapSec:0.000} s");
        }

        /// <summary>
        /// Time to cover an arc-length range, integrated across the same profile the
        /// lap prediction used — trapezoidal on speed, per node, so the two agree.
        /// </summary>
        private static float IntegrateTime(RacingLineAsset line, float from, float to)
        {
            var cum = PathCurvature.Cumulative(line.points);
            int n = line.points.Length;
            float t = 0f;
            for (int i = 0; i < n - 1; i++)
            {
                if (cum[i] < from || cum[i] >= to) continue;
                float ds = cum[i + 1] - cum[i];
                float vAvg = Mathf.Max(0.05f, 0.5f * (line.speed[i] + line.speed[i + 1]));
                t += ds / vAvg;
            }
            return t;
        }

        private static TrackSectorSet FindSet(SceneTrackDescriptor d)
        {
            var timer = Object.FindFirstObjectByType<SectorTimer>();
            if (timer != null && timer.sectors != null) return timer.sectors;

            string scene = EditorSceneManager.GetActiveScene().name;
            return AssetDatabase.LoadAssetAtPath<TrackSectorSet>(
                $"{TrackStudio.SectorDir}/{scene}_Sectors.asset");
        }

        private static TrackSectorSet Create(SceneTrackDescriptor d)
        {
            Directory.CreateDirectory(TrackStudio.SectorDir);
            AssetDatabase.Refresh();
            string scene = EditorSceneManager.GetActiveScene().name;
            string path = $"{TrackStudio.SectorDir}/{scene}_Sectors.asset";
            var set = ScriptableObject.CreateInstance<TrackSectorSet>();
            AssetDatabase.CreateAsset(set, path);
            return set;
        }
    }
}
