using AIHWSim.Audio;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Menu
{
    /// <summary>
    /// The Showroom's 3D half: a glossy navy podium with a gold rim under
    /// three-point lighting, parked at (0, −600, 0) — the same out-of-the-way
    /// address PartPreviewRig uses — with its own camera drawn OVER the live
    /// attract loop (depth 10). The attract keeps running underneath, so
    /// backing out of the showroom costs nothing.
    ///
    /// The camera culls the DEFAULT layers, not the viz-only mask
    /// PartPreviewRig uses: a whole car's body lives on the default layer and
    /// a viz-only camera would show wheels, lights and wings orbiting an
    /// invisible chassis.
    ///
    /// Turntable + player spin (right stick / mouse drag, with decay), a
    /// cosmetic rev (pitch-ramped 2D engine loop + body squat — pure theatre,
    /// the preview car is kinematic), and a horn test.
    /// </summary>
    public sealed class ShowroomRig : MonoBehaviour
    {
        private const float Turntable = 10f;      // deg/s baseline
        private const float DefaultRest = 0.078f; // stock car origin → contact patch
        private static readonly Vector3 Home = new Vector3(0f, -600f, 0f);

        private Transform _stage;                  // rotates: podium + car
        private Transform _floor, _rim;            // dropped to the car's contact patch
        private GameObject _car;
        private Camera _cam;
        private AudioSource _rev;
        private float _spinVel;
        private float _revLevel;
        private int _hornStyle;

        public static ShowroomRig Create()
        {
            var go = new GameObject("ShowroomRig");
            go.transform.position = Home;
            var rig = go.AddComponent<ShowroomRig>();
            rig.Build();
            return rig;
        }

        private void Build()
        {
            _stage = new GameObject("stage").transform;
            _stage.SetParent(transform, false);

            // Podium: glossy near-black navy disc with a gold rim ring below
            // the lip — the title art's floor, at toy scale.
            _floor = Disc("podium", new Color(0.05f, 0.065f, 0.10f), 0.95f,
                new Vector3(0f, -0.015f, 0f), new Vector3(1.15f, 0.015f, 1.15f));
            _floor.SetParent(_stage, false);
            _rim = Disc("rim", new Color(0.94f, 0.78f, 0.36f), 0.85f,
                new Vector3(0f, -0.031f, 0f), new Vector3(1.22f, 0.008f, 1.22f));
            _rim.SetParent(_stage, false);
            DropFloor(DefaultRest);

            // Three-point light. Culling stays default — the rig sits 600 m
            // below the attract circuit, far outside every light's range there.
            Spot("key", new Vector3(1.4f, 1.6f, 1.2f), 2.6f, new Color(1f, 0.95f, 0.85f));
            Spot("fill", new Vector3(-1.6f, 0.9f, 0.6f), 0.9f, new Color(0.7f, 0.8f, 1f));
            Spot("rimlight", new Vector3(0.2f, 1.2f, -1.8f), 1.6f, new Color(1f, 0.85f, 0.55f));

            var camGo = new GameObject("ShowroomCamera");
            camGo.transform.SetParent(transform, false);
            camGo.transform.localPosition = new Vector3(0.55f, 0.30f, -0.72f);
            camGo.transform.LookAt(transform.position + new Vector3(0f, 0.06f, 0f));
            _cam = camGo.AddComponent<Camera>();
            _cam.depth = 10f;                       // over the attract camera
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.045f, 0.06f, 0.11f);
            _cam.nearClipPlane = 0.02f;
            _cam.farClipPlane = 50f;

            var revGo = new GameObject("rev");
            revGo.transform.SetParent(transform, false);
            _rev = revGo.AddComponent<AudioSource>();
            _rev.playOnAwake = false;
            _rev.loop = true;
            _rev.spatialBlend = 0f;
            _rev.volume = 0f;
            var clip = ProceduralAudio.Get(ProceduralAudio.Engine);
            if (clip != null) { _rev.clip = clip; _rev.Play(); }
        }

        /// <summary>Swap the displayed car for this (already loadout-applied) design.</summary>
        public void Show(VehicleDesign design)
        {
            if (_car != null) Destroy(_car);
            _car = null;
            if (design == null) return;
            var built = VehicleFactory.Build(design, transform.position, Quaternion.identity,
                previewKinematic: true);
            _car = built.root;
            _car.transform.SetParent(_stage, true);
            _hornStyle = design.hornStyle;
            DropFloor(RestHeight(design));
        }

        /// <summary>
        /// Distance from a built car's origin (the chassis/rigidbody point
        /// VehicleFactory.Build places at the spawn position) down to the lowest
        /// tyre's contact patch. The rest of the codebase hardcodes this as
        /// 0.078 m for the default design and puts the ground plane there
        /// (MenuBootstrap, GarageBootstrap); computing it per design keeps
        /// big-wheel cars like TT Baja sitting flat too.
        /// </summary>
        private static float RestHeight(VehicleDesign d)
        {
            float rest = 0f;
            if (d?.wheels != null)
            {
                for (int i = 0; i < d.wheels.Count; i++)
                {
                    var w = d.wheels[i];
                    if (w == null) continue;
                    float hubY = w.localPos.y +
                                 SuspensionGeometry.HubOffsetLocal(w.localPos.x, w.suspAngleDeg, w.suspLength).y;
                    rest = Mathf.Max(rest, -(hubY - w.radius));
                }
            }
            return rest > 0.0001f ? rest : DefaultRest;
        }

        /// <summary>Park the podium's top surface at the car's contact patch, so
        /// the wheels rest ON the disc instead of sinking through it. The car
        /// itself stays at stage origin — that's what the camera framing, the
        /// turntable and the rev squat are all tuned around.</summary>
        private void DropFloor(float rest)
        {
            if (_floor == null || _rim == null) return;
            _floor.localPosition = new Vector3(0f, -0.015f - rest, 0f);
            _rim.localPosition = new Vector3(0f, -0.031f - rest, 0f);
        }

        public void Spin(float velDegPerSec) => _spinVel += velDegPerSec;

        /// <summary>0..1 rev input this frame (pure audio + squat theatre).</summary>
        public void Rev(float amount01)
        {
            _revLevel = Mathf.Clamp01(amount01);
        }

        public void Honk()
        {
            SfxPlayer.Ensure()?.PlayUi(ProceduralAudio.HornKey(_hornStyle));
        }

        private void Update()
        {
            _spinVel *= Mathf.Exp(-3f * Time.unscaledDeltaTime);
            _stage.Rotate(0f, (Turntable + _spinVel) * Time.unscaledDeltaTime, 0f, Space.Self);

            if (_rev != null)
            {
                _rev.pitch = Mathf.Lerp(0.4f, 1.8f, _revLevel);
                float target = _revLevel > 0.02f ? 0.10f + 0.55f * _revLevel : 0f;
                _rev.volume = Mathf.MoveTowards(_rev.volume, target * SfxPlayer.SfxGain,
                    Time.unscaledDeltaTime * 3f);
            }
            if (_car != null)
            {
                // The squat: nose lifts a touch under "throttle".
                var e = _car.transform.localEulerAngles;
                e.x = Mathf.LerpAngle(e.x, -2.5f * _revLevel, Time.unscaledDeltaTime * 8f);
                _car.transform.localEulerAngles = e;
            }
        }

        public void Dispose()
        {
            if (this != null && gameObject != null) Destroy(gameObject);
        }

        private static Transform Disc(string name, Color c, float gloss, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var mat = new Material(Shader.Find("Standard")) { color = c };
            mat.SetFloat("_Glossiness", gloss);
            mat.SetFloat("_Metallic", 0.4f);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            return go.transform;
        }

        private void Spot(string name, Vector3 localPos, float intensity, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.LookAt(transform.position);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.spotAngle = 70f;
            l.range = 8f;
            l.intensity = intensity;
            l.color = color;
        }
    }
}
