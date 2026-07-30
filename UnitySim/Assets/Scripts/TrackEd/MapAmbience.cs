using System;
using AIHWSim.Track;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>A local coloured light that is meant to show in the fog — a
    /// crater, a lit hall, a crypt mouth. Positions are map coordinates.</summary>
    public sealed class GlowSpec
    {
        public Vector3 pos;
        public float range = 20f;
        public float intensity = 4f;
        public Color color = Color.white;
    }

    /// <summary>
    /// Sky, fog, key light and glow points for one map. This is where most of a
    /// TinyTorque map's look actually lives: the Blender modules get the
    /// enchanted vale from a dim moon-blue sun with every warm note coming off
    /// a lit window, and the haunted hollow from haze at twice everyone else's
    /// density — not from the props, which are the same grey meshes either way.
    /// </summary>
    public sealed class AmbienceDef
    {
        public string key = "";
        public string label = "Daylight";

        // Sky dome, read up the view vector. skyTop.a == 0 means "no dome" —
        // the camera's own background colour is left alone.
        public Color skyTop, skyHorizon, skyGround;
        public Color wedge = new Color(0f, 0f, 0f, 0f);  // alpha = strength
        public Vector3 wedgeDir = Vector3.forward;        // compass side it sits on

        public Color ambient = Color.clear;   // clear = don't touch the scene's
        public Color fog = Color.grey;
        public float fogDensity;              // 0 = fog off

        public Color sunColor = Color.white;
        public float sunIntensity = 1.1f;
        public Vector3 sunEuler = new Vector3(50f, -30f, 0f);

        public Color groundColor = new Color(0.38f, 0.27f, 0.16f);
        public GlowSpec[] glows = Array.Empty<GlowSpec>();

        /// <summary>Built geometry the theme needs (the toy room's walls).</summary>
        public Action<Transform> extras;

        public bool HasDome => skyTop.a > 0f;
    }

    /// <summary>
    /// Per-map atmosphere, applied by <see cref="TrackFactory.Build"/> so the
    /// builder preview and the drive scene get exactly the same one — the same
    /// "what you build is what you drive" contract the factory already keeps
    /// for geometry.
    ///
    /// Values are ported from the Blender map modules (tt_11_map,
    /// tt_16_toy_map, tt_17_ench_map, tt_18_haunt_map + the shared
    /// tt_15_mapkit) with two deliberate adjustments: fog density is the
    /// Blender haze density x10 because 1 game metre is 10 authored metres, and
    /// sky/ambient colours are lifted well above the authored linear values
    /// because those are photographed through AgX with a compositor bloom and
    /// Unity has neither.
    /// </summary>
    public static class MapAmbience
    {
        public const string Downtown = "downtown";
        public const string ToyRoom = "toyroom";
        public const string Enchanted = "enchanted";
        public const string Haunted = "haunted";
        public const string CityNoon = "citynoon";

        // ---- defs ----------------------------------------------------------

        /// <summary>Every map authored before the TinyTorque ports. Carries no
        /// dome and no ambient override, so it restores the scene exactly as
        /// its bootstrap left it.</summary>
        private static readonly AmbienceDef Neutral = new AmbienceDef
        {
            key = "", label = "Daylight",
        };

        public static readonly AmbienceDef[] All =
        {
            Neutral,

            // Dusk over the arcade city: a warm band low in the south behind
            // the volcano, deep blue overhead. tt_11_map.world/lights.
            new AmbienceDef
            {
                key = Downtown, label = "City dusk",
                skyTop = new Color(0.28f, 0.16f, 0.13f, 1f),
                skyHorizon = new Color(0.72f, 0.40f, 0.24f, 1f),
                skyGround = new Color(0.20f, 0.15f, 0.14f, 1f),
                wedge = new Color(0.95f, 0.45f, 0.18f, 0.85f),
                wedgeDir = new Vector3(0f, 0f, -1f),      // the badlands are south
                ambient = new Color(0.30f, 0.26f, 0.28f),
                fog = new Color(0.58f, 0.50f, 0.52f), fogDensity = 0.0030f,
                sunColor = new Color(1.00f, 0.80f, 0.60f), sunIntensity = 1.15f,
                sunEuler = new Vector3(26.6f, 115.0f, 0f),
                groundColor = new Color(0.30f, 0.22f, 0.17f),
                glows = new[]
                {
                    // The crater, standing in for the light the lava lake throws.
                    new GlowSpec { pos = new Vector3(10f, 4.4f, -30.5f), range = 26f,
                                   intensity = 7f, color = new Color(1.00f, 0.36f, 0.10f) },
                },
            },

            // An attic, not a sky: almost every lumen in frame is the standard
            // lamp or the dormer window. tt_16_toy_map.lights.
            new AmbienceDef
            {
                key = ToyRoom, label = "Attic",
                skyTop = new Color(0.050f, 0.042f, 0.038f, 1f),
                skyHorizon = new Color(0.130f, 0.095f, 0.062f, 1f),
                skyGround = new Color(0.035f, 0.030f, 0.026f, 1f),
                wedge = new Color(0.34f, 0.22f, 0.12f, 0.8f),
                wedgeDir = new Vector3(0f, 0f, -1f),
                ambient = new Color(0.26f, 0.21f, 0.17f),
                fog = new Color(0.42f, 0.36f, 0.30f), fogDensity = 0.0022f,
                sunColor = new Color(1.00f, 0.86f, 0.66f), sunIntensity = 1.35f,
                sunEuler = new Vector3(18.2f, -46.9f, 0f),
                groundColor = new Color(0.16f, 0.10f, 0.055f),
                glows = new[]
                {
                    // The standard lamp really does light its corner of the room.
                    new GlowSpec { pos = new Vector3(6f, 3.4f, -27f), range = 34f,
                                   intensity = 4.2f, color = new Color(1.00f, 0.84f, 0.60f) },
                },
                extras = BuildAtticRoom,
            },

            // Deep twilight with an aurora wedge low in the north over the
            // castle. tt_17_ench_map.build/lights.
            new AmbienceDef
            {
                key = Enchanted, label = "Moonlit vale",
                skyTop = new Color(0.035f, 0.045f, 0.100f, 1f),
                skyHorizon = new Color(0.130f, 0.140f, 0.220f, 1f),
                skyGround = new Color(0.050f, 0.050f, 0.080f, 1f),
                wedge = new Color(0.22f, 0.50f, 0.52f, 0.75f),
                wedgeDir = new Vector3(0f, 0f, 1f),       // the castle closes the north
                ambient = new Color(0.20f, 0.24f, 0.32f),
                fog = new Color(0.30f, 0.42f, 0.50f), fogDensity = 0.0042f,
                sunColor = new Color(0.62f, 0.76f, 1.00f), sunIntensity = 0.95f,
                sunEuler = new Vector3(29.4f, 50.2f, 0f),
                groundColor = new Color(0.055f, 0.100f, 0.055f),
                glows = new[]
                {
                    // The keep, so the castle is more than a row of lit windows.
                    new GlowSpec { pos = new Vector3(0f, 7.8f, 35f), range = 40f,
                                   intensity = 3.4f, color = new Color(1.00f, 0.78f, 0.44f) },
                    // The village green, round the fountain.
                    new GlowSpec { pos = new Vector3(0f, 1.2f, 4f), range = 26f,
                                   intensity = 2.6f, color = new Color(1.00f, 0.72f, 0.40f) },
                },
            },

            // Near-black, and the fog is the subject rather than depth cueing:
            // twice the density of any other map. tt_18_haunt_map.build/lights.
            new AmbienceDef
            {
                key = Haunted, label = "Hollow",
                skyTop = new Color(0.020f, 0.030f, 0.045f, 1f),
                skyHorizon = new Color(0.075f, 0.110f, 0.110f, 1f),
                skyGround = new Color(0.030f, 0.042f, 0.042f, 1f),
                wedge = new Color(0.10f, 0.17f, 0.17f, 0.7f),
                wedgeDir = new Vector3(0f, 0f, 1f),
                ambient = new Color(0.14f, 0.19f, 0.20f),
                fog = new Color(0.26f, 0.36f, 0.40f), fogDensity = 0.0068f,
                sunColor = new Color(0.58f, 0.74f, 1.00f), sunIntensity = 0.85f,
                sunEuler = new Vector3(27.0f, 139.2f, 0f),
                groundColor = new Color(0.055f, 0.060f, 0.045f),
                glows = new[]
                {
                    // The hall, the crypt mouth and the chapel are supposed to
                    // be the brightest things in frame, and only are because
                    // the fill above stays low.
                    new GlowSpec { pos = new Vector3(0f, 3.2f, 27f), range = 30f,
                                   intensity = 3.2f, color = new Color(0.44f, 1.00f, 0.56f) },
                    new GlowSpec { pos = new Vector3(30f, 0.6f, -32f), range = 20f,
                                   intensity = 2.6f, color = new Color(0.40f, 1.00f, 0.50f) },
                    new GlowSpec { pos = new Vector3(-28f, 1.0f, 8f), range = 20f,
                                   intensity = 2.2f, color = new Color(0.40f, 0.90f, 0.52f) },
                },
            },

            // Midday over Torque Falls, and the brief inverts: the other four
            // maps keep the sky dark and let lamps do the work, here the SKY is
            // the fill. tt_25_city_map.sky/lights — one hard sun at 3.9, a cool
            // sky fill and a warm bounce, all three of them suns because a
            // town has no walls for an area light to stop against.
            //
            // No fog at all, which is the deliberate part. The source dropped
            // its haze box for the same reason: a finite volume has a lid, and
            // a camera looking three kilometres to a horizon crosses it at a
            // fixed height in frame, so the "depth" it buys arrives as a hard
            // seam across the sky. The ground fading to the sky colour is the
            // whole of the aerial perspective a clear noon needs.
            new AmbienceDef
            {
                key = CityNoon, label = "Midday",
                skyTop = new Color(0.105f, 0.250f, 0.620f, 1f),
                skyHorizon = new Color(0.640f, 0.720f, 0.850f, 1f),
                skyGround = new Color(0.365f, 0.415f, 0.500f, 1f),
                // No wedge: nothing in a noon sky sits on one compass side.
                ambient = new Color(0.52f, 0.56f, 0.62f),
                fog = new Color(0.66f, 0.72f, 0.82f), fogDensity = 0f,
                sunColor = new Color(1.00f, 0.955f, 0.885f), sunIntensity = 1.45f,
                // CM_Sun stands at (300, -420, 380) aiming (-20, 20, 8): a
                // 39-degree elevation from the south-east, which in Unity's
                // Y-up frame is pitch 39 / yaw 145.
                sunEuler = new Vector3(39.0f, 145.0f, 0f),
                groundColor = new Color(0.29f, 0.38f, 0.20f),
            },
        };

        public static AmbienceDef Resolve(string key)
        {
            if (!string.IsNullOrEmpty(key))
                foreach (var a in All)
                    if (a.key == key) return a;
            return Neutral;
        }

        // ---- apply ---------------------------------------------------------

        // The neutral map has to put the scene back exactly as its bootstrap
        // left it, and the builder and the drive scene do not agree on what
        // that is. So rather than hard-coding either, snapshot whatever the
        // scene had the first time a map is built in it. Re-snapshotted per
        // scene, because the static outlives the scene load.
        private static bool _captured;
        private static int _capturedScene;
        private static Color _wasAmbient;
        private static UnityEngine.Rendering.AmbientMode _wasAmbientMode;
        private static bool _wasFog;
        private static Color _wasFogColor;
        private static float _wasFogDensity;
        private static FogMode _wasFogMode;

        private static void CaptureNeutral()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (_captured && _capturedScene == scene) return;
            _captured = true;
            _capturedScene = scene;
            _wasAmbient = RenderSettings.ambientLight;
            _wasAmbientMode = RenderSettings.ambientMode;
            _wasFog = RenderSettings.fog;
            _wasFogColor = RenderSettings.fogColor;
            _wasFogDensity = RenderSettings.fogDensity;
            _wasFogMode = RenderSettings.fogMode;
        }

        /// <summary>
        /// Apply <paramref name="a"/> to the scene, building the dome, the glow
        /// points and any themed geometry under <paramref name="root"/> so they
        /// die with the map. Global state (fog, ambient, the key light) is
        /// restored to the scene's own values by the neutral def, so loading a
        /// plain map after a themed one does not leave the fog behind.
        /// </summary>
        public static void Apply(AmbienceDef a, Transform root)
        {
            CaptureNeutral();
            a ??= Neutral;

            if (a.fogDensity > 0f)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogColor = a.fog;
                RenderSettings.fogDensity = a.fogDensity;
            }
            else
            {
                RenderSettings.fog = _wasFog;
                RenderSettings.fogMode = _wasFogMode;
                RenderSettings.fogColor = _wasFogColor;
                RenderSettings.fogDensity = _wasFogDensity;
            }

            if (a.ambient.a > 0f)
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = a.ambient;
            }
            else
            {
                RenderSettings.ambientMode = _wasAmbientMode;
                RenderSettings.ambientLight = _wasAmbient;
            }

            ApplyKeyLight(a);
            if (a.HasDome) BuildDome(a, root);
            BuildGlows(a, root);
            a.extras?.Invoke(root);
        }

        /// <summary>
        /// Retune the scene's existing directional light rather than adding
        /// one: both bootstraps create exactly one before the map is built, and
        /// a second sun would double every shadow.
        /// </summary>
        private static void ApplyKeyLight(AmbienceDef a)
        {
            Light key = null;
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { key = l; break; }
            if (key == null) return;
            key.color = a.sunColor;
            key.intensity = a.sunIntensity;
            key.transform.rotation = Quaternion.Euler(a.sunEuler);
        }

        /// <summary>
        /// Point a camera at a map's atmosphere. The background only shows
        /// where the dome does not reach (and everywhere, if the shader failed
        /// to load), so it is set to the horizon colour; the far plane has to
        /// clear the 400 m dome or the sky is culled away. Callers pass their
        /// own neutral colour because the menu, the builder and the drive scene
        /// do not agree on one.
        /// </summary>
        /// <param name="sceneOwnsSky">
        /// Leave the camera's clear flags and background alone — a hand-authored
        /// scene track has a real skybox, baked reflection probes and its own
        /// RenderSettings, and the SolidColor assignment below replaces all of it
        /// with a flat colour. The far plane is still widened, because a scene can
        /// be as large as a tile map and clipping distant terrain is never wanted.
        /// </param>
        public static void ApplyCamera(Camera cam, string key, Color neutralBackground,
            bool sceneOwnsSky = false)
        {
            if (cam == null) return;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, 900f);
            if (sceneOwnsSky) return;
            var a = Resolve(key);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = a.HasDome ? a.skyHorizon : neutralBackground;
        }

        private static Shader _skyShader;
        private static bool _skyProbed;
        private static readonly System.Collections.Generic.Dictionary<string, Material> _domeMats
            = new System.Collections.Generic.Dictionary<string, Material>();

        private static void BuildDome(AmbienceDef a, Transform root)
        {
            if (!_skyProbed)
            {
                _skyProbed = true;
                _skyShader = Resources.Load<Shader>("SkyGradient");
                if (_skyShader == null)
                    Debug.LogWarning("[MapAmbience] SkyGradient shader missing — " +
                                     "falling back to the flat camera background.");
            }
            if (_skyShader == null) return;

            var dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dome.name = "SkyDome";
            UnityEngine.Object.Destroy(dome.GetComponent<Collider>());
            dome.transform.SetParent(root, false);
            dome.transform.localScale = Vector3.one * 800f;   // 400 m radius

            // One material per theme, cached: the builder rebuilds its preview
            // on every add/delete and a fresh Material each time would leak one
            // per edit for the whole session.
            if (!_domeMats.TryGetValue(a.key, out var mat) || mat == null)
            {
                mat = new Material(_skyShader) { name = "SkyGradient_" + a.key };
                mat.SetColor("_TopColor", a.skyTop);
                mat.SetColor("_HorizonColor", a.skyHorizon);
                mat.SetColor("_GroundColor", a.skyGround);
                mat.SetColor("_WedgeColor", a.wedge);
                mat.SetVector("_WedgeDir", a.wedgeDir);
                _domeMats[a.key] = mat;
            }
            var r = dome.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            // Ride with the camera. Without this the gradient slides as the car
            // crosses a 112 m map, because the dome is only 400 m out and the
            // camera walks a meaningful fraction of that.
            dome.AddComponent<SkyDomeFollow>();
        }

        private static void BuildGlows(AmbienceDef a, Transform root)
        {
            if (a.glows == null || a.glows.Length == 0) return;
            var holder = new GameObject("Glows");
            holder.transform.SetParent(root, false);
            foreach (var g in a.glows)
            {
                var go = new GameObject("Glow");
                go.transform.SetParent(holder.transform, false);
                go.transform.localPosition = g.pos;
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = g.range;
                l.intensity = g.intensity;
                l.color = g.color;
                l.shadows = LightShadows.None;   // these are mood, not shaping
            }
        }

        // ---- themed geometry -------------------------------------------------

        /// <summary>
        /// The attic's two walls, its skirting and its dado rail. This is not
        /// decoration: a floor that runs to a horizon reads as tarmac, and the
        /// whole premise of the toy map is that the car is a diecast toy loose
        /// on a real floor. The source map makes the same point at 130 units;
        /// at 1:10 that is 13 m of wall, which fills the top of frame from
        /// anywhere on the circuit.
        /// </summary>
        private static Material _paper, _trim, _rail;

        private static void BuildAtticRoom(Transform root)
        {
            const float wallN = 43f, wallW = -47f;   // the map's north/west edges
            const float h = 13f, thick = 0.5f, span = 120f;

            // Cached like the dome material: the builder rebuilds constantly.
            if (_paper == null)
            {
                _paper = TrackBuilder.StandardMat(Color.white);
                _paper.name = "AtticPaper";
                _paper.mainTexture = TrackBuilder.StripeTexture(
                    new Color(0.30f, 0.27f, 0.22f), new Color(0.40f, 0.35f, 0.28f), 8, 8);
                _paper.mainTextureScale = new Vector2(24f, 3f);
                _paper.SetFloat("_Glossiness", 0.05f);
                _trim = TrackBuilder.StandardMat(new Color(0.52f, 0.47f, 0.40f));
                _rail = TrackBuilder.StandardMat(new Color(0.38f, 0.34f, 0.28f));
            }
            Material paper = _paper, trim = _trim, rail = _rail;

            var room = new GameObject("AtticRoom");
            room.transform.SetParent(root, false);
            var p = room.transform;

            // North wall runs along X, west wall along Z. Both are solid — the
            // car should bounce off the skirting, not drive into the wallpaper.
            TrackBuilder.Box("Wall_N", new Vector3(0f, h * 0.5f, wallN + thick * 0.5f),
                new Vector3(span, h, thick), Quaternion.identity, paper, p);
            TrackBuilder.Box("Skirt_N", new Vector3(0f, 0.19f, wallN - 0.04f),
                new Vector3(span, 0.38f, 0.12f), Quaternion.identity, trim, p);
            TrackBuilder.Box("Rail_N", new Vector3(0f, 2.2f, wallN - 0.03f),
                new Vector3(span, 0.2f, 0.08f), Quaternion.identity, rail, p);

            TrackBuilder.Box("Wall_W", new Vector3(wallW - thick * 0.5f, h * 0.5f, 0f),
                new Vector3(thick, h, span), Quaternion.identity, paper, p);
            TrackBuilder.Box("Skirt_W", new Vector3(wallW + 0.04f, 0.19f, 0f),
                new Vector3(0.12f, 0.38f, span), Quaternion.identity, trim, p);
            TrackBuilder.Box("Rail_W", new Vector3(wallW + 0.03f, 2.2f, 0f),
                new Vector3(0.08f, 0.2f, span), Quaternion.identity, rail, p);
        }
    }

    /// <summary>Keeps the sky dome centred on the camera, so its gradient does
    /// not slide as the car crosses the map.</summary>
    public sealed class SkyDomeFollow : MonoBehaviour
    {
        private Transform _cam;

        private void LateUpdate()
        {
            if (_cam == null)
            {
                var c = Camera.main;
                if (c == null) return;
                _cam = c.transform;
            }
            transform.position = _cam.position;
        }
    }
}
