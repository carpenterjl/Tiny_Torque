#!/usr/bin/env python3
"""
sysid.py — fit the car's parameters from logs, and write them back (VAL-09).

    python Tools/sysid.py <test> <log.csv> [options] [--write params.json]
    python Tools/sysid.py selftest

The tests are the bench and track procedures of SIM_TO_REAL_PLAN §10. Each
reads a CSV log (the sim's telemetry with -opusTelemetry 1, a car log
decoded by Tools/tt_log_decode.js, an RC-suite trace, or a bench capture),
fits a model, and prints the fit with its uncertainty:

    coastdown  S9   drag_c0, drag_c1 (drag_c2)    m_eff dv/dt = -(c0 + c1 v + c2 v^2)
    cornering  S10  tyre_c_alpha (C_alpha/F_z)     body sideslip on steady circles:
                                                   beta = asin(l_r kappa) - a_y / (g k)
    steer      S8   steer_lag_s, steer_backlash_rad, the servo's w_n, zeta, dead time
    latency    S12  cmd_delay_s                    command -> response, cross-correlation
    rollout    S7   wheel_radius_m                 counts over a taped distance
    imu        S13  gyro / accel bias and noise    a still stretch
    battery    S13  pack internal resistance       V = OCV(t) - R I

With --write, the fitted values go into a firmware parameter file
(Controllers/params/<car>.json): the value, its "_<field>" note (what was
measured, on which log, when, and what it was before), and the file's
"_provenance" map, which extends the Opus three-tag rule (published /
derived / estimated) with a fourth: **measured**. Then run
node Tools/gen_params.js. The twin (VehiclePresets.cs) is code, not data:
the report says which of its fields the fit speaks to.

`selftest` fits synthetic logs made from known parameters with noise, and
must recover them (CI runs it).

Needs numpy and scipy.
"""
import argparse
import csv
import datetime
import json
import math
import os
import re
import sys

import numpy as np
from scipy.optimize import least_squares

G = 9.80665

# ---------------------------------------------------------------- logs --

def read_log(path):
    """CSV -> {column: float array}; blanks and text read as NaN."""
    with open(path, encoding='utf-8-sig', newline='') as f:
        rd = csv.reader(f)
        head = next(rd)
        rows = list(rd)
    out = {}
    for j, h in enumerate(head):
        col = np.full(len(rows), np.nan)
        for i, r in enumerate(rows):
            if j < len(r) and r[j] != '':
                try:
                    col[i] = float(r[j])
                except ValueError:
                    pass
        out[h.strip()] = col
    return out


def pick(log, *names):
    """The first of these columns the log has."""
    for n in names:
        if n and n in log:
            return log[n]
    raise SystemExit('none of the columns %s in the log' % (', '.join(n for n in names if n),))


def segments(mask, min_len):
    """[(i0, i1)] runs of True at least min_len long (i1 exclusive)."""
    out, i, n = [], 0, len(mask)
    while i < n:
        if mask[i]:
            j = i
            while j < n and mask[j]:
                j += 1
            if j - i >= min_len:
                out.append((i, j))
            i = j
        else:
            i += 1
    return out


def sigma(res):
    """Parameter standard deviations from a least_squares result."""
    J = res.jac
    dof = max(1, J.shape[0] - J.shape[1])
    s2 = 2.0 * res.cost / dof
    cov = np.linalg.pinv(J.T @ J) * s2
    return np.sqrt(np.maximum(np.diag(cov), 0.0))


class Fit:
    def __init__(self, test):
        self.test = test
        self.params = {}        # firmware field -> (value, sigma)
        self.extra = {}         # other results -> (value, sigma or None)
        self.notes = []

    def show(self):
        print('%s fit:' % self.test)
        for k, (v, s) in self.params.items():
            print('  %-20s %12.6g  +- %.3g' % (k, v, s if s is not None else float('nan')))
        for k, (v, s) in self.extra.items():
            print('  %-20s %12.6g%s' % (k, v, '' if s is None else '  +- %.3g' % s))
        for n in self.notes:
            print('  ' + n)

# ------------------------------------------------------------ coastdown --

def fit_coastdown(t, v, drive, m_eff, v_min=0.3, fix_c2=None, drive_eps=1e-3):
    """Coast-down: stretches with no drive, v(t) from the ODE, shooting
    from each stretch's first speed."""
    ok = np.isfinite(t) & np.isfinite(v) & (v > v_min) & (np.abs(np.nan_to_num(drive)) < drive_eps)
    dt = np.median(np.diff(t))
    segs = [(a, b) for a, b in segments(ok, int(round(1.0 / dt))) if t[b - 1] - t[a] > 0.8]
    if not segs:
        raise SystemExit('coastdown: no coasting stretch above %.2f m/s' % v_min)

    def sim(c, a, b):
        c0, c1, c2 = c
        out = np.empty(b - a)
        x = v[a]
        out[0] = x
        for i in range(a + 1, b):
            h = t[i] - t[i - 1]
            f = lambda y: -(c0 + c1 * y + c2 * y * y) / m_eff
            k1 = f(x); k2 = f(x + 0.5 * h * k1); k3 = f(x + 0.5 * h * k2); k4 = f(x + h * k3)
            x = x + h * (k1 + 2 * k2 + 2 * k3 + k4) / 6.0
            out[i - a] = x
        return out

    def resid(p):
        c = [p[0], p[1], fix_c2 if fix_c2 is not None else p[2]]
        return np.concatenate([sim(c, a, b) - v[a:b] for a, b in segs])

    # start from the derivative's straight-line fit
    vv = np.concatenate([v[a:b] for a, b in segs])
    dv = np.concatenate([np.gradient(v[a:b], t[a:b]) for a, b in segs])
    A = np.vstack([np.ones_like(vv), vv]).T
    c01 = np.linalg.lstsq(A, -m_eff * dv, rcond=None)[0]
    p0 = [max(c01[0], 0.01), max(c01[1], 0.0)] + ([] if fix_c2 is not None else [0.001])
    r = least_squares(resid, p0, bounds=(0.0, np.inf))
    s = sigma(r)
    fit = Fit('coastdown')
    fit.params['drag_c0'] = (r.x[0], s[0])
    fit.params['drag_c1'] = (r.x[1], s[1])
    if fix_c2 is None:
        fit.params['drag_c2'] = (r.x[2], s[2])
    fit.extra['stretches'] = (len(segs), None)
    fit.extra['speed_rms_mps'] = (float(np.sqrt(np.mean(r.fun ** 2))), None)
    fit.notes.append('F(v) = %.4f + %.4f v + %.5f v^2 N  (m_eff %.4f kg)'
                     % (r.x[0], r.x[1], r.x[2] if fix_c2 is None else fix_c2, m_eff))
    fit.notes.append('twin: the sum of rolling resistance (WheelSpec.rollCrr), bearings '
                     '(bearingNm, bearingNmsPerRad), motor friction and Cd*A; not unique per term')
    return fit

# ------------------------------------------------------------ cornering --

def smooth(x, n):
    if n <= 1:
        return x
    k = np.ones(n) / n
    return np.convolve(x, k, mode='same')


def cornering_samples(t, x, y, psi, v, r, r_min=0.15, v_min=0.8, settle_s=0.4, win=5):
    """Steady-circle samples: (kappa, a_y, beta) with beta = course - heading."""
    psi = np.unwrap(psi)
    xs, ys = smooth(x, win), smooth(y, win)
    course = np.unwrap(np.arctan2(np.gradient(ys, t), np.gradient(xs, t)))
    beta = (course - psi + np.pi) % (2 * np.pi) - np.pi
    rs = smooth(r, win)
    # steady: the yaw rate over a quarter second changes little (differenced
    # over a few samples, gyro noise alone breaks every stretch)
    rl = smooth(r, 25)
    steady = (np.abs(rs) > r_min) & (v > v_min) & (np.abs(np.gradient(rl, t)) < 0.5)
    dt = np.median(np.diff(t))
    keep = np.zeros_like(steady)
    for a, b in segments(steady, int(round(1.0 / dt))):
        a2 = a + int(round(settle_s / dt))
        keep[a2:b - win] = True
    if keep.sum() < 20:
        raise SystemExit('cornering: no steady circle (|r| > %.2f rad/s, v > %.1f m/s)' % (r_min, v_min))
    return rs[keep] / v[keep], v[keep] * rs[keep], beta[keep]


def fit_cornering(kap, ay, beta, l_r, fit_l_r=False):
    """beta = asin(l_r kappa) - a_y / (g k): k by least squares, and the rear
    axle's place l_r too when the circles were driven at more than one
    speed (the kinematic part goes with kappa, the tyres' with a_y; at one
    speed they cannot be told apart, and 1 cm of l_r is ~20 % of k)."""
    fit = Fit('cornering')
    if fit_l_r:
        A = np.vstack([kap, -ay / G]).T
        p, *_ = np.linalg.lstsq(A, beta, rcond=None)
        res = beta - A @ p
        s2 = float(np.sum(res ** 2) / max(1, len(res) - 2))
        cov = np.linalg.inv(A.T @ A) * s2
        l_r, inv_k = float(p[0]), float(p[1])
        k = 1.0 / inv_k if inv_k > 0 else float('inf')
        fit.params['tyre_c_alpha'] = (k, k * k * math.sqrt(cov[1, 1]))
        fit.extra['rear_axle_behind_m'] = (l_r, math.sqrt(cov[0, 0]))
        fit.extra['samples'] = (len(kap), None)
        fit.extra['a_y_range'] = (float(np.max(np.abs(ay))), None)
        fit.extra['beta_rms_deg'] = (float(np.degrees(np.sqrt(np.mean(res ** 2)))), None)
        fit.notes.append('rear axle fitted; per-tyre C_alpha = k * F_z (the small-angle form of the model)')
        fit.notes.append('twin: the tyre (WheelSpec / tyre model) lateral stiffness; R5 measures the same')
        return fit
    yk = np.arcsin(np.clip(l_r * kap, -1, 1)) - beta   # = a_y / (g k)
    xk = ay / G
    inv_k = float(np.sum(xk * yk) / np.sum(xk * xk))
    res = yk - inv_k * xk
    s_inv = float(np.sqrt(np.sum(res ** 2) / max(1, len(res) - 1) / np.sum(xk * xk)))
    k = 1.0 / inv_k if inv_k > 0 else float('inf')
    fit.params['tyre_c_alpha'] = (k, k * k * s_inv)
    fit.extra['samples'] = (len(kap), None)
    fit.extra['a_y_range'] = (float(np.max(np.abs(ay))), None)
    fit.extra['beta_rms_deg'] = (float(np.degrees(np.sqrt(np.mean(res ** 2)))), None)
    fit.notes.append('rear axle %.3f m behind the origin; per-tyre C_alpha = k * F_z' % l_r)
    fit.notes.append('twin: the tyre (WheelSpec / tyre model) lateral stiffness; R5 measures the same')
    return fit

# ---------------------------------------------------------------- steer --

def servo_sim(t, cmd, td, wn, zeta, half_lash, a0):
    """The road wheel through a servo: the command delayed by td, a 2nd-order
    position loop (wn, zeta), then the linkage's free play (+-half_lash)."""
    n = len(t)
    out = np.empty(n)
    pos, vel, wheel = a0, 0.0, a0
    j = 0
    for i in range(n):
        if i > 0:
            h = t[i] - t[i - 1]
            sub = max(1, int(math.ceil(h / 0.001)))
            hh = h / sub
            for q in range(sub):
                # the command acting at this instant: the latest one at
                # least td old (it switches between samples, so td is
                # continuous, not a whole number of them)
                tau = t[i - 1] + (q + 1) * hh
                while j + 1 < n and t[j + 1] <= tau - td:
                    j += 1
                u = cmd[j] if t[j] <= tau - td else cmd[0]
                acc = wn * wn * (u - pos) - 2.0 * zeta * wn * vel
                vel += acc * hh
                pos += vel * hh
            if wheel < pos - half_lash:
                wheel = pos - half_lash
            elif wheel > pos + half_lash:
                wheel = pos + half_lash
        out[i] = wheel
    return out


def fit_steer(t, cmd, angle):
    ok = np.isfinite(t) & np.isfinite(cmd) & np.isfinite(angle)
    t, cmd, angle = t[ok], cmd[ok], angle[ok]

    def resid(p):
        td, wn, zeta, hl = p
        return servo_sim(t, cmd, td, wn, zeta, hl, angle[0]) - angle

    # the dead time: the best of a coarse grid first (the residual is
    # flat in it between switch times of a sampled log)
    best = None
    for td0 in np.arange(0.0, 0.05, 0.0025):
        e = resid([td0, 30.0, 0.7, 0.002])
        c = float(np.dot(e, e))
        if best is None or c < best[0]:
            best = (c, td0)
    r = least_squares(resid, [best[1], 30.0, 0.7, 0.002],
                      bounds=([0.0, 3.0, 0.1, 0.0], [0.1, 200.0, 2.0, 0.05]), x_scale=[0.005, 10, 0.2, 0.002],
                      diff_step=[1e-3, 1e-3, 1e-3, 1e-3])
    s = sigma(r)
    td, wn, zeta, hl = r.x
    fit = Fit('steer')
    # what the firmware predicts with: a first-order lag with the same mean delay
    lag = td + 2.0 * zeta / wn
    fit.params['steer_lag_s'] = (lag, math.hypot(s[0], 2.0 * math.hypot(s[2] / wn, zeta * s[1] / wn ** 2)))
    fit.params['steer_backlash_rad'] = (2.0 * hl, 2.0 * s[3])
    fit.extra['dead_time_s'] = (td, s[0])
    fit.extra['omega_n_rad_s'] = (wn, s[1])
    fit.extra['zeta'] = (zeta, s[2])
    fit.extra['angle_rms_rad'] = (float(np.sqrt(np.mean(r.fun ** 2))), None)
    fit.notes.append('twin: servoBandwidthHz = w_n / 2 pi = %.2f, servoDamping = %.2f, servoBacklashDeg = %.2f'
                     % (wn / (2 * math.pi), zeta, math.degrees(2 * hl)))
    fit.notes.append('the dead time includes the command latency (the twin: one 10 ms tick) and the PWM frame')
    fit.notes.append('steer_lag_s is the servo alone; the yaw response adds the tyres (FW-13 measured 40 ms on the twin)')
    return fit

# -------------------------------------------------------------- latency --

def fit_latency(t, cmd, resp, max_s=0.1):
    """The lag that best lines the response up with the command: the peak of
    the cross-correlation of their changes, refined by a parabola."""
    ok = np.isfinite(t) & np.isfinite(cmd) & np.isfinite(resp)
    t, cmd, resp = t[ok], cmd[ok], resp[ok]
    dt = float(np.median(np.diff(t)))
    tg = np.arange(t[0], t[-1], dt)
    c = np.interp(tg, t, cmd)
    q = np.interp(tg, t, resp)
    dc, dq = np.diff(c), np.diff(q)
    dc -= dc.mean(); dq -= dq.mean()
    nmax = int(round(max_s / dt))
    xc = np.array([np.dot(dc[:len(dc) - k], dq[k:]) for k in range(nmax + 1)])
    k = int(np.argmax(xc))
    frac = 0.0
    if 0 < k < nmax:
        y0, y1, y2 = xc[k - 1], xc[k], xc[k + 1]
        den = y0 - 2 * y1 + y2
        frac = 0.5 * (y0 - y2) / den if den != 0 else 0.0
    fit = Fit('latency')
    # A row's command is computed from that row's measurements, and a
    # command acting d after its tick shows in the first sample after
    # that: d lies in ((k - 1) dt, k dt]. The firmware's prediction counts
    # whole periods of commands in flight: k - 1 of them.
    fit.params['cmd_delay_s'] = (max(0.0, (k + frac - 1.0)) * dt, 0.5 * dt)
    fit.extra['peak_lag_samples'] = (k, None)
    fit.extra['sample_s'] = (dt, None)
    fit.notes.append('sampled at %.1f ms: a command held for a period shows its delay to the period;'
                     ' it acts between %.0f and %.0f ms after its tick' % (dt * 1e3, (k - 1) * dt * 1e3, k * dt * 1e3))
    fit.notes.append('cmd_delay_s in the parameters is beyond the drive\'s own lag: subtract it (--minus-ms)')
    return fit

# -------------------------------------------------------------- rollout --

def fit_rollout(counts, distance_m, counts_per_rev):
    rad = distance_m * counts_per_rev / (2.0 * math.pi * counts)
    fit = Fit('rollout')
    fit.params['wheel_radius_m'] = (rad, rad * 0.5 / abs(counts))
    fit.notes.append('%d counts over %.3f m at %g counts per wheel turn' % (counts, distance_m, counts_per_rev))
    return fit

# ------------------------------------------------------------------ imu --

def fit_imu(t, gyro, accel):
    fs = 1.0 / float(np.median(np.diff(t)))
    fit = Fit('imu')
    for i, ax in enumerate('xyz'):
        g = gyro[:, i]
        fit.extra['gyro_bias_' + ax] = (float(np.mean(g)), float(np.std(g) / math.sqrt(len(g))))
        fit.extra['gyro_noise_' + ax + '_dps_rtHz'] = (float(np.degrees(np.std(g)) / math.sqrt(fs / 2.0)), None)
    a = np.mean(accel, axis=0)
    fit.extra['accel_norm_mps2'] = (float(np.linalg.norm(a)), None)
    fit.extra['accel_scale_err'] = (float(np.linalg.norm(a) / G - 1.0), None)
    fit.extra['tilt_deg'] = (float(np.degrees(math.acos(min(1.0, abs(a[2]) / np.linalg.norm(a))))), None)
    for i, ax in enumerate('xyz'):
        fit.extra['accel_noise_' + ax + '_ug_rtHz'] = (float(np.std(accel[:, i]) / G * 1e6 / math.sqrt(fs / 2.0)), None)
    fit.notes.append('white noise assumed (std over sqrt(fs/2)); the Allan plot is the Unity ImuCheck / R9')
    return fit

# -------------------------------------------------------------- battery --

def fit_battery(t, volts, amps):
    ok = np.isfinite(t) & np.isfinite(volts) & np.isfinite(amps)
    t, volts, amps = t[ok], volts[ok], amps[ok]
    tt = (t - t[0]) / max(1e-9, t[-1] - t[0])
    A = np.vstack([np.ones_like(tt), tt, tt * tt, -amps]).T
    p, res, *_ = np.linalg.lstsq(A, volts, rcond=None)
    e = volts - A @ p
    s2 = float(np.sum(e * e) / max(1, len(e) - 4))
    cov = np.linalg.inv(A.T @ A) * s2
    fit = Fit('battery')
    fit.extra['r_int_ohm'] = (float(p[3]), float(math.sqrt(cov[3, 3])))
    fit.extra['ocv_start_v'] = (float(p[0]), None)
    fit.extra['ocv_end_v'] = (float(p[0] + p[1] + p[2]), None)
    fit.notes.append('twin: BatterySpec.internalR; the firmware has no field for it yet')
    return fit

# --------------------------------------------------------------- write --

def write_params(path, fit, log_name, today=None):
    """Put the fit's values into a parameter file, keeping its layout: each
    value line, its "_<field>" note, and "_provenance"."""
    today = today or datetime.date.today().isoformat()
    text = open(path, encoding='utf-8').read()
    data = json.loads(text)
    prov = dict(data.get('_provenance', {}))
    for field, (val, sd) in fit.params.items():
        if field not in data:
            print('  (%s not in %s: not written)' % (field, os.path.basename(path)))
            continue
        old = data[field]
        new = float('%.6g' % val)
        m = re.search(r'("%s":\s*)([-+0-9.eE]+)' % re.escape(field), text)
        if not m:
            raise SystemExit('cannot find the value of %s' % field)
        text = text[:m.start(2)] + repr(new) + text[m.end(2):]
        was = data.get('_' + field)
        note = ('MEASURED %s by Tools/sysid.py %s on %s: %.6g +- %.2g; was %s%s'
                % (today, fit.test, log_name, val, sd if sd is not None else float('nan'), old,
                   (' (' + was + ')') if was else ''))
        note_json = json.dumps(note, ensure_ascii=False)
        mn = re.search(r'("_%s":\s*)("(?:[^"\\]|\\.)*")' % re.escape(field), text)
        if mn:
            text = text[:mn.start(2)] + note_json + text[mn.end(2):]
        else:
            m = re.search(r'("%s":\s*[-+0-9.eE]+)(,?)' % re.escape(field), text)
            indent = re.search(r'\n([ \t]*)"%s"' % re.escape(field), text).group(1)
            ins = '%s,\n%s"_%s": %s%s' % (m.group(1), indent, field, note_json, m.group(2))
            text = text[:m.start()] + ins + text[m.end():]
        prov[field] = 'measured: %s %s, %s' % (fit.test, log_name, today)
        print('  %s: %s -> %r' % (field, old, new))
    # the provenance map, last in the file
    prov_json = json.dumps(prov, indent=4, ensure_ascii=False).replace('\n', '\n  ')
    if '"_provenance"' in text:
        text = re.sub(r'"_provenance":\s*\{[^{}]*\}', '"_provenance": ' + prov_json, text)
    else:
        k = text.rstrip().rfind('}')
        body = text[:k].rstrip()
        text = body + ',\n\n  "_provenance": ' + prov_json + '\n}\n'
    json.loads(text)       # still valid
    open(path, 'w', encoding='utf-8', newline='\n').write(text)

# ------------------------------------------------------------- selftest --

def selftest():
    rng = np.random.default_rng(7)
    fails = 0

    def check(name, got, want, tol):
        nonlocal fails
        ok = abs(got - want) <= tol
        print('%s %-44s %10.5g (want %.5g +- %.3g)' % ('PASS' if ok else 'FAIL', name, got, want, tol))
        if not ok:
            fails += 1

    # coast-down: two stretches from 4 and 2.5 m/s, 100 Hz, 1 cm/s noise
    m_eff, c = 2.315, (1.43, 0.156, 0.0028)
    ts, vs, ds, t0 = [], [], [], 0.0
    for v0 in (4.0, 2.5):
        tt = np.arange(0, 3.0, 0.01)
        v = np.empty_like(tt); v[0] = v0
        for i in range(1, len(tt)):
            x = v[i - 1]
            v[i] = x - 0.01 * (c[0] + c[1] * x + c[2] * x * x) / m_eff
        ts.append(tt + t0); vs.append(v + rng.normal(0, 0.01, len(v))); ds.append(np.zeros_like(tt))
        ts.append(tt[:50] + t0 + 3.0); vs.append(np.full(50, v[-1])); ds.append(np.ones(50))   # driven between
        t0 += 3.5
    f = fit_coastdown(np.concatenate(ts), np.concatenate(vs), np.concatenate(ds), m_eff)
    check('coastdown drag_c0', f.params['drag_c0'][0], c[0], 0.05 * c[0])
    check('coastdown drag_c1', f.params['drag_c1'][0], c[1], 0.03)

    # cornering: circles at a few speeds and radii, with the twin's sideslip
    k_true, l_r = 16.3, 0.15
    T, X, Y, P, V, R = [], [], [], [], [], []
    t0 = 0.0
    for v, rad in ((1.0, 4.0), (2.0, 4.0), (3.0, 4.0), (2.0, 2.0), (1.5, 1.5)):
        tt = np.arange(0, 6.0, 0.01)
        r = v / rad
        beta = math.asin(l_r / rad) - v * r / (G * k_true)
        course = r * tt
        X.append(rad * np.sin(course)); Y.append(rad * (1 - np.cos(course)))
        P.append(course - beta + rng.normal(0, 0.002, len(tt)))
        V.append(np.full(len(tt), v)); R.append(np.full(len(tt), r) + rng.normal(0, 0.003, len(tt)))
        T.append(tt + t0); t0 += 6.0 + 0.01
    # positions with UWB-class noise would need the EKF; a little noise on truth
    smp = cornering_samples(np.concatenate(T), np.concatenate(X) + rng.normal(0, 0.0005, sum(map(len, X))),
                            np.concatenate(Y) + rng.normal(0, 0.0005, sum(map(len, Y))),
                            np.concatenate(P), np.concatenate(V), np.concatenate(R))
    f = fit_cornering(*smp, l_r)
    check('cornering tyre_c_alpha (l_r known)', f.params['tyre_c_alpha'][0], k_true, 0.1 * k_true)
    f = fit_cornering(*smp, 0.0, fit_l_r=True)
    check('cornering tyre_c_alpha (l_r fitted)', f.params['tyre_c_alpha'][0], k_true, 0.1 * k_true)
    check('cornering rear axle (m)', f.extra['rear_axle_behind_m'][0], l_r, 0.01)

    # steer: steps and reversals through the twin's servo
    tt = np.arange(0, 8.0, 0.01)
    cmd = np.zeros_like(tt)
    for t_on, val in ((0.5, 0.05), (1.5, -0.03), (2.5, 0.1), (3.5, 0.0), (4.5, -0.08), (5.5, 0.02), (6.5, -0.02)):
        cmd[tt >= t_on] = val
    wn, zeta, td, hl = 2 * math.pi * 6.0, 0.6, 0.0025, 0.5 * math.radians(0.5)
    ang = servo_sim(tt, cmd, td, wn, zeta, hl, 0.0) + rng.normal(0, 0.0003, len(tt))
    f = fit_steer(tt, cmd, ang)
    check('steer omega_n (rad/s)', f.extra['omega_n_rad_s'][0], wn, 0.05 * wn)
    check('steer zeta', f.extra['zeta'][0], zeta, 0.05)
    check('steer backlash (rad)', f.params['steer_backlash_rad'][0], 2 * hl, 0.002)

    # latency: a new command every 10 ms tick, held, acting 31.5 ms later:
    # sampled at the ticks it shows from the fourth tick on (40 ms)
    tt = np.arange(0, 10.0, 0.01)
    cmd = rng.normal(0, 1, len(tt))
    resp = np.concatenate([np.zeros(4), cmd[:-4]]) + rng.normal(0, 0.05, len(tt))
    f = fit_latency(tt, cmd, resp)
    check('latency (ms, to the period)', f.params['cmd_delay_s'][0] * 1e3, 30.0, 3.0)

    # rollout: ten wheel turns of a 0.033 m wheel at 20 480 counts per turn
    f = fit_rollout(10 * 20480, 10 * 2 * math.pi * 0.033, 20480)
    check('rollout wheel_radius_m', f.params['wheel_radius_m'][0], 0.033, 1e-9)

    # battery: OCV falling, pulses of current, R 20 mOhm
    tt = np.arange(0, 60.0, 0.01)
    amps = 2.0 + 8.0 * ((tt % 5.0) < 2.0)
    volts = 8.2 - 0.004 * tt - 0.020 * amps + rng.normal(0, 0.002, len(tt))
    f = fit_battery(tt, volts, amps)
    check('battery r_int_ohm', f.extra['r_int_ohm'][0], 0.020, 0.001)

    # imu: still, biased, white noise
    tt = np.arange(0, 20.0, 0.005)
    gyro = rng.normal(0, 0.003, (len(tt), 3)) + np.array([0.001, -0.002, 0.0005])
    acc = rng.normal(0, 0.02, (len(tt), 3)) + np.array([0.0, 0.0, G])
    f = fit_imu(tt, gyro, acc)
    check('imu gyro_bias_y', f.extra['gyro_bias_y'][0], -0.002, 0.0003)
    check('imu accel norm', f.extra['accel_norm_mps2'][0], G, 0.005)

    # write-back keeps the file valid and its layout
    import tempfile
    src = ('{\n  "name": "x",\n  "version": 5,\n\n  "drag_c0": 1.4,\n  "_drag_c0": "old note",\n'
           '  "drag_c1": 0.1,\n  "tyre_c_alpha": 16.3\n}\n')
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, 'p.json')
        open(p, 'w', encoding='utf-8').write(src)
        fk = Fit('coastdown'); fk.params['drag_c0'] = (1.5, 0.01); fk.params['drag_c1'] = (0.2, 0.01)
        write_params(p, fk, 'log.csv', '2026-01-01')
        j = json.load(open(p, encoding='utf-8'))
        ok = (j['drag_c0'] == 1.5 and j['drag_c1'] == 0.2 and j['tyre_c_alpha'] == 16.3 and
              j['_drag_c0'].startswith('MEASURED 2026-01-01') and 'was 1.4 (old note)' in j['_drag_c0'] and
              '_drag_c1' in j and j['_provenance']['drag_c1'].startswith('measured'))
        print('%s %-44s' % ('PASS' if ok else 'FAIL', 'write-back: values, notes, provenance'))
        if not ok:
            fails += 1
    print('selftest: %s' % ('ok' if fails == 0 else '%d FAILED' % fails))
    return fails

# ------------------------------------------------------------------ cli --

def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('test', choices=['coastdown', 'cornering', 'steer', 'latency', 'rollout', 'imu',
                                     'battery', 'selftest'])
    ap.add_argument('log', nargs='?')
    ap.add_argument('more', nargs='*', help='cornering: more logs (circles at other speeds)')
    ap.add_argument('--fit-l-r', action='store_true', help='cornering: fit the rear axle\'s place too')
    ap.add_argument('--t', default='', help='time column (default: time or t)')
    ap.add_argument('--v', default='', help='speed column')
    ap.add_argument('--drive', default='', help='drive command column (coastdown: |x| < eps is coasting)')
    ap.add_argument('--m-eff', type=float, default=0.0, help='effective mass, kg (coastdown)')
    ap.add_argument('--params', default='', help='parameter file to read m_eff, wheelbase from')
    ap.add_argument('--fix-c2', type=float, default=None)
    ap.add_argument('--v-min', type=float, default=0.3)
    ap.add_argument('--cmd', default='', help='command column (steer, latency)')
    ap.add_argument('--resp', default='', help='response column (steer angle, latency)')
    ap.add_argument('--minus-ms', type=float, default=0.0, help='latency: the drive\'s own lag to take off')
    ap.add_argument('--counts', type=float, default=0.0)
    ap.add_argument('--distance', type=float, default=0.0)
    ap.add_argument('--cpr', type=float, default=0.0)
    ap.add_argument('--l-r', type=float, default=0.0, help='rear axle behind the origin, m (cornering)')
    ap.add_argument('--from', dest='t_from', type=float, default=None, help='use the log from this time')
    ap.add_argument('--to', dest='t_to', type=float, default=None, help='...up to this time')
    ap.add_argument('--write', default='', help='parameter JSON to write the fit into')
    a = ap.parse_args()

    if a.test == 'selftest':
        sys.exit(1 if selftest() else 0)

    prm = json.load(open(a.params, encoding='utf-8')) if a.params else {}
    if a.test == 'rollout':
        cpr = a.cpr or (prm.get('enc_cpr', [0])[0] * prm.get('enc_ratio', [1])[0])
        fit = fit_rollout(a.counts, a.distance, cpr)
        log_name = 'roll-out %.3f m' % a.distance
    else:
        if not a.log:
            ap.error('a log is needed')
        log = read_log(a.log)
        log_name = os.path.basename(a.log)
        t = pick(log, a.t, 'time', 't')
        sel = np.ones(len(t), bool)
        if a.t_from is not None: sel &= t >= a.t_from
        if a.t_to is not None: sel &= t <= a.t_to
        log = {k: v[sel] for k, v in log.items()}
        t = t[sel]
        if a.test == 'coastdown':
            m_eff = a.m_eff or prm.get('mass_eff_kg', 0.0)
            if not m_eff:
                ap.error('coastdown needs --m-eff or --params')
            v = pick(log, a.v, 'veh/speed', 'v', 'dbg/v_est', 'dbg/v_meas')
            drv = pick(log, a.drive, 'iq', 'dbg/i_cmd', 'drive')
            fit = fit_coastdown(t, v, drv, m_eff, a.v_min, a.fix_c2)
        elif a.test == 'cornering':
            l_r = a.l_r or 0.5 * prm.get('wheelbase_m', 0.0)
            if not l_r and not a.fit_l_r:
                ap.error('cornering needs --l-r, --params or --fit-l-r')
            parts = []
            for i, lg in enumerate([log] + [read_log(p) for p in a.more]):
                tt = t if i == 0 else pick(lg, a.t, 'time', 't')
                # truth (sim) or the EKF (car): x, y in the site frame, heading + = left
                if 'veh/pos_x' in lg:
                    x, y = lg['veh/pos_z'], -lg['veh/pos_x']
                    psi = -np.radians(lg['veh/yaw_deg'])
                    v = lg['veh/speed']
                else:
                    x, y = pick(lg, 'dbg/nav_x'), pick(lg, 'dbg/nav_y')
                    psi = np.radians(pick(lg, 'dbg/nav_psi_deg'))
                    v = pick(lg, 'dbg/v_est')
                r = pick(lg, 'sens/imu/gz', 'imu/gyro_z', 'dbg/yaw_rate')
                parts.append(cornering_samples(tt, x, y, psi, v, r))
            smp = [np.concatenate([p[j] for p in parts]) for j in range(3)]
            fit = fit_cornering(*smp, l_r, fit_l_r=a.fit_l_r)
            if a.more:
                log_name += ' + ' + ', '.join(os.path.basename(p) for p in a.more)
        elif a.test == 'steer':
            fit = fit_steer(t, pick(log, a.cmd, 'dbg/steer_cmd'), pick(log, a.resp, 'sens/steer_angle/angle_rad', 'sens/steer/angle'))
        elif a.test == 'latency':
            fit = fit_latency(t, pick(log, a.cmd, 'dbg/i_cmd'), pick(log, a.resp, 'sens/motor_wheel_fl/iq'))
            if a.minus_ms:
                v, s = fit.params['cmd_delay_s']
                fit.params['cmd_delay_s'] = (v - a.minus_ms * 1e-3, s)
        elif a.test == 'imu':
            gy = np.vstack([pick(log, 'sens/imu/gx'), pick(log, 'sens/imu/gy'), pick(log, 'sens/imu/gz')]).T
            ac = np.vstack([pick(log, 'sens/imu/ax'), pick(log, 'sens/imu/ay'), pick(log, 'sens/imu/az')]).T
            ok = np.all(np.isfinite(gy), axis=1) & np.all(np.isfinite(ac), axis=1)
            fit = fit_imu(t[ok], gy[ok], ac[ok])
        elif a.test == 'battery':
            fit = fit_battery(t, pick(log, 'sens/pack_2s/volt', 'dbg/batt_v', 'v'),
                              pick(log, 'sens/pack_2s/amps', 'i'))
    fit.show()
    if a.write:
        write_params(a.write, fit, log_name)
        print('written to %s: now run  node Tools/gen_params.js' % a.write)


if __name__ == '__main__':
    main()
