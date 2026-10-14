/*
 * tt-cal.js — the fits behind the Calibration Companion's FOC procedures
 * (VAL-08): motor constant, motor friction and inertia, regen efficiency,
 * roll-out radius, steering map and backlash, gyro Allan deviation.
 *
 * Pure functions on arrays of numbers, no DOM: calibration.html calls them
 * on pasted log rows, and Tools/tt_cal_selftest.js runs them on synthetic
 * logs made from known parameters (in CI). Each fit returns what it found
 * and how well it fitted (r² or a residual), never a value without its
 * evidence. Conventions: SI throughout; a motor's "kt" is N·m per amp of
 * Iq with the amplitude-invariant Clarke transform the firmware's FOC uses
 * (foc/foc.c), so torque = 1.5 · p · λ · Iq and kt = 1.5 · p · λ.
 */
(function (root) {
    'use strict';

    // ---- small linear algebra ------------------------------------------

    /* y ≈ a·x + b (or through the origin: b = 0). */
    function fitLine(x, y, throughOrigin) {
        const n = x.length;
        if (n < (throughOrigin ? 1 : 2)) return null;
        let sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (let i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; }
        let a, b;
        if (throughOrigin) {
            if (sxx < 1e-18) return null;
            a = sxy / sxx; b = 0;
        } else {
            const d = n * sxx - sx * sx;
            if (Math.abs(d) < 1e-18) return null;
            a = (n * sxy - sx * sy) / d;
            b = (sy - a * sx) / n;
        }
        const mean = sy / n;
        let ss = 0, sst = 0;
        for (let i = 0; i < n; i++) {
            const e = y[i] - (a * x[i] + b);
            ss += e * e; sst += (y[i] - mean) * (y[i] - mean);
        }
        return { a: a, b: b, r2: sst > 1e-18 ? 1 - ss / sst : 1, rmse: Math.sqrt(ss / n), n: n };
    }

    function median(v) {
        const s = v.slice().sort(function (p, q) { return p - q; });
        const m = s.length >> 1;
        return s.length ? (s.length % 2 ? s[m] : 0.5 * (s[m - 1] + s[m])) : NaN;
    }

    /* Central-difference derivative of y(t) on an uneven grid. */
    function derivative(t, y) {
        const d = new Array(t.length).fill(NaN);
        for (let i = 1; i < t.length - 1; i++) {
            const dt = t[i + 1] - t[i - 1];
            if (dt > 0) d[i] = (y[i + 1] - y[i - 1]) / dt;
        }
        return d;
    }

    // ---- E: the motor constant --------------------------------------------

    /* From the driver's own commissioning: flux linkage λ (Wb) and pole pairs. */
    function ktFromFlux(polePairs, lambdaWb) {
        return 1.5 * polePairs * lambdaWb;
    }

    /* From a back-EMF measurement: the motor spun at mechanical ω (rad/s), the
       line-to-line PEAK voltage read on a scope, several speeds. V_ll,peak =
       √3 · p · λ · ω, so kt = 1.5 p λ = (√3/2) · V_ll,peak / ω. */
    function ktFromBemf(rows) {
        const w = [], v = [];
        rows.forEach(function (r) { if (r[0] > 1e-3 && r[1] > 0) { w.push(r[0]); v.push(r[1]); } });
        const f = fitLine(w, v, true);
        if (!f) return null;
        const kt = Math.sqrt(3) / 2 * f.a;
        return { kePeakLL: f.a, kt: kt, kvRpmPerV: 60 / (2 * Math.PI * kt), r2: f.r2, n: f.n };
    }

    /* From a Kv rating (rpm per volt, the hobby figure). */
    function ktFromKv(kvRpmPerV) {
        return 60 / (2 * Math.PI * kvRpmPerV);
    }

    /* From a torque bench: Iq (A) against the torque a transducer read (N·m). */
    function ktFromDyno(rows) {
        const i = [], t = [];
        rows.forEach(function (r) { if (Math.abs(r[0]) > 1e-3) { i.push(r[0]); t.push(r[1]); } });
        const f = fitLine(i, t, true);
        return f ? { kt: f.a, r2: f.r2, n: f.n } : null;
    }

    // ---- F: motor friction and inertia (S4) --------------------------------

    /* Steady speeds with the wheels off the ground: at each ω (rad/s, motor
       side) the Iq (A) that holds it. kt · Iq = Tc + b · ω. */
    function fitFriction(rows, kt) {
        const w = [], tq = [];
        rows.forEach(function (r) { if (r[0] > 0) { w.push(r[0]); tq.push(kt * r[1]); } });
        const f = fitLine(w, tq, false);
        if (!f) return null;
        return { tc: f.b, b: f.a, r2: f.r2, rmse: f.rmse, n: f.n };
    }

    /* The zero-torque run-down (t, ω): J · dω/dt = −(Tc + b · ω). With Tc
       and b from the sweep, J is the one unknown; fitted through the origin
       of (−(Tc + bω), J·ω̇) so every sample weighs in. */
    function fitRunDown(rows, tc, b) {
        const t = rows.map(function (r) { return r[0]; }), w = rows.map(function (r) { return r[1]; });
        const wd = derivative(t, w);
        const x = [], y = [];
        for (let i = 0; i < t.length; i++) {
            if (!isFinite(wd[i]) || wd[i] >= 0 || w[i] <= 0.05 * Math.max.apply(null, w)) continue;
            x.push(-wd[i]); y.push(tc + b * w[i]);     // J · (−ω̇) = Tc + b ω
        }
        const f = fitLine(x, y, true);
        return f ? { J: f.a, r2: f.r2, n: f.n } : null;
    }

    // ---- G: regen efficiency -----------------------------------------------

    /* A braking run on the motors alone: t (s), speed (m/s), pack volts, pack
       amps (+ = out of the pack). What came back into the pack against what the
       motors took out of the car's motion, the drag's share removed. */
    function fitRegen(rows, massEff, drag) {
        if (rows.length < 3) return null;
        let eRet = 0, wDrag = 0;
        for (let i = 1; i < rows.length; i++) {
            const dt = rows[i][0] - rows[i - 1][0];
            if (dt <= 0) continue;
            const v = 0.5 * (rows[i][1] + rows[i - 1][1]);
            const p = 0.5 * (rows[i][2] * rows[i][3] + rows[i - 1][2] * rows[i - 1][3]);
            if (p < 0) eRet += -p * dt;                       // charging: power into the pack
            const fd = drag.c0 + drag.c1 * v + drag.c2 * v * v;
            wDrag += fd * Math.abs(v) * dt;
        }
        const v0 = rows[0][1], v1 = rows[rows.length - 1][1];
        const dKe = 0.5 * massEff * (v0 * v0 - v1 * v1);
        const eBrake = dKe - wDrag;
        return {
            eRet: eRet, dKe: dKe, wDrag: wDrag, eBrake: eBrake,
            eta: eBrake > 1e-6 ? eRet / eBrake : NaN
        };
    }

    // ---- H: roll-out radius (S7) -------------------------------------------

    /* Counts over a taped distance, per wheel: rows [wheel 0..3, counts, metres],
       cprWheel = counts per WHEEL turn (encoder cpr × gear). */
    function fitRollout(rows, cprWheel) {
        const byWheel = [[], [], [], []];
        rows.forEach(function (r) {
            const w = Math.round(r[0]);
            if (w >= 0 && w < 4 && r[1] > 0 && r[2] > 0) byWheel[w].push(r[2] / (r[1] / cprWheel * 2 * Math.PI));
        });
        const radius = byWheel.map(function (v) {
            if (!v.length) return NaN;
            return v.reduce(function (a, b) { return a + b; }, 0) / v.length;
        });
        const have = radius.filter(isFinite);
        const mean = have.length ? have.reduce(function (a, b) { return a + b; }, 0) / have.length : NaN;
        let spread = 0;
        have.forEach(function (r) { spread = Math.max(spread, Math.abs(r - mean)); });
        return { radius: radius, mean: mean, spreadFrac: have.length ? spread / mean : NaN, n: have.length };
    }

    // ---- I: the steering map (S8) ------------------------------------------

    /* Road-wheel angle from a steady circle: yaw rate r at speed v on a car of
       wheelbase L (kinematic, low speed): δ = atan(L · r / v). */
    function steerFromYaw(yawRate, v, wheelbase) {
        return v > 0.05 ? Math.atan(wheelbase * yawRate / v) : NaN;
    }

    /* Command u (−1..1) against road-wheel angle δ (rad): a centre offset u0,
       a gain each side (the linkage is rarely symmetric), and from rows that
       say which way the command was going (dir +1 rising, −1 falling) the
       backlash as the up/down split. */
    function fitSteerMap(rows) {
        const u = rows.map(function (r) { return r[0]; }), d = rows.map(function (r) { return r[1]; });
        const dir = rows.map(function (r) { return r.length > 2 ? Math.sign(r[2]) : 0; });
        if (u.length < 4) return null;
        let best = null;
        // The centre: the u where δ crosses zero, searched on a grid.
        for (let u0 = -0.3; u0 <= 0.3001; u0 += 0.005) {
            let sl = 0, sll = 0, sr = 0, srr = 0, ss = 0;
            for (let i = 0; i < u.length; i++) {
                const x = u[i] - u0;
                if (x < 0) { sl += x * d[i]; sll += x * x; } else { sr += x * d[i]; srr += x * x; }
            }
            const kL = sll > 1e-12 ? sl / sll : NaN, kR = srr > 1e-12 ? sr / srr : NaN;
            if (!isFinite(kL) || !isFinite(kR)) continue;
            for (let i = 0; i < u.length; i++) {
                const x = u[i] - u0, e = d[i] - (x < 0 ? kL : kR) * x;
                ss += e * e;
            }
            if (!best || ss < best.ss) best = { u0: u0, kL: kL, kR: kR, ss: ss };
        }
        if (!best) return null;
        const mean = d.reduce(function (a, b) { return a + b; }, 0) / d.length;
        let sst = 0;
        d.forEach(function (y) { sst += (y - mean) * (y - mean); });
        // Backlash: at the same command the wheels sit on one side of the play
        // when rising and the other when falling; the split is the play.
        let backlash = NaN;
        const ups = [], downs = [];
        for (let i = 0; i < u.length; i++) {
            const x = u[i] - best.u0, pred = (x < 0 ? best.kL : best.kR) * x, e = d[i] - pred;
            if (dir[i] > 0) ups.push(e); else if (dir[i] < 0) downs.push(e);
        }
        if (ups.length >= 2 && downs.length >= 2) backlash = Math.abs(median(ups) - median(downs));
        const maxSteer = 0.5 * (Math.abs(best.kL) + Math.abs(best.kR));
        return {
            u0: best.u0, kL: best.kL, kR: best.kR, maxSteerRad: maxSteer,
            asymFrac: maxSteer > 1e-9 ? (Math.abs(best.kR) - Math.abs(best.kL)) / maxSteer : NaN,
            backlashRad: backlash, r2: sst > 1e-18 ? 1 - best.ss / sst : 1, n: u.length
        };
    }

    // ---- J: Allan deviation (S13) ------------------------------------------

    /* The overlapping Allan deviation of a rate signal sampled at fs (Hz), at
       cluster times τ log-spaced from 2 samples to a fifth of the record. */
    function allanDeviation(x, fs, points) {
        const n = x.length;
        if (n < 32 || !(fs > 0)) return null;
        const theta = new Float64Array(n + 1);          // the integral (angle)
        for (let i = 0; i < n; i++) theta[i + 1] = theta[i] + x[i] / fs;
        const out = { tau: [], adev: [] };
        const mMax = Math.floor(n / 5);
        const steps = points || 40;
        let last = 0;
        for (let k = 0; k < steps; k++) {
            const m = Math.max(1, Math.round(Math.pow(10, Math.log10(1) + (Math.log10(mMax) - 0) * k / (steps - 1))));
            if (m === last || m > mMax) continue;
            last = m;
            let s = 0, c = 0;
            for (let i = 0; i + 2 * m <= n; i++) {
                const d = theta[i + 2 * m] - 2 * theta[i + m] + theta[i];
                s += d * d; c++;
            }
            if (!c) continue;
            const tau = m / fs;
            out.tau.push(tau);
            out.adev.push(Math.sqrt(s / (2 * tau * tau * c)));
        }
        return out;
    }

    /* What a gyro's Allan curve says: the angle random walk N (the curve at
       τ = 1 s on its −½ slope, in rad/s/√Hz: the white noise density), the bias
       instability B (the floor, /0.664), and where the floor is. */
    function allanFigures(av) {
        if (!av || av.tau.length < 3) return null;
        // N: interpolate log-log at τ = 1 s, or extrapolate the first points' slope.
        let N = NaN;
        for (let i = 1; i < av.tau.length; i++) {
            if (av.tau[i - 1] <= 1 && av.tau[i] >= 1) {
                const f = (Math.log(1) - Math.log(av.tau[i - 1])) / (Math.log(av.tau[i]) - Math.log(av.tau[i - 1]));
                N = Math.exp(Math.log(av.adev[i - 1]) + f * (Math.log(av.adev[i]) - Math.log(av.adev[i - 1])));
                break;
            }
        }
        if (!isFinite(N)) N = av.adev[0] * Math.sqrt(av.tau[0]);   // −½ slope from the first point
        let iMin = 0;
        for (let i = 1; i < av.adev.length; i++) if (av.adev[i] < av.adev[iMin]) iMin = i;
        return { N: N, B: av.adev[iMin] / 0.664, tauB: av.tau[iMin], atFloor: iMin < av.adev.length - 1 };
    }

    root.TT = root.TT || {};
    root.TT.Cal = {
        fitLine: fitLine, median: median, derivative: derivative,
        ktFromFlux: ktFromFlux, ktFromBemf: ktFromBemf, ktFromKv: ktFromKv, ktFromDyno: ktFromDyno,
        fitFriction: fitFriction, fitRunDown: fitRunDown,
        fitRegen: fitRegen,
        fitRollout: fitRollout,
        steerFromYaw: steerFromYaw, fitSteerMap: fitSteerMap,
        allanDeviation: allanDeviation, allanFigures: allanFigures
    };
    if (typeof module !== 'undefined' && module.exports) module.exports = root.TT.Cal;
})(typeof window !== 'undefined' ? window : globalThis);
