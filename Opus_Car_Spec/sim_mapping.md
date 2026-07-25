# Real part → simulator field

The authoritative table. The **Opus Vector** preset in
`UnitySim/Assets/Scripts/Garage/VehiclePresets.cs` implements exactly this; if the two
ever disagree, this file is the specification and the code is the bug.

Tags: **P** published · **D** derived · **E** estimated · **C** design choice.

---

## Chassis and body

| Simulator field | Value | Tag | Source |
|---|---|---|---|
| `name` | `"Opus Vector"` | C | |
| `bodyShape` | `LowRacer` | C | Flat-deck F1TENTH silhouette |
| `bodySize` | (0.200, 0.090, 0.420) m | C | 1/10 touring footprint |
| `mass` (chassis only) | 1.5685 kg | D | `mass_budget.md` |
| `useCompositeMass` | `true` | C | Total mass and CoM come from the part masses |
| `steerRate` | 600 °/s | D | Savox 0.09 s/60° = 667 °/s, 10 % load derate |
| `ackermannPct` | 100 | C | Proper steering geometry |
| `imuVibration` | 0.10 | E | Motor-borne chassis vibration reaching the BNO055 |
| `wheelVelQuantCpr` | 4096 | P | Matches the real encoder resolution |
| `wheelVelNoiseStd` | 0 | C | The encoder tick channel is the noise-bearing one; see note 2 |
| `controllerDll` | `"opus_controller.dll"` | C | The firmware this car ships with |

## Wheels — front (indices 0, 1), steered, **unpowered**

| Simulator field | Value | Tag | Source |
|---|---|---|---|
| `localPos` | (±0.086, −0.045, +0.150) m | C | 172 mm track, 300 mm wheelbase |
| `radius` | 0.033 m | C | 66 mm Ø touring tyre |
| `allowsSteering` | `true` | C | |
| `steerAngle` | 28° | C | Typical 1/10 lock |
| `powered` | `false` | C | RWD — and the reason these are the odometry source |
| `massKg` | 0.060 kg | E | `mass_budget.md` line 3 |
| `balloonPct` | **0** | C | **Critical — see note 1** |
| `suspStiffness` / `suspDampingRatio` / `suspTravel` | 320 N/m / 0.65 / 0.030 m | E | Typical 1/10 touring, ζ near critical |
| `suspLength` | 0.030 m | C | Visible strut, motion ratio 1 |
| `gripMult` | 1.0 | C | |
| `wheelStyle` | 0 (slick) | C | Touring tyre |

## Wheels — rear (indices 2, 3), **powered**

Same geometry at `z = −0.150`, `allowsSteering = false`, `powered = true`,
`balloonPct = 3` (tyre growth is real and harmless here — the rears are not the
odometer), and each carrying a motor:

| `MotorParams` field | Value | Tag | Source |
|---|---|---|---|
| `maxVoltage` | 7.4 V | P | 2S LiPo nominal |
| `kt` | 0.0025130 N·m/A | D | `60/(2π·3800)` — `derived_parameters.md` §1 |
| `resistance` | 0.060 Ω | D | 2 × the real 30 mΩ, per the two-motor split (§4) |
| `gearRatio` | 11.2 | C | 15T/62T × 2.71 |
| `noLoadCurrent` | 0.9 A | D | Half the real 1.8 A |
| `maxCurrent` | 30 A | D | Half the real 60 A ESC limit |
| `rotorInertia` | 3.22 × 10⁻⁶ kg·m² | D | Half the real 6.44 × 10⁻⁶ |
| `viscousDamping` | 1 × 10⁻⁶ N·m·s/rad | E | Class-typical |
| `efficiency` | 0.85 | E | Spur/pinion + bevel drivetrain |
| `coulombScale` | 1.0 | C | Enable the Coulomb term |
| `escPwmSteps` | 1024 | E | 10-bit ESC |
| `escDeadbandV` | 0.10 V | E | RC ESC neutral band |
| `escTimeConstMs` | 5 ms | E | ESC input filter + commutation update |

**Every extensive quantity is halved** because two simulated motors stand in for one real
motor — `derived_parameters.md` §4 proves the split reproduces the real torque–speed
curve exactly.

## Sensors

| Name | Kind | Position / aim | Simulator config | Tag |
|---|---|---|---|---|
| `tof_front` | ToF | (0, 0.030, 0.210) | `range 4.0`, `coneRays 3`, `coneAngle 27°`, `noiseStd 0.008`, `updateRateHz 50`, `latencyMs 20` | P/D |
| `tof_left` | ToF | (−0.060, 0.030, 0.190), yaw −32° | same | P/D |
| `tof_right` | ToF | (+0.060, 0.030, 0.190), yaw +32° | same | P/D |
| `enc_fl` | Encoder | wheel 0 | `cprTicks 4096`, `encoderGearRatio 1`, `updateRateHz 0`, `latencyMs 0`, `noiseStd 0` | P/C |
| `enc_fr` | Encoder | wheel 1 | same | P/C |
| `enc_rl` | Encoder | wheel 2 | same | P/C |
| `enc_rr` | Encoder | wheel 3 | same | P/C |
| `cam_front` | Camera | (0, 0.090, 0.055), pitch 6° | `64 × 48`, `fov 66°`, `10 Hz` | P |
| `battery` | Battery | (0, −0.020, −0.050) | `7.4 V`, `internalR 0.020 Ω`, `massKg 0.265` | P/E |

ToF range, rate and field of view are straight off the VL53L1X datasheet; `noiseStd`
converts its ±25 mm accuracy bound to a σ (`derived_parameters.md` §7).

---

## Two notes that will silently ruin the mission if ignored

**1. `balloonPct = 0` on the front wheels.** The simulator's tyre-ballooning model
rewrites `WheelCollider.radius` at speed, while `WheelEncoderSensor` integrates `rpm`.
The odometer's distance-per-revolution constant would then be wrong by the growth
fraction: at 4.5 m/s a `balloonPct = 3` front tyre grows 0.89 %, which is **129 mm of
error over the 14.5 m leg** — two orders of magnitude past the mission's tolerance. The
rear wheels keep ballooning because nothing measures distance from them.

**2. Encoder `updateRateHz = 0` and `latencyMs = 0` are the *realistic* setting, not a
cheat.** It is tempting to copy the `Real Twin` preset's 50 Hz / 20 ms sensor realism
onto the encoders. Don't: real quadrature encoders are hardware counters read
synchronously by the MCU, with no sampling delay. Modelling them at 50 Hz with 20 ms of
latency would inject **90 mm of odometry lag at 4.5 m/s** and model something that does
not exist. The ToF sensors, which *are* polled over I²C, correctly carry both.

---

## What the simulator represents since iteration 22

Four items moved off the missing list when the brush tyre model landed:

- **Tyre slip physics** — the PhysX WheelCollider friction curve is gone. Tyre
  forces now come from a brush model (slip ratio κ and slip angle α, normalized
  peaks at ~10 % / ~7°, friction-ellipse combined slip) with the wheel spin
  integrated against real drive/brake/rolling torques. The measured
  consequences are on record in [`calibration.md`](calibration.md): encoder
  scale error fell from an artifact 11.6 % to ~0.0 %, driven-tyre slip loss
  from 53 % to ~1 %.
- **ESC drive/brake/reverse behaviour** — a negative command while rolling is a
  proportional shorted-winding brake (force ∝ duty × speed, nothing at rest,
  current circulating in the bridge, zero pack draw); reverse engages only
  after a dwell in neutral at rest; optional drag brake at neutral.
- **Battery discharge** — the 5200 mAh capacity is live: a coulomb counter
  drives state of charge, the rail follows the LiPo OCV curve (4.2 V/cell full
  → plateau → knee), and `sens/battery1/soc` reports it. Respawn restores a
  full pack for run-to-run determinism.
- **Servo under load** — the Savox torque-speed line: available slew derates as
  steered-tyre lateral force × trail approaches the 0.883 N·m stall, so
  steering authority collapses exactly when the tyres are working hardest.

## What the simulator cannot represent

For completeness, the parts of the real vehicle that still have no counterpart:

- **Commutation detail** — phase advance, sensored vs sensorless startup, ESC timing.
- **Open differential** — the two rear motors act as a locked torque split.
- **Camera image quality** — the simulated frame is noise-free and perfectly exposed.
- **Thermal effects** — no winding heating, so no resistance rise or ESC derate.
- **Suspension stiction** — springs and dampers are ideal.
- **Unpowered-wheel rolling losses** — the brush model gives the free-rolling
  fronts almost no drag, so their slip (and hence `CAL_SCALE`) now sits *below*
  the 1–4 % a physical wheel shows from bearing drag and carcass deformation.
  The sim moved from over-representing this error to slightly under-representing
  it; the calibration procedure is what transfers, not the number.

None of these affect the mission, which is dead-reckoned from encoders and the IMU over
a 30 m run lasting under 13 seconds.
