# Derived parameters

How the published specifications in [`bill_of_materials.md`](bill_of_materials.md) become
the numbers the simulator's DC-machine and chassis models need. Anything marked
**estimated** here is a genuine gap in the vendor data, not laziness — §3 explains what
would close it.

Symbols: `Kv` motor velocity constant, `Kt = Ke` torque/back-EMF constant, `G` final
drive ratio, `r` wheel radius, `η` drivetrain efficiency, `m` vehicle mass,
`v` road speed, `ω` angular velocity.

Fixed inputs: `r = 0.033 m`, `m = 2.1315 kg` (from [`mass_budget.md`](mass_budget.md)),
`η = 0.85` (estimated, class-typical for a spur/pinion + bevel drivetrain).

---

## 1. Torque constant from Kv — *derived*

The SI identity for any permanent-magnet machine, with Kv in rpm/V:

```
Kt = Ke = 60 / (2π · Kv) = 60 / (2π · 3800) = 0.0025130 N·m/A   (= V·s/rad)
```

This is exact, not an approximation: it is a unit conversion of the same physical
constant. It is the single most important derived number in the model, and it rests
only on the published 3800 Kv.

## 2. Final drive — *design choice*

```
G = pinion/spur × transmission = (62/15) × 2.71 = 4.1333 × 2.71 = 11.2 : 1
```

A 15-tooth pinion on a 62-tooth spur through a 2.71:1 transmission — a standard 1/10
4WD gearing stack. Chosen so that the mission's 4.5 m/s cruise sits at roughly half of
the drivetrain's no-load speed, leaving headroom for acceleration:

```
ω_motor,no-load(7.4 V) = 3800 × 7.4 = 28 120 rpm = 2944.9 rad/s
v_no-load             = ω · r / G = 2944.9 × 0.033 / 11.2 = 8.68 m/s
```

4.5 m/s is 52 % of that, and 28 120 rpm is well inside the motor's published
100 000 rpm ceiling.

## 3. Winding resistance, no-load current, rotor inertia — *estimated*

Castle publishes none of these. Values used, with their basis:

| Parameter | Value | Basis |
|---|---|---|
| Line-to-line resistance `R` | 30 mΩ | 4-pole 3650/1410-size motors in the 3300–4000 Kv band measure 15–30 mΩ phase-to-phase; +≈5 mΩ for ESC FETs and wiring |
| No-load current `I0` | 1.8 A | Class-typical for a 36 mm can on 2S; sets bearing, windage and iron losses |
| Rotor inertia `J` | 6.44 × 10⁻⁶ kg·m² | Geometric estimate below |

Rotor inertia from geometry — a solid cylinder of Ø22 mm × 40 mm inside the 36 mm can,
effective density 7000 kg/m³ (steel shaft plus sintered NdFeB):

```
V = (π/4)(0.022)²(0.040) = 1.5205 × 10⁻⁵ m³
M = 7000 · V             = 0.1064 kg
J = ½ M R²  = 0.5 × 0.1064 × (0.011)² = 6.44 × 10⁻⁶ kg·m²
```

Reflected through the gearbox this is `J·G² = 8.08 × 10⁻⁴ kg·m²`, equivalent to
**0.74 kg of extra translational mass** — a 35 % inertia penalty on a 2.13 kg car. That
is not an artefact; drivetrain inertia genuinely dominates small-car launch behaviour,
and it is why the controller's acceleration limit is set below the traction limit.

**What would close these gaps:** a bench measurement. `R` from a milliohm meter across
two phases; `I0` from a free-running current draw at a known voltage; `J` from a
spin-down test. All three are 15-minute measurements on the physical car, and
[`calibration.md`](calibration.md) is where the results would go.

## 4. One real motor → two simulated motors

The simulator gives each powered wheel its own motor. The real car has one motor and a
differential. To keep total torque, total current and total inertia correct, parameters
are split by whether they are *extensive* (add across the two sim motors) or *intensive*
(the same for each):

| Parameter | Real | Per simulated motor | Why |
|---|---|---|---|
| `kt` | 0.0025130 | **0.0025130** | intensive — each half-shaft sees the same constant |
| `gearRatio` | 11.2 | **11.2** | intensive |
| `resistance` | 0.030 Ω | **0.060 Ω** | two paths in parallel: `2/R_sim = 1/R_real` |
| `noLoadCurrent` | 1.8 A | **0.9 A** | extensive — the two must sum to the real drag |
| `maxCurrent` | 60 A | **30 A** | extensive |
| `rotorInertia` | 6.44 × 10⁻⁶ | **3.22 × 10⁻⁶** | extensive — one rotor, not two |

Check that the split reproduces the real torque–speed curve. With both sim motors at the
same voltage `V` and per-motor current `I_each = (V − Ke·ω)/R_sim`:

```
I_total,sim = 2 · (V − Ke·ω)/0.060 = (V − Ke·ω)/0.030 = I_real   ✓
F_total,sim = 2 · (Kt·G·η/r) · I_each = (Kt·G·η/r) · I_real       ✓
```

Both match the single real motor exactly. **The residual difference is behavioural, not
electrical:** two independently-commanded motors act as a locked torque split rather
than an open differential. That matters when the inside and outside wheels turn at
different speeds; through the mission's 5.06 m-radius corner the speed difference is
1.7 %, so it is negligible here.

## 5. Referred constants — *derived*

Everything the longitudinal controller needs, referred to the road:

```
back-EMF per road speed   Kt·G / r     = 0.0025130 × 11.2 / 0.033 = 0.8529 V per (m/s)
ground force per amp      Kt·G·η / r   = 0.8529 × 0.85            = 0.7250 N/A   (per total amp)
                                                                    1.4499 N/A   (per sim-motor amp)
acceleration per amp      ÷ m                                     = 0.6802 (m/s²) per sim-motor amp
back-EMF at 4.5 m/s       0.8529 × 4.5                            = 3.838 V   (52 % of the 7.4 V rail)
```

The last line is why this control loop can be accurate: at cruise, **94 % of the
commanded voltage is pure feed-forward** against a constant that is known exactly from
Kv. The PID only has to supply the remaining 6 %.

## 6. Drag model and the mission's power demand — *derived*

Four loss terms, all computed from simulator constants rather than fitted:

| Term | Expression | Value |
|---|---|---|
| Motor Coulomb friction | `2 · Kt·I0,sim · G·η/r` | 1.305 N (constant) |
| Wheel bearing damping | `4 · d_w · v/r²`, `d_w = 2×10⁻⁴` | 0.735·v N |
| Motor viscous friction | `2 · b · G²·η · v/r²`, `b = 1×10⁻⁶` | 0.196·v N |
| Aerodynamic | `½ρ·Cd·A·v²`, Cd 0.55, A 0.0162 m² | 0.00546·v² N |

```
F_drag(v) = 1.305 + 0.930·v + 0.00546·v²
F_drag(4.5) = 5.60 N   →  2.63 m/s² of free deceleration at cruise
```

Cruise demand: `I_each = 5.60 / 1.4499 = 3.86 A`, so
`V = 0.8529 × 4.5 + 0.060 × 3.86 = 3.838 + 0.232 = 4.07 V`.

Peak demand is during launch at the controller's 6 m/s² limit:
`F = 2.1315 × 6 + 1.4 ≈ 14.2 N → I_each ≈ 9.8 A → I_total ≈ 20 A`. Against a 60 A ESC
rating that is **33 % of the limit**, which is why §2 of the BOM can leave the exact ESC
unpinned — it never binds.

## 7. ToF noise from an accuracy bound — *derived*

The VL53L1X datasheet quotes ranging accuracy as **±25 mm**, which is an error *bound*,
not a standard deviation. Treating the bound as approximately 3σ:

```
σ = 0.025 / 3 = 0.0083 m  →  noiseStd = 0.008 m
```

Sampled at the published 50 Hz with 20 ms of I²C and processing latency.

## 8. Encoder resolution — *derived*

```
distance per count = 2πr / CPR = 0.20735 / 4096 = 5.062 × 10⁻⁵ m = 0.0506 mm
counts per second at 4.5 m/s   = 4.5 / 5.062×10⁻⁵ = 88 900 counts/s
counts per 10 ms control tick  = 889
wrap period (16-bit) at cruise = 65536 / 88900 = 0.74 s
```

889 counts per tick against a 32768 half-wrap is a **37× margin**, so wrap detection by
sign is unambiguous. The 0.74 s wrap period means the wrap path is exercised roughly
30 times during a single mission run — it is live code, not a corner case.

## 9. Steering — *derived*

Published servo speed is 0.09 s/60° at 6 V, no load:

```
667 °/s no-load  →  600 °/s used, a 10 % derate for linkage load and endpoint damping
```

Maximum steer angle 28° (design choice, typical 1/10 touring), Ackermann 100 %.

The mission's corner needs almost none of it:

```
a_lat = 4.0 m/s²  →  R = v²/a = 20.25/4.0 = 5.06 m
                     ψ̇ = v/R = 0.889 rad/s = 50.9 °/s
                     δ_kinematic = atan(L/R) = atan(0.300/5.06) = 3.39°   (12 % of lock)
                     arc = R·π/4 = 3.98 m, 0.88 s at constant radius
```

`a_lat` is capped at 4.0 m/s² by **inner-wheel load**, not grip: lateral transfer is
`m·a·h/t = 2.1315 × 4.0 × 0.05 / 0.172 = 2.48 N` against a ~5.2 N static corner load. At
8 m/s² the transfer would equal the static load and the inner front wheel would lift —
taking the odometry and heading sources with it. Grip is not the limit; at 4 m/s² the
car is using about 19 % of its lateral friction budget.

## 10. Braking — *derived*

Stopping from 4.5 m/s in exactly 1.5 m:

```
a_brake = v² / (2s) = 20.25 / 3.0 = 6.75 m/s²  = 0.69 g
```

Against a longitudinal friction budget of roughly `μ·g ≈ 1.6 × 9.81 = 15.7 m/s²`, that
is **43 % of available grip** — a 2.3× margin. Actuator force required falls out of the
drag model:

```
at 4.5 m/s:  F = m·a − F_drag = 14.39 − 5.60 =  8.79 N
at 0.3 m/s:  F = m·a − F_drag = 14.39 − 1.58 = 12.81 N
```

Note the demand *rises* as the car slows, because aerodynamic and viscous drag fall
away. That is why the controller cannot brake on regenerative motor torque alone — the
rear axle, unloaded by weight transfer, runs out of grip before the car stops — and why
the friction brake blends in over the second half of the stop.
