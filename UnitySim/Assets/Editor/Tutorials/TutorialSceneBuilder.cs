using System.Collections.Generic;
using System.IO;
using AIHWSim.Arcade;
using AIHWSim.Core;
using AIHWSim.Core.Boot;
using AIHWSim.Core.Config;
using AIHWSim.Modes;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using AIHWSim.Tutorial;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Starter scenes for the driving tutorials: a floor, a spawn, a director and
    /// a few worked steps, ready to be turned into a real lesson.
    ///
    /// <b>CREATE-IF-MISSING, always.</b> There is no "regenerate" item in this
    /// file, and that absence is the design. These scenes are meant to be
    /// hand-edited into custom maps — that is the whole reason the steps are
    /// scene objects rather than a table in code — so a menu item that rebuilt
    /// them would eventually eat somebody's afternoon. The mode templates take
    /// the opposite bargain (they are demonstrations of this file's output and
    /// say so); tutorials are content. Running this twice logs a line per scene
    /// and changes nothing.
    ///
    /// <b>Registered in Build Settings</b>, unlike the mode templates and the
    /// physics tests. Those are opened by hand in the editor; these are loaded by
    /// name at runtime from the menu, and an unregistered scene name is a black
    /// screen.
    ///
    /// <b>Edit-time hygiene</b> follows <c>ModeTemplateBuilder</c>: materials are
    /// find-or-create ASSETS keyed by colour and assigned as
    /// <c>sharedMaterial</c>, because <c>renderer.material</c> instantiates per
    /// object and every one of those saves into the scene as an orphan
    /// sub-asset. Colliders come off with <c>DestroyImmediate</c>, since a
    /// coroutine-free edit-time <c>Destroy</c> never runs.
    /// </summary>
    public static class TutorialSceneBuilder
    {
        public const string SceneDir = "Assets/Scenes/Tutorials";
        private const string MatDir = SceneDir + "/Materials";

        /// <summary>
        /// Per-scene settings live under <c>Settings/Driving/Scenes/&lt;Scene&gt;/</c>
        /// because that is where <see cref="SceneSettingsOwnership"/> says a
        /// scene's own rules belong. Writing them anywhere else is not a
        /// different choice, just a slower one: the ownership hook clones them
        /// into that folder on the first save and repoints the descriptor,
        /// leaving whatever was created elsewhere as an orphan nothing reads.
        /// </summary>
        private const string LevelRoot = "Assets/Settings/Driving/Scenes";

        private const string Tag = "[TUT]";

        /// <summary>Floor ids from <c>TrackCatalog.Floors</c>.</summary>
        private const int Asphalt = 1, Grass = 2;

        // ---- entry points ---------------------------------------------------

        [MenuItem("Tools/AIHWSim/Tutorials/Create Missing Tutorial Scenes", priority = 410)]
        public static void CreateMissing()
        {
            Directory.CreateDirectory(SceneDir);
            Directory.CreateDirectory(MatDir);
            Directory.CreateDirectory(LevelRoot);
            AssetDatabase.Refresh();

            int made = 0, kept = 0;
            foreach (var row in TutorialCatalog.All)
            {
                if (string.IsNullOrEmpty(row.scene)) continue;   // overlay lesson
                if (Create(row)) made++; else kept++;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"{Tag} created {made} scene(s), left {kept} existing one(s) alone — {SceneDir}");
        }

        /// <summary>Headless entry, for a fresh clone that has no scenes yet.</summary>
        public static void CreateMissingHeadless()
        {
            CreateMissing();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static string ScenePath(string scene) => $"{SceneDir}/{scene}.unity";

        /// <summary>Build one. Returns false when the file already exists, which
        /// is not an error — it is the normal case after the first run.</summary>
        private static bool Create(TutorialCatalog.Row row)
        {
            string path = ScenePath(row.scene);
            if (File.Exists(path))
            {
                Debug.Log($"{Tag} skipped existing: {path}");
                EnsureRegistered(path);
                return false;
            }

            _mats.Clear();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var world = Common(row, 60f, 60f, Asphalt);
            Steps(row, world);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, path);
            EnsureRegistered(path);
            Debug.Log($"{Tag} created {path}");
            return true;
        }

        /// <summary>Through <c>SceneBuilderMenu</c>'s helper, never by hand: it is
        /// the one that normalizes the path, and an un-normalized compare is what
        /// appends a second row with an all-zero GUID that fails a player build.</summary>
        private static void EnsureRegistered(string path) => SceneBuilderMenu.AddSceneToBuild(path);

        // ---- the scene ---------------------------------------------------------

        private readonly struct World
        {
            public readonly Transform Env;
            public readonly SceneTrackDescriptor Track;
            public World(Transform env, SceneTrackDescriptor track) { Env = env; Track = track; }
        }

        /// <summary>
        /// Floor, sun, sky, kill plane, descriptor and bootstrap — the skeleton
        /// every driving scene in this project has. The floor's TOP is y = 0, so
        /// everything below can be placed at y = 0 and mean "on the ground".
        /// </summary>
        private static World Common(TutorialCatalog.Row row, float sx, float sz, int floorType)
        {
            var env = new GameObject("Environment").transform;

            var floor = Slab("Floor", Vector3.zero, new Vector2(sx, sz), 0.4f,
                             FloorColour(floorType), env);
            floor.AddComponent<SurfaceTag>().floorType = floorType;

            Sun(env);
            Sky();

            var kill = new GameObject("KillPlane");
            kill.transform.SetParent(env, false);
            kill.transform.position = new Vector3(0f, -8f, 0f);
            kill.AddComponent<BoxCollider>().size = new Vector3(sx * 4f, 2f, sz * 4f);
            kill.AddComponent<KillPlane>();

            var trackGo = new GameObject("TrackDescriptor");
            var track = trackGo.AddComponent<SceneTrackDescriptor>();
            track.displayName = row.label;
            // FreeRoam on purpose. A tutorial map has no finish line and nothing
            // to win, and the kind is also what keeps it out of the race picker —
            // which is the whole reason tutorials have their own catalogue.
            track.kind = TrackPresets.TrackKind.FreeRoam;
            track.sceneOwnsSky = true;
            track.sceneFallbackFloor = floorType;

            // One spawn, facing down +Z, a little back from the middle so the
            // first objective can sit ahead of the car.
            var spawn = new GameObject(TrackSpawnMarker.NameFor(0));
            spawn.transform.SetParent(trackGo.transform, false);
            spawn.transform.SetPositionAndRotation(new Vector3(0f, 0f, -18f), Quaternion.identity);
            spawn.AddComponent<TrackSpawnMarker>().gridOrder = 0;

            var bootGo = new GameObject("DrivingScene");
            var boot = bootGo.AddComponent<TrackBootstrap>();
            boot.buildDefaultOval = false;   // the scene's own geometry IS the map

            var d = bootGo.AddComponent<DrivingSceneDescriptor>();
            d.level = DrivingSceneSetup.LoadOrCreate<LevelSettings>(
                $"{LevelRoot}/{row.scene}", $"LevelSettings_{row.scene}");
            d.physics = DrivingSceneSetup.LoadOrCreate<PhysicsSettings>("PhysicsSettings_Default");
            d.assists = DrivingSceneSetup.LoadOrCreate<Vehicles.AssistTuningOverride>("AssistTuning_Default");
            d.modes = DrivingSceneSetup.LoadOrCreate<ModeConfigOverride>("ModeTuning_Default");
            d.arcade = DrivingSceneSetup.LoadOrCreate<ArcadeConfigOverride>("ArcadeTuning_Default");

            Lighting(row);
            return new World(env, track);
        }

        /// <summary>
        /// The Tutorial root: the director and the written lesson from
        /// <see cref="TutorialSceneContent"/>, with a trigger volume placed for
        /// each step that wants one.
        ///
        /// A catalogue row with no lesson written yet gets a worked placeholder
        /// instead — one of each interesting condition — so a new tutorial still
        /// produces a scene that opens, runs and can be edited.
        /// </summary>
        private static void Steps(TutorialCatalog.Row row, World world)
        {
            var rootGo = new GameObject("Tutorial");
            var dir = rootGo.AddComponent<TutorialDirector>();
            dir.tutorialId = row.id;

            var specs = TutorialSceneContent.For(row.id);
            if (specs.Count == 0) specs = Placeholder(row);

            int volume = 0;
            foreach (var spec in specs)
            {
                TutorialTrigger trigger = null;
                if (spec.condition == TutorialCondition.TriggerVolume)
                {
                    volume++;
                    trigger = TriggerVolume(world.Env, $"Objective_{volume}",
                                            spec.triggerAt, spec.triggerSize);
                }
                AddStep(rootGo.transform, spec, trigger);
            }
        }

        /// <summary>What an unwritten tutorial gets: one step of each shape,
        /// saying plainly that it is a starting point.</summary>
        private static List<TutorialSceneContent.Spec> Placeholder(TutorialCatalog.Row row) =>
            new List<TutorialSceneContent.Spec>
            {
                new TutorialSceneContent.Spec
                {
                    title = "Not written yet",
                    body = $"PLACEHOLDER — write the real lesson here.\n\n{row.blurb}",
                    condition = TutorialCondition.Continue,
                    seconds = 1f,
                },
                new TutorialSceneContent.Spec
                {
                    title = "Get moving",
                    body = "Hold {throttle} to pull away. {steer} turns; {brake} slows you down.",
                    condition = TutorialCondition.InputHeld,
                    input = TutorialInput.Throttle,
                    amount = 0.4f, seconds = 1.2f, banner = "That's it",
                },
                new TutorialSceneContent.Spec
                {
                    title = "Drive to the marker",
                    body = "Head for the gate ahead of you.",
                    condition = TutorialCondition.TriggerVolume,
                    triggerAt = new Vector3(0f, 1.2f, 4f),
                    triggerSize = new Vector3(9f, 3f, 3f),
                    seconds = 0.4f, banner = "Reached it",
                },
                new TutorialSceneContent.Spec
                {
                    title = "Lesson over",
                    body = "Replace these steps with the real ones. Add a TutorialStep " +
                           "as a child of this object; sibling order is step order.",
                    condition = TutorialCondition.Timer,
                    seconds = 5f, banner = "Done",
                },
            };

        private static void AddStep(Transform parent, TutorialSceneContent.Spec spec,
                                    TutorialTrigger trigger)
        {
            int n = parent.childCount + 1;
            var go = new GameObject($"Step{n:00}_{Slug(spec.title)}");
            go.transform.SetParent(parent, false);
            var step = go.AddComponent<TutorialStep>();
            step.title = spec.title;
            step.body = spec.body;
            step.condition = spec.condition;
            step.trigger = trigger;
            step.input = spec.input;
            step.amount = spec.amount;
            step.seconds = spec.seconds;
            step.banner = spec.banner;
            step.token = spec.token ?? "";
        }

        /// <summary>A hierarchy-safe name from a step title.</summary>
        private static string Slug(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Step";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars);
        }

        /// <summary>A trigger box with a visible shell in the editor. The shell's
        /// renderer is switched off at Awake by the component itself, so the
        /// authoring aid never reaches the player's screen.</summary>
        private static TutorialTrigger TriggerVolume(Transform parent, string name,
                                                     Vector3 pos, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.30f, 0.80f, 1f));
            go.GetComponent<BoxCollider>().isTrigger = true;
            return go.AddComponent<TutorialTrigger>();
        }

        // ---- pieces -------------------------------------------------------------

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

        private static void Lighting(TutorialCatalog.Row row)
        {
            string path = $"{SceneDir}/Lighting_{row.scene}.lighting";
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (ls == null)
            {
                ls = new LightingSettings { name = $"Lighting_{row.scene}" };
                AssetDatabase.CreateAsset(ls, path);
            }
            Lightmapping.lightingSettings = ls;
        }

        /// <summary>A flattened cube whose TOP face is at <paramref name="top"/>.y.</summary>
        private static GameObject Slab(string name, Vector3 top, Vector2 size, float thickness,
                                       Color c, Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = top - Vector3.up * (thickness * 0.5f);
            go.transform.localScale = new Vector3(size.x, thickness, size.y);
            go.GetComponent<Renderer>().sharedMaterial = Mat(c);
            return go;
        }

        // ---- shared assets --------------------------------------------------------

        private static readonly Dictionary<Color, Material> _mats = new Dictionary<Color, Material>();

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

        private static Color FloorColour(int floorType) => floorType switch
        {
            Grass => new Color(0.32f, 0.46f, 0.24f),
            _ => new Color(0.26f, 0.26f, 0.28f),
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
    }
}
