using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// Tire smoke while a drift is committed: soft puffs shed at the rear
    /// wheels, tinted from the car's own body colour, so every slide is signed
    /// by the car making it.
    ///
    /// The puffs live in WORLD space, under a detached pool root — smoke that
    /// rides along with the car reads as a decal, not as something the tyres
    /// are leaving behind. That is also why this component persists on the car
    /// instead of living on the drift-sparks object: the sparks are destroyed
    /// the instant the drift ends, and tearing the pool down with them would
    /// vanish every puff mid-fade. Here the director only flips
    /// <see cref="emitting"/>, and the trail finishes fading on its own.
    ///
    /// Pooled primitives on VizLayer, like every other cosmetic: a fixed ring
    /// of spheres recycled oldest-first, one shared alpha material, per-puff
    /// colour and fade through a MaterialPropertyBlock. No allocation after
    /// Awake, and a car's own CameraSensor never sees its own smoke.
    ///
    /// All numbers here are cosmetic-only — nothing reads back into physics or
    /// gameplay, which is why they are local rather than in ArcadeConfig.
    /// </summary>
    public sealed class DriftSmoke : MonoBehaviour
    {
        private const int PoolSize = 24;
        private const float EmitInterval = 0.05f;
        private const float PuffSeconds = 0.55f;
        private const float StartScale = 0.055f;
        private const float EndScale = 0.17f;
        private const float StartAlpha = 0.48f;
        /// <summary>How far toward white the body colour is pushed. Smoke made
        /// of pure saturated paint reads as magic; a pale tint of it reads as
        /// "that car's smoke".</summary>
        private const float TintToWhite = 0.4f;
        /// <summary>Rear-axle emit points in car space — the same stance as the
        /// drift sparks, a touch lower and further back.</summary>
        private static readonly Vector3 EmitLeft = new Vector3(-0.10f, -0.04f, -0.17f);
        private static readonly Vector3 EmitRight = new Vector3(0.10f, -0.04f, -0.17f);

        /// <summary>The director's one knob: true only while a slide is held.</summary>
        public bool emitting;

        private CarVehicle _car;
        private Color _tint = Color.white;
        private Transform _poolRoot;
        private readonly Transform[] _puff = new Transform[PoolSize];
        private readonly Renderer[] _rend = new Renderer[PoolSize];
        private readonly float[] _age = new float[PoolSize];      // >= PuffSeconds = dead
        private readonly Vector3[] _vel = new Vector3[PoolSize];
        private int _next;
        private float _sinceEmit;
        private bool _left;
        private MaterialPropertyBlock _mpb;

        /// <summary>Attach to a car, once; safe to call again (returns the
        /// existing one).</summary>
        public static DriftSmoke Attach(CarVehicle car)
        {
            if (car == null) return null;
            var s = car.GetComponent<DriftSmoke>();
            if (s == null)
            {
                s = car.gameObject.AddComponent<DriftSmoke>();
                s._car = car;
            }
            return s;
        }

        private void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            _poolRoot = new GameObject("DriftSmokePool").transform;

            for (int i = 0; i < PoolSize; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                go.name = "puff";
                go.layer = PartVisualFactory.VizLayer;
                go.transform.SetParent(_poolRoot, false);
                go.SetActive(false);
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = ArcadeVfx.DriftSmokeSkin;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _puff[i] = go.transform;
                _rend[i] = r;
                _age[i] = PuffSeconds;
            }
        }

        private void OnDestroy()
        {
            // The pool is deliberately detached (world-space smoke), so the
            // car's destruction does not reach it — clean it up by hand.
            if (_poolRoot != null) Destroy(_poolRoot.gameObject);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;   // paused: hold the trail rather than eat it

            if (_car == null) _car = GetComponent<CarVehicle>();

            // The tint tracks the car rather than being cached at attach time,
            // so a repaint in the garage carries into the next session's smoke.
            if (_car != null)
                _tint = Color.Lerp(_car.bodyColor, Color.white, TintToWhite);

            if (emitting && _car != null)
            {
                _sinceEmit += dt;
                while (_sinceEmit >= EmitInterval)
                {
                    _sinceEmit -= EmitInterval;
                    Spawn();
                }
            }
            else _sinceEmit = EmitInterval;   // first puff of the next drift is instant

            for (int i = 0; i < PoolSize; i++)
            {
                if (_age[i] >= PuffSeconds) continue;
                _age[i] += dt;
                if (_age[i] >= PuffSeconds) { _puff[i].gameObject.SetActive(false); continue; }

                float t = _age[i] / PuffSeconds;
                _puff[i].position += _vel[i] * dt;
                _puff[i].localScale = Vector3.one * Mathf.Lerp(StartScale, EndScale, Mathf.Sqrt(t));

                var c = _tint;
                c.a = StartAlpha * (1f - t) * (1f - t);
                _mpb.SetColor("_Color", c);
                _rend[i].SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Recycle the oldest slot as a fresh puff at the rear wheel,
        /// alternating sides so the trail is a double line, like the tyres.</summary>
        private void Spawn()
        {
            int i = _next;
            _next = (_next + 1) % PoolSize;
            _left = !_left;

            var car = _car.transform;
            _puff[i].position = car.TransformPoint(_left ? EmitLeft : EmitRight);
            _puff[i].localScale = Vector3.one * StartScale;
            // Mostly up, drifting slightly out of the car's wake — enough
            // motion to look alive, not enough to chase the car.
            _vel[i] = Vector3.up * 0.22f - car.forward * 0.15f +
                      car.right * (_left ? -0.06f : 0.06f);
            _age[i] = 0f;

            var c = _tint;
            c.a = StartAlpha;
            _mpb.SetColor("_Color", c);
            _rend[i].SetPropertyBlock(_mpb);
            _puff[i].gameObject.SetActive(true);
        }
    }
}
