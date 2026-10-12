#!/usr/bin/env node
/*
 * gen_route.js — route JSON -> firmware tables and the sim's copy (NAV-01).
 *
 *   node Tools/gen_route.js            regenerate from every Controllers/routes/*.json
 *   node Tools/gen_route.js --check    exit 1 if a generated file is stale (CI)
 *   node Tools/gen_route.js --list     print each route's length, bounds and CRC
 *
 * A route is designed as a chain of segments from a start pose, in the site
 * frame (the UWB anchors' frame: x, y metres, heading + = left / CCW):
 *
 *   { "line": 14.5 }                                straight, metres
 *   { "arc_deg": 90, "radius": 4 }                  constant curvature; + = left
 *   { "spiral_m": 1.0, "k1": 0.25 }                 clothoid: curvature linear from
 *                                                   where it is to k1 (1/m)
 *   { "turn_deg": 180, "radius": 4, "spiral_m": 1 } spiral in, arc, spiral out;
 *                                                   heading changes by turn_deg
 *
 * Any segment may carry "v_cap" (m/s) for the points on it. The route is
 * resampled every `spacing_m` along its length with the design's own
 * heading and curvature (never differenced from the points), and written to
 *
 *   Controllers/core/routes_gen.c                     every route, const tables
 *   UnitySim/Assets/Resources/Routes/<name>.json      the same points, for the
 *                                                     sim's scorer
 *
 * Both are committed, so building firmware never needs Node. Floats are
 * printed with 9 significant digits, which a C compiler reads back to the
 * same float32 this script computed the CRC over.
 */
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const SRC_DIR = path.join(ROOT, 'Controllers', 'routes');
const OUT_C = path.join(ROOT, 'Controllers', 'core', 'routes_gen.c');
const OUT_SIM = path.join(ROOT, 'UnitySim', 'Assets', 'Resources', 'Routes');

const MAX_PTS = 1024;          // TT_ROUTE_MAX_PTS
const KAPPA_MAX = 1.5;         // 1/m: the twin turns no tighter than ~0.56 m
const DEG = Math.PI / 180;
/* float32, and never -0: C prints both zeros the same, the CRC would not. */
const f32 = v => { const r = Math.fround(v); return r === 0 ? 0 : r; };

// ---- geometry ---------------------------------------------------------------

/* One piece of constant or linearly changing curvature. */
function piece(len, k0, k1, vcap) {
    if (!(len > 0)) throw new Error('a segment needs a positive length');
    return { len, k0, k1, vcap };
}

/* The design's segments -> pieces of linear curvature. */
function pieces(route) {
    const out = [];
    let k = 0;      // curvature where the last piece ended
    route.segments.forEach((seg, i) => {
        const where = `${route.name}: segment ${i}`;
        const vcap = seg.v_cap || 0;
        const keys = Object.keys(seg).filter(x => !x.startsWith('_') && x !== 'v_cap');
        const has = n => keys.includes(n);
        const only = (...ks) => {
            for (const x of keys) if (!ks.includes(x)) throw new Error(`${where}: unknown key '${x}'`);
        };
        if (has('line')) {
            only('line');
            if (k !== 0) throw new Error(`${where}: a line must follow zero curvature (add a spiral)`);
            out.push(piece(seg.line, 0, 0, vcap));
        } else if (has('arc_deg')) {
            only('arc_deg', 'radius');
            const sgn = Math.sign(seg.arc_deg), kk = sgn / seg.radius;
            if (!(seg.radius > 0) || sgn === 0) throw new Error(`${where}: arc needs radius > 0 and a nonzero angle`);
            if (Math.abs(kk - k) > 1e-9) throw new Error(`${where}: arc curvature ${kk} does not continue ${k} (add a spiral)`);
            out.push(piece(Math.abs(seg.arc_deg) * DEG * seg.radius, kk, kk, vcap));
        } else if (has('turn_deg')) {
            only('turn_deg', 'radius', 'spiral_m');
            const a = Math.abs(seg.turn_deg) * DEG, sgn = Math.sign(seg.turn_deg), r = seg.radius;
            const ls = seg.spiral_m || 0;
            if (!(r > 0) || sgn === 0) throw new Error(`${where}: turn needs radius > 0 and a nonzero angle`);
            if (k !== 0) throw new Error(`${where}: a turn must start at zero curvature`);
            const arc = r * a - ls;          // two spirals turn ls / r between them
            if (arc < -1e-9) throw new Error(`${where}: spirals of ${ls} m turn more than ${seg.turn_deg} deg`);
            if (ls > 0) out.push(piece(ls, 0, sgn / r, vcap));
            if (arc > 1e-9) out.push(piece(arc, sgn / r, sgn / r, vcap));
            if (ls > 0) out.push(piece(ls, sgn / r, 0, vcap));
            k = 0;
            return;
        } else if (has('spiral_m')) {
            only('spiral_m', 'k1');
            out.push(piece(seg.spiral_m, k, seg.k1, vcap));
            k = seg.k1;
            return;
        } else {
            throw new Error(`${where}: needs line, arc_deg, spiral_m or turn_deg`);
        }
        k = out[out.length - 1].k1;
    });
    if (k !== 0 && route.stop) throw new Error(`${route.name}: ends on a curve; add a spiral to zero`);
    return out;
}

/* State u metres into a piece that starts at st = {x, y, psi}. Heading is
 * exact; position by Simpson's rule on a fine grid (closed form on lines and
 * arcs). */
function along(p, st, u) {
    const dk = (p.k1 - p.k0) / p.len;
    const psiAt = t => st.psi + p.k0 * t + 0.5 * dk * t * t;
    const kappa = p.k0 + dk * u;
    const psi = psiAt(u);
    if (dk === 0 && p.k0 === 0)
        return { x: st.x + u * Math.cos(st.psi), y: st.y + u * Math.sin(st.psi), psi, kappa };
    if (dk === 0)
        return { x: st.x + (Math.sin(psi) - Math.sin(st.psi)) / p.k0,
                 y: st.y - (Math.cos(psi) - Math.cos(st.psi)) / p.k0, psi, kappa };
    const n = Math.max(2, 2 * Math.ceil(u / 0.002));
    const h = u / n;
    let sx = 0, sy = 0;
    for (let i = 0; i <= n; i++) {
        const w = (i === 0 || i === n) ? 1 : (i % 2 ? 4 : 2), a = psiAt(i * h);
        sx += w * Math.cos(a); sy += w * Math.sin(a);
    }
    return { x: st.x + sx * h / 3, y: st.y + sy * h / 3, psi, kappa };
}

function build(route) {
    const ps = pieces(route);
    const spacing = route.spacing_m || 0.1;
    const st0 = { x: route.start.x, y: route.start.y, psi: route.start.psi_deg * DEG };
    // piece start states
    const starts = [];
    let st = st0, total = 0;
    for (const p of ps) {
        starts.push({ st, s0: total });
        const e = along(p, st, p.len);
        st = { x: e.x, y: e.y, psi: e.psi };
        total += p.len;
    }
    const n = Math.round(total / spacing) + 1;
    if (n > MAX_PTS) throw new Error(`${route.name}: ${n} points > ${MAX_PTS}; raise spacing_m`);
    const pts = [];
    let j = 0;
    for (let i = 0; i < n; i++) {
        const s = i === n - 1 ? total : i * total / (n - 1);
        while (j < ps.length - 1 && s > starts[j].s0 + ps[j].len) j++;
        const u = Math.min(ps[j].len, Math.max(0, s - starts[j].s0));
        const e = along(ps[j], starts[j].st, u);
        pts.push({ x: e.x, y: e.y, s, psi: e.psi, kappa: e.kappa, v_cap: ps[j].vcap });
    }
    return { pts, total };
}

// ---- the route record -------------------------------------------------------

function load(file) {
    const r = JSON.parse(fs.readFileSync(file, 'utf8'));
    const where = path.basename(file);
    const known = ['name', 'id', 'track', 'spacing_m', 'start', 'v_max', 'a_lat_max',
                   'a_drive', 'a_brake', 'stop', 'segments'];
    for (const k of Object.keys(r))
        if (!k.startsWith('_') && !known.includes(k)) throw new Error(`${where}: unknown key '${k}'`);
    if (!/^[a-z0-9_]+$/.test(r.name || '')) throw new Error(`${where}: "name" must be lower_snake`);
    if (path.basename(file, '.json') !== r.name) throw new Error(`${where}: file name must be the route name`);
    if (!Number.isInteger(r.id) || r.id <= 0) throw new Error(`${where}: "id" must be a positive integer`);
    if (!r.start || ![r.start.x, r.start.y, r.start.psi_deg].every(Number.isFinite))
        throw new Error(`${where}: "start" needs x, y, psi_deg`);
    for (const k of ['v_max', 'a_lat_max', 'a_drive', 'a_brake'])
        if (!(r[k] > 0)) throw new Error(`${where}: "${k}" must be > 0`);
    if (!Array.isArray(r.segments) || r.segments.length === 0) throw new Error(`${where}: no segments`);
    r.stop = r.stop !== false;
    return r;
}

/* Float32 everything the CRC covers, exactly as the C tables hold it. */
function frozen(route, geo) {
    return {
        name: route.name, id: route.id, track: route.track || '',
        n: geo.pts.length,
        pts: geo.pts.map(p => [p.x, p.y, p.s, p.psi, p.kappa, p.v_cap].map(f32)),
        start: [route.start.x, route.start.y, route.start.psi_deg * DEG].map(f32),
        has_start: 1, stop_at_end: route.stop ? 1 : 0,
        v_max: f32(route.v_max), a_lat_max: f32(route.a_lat_max),
        a_drive: f32(route.a_drive), a_brake: f32(route.a_brake),
        length_m: geo.total,
    };
}

const CRC_TABLE = (() => {
    const t = new Uint32Array(256);
    for (let i = 0; i < 256; i++) {
        let c = i;
        for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
        t[i] = c >>> 0;
    }
    return t;
})();

function crc32(bytes) {
    let c = 0xFFFFFFFF;
    for (const b of bytes) c = CRC_TABLE[(c ^ b) & 0xFF] ^ (c >>> 8);
    return (c ^ 0xFFFFFFFF) >>> 0;
}

/* tt_route_crc()'s byte stream. */
function routeCrc(fr) {
    const floats = [];
    for (const p of fr.pts) floats.push(...p);
    floats.push(...fr.start);
    const buf = Buffer.alloc(4 + floats.length * 4 + 2 + 16);
    let o = buf.writeUInt32LE(fr.n, 0);
    for (const v of floats) o = buf.writeFloatLE(v, o);
    o = buf.writeUInt8(fr.has_start, o);
    o = buf.writeUInt8(fr.stop_at_end, o);
    for (const v of [fr.v_max, fr.a_lat_max, fr.a_drive, fr.a_brake]) o = buf.writeFloatLE(v, o);
    return crc32(buf);
}

/* A float32 that a C compiler reads back bit for bit. */
function cf(v) {
    if (!Number.isFinite(v)) throw new Error('not finite: ' + v);
    if (f32(v) === 0) return '0.0f';
    let s = f32(v).toPrecision(9);
    if (!/[.e]/.test(s)) s += '.0';
    return s + 'f';
}

function validate(fr) {
    const p = fr.pts, err = m => { throw new Error(`${fr.name}: ${m}`); };
    if (fr.n < 2) err('fewer than two points');
    if (p[0][2] !== 0) err('s does not start at 0');
    for (let i = 1; i < fr.n; i++) {
        const ds = p[i][2] - p[i - 1][2];
        const d = Math.hypot(p[i][0] - p[i - 1][0], p[i][1] - p[i - 1][1]);
        if (!(ds > 0)) err(`s not increasing at ${i}`);
        if (Math.abs(d - ds) > 0.01 * ds + 0.001) err(`chord ${d} vs ds ${ds} at ${i}`);
        if (Math.abs(p[i][4]) > KAPPA_MAX) err(`curvature ${p[i][4]} beyond ${KAPPA_MAX} at ${i}`);
    }
}

function summary(fr) {
    const xs = fr.pts.map(p => p[0]), ys = fr.pts.map(p => p[1]);
    const kmax = Math.max(...fr.pts.map(p => Math.abs(p[4])));
    const e = fr.pts[fr.n - 1];
    return `${fr.name.padEnd(10)} id ${fr.id}  ${fr.length_m.toFixed(2)} m, ${fr.n} pts, ` +
        `x ${Math.min(...xs).toFixed(2)}..${Math.max(...xs).toFixed(2)}, ` +
        `y ${Math.min(...ys).toFixed(2)}..${Math.max(...ys).toFixed(2)}, |k| <= ${kmax.toFixed(3)}, ` +
        `end (${e[0].toFixed(2)}, ${e[1].toFixed(2)}, ${(e[3] / DEG).toFixed(1)} deg), ` +
        `crc ${fr.crc.toString(16).padStart(8, '0')}`;
}

// ---- output -----------------------------------------------------------------

function cText(routes) {
    const L = [
        '/* GENERATED by Tools/gen_route.js from the route files in Controllers/routes/ — do not edit. */',
        '#include "tt_route.h"',
        '',
    ];
    for (const fr of routes) {
        L.push(`/* ${fr.name}: ${fr.length_m.toFixed(3)} m, ${fr.n} points. x, y, s, psi, kappa, v_cap */`);
        L.push(`static const TtRoutePt route_${fr.name}_pts[${fr.n}] = {`);
        for (const p of fr.pts) L.push('    { ' + p.map(cf).join(', ') + ' },');
        L.push('};');
        L.push(`const TtRoute tt_route_${fr.name} = {`);
        L.push(`    "${fr.name}", ${fr.id}u, 0x${fr.crc.toString(16).toUpperCase().padStart(8, '0')}u, ${fr.n}u, route_${fr.name}_pts,`);
        L.push(`    { ${fr.start.map(cf).join(', ')} }, ${fr.has_start}u, ${fr.stop_at_end}u, { 0u, 0u },`);
        L.push(`    ${cf(fr.v_max)}, ${cf(fr.a_lat_max)}, ${cf(fr.a_drive)}, ${cf(fr.a_brake)}`);
        L.push('};');
        L.push('');
    }
    L.push('const TtRoute *const tt_routes[] = {');
    for (const fr of routes) L.push(`    &tt_route_${fr.name},`);
    L.push('    0', '};', '');
    return L.join('\n');
}

function simText(fr) {
    // Plain numbers (the float32 values), one point per line, for the scorer.
    const n = v => Number(f32(v).toPrecision(9));
    const head = {
        _about: 'GENERATED by Tools/gen_route.js from Controllers/routes/' + fr.name + '.json — do not edit',
        name: fr.name, id: fr.id, crc: fr.crc, track: fr.track, length_m: n(fr.length_m),
        start: fr.start.map(n), stop_at_end: fr.stop_at_end,
        v_max: n(fr.v_max), a_lat_max: n(fr.a_lat_max), a_drive: n(fr.a_drive), a_brake: n(fr.a_brake),
    };
    const lines = JSON.stringify(head, null, 2).split('\n');
    lines.pop();   // the closing brace
    lines[lines.length - 1] += ',';
    lines.push('  "pts": [');
    fr.pts.forEach((p, i) => lines.push('    [' + p.map(n).join(', ') + ']' + (i < fr.n - 1 ? ',' : '')));
    lines.push('  ]', '}', '');
    return lines.join('\n');
}

function generateAll() {
    const files = fs.readdirSync(SRC_DIR).filter(f => f.endsWith('.json')).sort();
    const routes = [];
    const ids = new Map();
    for (const f of files) {
        const route = load(path.join(SRC_DIR, f));
        if (ids.has(route.id)) throw new Error(`route id ${route.id} used by ${ids.get(route.id)} and ${route.name}`);
        ids.set(route.id, route.name);
        const fr = frozen(route, build(route));
        validate(fr);
        fr.crc = routeCrc(fr);
        routes.push(fr);
    }
    routes.sort((a, b) => a.id - b.id);
    const out = [[OUT_C, cText(routes)]];
    for (const fr of routes) out.push([path.join(OUT_SIM, fr.name + '.json'), simText(fr)]);
    return { routes, out };
}

function main() {
    const check = process.argv.includes('--check');
    const { routes, out } = generateAll();
    if (process.argv.includes('--list')) { for (const fr of routes) console.log(summary(fr)); return; }
    let stale = 0;
    for (const [file, text] of out) {
        const old = fs.existsSync(file) ? fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n') : null;
        const rel = path.relative(ROOT, file);
        if (old === text) { console.log('up to date: ' + rel); continue; }
        if (check) { console.error('STALE: ' + rel + ' (run node Tools/gen_route.js)'); stale++; continue; }
        fs.mkdirSync(path.dirname(file), { recursive: true });
        fs.writeFileSync(file, text);
        console.log('wrote ' + rel);
    }
    process.exit(stale ? 1 : 0);
}

if (require.main === module) main();
module.exports = { generateAll, routeCrc, crc32, cf };
