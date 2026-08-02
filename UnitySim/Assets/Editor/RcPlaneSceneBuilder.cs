using System.Collections.Generic;
using System.IO;
using AIHWSim.Core.Flight;
using AIHWSim.Garage;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles.Aero;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Builds the free-flight scene at EDIT time — every GameObject in the
    /// hierarchy, adjustable in the inspector before Play — using the very same
    /// builders the runtime path uses (<see cref="FlightTestEnvironment"/>,
    /// <see cref="DebugPlaneRig"/>), handed a <see cref="SceneBuildContext"/>
    /// that makes their four Play-mode habits legal here: DestroyImmediate for
    /// collider stripping, asset-backed shared materials instead of per-object
    /// instances, and saved PhysicsMaterial/skybox assets instead of transients.
    ///
    /// One builder, two worlds: because the scenery and the aircraft come from
    /// the shared code, an authored scene cannot drift from what the scripted
    /// tests fly — the only differences are which delegate strips a collider and
    /// where a material lives. The runner's fields go through
    /// <see cref="DebugPlaneRig.ConfigureRunner"/> for the same reason.
    ///
    /// Generated assets live beside the scene in
    /// <c>Assets/Scenes/RcPlaneScene/</c>, find-or-create, keyed by colour — so
    /// re-running the builder reuses them and the scene file never accumulates
    /// dangling embedded materials.
    /// </summary>
    public static class RcPlaneSceneBuilder
    {
        private const string AssetDir = "Assets/Scenes/RcPlaneScene";

        private static readonly Dictionary<Color, Material> _mats = new();

        public static void CreateAuthoredScene(string scenePath) =>
            CreateAuthoredScene(scenePath, RcPlaneBootstrap.Aircraft.SportRc);

        /// <summary>Headless entry: author both scenes (trainer and jet), no
        /// dialogs, exit. What the batch verification calls so the scenes in
        /// the repo are the builder's output rather than a hand's.</summary>
        public static void CreateAllHeadless()
        {
            CreateAuthoredScene("Assets/Scenes/RcPlaneScene.unity",
                                RcPlaneBootstrap.Aircraft.SportRc);
            CreateAuthoredScene("Assets/Scenes/RcJetScene.unity",
                                RcPlaneBootstrap.Aircraft.HydraVtol);
            Debug.Log("[SCENE] authored RcPlaneScene + RcJetScene");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void CreateAuthoredScene(string scenePath,
                                               RcPlaneBootstrap.Aircraft aircraft)
        {
            Directory.CreateDirectory(AssetDir);
            _mats.Clear();

            PhysicsMaterial frictionless = FrictionlessAsset();
            Material sky = SkyAsset();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                                                    NewSceneMode.Single);

            var ctx = new SceneBuildContext
            {
                destroy = Object.DestroyImmediate,
                paint = PaintShared,
                surface = frictionless,
                skybox = sky,
            };

            // Scenery first, aircraft second, runner third — the same
            // load-bearing order as the runtime path, minus the physics sync
            // (colliders resolve when the scene is saved and loaded).
            var (cam, graph) = FlightTestEnvironment.Build(
                FlightTestEnvironment.EnvSpec.Airfield(), ctx);

            bool jet = aircraft == RcPlaneBootstrap.Aircraft.HydraVtol;
            AircraftSpec spec = jet ? DebugJets.HydraVtol() : DebugPlanes.SportRc();
            var (pos, rot) = FlightTestEnvironment.LaunchPose(40f);
            DebugPlaneRig.Rig rig = DebugPlaneRig.BuildPlane(spec, pos, rot, ctx);

            var cameras = cam.gameObject.AddComponent<FlightCameraRig>();
            cameras.target = rig.plane.transform;
            // The code-set value becomes an AUTHORED value: from here on the
            // inspector owns it and nothing re-stamps it at Play.
            cameras.pilotPosition = new Vector3(-14f, 1.6f, -70f);

            var runnerGo = new GameObject("SimulationRunner");
            var runner = runnerGo.AddComponent<AIHWSim.Core.SimulationRunner>();
            DebugPlaneRig.ConfigureRunner(runner, rig.plane, rig.input, graph,
                                          physicsRateHz: 400, controlRateHz: 100,
                                          logCsv: true);
            runner.logLabel = "RcPlane";
            // Authored but NOT bound — channel registration is a runtime act;
            // the bootstrap's adopt path calls Bind at Awake.
            var telemetry = runnerGo.AddComponent<FlightTelemetry>();

            var sas = rig.root.AddComponent<SasController>();
            sas.plane = rig.plane;
            sas.target = FarPylon();
            rig.input.sas = sas;
            if (jet)
            {
                // TUNED hover starting points (rate terms only) — the same
                // values RcPlaneBootstrap's code path uses, BAKED here so the
                // inspector owns them from now on.
                sas.rollRateGain = 0.008f;
                sas.pitchRateGain = 0.020f;
            }

            if (jet)
            {
                // The target range, authored: every drone circuit, waypoint
                // handle and barrel becomes a draggable scene object.
                var targetsGo = new GameObject("Targets");
                var spawner = targetsGo.AddComponent<AIHWSim.Combat.TargetSpawner>();
                spawner.BuildAll(ctx);
            }

            // A draggable spawn marker at the hand-launch spot. Height is owned
            // by the bootstrap's launchAltitude; this is where over the field.
            var spawnGo = new GameObject("SpawnPoint");
            spawnGo.transform.SetPositionAndRotation(pos, rot);

            var bootGo = new GameObject("RcPlaneBootstrap");
            var boot = bootGo.AddComponent<RcPlaneBootstrap>();
            boot.aircraft = aircraft;
            bootGo.AddComponent<FlightHud>();
            var d = bootGo.AddComponent<FlightSceneDescriptor>();
            d.plane = rig.plane;
            d.input = rig.input;
            d.runner = runner;
            d.telemetry = telemetry;
            d.cameraRig = cameras;
            d.sas = sas;
            d.sasTarget = sas.target;
            d.spawnPoint = spawnGo.transform;

            Selection.activeGameObject = bootGo;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>The far pylon, chosen exactly the way the runtime fallback
        /// chooses it — but resolved ONCE, here, into a serialized reference.</summary>
        private static Transform FarPylon()
        {
            var pylons = GameObject.Find("Pylons");
            if (pylons == null) return null;

            Transform best = null;
            foreach (Transform t in pylons.transform)
                if (best == null || t.position.z > best.position.z) best = t;
            return best;
        }

        // ---- assets ------------------------------------------------------

        /// <summary>The runtime material's values, saved. Built by calling the
        /// same factory the runtime path calls, so the two cannot disagree.</summary>
        private static PhysicsMaterial FrictionlessAsset()
        {
            string path = AssetDir + "/Frictionless.physicsMaterial";
            var existing = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (existing != null) return existing;

            PhysicsMaterial mat = Core.PhysicsTests.PhysicsTestEnvironment.Frictionless();
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static Material SkyAsset()
        {
            string path = AssetDir + "/Sky_Procedural.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader sky = Shader.Find("Skybox/Procedural");
            if (sky == null) return null;   // ctx.skybox null → builder's fallback
            var mat = new Material(sky);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// <summary>The editor's paint: one Standard-shader material asset per
        /// distinct colour, assigned as <c>sharedMaterial</c>. The runtime path
        /// instances a material per object; visually identical (same shader,
        /// same colour), but an instance cannot be saved and an asset can.</summary>
        private static void PaintShared(Renderer r, Color c)
        {
            if (!_mats.TryGetValue(c, out Material mat) || mat == null)
            {
                string hex = ColorUtility.ToHtmlStringRGB(c);
                string path = AssetDir + "/Mat_" + hex + ".mat";
                mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Standard")) { color = c };
                    AssetDatabase.CreateAsset(mat, path);
                }
                _mats[c] = mat;
            }
            r.sharedMaterial = mat;
        }
    }
}
