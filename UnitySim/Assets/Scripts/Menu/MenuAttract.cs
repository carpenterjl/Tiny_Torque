using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Menu
{
    /// <summary>
    /// Live "attract mode" behind the main menu: a random closed-loop circuit with
    /// a handful of AI cars driving laps, viewed by a slowly orbiting camera. It
    /// reuses the exact pieces the game already ships — <see cref="TrackFactory"/>
    /// for the map, <see cref="BotDriver"/>/<see cref="BotPath"/> for the driving,
    /// and a <see cref="SimulationRunner"/> per car wired like a race bot (no DLL,
    /// no logging, no per-car camera). The track is built NON-interactive, so no
    /// LapTimer/checkpoint HUD is created to bleed over the menu panel.
    ///
    /// <see cref="Build"/> returns false on any failure so the caller can fall back
    /// to the static showcar — a broken preset can never leave a black menu.
    /// </summary>
    public sealed class MenuAttract : MonoBehaviour
    {
        // Curated closed-loop spline circuits (clean, closed bot paths). Every
        // name here must exist in TrackPresets.All — a retired name resolves
        // null and silently costs the attract loop for that boot.
        private static readonly string[] Circuits =
            { "Boost Speedway", "Neon Vortex", "Downtown Dash",
              "Playroom Raceway", "Enchanted Ascent", "Graveyard Shift" };

        private const int CarCount = 4;
        private const int PhysicsRateHz = 400;
        private const int ControlRateHz = 100;
        private const float OrbitDegPerSec = 8f;

        /// <summary>The menu's own backdrop, kept for circuits with no themed
        /// ambience of their own.</summary>
        private static readonly Color MenuBackdrop = new Color(0.09f, 0.10f, 0.14f);

        private Camera _cam;
        private string _ambience = "";
        private Vector3 _center;
        private float _orbitRadius;
        private float _orbitHeight;
        private float _angle;

        /// <summary>Builds the attract scene. Returns false if anything went wrong.</summary>
        public bool Build()
        {
            try
            {
                var design = TrackPresets.Resolve(Circuits[Random.Range(0, Circuits.Length)]);
                if (design == null) return false;
                _ambience = design.ambience;   // the attract loop gets the map's sky too

                var track = TrackFactory.Build(design, interactive: false);
                if (track == null || track.root == null) return false;

                bool closed;
                var path = BotPath.Build(design, null, null, track.spawnPos,
                    track.spawnRot * Vector3.forward, out closed);
                if (path == null || path.Count < 2) return false;

                SpawnCars(path, closed);
                BuildCamera(path);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MenuAttract] Falling back to showcar: {e.Message}");
                return false;
            }
        }

        private void SpawnCars(List<Vector3> path, bool closed)
        {
            int n = Mathf.Min(CarCount, path.Count);
            for (int k = 0; k < n; k++)
            {
                // Spread the pack evenly around the loop so cars are visible from
                // every camera angle.
                int idx = (int)((long)k * path.Count / n);
                Vector3 p = path[idx];
                Vector3 tangent = path[(idx + 1) % path.Count] - p;
                tangent.y = 0f;
                Quaternion rot = tangent.sqrMagnitude > 1e-5f
                    ? Quaternion.LookRotation(tangent.normalized, Vector3.up)
                    : Quaternion.identity;
                Vector3 pos = p + Vector3.up * 0.08f;

                var design = VehiclePresets.All[k % VehiclePresets.All.Length].build();
                // Paint variety (same hue spread the race grid uses).
                design.bodyColor = Color.HSVToRGB((k * 0.137f) % 1f, 0.65f, 0.95f);

                var built = VehicleFactory.Build(design, pos, rot, previewKinematic: false);
                built.car.SetSpawn(pos, rot);

                var carInput = built.car.gameObject.AddComponent<CarInput>();
                carInput.car = built.car;
                carInput.lapTimer = null;   // no lap timing in the attract loop
                carInput.enableMouseSteer = false;
                carInput.source = new BotDriver(built.car, path, closed, BotDifficulty.Medium);

                // Step the car exactly like a race bot: Manual drive via CarInput,
                // no controller DLL, no telemetry, no mode box.
                var runner = new GameObject($"MenuBotRunner{k}").AddComponent<SimulationRunner>();
                runner.physicsRateHz = PhysicsRateHz;
                runner.controlRateHz = ControlRateHz;
                runner.vehicleBehaviour = built.car;
                runner.inputBehaviour = carInput;
                runner.sensorRig = built.rig;
                runner.graphProfile = SimulationRunner.GraphProfile.Car;
                runner.loadControllerDll = false;
                runner.allowModeToggle = false;
                runner.showModeBox = false;
                runner.logCsv = false;
                runner.loggable = false;
                runner.startInManual = true;
            }
        }

        private void BuildCamera(List<Vector3> path)
        {
            // Frame the whole circuit from its centroid + extent.
            Vector3 sum = Vector3.zero;
            foreach (var pt in path) sum += pt;
            _center = sum / path.Count;

            float maxR = 1f;
            foreach (var pt in path)
            {
                Vector3 d = pt - _center; d.y = 0f;
                maxR = Mathf.Max(maxR, d.magnitude);
            }
            _orbitRadius = maxR * 1.5f + 2f;
            _orbitHeight = maxR * 0.8f + 2f;

            _cam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                _cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            _cam.farClipPlane = Mathf.Max(500f, _orbitRadius * 4f);
            MapAmbience.ApplyCamera(_cam, _ambience, MenuBackdrop);
            PositionCamera();
        }

        private void PositionCamera()
        {
            float a = _angle * Mathf.Deg2Rad;
            Vector3 pos = _center
                + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * _orbitRadius
                + Vector3.up * _orbitHeight;
            _cam.transform.position = pos;
            _cam.transform.LookAt(_center);
        }

        private void Update()
        {
            if (_cam == null) return;
            _angle += OrbitDegPerSec * Time.deltaTime;
            PositionCamera();
        }
    }
}
