/* Regression test for the Tools/ shared modules — run it with
 *     node Tools/verify.js
 * after touching tt-schema.js, tt-motor.js or tt-sim.js, and whenever the
 * game's VehicleDesign / MotorParams / mission_cfg.h constants change.
 *
 * It checks the browser-side copies against the actual repo: a real vehicle
 * JSON round-trips without losing a field, the stock design matches
 * VehicleDesign.Default(), the motor maths reproduces MotorModel's closed
 * forms, and the derived plant constants land on the values written into
 * Controllers/opus_mission/mission_cfg.h. Exit code 0 = everything agrees.
 */
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const REPO = path.resolve(__dirname, '..');
const TOOLS = path.join(REPO, 'Tools', 'shared');
const ctx = { console, window: undefined };
ctx.globalThis = ctx;
vm.createContext(ctx);
['tt-schema.js', 'tt-motor.js', 'tt-sim.js'].forEach(f => {
    vm.runInContext(fs.readFileSync(path.join(TOOLS, f), 'utf8'), ctx, { filename: f });
});
const S = ctx.TT.Schema, M = ctx.TT.Motor, Sim = ctx.TT.Sim;

let fails = 0, checks = 0;
function ok(name, cond, detail) {
    checks++;
    if (!cond) { fails++; console.log('  FAIL  ' + name + (detail ? '  — ' + detail : '')); }
    else console.log('  ok    ' + name + (detail ? '  (' + detail + ')' : ''));
}
function near(a, b, tol) { return Math.abs(a - b) <= (tol === undefined ? 1e-6 : tol); }

console.log('\n=== 1. Round-trip a real vehicle file =========================');
const realPath = path.join(REPO, 'UnitySim', 'Vehicles', 'Real Twin 2.json');
const raw = JSON.parse(fs.readFileSync(realPath, 'utf8'));
const norm = S.normalize(raw);
const emitted = S.toJson(norm);
let reparsed;
try { reparsed = JSON.parse(emitted); ok('emitted JSON is valid JSON', true); }
catch (e) { ok('emitted JSON is valid JSON', false, e.message); }

// Every key present in the original must survive with the same value.
function deepCompare(a, b, pathStr, out) {
    if (a === null || typeof a !== 'object') {
        if (typeof a === 'number') {
            if (!near(a, b, Math.max(1e-7, Math.abs(a) * 1e-6))) out.push(pathStr + ': ' + a + ' -> ' + b);
        } else if (a !== b) out.push(pathStr + ': ' + JSON.stringify(a) + ' -> ' + JSON.stringify(b));
        return;
    }
    if (Array.isArray(a)) {
        if (!Array.isArray(b) || a.length !== b.length) { out.push(pathStr + ': array length ' + a.length + ' -> ' + (b && b.length)); return; }
        a.forEach((v, i) => deepCompare(v, b[i], pathStr + '[' + i + ']', out));
        return;
    }
    Object.keys(a).forEach(k => {
        if (!(k in (b || {}))) { out.push(pathStr + '.' + k + ': MISSING in output'); return; }
        deepCompare(a[k], b[k], pathStr + '.' + k, out);
    });
}
const diffs = [];
deepCompare(raw, reparsed, 'design', diffs);
ok('every original field survives the round trip', diffs.length === 0, diffs.slice(0, 6).join(' | '));

// The generator must ALSO add the fields Unity would default-in.
['liveryPng', 'servoStallNm', 'controllerDll', 'antennas'].forEach(k => {
    ok('output carries "' + k + '" (absent in the old file)', k in reparsed);
});
ok('wheel gains wheelStyle + suspLength', 'wheelStyle' in reparsed.wheels[0] && 'suspLength' in reparsed.wheels[0]);
ok('motor object is complete (17 fields)', Object.keys(reparsed.wheels[0].motor).length === 17,
    Object.keys(reparsed.wheels[0].motor).length + ' fields');
S.MOTOR_FIELDS.forEach(f => { if (!(f in reparsed.wheels[0].motor)) { fails++; console.log('  FAIL  motor missing ' + f); } });
ok('motor field ORDER matches MotorParams declaration',
    JSON.stringify(Object.keys(reparsed.wheels[0].motor)) === JSON.stringify(S.MOTOR_FIELDS));
ok('escBrakeStrengthPct defaults to 100 not 0 (struct trap)', reparsed.wheels[0].motor.escBrakeStrengthPct === 100);
ok('escReverseLockMs defaults to 150 not 0 (struct trap)', reparsed.wheels[0].motor.escReverseLockMs === 150);
ok('color carries alpha', reparsed.bodyColor.a === 1);

console.log('\n=== 2. Stock design matches VehicleDesign.Default() ============');
const stock = S.stockDesign();
ok('name', stock.name === 'Stock RC');
ok('bodyShape LowRacer = 4', stock.bodyShape === 4);
ok('4 wheels, 2 powered, 2 steered', stock.wheels.length === 4 &&
    stock.wheels.filter(w => w.powered).length === 2 &&
    stock.wheels.filter(w => w.allowsSteering).length === 2);
ok('8 sensors (1 cam + 3 tof + 4 enc)', stock.sensors.length === 8);
ok('1 battery, 2 antennas', stock.batteries.length === 1 && stock.antennas.length === 2);
const stockValid = S.validate(stock).filter(v => v.level === 'error');
ok('stock design validates with no errors', stockValid.length === 0, JSON.stringify(stockValid));

console.log('\n=== 3. Stats vs the in-game VehicleStats =======================');
const st = S.stats(stock);
console.log('    totalMass=' + st.totalMass.toFixed(4) + ' kg  top=' + st.estTopSpeedMs.toFixed(3) +
    ' m/s  stallTq=' + st.totalStallTorqueNm.toFixed(4) + ' N·m  rideFreq=' + st.rideFreqHz.toFixed(2) +
    ' Hz  sag=' + st.sagPct.toFixed(1) + '%  frontWt=' + st.frontWeightPct.toFixed(1) + '%');
ok('stock total mass ≈ 1.8 kg (composite)', st.totalMass > 1.6 && st.totalMass < 2.1, st.totalMass.toFixed(3));
ok('stock top speed ≈ 10 m/s (design target)', st.estTopSpeedMs > 8 && st.estTopSpeedMs < 12, st.estTopSpeedMs.toFixed(2));
// Default motor: kt .003, R .09, Imax 40, gear 8, eff .85 → per motor 0.003*40*8*0.85 = 0.816 N·m
ok('stall torque = 2 × kt·Imax·gear·η', near(st.totalStallTorqueNm, 2 * 0.003 * 40 * 8 * 0.85, 1e-4),
    st.totalStallTorqueNm.toFixed(4));

console.log('\n=== 4. Motor maths ============================================');
// Opus Vector: Castle 1410-3800Kv → kt = 60/(2π·3800) = 0.00251302...
const ktOpus = M.ktFromKv(3800);
ok('kt from 3800 Kv = 0.0025130 (preset value)', near(ktOpus, 0.0025130, 5e-7), ktOpus.toFixed(7));
ok('Kv round-trips', near(M.kvFromKt(ktOpus), 3800, 1e-6));

// Datasheet conversion must match MotorModel.ApplyDatasheet exactly.
const ds = { nominalVoltage: 7.4, stallTorque: 0.25, noLoadRpm: 23000, noLoadCurrent: 1.2 };
const conv = M.applyDatasheet(ds);
const w0 = 23000 * Math.PI * 2 / 60;
const expR = 7.4 * 7.4 / (0.25 * w0 + 7.4 * 1.2);
const expK = 0.25 * expR / 7.4;
ok('R = Vn²/(τs·ω0 + Vn·I0)', near(conv.resistance, expR, 1e-12), conv.resistance.toFixed(6));
ok('Kt = τs·R/Vn', near(conv.kt, expK, 1e-12), conv.kt.toFixed(7));
const back = M.toDatasheet({ resistance: conv.resistance, kt: conv.kt, maxVoltage: 7.4, noLoadCurrent: 1.2 });
ok('datasheet round-trip returns the stall torque', near(back.stallTorque, 0.25, 1e-6), back.stallTorque.toFixed(5));
ok('datasheet round-trip returns the no-load rpm', near(back.noLoadRpm, 23000, 1e-3), back.noLoadRpm.toFixed(1));

// The one-motor-through-a-diff split (Opus Vector convention).
const real = { resistance: 0.030, noLoadCurrent: 1.8, maxCurrent: 60, rotorInertia: 6.44e-6, kt: ktOpus, gearRatio: 11.2 };
const split = M.splitAcrossMotors(real, 2);
ok('split: R doubles (0.030 → 0.060)', near(split.resistance, 0.060, 1e-9), split.resistance.toFixed(4));
ok('split: I0 halves (1.8 → 0.9)', near(split.noLoadCurrent, 0.9, 1e-9));
ok('split: Imax halves (60 → 30)', near(split.maxCurrent, 30, 1e-9));
ok('split: J halves (6.44e-6 → 3.22e-6)', near(split.rotorInertia, 3.22e-6, 1e-12));
ok('split: kt and gear unchanged', split.kt === real.kt && split.gearRatio === real.gearRatio);
const rejoined = M.combineFromMotors(split, 2);
ok('combine undoes split', near(rejoined.resistance, 0.030, 1e-12) && near(rejoined.noLoadCurrent, 1.8, 1e-12));

console.log('\n=== 5. Plant constants vs mission_cfg.h =======================');
// mission_cfg.h: VE_KT 0.0025130, VE_GEAR 11.2, VE_WHEEL_R 0.033, VE_ETA 0.85,
//   VE_BEMF_V_PER_MS = 0.8529, VE_FORCE_PER_AMP = 0.7250, VE_MASS_EFF 2.708 (m 2.1315)
// The header's own worked example uses J = 2.5e-6 per simulated motor:
//   m_rot = 2 * 2.5e-6 * 11.2^2 / 0.033^2 = 0.576 kg
const p = M.plant({
    kt: 0.0025130, gearRatio: 11.2, wheelRadius: 0.033, resistance: 0.060,
    efficiency: 0.85, motorCount: 2, rotorInertia: 2.5e-6, mass: 2.1315,
    maxVoltage: 7.4, maxCurrent: 30
});
console.log('    bemf=' + p.bemfVPerMs.toFixed(4) + ' V/(m/s)  forcePerAmp=' + p.forcePerAmp.toFixed(4) +
    ' N/A  massEff=' + p.massEff.toFixed(4) + ' kg  tauMech=' + p.tauMech.toFixed(3) + ' s');
ok('VE_BEMF_V_PER_MS = 0.8529', near(p.bemfVPerMs, 0.8529, 5e-4), p.bemfVPerMs.toFixed(4));
ok('VE_FORCE_PER_AMP = 0.7250', near(p.forcePerAmp, 0.7250, 5e-4), p.forcePerAmp.toFixed(4));
ok('VE_MASS_EFF = 2.708', near(p.massEff, 2.708, 5e-3), p.massEff.toFixed(4));

// Inverse model: at 4.5 m/s cruise the feed-forward should dominate.
const inv = M.voltageForForce(p, 4.5, 2.9);   // 2.9 N ≈ i22 coast drag
console.log('    cruise 4.5 m/s @ 2.9 N: V=' + inv.voltage.toFixed(3) + ' V, I=' + inv.currentEach.toFixed(3) +
    ' A/motor, back-EMF share=' + (inv.feedForwardShare * 100).toFixed(1) + '%');
ok('back-EMF is >90 % of the cruise demand', inv.feedForwardShare > 0.9,
    (inv.feedForwardShare * 100).toFixed(1) + '%');

console.log('\n=== 6. PID port matches pid.c behaviour =======================');
const pid = new Sim.Pid(1, 0, 0.5, -10, 10);
const first = pid.update(1, 0, 0.01);
ok('derivative does not spike on the first step (seeded)', first === 1, String(first));
// Derivative on measurement: a setpoint step must not kick the output.
const pid2 = new Sim.Pid(2, 0, 1, -100, 100);
pid2.update(0, 0, 0.01);
const kick = pid2.update(5, 0, 0.01);
ok('setpoint step gives P only, no derivative kick', near(kick, 10, 1e-9), String(kick));
// Anti-windup: saturated for a long time must not accumulate integrator.
const pidW = new Sim.Pid(0, 5, 0, -1, 1);
for (let i = 0; i < 500; i++) pidW.update(10, 0, 0.01);
const wound = pidW.integrator;
pidW.antiWindup = false; pidW.reset(); pidW.antiWindup = false;
for (let i = 0; i < 500; i++) pidW.update(10, 0, 0.01);
ok('anti-windup keeps the integrator bounded', wound < 1.2 && pidW.integrator > 100,
    'clamped=' + wound.toFixed(3) + ' vs unclamped=' + pidW.integrator.toFixed(0));
ok('dt<=0 returns the last clamped output', near(new Sim.Pid(1,0,0,-1,1).update(1,0,0), 0, 1e-9));

console.log('\n=== 7. Closed-loop sim sanity =================================');
// Reference car with its shipped gains (GA_SPD_KP 12, GA_SPD_KI 30) and the
// measured drag polynomial (VE_DRAG_C0/C1/C2).
const refPlant = {
    kt: 0.0025130, gearRatio: 11.2, wheelRadius: 0.033, resistance: 0.060,
    efficiency: 0.85, motorCount: 2, rotorInertia: 2.5e-6, mass: 2.1315,
    maxVoltage: 7.4, maxCurrent: 30,
    dragC0: 0.90, dragC1: 0.38, dragC2: 0.015, tractionEff: 0.99
};
const run = Sim.runSpeedStep({ duration: 4, stepTime: 0.5, vTarget: 4.5, plant: refPlant });
const vEnd = run.v[run.v.length - 1];
console.log('    final v=' + vEnd.toFixed(4) + ' m/s, rise=' + run.metrics.riseTime.toFixed(3) +
    ' s, overshoot=' + run.metrics.overshootPct.toFixed(2) + '%, ssErr=' + run.metrics.ssError.toFixed(4));
ok('speed loop converges on 4.5 m/s', near(vEnd, 4.5, 0.05), vEnd.toFixed(4));
ok('step metrics located the step', run.metrics.found);
ok('rise time is plausible (0.05–2 s)', run.metrics.riseTime > 0.05 && run.metrics.riseTime < 2,
    run.metrics.riseTime.toFixed(3));

const brake = Sim.runBrakeProfile({ v0: 4.5, distance: 1.5, decel: 6.75, plant: refPlant });
console.log('    brake stop distance=' + brake.stopDistance.toFixed(4) + ' m (error ' +
    brake.errorMm.toFixed(0) + ' mm), peak ESC ' +
    Math.max.apply(null, brake.escN).toFixed(1) + ' N / friction ' +
    Math.max.apply(null, brake.fricN).toFixed(1) + ' N');
ok('braking profile stops within 150 mm of the 1.5 m mark', Math.abs(brake.errorMm) < 150,
    brake.errorMm.toFixed(0) + ' mm');
ok('ESC brake respects the 7 N rear-grip cap', Math.max.apply(null, brake.escN) <= 7.001,
    Math.max.apply(null, brake.escN).toFixed(2) + ' N');
ok('friction brake picks up the surplus', Math.max.apply(null, brake.fricN) > 1);

console.log('\n=== 8. Drag-polynomial fit recovers known constants ===========');
const trueC = { c0: 2.9, c1: 0.35, c2: 0.11 }, mEff = 2.708;
const vs = [], ds2 = [];
for (let v = 0.5; v <= 6; v += 0.25) {
    vs.push(v);
    ds2.push((trueC.c0 + trueC.c1 * v + trueC.c2 * v * v) / mEff);
}
const fit = Sim.fitDragPolynomial(vs, ds2, mEff);
console.log('    fit c0=' + fit.c0.toFixed(4) + ' c1=' + fit.c1.toFixed(4) + ' c2=' + fit.c2.toFixed(4) +
    '  r²=' + fit.r2.toFixed(6));
ok('recovers c0', near(fit.c0, trueC.c0, 1e-6));
ok('recovers c1', near(fit.c1, trueC.c1, 1e-6));
ok('recovers c2', near(fit.c2, trueC.c2, 1e-6));

console.log('\n=== 9. Validator catches the real traps =======================');
const bad = S.stockDesign();
bad.mass = 900;
ok('flags mass > 50 (VehicleLibrary hides it)',
    S.validate(bad).some(v => v.level === 'error' && /50 kg/.test(v.msg)));
const bad2 = S.stockDesign();
bad2.sensors[4].wheelIndex = 9;
ok('flags encoder bound to a nonexistent wheel',
    S.validate(bad2).some(v => v.level === 'error' && /wheelIndex/.test(v.msg)));
const bad3 = S.stockDesign();
bad3.wheels.forEach(w => { w.powered = false; });
ok('flags a car with no powered wheel',
    S.validate(bad3).some(v => v.level === 'error' && /cannot move/.test(v.msg)));
const bad4 = S.stockDesign();
bad4.wheels[2].motorEntryMode = 1;
ok('flags datasheet mode with empty datasheet',
    S.validate(bad4).some(v => v.level === 'error' && /Datasheet/.test(v.msg)));
const bad5 = S.stockDesign();
bad5.controllerDll = '../evil.dll';
ok('flags a controllerDll with path separators',
    S.validate(bad5).some(v => v.level === 'error' && /SafeDllName/.test(v.msg)));

console.log('\n=== 10. Filename sanitization =================================');
ok('name → file name', S.fileNameFor({ name: 'My Car' }) === 'My Car.json');
ok('invalid chars replaced', S.fileNameFor({ name: 'a/b:c' }) === 'a_b_c.json');
ok('empty name falls back', S.fileNameFor({ name: '   ' }) === 'vehicle.json');

console.log('\n===============================================================');
console.log(fails === 0 ? 'ALL ' + checks + ' CHECKS PASSED' : fails + ' of ' + checks + ' CHECKS FAILED');
process.exit(fails === 0 ? 0 : 1);
