using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.UI
{
    /// <summary>
    /// The crate room's 3D half: one lit turntable showing either the crate
    /// about to be opened or the item that just came out of it, parked at
    /// (0, −700, 0) — its own address, clear of ShowroomRig at −600 — with its
    /// own camera drawn over everything else (depth 11).
    ///
    /// Unlike <see cref="Menu.ShowroomRig"/>, which shows a whole car and so has
    /// to render the DEFAULT layers, this rig culls to
    /// <see cref="PartVisualFactory.VizLayer"/> — every cosmetic and every crate
    /// lands there — which keeps the attract loop, the podium and this screen
    /// from ever appearing in each other's frames.
    ///
    /// The subject is framed by its own bounds, because the pack's items run
    /// from a 13 mm bobble to a 116 mm surfboard and the four crates are half a
    /// metre: one fixed camera would show a speck or a wall.
    /// </summary>
    public sealed class CrateRig : MonoBehaviour
    {
        private const float Turntable = 28f;   // deg/s — livelier than the showroom
        private static readonly Vector3 Home = new Vector3(0f, -700f, 0f);

        private Transform _stage;
        private GameObject _subject;
        private Camera _cam;
        private float _spinVel;
        private float _pop;                     // scale punch on a fresh reveal

        public static CrateRig Create()
        {
            var go = new GameObject("CrateRig");
            go.transform.position = Home;
            var rig = go.AddComponent<CrateRig>();
            rig.Build();
            return rig;
        }

        private void Build()
        {
            _stage = new GameObject("stage").transform;
            _stage.SetParent(transform, false);

            Spot("key", new Vector3(1.2f, 1.5f, -1.1f), 2.4f, new Color(1f, 0.96f, 0.88f));
            Spot("fill", new Vector3(-1.4f, 0.8f, -0.7f), 1.0f, new Color(0.72f, 0.82f, 1f));
            Spot("rimlight", new Vector3(0.1f, 1.0f, 1.6f), 1.5f, new Color(1f, 0.86f, 0.58f));

            var camGo = new GameObject("CrateCamera");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.depth = 11f;                     // over the showroom camera
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.03f, 0.04f, 0.08f);
            _cam.cullingMask = 1 << PartVisualFactory.VizLayer;
            _cam.fieldOfView = 34f;
            _cam.nearClipPlane = 0.005f;
            _cam.farClipPlane = 50f;
        }

        /// <summary>Show a crate or an item by its mesh key. Reframes the camera
        /// on whatever turns up, so a bobble and a vault both fill the panel.</summary>
        public void Show(string meshKey)
        {
            if (_subject != null) Destroy(_subject);
            _subject = null;
            _spinVel = 0f;
            _pop = 1f;
            if (string.IsNullOrEmpty(meshKey)) return;

            var holder = new GameObject("subject");
            holder.transform.SetParent(_stage, false);
            _subject = holder;
            if (CosmeticCatalog.Build(holder.transform, meshKey) == null)
            {
                Destroy(holder);
                _subject = null;
                return;
            }
            Frame();
        }

        /// <summary>Pull the camera back to fit the subject's bounds, and centre
        /// the subject on the turntable so it spins about itself rather than
        /// orbiting its own origin (a rim's origin is its hub, a topper's is its
        /// base — neither is the middle).</summary>
        private void Frame()
        {
            var rends = _subject.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return;
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

            _subject.transform.localPosition = _stage.position - b.center;

            float radius = Mathf.Max(0.01f, b.extents.magnitude);
            float dist = radius / Mathf.Tan(Mathf.Deg2Rad * _cam.fieldOfView * 0.5f) * 1.9f;
            var dir = new Vector3(0.35f, 0.42f, -1f).normalized;
            _cam.transform.position = transform.position + dir * dist;
            _cam.transform.LookAt(transform.position);
            _cam.nearClipPlane = Mathf.Max(0.005f, dist * 0.05f);
        }

        public void Spin(float velDegPerSec) => _spinVel += velDegPerSec;

        /// <summary>Kick the turntable, for the moment a reveal lands.</summary>
        public void Punch()
        {
            _spinVel += 420f;
            _pop = 0f;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _spinVel *= Mathf.Exp(-2.6f * dt);
            _stage.Rotate(0f, (Turntable + _spinVel) * dt, 0f, Space.Self);

            // Scale punch: overshoot to 1.12 and settle, so a reveal reads as an
            // arrival rather than a swap.
            if (_subject != null && _pop < 1f)
            {
                _pop = Mathf.Min(1f, _pop + dt * 3.2f);
                float s = 1f + 0.12f * Mathf.Sin(_pop * Mathf.PI) - 0.35f * (1f - _pop);
                _subject.transform.localScale = Vector3.one * Mathf.Max(0.05f, s);
            }
        }

        public void Dispose()
        {
            if (this != null && gameObject != null) Destroy(gameObject);
        }

        private void Spot(string name, Vector3 localPos, float intensity, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.LookAt(transform.position);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.spotAngle = 74f;
            l.range = 8f;
            l.intensity = intensity;
            l.color = color;
            l.cullingMask = 1 << PartVisualFactory.VizLayer;
        }
    }
}
