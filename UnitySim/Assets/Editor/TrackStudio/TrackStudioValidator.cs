using System.Collections.Generic;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Regression fence for hand-authored scene tracks.
    ///
    /// Run headlessly (the editor must be closed):
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; ^
    ///     -executeMethod AIHWSim.TrackTools.TrackStudioValidator.Report -logFile trk.log
    /// ... then grep "[TRK] RESULT"
    /// </code>
    /// <c>-quit</c> IS correct here, unlike <c>OpusMissionRunner</c>: this is
    /// edit-mode only and never enters play mode, so there is no watcher that needs
    /// the process kept alive.
    ///
    /// Every check guards a failure this tooling can actually produce, and most of
    /// them guard a SILENT one — a track that loads, drives, and is quietly wrong.
    /// </summary>
    public static class TrackStudioValidator
    {
        private static int _fail;

        private static void Fail(string msg)
        {
            _fail++;
            TrackStudio.Error("FAIL " + msg);
        }

        public static void Report()
        {
            _fail = 0;
            int scenes = 0;

            foreach (var row in SceneTrackCatalog.All)
            {
                string path = PathForScene(row.scene);
                if (path == null)
                {
                    Fail($"SceneTrackCatalog names '{row.scene}' but no such scene asset exists");
                    continue;
                }
                if (!InBuildSettings(path))
                    Fail($"'{row.scene}' is in SceneTrackCatalog but not in Build Settings — " +
                         "the picker would offer a track SceneManager.LoadScene cannot find");

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                CheckScene(row);
                scenes++;
            }

            CheckConstantParity();
            CheckSolverDeterminism();

            if (_fail == 0) TrackStudio.Log($"RESULT ALL PASS ({scenes} scene tracks)");
            else Debug.LogError($"{TrackStudio.Tag} RESULT {_fail} FAILED");
        }

        // -------------------------------------------------------------------

        private static void CheckScene(SceneTrackCatalog.Row row)
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null)
            {
                Fail($"'{row.scene}' has no SceneTrackDescriptor");
                return;
            }

            if (d.kind != row.kind)
                Fail($"'{row.scene}' kind mismatch: catalog says {row.kind}, " +
                     $"descriptor says {d.kind}");

            // An Arena scene track is supported exactly as far as it can answer
            // "where is the floor" — ArenaNav takes the arena's centre, its radius,
            // its containment test and every drop from those bounds. Without a
            // playfield the mode would compose and then place goals, pickups and
            // the ball by averaging the spawn ring, which looks like it works right
            // up until the pitch is not centred on its spawns.
            if (d.kind == TrackPresets.TrackKind.Arena && d.playfield == null)
                Fail($"'{row.scene}' is an Arena scene track with no Playfield collider. " +
                     "ArenaNav reads BuiltTrack.floorCollider.bounds for the arena's " +
                     "centre, radius and containment test; assign the floor collider to " +
                     "the descriptor's Playfield field");

            CheckMarkers(row.scene, d);
            CheckSurfaces(row.scene, d);
            CheckRacingLine(row.scene, d);
            CheckSectors(row.scene, d);
        }

        /// <summary>
        /// Sector boundaries must ascend from 0 and their targets must add up to the
        /// lap the profile predicts. A set that does not sum is not a rounding
        /// problem — it means the boundaries and the line have diverged, and every
        /// split shown to a player would be measured against the wrong reference.
        /// </summary>
        private static void CheckSectors(string scene, SceneTrackDescriptor d)
        {
            var set = d.sectors;
            if (set == null) return;

            if (set.line == null)
            {
                Fail($"'{scene}' sector set '{set.name}' has no racing line — its " +
                     "boundaries are distances along nothing");
                return;
            }
            if (set.line != d.racingLine)
                Fail($"'{scene}' sector set is measured along '{set.line.name}' but the " +
                     $"descriptor uses '{(d.racingLine != null ? d.racingLine.name : "<none>")}'");

            if (set.sectors.Length == 0)
            {
                Fail($"'{scene}' sector set '{set.name}' is empty");
                return;
            }
            if (set.sectors[0].sStart > 0.01f)
                Fail($"'{scene}' first sector starts at {set.sectors[0].sStart:0.00} m, not 0");

            for (int i = 1; i < set.sectors.Length; i++)
                if (set.sectors[i].sStart <= set.sectors[i - 1].sStart)
                {
                    Fail($"'{scene}' sector boundaries are not strictly ascending " +
                         $"({set.sectors[i - 1].sStart:0.00} then {set.sectors[i].sStart:0.00}) — " +
                         "one split would come out negative and every later one would be wrong");
                    break;
                }

            float lineLen = set.line.lineLength;
            foreach (var s in set.sectors)
                if (s.sStart >= lineLen)
                {
                    Fail($"'{scene}' sector boundary at {s.sStart:0.00} m is past the " +
                         $"{lineLen:0.00} m lap");
                    break;
                }

            float sum = set.TotalTarget;
            float lap = set.line.predictedLapSec;
            if (lap > 0.01f && Mathf.Abs(sum - lap) / lap > 0.01f)
                Fail($"'{scene}' sector targets sum to {sum:0.000} s against a " +
                     $"{lap:0.000} s predicted lap (>1% apart) — the sectors and the " +
                     "line have diverged");

            // CsvLogger joins column names with ',' and does no quoting, so a comma
            // in a channel name silently corrupts every CSV from that session.
            foreach (string ch in new[] { "race/sector", "race/sector_time", "race/sector_delta" })
                if (string.IsNullOrEmpty(ch) || ch.Contains(",") || ch.Contains("\n"))
                    Fail($"telemetry channel name '{ch}' would corrupt the CSV");
        }

        private static void CheckMarkers(string scene, SceneTrackDescriptor d)
        {
            var finish = d.Finish();
            var finishes = Object.FindObjectsByType<TrackFinishMarker>(FindObjectsSortMode.None);
            var cps = d.Checkpoints();
            var spawns = d.Spawns();

            if (spawns.Count == 0)
                Fail($"'{scene}' has no spawn marker");

            // A component whose script asset cannot be resolved reloads as Missing
            // Script — invisible to GetComponentsInChildren, so it is invisible to
            // every count above and to the seeding in MakeCurrentSceneATrack, which
            // then adds a duplicate on each run. Cheap to detect, expensive to
            // notice by eye: this is what put two "Spawn" objects in TTA_Sandbox.
            int missing = 0;
            foreach (var t in d.GetComponentsInChildren<Transform>(true))
                missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
            if (missing > 0)
                Fail($"'{scene}' has {missing} component(s) with a missing script under " +
                     "the descriptor. Run Track Studio > Repair markers. Usual cause: a " +
                     "MonoBehaviour sharing a .cs file with another class, so Unity has " +
                     "no MonoScript asset for it.");

            string grid = TrackStudioWindow.CheckGrid(spawns);
            if (grid != null) Fail($"'{scene}' {grid}");

            for (int i = 0; i < spawns.Count; i++)
                for (int j = i + 1; j < spawns.Count; j++)
                    if (Vector3.Distance(spawns[i].transform.position,
                                         spawns[j].transform.position) < 0.5f)
                        Fail($"'{scene}' spawns '{spawns[i].name}' and '{spawns[j].name}' " +
                             "are within 0.5 m — cars would spawn inside each other");

            // What is checked is the MARKERS, not the kind — because that is what
            // SceneTrackBuilder.BuildGates reads. A finish marker builds a LapTimer
            // on any scene track, so a FreeRoam map is allowed to carry one and time
            // laps: a place to practise a line, with a clock, and nothing to win.
            // The kind decides what the map is offered FOR (the race picker drops
            // FreeRoam), not whether its gates work.
            if (d.kind == TrackPresets.TrackKind.Circuit && finish == null)
                Fail($"'{scene}' is a Circuit with no finish marker — no LapTimer, " +
                     "so no lap can ever be counted");

            // Arena is the one kind where a finish line is still wrong: the arena
            // modes are won on goals, flags or survival, and ArenaNav never asks a
            // lap timer anything. A gate there is an authoring slip, not a feature.
            if (d.kind == TrackPresets.TrackKind.Arena && finishes.Length > 0)
                Fail($"'{scene}' is an Arena but carries a finish marker — the arena " +
                     "modes have no lap to time");

            if (finishes.Length > 1)
                Fail($"'{scene}' has {finishes.Length} finish markers; only the first " +
                     "would become the LapTimer");

            if (finish != null)
            {
                if (cps.Count == 0)
                    Fail($"'{scene}' has a finish marker and no checkpoints — a lap " +
                         "would count on any crossing of the line, including driving " +
                         "backwards over it");

                string orders = TrackStudioWindow.CheckOrders(cps);
                if (orders != null) Fail($"'{scene}': {orders}");
            }

            // A gate narrower than the road is a gate cars drive around. This is the
            // classic "my lap does not count and I cannot see why" bug.
            if (d.HasCorridor)
            {
                if (finish != null) CheckGateWidth(scene, d, finish.transform.position,
                    finish.gateWidth, finish.name);
                foreach (var c in cps)
                    CheckGateWidth(scene, d, c.transform.position, c.gateWidth, c.name);
            }
        }

        private static void CheckGateWidth(string scene, SceneTrackDescriptor d,
            Vector3 pos, float gateWidth, string name)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < d.centerline.Length; i++)
            {
                float sq = (d.centerline[i] - pos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            if (best < 0) return;
            float roadWidth = d.halfWidths[best] * 2f;
            if (gateWidth + 1e-3f < roadWidth)
                Fail($"'{scene}' gate '{name}' is {gateWidth:0.00} m across a " +
                     $"{roadWidth:0.00} m road — a car can pass beside it and the lap " +
                     "will never validate");
        }

        private static void CheckSurfaces(string scene, SceneTrackDescriptor d)
        {
            int floors = TrackCatalog.Floors.Length;

            if (d.sceneFallbackFloor < 0 || d.sceneFallbackFloor >= floors)
                Fail($"'{scene}' sceneFallbackFloor {d.sceneFallbackFloor} is outside " +
                     $"0..{floors - 1}");

            foreach (var tag in Object.FindObjectsByType<SurfaceTag>(FindObjectsSortMode.None))
                if (tag.floorType < 0 || tag.floorType >= floors)
                    Fail($"'{scene}' SurfaceTag on '{tag.name}' has floorType " +
                         $"{tag.floorType}, outside 0..{floors - 1}");

            var terrains = Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            if (terrains.Length == 0) return;

            if (d.terrainFloors == null)
            {
                Fail($"'{scene}' has {terrains.Length} terrain(s) but no TerrainFloorTable — " +
                     "every one of them would drive as the 1.0 baseline regardless of paint");
                return;
            }

            foreach (var row in d.terrainFloors.rows)
                if (row.floorType < 0 || row.floorType >= floors)
                    Fail($"'{scene}' TerrainFloorTable maps " +
                         $"'{(row.layer != null ? row.layer.name : "<null>")}' to floorType " +
                         $"{row.floorType}, outside 0..{floors - 1}");

            // A layer with no row silently falls to the default, which looks exactly
            // like a paint job that did nothing.
            var seen = new HashSet<TerrainLayer>();
            foreach (var t in terrains)
            {
                if (t.terrainData == null) continue;
                foreach (var layer in t.terrainData.terrainLayers)
                {
                    if (layer == null || !seen.Add(layer)) continue;
                    bool mapped = false;
                    foreach (var row in d.terrainFloors.rows)
                        if (row.layer == layer) { mapped = true; break; }
                    if (!mapped)
                        Fail($"'{scene}' TerrainLayer '{layer.name}' has no row in " +
                             $"'{d.terrainFloors.name}' — it would fall back to floor " +
                             $"{d.terrainFloors.defaultFloorType}");
                }
            }
        }

        /// <summary>
        /// The stale-bake case: edit the spline, forget to re-bake, and the line now
        /// cuts a corner that no longer exists. This is the single most likely real
        /// failure of the whole tool, which is why the hash is stamped at bake time
        /// purely so it can be checked here.
        /// </summary>
        private static void CheckRacingLine(string scene, SceneTrackDescriptor d)
        {
            var rl = d.racingLine;
            if (rl == null) return;   // no line is fine; a WRONG line is not

            if (!rl.IsUsable)
            {
                Fail($"'{scene}' racing line '{rl.name}' has mismatched or empty arrays");
                return;
            }
            if (rl.sceneName != scene)
                Fail($"'{scene}' references racing line '{rl.name}' baked from " +
                     $"'{rl.sceneName}'");

            if (!d.HasCorridor)
            {
                Fail($"'{scene}' has a racing line but no corridor to check it against");
                return;
            }

            var nodes = RacingLineBaker.CorridorFromDescriptor(d);
            string hash = RaceLineSolver.HashCorridor(nodes, d.corridorClosed);
            if (rl.bakeHash != hash)
            {
                Fail($"'{scene}' racing line is STALE — the corridor has changed since it " +
                     "was baked. Re-bake before trusting the line or its sector targets");
                return;
            }

            if (rl.points.Length != nodes.Length)
            {
                Fail($"'{scene}' racing line has {rl.points.Length} nodes for a " +
                     $"{nodes.Length}-node corridor");
                return;
            }

            for (int i = 0; i < nodes.Length; i++)
            {
                float lat = rl.lateral != null && i < rl.lateral.Length ? rl.lateral[i] : 0f;
                float limit = Mathf.Max(nodes[i].halfLeft, nodes[i].halfRight) + 1e-3f;
                if (Mathf.Abs(lat) > limit)
                {
                    Fail($"'{scene}' racing line node {i} sits {Mathf.Abs(lat):0.000} m off " +
                         $"centre, outside the {limit:0.000} m usable corridor");
                    break;   // one report is enough; they come in runs
                }
            }

            // A calibration that claims to be valid must actually be defensible.
            var c = rl.calibration;
            if (c.valid)
            {
                if (c.residualPct > 2f)
                    Fail($"'{scene}' calibration claims valid with a {c.residualPct:0.0}% " +
                         "residual (limit 2%)");
                if (c.lapRepeatDeltaSec > 0.02f)
                    Fail($"'{scene}' calibration laps 2 and 3 differ by " +
                         $"{c.lapRepeatDeltaSec * 1000f:0} ms — the run was not " +
                         "deterministic, so the fit is noise");
                if (c.limitFraction < 0.5f)
                    Fail($"'{scene}' calibration only reached the grip limit on " +
                         $"{c.limitFraction:P0} of the lap — the fit is extrapolation");
            }
        }

        // -------------------------------------------------------------------

        /// <summary>
        /// PathCurvature and RaceLineSolver deliberately duplicate constants and
        /// maths from BotDriver so that BotDriver could stay untouched. Duplication
        /// that is checked is a tradeoff; duplication that is not is a bug waiting.
        /// </summary>
        private static void CheckConstantParity()
        {
            // Mirrors BotDriver's CarHalfWidth = 0.20f, EdgeMargin = 0.30f,
            // KappaRef = 0.18f. If BotDriver changes, this fails and someone reads
            // both files, which is exactly the intent.
            if (!Mathf.Approximately(RaceLineSolver.CarHalfWidth, 0.20f))
                Fail($"RaceLineSolver.CarHalfWidth is {RaceLineSolver.CarHalfWidth}, " +
                     "BotDriver uses 0.20");
            if (!Mathf.Approximately(RaceLineSolver.EdgeMargin, 0.30f))
                Fail($"RaceLineSolver.EdgeMargin is {RaceLineSolver.EdgeMargin}, " +
                     "BotDriver uses 0.30");
            if (!Mathf.Approximately(RaceLineSolver.KappaRef, 0.18f))
                Fail($"RaceLineSolver.KappaRef is {RaceLineSolver.KappaRef}, " +
                     "BotDriver uses 0.18");

            // The curvature recipe itself, on a circle of known radius: kappa must
            // be 1/r. This is what actually catches a divergence in the maths rather
            // than in the constants.
            const float R = 4f;
            const int N = 128;
            var circle = new Vector3[N];
            for (int i = 0; i < N; i++)
            {
                // Clockwise seen from above = a right turn = positive kappa.
                float a = -2f * Mathf.PI * i / N;
                circle[i] = new Vector3(Mathf.Cos(a) * R, 0f, Mathf.Sin(a) * R);
            }
            var k = PathCurvature.Signed(circle, closed: true);
            float expect = 1f / R;
            for (int i = 0; i < N; i++)
                if (Mathf.Abs(k[i] - expect) > 0.02f)
                {
                    Fail($"PathCurvature on a {R} m circle gives {k[i]:0.0000} at node {i}, " +
                         $"expected {expect:0.0000} (+ve = right turn)");
                    break;
                }
        }

        /// <summary>
        /// The solver must be reproducible: same corridor in, same line out. An
        /// unseeded RNG or an iteration-order dependence sneaking into the SOR sweep
        /// would make every re-bake a diff and every stale-hash check meaningless.
        /// </summary>
        private static void CheckSolverDeterminism()
        {
            var nodes = new RaceLineSolver.Node[64];
            for (int i = 0; i < nodes.Length; i++)
            {
                float a = 2f * Mathf.PI * i / nodes.Length;
                var c = new Vector3(Mathf.Cos(a) * 8f, 0f, Mathf.Sin(a) * 5f);
                var tan = new Vector3(-Mathf.Sin(a) * 8f, 0f, Mathf.Cos(a) * 5f).normalized;
                nodes[i] = new RaceLineSolver.Node
                {
                    center = c,
                    right = Vector3.Cross(Vector3.up, tan).normalized,
                    halfLeft = 0.8f,
                    halfRight = 0.8f,
                    surface = 1,
                };
            }

            var a1 = RaceLineSolver.Solve(nodes, true, RaceLineSolver.Settings.Default);
            var a2 = RaceLineSolver.Solve(nodes, true, RaceLineSolver.Settings.Default);
            for (int i = 0; i < a1.points.Length; i++)
                if (Vector3.Distance(a1.points[i], a2.points[i]) > 1e-4f)
                {
                    Fail($"RaceLineSolver is not deterministic: node {i} moved " +
                         $"{Vector3.Distance(a1.points[i], a2.points[i]):0.000000} m " +
                         "between two identical solves");
                    break;
                }

            // The blend must actually do something, or a slider that reads as tuning
            // is really doing nothing at all.
            var inner = RaceLineSolver.Settings.Default;
            inner.shortestPathBlend = 1f;
            var a3 = RaceLineSolver.Solve(nodes, true, inner);
            if (a3.length >= a1.length)
                Fail($"shortest-path blend is inert: blend=1 gives {a3.length:0.000} m, " +
                     $"blend=default gives {a1.length:0.000} m");
        }

        // -------------------------------------------------------------------

        private static string PathForScene(string sceneName)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Scene " + sceneName))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(p) == sceneName) return p;
            }
            return null;
        }

        private static bool InBuildSettings(string path)
        {
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled && s.path.Replace('\\', '/') == path) return true;
            return false;
        }
    }
}
