/* =====================================================================
   tt-sim.js — the small numerical core behind the live plots.

   Two things live here:
     • A JS port of Controllers/common/pid.c, kept faithful (derivative on
       measurement, conditional-integration anti-windup) so what the lesson
       plots show is what the generated firmware will actually do.
     • A fixed-step longitudinal plant: ESC → DC motor → vehicle, using the
       same equations as MotorModel.WheelTorque + the drag polynomial the
       calibration procedure identifies.

   Plus StepMetrics (a port of Telemetry/StepMetrics.cs) so the browser and
   the game report the same rise/overshoot/settling numbers.
   ===================================================================== */
(function (global) {
    'use strict';

    function clampf(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

    // ---- PID (port of common/pid.c) --------------------------------------

    function Pid(kp, ki, kd, outMin, outMax) {
        this.kp = kp; this.ki = ki; this.kd = kd;
        this.out_min = outMin; this.out_max = outMax;
        this.antiWindup = true;      // lesson toggle; the C is always true
        this.reset();
    }
    Pid.prototype.reset = function () {
        this.integrator = 0;
        this.prev_measurement = 0;
        this.initialized = false;
        this.p_term = this.i_term = this.d_term = 0;
    };
    Pid.prototype.update = function (setpoint, measurement, dt) {
        if (dt <= 0) return clampf(this.p_term + this.i_term + this.d_term, this.out_min, this.out_max);

        const error = setpoint - measurement;
        if (!this.initialized) { this.prev_measurement = measurement; this.initialized = true; }

        this.p_term = this.kp * error;

        // Derivative on measurement: a setpoint step must not kick the output.
        const dmeas = (measurement - this.prev_measurement) / dt;
        this.d_term = -this.kd * dmeas;
        this.prev_measurement = measurement;

        const integratorNext = this.integrator + this.ki * error * dt;
        this.i_term = integratorNext;

        let output = this.p_term + this.i_term + this.d_term;

        // Conditional integration: commit the integrator step only if we are
        // not saturated, or if the step unwinds us back toward range.
        const outputClamped = clampf(output, this.out_min, this.out_max);
        const saturated = output !== outputClamped;
        const intoSaturation =
            (output > this.out_max && error > 0) ||
            (output < this.out_min && error < 0);

        if (!this.antiWindup || !(saturated && intoSaturation)) {
            this.integrator = integratorNext;
        } else {
            this.i_term = this.integrator;
            output = this.p_term + this.i_term + this.d_term;
        }
        return clampf(output, this.out_min, this.out_max);
    };

    // ---- ESC front-end (MotorPart.StepDrive) ------------------------------

    function Esc(p) {
        this.p = p || {};
        this.reset();
    }
    Esc.prototype.reset = function () { this.vFilt = 0; this.vPrev = 0; };
    /* Deadband → PWM quantize → slew → first-order lag, in that order —
       the same pipeline the sim runs before the DC model sees a voltage. */
    Esc.prototype.step = function (vCmd, dt) {
        const p = this.p;
        let v = vCmd;
        const maxV = Math.max(0.01, p.maxVoltage || 7.4);

        if (p.escDeadbandV > 0 && Math.abs(v) < p.escDeadbandV) v = 0;
        if (p.escPwmSteps > 0) {
            const steps = p.escPwmSteps;
            v = Math.round(v / maxV * steps) / steps * maxV;
        }
        if (p.escSlewVPerS > 0) {
            const maxStep = p.escSlewVPerS * dt;
            v = clampf(v, this.vPrev - maxStep, this.vPrev + maxStep);
        }
        this.vPrev = v;
        if (p.escTimeConstMs > 0) {
            const a = 1 - Math.exp(-dt * 1000 / p.escTimeConstMs);
            this.vFilt += (v - this.vFilt) * a;
        } else {
            this.vFilt = v;
        }
        return clampf(this.vFilt, -maxV, maxV);
    };

    // ---- Longitudinal plant ----------------------------------------------

    /* One powered-axle vehicle in a straight line.

       Per step, per motor:
           ω_motor = (v/r)·gear
           I       = clamp((V − kt·ω_motor)/R, ±min(V_rail/R, I_esc))
           τ       = (kt·I − b·ω_motor − Tc·sign ω)·gear·η
           F       = τ/r
       Then  m_eff·dv/dt = ΣF − drag(v),  drag = c0 + c1·v + c2·v²
       (signed against motion; c0 is the rolling/Coulomb term the coast-down
       calibration identifies — it is NOT aero-only).

       m_eff carries the reflected rotor inertia, which at RC scale is a
       fifth of the apparent mass — ignoring it makes every feed-forward
       gain wrong. */
    function Plant(cfg) {
        this.cfg = Object.assign({
            kt: 0.003, resistance: 0.09, gearRatio: 8, wheelRadius: 0.033,
            efficiency: 0.85, noLoadCurrent: 1.2, coulombScale: 1,
            viscousDamping: 1e-6, maxCurrent: 40, maxVoltage: 7.4,
            rotorInertia: 5e-6, motorCount: 2, mass: 1.8,
            // Measured coast-down polynomial from the reference car (i22
            // calibration): c0 is the Coulomb/rolling term, c2 the analytic
            // aero, c1 the remainder. ≈2.9 N total at 4.5 m/s.
            dragC0: 0.90, dragC1: 0.38, dragC2: 0.015,
            tractionEff: 0.99,
            escDeadbandV: 0.10, escPwmSteps: 1024, escTimeConstMs: 5, escSlewVPerS: 0
        }, cfg || {});
        this.esc = new Esc(this.cfg);
        this.reset();
    }
    Plant.prototype.reset = function (v0) {
        this.v = v0 || 0;
        this.esc.reset();
        this.lastCurrent = 0;
        this.lastForce = 0;
        this.lastVoltage = 0;
    };
    Plant.prototype.massEff = function () {
        const c = this.cfg;
        return c.mass + c.motorCount * c.rotorInertia * c.gearRatio * c.gearRatio / (c.wheelRadius * c.wheelRadius);
    };
    Plant.prototype.drag = function (v) {
        const c = this.cfg;
        const s = Math.sign(v) || 1;
        return s * (c.dragC0 + c.dragC1 * Math.abs(v) + c.dragC2 * v * v);
    };
    /* Advance one step with a commanded voltage (pre-ESC). Returns the
       operating point so the plots can show current/force, not just speed. */
    Plant.prototype.step = function (vCmd, dt) {
        const c = this.cfg;
        const v = this.esc.step(vCmd, dt);
        this.lastVoltage = v;

        const omegaWheel = this.v / Math.max(1e-3, c.wheelRadius);
        const omegaMotor = omegaWheel * c.gearRatio;
        let stall = Math.max(0.01, c.maxVoltage) / Math.max(1e-4, c.resistance);
        if (c.maxCurrent > 0) stall = Math.min(stall, c.maxCurrent);
        const current = clampf((v - c.kt * omegaMotor) / Math.max(1e-4, c.resistance), -stall, stall);

        const tc = Math.max(0, c.coulombScale) * c.kt * Math.max(0, c.noLoadCurrent);
        let tauMotor;
        if (Math.abs(omegaMotor) > 0.5) {
            tauMotor = c.kt * current - c.viscousDamping * omegaMotor - tc * Math.sign(omegaMotor);
        } else {
            const net = c.kt * current - c.viscousDamping * omegaMotor;
            tauMotor = Math.sign(net) * Math.max(0, Math.abs(net) - tc);
        }
        const eff = c.efficiency > 0 ? Math.min(1, c.efficiency) : 1;
        const forceEach = tauMotor * c.gearRatio * eff / Math.max(1e-3, c.wheelRadius);
        const force = forceEach * c.motorCount * (c.tractionEff > 0 ? c.tractionEff : 1);

        const accel = (force - this.drag(this.v)) / this.massEff();
        this.v += accel * dt;
        if (Math.abs(this.v) < 1e-4 && Math.abs(force) < Math.abs(this.drag(1e-3))) this.v = 0;

        this.lastCurrent = current;
        this.lastForce = force;
        return { v: this.v, accel: accel, current: current, voltage: v, force: force };
    };

    // ---- Kinematic bicycle (steering lessons) -----------------------------

    /* ψ̇ = v·tan(δ)/L — the feed-forward the lateral loop is built on.
       `understeerGrad` fakes the tyre compliance the kinematic model
       ignores, so the lesson can show why the yaw PID is needed. */
    function Bicycle(cfg) {
        this.cfg = Object.assign({ wheelbase: 0.30, understeerGrad: 0.0, servoRateDegPerSec: 480, maxSteerDeg: 28 }, cfg || {});
        this.reset();
    }
    Bicycle.prototype.reset = function () { this.yaw = 0; this.yawRate = 0; this.steerDeg = 0; };
    Bicycle.prototype.step = function (steerCmdDeg, speed, dt) {
        const c = this.cfg;
        // Servo slew: the commanded angle is a target, not an instant pose.
        const maxStep = c.servoRateDegPerSec * dt;
        const target = clampf(steerCmdDeg, -c.maxSteerDeg, c.maxSteerDeg);
        this.steerDeg += clampf(target - this.steerDeg, -maxStep, maxStep);

        const delta = this.steerDeg * Math.PI / 180;
        const kinematic = speed * Math.tan(delta) / Math.max(1e-3, c.wheelbase);
        // Steady-state understeer: ψ̇ = v·δ / (L + K·v²)
        const withUnder = speed * Math.tan(delta) / Math.max(1e-3, c.wheelbase + c.understeerGrad * speed * speed);
        this.yawRate = c.understeerGrad > 0 ? withUnder : kinematic;
        this.yaw += this.yawRate * dt;
        return { yawRate: this.yawRate, yaw: this.yaw, steerDeg: this.steerDeg };
    };

    // ---- Transport delay -------------------------------------------------

    /* Models the sim's actuation delay + sensor latency as an N-tick FIFO —
       the single most common reason a sim-tuned gain oscillates on hardware. */
    function Delay(ticks) { this.set(ticks); }
    Delay.prototype.set = function (ticks) {
        this.n = Math.max(0, ticks | 0);
        this.buf = new Array(this.n + 1).fill(0);
        this.i = 0;
    };
    Delay.prototype.push = function (v) {
        if (this.n === 0) return v;
        const out = this.buf[this.i];
        this.buf[this.i] = v;
        this.i = (this.i + 1) % this.buf.length;
        return out;
    };

    // ---- Step metrics (port of Telemetry/StepMetrics.cs) -------------------

    /* Given aligned arrays (time, setpoint, measurement), find the most
       recent clean setpoint step and measure the response exactly the way
       the in-game overlay and the CSV sidecar do. */
    function stepMetrics(t, sp, ms) {
        const r = { found: false, riseTime: -1, settlingTime: -1, overshootPct: 0, peakTime: 0, ssError: 0, stepTime: 0, initial: 0, target: 0 };
        const n = Math.min(t.length, Math.min(sp.length, ms.length));
        if (n < 32) return r;

        let lo = Infinity, hi = -Infinity;
        for (let i = 0; i < n; i++) { const v = sp[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
        const span = hi - lo;
        if (span < 1e-4) return r;
        const thresh = span * 0.10;

        let edge = -1;
        for (let i = n - 1; i >= 1; i--) {
            if (Math.abs(sp[i] - sp[i - 1]) < thresh) continue;
            const before = sp[i - 1], tEdge = t[i];
            let flat = true;
            for (let j = i - 1; j >= 0 && tEdge - t[j] <= 0.5; j--) {
                if (Math.abs(sp[j] - before) > thresh * 0.2) { flat = false; break; }
            }
            if (flat) { edge = i; break; }
        }
        if (edge < 1 || edge > n - 8) return r;

        r.found = true;
        r.stepTime = t[edge];
        r.initial = ms[edge - 1];
        r.target = sp[edge];
        const delta = r.target - r.initial;
        if (Math.abs(delta) < 1e-4) { r.found = false; return r; }
        const sign = Math.sign(delta);

        let t10 = -1, t90 = -1, peak = r.initial, peakT = r.stepTime, lastOut = -1;
        for (let i = edge; i < n; i++) {
            const prog = (ms[i] - r.initial) * sign;
            if (t10 < 0 && prog >= 0.1 * Math.abs(delta)) t10 = t[i];
            if (t90 < 0 && prog >= 0.9 * Math.abs(delta)) t90 = t[i];
            if ((ms[i] - peak) * sign > 0) { peak = ms[i]; peakT = t[i]; }
            if (Math.abs(ms[i] - r.target) > 0.05 * Math.abs(delta)) lastOut = i;
        }
        if (t10 >= 0 && t90 >= 0) r.riseTime = t90 - t10;
        r.overshootPct = Math.max(0, (peak - r.target) * sign / Math.abs(delta) * 100);
        r.peakTime = peakT - r.stepTime;
        r.settlingTime = lastOut < 0 ? 0 : (lastOut >= n - 1 ? -1 : t[lastOut + 1] - r.stepTime);

        const tail = Math.max(1, Math.floor((n - edge) / 5));
        let sum = 0;
        for (let i = n - tail; i < n; i++) sum += ms[i];
        r.ssError = sum / tail - r.target;
        return r;
    }

    // ---- Least squares (calibration fits) ---------------------------------

    /* Solve A·x = b in the least-squares sense by normal equations with
       Gaussian elimination. Small, dense, well-conditioned problems only —
       which is exactly what a 3-term drag polynomial fit is. */
    function lstsq(A, b) {
        const m = A.length, n = A[0].length;
        const AtA = [], Atb = new Array(n).fill(0);
        for (let i = 0; i < n; i++) AtA.push(new Array(n).fill(0));
        for (let k = 0; k < m; k++) {
            for (let i = 0; i < n; i++) {
                Atb[i] += A[k][i] * b[k];
                for (let j = 0; j < n; j++) AtA[i][j] += A[k][i] * A[k][j];
            }
        }
        // Gaussian elimination with partial pivoting.
        const M = AtA.map(function (row, i) { return row.concat([Atb[i]]); });
        for (let c = 0; c < n; c++) {
            let piv = c;
            for (let rIdx = c + 1; rIdx < n; rIdx++) if (Math.abs(M[rIdx][c]) > Math.abs(M[piv][c])) piv = rIdx;
            if (Math.abs(M[piv][c]) < 1e-12) return null;
            const tmp = M[c]; M[c] = M[piv]; M[piv] = tmp;
            for (let rIdx = 0; rIdx < n; rIdx++) {
                if (rIdx === c) continue;
                const f = M[rIdx][c] / M[c][c];
                for (let j = c; j <= n; j++) M[rIdx][j] -= f * M[c][j];
            }
        }
        const x = new Array(n);
        for (let i = 0; i < n; i++) x[i] = M[i][n] / M[i][i];
        return x;
    }

    /* Fit drag(v) = c0 + c1·v + c2·v² to measured decelerations from a
       coast-down: F_drag = m_eff·(−dv/dt) at each sample. */
    function fitDragPolynomial(speeds, decels, massEff, opts) {
        const o = opts || {};
        const A = [], b = [];
        for (let i = 0; i < speeds.length; i++) {
            const v = speeds[i];
            const row = [1, v, v * v];
            if (o.noLinear) row[1] = 0;
            A.push(row);
            b.push(massEff * decels[i]);
        }
        const x = lstsq(A, b);
        if (!x) return null;
        const c = { c0: Math.max(0, x[0]), c1: o.noLinear ? 0 : x[1], c2: x[2] };
        // Residuals for the goodness readout.
        let ss = 0, sst = 0;
        const mean = b.reduce(function (s, v) { return s + v; }, 0) / b.length;
        for (let i = 0; i < speeds.length; i++) {
            const v = speeds[i];
            const pred = c.c0 + c.c1 * v + c.c2 * v * v;
            ss += (b[i] - pred) * (b[i] - pred);
            sst += (b[i] - mean) * (b[i] - mean);
        }
        c.rmse = Math.sqrt(ss / Math.max(1, speeds.length));
        c.r2 = sst > 1e-12 ? 1 - ss / sst : 1;
        return c;
    }

    /* Linear fit y = a·x + b, returned with r². Used by the encoder-scale
       and brake-slip calibrations. */
    function fitLinear(xs, ys) {
        const n = Math.min(xs.length, ys.length);
        if (n < 2) return null;
        let sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (let i = 0; i < n; i++) { sx += xs[i]; sy += ys[i]; sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i]; }
        const den = n * sxx - sx * sx;
        if (Math.abs(den) < 1e-12) return null;
        const a = (n * sxy - sx * sy) / den;
        const b = (sy - a * sx) / n;
        let ss = 0, sst = 0;
        const mean = sy / n;
        for (let i = 0; i < n; i++) {
            const pred = a * xs[i] + b;
            ss += (ys[i] - pred) * (ys[i] - pred);
            sst += (ys[i] - mean) * (ys[i] - mean);
        }
        return { slope: a, intercept: b, r2: sst > 1e-12 ? 1 - ss / sst : 1, n: n };
    }

    // ---- Ready-made experiments the lesson pages plot ----------------------

    /* Closed-loop speed run: feed-forward inverse model + PID trim, exactly
       the structure of opus_mission.c's longitudinal(). Returns channel
       arrays ready to hand to tt-plot / JGraph. */
    function runSpeedStep(opts) {
        const o = Object.assign({
            dt: 0.01, duration: 4, stepTime: 0.5, vStart: 0, vTarget: 4.5,
            // Defaults are the reference car's shipped gains (GA_SPD_KP/KI).
            kp: 12.0, ki: 30.0, kd: 0.0, aMax: 12, useFeedForward: true,
            antiWindup: true, delayTicks: 0, plant: {}
        }, opts || {});

        const plant = new Plant(o.plant);
        plant.reset(o.vStart);
        const pid = new Pid(o.kp, o.ki, o.kd, -o.aMax, o.aMax);
        pid.antiWindup = o.antiWindup;
        const delay = new Delay(o.delayTicks);

        const n = Math.max(2, Math.round(o.duration / o.dt));
        const out = { t: [], sp: [], v: [], volt: [], current: [], accel: [], ff: [], trim: [] };
        const mEff = plant.massEff();
        const c = plant.cfg;
        const bemf = c.kt * c.gearRatio / Math.max(1e-3, c.wheelRadius);
        const forcePerAmpAll = bemf * (c.efficiency > 0 ? c.efficiency : 1) * c.motorCount;

        for (let i = 0; i < n; i++) {
            const t = i * o.dt;
            const vRef = t < o.stepTime ? o.vStart : o.vTarget;

            const aTrim = pid.update(vRef, plant.v, o.dt);
            const aCmd = clampf(aTrim, -o.aMax, o.aMax);
            const fReq = mEff * aCmd + plant.drag(plant.v);
            const fMotor = fReq / (c.tractionEff > 0 ? c.tractionEff : 1);
            const iEach = fMotor / Math.max(1e-6, forcePerAmpAll);

            // The lesson's whole point: with feed-forward the PID only trims.
            const vFf = o.useFeedForward ? bemf * plant.v + c.resistance * iEach : 0;
            const vCmd = o.useFeedForward ? vFf : clampf(aCmd, -c.maxVoltage, c.maxVoltage);
            const vDelayed = delay.push(clampf(vCmd, -c.maxVoltage, c.maxVoltage));

            const s = plant.step(vDelayed, o.dt);
            out.t.push(t); out.sp.push(vRef); out.v.push(s.v);
            out.volt.push(s.voltage); out.current.push(s.current); out.accel.push(s.accel);
            out.ff.push(o.useFeedForward ? bemf * plant.v : 0);
            out.trim.push(aTrim);
        }
        out.metrics = stepMetrics(out.t, out.sp, out.v);
        return out;
    }

    /* Closed-loop yaw-rate run: kinematic feed-forward + PID on yaw rate. */
    function runYawStep(opts) {
        const o = Object.assign({
            dt: 0.01, duration: 3, stepTime: 0.3, speed: 4.5,
            yawRateTarget: 50 * Math.PI / 180,
            kp: 6, ki: 8, kd: 0.1, maxTrimDeg: 10,
            useFeedForward: true, understeerGrad: 0.02,
            wheelbase: 0.30, servoRateDegPerSec: 480, maxSteerDeg: 28, delayTicks: 0
        }, opts || {});

        const bike = new Bicycle({
            wheelbase: o.wheelbase, understeerGrad: o.understeerGrad,
            servoRateDegPerSec: o.servoRateDegPerSec, maxSteerDeg: o.maxSteerDeg
        });
        const pid = new Pid(o.kp, o.ki, o.kd, -o.maxTrimDeg, o.maxTrimDeg);
        const delay = new Delay(o.delayTicks);

        const n = Math.max(2, Math.round(o.duration / o.dt));
        const out = { t: [], sp: [], yawRate: [], steer: [], ff: [], yaw: [] };
        for (let i = 0; i < n; i++) {
            const t = i * o.dt;
            const ref = t < o.stepTime ? 0 : o.yawRateTarget;
            const ffDeg = o.useFeedForward
                ? Math.atan2(o.wheelbase * ref, Math.max(0.5, o.speed)) * 180 / Math.PI : 0;
            const trim = pid.update(ref, bike.yawRate, o.dt);
            const cmd = delay.push(ffDeg + trim);
            const s = bike.step(cmd, o.speed, o.dt);
            out.t.push(t); out.sp.push(ref); out.yawRate.push(s.yawRate);
            out.steer.push(s.steerDeg); out.ff.push(ffDeg); out.yaw.push(s.yaw);
        }
        out.metrics = stepMetrics(out.t, out.sp, out.yawRate);
        return out;
    }

    /* Distance-parameterised braking: v_ref = √(2·a·s_remaining), the profile
       that lands the car on a mark instead of merely stopping quickly. */
    function runBrakeProfile(opts) {
        const o = Object.assign({
            dt: 0.01, v0: 4.5, distance: 1.5, decel: 6.75,
            escBrakeNPerMs: 20.6, escBrakeCapN: 7, maxFrictionN: 24,
            leadTimeS: 0.02, plant: {}, kp: 12.0, ki: 30.0
        }, opts || {});

        const plant = new Plant(o.plant);
        plant.reset(o.v0);
        const pid = new Pid(o.kp, o.ki, 0, -12, 12);
        const mEff = plant.massEff();

        const out = { t: [], s: [], v: [], vref: [], escN: [], fricN: [], totalN: [] };
        let s = 0, t = 0;
        const maxT = 6;
        while (t < maxT && plant.v > 0.001) {
            const rem = Math.max(0, o.distance - s);
            // Lead term compensates the loop dead time: aim at where we will be.
            const remLead = Math.max(0, rem - plant.v * o.leadTimeS);
            const vRef = Math.sqrt(2 * o.decel * remLead);

            // The profile carries its own acceleration: differentiating
            // v_ref = √(2a·s_rem) along the path gives exactly −a. Feeding that
            // forward is what makes the PID a trim rather than the whole loop —
            // without it the car has to build an error before it brakes at all,
            // and it sails past the mark. (opus_mission.c passes this as a_ff.)
            const aFf = -o.decel;
            const aTrim = pid.update(vRef, plant.v, o.dt);
            const aCmd = clampf(aFf + aTrim, -12, 12);
            const fWanted = Math.max(0, -(mEff * aCmd - plant.drag(plant.v)));

            // Shorted-winding ESC brake: force ∝ duty·speed, so it fades to
            // nothing at rest — and it is capped by the driven axle's grip.
            const escAvail = Math.min(o.escBrakeCapN, o.escBrakeNPerMs * plant.v);
            const esc = Math.min(fWanted, escAvail);
            const fric = Math.min(o.maxFrictionN, Math.max(0, fWanted - esc));
            const total = esc + fric;

            const accel = (-total - plant.drag(plant.v)) / mEff;
            plant.v = Math.max(0, plant.v + accel * o.dt);
            s += plant.v * o.dt;
            out.t.push(t); out.s.push(s); out.v.push(plant.v); out.vref.push(vRef);
            out.escN.push(esc); out.fricN.push(fric); out.totalN.push(total);
            t += o.dt;
        }
        out.stopDistance = s;
        out.errorMm = (s - o.distance) * 1000;
        return out;
    }

    global.TT = global.TT || {};
    global.TT.Sim = {
        Pid: Pid, Esc: Esc, Plant: Plant, Bicycle: Bicycle, Delay: Delay,
        stepMetrics: stepMetrics, lstsq: lstsq,
        fitDragPolynomial: fitDragPolynomial, fitLinear: fitLinear,
        runSpeedStep: runSpeedStep, runYawStep: runYawStep, runBrakeProfile: runBrakeProfile,
        clampf: clampf
    };
})(typeof window !== 'undefined' ? window : globalThis);
