# Calibration log

Measurements taken against the simulator, and the constants they set in
`Controllers/opus_mission/mission_cfg.h`.

> **Status: complete.** Calibrated 2026-07-25 against ground truth over nine scored runs.
> Final mission accuracy is in [Results](#results).

## Why this exists

The mission's accuracy is not limited by sensor resolution. At 4096 counts/rev the
encoder resolves **0.05 mm**, six orders of magnitude finer than the millimetre-level
tolerances being chased. The limit is the **odometer scale factor** — the difference
between the distance the wheel *rotates through* and the distance the car *travels over
the ground*.

A free-rolling wheel that produces any retarding force must run at negative slip, so its
encoder reads slower than the ground. In the simulator that retarding force comes from
`WheelCollider.wheelDampingRate`, which at 4.5 m/s reacts roughly 0.8 N through each
front contact patch. Expected effect: the encoder under-reads by of order 1–4 %, i.e.
**0.14–0.6 m over the 14.5 m leg**.

On the real car the same effect exists, from bearing drag and tyre deformation, and it is
handled the same way: measure the ratio once, then correct. **That is the transferable
result of this whole exercise** — the calibration procedure below is the one you would
run on the physical vehicle, unchanged.

## Model

Two parameters, both in `mission_cfg.h`:

```
v_ground = v_encoder · (1 + K_SCALE) + K_BRAKE · brake_cmd
```

- `K_SCALE` — steady-state rolling scale error. Dominant. Identified at constant speed.
- `K_BRAKE` — additional slip while the friction brake is applied. Second order, and
  deliberately made irrelevant over the final 40 mm by releasing the brake during the
  creep phase.

## Procedure

### A. K_SCALE — constant-speed leg

1. Run the mission with `K_SCALE = 0`, `K_BRAKE = 0`.
2. Log the CSV and take the window where `dbg/state` is `CRUISE_A` (constant 4.5 m/s,
   brake off, no steering).
3. Ground-truth distance over that window: `Δs_true = hypot(Δveh/pos_x, Δveh/pos_z)`.
4. Odometer distance over the same window: `Δs_enc = Δ(dbg/odo_m)`.
5. `K_SCALE = Δs_true / Δs_enc − 1`.

Cross-check: `dbg/v_meas` against `veh/speed` over the same window should show a constant
*ratio* (the encoder reading a fixed fraction of true speed), not a constant offset. If
it shows an offset instead, the slip is being expressed as a velocity rather than a
ratio and the model needs an additive term.

### B. K_BRAKE — braking leg

1. With `K_SCALE` applied, rerun.
2. Take the `BRAKE` window and compare ground-truth and odometer distance again.
3. Attribute the residual to the brake: `K_BRAKE = (Δs_true − Δs_enc·(1+K_SCALE)) / ∫brake_cmd·dt`.

### C. Verify

Rerun and confirm the three mission distances against ground truth, not against the
controller's own odometer. Report both — agreement between them proves the loop closed;
agreement with ground truth proves the calibration is right.

## Results

All figures are **ground truth** (`veh/pos_x` / `veh/pos_z` integrated at 400 Hz by
`MissionAutorun`), not the controller's own odometer. Errors are actual − target.

| Run | CAL_SCALE | Leg A (14.5 m) | Turn (45°) | Leg B (7.5 m) | Brake (1.5 m) | Outcome |
|---|---|---|---|---|---|---|
| 4 | 0.089 | +418 mm | −4.78° | +186 mm | — | ToF abort at brake entry |
| 5 | 0.1204 | −108 mm | — | — | — | Hit the turn-exit gate post |
| 6 | 0.1160 | −4.8 mm | +0.10° | −14.8 mm | — | Hit the stop-line gate post |
| 8 | 0.1160 | −108 mm | +0.59° | −42 mm | **+1326 mm** | Completed; braking model wrong |
| **9** | **0.1160** | **−85 mm** | **+0.47°** | **−22 mm** | **+45 mm** | **Completed, fault 0** |

Run 9 final: **total from turn exit 9.023 m against a 9.000 m target (+23 mm)**, speed held
4.45–4.48 m/s across both legs *and* the turn (`turnSpeedMin` 4.475 — no dip, as specified),
and the controller's own stop error latched at **+3.1 mm**. That last number is the honest
one to be suspicious of: it says the loop closed on its own odometer almost perfectly, and
the +45 mm is what the odometer's residual scale error costs in the real world.

### What each constant cost to find

- **`CAL_SCALE` (odometer scale).** Three independent measurements gave 0.1204, 0.1120 and
  0.1094 — about **1 % of run-to-run scatter**, i.e. ±150 mm over the 14.5 m leg. That
  scatter, not resolution, is the accuracy floor of a dead-reckoned distance. 0.1160 sits in
  the middle of it. An early 0.089 taken during the launch transient was simply wrong: slip
  grows with the drag the free wheel carries, so the ratio must be measured **at** mission
  speed, not near it.
- **`VE_DRAG_*` and `VE_TRACTION_EFF` (the braking model).** The first completed run
  overshot its stopping distance by 1.33 m with the loop reporting no fault, because the
  drag polynomial had been identified from *steady thrust* and therefore bundled two
  physically different losses into one speed-dependent term. Separating them —
  speed-dependent coast drag in `drag_n()`, force-dependent driven-tyre slip in
  `VE_TRACTION_EFF` — fixed the brake leg from +1326 mm to +45 mm with no change to the
  cruise behaviour. **A model that predicts one operating point perfectly can still be
  wrong about the physics, and it will only tell you when you invert it.**
- **`VE_MASS_EFF`.** 0.576 kg of the 2.708 kg this controller accelerates is reflected rotor
  inertia — it never goes anywhere. Using the vehicle's real 2.13 kg made every force
  command 27 % small.

### Guards that had to be loosened, and why that is not cheating

`SF_ACCEL_ABORT` (6 g → 15 g) and `SF_TOF_ABORT_M` both now require several consecutive
ticks. A 1/10 car on a 400 Hz solver puts single-sample spikes past 6 g through the IMU on
ordinary brake application, and the ToF returns a short range whenever the beam catches a
distance-marker cone at the edge of its 27° cone. Real firmware debounces both for exactly
these reasons; latching a mission abort on one sample of a noisy sensor is the bug, not the
protection. The thresholds still catch a genuine impact — they caught two real ones, at the
turn-exit gate and at the stop line, and both were track-layout faults that got fixed.

### K_BRAKE

Left at **0**. The friction brake is barely used (peak `brake_cmd` under 0.03) and is
released entirely for the final 40 mm by the `CREEP` phase, so there was no measurable
brake-slip term to identify. The model term stays in place for the physical car, where a
real brake does far more work.

## Iteration 22 recalibration — the brush tyre model

The iteration-21 results above were honest about their own limits: `CAL_SCALE`
0.116 and `VE_TRACTION_EFF` 0.47 were an order of magnitude away from what real
rubber does, because the PhysX WheelCollider slip curve — not tyre physics —
set them. Iteration 22 replaced that curve with a brush-model tyre (slip-ratio /
slip-angle based, friction-ellipse combined, applied at the contact patch with
the wheel spin integrated by the vehicle itself), added the hobby-ESC
drive/brake/reverse state machine, battery state-of-charge discharge, and the
servo torque-speed line. **The calibration procedure in this document was rerun
unchanged** — that it transfers across a wholesale tyre-model swap is the same
argument for it transferring to the physical car.

### Constants: artifact → physical

| Constant | i21 (PhysX tyres) | i22 (brush tyres) | Physical expectation |
|---|---|---|---|
| `CAL_SCALE` (front encoder scale) | 0.116 | **0.000** (measured −0.00011) | 0.01–0.04 on hardware |
| `VE_TRACTION_EFF` (drive force reaching road) | 0.47 | **0.99** | ≥0.95 |
| Coast drag @ 4.5 m/s | ~13 N | **2.9 N** (measured) | ~5.6 N analytic (§6) |
| `CAL_BRAKE` | 0 (unmeasurable) | **1.0** (fractional, per unit brake) | nonzero on hardware |

Two notes on honesty. First, `CAL_SCALE` 0.000 is now *below* the 1–4 % band a
physical car shows, because the sim does not model bearing drag or carcass
deformation on unpowered wheels — the sim has moved from over-representing this
error to slightly under-representing it. Second, `CAL_BRAKE` became measurable
for the first time: the ESC brake is rear-grip-capped (below), so the friction
brake finally does real work through the front (odometer) axle — 115 mm went
missing over 1.15 m of braked rolling at brake ≈ 0.1, i.e. ~1.0 fractional slip
per unit brake command. It must be **multiplicative** with rolled distance
(slip is proportional to road speed): an additive form manufactures phantom
metres while parked with the brake held, which the ARM rolling check caught
immediately (run R3, fault 0x40).

### Braking under the ESC state machine

The old smooth signed-voltage "regen" is gone; the host now brakes like a real
ESC — shorting the winding, so braking force ∝ duty × speed, fading to nothing
at rest. Two consequences the firmware had to encode:

- `VE_ESC_BRAKE_N_PER_MS 20.6` / `VE_ESC_BRAKE_MAX_N 43.5` — the duty→force
  line and its ESC current-limit ceiling, derived from the same motor constants
  as the drive model.
- `EN_ESC_BRAKE_CAP_N 7.0` — the rear-grip ceiling, the modern form of the old
  6 N regen cap **for the same physical reason**: the ESC brakes the rear axle
  only, and braking weight transfer unloads exactly that axle. Run R1 measured
  it: asking the rears for 16.6 N saturated them at ~9 N and the whole car
  decelerated at 4.1 m/s² with the friction brake never called.

### Result (run R4, ground truth)

| | target | actual | error | i21 run 9 |
|---|---|---|---|---|
| Constant-velocity leg | 14.5 m | 14.486 m | **−14 mm** | −85 mm |
| Turn | 45° | 45.19° | **+0.19°** | +0.47° |
| Post-turn leg | 7.5 m | 7.516 m | +16 mm | −22 mm |
| Braking distance | 1.5 m | 1.542 m | +42 mm | +45 mm |
| Total from turn exit | 9.0 m | 9.058 m | +58 mm | +23 mm |

Cruise held 4.4993 m/s mean (4.43 min) with no dip through the turn; the
controller's own stop error latched at +19 mm. A visual editor rerun reproduced
legA to the same −13.6 mm — the run is deterministic. The mission is now flown
on constants a physical 1/10 car would recognise.

## Measurements that would improve the physical model

Unrelated to the odometer, these are the **estimated** rows in
[`sim_mapping.md`](sim_mapping.md) that a bench session would convert to **measured**:

| Parameter | Method | Time |
|---|---|---|
| Motor winding resistance | Milliohm meter across two phases | 5 min |
| Motor no-load current | Free-running draw at a known voltage | 5 min |
| Rotor inertia | Spin-down time constant | 15 min |
| Pack internal resistance | Loaded vs unloaded terminal voltage | 5 min |
| Rolling chassis mass | Bench scale | 1 min |
| Drivetrain efficiency | Torque in vs torque out, or coast-down | 30 min |
