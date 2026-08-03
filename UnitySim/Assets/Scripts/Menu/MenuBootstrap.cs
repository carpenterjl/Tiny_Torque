using AIHWSim.Garage;
using AIHWSim.Persistence;
using UnityEngine;

namespace AIHWSim.Menu
{
    /// <summary>
    /// Builds the main-menu scene at runtime: applies saved options, then either a
    /// live <see cref="MenuAttract"/> loop (AI cars driving a random circuit behind
    /// the UI) or — if that fails — a dark showroom backdrop with a slowly rotating
    /// showcar, plus the <see cref="MenuUI"/>. Drop on one empty GameObject
    /// (Tools ▸ AIHWSim ▸ Create Menu Scene).
    /// </summary>
    public sealed class MenuBootstrap : MonoBehaviour
    {
        private Transform _showcar;

        private void Awake()
        {
            // Hosting must keep simulating when the window loses focus, or an
            // alt-tabbed LAN host would freeze the whole match.
            Application.runInBackground = true;
            SettingsStore.Apply();

            BuildLighting();

            // Attract mode: cars driving a map. Fall back to the static showcar if
            // the track/bot build fails for any reason (never a black menu).
            var attract = gameObject.AddComponent<MenuAttract>();
            if (!attract.Build())
            {
                Destroy(attract);
                BuildBackdrop();
                BuildShowcar();
                BuildCamera();
            }

            // First boot runs the intro (video → title card) and creates the
            // menu UI when it finishes; returning from a race goes straight in.
            var uiGo = new GameObject("MenuUI");
            if (!SplashSequence.ShownThisBoot && !Application.isBatchMode)
                SplashSequence.Run(gameObject, () => uiGo.AddComponent<MenuUI>());
            else
                uiGo.AddComponent<MenuUI>();
        }

        private void BuildLighting()
        {
            RenderSettings.ambientLight = new Color(0.35f, 0.36f, 0.40f);
            Core.Boot.SceneRig.BuildLighting(1.0f, new Vector3(45f, 35f, 0f));
        }

        private void BuildBackdrop()
        {
            var mat = new Material(Shader.Find("Standard")) { color = new Color(0.16f, 0.17f, 0.20f) };
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "MenuFloor";
            floor.transform.localScale = new Vector3(1f, 1f, 1f);
            floor.transform.position = new Vector3(0f, -0.078f, 0f);
            floor.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private void BuildShowcar()
        {
            var design = ResolveShowDesign();
            var built = VehicleFactory.Build(design, Vector3.zero, Quaternion.identity, previewKinematic: true);
            _showcar = built.root.transform;
        }

        private static VehicleDesign ResolveShowDesign()
        {
            string last = SettingsStore.Current.lastVehicle;
            if (!string.IsNullOrEmpty(last))
            {
                var d = VehicleLibrary.Load(last);
                if (d != null) return d;
                // ★ presets are not library files — resolve them like
                // MenuUI.ResolveVehicle does, so the showcar matches the pick.
                d = VehiclePresets.Resolve(last);
                if (d != null) return d;
            }
            return VehicleDesign.Default();
        }

        private void BuildCamera()
        {
            var cam = Core.Boot.SceneRig.CameraOrCreate();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
            cam.farClipPlane = 100f;
            cam.transform.position = new Vector3(0.5f, 0.28f, -0.65f);
            cam.transform.LookAt(new Vector3(0f, 0.05f, 0f));
            Rendering.CameraBloom.Attach(cam);
        }

        private void Update()
        {
            if (_showcar != null)
                _showcar.Rotate(0f, 12f * Time.deltaTime, 0f, Space.World);
        }
    }
}
