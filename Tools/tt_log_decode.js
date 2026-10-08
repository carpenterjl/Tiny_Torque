#!/usr/bin/env node
/*
 * tt_log_decode.js — a car's binary log -> the sim's CSV columns (FW-09).
 *
 *   node Tools/tt_log_decode.js <channels.def> <log.bin> [out.csv]
 *   node Tools/tt_log_decode.js <channels.def> --names      the sim's debug-name string
 *   node Tools/tt_log_decode.js --selftest
 *
 * The MCU writes one TtLogFrame per tick (Controllers/core/tt_log.h):
 *
 *   uint32 magic ("TLTG"), uint32 seq, uint32 t_us, uint32 n,
 *   uint32 params_hash, float32 v[32]                 little-endian, 148 bytes
 *
 * and the channel names come from the same X-macro table the firmware was
 * built with (e.g. Controllers/opus_mission/opus_log.def). The CSV gets the
 * columns the simulator's own telemetry uses — `time` in seconds, then
 * dbg/<name> — so the Telemetry Analyzer overlays a real run on a sim run
 * without a mapping table. `t_us` wraps every ~71 minutes; the decoder
 * unwraps it.
 */
'use strict';
const fs = require('fs');

const MAGIC = 0x47544C54;
const LOG_MAX = 32;
const FRAME_BYTES = 5 * 4 + LOG_MAX * 4;

/** Channel names, in order, from TT_LOG(name, unit, desc) rows. */
function readDef(text) {
    const names = [];
    const noComments = text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
    const re = /TT_LOG\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,/g;
    let m;
    while ((m = re.exec(noComments)) !== null) names.push(m[1]);
    return names;
}

/** Decode a buffer of frames. Returns { rows, hashes, skipped }. */
function decode(buf, names) {
    const rows = [];
    const hashes = new Set();
    let skipped = 0, prevT = null, wraps = 0;
    for (let off = 0; off + FRAME_BYTES <= buf.length; ) {
        if (buf.readUInt32LE(off) !== MAGIC) { off += 4; skipped++; continue; }   // resync
        const seq = buf.readUInt32LE(off + 4);
        const tUs = buf.readUInt32LE(off + 8);
        const n = Math.min(buf.readUInt32LE(off + 12), LOG_MAX);
        hashes.add(buf.readUInt32LE(off + 16));
        if (prevT !== null && tUs < prevT) wraps++;
        prevT = tUs;
        const v = [];
        for (let i = 0; i < names.length; i++)
            v.push(i < n ? buf.readFloatLE(off + 20 + 4 * i) : NaN);
        rows.push({ seq, time: (wraps * 4294967296 + tUs) / 1e6, v });
        off += FRAME_BYTES;
    }
    return { rows, hashes: [...hashes], skipped };
}

function toCsv(names, rows) {
    const out = ['time,' + names.map(n => 'dbg/' + n).join(',')];
    for (const r of rows)
        out.push(r.time.toFixed(6) + ',' + r.v.map(x => (Number.isFinite(x) ? String(+x.toPrecision(7)) : '')).join(','));
    return out.join('\n') + '\n';
}

function selftest() {
    const names = readDef('/* c */\nTT_LOG(state, "", "x")\nTT_LOG(odo_m, "m", "y")\n// TT_LOG(no, "", "")\n');
    const frames = [[1, 1000, [3, 0.5]], [2, 4294967000, [4, 1.5]], [3, 200, [5, 2.5]]];   // wraps once
    const buf = Buffer.alloc(frames.length * FRAME_BYTES);
    frames.forEach(([seq, t, v], k) => {
        const o = k * FRAME_BYTES;
        buf.writeUInt32LE(MAGIC, o); buf.writeUInt32LE(seq, o + 4); buf.writeUInt32LE(t, o + 8);
        buf.writeUInt32LE(v.length, o + 12); buf.writeUInt32LE(0xABCD1234, o + 16);
        v.forEach((x, i) => buf.writeFloatLE(x, o + 20 + 4 * i));
    });
    const d = decode(buf, names);
    const ok = names.join() === 'state,odo_m' && d.rows.length === 3 && d.skipped === 0 &&
        d.rows[2].v[1] === 2.5 && d.rows[2].time > d.rows[1].time && d.hashes[0] === 0xABCD1234 &&
        toCsv(names, d.rows).startsWith('time,dbg/state,dbg/odo_m\n');
    console.log(ok ? 'tt_log_decode selftest: ok' : 'tt_log_decode selftest: FAILED');
    return ok;
}

function main() {
    const a = process.argv.slice(2);
    if (a[0] === '--selftest') process.exit(selftest() ? 0 : 1);
    if (a.length < 2) {
        console.error('usage: tt_log_decode.js <channels.def> <log.bin> [out.csv] | <channels.def> --names | --selftest');
        process.exit(2);
    }
    const names = readDef(fs.readFileSync(a[0], 'utf8'));
    if (a[1] === '--names') { console.log(names.join(',')); return; }
    const d = decode(fs.readFileSync(a[1]), names);
    const csv = toCsv(names, d.rows);
    if (a[2]) fs.writeFileSync(a[2], csv); else process.stdout.write(csv);
    console.error(`${d.rows.length} frames, ${d.skipped} 4-byte words skipped while resyncing, params_hash ` +
        d.hashes.map(h => '0x' + h.toString(16).padStart(8, '0')).join(' '));
}

if (require.main === module) main();
module.exports = { readDef, decode, toCsv, FRAME_BYTES, MAGIC };
