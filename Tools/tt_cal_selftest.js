#!/usr/bin/env node
/*
 * tt_cal_selftest.js — the Calibration Companion's FOC fits (VAL-08) on
 * synthetic logs made from known parameters, with noise: each fit must give
 * the parameter back within its tolerance. Run by CI's tools job.
 *
 *   node Tools/tt_cal_selftest.js
 */
'use strict';
const Cal = require('./shared/tt-cal.js');

let seed = 20261013;
function rnd() {               // mulberry32: the same logs every run
    seed |= 0; seed = seed + 0x6D2B79F5 | 0;
    let t = Math.imul(seed ^ seed >>> 15, 1 | seed);
    t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
    return ((t ^ t >>> 14) >>> 0) / 4294967296;
}
function gauss() { return Math.sqrt(-2 * Math.log(1 - rnd())) * Math.cos(2 * Math.PI * rnd()); }

let fails = 0;
function check(name, got, want, tolFrac, absTol) {
    const err = Math.abs(got - want), tol = absTol !== undefined ? absTol : Math.abs(want) * tolFrac;
    const ok = isFinite(got) && err <= tol;
    console.log((ok ? 'ok   ' : 'FAIL ') + name + ': ' + got.toPrecision(5) + ' (want ' + want.toPrecision(5) +
                ', tol ' + tol.toPrecision(2) + ')');
    if (!ok) fails++;
}

// ---- E: kt --------------------------------------------------------------
{
    const p = 7, lambda = 0.00065, kt = 1.5 * p * lambda;
    check('kt from flux', Cal.ktFromFlux(p, lambda), kt, 1e-9);
    const rows = [];
    for (let w = 50; w <= 500; w += 50) rows.push([w, Math.sqrt(3) * p * lambda * w * (1 + 0.01 * gauss())]);
    const f = Cal.ktFromBemf(rows);
    check('kt from back-EMF', f.kt, kt, 0.02);
    check('Kv from back-EMF', f.kvRpmPerV, 60 / (2 * Math.PI * kt), 0.02);
    check('kt from Kv', Cal.ktFromKv(1400), 0.006820926, 1e-6);
    const dyno = [];
    for (let i = 0.5; i <= 5; i += 0.5) dyno.push([i, kt * i * (1 + 0.02 * gauss())]);
    check('kt from a torque bench', Cal.ktFromDyno(dyno).kt, kt, 0.03);
}

// ---- F: friction and inertia -------------------------------------------
{
    const kt = 0.0068, tc = 0.004, b = 1e-5, J = 2e-6;
    const sweep = [];
    for (let w = 50; w <= 600; w += 50) sweep.push([w, (tc + b * w) / kt * (1 + 0.01 * gauss())]);
    const fr = Cal.fitFriction(sweep, kt);
    check('friction Tc', fr.tc, tc, 0.05);
    check('friction b', fr.b, b, 0.05);
    // run-down from 600 rad/s, 1 kHz, 0.5 % speed noise
    const rd = [];
    let w = 600;
    for (let t = 0; w > 1; t += 0.001) {
        rd.push([t, w * (1 + 0.005 * gauss())]);
        w += -(tc + b * w) / J * 0.001;
    }
    const sm = rd.map(function (r, i) {       // a little smoothing, as a user would
        let s = 0, c = 0;
        for (let k = -5; k <= 5; k++) if (rd[i + k]) { s += rd[i + k][1]; c++; }
        return [r[0], s / c];
    });
    check('run-down J', Cal.fitRunDown(sm, fr.tc, fr.b).J, J, 0.08);
}

// ---- G: regen -----------------------------------------------------------
{
    const m = 2.3, drag = { c0: 1.4, c1: 0.16, c2: 0.003 }, eta = 0.7, V = 7.4;
    const rows = [];
    let v = 3.0, t = 0;
    while (v > 0.5) {
        const fd = drag.c0 + drag.c1 * v + drag.c2 * v * v;
        const a = -3.0;
        const fm = m * a + fd;                       // the motors' share (negative)
        const pRet = eta * (-fm) * v;                // W back into the pack
        rows.push([t, v, V, -pRet / V * (1 + 0.01 * gauss())]);
        v += a * 0.01; t += 0.01;
    }
    check('regen efficiency', Cal.fitRegen(rows, m, drag).eta, eta, 0.03);
}

// ---- H: roll-out --------------------------------------------------------
{
    const cpr = 20480, r = [0.0330, 0.0333, 0.0327, 0.0331];
    const rows = [];
    for (let w = 0; w < 4; w++)
        for (let k = 0; k < 3; k++) rows.push([w, 10 / (2 * Math.PI * r[w]) * cpr * (1 + 0.001 * gauss()), 10]);
    const f = Cal.fitRollout(rows, cpr);
    for (let w = 0; w < 4; w++) check('roll-out radius wheel ' + w, f.radius[w], r[w], 0.003);
    check('roll-out spread', f.spreadFrac, 0.0091, 0, 0.004);
}

// ---- I: the steering map -----------------------------------------------
{
    const u0 = 0.02, kL = 0.46, kR = 0.50, lash = 0.010;
    const rows = [];
    for (let pass = 0; pass < 2; pass++)
        for (let i = 0; i <= 20; i++) {
            const dir = pass === 0 ? 1 : -1;
            const u = dir > 0 ? -1 + 0.1 * i : 1 - 0.1 * i;
            const x = u - u0, d = (x < 0 ? kL : kR) * x - dir * lash / 2 + 0.002 * gauss();
            rows.push([u, d, dir]);
        }
    const f = Cal.fitSteerMap(rows);
    check('steer centre u0', f.u0, u0, 0, 0.012);
    check('steer gain left', f.kL, kL, 0.04);
    check('steer gain right', f.kR, kR, 0.04);
    check('steer max (mean gain)', f.maxSteerRad, 0.48, 0.04);
    check('steer backlash', f.backlashRad, lash, 0.3);
    check('steer from a circle', Cal.steerFromYaw(1.0, 2.0, 0.3), Math.atan(0.15), 1e-9);
}

// ---- J: Allan deviation -------------------------------------------------
{
    const fs = 400, N = 0.005;                      // rad/s/sqrt(Hz): white noise
    const sigma = N * Math.sqrt(fs), x = [];
    for (let i = 0; i < 60 * fs; i++) x.push(sigma * gauss());
    const av = Cal.allanDeviation(x, fs);
    const fig = Cal.allanFigures(av);
    check('Allan: angle random walk N', fig.N, N, 0.15);
    // the -1/2 slope: adev at 4x the tau is half
    const i1 = av.tau.findIndex(function (t) { return t >= 0.05; });
    const i2 = av.tau.findIndex(function (t) { return t >= 0.2; });
    check('Allan: white noise falls as 1/sqrt(tau)', av.adev[i2] / av.adev[i1], Math.sqrt(av.tau[i1] / av.tau[i2]), 0.2);
}

console.log(fails ? fails + ' check(s) FAILED' : 'all checks passed');
process.exit(fails ? 1 : 0);
