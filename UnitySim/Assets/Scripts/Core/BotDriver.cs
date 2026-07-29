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
    /// The lateral line has two layers, both scaled to the local road width.
    /// A racing-line term runs each corner out-in-out — wide on approach, cut
    /// to the inside at the apex, released wide on exit — with Hard bots using
    /// most of the corridor and Easy bots only half-heartedly. On straights a
    /// per-bot weave (a constant bias plus a slow sine, faded out as corners
    /// approach) spreads the pack across the track so it doesn't run
    /// nose-to-tail on rails. If a bot gets wedged it reverses to free itself,
    /// and only respawns as a last resort.
    /// </summary>
    public sealed class BotDriver : IDriverInputSource
    {
        private struct Params
        {
            public float baseSpeed;       // target straight-line speed (m/s)
            public float lookAhead;       // pure-pursuit distance (m)
            public float cornerCaution;   // higher = slows more for upcoming curvature
            public float lockDeg;         // heading error (deg) that maps to full steer lock
            // Racing line + spread personality. All lateral numbers are
            // FRACTIONS of the local usable half-width, never metres — that is
            // what keeps a bot honest on a 2.2 m bridge and lively on a 4.4 m
            // straight with one set of numbers.
            public float lineGain;        // how much of the corridor the out-in-out line uses
            public float weaveAmpFrac;    // straightaway weave amplitude ceiling
            public float weaveBiasFrac;   // constant inside/outside bias range
            public float anticipationSec; // corner look-ahead horizon (seconds of travel)
        }

        private static Params ForDifficulty(BotDifficulty d)
        {
            switch (d)
            {
                case BotDifficulty.Easy:
                    return new Params { baseSpeed = 5.5f, lookAhead = 1.6f, cornerCaution = 3.2f, lockDeg = 32f,
                        lineGain = 0.50f, weaveAmpFrac = 0.45f, weaveBiasFrac = 0.30f, anticipationSec = 0.55f };
                case BotDifficulty.Hard:
                    return new Params { baseSpeed = 9.5f, lookAhead = 1.1f, cornerCaution = 2.2f, lockDeg = 20f,
                        lineGain = 0.92f, weaveAmpFrac = 0.12f, weaveBiasFrac = 0.08f, anticipationSec = 1.05f };
                default: // Medium
                    return new Params { baseSpeed = 7.5f, lookAhead = 1.3f, cornerCaution = 2.6f, lockDeg = 26f,
                        lineGain = 0.75f, weaveAmpFrac = 0.28f, weaveBiasFrac = 0.20f, anticipationSec = 0.80f };
            }
        }

        private readonly CarVehicle _car;
        private readonly List<Vector3> _path;
        private readonly float[] _cum;   // cumulative arc length at each path point
        private readonly bool _closed;
        private readonly Params _p;

        // Track annotations, precomputed once in the constructor.
        private readonly float[] _half;   // half road width per node (m)
        private readonly float[] _kappa;  // signed curvature per node (rad/m, + = right turn)
        private readonly float _totalLen; // lap length incl. the closing segment when closed

        /// <summary>Half width assumed when the caller has none (the old fixed
        /// ±0.9 m clamp plus margins, in feel).</summary>
        private const float DefaultHalfWidth = 1.1f;
        /// <summary>Half a car, reserved out of the corridor.</summary>
        private const float CarHalfWidth = 0.20f;
        /// <summary>Extra corridor margin — track-limit penalties need all four
        /// wheels off, so this plus <see cref="CarHalfWidth"/> keeps the whole
        /// spread penalty-safe by construction.</summary>
        private const float EdgeMargin = 0.30f;
        /// <summary>Curvature at which a corner counts as fully a corner
        /// (≈ 5.5 m radius). Divides into the saturation terms below.</summary>
        private const float KappaRef = 0.18f;

        // Per-bot line personality, as FRACTIONS of the usable corridor.
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

        // Smoke-cloud blindness, pushed in by ArcadeDirector.
        private float _blindLeft, _blindSteerRelease = 0.5f, _blindThrottle = 0.35f;

        // Arena mode: a point to drive at instead of a line to drive along.
        private Vector3 _chaseAt;
        private float _chaseUntil = -1f;
        private float _chaseSpeed = 1f;

        /// <summary>
        /// Drive at a world point rather than along the racing line, until
        /// <paramref name="holdSeconds"/> elapses without another call.
        ///
        /// A settable seam in the <see cref="SetBlind"/> style, never a
        /// constructor argument: an arena has no line, and the object that knows
        /// where the ball or the flag is (the mode's director) lives in a layer
        /// Core is not allowed to depend on. So the target arrives as an
        /// argument and the bot stays ignorant of what it is chasing.
        /// </summary>
        public void SetChaseTarget(Vector3 worldPos, float speedScale = 1f, float holdSeconds = 0.5f)
        {
            _chaseAt = worldPos;
            _chaseSpeed = Mathf.Clamp(speedScale, 0.2f, 1.6f);
            _chaseUntil = Time.time + holdSeconds;
        }

        /// <summary>Stop chasing and go back to the line (if there is one).</summary>
        public void ClearChaseTarget() => _chaseUntil = -1f;

        private bool Chasing => Time.time <= _chaseUntil;

        /// <summary>
        /// This bot is inside (or has just left) a smoke cloud: for the next
        /// <paramref name="secondsLeft"/> it drives straight instead of following
        /// the racing line.
        ///
        /// A settable seam in the <see cref="SpeedScale"/> style, never a
        /// constructor argument — <c>MenuAttract</c> builds a BotDriver too and
        /// must keep compiling. The tuning arrives as arguments rather than being
        /// read from <c>ArcadeConfig</c> because Arcade depends on Core and not
        /// the other way round; pushing the numbers in is what keeps that
        /// one-directional.
        /// </summary>
        public void SetBlind(float secondsLeft, float steerRelease, float throttle)
        {
            _blindLeft = secondsLeft;
            _blindSteerRelease = steerRelease;
            _blindThrottle = throttle;
        }

        /// <summary><paramref name="halfWidths"/> is the per-node half road
        /// width from <see cref="BotPath"/>'s widths overload; null (the
        /// MenuAttract path and any older caller) falls back to
        /// <see cref="DefaultHalfWidth"/> everywhere.</summary>
        public BotDriver(CarVehicle car, IReadOnlyList<Vector3> path, bool closed,
            BotDifficulty diff, IReadOnlyList<float> halfWidths = null)
        {
            _car = car;
            _path = path != null ? new List<Vector3>(path) : new List<Vector3>();
            _closed = closed && _path.Count >= 3;
            _p = ForDifficulty(diff);
            int n = _path.Count;

            // Cumulative arc length (drives the weave phase along the lap).
            _cum = new float[n];
            for (int i = 1; i < n; i++)
                _cum[i] = _cum[i - 1] + Vector3.Distance(_path[i - 1], _path[i]);
            _totalLen = n >= 2
                ? _cum[n - 1] + (_closed ? Vector3.Distance(_path[n - 1], _path[0]) : 0f)
                : 0f;

            _half = new float[n];
            for (int i = 0; i < n; i++)
            {
                float w = halfWidths != null && i < halfWidths.Count
                    ? halfWidths[i] : DefaultHalfWidth;
                _half[i] = Mathf.Clamp(w, 0.3f, 5f);
            }

            // Signed curvature per node from the turn angle between adjacent
            // segments over the local arc length. Sign convention: positive =
            // right turn, matching right = Cross(up, tangent) in Compute, so a
            // positive offset is toward the right edge — which for a right
            // turn is the inside. Raw per-node angles on dense spline samples
            // are noisy, so a ±2-node box smooth follows.
            var raw = new float[n];
            for (int i = 0; i < n; i++)
            {
                int prev = _closed ? (i + n - 1) % n : Mathf.Max(0, i - 1);
                int next = _closed ? (i + 1) % n : Mathf.Min(n - 1, i + 1);
                if (prev == i || next == i) continue;   // open-path endpoints, patched below
                Vector3 a = Flat(_path[i] - _path[prev]);
                Vector3 b = Flat(_path[next] - _path[i]);
                if (a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f) continue;
                raw[i] = Vector3.SignedAngle(a, b, Vector3.up) * Mathf.Deg2Rad /
                    Mathf.Max(0.05f, 0.5f * (a.magnitude + b.magnitude));
            }
            if (!_closed && n >= 3) { raw[0] = raw[1]; raw[n - 1] = raw[n - 2]; }

            _kappa = new float[n];
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int count = 0;
                for (int j = -2; j <= 2; j++)
                {
                    int k = _closed ? (i + j + n) % n : i + j;
                    if (k < 0 || k >= n) continue;
                    sum += raw[k];
                    count++;
                }
                _kappa[i] = count > 0 ? sum / count : 0f;
            }

            // A distinct line per bot (constructed sequentially, so each differs).
            // Fractional, scaled by the local corridor at apply time.
            _offBias = Random.Range(-1f, 1f) * _p.weaveBiasFrac;
            _offAmp = Random.Range(0.5f, 1f) * _p.weaveAmpFrac;
            _offFreq = Random.Range(0.05f, 0.12f);
            _offPhase = Random.Range(0f, Mathf.PI * 2f);
        }

        // --- IDriverInputSource (poll the cached decision) ---

        public float Throttle() { EnsureFresh(); return _throttle; }
        public float Steer() { EnsureFresh(); return _steer; }
        public float Brake() { EnsureFresh(); return _brake; }
        public bool Handbrake() => false;
        public bool LookBackHeld() => false;      // no camera to swing
        public bool HornHeld() => false;          // bots have nothing to say
        public float MouseSteerDelta() => 0f;

        /// <summary>Boost is a mode decision, not a driving one — the soccer
        /// director latches it the same way ArcadeDirector latches item use.</summary>
        public bool BoostRequested;

        public bool BoostHeld() => BoostRequested;

        /// <summary>Latched by the mode's bot policy, consumed on the next
        /// poll — the RequestUseItem shape, for the same reason: a bot knows
        /// nothing about the ball, and the director that does is not allowed to
        /// reach into the driver's decision.</summary>
        public void RequestJump() => _jumpLatch = true;

        private bool _jumpLatch;

        public bool JumpPressed()
        {
            if (!_jumpLatch) return false;
            _jumpLatch = false;
            return true;
        }

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
            if (_car == null)
            {
                _throttle = _steer = _brake = 0f;
                return;
            }

            // Arena mode. Taken BEFORE the path check, because an arena has no
            // path at all and the pure-pursuit core below needs one; what it
            // does not need is a line, which is why the target is enough.
            if (Chasing)
            {
                ComputeChase(dt);
                return;
            }

            if (_path.Count < 2)
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

            // Blinded by a smoke cloud. Returns early on purpose, so the whole
            // pure-pursuit block below is skipped — a bot that cannot see must
            // not be following a racing line, which is the entire point of the
            // item.
            if (_blindLeft > 0f)
            {
                // Ease the wheel back to centre rather than snapping to zero
                // (which reads as a magic correction) or latching the last steer
                // (which makes a bot blinded mid-corner carve a perfect circle).
                _steer = Mathf.MoveTowards(_steer, 0f, dt / Mathf.Max(0.01f, _blindSteerRelease));
                _throttle = _blindThrottle;
                _brake = 0f;
                // The same resets the Frozen branch does, plus the respawn latch:
                // without that last one a blinded bot that noses a wall escalates
                // to a respawn and teleports itself clear of the cloud — a free
                // escape, and a teleport nobody asked for.
                _stuckTimer = 0f;
                _reversing = false;
                _reverseTimer = 0f;
                _respawnLatch = false;
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

            // Shift the aim point off the centerline: a racing-line term that
            // runs the corner out-in-out, plus this bot's personal weave on the
            // straights. Everything below is scaled by the LOCAL corridor, so a
            // narrow bridge collapses the whole spread toward centre.
            Vector3 tangent = Flat(AdvanceAlong(near, _p.lookAhead + 0.6f) - aimCenter);
            if (tangent.sqrMagnitude < 1e-5f) tangent = dNear;
            Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;

            // Continuous arc position — the old quantized _cum[near] stepped
            // the weave phase node to node instead of gliding.
            float s = ArcPos(pos, near);

            float usable = Mathf.Max(0f, HalfAt(s) - CarHalfWidth - EdgeMargin);
            float lookM = Mathf.Max(2f, v * _p.anticipationSec);
            float kHere = KappaAt(s);
            float kAhead = KappaAt(s + lookM);
            float cHere = Mathf.Min(1f, Mathf.Abs(kHere) / KappaRef);
            float cAhead = Mathf.Min(1f, Mathf.Abs(kAhead) / KappaRef);

            // Out-in-out from the sign arithmetic alone: on the approach
            // (cHere≈0) the -sign(kAhead) term pulls to the OUTSIDE of the
            // corner coming up; as cHere rises it hands over continuously to
            // +sign(kHere), the INSIDE of the corner being driven; an S-curve
            // flips kAhead's sign early and the crossover falls out for free.
            float line01 = cHere * Mathf.Sign(kHere)
                         - (1f - cHere) * cAhead * Mathf.Sign(kAhead);
            float offRacing = _p.lineGain * Mathf.Clamp(line01, -1f, 1f) * usable;

            // The personality weave survives on straights only — a corner is
            // driven on the line, which is the whole "arcade but competent" ask.
            float weaveGate = 1f - Mathf.Max(cHere, cAhead);
            float offWeave = (_offBias + _offAmp * Mathf.Sin(s * _offFreq + _offPhase))
                             * usable * weaveGate;

            float off = Mathf.Clamp(offRacing + offWeave, -usable, usable);
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

        /// <summary>Continuous arc-length position: project onto the segment
        /// leaving the nearest node instead of snapping to the node.</summary>
        private float ArcPos(Vector3 pos, int near)
        {
            int n = _path.Count;
            int nxt = near + 1 < n ? near + 1 : (_closed ? 0 : near);
            if (nxt == near) return _cum[near];
            Vector3 seg = _path[nxt] - _path[near];
            float t = Mathf.Clamp01(Vector3.Dot(pos - _path[near], seg) /
                Mathf.Max(1e-4f, seg.sqrMagnitude));
            return _cum[near] + t * seg.magnitude;
        }

        private float KappaAt(float s) => TableAt(_kappa, s);
        private float HalfAt(float s) => TableAt(_half, s);

        /// <summary>Lerped lookup of a per-node table by arc position, wrapping
        /// on a closed lap (so "curvature 6 m ahead" works across the line).</summary>
        private float TableAt(float[] table, float s)
        {
            int n = _path.Count;
            if (n == 0) return 0f;
            if (n == 1) return table[0];
            if (_closed && _totalLen > 0.01f) s = Mathf.Repeat(s, _totalLen);
            else s = Mathf.Clamp(s, 0f, _cum[n - 1]);

            // Greatest node with _cum <= s.
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_cum[mid] <= s) lo = mid; else hi = mid - 1;
            }
            int nxt = lo + 1 < n ? lo + 1 : 0;                     // closing segment wraps to 0
            float segLen = (lo + 1 < n ? _cum[lo + 1] : _totalLen) - _cum[lo];
            float t = segLen > 1e-4f ? (s - _cum[lo]) / segLen : 0f;
            return Mathf.Lerp(table[lo], table[nxt], Mathf.Clamp01(t));
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

        // --- Arena chase ------------------------------------------------------

        /// <summary>Whisker length, in metres, for the wall check.</summary>
        private const float WhiskerLen = 1.1f;

        /// <summary>
        /// Pure pursuit to a point, with whiskers instead of a corridor.
        ///
        /// The steering core is the same one the racing line uses — aim, signed
        /// angle, clamp — because that part was never about the line. What
        /// changes is where the aim comes from and how the car avoids scenery:
        /// an arena has walls in arbitrary places, so three raycasts ahead say
        /// which way is clear and bend the aim rather than a precomputed
        /// corridor half-width.
        /// </summary>
        private void ComputeChase(float dt)
        {
            if (_car.Frozen)
            {
                _throttle = _steer = _brake = 0f;
                _stuckTimer = 0f;
                return;
            }

            Vector3 pos = _car.transform.position;
            Vector3 fwd = Flat(_car.transform.forward);
            Vector3 toTarget = Flat(_chaseAt - pos);
            float dist = toTarget.magnitude;

            float steer = 0f;
            if (dist > 0.05f && fwd.sqrMagnitude > 1e-5f)
            {
                float signed = Vector3.SignedAngle(fwd, toTarget, Vector3.up);
                steer = Mathf.Clamp(signed / _p.lockDeg, -1f, 1f);
            }

            // Whiskers: nose, and 35 degrees either side. A blocked side pushes
            // the wheel the other way, proportional to how close the wall is.
            float avoid = 0f;
            avoid -= Probe(pos, Quaternion.Euler(0f, -35f, 0f) * fwd);
            avoid += Probe(pos, Quaternion.Euler(0f, 35f, 0f) * fwd);
            float ahead = Probe(pos, fwd);
            if (ahead > 0.6f && Mathf.Abs(avoid) < 0.05f)
                avoid = 1f;   // wall dead ahead and both sides equal: commit one way
            steer = Mathf.Clamp(steer + avoid, -1f, 1f);

            // Speed: full tilt at range, easing in as the target gets close so a
            // bot does not simply shunt straight past whatever it is chasing.
            float target = _p.baseSpeed * _chaseSpeed * Mathf.Clamp01(0.35f + dist * 0.35f);
            float err = target - _car.ForwardSpeed;
            _throttle = Mathf.Clamp01(err * 0.8f);
            _brake = err < -1.5f ? Mathf.Clamp01(-err * 0.25f) : 0f;
            // A hard turn wants less throttle, or the car understeers past.
            _throttle *= Mathf.Lerp(1f, 0.55f, Mathf.Abs(steer));
            _steer = steer;

            StuckRecovery(dt);
        }

        /// <summary>0 (clear) to 1 (about to hit) along a direction.</summary>
        private float Probe(Vector3 from, Vector3 dir)
        {
            var origin = from + Vector3.up * 0.05f;
            if (!Physics.Raycast(origin, dir.normalized, out var hit, WhiskerLen,
                    ~0, QueryTriggerInteraction.Ignore))
                return 0f;
            if (hit.collider.GetComponentInParent<CarVehicle>() != null) return 0f;
            return 1f - Mathf.Clamp01(hit.distance / WhiskerLen);
        }

        /// <summary>Wedged-against-a-wall recovery, the arena's cut-down version
        /// of the racing-line one: back up rather than respawn, because in a
        /// derby a free teleport out of a corner is worth more than the corner.</summary>
        private void StuckRecovery(float dt)
        {
            bool slow = Mathf.Abs(_car.ForwardSpeed) < 0.35f && _throttle > 0.2f;
            _stuckTimer = slow ? _stuckTimer + dt : 0f;

            if (_reversing)
            {
                _reverseTimer -= dt;
                _throttle = -0.7f;
                _steer = -_steer;
                _brake = 0f;
                if (_reverseTimer <= 0f) { _reversing = false; _stuckTimer = 0f; }
                return;
            }
            if (_stuckTimer < 1.2f) return;
            _reversing = true;
            _reverseTimer = 0.8f;
            _stuckTimer = 0f;
        }

    }
}
