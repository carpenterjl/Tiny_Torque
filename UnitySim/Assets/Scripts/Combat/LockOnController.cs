using System.Collections.Generic;
using AIHWSim.Audio;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// The Hydra's seeker head, minus the button: lock-on is AUTOMATIC. While
    /// missiles are the selected weapon, the nearest valid target inside a
    /// forward cone starts acquiring on its own, the HUD circle fills green,
    /// and after <see cref="acquireSeconds"/> it snaps to a solid red lock with
    /// a confirmation chirp. No scan key to hold — the pilot's job is pointing
    /// the aeroplane, which is already a full-time job.
    ///
    /// States: None → Acquiring (seeker tone, progress exposed for the HUD) →
    /// Locked. A target that slips out of the cone gets a short grace before
    /// the lock drops — a hard boundary would flicker exactly when the pilot is
    /// manoeuvring hardest, which is exactly when they need the answer stable.
    ///
    /// The cone and selection maths are static and pure so the [LOCK] bench can
    /// interrogate them without a scene.
    /// </summary>
    public sealed class LockOnController : MonoBehaviour
    {
        public enum LockState { None = 0, Acquiring = 1, Locked = 2 }

        public PlaneVehicle plane;

        [Tooltip("Half-angle of the seeker cone, degrees off the nose.")]
        public float coneHalfAngleDeg = 15f;
        [Tooltip("Seeker range against AIR targets (m).")]
        public float airRange = 700f;
        [Tooltip("Seeker range against ground/static targets (m).")]
        public float groundRange = 450f;
        [Tooltip("Seconds of continuous track before the lock goes red.")]
        public float acquireSeconds = 1.2f;
        [Tooltip("Seconds a tracked target may leave the cone before the seeker "
                 + "lets go. Stability at the boundary, not generosity.")]
        public float graceSeconds = 0.3f;

        /// <summary>Set by the weapons controller: the seeker only runs while
        /// missiles are the selected weapon.</summary>
        [System.NonSerialized] public bool active;

        public LockState State { get; private set; }
        public WeaponTarget Target { get; private set; }
        /// <summary>Acquisition progress, 0…1 — the HUD circle's fill.</summary>
        public float Progress { get; private set; }

        private float _grace;
        private AudioSource _seekSrc;

        private void Awake()
        {
            // The seeker tone is cockpit audio, not world audio: a looping 2D
            // source of our own, because SfxPlayer's pool is one-shots.
            _seekSrc = gameObject.AddComponent<AudioSource>();
            SfxPlayer.Configure(_seekSrc, spatial: false);
            _seekSrc.clip = ProceduralAudio.Get(ProceduralAudio.LockSeek);
            _seekSrc.loop = true;
        }

        private void Update()
        {
            if (!active || plane == null)
            {
                Drop();
                return;
            }

            WeaponTarget best = PickBest(plane.transform.position,
                                         plane.transform.forward);

            if (best == null)
            {
                // Nothing in the cone. A tracked target gets its grace; an
                // empty seeker just stays empty.
                if (Target != null)
                {
                    _grace += Time.deltaTime;
                    if (_grace >= graceSeconds || !Target.Alive) Drop();
                }
                UpdateAudio();
                return;
            }

            if (best != Target)
            {
                // New (or first) candidate: start the clock over. Nearest wins
                // even over an existing track — the Hydra retargets, it does
                // not go steady.
                Target = best;
                State = LockState.Acquiring;
                Progress = 0f;
            }

            _grace = 0f;
            if (State == LockState.Acquiring)
            {
                Progress = Mathf.Min(1f, Progress + Time.deltaTime / acquireSeconds);
                if (Progress >= 1f)
                {
                    State = LockState.Locked;
                    SfxPlayer.Ensure()?.Play2D(ProceduralAudio.LockConfirm);
                }
            }

            UpdateAudio();
        }

        private void Drop()
        {
            Target = null;
            State = LockState.None;
            Progress = 0f;
            _grace = 0f;
            UpdateAudio();
        }

        private void UpdateAudio()
        {
            bool want = State == LockState.Acquiring;
            if (want && !_seekSrc.isPlaying) _seekSrc.Play();
            else if (!want && _seekSrc.isPlaying) _seekSrc.Stop();
        }

        /// <summary>The nearest live target inside the cone, honouring per-
        /// category range. Iterates the registry — never a scene scan.</summary>
        private WeaponTarget PickBest(Vector3 origin, Vector3 forward)
        {
            WeaponTarget best = null;
            float bestDist = float.MaxValue;
            var all = WeaponTarget.All;
            for (int i = 0; i < all.Count; i++)
            {
                WeaponTarget t = all[i];
                if (t == null || !t.Alive || !t.gameObject.activeInHierarchy) continue;
                float range = t.category == WeaponTarget.Category.Air
                    ? airRange : groundRange;
                float dist = Vector3.Distance(origin, t.AimPoint);
                if (dist >= bestDist) continue;
                if (!InCone(origin, forward, t.AimPoint, coneHalfAngleDeg, range))
                    continue;
                best = t;
                bestDist = dist;
            }
            return best;
        }

        // ---- pure maths, benched by [LOCK] -------------------------------

        /// <summary>Whether a point is inside the seeker cone: within range and
        /// within the half-angle of the boresight. Pure, so the bench can walk
        /// the 15° boundary without building an aeroplane.</summary>
        public static bool InCone(Vector3 origin, Vector3 forward, Vector3 point,
                                  float halfAngleDeg, float range)
        {
            Vector3 to = point - origin;
            float dist = to.magnitude;
            if (dist <= 1e-4f) return true;   // on top of us is inside any cone
            if (dist > range) return false;
            return Vector3.Angle(forward, to) <= halfAngleDeg;
        }

        /// <summary>Index of the nearest in-cone candidate, −1 for none — the
        /// selection rule alone, over plain positions, for the bench's
        /// tie-break and boundary checks.</summary>
        public static int SelectIndex(Vector3 origin, Vector3 forward,
                                      IList<Vector3> points, float halfAngleDeg,
                                      float range)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float dist = Vector3.Distance(origin, points[i]);
                if (dist >= bestDist) continue;
                if (!InCone(origin, forward, points[i], halfAngleDeg, range)) continue;
                best = i;
                bestDist = dist;
            }
            return best;
        }
    }
}
