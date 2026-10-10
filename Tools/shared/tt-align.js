/* =====================================================================
   tt-align.js — putting two runs on one time base (VAL-06).

   A sim run and a car run (or two sim runs) never start at the same
   instant: the sim's clock starts at play, the car's at power-on, and the
   interesting part begins when someone presses go. Comparing them sample by
   sample needs three things this module does:

     • an offset: from an EVENT both runs share (the first non-zero command,
       the mission entering a phase), or the best fit of one channel;
     • resampling: run 2 interpolated onto run 1's samples (or a fixed
       rate) over the span both cover;
     • reality-gap numbers on the result: RMSE, bias, worst error, R²,
       the integral of each (energy, distance), trajectory error.

   Channel MAPPING (which run-2 column stands for which run-1 column) is the
   caller's: the functions take the arrays.

   Pure functions over plain arrays; no DOM. Checked by Tools/verify.js.
   ===================================================================== */
(function (global) {
    'use strict';

    function finite(v) { return typeof v === 'number' && isFinite(v); }

    /* Linear interpolation of (t, y) at tq; NaN outside [t0, tN]. t ascending. */
    function interp(t, y, tq) {
        const n = t.length;
        if (!n || !(tq >= t[0]) || !(tq <= t[n - 1])) return NaN;
        let lo = 0, hi = n - 1;
        while (hi - lo > 1) {
            const mid = (lo + hi) >> 1;
            if (t[mid] <= tq) lo = mid; else hi = mid;
        }
        if (t[hi] === t[lo]) return y[lo];
        const f = (tq - t[lo]) / (t[hi] - t[lo]);
        return y[lo] + f * (y[hi] - y[lo]);
    }

    /* The time of the first sample that meets a condition, or NaN.
       mode: 'abs_gt' (|y| > thr, the default — "the first non-zero command"),
             'ge' (y >= thr), 'le' (y <= thr), 'change' (y differs from its
             first value by more than thr). */
    function firstEvent(t, y, mode, thr) {
        const th = finite(thr) ? thr : 0;
        let y0 = NaN;
        for (let i = 0; i < t.length; i++) {
            const v = y[i];
            if (!finite(v)) continue;
            if (!finite(y0)) y0 = v;
            const hit = mode === 'ge' ? v >= th
                      : mode === 'le' ? v <= th
                      : mode === 'change' ? Math.abs(v - y0) > th
                      : Math.abs(v) > th;
            if (hit) return t[i];
        }
        return NaN;
    }

    /* The offset to ADD to run 2's time so its event lines up with run 1's. */
    function eventOffset(t1, y1, t2, y2, mode, thr) {
        const e1 = firstEvent(t1, y1, mode, thr), e2 = firstEvent(t2, y2, mode, thr);
        return finite(e1) && finite(e2) ? e1 - e2 : NaN;
    }

    /* Both runs on one grid: run 1's own samples (rate 0) or a fixed rate,
       over the span both cover once run 2 is shifted by `offset`. */
    function pair(t1, y1, t2, y2, offset, rate) {
        const off = finite(offset) ? offset : 0;
        const lo = Math.max(t1[0], t2[0] + off), hi = Math.min(t1[t1.length - 1], t2[t2.length - 1] + off);
        const out = { t: [], a: [], b: [] };
        if (!(hi > lo)) return out;
        const push = function (tq, a) {
            const b = interp(t2, y2, tq - off);
            if (finite(a) && finite(b)) { out.t.push(tq); out.a.push(a); out.b.push(b); }
        };
        if (rate > 0) {
            const dt = 1 / rate;
            for (let tq = lo; tq <= hi + 1e-9; tq += dt) push(tq, interp(t1, y1, tq));
        } else {
            for (let i = 0; i < t1.length; i++) if (t1[i] >= lo && t1[i] <= hi) push(t1[i], y1[i]);
        }
        return out;
    }

    /* Trapezoid integral of y over t. */
    function integral(t, y) {
        let s = 0;
        for (let i = 1; i < t.length; i++) s += 0.5 * (y[i] + y[i - 1]) * (t[i] - t[i - 1]);
        return s;
    }

    /* Reality-gap numbers for a pair: error = b - a (run 2 minus run 1). */
    function metrics(p) {
        const n = p.t.length;
        if (!n) return { n: 0 };
        let se = 0, sum = 0, worst = 0, worstT = p.t[0], ma = 0;
        for (let i = 0; i < n; i++) {
            const e = p.b[i] - p.a[i];
            se += e * e;
            sum += e;
            ma += p.a[i];
            if (Math.abs(e) > Math.abs(worst)) { worst = e; worstT = p.t[i]; }
        }
        ma /= n;
        let ss = 0;
        for (let i = 0; i < n; i++) ss += (p.a[i] - ma) * (p.a[i] - ma);
        const ia = integral(p.t, p.a), ib = integral(p.t, p.b);
        return {
            n: n,
            span: p.t[n - 1] - p.t[0],
            rmse: Math.sqrt(se / n),
            bias: sum / n,
            worst: worst,
            worstAt: worstT,
            r2: ss > 0 ? 1 - se / ss : NaN,     // how much of run 1's variation run 2 explains
            intA: ia,
            intB: ib,
            intDiffPct: Math.abs(ia) > 1e-12 ? (ib - ia) / Math.abs(ia) * 100 : NaN
        };
    }

    /* The offset (added to run 2's time) that minimises the RMSE between two
       channels, searched over ±range s: coarse, then finer around the best.
       Needs at least `minOverlap` of the shorter run in common. */
    function bestOffset(t1, y1, t2, y2, range, minOverlap) {
        const R = finite(range) && range > 0 ? range : 5;
        const need = finite(minOverlap) ? minOverlap : 0.5;
        const span = Math.min(t1[t1.length - 1] - t1[0], t2[t2.length - 1] - t2[0]);
        const base = (t1[0] - t2[0]);              // start-aligned
        let best = { off: NaN, rmse: Infinity };
        function score(off) {
            const p = pair(t1, y1, t2, y2, off, 50);
            if (p.t.length < 5 || p.t[p.t.length - 1] - p.t[0] < need * span) return Infinity;
            return metrics(p).rmse;
        }
        let step = R / 50, lo = base - R, hi = base + R;
        for (let pass = 0; pass < 4; pass++) {
            for (let off = lo; off <= hi + 1e-12; off += step) {
                const r = score(off);
                if (r < best.rmse) best = { off: off, rmse: r };
            }
            if (!finite(best.off)) break;
            lo = best.off - step;
            hi = best.off + step;
            step /= 10;
        }
        return best;
    }

    /* Position error between two paths after alignment: RMSE and at the end. */
    function trajectoryError(t1, x1, y1, t2, x2, y2, offset) {
        const px = pair(t1, x1, t2, x2, offset, 0), py = pair(t1, y1, t2, y2, offset, 0);
        const n = Math.min(px.t.length, py.t.length);
        if (!n) return { n: 0 };
        let se = 0, worst = 0;
        for (let i = 0; i < n; i++) {
            const d2 = (px.b[i] - px.a[i]) * (px.b[i] - px.a[i]) + (py.b[i] - py.a[i]) * (py.b[i] - py.a[i]);
            se += d2;
            if (d2 > worst) worst = d2;
        }
        const k = n - 1;
        return {
            n: n,
            rmse: Math.sqrt(se / n),
            max: Math.sqrt(worst),
            final: Math.hypot(px.b[k] - px.a[k], py.b[k] - py.a[k])
        };
    }

    const TT = global.TT = global.TT || {};
    TT.Align = {
        interp: interp, firstEvent: firstEvent, eventOffset: eventOffset, pair: pair,
        integral: integral, metrics: metrics, bestOffset: bestOffset, trajectoryError: trajectoryError
    };
})(typeof window !== 'undefined' ? window : globalThis);
