using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    public enum BotDifficulty { Easy = 0, Medium = 1, Hard = 2 }

    /// <summary>
    /// Bot AI that drives a car around a fixed ordered path (the track's racing
    /// line — see <see cref="BotPath"/>) with pure-pursuit steering and
    /// corner-aware speed. Implements the same <see cref="IDriverInputSource"/>
    /// seam humans use, so it plugs straight into <c>CarInput.source</c> — no
    /// physics or control-loop changes. Opponents and the player's
    /// "autonomous (bot AI)" option both use it.
    ///
    /// Each bot holds a slightly different line (a constant lateral bias plus a
    /// slow sine weave, perpendicular to the racing line) so a pack doesn't run
    /// nose-to-tail on rails. If it gets wedged it reverses to free itself, and
    /// only respawns as a last resort.
    /// </summary>
    public sealed class BotDriver : IDriverInputSource
    {
        private struct Params
        {
            public float baseSpeed;     // target straight-line speed (m/s)
            public float lookAhead;     // pure-pursuit distance (m)
            public float cornerCaution; // higher = slows more for upcoming curvature
            public float lockDeg;       // heading error (deg) that maps to full steer lock
        }

        private static Params ForDifficulty(BotDifficulty d)
        {
            switch (d)
            {
                case BotDifficulty.Easy:
                    return new Params { baseSpeed = 5.5f, lookAhead = 1.6f, cornerCaution = 3.2f, lockDeg = 32f };
                case BotDifficulty.Hard:
                    return new Params { baseSpeed = 9.5f, lookAhead = 1.1f, cornerCaution = 2.2f, lockDeg = 20f };
                default: // Medium
                    return new Params { baseSpeed = 7.5f, lookAhead = 1.3f, cornerCaution = 2.6f, lockDeg = 26f };
            }
        }

        private readonly CarVehicle _car;
        private readonly List<Vector3> _path;
        private readonly float[] _cum;   // cumulative arc length at each path point
        private readonly bool _closed;
        private readonly Params _p;

        // Per-bot line personality (its own offset from the racing line).
        private const float MaxOffset = 0.9f;   // metres, kept well inside the ribbon
        private readonly float _offBias;        // constant inside/outside bias
        private readonly float _offAmp;         // weave amplitude
        private readonly float _offFreq;        // weave rate (rad per metre of track)
        private readonly float _offPhase;

        // Cached per-frame decision (computed once per Time.frameCount).
        private int _lastFrame = -1;
        private float _throttle, _steer, _brake;

        // Recovery: reverse a couple of times to free a wedged car before respawning.
        private float _stuckTimer;
        private bool _reversing;
        private float _reverseTimer;
        private int _reverseCount;
        private bool _respawnLatch;
        private bool _useItemLatch;

        /// <summary>Rubber-band multiplier on target speed (1 = none). Set by the RaceDirector.</summary>
        public float SpeedScale = 1f;

        public BotDriver(CarVehicle car, IReadOnlyList<Vector3> path, bool closed, BotDifficulty diff)
        {
            _car = car;
            _path = path != null ? new List<Vector3>(path) : new List<Vector3>();
            _closed = closed && _path.Count >= 3;
            _p = ForDifficulty(diff);

            // Cumulative arc length (drives the weave phase along the lap).
            _cum = new float[_path.Count];
            for (int i = 1; i < _path.Count; i++)
                _cum[i] = _cum[i - 1] + Vector3.Distance(_path[i - 1], _path[i]);

            // A distinct line per bot (constructed sequentially, so each differs).
            _offBias = Random.Range(-0.35f, 0.35f);
            _offAmp = Random.Range(0.25f, 0.7f);
            _offFreq = Random.Range(0.05f, 0.12f);
            _offPhase = Random.Range(0f, Mathf.PI * 2f);
        }

        // --- IDriverInputSource (poll the cached decision) ---

        public float Throttle() { EnsureFresh(); return _throttle; }
        public float Steer() { EnsureFresh(); return _steer; }
        public float Brake() { EnsureFresh(); return _brake; }
        public bool Handbrake() => false;
        public float MouseSteerDelta() => 0f;

        /// <summary>
        /// Arcade: fire the held item on the next poll. The DECISION is not made
        /// here — a bot knows nothing about items or the rest of the field, so
        /// ArcadeDirector (which sees both) sets this latch and the driver simply
        /// reports it, the same shape as the stuck-recovery respawn latch.
        /// </summary>
        public void RequestUseItem() => _useItemLatch = true;

        public bool UseItemPressed()
        {
            if (!_useItemLatch) return false;
            _useItemLatch = false;
            return true;
        }

        public bool RespawnPressed()
        {
            EnsureFresh();
            if (!_respawnLatch) return false;
            _respawnLatch = false;
            // Fresh start after a teleport — clear the recovery state.
            _reversing = false;
            _reverseTimer = 0f;
            _reverseCount = 0;
            _stuckTimer = 0f;
            return true;
        }

        // --- Decision ---

        private void EnsureFresh()
        {
            if (_lastFrame == Time.frameCount) return;
            _lastFrame = Time.frameCount;
            Compute(Mathf.Min(Time.deltaTime, 0.1f));
        }

        private void Compute(float dt)
        {
            if (_car == null || _path.Count < 2)
            {
                _throttle = _steer = _brake = 0f;
                return;
            }

            // Held by the race countdown: output nothing and don't mistake the
            // enforced standstill for being stuck.
            if (_car.Frozen)
            {
                _throttle = _steer = _brake = 0f;
                _stuckTimer = 0f;
                _reversing = false;
                _reverseTimer = 0f;
                return;
            }

            Vector3 pos = _car.transform.position;
            float v = _car.ForwardSpeed;
            int near = NearestIndex(pos);

            // Centerline look-ahead + upcoming curvature (measured on the line).
            Vector3 aimCenter = AdvanceAlong(near, _p.lookAhead);
            Vector3 aheadB = AdvanceAlong(near, _p.lookAhead + 1.2f);
            float curv = 0f;
            Vector3 dNear = Flat(aimCenter - pos);
            Vector3 dFar = Flat(aheadB - aimCenter);
            if (dNear.sqrMagnitude > 1e-4f && dFar.sqrMagnitude > 1e-4f)
                curv = Mathf.Clamp01(Vector3.Angle(dNear, dFar) / 60f);

            // Shift the aim point off the centerline by this bot's personal line,
            // easing back toward the line in tight corners so it never cuts wide.
            Vector3 tangent = Flat(AdvanceAlong(near, _p.lookAhead + 0.6f) - aimCenter);
            if (tangent.sqrMagnitude < 1e-5f) tangent = dNear;
            Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
            float off = _offBias + _offAmp * Mathf.Sin(_cum[near] * _offFreq + _offPhase);
            off *= 1f - 0.5f * curv;
            off = Mathf.Clamp(off, -MaxOffset, MaxOffset);
            Vector3 aim = aimCenter + right * off;

            // Pure-pursuit steering toward the (offset) aim point.
            Vector3 fwd = Flat(_car.transform.forward);
            Vector3 toAim = Flat(aim - pos);
            float driveSteer = 0f;
            if (toAim.sqrMagnitude > 1e-5f && fwd.sqrMagnitude > 1e-5f)
            {
                float signed = Vector3.SignedAngle(fwd, toAim, Vector3.up);
                driveSteer = Mathf.Clamp(signed / _p.lockDeg, -1f, 1f);
            }

            // Corner-aware target speed → throttle / brake intent.
            float target = _p.baseSpeed * Mathf.Max(0.1f, SpeedScale) / (1f + _p.cornerCaution * curv);
            float err = target - v;
            float driveThrottle, driveBrake;
            if (err > 0.2f) { driveThrottle = Mathf.Clamp01(err / 2f) * (1f - 0.3f * Mathf.Abs(driveSteer)); driveBrake = 0f; }
            else if (err < -0.5f) { driveThrottle = 0f; driveBrake = Mathf.Clamp01(-err / 3f); }
            else { driveThrottle = 0.1f; driveBrake = 0f; }

            // --- recovery state machine ---
            if (_reversing)
            {
                _reverseTimer -= dt;
                _throttle = -0.6f;                              // back straight/countersteered out
                _steer = Mathf.Clamp(-driveSteer * 1.2f, -1f, 1f);
                _brake = 0f;
                // Freed (moving again after a moment) or timed out → resume driving.
                bool freed = Mathf.Abs(v) > 0.8f && _reverseTimer < 1.1f;
                if (freed || _reverseTimer <= 0f)
                {
                    _reversing = false;
                    _stuckTimer = 0f;
                    if (freed) _reverseCount = 0; // recovered cleanly; reset escalation
                }
                return;
            }

            _throttle = driveThrottle;
            _steer = driveSteer;
            _brake = driveBrake;

            if (Mathf.Abs(v) > 2f) _reverseCount = 0; // driving fine — forget past wedges

            // Trying to go but pinned → escalate: reverse, reverse, then respawn.
            bool stuck = _throttle > 0.3f && Mathf.Abs(v) < 0.3f;
            if (stuck) _stuckTimer += dt;
            else _stuckTimer = Mathf.Max(0f, _stuckTimer - dt * 2f);

            if (_stuckTimer > 1.2f)
            {
                _stuckTimer = 0f;
                if (_reverseCount >= 2) { _respawnLatch = true; _reverseCount = 0; }
                else { _reversing = true; _reverseTimer = 1.5f; _reverseCount++; }
            }
        }

        /// <summary>Point <paramref name="dist"/> metres forward along the path from index i.</summary>
        private Vector3 AdvanceAlong(int i, float dist)
        {
            int n = _path.Count;
            float remaining = dist;
            int cur = i;
            for (int step = 0; step < n; step++)
            {
                int nxt = cur + 1;
                if (nxt >= n)
                {
                    if (_closed) nxt = 0;
                    else return _path[n - 1]; // open path: clamp at the end
                }
                float seg = Vector3.Distance(_path[cur], _path[nxt]);
                if (seg >= remaining || step == n - 1)
                {
                    float t = seg > 1e-4f ? remaining / seg : 0f;
                    return Vector3.Lerp(_path[cur], _path[nxt], Mathf.Clamp01(t));
                }
                remaining -= seg;
                cur = nxt;
            }
            return _path[cur];
        }

        private int NearestIndex(Vector3 p)
        {
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < _path.Count; i++)
            {
                float sq = (_path[i] - p).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    }
}
