using System.IO;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Solves the ideal line for the open scene track and writes it to a
    /// <see cref="RacingLineAsset"/>.
    ///
    /// The solve itself lives in <see cref="RaceLineSolver"/> (pure maths, no
    /// editor) so the headless calibration run and the validator get identical
    /// answers. This is the asset plumbing around it: build the corridor, keep the
    /// existing asset so its references survive, stamp the hash that catches a
    /// stale bake.
    /// </summary>
    public static class RacingLineBaker
    {
        [MenuItem(TrackStudio.Menu + "4. Bake racing line", priority = TrackStudio.PrioBakeLine)]
        public static void BakeCurrent()
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null) { TrackStudio.Warn("no SceneTrackDescriptor in this scene."); return; }
            Bake(d, RaceLineSolver.Settings.Default);
        }

        /// <summary>
        /// Build the corridor from the descriptor's baked centreline, solve, and
        /// save. Uses the corridor rather than re-reading the spline so the line is
        /// solved against exactly what the bots drive — the two cannot disagree
        /// about where the road is.
        /// </summary>
        public static RacingLineAsset Bake(SceneTrackDescriptor d, RaceLineSolver.Settings settings)
        {
            if (d == null || !d.HasCorridor)
            {
                TrackStudio.Warn("no baked corridor — bake the ribbon first.");
                return null;
            }

            var nodes = CorridorFromDescriptor(d);
            if (nodes.Length < 3)
            {
                TrackStudio.Warn("corridor is too short to solve.");
                return null;
            }

            // Carry a previous calibration forward: it describes the CAR, not the
            // line, so re-solving with a different blend must not silently throw
            // away a measurement that cost nine laps to obtain. It is invalidated
            // only when the corridor itself changed.
            var asset = LoadOrCreate(d);
            string hash = RaceLineSolver.HashCorridor(nodes, d.corridorClosed);
            bool corridorChanged = asset.bakeHash != hash;
            var carried = asset.calibration;

            if (carried.valid && !corridorChanged)
            {
                settings.muScale = carried.muScale;
                settings.accelA0 = carried.accelA0;
                settings.vMax = carried.vMax;
                settings.brakeUse = carried.brakeUse;
            }

            var res = RaceLineSolver.Solve(nodes, d.corridorClosed, settings);

            Undo.RecordObject(asset, "Bake racing line");
            asset.sceneName = EditorSceneManager.GetActiveScene().name;
            asset.bakeHash = hash;
            asset.points = res.points;
            asset.lateral = res.lateral;
            asset.curvature = res.curvature;
            asset.speed = res.speed;
            asset.closed = d.corridorClosed;
            asset.apexIndices = res.apexIndices;
            asset.brakeZones = res.brakeZones.ToArray();
            asset.predictedLapSec = res.lapSeconds;
            asset.lineLength = res.length;

            if (corridorChanged && carried.valid)
            {
                asset.calibration = default;
                TrackStudio.Warn("the corridor changed, so the previous calibration no " +
                                 "longer describes this track — re-run the calibration pass.");
            }
            else
            {
                asset.calibration = carried;
                asset.calibration.predictedLapSec = res.lapSeconds;
                if (carried.valid && carried.measuredLapSec > 0.01f)
                    asset.calibration.residualPct =
                        Mathf.Abs(res.lapSeconds - carried.measuredLapSec)
                        / carried.measuredLapSec * 100f;
            }

            EditorUtility.SetDirty(asset);

            if (d.racingLine != asset)
            {
                Undo.RecordObject(d, "Assign racing line");
                d.racingLine = asset;
                EditorUtility.SetDirty(d);
                SceneTrackSetup.MarkSceneDirty();
            }

            AssetDatabase.SaveAssets();
            TrackStudio.Log($"LINE {res.points.Length} nodes, {res.length:0.0} m, " +
                $"predicted {res.lapSeconds:0.000} s, {res.apexIndices.Length} apexes, " +
                $"{res.brakeZones.Count} brake zones, limit fraction {res.limitFraction:0.00}");
            return asset;
        }

        /// <summary>
        /// The corridor as the solver wants it. Half widths come from the baked
        /// corridor and already exclude nothing — the car half width and edge margin
        /// are subtracted HERE, once, so the two callers cannot apply it twice or
        /// zero times.
        /// </summary>
        public static RaceLineSolver.Node[] CorridorFromDescriptor(SceneTrackDescriptor d)
        {
            int n = d.centerline.Length;
            var right = PathCurvature.RightVectors(d.centerline, d.corridorClosed);
            var nodes = new RaceLineSolver.Node[n];
            for (int i = 0; i < n; i++)
            {
                float usable = Mathf.Max(0f,
                    d.halfWidths[i] - RaceLineSolver.CarHalfWidth - RaceLineSolver.EdgeMargin);
                nodes[i] = new RaceLineSolver.Node
                {
                    center = d.centerline[i],
                    right = right[i],
                    halfLeft = usable,
                    halfRight = usable,
                    // The corridor carries no per-node surface, so the line is solved
                    // on the descriptor's fallback. Painted surfaces still affect the
                    // car; they just do not steer the geometric solve.
                    surface = Mathf.Clamp(d.sceneFallbackFloor, 0,
                        TrackCatalog.Floors.Length - 1),
                    bankRad = 0f,
                };
            }
            return nodes;
        }

        private static RacingLineAsset LoadOrCreate(SceneTrackDescriptor d)
        {
            if (d.racingLine != null) return d.racingLine;

            Directory.CreateDirectory(TrackStudio.RacingLineDir);
            AssetDatabase.Refresh();

            string scene = EditorSceneManager.GetActiveScene().name;
            string path = $"{TrackStudio.RacingLineDir}/{scene}_RacingLine.asset";
            var existing = AssetDatabase.LoadAssetAtPath<RacingLineAsset>(path);
            if (existing != null) return existing;

            var created = ScriptableObject.CreateInstance<RacingLineAsset>();
            AssetDatabase.CreateAsset(created, path);
            return created;
        }
    }
}
