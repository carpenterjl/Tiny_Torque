using System.Collections.Generic;
using System.IO;
using AIHWSim.Arcade;
using AIHWSim.Core;
using AIHWSim.Core.Boot;
using AIHWSim.Core.Config;
using AIHWSim.Core.Flight;
using AIHWSim.Modes;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using AIHWSim.TrackTools;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// One starting scene per mode, built at EDIT time — every floor, wall, gate,
    /// goal and spawn a real GameObject you can select and drag before you press
    /// Play.
    ///
    /// <b>What these are for.</b> Every mode in this project composes itself at
    /// Play time: <c>TrackBootstrap</c> builds the oval, <c>SoccerDirector</c>
    /// places the goals from the spawn ring, <c>DerbyDirector</c> scatters
    /// pickups on two rings. That is the right behaviour for a map that has said
    /// nothing, and it stays the fallback. These scenes are the other half: the
    /// minimum a mode actually needs, authored, so you can see the shape of a
    /// mode before you own a level — and then copy one as the seed of a real
    /// level rather than starting from an empty scene and a wiki page.
    ///
    /// <b>Generated, not hand-authored.</b> Re-running the menu item rebuilds
    /// them from this file, which is what keeps nine scene files from drifting
    /// away from the code they demonstrate. Edit a template freely — it is a
    /// starting point — but expect a regenerate to replace it, and put changes
    /// worth keeping here.
    ///
    /// <b>Deliberately NOT in Build Settings</b>, following the ten physics test
    /// scenes and <c>PhysicsDebugScene</c>: <c>EditorSceneManager.OpenScene</c>
    /// reaches an unregistered scene, so nothing needs the entry, and staying out
    /// of <c>EditorBuildSettings.asset</c> leaves the Release build and the
    /// in-game track pickers exactly as they are. A template is a tool.
    ///
    /// <b>Edit-time hygiene.</b> Materials are find-or-create ASSETS keyed by
    /// colour, and colliders are stripped with <c>DestroyImmediate</c> — the same
    /// two rules <see cref="SceneBuildContext"/> exists to carry into the flight
    /// builders, and for the same reason: <c>renderer.material</c> instantiates
    /// per object, and forty of those saved into a scene are forty embedded
    /// sub-assets nobody asked for.
    /// </summary>
    public static class ModeTemplateBuilder
    {
        public const string SceneDir = "Assets/Scenes/ModeTemplates";
        private const string MatDir = SceneDir + "/Materials";
        private const string LevelDir = "Assets/Settings/Driving/Templates";

        /// <summary>Floor ids from <c>TrackCatalog.Floors</c>, named so the geometry
        /// below reads as a surface rather than as an integer.</summary>
        private const int Asphalt = 1, Grass = 2, Ice = 4;

        /// <summary>How high the baked road ribbon rides above the outfield it
        /// crosses. Two coplanar surfaces z-fight, and a road that flickers
        /// against its own grass is the first thing anybody would report; 2 cm is
        /// invisible under a 33 mm wheel and unambiguous to the depth buffer.
        /// Everything placed ON the road — gates, grid slots, item boxes — is
        /// lifted by the same amount.</summary>
        private const float RoadY = 0.02f;

        public enum Template
        {
            SimPhysics, ArcadePhysics, FreeRoam,
            LapRace, SprintRace,
            Soccer, Ctf, Demolition,
            RcPlane,
        }

        public readonly struct Entry
        {
            public readonly Template Id;
            public readonly string Label;
            public Entry(Template id, string label) { Id = id; Label = label; }
            public string Name => $"Template_{Id}";
            public string ScenePath => $"{SceneDir}/{Name}.unity";
        }

        /// <summary>The suite, in the order it is meant to be read: the two
        /// handling sandboxes first (they are the smallest thing that is a
        /// driving scene), then the two races, then the three arena modes, then
        /// the aircraft.</summary>
        public static readonly Entry[] All =
        {
            new Entry(Template.SimPhysics,    "Simulation physics"),
            new Entry(Template.ArcadePhysics, "Arcade physics"),
            new Entry(Template.FreeRoam,      "Free roam"),
            new Entry(Template.LapRace,       "Lapped race"),
            new Entry(Template.SprintRace,    "Point-to-point race"),
            new Entry(Template.Soccer,        "Soccer"),
            new Entry(Template.Ctf,           "Capture the flag"),
            new Entry(Template.Demolition,    "Demolition derby"),
            new Entry(Template.RcPlane,       "RC plane"),
        };

        public static Entry? Find(Template id)
        {
            foreach (var e in All) if (e.Id == id) return e;
            return null;
        }

        // ---- entry points ---------------------------------------------------

        public static void CreateAll()
        {
            Directory.CreateDirectory(SceneDir);
            Directory.CreateDirectory(MatDir);
            Directory.CreateDirectory(LevelDir);
            AssetDatabase.Refresh();

            foreach (var e in All) Create(e);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[TPL] built {All.Length} template scenes in {SceneDir}");
        }

        /// <summary>Headless entry: build every template, no dialogs, exit. What
        /// the batch verification calls, so the scenes in the repo are this
        /// file's output rather than a hand's.</summary>
        public static void CreateAllHeadless()
        {
            CreateAll();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void Create(Entry e)
        {
            _mats.Clear();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            switch (e.Id)
            {
                case Template.SimPhysics: BuildSimPhysics(e); break;
                case Template.ArcadePhysics: BuildArcadePhysics(e); break;
                case Template.FreeRoam: BuildFreeRoam(e); break;
                case Template.LapRace: BuildLapRace(e); break;
                case Template.SprintRace: BuildSprintRace(e); break;
                case Template.Soccer: BuildSoccer(e); break;
                case Template.Ctf: BuildCtf(e); break;
                case Template.Demolition: BuildDemolition(e); break;
                case Template.RcPlane: BuildRcPlane(e); break;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, e.ScenePath);
        }

        // ---- the driving templates -------------------------------------------

        private static void BuildSimPhysics(Entry e)
        {
            var w = Common(e, 60f, 60f, Asphalt, TrackPresets.TrackKind.FreeRoam);
            Spawn(w.track, 0, 0, new Vector3(0f, 0f, -20f), 0f);

            var lvl = Level(e);
            lvl.match = MatchMode.Race;
            lvl.targetLaps = 0;                 // a free drive: nothing to win
            lvl.arcade = false;
            lvl.arcadeHandling = false;         // the honest brush-tyre model
            Save(lvl);
        }

        private static void BuildArcadePhysics(Entry e)
        {
            var w = Common(e, 60f, 60f, Asphalt, TrackPresets.TrackKind.FreeRoam);

            // A low-grip pad to slide on. Its top sits 2 mm PROUD of the asphalt
            // rather than flush: two coplanar surfaces z-fight, and 2 mm under a
            // 33 mm wheel is a step no driver will find.
            var pad = Slab("DriftPad", new Vector3(0f, 0.002f, 0f), new Vector2(24f, 24f),
                           0.4f, new Color(0.62f, 0.78f, 0.88f), w.env, collider: true);
            pad.AddComponent<SurfaceTag>().floorType = Ice;

            Walls(w.env, 60f, 60f, 0.5f, new Color(0.72f, 0.68f, 0.30f));
            Spawn(w.track, 0, 0, new Vector3(0f, 0f, -20f), 0f);

            var lvl = Level(e);
            lvl.match = MatchMode.Race;
            lvl.targetLaps = 0;
            lvl.arcade = false;                 // no item boxes: they need a lap count
            lvl.arcadeHandling = true;          // the grip floor and the drift model
            Save(lvl);
        }

        private static void BuildFreeRoam(Entry e)
        {
            var w = Common(e, 80f, 80f, Asphalt, TrackPresets.TrackKind.FreeRoam);

            // Something to drive AT. A town is out of scope for a template; four
            // blocks and a ramp are enough to prove a car, a camera and a floor.
            var blockMat = new Color(0.55f, 0.55f, 0.60f);
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + Mathf.PI * 0.25f;
                Slab($"Block_{i}", new Vector3(Mathf.Cos(a) * 12f, 1.2f, Mathf.Sin(a) * 12f),
                     new Vector2(2.4f, 2.4f), 2.4f, blockMat, w.env);
            }
            var ramp = Slab("Ramp", new Vector3(0f, 0.6f, 22f), new Vector2(6f, 8f),
                            0.3f, new Color(0.42f, 0.32f, 0.22f), w.env);
            ramp.transform.rotation = Quaternion.Euler(-9f, 0f, 0f);

            for (int i = 0; i < 4; i++)
                Spawn(w.track, i, 0, new Vector3((i - 1.5f) * 1.2f, 0f, -24f), 0f);

            var lvl = Level(e);
            lvl.match = MatchMode.FreeRoam;     // no director, no clock, nothing to win
            lvl.targetLaps = 0;
            Save(lvl);
        }

        private static void BuildLapRace(Entry e)
        {
            var w = Common(e, 100f, 80f, Grass, TrackPresets.TrackKind.Circuit);

            // A closed oval, 24 x 15 m, as a real Unity spline: the road is baked
            // ribbon geometry and the bot corridor comes off the same curve, so
            // widening a corner here does both.
            const float rx = 24f, rz = 15f;
            var pts = new List<Vector3>();
            for (int i = 0; i < 12; i++)
            {
                float t = i * Mathf.PI * 2f / 12f;
                pts.Add(new Vector3(Mathf.Cos(t) * rx, RoadY, Mathf.Sin(t) * rz));
            }
            SplineRoad("Circuit", pts, closed: true, width: 3.4f, surface: Asphalt);

            // Travel direction at angle t is the tangent (-rx sin t, 0, rz cos t),
            // so at t = 0 cars are heading +Z. Every gate below is placed on the
            // curve and turned to face along it — a gate rotated to "look nice" is
            // a gate cars pass beside.
            Gate<TrackFinishMarker>(w.track, 0f, rx, rz, 0);
            for (int i = 0; i < 3; i++)
                Gate<TrackCheckpointMarker>(w.track, (i + 1) * Mathf.PI * 0.5f, rx, rz, i);

            // Six on the grid, two abreast, behind the line and on the road.
            for (int i = 0; i < 6; i++)
                Spawn(w.track, i, 0,
                      new Vector3(rx + (i % 2 == 0 ? -0.85f : 0.85f), RoadY, -2f - (i / 2) * 1.6f),
                      0f);

            // Power-ups: two rows of three across the road, the layout
            // ArcadeDirector lays down itself on a map that authored none.
            var boxes = new GameObject("ItemBoxes").transform;
            foreach (float t in new[] { Mathf.PI * 0.5f, Mathf.PI * 1.5f })
            {
                Vector3 p = new Vector3(Mathf.Cos(t) * rx, RoadY, Mathf.Sin(t) * rz);
                Vector3 tangent = new Vector3(-Mathf.Sin(t) * rx, 0f, Mathf.Cos(t) * rz).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, tangent);
                for (int k = -1; k <= 1; k++)
                    ItemBox(p + right * (k * 0.9f) + Vector3.up * 0.16f, boxes);
            }

            SceneTrackSetup.BakeRoads();        // ribbon geometry + the bot corridor

            var lvl = Level(e);
            lvl.match = MatchMode.Race;
            lvl.targetLaps = 3;
            lvl.countdownSeconds = 3;
            lvl.arcade = true;
            lvl.trackLimits = true;
            lvl.arcadeHandling = true;
            lvl.botOpponents = 5;               // a full grid of six
            Save(lvl);
        }

        private static void BuildSprintRace(Entry e)
        {
            var w = Common(e, 130f, 70f, Grass, TrackPresets.TrackKind.Circuit);

            // An OPEN road from one end of the field to the other. The bake reads
            // the curve and records corridorClosed = false; pointToPoint below is
            // the separate claim that the finish gate is a destination.
            var pts = new List<Vector3>
            {
                new Vector3(-52f, RoadY, 0f),
                new Vector3(-26f, RoadY, 12f),
                new Vector3(0f, RoadY, -10f),
                new Vector3(26f, RoadY, 10f),
                new Vector3(52f, RoadY, 0f),
            };
            SplineRoad("Sprint", pts, closed: false, width: 3.4f, surface: Asphalt);

            // Gates face along the local direction of travel, computed from the
            // neighbouring control points rather than assumed.
            for (int i = 0; i < 3; i++)
                Marker<TrackCheckpointMarker>(w.track, pts[i + 1],
                    Heading(pts[i], pts[i + 2]), i);
            Marker<TrackFinishMarker>(w.track, pts[4], Heading(pts[3], pts[4]), 0);

            for (int i = 0; i < 4; i++)
                Spawn(w.track, i, 0,
                      pts[0] + new Vector3(0f, 0f, i % 2 == 0 ? -0.85f : 0.85f)
                             - new Vector3((i / 2) * 1.6f, 0f, 0f),
                      Heading(pts[0], pts[1]));

            SceneTrackSetup.BakeRoads();
            // The one thing that makes this a sprint rather than a one-lap circuit:
            // the run is timed from GO and the first crossing of the far gate ends
            // it. Set after the bake, which owns corridorClosed and nothing else.
            w.track.pointToPoint = true;

            var lvl = Level(e);
            lvl.match = MatchMode.Race;
            lvl.targetLaps = 1;                 // more than one lap of a course you
            lvl.countdownSeconds = 3;           // cannot return to is unfinishable
            lvl.arcadeHandling = true;
            lvl.botOpponents = 3;
            Save(lvl);
        }

        private static void BuildSoccer(Entry e)
        {
            var w = Arena(e, 24f, 16f);

            // Two goals, each a box the ball has to be inside. The marker's +Z
            // points INTO the net, away from the pitch, which is what puts the
            // mouth on the field side.
            Goal(0, new Vector3(-11.4f, 0.5f, 0f), Quaternion.LookRotation(Vector3.left));
            Goal(1, new Vector3(11.4f, 0.5f, 0f), Quaternion.LookRotation(Vector3.right));

            var spot = new GameObject("BallSpawn");
            spot.transform.position = new Vector3(0f, 0.2f, 0f);
            spot.AddComponent<ArenaBallSpawn>();

            // Two a side, facing the middle.
            for (int i = 0; i < 4; i++)
            {
                int team = i % 2;
                float x = team == 0 ? -6f : 6f;
                float z = i < 2 ? -3f : 3f;
                Spawn(w.track, i, team, new Vector3(x, 0f, z), team == 0 ? 90f : 270f);
            }

            var lvl = Level(e);
            lvl.match = MatchMode.Soccer;
            lvl.targetLaps = 0;
            lvl.targetScore = 3;
            lvl.countdownSeconds = 3;
            lvl.botOpponents = 3;               // 2 v 2 with you on blue
            Save(lvl);
        }

        private static void BuildCtf(Entry e)
        {
            var w = Arena(e, 30f, 20f);

            for (int team = 0; team < 2; team++)
            {
                var go = new GameObject($"Base_{team}");
                go.transform.position = new Vector3(team == 0 ? -12f : 12f, 0f, 0f);
                go.AddComponent<CtfBaseMarker>().team = team;
            }

            for (int i = 0; i < 6; i++)
            {
                int team = i % 2;
                float x = team == 0 ? -9f : 9f;
                Spawn(w.track, i, team, new Vector3(x, 0f, (i / 2 - 1) * 3f),
                      team == 0 ? 90f : 270f);
            }

            var lvl = Level(e);
            lvl.match = MatchMode.Ctf;
            lvl.targetLaps = 0;
            lvl.targetScore = 3;
            lvl.countdownSeconds = 3;
            lvl.botOpponents = 5;
            Save(lvl);
        }

        private static void BuildDemolition(Entry e)
        {
            var w = Arena(e, 26f, 26f);

            // Repairs on the outer ring, crates near the middle — the same shape
            // DerbyDirector scatters when a scene says nothing, authored here so
            // it can be dragged.
            var root = new GameObject("Pickups").transform;
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + Mathf.PI * 0.25f;
                Pickup(root, new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 8f,
                       ArenaPickup.Kind.Repair);
            }
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f;
                Pickup(root, new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 3.6f,
                       ArenaPickup.Kind.Mine);
            }

            // A ring of six, free-for-all: team -1 is what a derby roster gets,
            // and the markers carry 0 because a spawn's team means nothing here.
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI / 3f;
                var p = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 10f;
                Spawn(w.track, i, 0, p,
                      Quaternion.LookRotation(-p.normalized, Vector3.up).eulerAngles.y);
            }

            var lvl = Level(e);
            lvl.match = MatchMode.Derby;
            lvl.targetLaps = 0;
            lvl.countdownSeconds = 3;
            lvl.botOpponents = 5;
            Save(lvl);
        }

        // ---- the aircraft template -------------------------------------------

        /// <summary>
        /// The minimum an RC plane needs: ground to land on, a runway to line up
        /// with, a spawn, and a box that says how far the field goes.
        ///
        /// Built through the same <c>FlightTestEnvironment</c> /
        /// <c>DebugPlaneRig</c> pair the scripted flight tests fly and
        /// <c>RcPlaneSceneBuilder</c> authors, handed a real
        /// <see cref="SceneBuildContext"/> so their three Play-mode habits —
        /// <c>Destroy</c>, instanced materials, unassetted physics materials — are
        /// legal here. One builder, two worlds: an authored scene cannot drift
        /// from what the tests measure.
        /// </summary>
        private static void BuildRcPlane(Entry e)
        {
            var ctx = new SceneBuildContext
            {
                destroy = Object.DestroyImmediate,
                paint = PaintShared,
                surface = FrictionlessAsset(),
                skybox = SkyAsset(),
            };

            var (cam, _) = FlightTestEnvironment.Build(
                FlightTestEnvironment.EnvSpec.Airfield(), ctx);

            var (pos, rot) = FlightTestEnvironment.LaunchPose(40f);
            var spawn = new GameObject("SpawnPoint");
            spawn.transform.SetPositionAndRotation(pos, rot);

            // The flying field. Advisory, and sized to the airfield the
            // environment just built rather than to a number picked here.
            var airGo = new GameObject("Airspace");
            var air = airGo.AddComponent<AirspaceBounds>();
            air.halfExtents = new Vector3(220f, 140f, 260f);
            airGo.transform.position = new Vector3(0f, 60f, 0f);

            Lighting(e);

            var bootGo = new GameObject("RcPlaneBootstrap");
            var boot = bootGo.AddComponent<RcPlaneBootstrap>();
            boot.aircraft = RcPlaneBootstrap.Aircraft.SportRc;
            bootGo.AddComponent<FlightHud>();

            // No FlightSceneDescriptor and no aircraft in the scene: this is a
            // TEMPLATE, and the bootstrap builds the plane, the runner and the
            // camera rig at Play exactly as it does in a fresh scene. The fully
            // authored version — every part of the aircraft a draggable object —
            // already exists as RcPlaneScene; duplicating it here would give you
            // two of the same thing and one of them stale.
            if (cam != null) Selection.activeGameObject = cam.gameObject;
            Selection.activeGameObject = bootGo;
        }

        // ---- shared skeleton ---------------------------------------------------

        private readonly struct World
        {
            public readonly Transform env;
            public readonly SceneTrackDescriptor track;
            public readonly Collider floor;
            public World(Transform env, SceneTrackDescriptor track, Collider floor)
            { this.env = env; this.track = track; this.floor = floor; }
        }

        /// <summary>
        /// Floor, sun, sky, kill plane, track descriptor and bootstrap — the parts
        /// every driving template has, in the order they read in the hierarchy.
        /// The floor's TOP is y = 0 in all of them, so every spawn, gate and
        /// pickup below can be placed at y = 0 and mean "on the ground".
        /// </summary>
        private static World Common(Entry e, float sx, float sz, int floorType,
                                    TrackPresets.TrackKind kind)
        {
            var env = new GameObject("Environment").transform;

            var floor = Slab("Floor", Vector3.zero, new Vector2(sx, sz), 0.4f,
                             FloorColour(floorType), env, collider: true);
            floor.AddComponent<SurfaceTag>().floorType = floorType;

            Sun(env);
            Sky();

            // The floor under the floor. Wider than the world it catches, because
            // a car that leaves at speed leaves sideways as well as down.
            var kill = new GameObject("KillPlane");
            kill.transform.SetParent(env, false);
            kill.transform.position = new Vector3(0f, -8f, 0f);
            var kcol = kill.AddComponent<BoxCollider>();
            kcol.size = new Vector3(sx * 4f, 2f, sz * 4f);
            kill.AddComponent<KillPlane>();

            var trackGo = new GameObject("TrackDescriptor");
            var track = trackGo.AddComponent<SceneTrackDescriptor>();
            track.displayName = e.Label;
            track.kind = kind;
            track.sceneOwnsSky = true;
            track.sceneFallbackFloor = floorType;

            var bootGo = new GameObject("DrivingScene");
            var boot = bootGo.AddComponent<TrackBootstrap>();
            // The scene's own geometry IS the map: no procedural oval on top of it.
            boot.buildDefaultOval = false;

            var d = bootGo.AddComponent<DrivingSceneDescriptor>();
            d.level = DrivingSceneSetup.LoadOrCreate<LevelSettings>(
                LevelDir, $"LevelSettings_{e.Id}");
            d.physics = DrivingSceneSetup.LoadOrCreate<PhysicsSettings>("PhysicsSettings_Default");
            d.assists = DrivingSceneSetup.LoadOrCreate<Vehicles.AssistTuningOverride>("AssistTuning_Default");
            d.modes = DrivingSceneSetup.LoadOrCreate<ModeConfigOverride>("ModeTuning_Default");
            d.arcade = DrivingSceneSetup.LoadOrCreate<ArcadeConfigOverride>("ArcadeTuning_Default");

            Lighting(e);
            Selection.activeGameObject = bootGo;
            return new World(env, track, floor.GetComponent<Collider>());
        }

        /// <summary>A walled arena: the shared skeleton plus a wall ring and the
        /// playfield claim, which is what lets a scene track be an Arena at all.</summary>
        private static World Arena(Entry e, float sx, float sz)
        {
            var w = Common(e, sx, sz, Asphalt, TrackPresets.TrackKind.Arena);
            Walls(w.env, sx, sz, 0.6f, new Color(0.30f, 0.34f, 0.42f));
            // ArenaNav takes the arena's centre, its radius and its containment
            // test from these bounds. Without it the mode falls back to averaging
            // the spawn ring, which is right only while the pitch is centred on
            // its grid — see SceneTrackDescriptor.playfield.
            w.track.playfield = w.floor;
            return w;
        }

        // ---- pieces -----------------------------------------------------------

        private static void Sun(Transform parent)
        {
            var go = new GameObject("Sun");
            go.transform.SetParent(parent, false);
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.05f;
            light.color = new Color(1f, 0.97f, 0.91f);
            light.shadows = LightShadows.Soft;
        }

        private static void Sky()
        {
            var sky = SkyAsset();
            if (sky == null) return;
            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            RenderSettings.fog = false;
            DynamicGI.UpdateEnvironment();
        }

        /// <summary>Its own lighting settings asset per scene, so changing how one
        /// template bakes cannot reach the other eight.</summary>
        private static void Lighting(Entry e)
        {
            string path = $"{SceneDir}/Lighting_{e.Id}.lighting";
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (ls == null)
            {
                ls = new LightingSettings { name = $"Lighting_{e.Id}" };
                AssetDatabase.CreateAsset(ls, path);
            }
            Lightmapping.lightingSettings = ls;
        }

        private static void Walls(Transform parent, float sx, float sz, float h, Color c)
        {
            const float t = 0.4f;
            Slab("Wall_N", new Vector3(0f, h, (sz + t) * 0.5f), new Vector2(sx + t * 2f, t), h, c, parent);
            Slab("Wall_S", new Vector3(0f, h, -(sz + t) * 0.5f), new Vector2(sx + t * 2f, t), h, c, parent);
            Slab("Wall_E", new Vector3((sx + t) * 0.5f, h, 0f), new Vector2(t, sz + t * 2f), h, c, parent);
            Slab("Wall_W", new Vector3(-(sx + t) * 0.5f, h, 0f), new Vector2(t, sz + t * 2f), h, c, parent);
        }

        /// <summary>A flattened cube whose TOP face is at
        /// <paramref name="top"/>.y — the one convention every placement in this
        /// file relies on.</summary>
        private static GameObject Slab(string name, Vector3 top, Vector2 size, float thickness,
                                       Color c, Transform parent, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.position = top - Vector3.up * (thickness * 0.5f);
            go.transform.localScale = new Vector3(size.x, thickness, size.y);
            go.GetComponent<Renderer>().sharedMaterial = Mat(c);
            return go;
        }

        private static void Spawn(SceneTrackDescriptor d, int gridOrder, int team,
                                  Vector3 pos, float yawDeg)
        {
            var go = new GameObject(TrackSpawnMarker.NameFor(gridOrder));
            go.transform.SetParent(d.transform, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yawDeg, 0f));
            var m = go.AddComponent<TrackSpawnMarker>();
            m.gridOrder = gridOrder;
            m.team = team;
        }

        /// <summary>A gate on the oval at angle <paramref name="t"/>, turned to
        /// face along the curve's tangent there.</summary>
        private static void Gate<T>(SceneTrackDescriptor d, float t, float rx, float rz, int order)
            where T : TrackMarker
        {
            var p = new Vector3(Mathf.Cos(t) * rx, RoadY, Mathf.Sin(t) * rz);
            var tangent = new Vector3(-Mathf.Sin(t) * rx, 0f, Mathf.Cos(t) * rz);
            Marker<T>(d, p, Quaternion.LookRotation(tangent.normalized, Vector3.up).eulerAngles.y,
                      order);
        }

        private static void Marker<T>(SceneTrackDescriptor d, Vector3 pos, float yawDeg, int order)
            where T : TrackMarker
        {
            var go = new GameObject("Gate");
            go.transform.SetParent(d.transform, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yawDeg, 0f));
            var m = go.AddComponent<T>();
            if (m is TrackCheckpointMarker cp)
            {
                cp.order = order;
                go.name = TrackCheckpointMarker.NameFor(order);
            }
            else go.name = "Finish";
        }

        /// <summary>Yaw of the direction from <paramref name="a"/> to
        /// <paramref name="b"/>, flattened — a gate's +Z is where cars go.</summary>
        private static float Heading(Vector3 a, Vector3 b)
        {
            var d = b - a;
            d.y = 0f;
            return d.sqrMagnitude < 1e-6f
                ? 0f
                : Quaternion.LookRotation(d.normalized, Vector3.up).eulerAngles.y;
        }

        private static void SplineRoad(string name, List<Vector3> pts, bool closed,
                                       float width, int surface)
        {
            var go = new GameObject(name);
            var container = go.AddComponent<SplineContainer>();
            var spline = container.Spline;
            spline.Clear();
            foreach (var p in pts)
                spline.Add(new BezierKnot(new float3(p.x, p.y, p.z)), TangentMode.AutoSmooth);
            spline.Closed = closed;

            var auth = go.AddComponent<TrackSplineAuthoring>();
            auth.defaultWidth = width;
            auth.defaultSurface = surface;
            auth.edgeStripes = true;
            auth.contributesCorridor = true;
        }

        private static void ItemBox(Vector3 pos, Transform parent)
        {
            var go = new GameObject("ItemBox");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;

            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = Vector3.one * 0.32f;

            // The visual is a child so ArcadeItemBox can spin and hide it without
            // moving the trigger it lives in.
            var viz = Slab("viz", pos + Vector3.up * 0.11f, new Vector2(0.22f, 0.22f), 0.22f,
                           new Color(0.95f, 0.82f, 0.20f), go.transform, collider: false);
            viz.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);

            go.AddComponent<ArcadeItemBox>().viz = viz.transform;
        }

        private static void Pickup(Transform parent, Vector3 pos, ArenaPickup.Kind kind)
        {
            var go = new GameObject(kind == ArenaPickup.Kind.Repair ? "Repair" : "MineCrate");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.AddComponent<ArenaPickupMarker>().kind = kind;
        }

        private static void Goal(int team, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject($"Goal_{team}");
            go.transform.SetPositionAndRotation(pos, rot);
            go.AddComponent<ArenaGoalMarker>().team = team;
        }

        private static LevelSettings Level(Entry e) =>
            DrivingSceneSetup.LoadOrCreate<LevelSettings>(LevelDir, $"LevelSettings_{e.Id}");

        private static void Save(LevelSettings lvl)
        {
            EditorUtility.SetDirty(lvl);
            AssetDatabase.SaveAssets();
        }

        // ---- shared assets -----------------------------------------------------

        private static readonly Dictionary<Color, Material> _mats = new Dictionary<Color, Material>();

        /// <summary>One Standard-shader material ASSET per distinct colour,
        /// assigned as <c>sharedMaterial</c>. The runtime path instances a material
        /// per object — visually identical, but an instance cannot be saved and an
        /// asset can. Same trick, same reason, as RcPlaneSceneBuilder's paint.</summary>
        private static Material Mat(Color c)
        {
            if (_mats.TryGetValue(c, out var cached) && cached != null) return cached;

            string hex = ColorUtility.ToHtmlStringRGB(c);
            string path = $"{MatDir}/Mat_{hex}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(MatDir);
                mat = new Material(Shader.Find("Standard")) { color = c };
                AssetDatabase.CreateAsset(mat, path);
            }
            _mats[c] = mat;
            return mat;
        }

        private static void PaintShared(Renderer r, Color c) => r.sharedMaterial = Mat(c);

        /// <summary>A readable colour for a floor type. Cosmetic only — the
        /// SurfaceTag is what the tyre model reads.</summary>
        private static Color FloorColour(int floorType) => floorType switch
        {
            Grass => new Color(0.32f, 0.46f, 0.24f),
            Ice => new Color(0.62f, 0.78f, 0.88f),
            _ => new Color(0.26f, 0.26f, 0.28f),   // asphalt
        };

        private static Material SkyAsset()
        {
            string path = $"{MatDir}/Sky_Procedural.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null) return null;
            Directory.CreateDirectory(MatDir);
            var mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// <summary>The runtime physics material's values, saved — built by calling
        /// the same factory the runtime path calls, so the two cannot disagree.</summary>
        private static PhysicsMaterial FrictionlessAsset()
        {
            string path = $"{MatDir}/Frictionless.physicsMaterial";
            var existing = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (existing != null) return existing;

            Directory.CreateDirectory(MatDir);
            var mat = Core.PhysicsTests.PhysicsTestEnvironment.Frictionless();
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
