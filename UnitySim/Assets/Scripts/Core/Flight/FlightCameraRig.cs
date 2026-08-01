using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Three ways to watch a model aeroplane, cycled with V.
    ///
    /// <b>The ground station is the default, and that is a fidelity decision.</b>
    /// RC is flown standing on the ground watching a shape in the distance, and the
    /// single hardest thing about it is that when the aeroplane is coming toward
    /// you, left stick rolls it to YOUR right. A chase camera hides that reversal
    /// completely. A simulator that hides it is teaching a skill that does not
    /// transfer, so the view that tells the truth is the one it opens in.
    ///
    /// <b><see cref="ChaseCamera"/> is reused unmodified.</b> Its yaw-only follow
    /// is right here for exactly the reason it is right on a car: the horizon stays
    /// level, so bank reads as bank rather than as the world tilting. Only the
    /// boresight view needs roll, and it gets it by parenting the camera to the
    /// aircraft — no new maths, and a zero-line diff on a file the race code uses.
    /// </summary>
    public sealed class FlightCameraRig : MonoBehaviour
    {
        public enum View { GroundStation = 0, Chase = 1, Boresight = 2 }

        public Transform target;

        [Header("Ground station")]
        /// <summary>Where the pilot stands. Beside the strip, at eye height.</summary>
        public Vector3 pilotPosition = new Vector3(-12f, 1.6f, -60f);
        public float minFov = 22f;
        public float maxFov = 60f;
        /// <summary>Range at which the FOV has narrowed all the way down. Real
        /// pilots do this with their eyes; a fixed FOV would lose the model.</summary>
        public float fovFullZoomRange = 220f;
        public float aimLerp = 12f;

        [Header("Chase")]
        public Vector3 chaseOffset = new Vector3(0f, 2.5f, -12f);
        public float chaseLerp = 4f;

        [Header("Boresight")]
        public Vector3 boresightOffset = new Vector3(0f, 0.9f, -3.5f);

        public View Current { get; private set; } = View.GroundStation;

        private Camera _cam;
        private ChaseCamera _chase;
        private float _baseFov;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _baseFov = _cam != null ? _cam.fieldOfView : 60f;

            // Added here, disabled, so there is exactly one ChaseCamera on exactly
            // one camera. A second Camera object would mean a second AudioListener,
            // which Unity warns about every frame.
            _chase = gameObject.GetComponent<ChaseCamera>();
            if (_chase == null) _chase = gameObject.AddComponent<ChaseCamera>();
            _chase.offset = chaseOffset;
            _chase.followLerp = chaseLerp;
            _chase.enabled = false;
        }

        private void Start() => Apply(Current);

        public void Cycle() => Apply((View)(((int)Current + 1) % 3));

        public void Apply(View v)
        {
            Current = v;
            if (target == null) return;

            _chase.target = target;
            _chase.enabled = v == View.Chase;

            if (v == View.Boresight)
            {
                transform.SetParent(target, false);
                transform.localPosition = boresightOffset;
                transform.localRotation = Quaternion.identity;
            }
            else
            {
                transform.SetParent(null, true);
            }

            if (v == View.GroundStation)
            {
                transform.position = pilotPosition;
                if (_cam != null) _cam.fieldOfView = _baseFov;
            }
        }

        private void LateUpdate()
        {
            if (target == null || Current != View.GroundStation) return;

            // Stand still and turn your head — which is all a pilot does.
            transform.position = pilotPosition;
            Vector3 to = target.position - pilotPosition;
            if (to.sqrMagnitude < 1e-4f) return;

            Quaternion look = Quaternion.LookRotation(to.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, look,
                                                  1f - Mathf.Exp(-aimLerp * Time.deltaTime));

            if (_cam != null)
            {
                float t = Mathf.Clamp01(to.magnitude / Mathf.Max(1f, fovFullZoomRange));
                _cam.fieldOfView = Mathf.Lerp(maxFov, minFov, t);
            }
        }
    }
}
