using System;
using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Generates part-palette icons by photographing the real
    /// <see cref="PartVisualFactory"/> geometry into a small transparent texture —
    /// so the palette thumbnails can never drift from what actually gets built.
    /// Icons are rendered once, off to the side of the world, and cached by key.
    /// </summary>
    public static class PartIconFactory
    {
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        public static Texture2D Icon(string key)
        {
            if (_cache.TryGetValue(key, out var t) && t != null) return t;
            t = Snapshot(BuildFor(key), 64);
            _cache[key] = t;
            return t;
        }

        /// <summary>
        /// The build callback behind a palette key — shared by the icon snapshots
        /// and the live rotating hover preview so both always show the same part.
        /// </summary>
        public static Action<Transform> BuildFor(string key)
        {
            return key switch
            {
                "wheel"         => p => PartVisualFactory.BuildWheelViz(p, 0.033f, false, -1f, WheelCatalog.Default),
                "wheel_powered" => p => PartVisualFactory.BuildWheelViz(p, 0.033f, true, -1f, WheelCatalog.Default),
                "camera"        => p => PartVisualFactory.BuildCameraViz(p),
                "tof"           => p => PartVisualFactory.BuildTofViz(p),
                "encoder"       => p => PartVisualFactory.BuildEncoderViz(p),
                "suspension"    => p => PartVisualFactory.BuildSuspensionViz(p),
                "color"         => p => PartVisualFactory.BuildColorViz(p),
                "mag"           => p => PartVisualFactory.BuildMagViz(p),
                "bump"          => p => PartVisualFactory.BuildBumpViz(p),
                "rf"            => p => PartVisualFactory.BuildRfViz(p),
                // The LED's dome deliberately lives on the DEFAULT layer (the
                // sensor camera must see it); the icon camera culls to the viz
                // layer, so re-layer the snapshot copy or the dome is missing.
                "led"           => p =>
                {
                    PartVisualFactory.BuildLedViz(p);
                    foreach (var r in p.GetComponentsInChildren<Renderer>())
                        r.gameObject.layer = PartVisualFactory.VizLayer;
                },
                "battery"       => p => PartVisualFactory.BuildBatteryViz(p),
                "antenna"       => p => PartVisualFactory.BuildAntennaViz(p, 15f, 1f),
                "light"         => p => PartVisualFactory.BuildLightViz(p, 0, 1f),
                "wing"          => p => PartVisualFactory.BuildWingViz(p, 8f, 1f),
                "splitter"      => p => PartVisualFactory.BuildSplitterViz(p, 1f),
                "sidedam"       => p => PartVisualFactory.BuildSideDamViz(p, 1f),
                "canard"        => p => PartVisualFactory.BuildCanardViz(p, 8f, 1f),
                _               => p => PartVisualFactory.BuildTofViz(p),
            };
        }

        /// <summary>
        /// Photograph arbitrary geometry into a small transparent icon. The build
        /// callback must place everything it wants visible on
        /// <see cref="PartVisualFactory.VizLayer"/> (the camera culls to it). Also
        /// used by the track builder's item palette.
        /// </summary>
        public static Texture2D Snapshot(Action<Transform> build, int size)
        {
            const float fov = 32f;
            var origin = new Vector3(0f, -500f, 0f); // far from the real scene

            var root = new GameObject("icon_build") { hideFlags = HideFlags.HideAndDontSave };
            root.transform.position = origin;
            build(root.transform);

            Bounds b = ComputeBounds(root);
            float radius = Mathf.Max(0.05f, b.extents.magnitude);
            float dist = radius / Mathf.Tan(Mathf.Deg2Rad * fov * 0.5f) * 1.4f;
            Vector3 dir = new Vector3(0.85f, 0.65f, -1f).normalized;

            var camGo = new GameObject("icon_cam") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist * 4f + 10f;
            cam.cullingMask = 1 << PartVisualFactory.VizLayer;
            cam.transform.position = b.center + dir * dist;
            cam.transform.LookAt(b.center);

            var lightGo = new GameObject("icon_light") { hideFlags = HideFlags.HideAndDontSave };
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.cullingMask = 1 << PartVisualFactory.VizLayer;
            light.transform.rotation = Quaternion.Euler(38f, 20f, 0f);

            var rt = RenderTexture.GetTemporary(size, size, 16, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            var prev = RenderTexture.active;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);

            // Deactivate BEFORE the deferred Destroy. Destroy only takes effect at
            // the end of the frame, but cam.Render() above is immediate and both
            // palettes snapshot their whole icon set inside one frame — so without
            // this, icon N photographs every model built for icons 1..N-1 still
            // parked at `origin` (and is lit by their leftover lights). SetActive
            // is immediate, so each snapshot sees only its own geometry.
            root.SetActive(false);
            camGo.SetActive(false);
            lightGo.SetActive(false);
            UnityEngine.Object.Destroy(root);
            UnityEngine.Object.Destroy(camGo);
            UnityEngine.Object.Destroy(lightGo);
            return tex;
        }

        private static Bounds ComputeBounds(GameObject root)
        {
            var rends = root.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return new Bounds(root.transform.position, Vector3.one * 0.3f);
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }
    }
}
