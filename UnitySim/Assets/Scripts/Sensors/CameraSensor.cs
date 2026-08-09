using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Aimable grayscale camera. Renders the scene from this GameObject's pose to
    /// a small off-screen RenderTexture at a fixed frame rate, then reads it back
    /// into a row-major grayscale byte buffer that is shipped to the controller
    /// via the ABI (cam_pixels). A Texture2D preview is exposed for the on-screen
    /// picture-in-picture HUD.
    ///
    /// Contributes no floats to sensor_data (DataCount == 0); the image is the
    /// payload.
    /// </summary>
    public sealed class CameraSensor : SensorComponent
    {
        [Header("Camera")]
        [Range(8, 128)] public int width = 64;
        // 128 rather than the 96 it was: the cap used to be shaped like a
        // letterboxed viewport, and a perception loop wants a square frame at
        // least as often as it wants a wide one. Nothing downstream cared —
        // the readback, the grayscale pack and the ABI are all width x height.
        [Range(8, 128)] public int height = 48;
        public float fieldOfView = 60f;
        [Tooltip("Capture frame rate (Hz). Independent of the control rate.")]
        public float frameRateHz = 10f;

        private static readonly string[] Fields = System.Array.Empty<string>();

        private Camera _cam;
        private RenderTexture _rt;
        private Texture2D _tex;      // RGBA readback + HUD preview
        private byte[] _gray;
        private Color32[] _scratch;
        private float _nextCaptureTime = -1f;

        public override SensorType Type => SensorType.Camera;
        public override int DataCount => 0;
        public override IReadOnlyList<string> FieldNames => Fields;

        /// <summary>Latest grayscale frame, or null. Row-major, length ==
        /// Width*Height, <b>row 0 = the TOP of the image</b> — see
        /// <c>controller_api.h</c>, which is where that convention is contracted.</summary>
        public byte[] Pixels => _gray;
        public int Width => width;
        public int Height => height;
        /// <summary>Preview texture for the HUD (updated on each capture).</summary>
        public Texture2D Preview => _tex;

        /// <summary>
        /// Captures completed so far. A consumer that wants each frame ONCE — the
        /// IPC camera stream does — watches this rather than the pixel buffer:
        /// the buffer is reused in place, so it is not possible to tell a fresh
        /// frame from last one's leftovers by looking at it, and a stationary car
        /// pointed at a wall produces identical pixels for a genuinely new frame.
        /// </summary>
        public int FrameIndex { get; private set; }

        /// <summary>Sim time of the most recent capture, so a streamed frame can
        /// be timestamped with when it was TAKEN rather than when it was sent.
        /// The camera runs at its own rate (default 10 Hz), well below the frame
        /// rate that polls it, and the difference is visible.</summary>
        public float LastCaptureTime { get; private set; }

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            EnsureCamera(vehicleRoot);
        }

        private void EnsureCamera(Transform vehicleRoot)
        {
            width = Mathf.Clamp(width, 8, 128);
            height = Mathf.Clamp(height, 8, 128);

            _rt = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32) { name = sensorName + "_RT" };
            _rt.Create();
            _tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _gray = new byte[width * height];

            _cam = GetComponent<Camera>();
            if (_cam == null) _cam = gameObject.AddComponent<Camera>();
            _cam.fieldOfView = fieldOfView;
            _cam.nearClipPlane = 0.03f;
            _cam.farClipPlane = 200f;
            _cam.targetTexture = _rt;
            _cam.enabled = false;              // rendered on demand from Capture()
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.53f, 0.70f, 0.92f);
            // Never let stylized part geometry (own housing/lens, other parts'
            // visuals) show up in or occlude the sensor image.
            _cam.cullingMask &= ~(1 << PartVisualFactory.VizLayer);
            // Don't let this camera hijack audio.
            var al = GetComponent<AudioListener>();
            if (al != null) Destroy(al);
        }

        public override void Sample(float dt, float[] dest, int offset) { /* image-only */ }

        /// <summary>Render + read back a frame if the frame interval has elapsed.</summary>
        public void CaptureIfDue(float simTime)
        {
            if (_cam == null) return;
            float period = frameRateHz > 0.01f ? 1f / frameRateHz : 0.1f;
            if (_nextCaptureTime >= 0f && simTime < _nextCaptureTime) return;
            _nextCaptureTime = simTime + period;

            var prev = RenderTexture.active;
            _cam.Render();
            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            _tex.Apply(false);
            RenderTexture.active = prev;

            // GetPixels32 hands back the image BOTTOM row first (Unity texture
            // space), but cam_pixels is defined top-down — the usual vision
            // convention, and the one a controller author will assume. Reverse the
            // row index on the way out; per-pixel cost is unchanged.
            _scratch = _tex.GetPixels32();
            int n = Mathf.Min(width, _scratch.Length / Mathf.Max(1, height));
            for (int y = 0; y < height; y++)
            {
                int src = y * width;                    // row 0 = bottom
                int dst = (height - 1 - y) * width;     // row 0 = top
                for (int x = 0; x < n; x++)
                {
                    var c = _scratch[src + x];
                    _gray[dst + x] = (byte)((c.r + c.g + c.b) / 3);
                }
            }

            LastCaptureTime = simTime;
            FrameIndex++;
        }

        private void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_tex != null) Destroy(_tex);
        }
    }
}
