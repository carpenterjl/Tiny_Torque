using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Live rotating 3D preview of a palette part, shown inside the hover tooltip.
    /// Persistent sibling of <see cref="PartIconFactory.Snapshot"/>: the part's
    /// real <see cref="PartVisualFactory"/> geometry spins under a dedicated
    /// camera that renders into a small RenderTexture each frame while visible.
    /// Costs nothing when hidden (camera disabled, geometry cached per key).
    /// Owned and ticked by <see cref="GarageUI"/>.
    /// </summary>
    public sealed class PartPreviewRig
    {
        public const int Size = 160;

        private static readonly Vector3 Origin = new Vector3(0f, -600f, 0f);

        private string _key;
        private GameObject _root, _spin, _camGo, _lightGo;
        private Camera _cam;
        private RenderTexture _rt;
        private float _yaw;

        /// <summary>The live preview image (valid while shown this frame).</summary>
        public Texture Texture => _rt;

        /// <summary>Show (or switch to) the part behind a palette key.</summary>
        public void Show(string key)
        {
            EnsureRig();
            if (key != _key)
            {
                _key = key;
                if (_spin != null) Object.Destroy(_spin);
                _spin = new GameObject("preview_part") { hideFlags = HideFlags.HideAndDontSave };
                _spin.transform.SetParent(_root.transform, false);
                PartIconFactory.BuildFor(key)(_spin.transform);
                FrameCamera();
            }
            _root.SetActive(true);
            _cam.enabled = false; // rendered manually from Tick
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>Advance the turntable + render one frame (call while shown).</summary>
        public void Tick(float dt)
        {
            if (_root == null || !_root.activeSelf || _spin == null) return;
            _yaw += 40f * dt;
            _spin.transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            _cam.Render();
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            if (_camGo != null) Object.Destroy(_camGo);
            if (_lightGo != null) Object.Destroy(_lightGo);
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
            _root = _spin = _camGo = _lightGo = null;
            _cam = null; _rt = null; _key = null;
        }

        private void EnsureRig()
        {
            if (_root != null) return;

            _root = new GameObject("part_preview_rig") { hideFlags = HideFlags.HideAndDontSave };
            _root.transform.position = Origin;

            _rt = new RenderTexture(Size, Size, 16, RenderTextureFormat.ARGB32)
            {
                name = "part_preview_rt",
                hideFlags = HideFlags.HideAndDontSave,
            };

            _camGo = new GameObject("preview_cam") { hideFlags = HideFlags.HideAndDontSave };
            _camGo.transform.SetParent(_root.transform, false);
            _cam = _camGo.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.fieldOfView = 32f;
            _cam.nearClipPlane = 0.01f;
            _cam.cullingMask = 1 << PartVisualFactory.VizLayer;
            _cam.targetTexture = _rt;

            _lightGo = new GameObject("preview_light") { hideFlags = HideFlags.HideAndDontSave };
            _lightGo.transform.SetParent(_root.transform, false);
            var light = _lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.cullingMask = 1 << PartVisualFactory.VizLayer;
            light.transform.rotation = Quaternion.Euler(38f, 20f, 0f);
        }

        // Frame the camera on the freshly built part (¾ view, like the icons).
        private void FrameCamera()
        {
            var rends = _spin.GetComponentsInChildren<Renderer>(true);
            Bounds b = rends.Length > 0
                ? rends[0].bounds
                : new Bounds(_spin.transform.position, Vector3.one * 0.05f);
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

            float radius = Mathf.Max(0.02f, b.extents.magnitude);
            float dist = radius / Mathf.Tan(Mathf.Deg2Rad * _cam.fieldOfView * 0.5f) * 1.5f;
            Vector3 dir = new Vector3(0.85f, 0.55f, -1f).normalized;
            _cam.farClipPlane = dist * 4f + 10f;
            _cam.transform.position = b.center + dir * dist;
            _cam.transform.LookAt(b.center);
        }
    }
}
