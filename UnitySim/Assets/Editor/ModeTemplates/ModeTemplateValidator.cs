using System.Collections.Generic;
using System.IO;
using AIHWSim.Core;
using AIHWSim.Core.Boot;
using AIHWSim.Modes;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <c>[TPL]</c> — open every generated template scene and check that it is
    /// still the thing the builder promised.
    ///
    /// The templates are the one part of this feature that lives in scene files
    /// rather than in code, which makes them the one part that can rot silently:
    /// a renamed component, a moved marker, a mode enum reordered, a script whose
    /// file no longer matches its class. Every check below is something that
    /// would present at Play as a mode quietly not composing, and nothing below
    /// is a check the builder could pass by construction — it re-reads what was
    /// serialized, which is the only copy the game will ever see.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.ModeTemplateValidator.Report -logFile &lt;log&gt;
    /// </code>
    /// </summary>
    public static class ModeTemplateValidator
    {
        private const string Tag = "[TPL]";

        private static readonly List<string> Fails = new List<string>();
        private static int _checks;

        [MenuItem("Tools/AIHWSim/Mode Templates/Validate Templates [TPL]", priority = 110)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            Fails.Clear();
            _checks = 0;

            foreach (var e in ModeTemplateBuilder.All) CheckScene(e);

            foreach (string f in Fails) Debug.LogError($"{Tag} FAIL {f}");
            string line = Fails.Count == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks over "
                  + $"{ModeTemplateBuilder.All.Length} scenes)"
                : $"{Tag} RESULT {Fails.Count} FAILED of {_checks} checks";
            if (Fails.Count == 0) Debug.Log(line); else Debug.LogError(line);

            if (exitWhenDone) EditorApplication.Exit(Fails.Count == 0 ? 0 : 1);
        }

        // ---- one scene ---------------------------------------------------------

        private static void CheckScene(ModeTemplateBuilder.Entry e)
        {
            _checks++;
            if (!File.Exists(e.ScenePath))
            {
                Fails.Add($"{e.Name}: scene file missing — run "
                          + "Tools ▸ AIHWSim ▸ Mode Templates ▸ Create All Template Scenes");
                return;
            }

            EditorSceneManager.OpenScene(e.ScenePath, OpenSceneMode.Single);

            CheckNoMissingScripts(e);
            CheckOneSun(e);

            // The RC plane template is a flight scene: no track descriptor, no
            // driving rules, no grid. Checked for what it DOES have and then left
            // alone rather than run through checks that would all read "n/a".
            if (e.Id == ModeTemplateBuilder.Template.RcPlane) { CheckRcPlane(e); return; }

            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            var driving = Object.FindFirstObjectByType<DrivingSceneDescriptor>();
            var boot = Object.FindFirstObjectByType<TrackBootstrap>();

            _checks += 3;
            if (d == null) { Fails.Add($"{e.Name}: no SceneTrackDescriptor"); return; }
            if (driving == null) { Fails.Add($"{e.Name}: no DrivingSceneDescriptor"); return; }
            if (boot == null) { Fails.Add($"{e.Name}: no TrackBootstrap"); return; }

            // The oval would be built ON TOP of the authored geometry, which is
            // the one setting that silently ruins a template.
            _checks++;
            if (boot.buildDefaultOval)
                Fails.Add($"{e.Name}: TrackBootstrap still builds the default oval, "
                          + "which would land a procedural loop on top of this scene");

            _checks++;
            var level = driving.level;
            if (level == null) { Fails.Add($"{e.Name}: DrivingSceneDescriptor has no LevelSettings"); return; }

            _checks++;
            var wantMode = ExpectedMode(e.Id);
            if (level.match != wantMode)
                Fails.Add($"{e.Name}: LevelSettings says {level.match}, the template is "
                          + $"{wantMode} — a template that runs a different mode than its "
                          + "name is the whole thing this gate is for");

            CheckFloor(e, d);
            CheckSpawns(e, d, wantMode);
            CheckKillPlane(e);
            CheckRaceGates(e, d, level);
            CheckArena(e, d, wantMode);
        }

        /// <summary>What each template claims to be. A literal table rather than a
        /// read of the asset, because the asset is the thing under test.</summary>
        private static MatchMode ExpectedMode(ModeTemplateBuilder.Template id) => id switch
        {
            ModeTemplateBuilder.Template.FreeRoam => MatchMode.FreeRoam,
            ModeTemplateBuilder.Template.Soccer => MatchMode.Soccer,
            ModeTemplateBuilder.Template.Ctf => MatchMode.Ctf,
            ModeTemplateBuilder.Template.Demolition => MatchMode.Derby,
            _ => MatchMode.Race,   // the two sandboxes and the two races
        };

        // ---- checks ------------------------------------------------------------

        /// <summary>
        /// A component whose MonoScript could not be resolved serializes as a
        /// stub, reloads as Missing, and is invisible to
        /// <c>GetComponentsInChildren</c> — so a goal, a spawn or a kill plane can
        /// stop existing with nothing in the console. This is the check that
        /// notices.
        /// </summary>
        private static void CheckNoMissingScripts(ModeTemplateBuilder.Entry e)
        {
            int missing = 0;
            foreach (var go in Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var c in go.GetComponents<Component>())
                    if (c == null) missing++;

            _checks++;
            if (missing > 0)
                Fails.Add($"{e.Name}: {missing} Missing Script component(s) — a "
                          + "MonoBehaviour authored into a saved scene needs its own "
                          + "file, named after the class");
        }

        /// <summary>Exactly one enabled directional light. Two suns double every
        /// shadow and wash the scene out, and none renders a black world; both are
        /// easy to arrive at by adding an object and hard to see in a diff.</summary>
        private static void CheckOneSun(ModeTemplateBuilder.Entry e)
        {
            int suns = 0;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.enabled) suns++;

            _checks++;
            if (suns != 1)
                Fails.Add($"{e.Name}: {suns} enabled directional light(s), expected 1");
        }

        /// <summary>A floor with a collider and a surface tag. Without the tag the
        /// whole scene drives as the 1.0 baseline whatever it looks like.</summary>
        private static void CheckFloor(ModeTemplateBuilder.Entry e, SceneTrackDescriptor d)
        {
            var floor = GameObject.Find("Floor");
            _checks++;
            if (floor == null) { Fails.Add($"{e.Name}: no object named 'Floor'"); return; }

            _checks++;
            if (floor.GetComponent<Collider>() == null)
                Fails.Add($"{e.Name}: the floor has no collider — the cars would fall through it");

            _checks++;
            var tag = floor.GetComponent<SurfaceTag>();
            if (tag == null)
                Fails.Add($"{e.Name}: the floor has no SurfaceTag, so it drives as the "
                          + "1.0 baseline whatever it is painted");
            else if (tag.floorType != d.sceneFallbackFloor)
                Fails.Add($"{e.Name}: floor SurfaceTag is {tag.floorType} but the "
                          + $"descriptor's fallback is {d.sceneFallbackFloor}");
        }

        /// <summary>Spawns must be a dense 0..n-1 run: <c>TrackBootstrap</c> asks
        /// for slot i by <c>gridOrder</c>, so one gap drops a car onto the
        /// procedural fallback row without saying so. A team mode additionally
        /// needs both sides present, or one team has no end to defend.</summary>
        private static void CheckSpawns(ModeTemplateBuilder.Entry e, SceneTrackDescriptor d,
                                        MatchMode mode)
        {
            var spawns = d.Spawns();
            _checks++;
            if (spawns.Count == 0)
            {
                Fails.Add($"{e.Name}: no spawn markers — the car would start at the "
                          + "descriptor's own transform");
                return;
            }

            _checks++;
            for (int i = 0; i < spawns.Count; i++)
                if (spawns[i].gridOrder != i)
                {
                    Fails.Add($"{e.Name}: spawn gridOrder is not a dense 0..{spawns.Count - 1} "
                              + $"run (slot {i} says {spawns[i].gridOrder})");
                    break;
                }

            if (mode != MatchMode.Soccer && mode != MatchMode.Ctf) return;

            bool blue = false, orange = false;
            foreach (var s in spawns)
            {
                if (s.team == 0) blue = true;
                if (s.team == 1) orange = true;
            }
            _checks++;
            if (!blue || !orange)
                Fails.Add($"{e.Name}: {mode} needs spawns on both teams "
                          + $"(blue {blue}, orange {orange})");
        }

        private static void CheckKillPlane(ModeTemplateBuilder.Entry e)
        {
            var kill = Object.FindFirstObjectByType<KillPlane>();
            _checks++;
            if (kill == null)
            {
                Fails.Add($"{e.Name}: no KillPlane — a car driven off the slab falls forever");
                return;
            }

            _checks++;
            var col = kill.GetComponent<Collider>();
            if (col == null) Fails.Add($"{e.Name}: the KillPlane has no collider");
            else if (col.bounds.max.y > 0f)
                Fails.Add($"{e.Name}: the KillPlane reaches y={col.bounds.max.y:0.00}, which is "
                          + "at or above the floor — it would catch cars that are driving");
        }

        /// <summary>
        /// A race needs a finish line and checkpoints that form a dense run: a
        /// single gap makes the track permanently un-lappable, with no error
        /// anywhere. A sprint additionally has to claim <c>pointToPoint</c> and
        /// put its finish somewhere other than its grid.
        /// </summary>
        private static void CheckRaceGates(ModeTemplateBuilder.Entry e, SceneTrackDescriptor d,
                                           Core.Config.LevelSettings level)
        {
            bool racing = level.match == MatchMode.Race && level.targetLaps > 0;
            if (!racing)
            {
                // The other way round matters too: a free-drive template carrying a
                // finish line is a template whose author changed their mind halfway.
                _checks++;
                if (d.Finish() != null)
                    Fails.Add($"{e.Name}: has a finish marker but runs no race "
                              + $"({level.match}, {level.targetLaps} laps)");
                return;
            }

            var finish = d.Finish();
            _checks++;
            if (finish == null)
            {
                Fails.Add($"{e.Name}: a {level.targetLaps}-lap race with no finish marker — "
                          + "the race would refuse to compose and fall back to a free drive");
                return;
            }

            var cps = d.Checkpoints();
            _checks++;
            for (int i = 0; i < cps.Count; i++)
                if (cps[i].order != i)
                {
                    Fails.Add($"{e.Name}: checkpoint order is not a dense 0..{cps.Count - 1} "
                              + $"run (position {i} says {cps[i].order})");
                    break;
                }

            _checks++;
            if (!d.HasCorridor)
                Fails.Add($"{e.Name}: no baked bot corridor — the opponents have nothing "
                          + "to follow. Re-run the builder, which bakes it");

            bool sprint = e.Id == ModeTemplateBuilder.Template.SprintRace;
            _checks++;
            if (d.pointToPoint != sprint)
                Fails.Add($"{e.Name}: pointToPoint is {d.pointToPoint}, expected {sprint}");

            if (!sprint) return;

            _checks++;
            if (level.targetLaps != 1)
                Fails.Add($"{e.Name}: a point-to-point course set to {level.targetLaps} laps — "
                          + "more than one lap of a course you cannot return to the start of "
                          + "is unfinishable");

            // The claim a sprint makes is that the finish is a DESTINATION. A gate
            // sitting on the grid would be one the field crosses at GO.
            var spawns = d.Spawns();
            _checks++;
            if (spawns.Count > 0)
            {
                float near = Vector3.Distance(finish.transform.position,
                                              spawns[0].transform.position);
                if (near < 5f)
                    Fails.Add($"{e.Name}: the finish gate is {near:0.0} m from the grid — "
                              + "on a point-to-point course the field would cross it at GO");
            }
        }

        private static void CheckArena(ModeTemplateBuilder.Entry e, SceneTrackDescriptor d,
                                       MatchMode mode)
        {
            bool arena = mode == MatchMode.Soccer || mode == MatchMode.Ctf
                      || mode == MatchMode.Derby;
            if (!arena)
            {
                _checks++;
                if (d.kind == TrackPresets.TrackKind.Arena)
                    Fails.Add($"{e.Name}: claims TrackKind.Arena but runs {mode}");
                return;
            }

            _checks++;
            if (d.kind != TrackPresets.TrackKind.Arena)
                Fails.Add($"{e.Name}: runs {mode} but its kind is {d.kind}");

            _checks++;
            if (d.playfield == null)
                Fails.Add($"{e.Name}: an arena with no Playfield collider — ArenaNav would "
                          + "fall back to averaging the spawn ring for the centre, the "
                          + "radius and the containment test");

            switch (mode)
            {
                case MatchMode.Soccer:
                    CheckPair<ArenaGoalMarker>(e, "goal", m => m.team);
                    _checks++;
                    if (Object.FindFirstObjectByType<ArenaBallSpawn>() == null)
                        Fails.Add($"{e.Name}: no ArenaBallSpawn — the ball would kick off "
                                  + "from the middle of the floor, which is not this pitch's "
                                  + "centre spot unless it happens to be");
                    break;

                case MatchMode.Ctf:
                    CheckPair<CtfBaseMarker>(e, "CTF base", m => m.team);
                    break;

                case MatchMode.Derby:
                    var picks = Object.FindObjectsByType<ArenaPickupMarker>(
                        FindObjectsSortMode.None);
                    _checks++;
                    if (picks.Length == 0)
                        Fails.Add($"{e.Name}: no ArenaPickupMarkers — the derby would "
                                  + "scatter its own, which is correct behaviour and not "
                                  + "what this template is demonstrating");
                    break;
            }
        }

        /// <summary>Exactly one marker of this kind per team. Two for the same team
        /// is an authoring slip the directors resolve by taking the first, which is
        /// arbitrary; none for a team sends that end back to the spawn-ring
        /// fallback with nothing said.</summary>
        private static void CheckPair<T>(ModeTemplateBuilder.Entry e, string what,
                                         System.Func<T, int> teamOf) where T : Component
        {
            var found = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            for (int team = 0; team < 2; team++)
            {
                int n = 0;
                foreach (var m in found) if (Mathf.Clamp(teamOf(m), 0, 1) == team) n++;
                _checks++;
                if (n != 1)
                    Fails.Add($"{e.Name}: {n} {what} marker(s) for team {team}, expected 1");
            }
        }

        private static void CheckRcPlane(ModeTemplateBuilder.Entry e)
        {
            _checks++;
            if (Object.FindFirstObjectByType<AIHWSim.Core.Flight.RcPlaneBootstrap>() == null)
                Fails.Add($"{e.Name}: no RcPlaneBootstrap — nothing would build an aircraft");

            _checks++;
            if (Object.FindFirstObjectByType<AIHWSim.Core.Flight.AirspaceBounds>() == null)
                Fails.Add($"{e.Name}: no AirspaceBounds — the field has no stated limits");

            _checks++;
            if (GameObject.Find("SpawnPoint") == null)
                Fails.Add($"{e.Name}: no SpawnPoint object");
        }
    }
}
