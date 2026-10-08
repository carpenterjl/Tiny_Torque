# Controller Interface Specification

The Unity host and every controller DLL communicate through one C header:
`Controllers/hal/controller_api.h`. This document is the authoritative
description of that contract. **If you change the header, update
`UnitySim/Assets/Scripts/Bridge/ControllerInterop.cs` to match** — the two
struct layouts must stay byte-identical.

## Data flow (once per control tick)

```
Unity  --CtrlInputs-->  ctrl_step()  --CtrlOutputs-->  Unity
```

The controller is a pure function of its inputs plus its own internal state. It
must not block, allocate across the boundary, or throw.

## Structs

### CtrlInputs (host → controller)

| Field         | Type      | Units   | Meaning                                   |
|---------------|-----------|---------|-------------------------------------------|
| `time_s`      | float     | s       | Seconds since sim start                   |
| `dt_s`        | float     | s       | Control period for this tick              |
| `gyro[3]`     | float[3]  | rad/s   | Body angular rate (x, y, z)               |
| `accel[3]`    | float[3]  | m/s²    | Body specific force (x, y, z)             |
| `wheel_vel[4]`| float[4]  | rad/s   | Measured wheel angular velocity           |
| `setpoint[4]` | float[4]  | varies  | Operator commands (vehicle-defined)       |
| `sensor_data` | const float* | varies | Flat configurable-sensor block (ABI v2), layout per manifest |
| `sensor_count`| int       | —       | Number of manifest entries (ABI v2)       |
| `sensor_data_len`| int    | —       | Total floats in `sensor_data` (ABI v2)    |
| `cam_pixels`  | const unsigned char* | 0–255 | Grayscale frame, row-major, **row 0 = top** (see below), or NULL (ABI v2) |
| `cam_width`   | int       | px      | Camera frame width, 0 if no camera (ABI v2)|
| `cam_height`  | int       | px      | Camera frame height, 0 if no camera (ABI v2)|

The pointer fields are valid only for the duration of the `ctrl_step` call.

**Camera row order (ABI v4).** `cam_pixels` is top-down: pixel *(x, y)* is
`cam_pixels[y * cam_width + x]` with *y* counting **down** from the top edge.
Through ABI v3 the frame arrived bottom-up — not by design, but because that is
the order Unity's texture space hands it over in, and no version of this document
said which it was. v4 changes no struct layout; it only fixes the convention. A
controller that reads the frame symmetrically (left-half vs right-half sums,
whole-frame brightness) is unaffected; one that looks at "the bottom of the
image" for the track ahead needs its row indices flipped.

## Configurable sensors & actuators (ABI v3)

Each `SensorInfo` carries an `actuator_index`: for a `MOTOR` it is the
`actuator[]` slot the host reads that motor's voltage from (−1 for
non-actuators). ABI v2 added the sensor block; ABI v3 added `actuator_index` and
made motors voltage-driven actuators.


Vehicles assembled in the garage carry a loadout of sensor *parts*. The host
describes the loadout once, right after `ctrl_init`, via the optional
`ctrl_configure(const SensorInfo* sensors, int count)` export. Controllers that
don't export it are driven exactly as in v1 (the new `CtrlInputs` fields are
still populated but can be ignored).

Each `SensorInfo` names a sensor, tags its type, and points at the slice
`sensor_data[data_offset .. data_offset+data_count)` it fills each tick:

| `type` (`SENSOR_*`) | Slice layout                                  |
|---------------------|-----------------------------------------------|
| `TOF` (1)           | `[distance_m]` (`range_max` on no hit)        |
| `ENCODER` (2)       | `[ang_vel_rad_s, ticks]` (wrapped counter)    |
| `MOTOR` (3)         | `[voltage_V, current_A, torque_Nm]` feedback; also an **actuator** (see `actuator_index`, and `range_*` = ±maxVoltage). On a design with the realistic sensor profile, `torque_Nm` is NaN (not measurable) |
| `IMU` (4)           | `[gx,gy,gz, ax,ay,az]` (mirror of gyro/accel) |
| `CAMERA` (5)        | no floats — frame via `cam_pixels`/`cam_*`    |
| `SUSPENSION` (6)    | `[spring_force_N, compression_01, angle_deg]` |
| `BATTERY` (7)       | `[terminal_V, total_current_A, soc_01]` — bus voltage sags with load across the pack's internal resistance; a controller can voltage-compensate its motor commands. On a design with the realistic sensor profile (`sensorRealism` 1), V and I carry INA228-class errors and `soc_01` is NaN: estimate it in firmware |

Type tags are append-only (an old controller iterating the manifest simply
ignores unknown tags), so appending `SUSPENSION`/`BATTERY` did not change the
ABI layout — it stayed at **v3** then. v4–v6 added conventions, an optional
export and sensor tags 8–12, none of which moved a field. The header is now at
**v7**, which appends a tail to `CtrlInputs` and is opt-in — see
[ABI v7](#abi-v7-opt-in) below and the version notes at the top of
`controller_api.h`.

Sensor readings are also published to telemetry as `sens/<name>/<field>` and
logged to CSV in both Manual and Autonomous modes. See
`Controllers/car_sensors/car_sensors.c` for a minimal v2 reference controller.

### CtrlOutputs (controller → host)

| Field         | Type       | Units  | Meaning                                        |
|---------------|------------|--------|------------------------------------------------|
| `actuator[8]` | float[8]   | mixed  | Per-vehicle actuator commands (see below)      |
| `debug[16]`   | float[16]  | any    | Free channels, auto-graphed by name            |

**Car actuator layout (ABI v3):** each drive motor reads its own slot
`actuator[SensorInfo.actuator_index]` as a **signed voltage** (sign = direction),
clamped by the host to that motor's ±maxVoltage (advertised as the motor's
`range_min/range_max` in the manifest). `actuator[6]` = steering `[-1,1]` (front
servo), `actuator[7]` = brake `[0,1]`. `CTRL_STEER_ACTUATOR` / `CTRL_BRAKE_ACTUATOR`
name these. A wheel with no motor free-rolls. Manual mode drives the same slots
(throttle→full-scale voltage) so both modes share the drivetrain physics.

## Exports

| Symbol                  | Signature                                    | Notes                                    |
|-------------------------|----------------------------------------------|------------------------------------------|
| `ctrl_init`             | `int (float control_rate_hz)`                | Return 0 on success. Called on load.     |
| `ctrl_step`             | `void (const CtrlInputs*, CtrlOutputs*)`     | The control law. Runs at the control rate.|
| `ctrl_shutdown`         | `void (void)`                                | Called on unload / play-mode exit.       |
| `ctrl_get_debug_names`  | `const char* (void)`                         | Comma-separated labels for `debug[]`.    |
| `ctrl_configure`        | `void (const SensorInfo*, int count)`        | **Optional** (ABI v2). Sensor manifest; called once after `ctrl_init`. |
| `ctrl_get_vehicle`      | `int (void)`                                 | **Optional** (ABI v5). One `CTRL_VEHICLE_*` value; the car to load into. |
| `ctrl_abi_version`      | `int (int* sizeof_inputs, int* sizeof_outputs)` | **Optional** (ABI v7). Makes the DLL a v7 controller; write it with `CTRL_DEFINE_ABI_VERSION()`. |
| `ctrl_configure2`       | `void (const SensorInfo2*, int count)`       | **Optional** (ABI v7). Extended manifest; replaces `ctrl_configure` when exported. |
| `ctrl_reset`            | `void (void)`                                | **Optional** (ABI v7). Respawn: drop mission state, keep one-time init. |
| `ctrl_get_control_rate` | `float (void)`                               | **Optional** (ABI v7). Wanted tick rate in Hz; 0 = host default. Asked before `ctrl_init`. |

`debug[i]` is graphed/logged as `dbg/<name_i>`, where names come from
`ctrl_get_debug_names()` in order.

### Choosing a vehicle (ABI v5)

`ctrl_get_vehicle()` returns one of the `CTRL_VEHICLE_*` numbers in
`controller_api.h` — `CTRL_VEHICLE_MENU` (0), the stock chassis, or one of the
nine built-in cars. It is read by the **Simulate Controller** screen only, and
only at start: the answer decides which car gets built, so it is asked *before*
`ctrl_init`, with no car and no manifest in existence yet. It must therefore
answer from a constant. A **Build & Reload** mid-session swaps the code driving
the car and never the car.

Aircraft and the debug VW Tiguan have no numbers: neither is something a car
controller drives. Designs saved in the garage have none either — they did not
exist when the DLL was compiled, so those are picked in the menu.

The host is the authority on what it honours. An unrecognised number, or a car
the player has not unlocked, is reported on the console and the menu's own pick
stands; the number is never a way around the picker's list. `0`, a controller
built before v5, and a DLL with no such export are all the same answer: whatever
the menu picked.

## ABI v7 (opt-in)

v7 exists so the same firmware can run in the simulator and on a real car. It
is **opt-in**: a DLL becomes a v7 controller only by exporting
`ctrl_abi_version()` (one line: `CTRL_DEFINE_ABI_VERSION()`). Every older
controller — the built-in ones, UserScripts, generated code — is driven exactly
as before, in the same frame, with the same signs.

For a v7 controller the host:

- **Checks the build.** `ctrl_abi_version()` returns the ABI it was compiled
  against and the sizes of `CtrlInputs` / `CtrlOutputs`. A newer ABI, or sizes
  that differ from the host's, and the DLL is refused with a message rather
  than driven with fields at the wrong offsets.
- **Fills the v7 tail of `CtrlInputs`:**

  | Field     | Type                 | Meaning |
  |-----------|----------------------|---------|
  | `tick`    | uint32               | Control ticks since the run started |
  | `flags`   | uint32               | `CTRL_IN_FLU` (always set for v7) |
  | `time_us` | uint64               | Sim time in exact integer microseconds |
  | `stamps`  | const `SensorStamp*` | One `{seq, t_sample_us}` per manifest entry |

  `seq` changes only when a sensor takes a **fresh** sample, so a 15 Hz ToF
  read inside a 100 Hz loop is recognisable as old; `t_sample_us` is when the
  value was sampled (low 32 bits of the µs clock — compare by difference).

  Sample timing: a sensor with its own rate, latency, phase offset or jitter
  is sampled on the **physics** step its clock lands on, so `t_sample_us` sits
  on the physics grid (2.5 ms at 400 Hz), not the control grid. Its latency is
  resolved to the physics step too. A sensor with no rate of its own takes one
  sample per control period, exactly `latency_s` before the tick that reads
  it. A design can also set a compute latency (a command reaches the
  actuators that long after its tick) and a jitter on `time_us`/`dt_s`. By
  default all of these are 0, which gives the older timing.
- **Uses the FLU body frame.** SI units; x forward, y left, z up,
  right-handed (ISO 8855 / ROS REP-103). A left turn is a positive yaw rate,
  a car at rest reads accel ≈ (0, 0, +9.81), an RF bearing is positive to the
  left. Older controllers keep the simulator's native frame (x right, y up,
  z forward, left-handed). `Editor/FrameConventionCheck` verifies the
  conversion against real rigid-body motion.
- **Takes steering in radians.** `actuator[6]` is the road-wheel angle,
  positive LEFT, clamped to full lock (older controllers: `[-1, 1]`, positive
  right).
- **Hands over an extended manifest** through `ctrl_configure2`, if exported.
  Each `SensorInfo2` is the v6 entry plus: `wheel_index`, the mount pose (FLU
  position and roll/pitch/yaw from the vehicle origin), `rate_hz`,
  `latency_s`, the encoder's `cpr`/`wrap`/`gear_ratio`, the motor's
  `kt`/`resistance_ohm`/`gear_ratio`/`efficiency`, `wheel_radius_m`, and the
  actuator slot's `units` (`CTRL_UNITS_VOLTS` for a motor behind a hobby ESC or
  a plain voltage drive, `CTRL_UNITS_AMPS_IQ` for an FOC drive). One extra
  `SENSOR_STEER_FB` entry describes the steering actuator: `actuator_index` 6,
  range ± full lock in radians. Bind parts by type and wheel index, not by
  name.
- **Reports an FOC-driven motor as `SENSOR_FOC_FB`** (tag 17) instead of
  `SENSOR_MOTOR`. Its slot takes Iq in amps (range ± the motoring current
  limit; negative while rolling forward is regen braking, zero coasts), and
  its six channels are what a real driver can measure: `[Iq A, Id A, ω_m rad/s
  (the driver's PLL), V_bus V, T_winding °C, fault bits]` — fault 1 =
  over-voltage (latched, bridge off), 2 = thermal derate, 4 = current-limited,
  8 = voltage-limited. There is no torque channel: torque is not measurable.
  The design picks the drive per motor (`MotorParams.driveMode`); a v6
  controller sees the same tag and slot units it would not understand, so FOC
  designs are for v7 controllers.
- **Reports a raw MEMS IMU part as `SENSOR_IMU6`** (tag 13): `[gx, gy, gz
  rad/s, ax, ay, az m/s²]` in the chip's own right-handed frame (x along the
  part's aim, y to its left, z up out of the package). Rotate it into the
  body with the entry's mount `rpy_rad`; `pos_m` is the lever arm, and the
  values include its centripetal and tangential terms. `rate_hz` is the
  chip's output data rate. The values carry the part's datasheet errors
  (noise density, bias instability, turn-on bias, scale and cross-axis error,
  digital low-pass, full-scale clip, LSB). The top-level `gyro[]`/`accel[]`
  stay the simulator's built-in body IMU.
- **Reports a steering-angle sensor as `SENSOR_STEER_ANGLE`** (tag 19):
  `[angle_rad]`, the bicycle-model road-wheel angle, + = left, after the
  servo's lag and the linkage backlash. It's an optional part (a pot or
  encoder on the steering), separate from the `SENSOR_STEER_FB` entry that
  describes the actuator.
- **Calls `ctrl_reset`** on a respawn or run restart, if exported, instead of
  `ctrl_shutdown` + `ctrl_init` + configure — the way an MCU never re-inits
  its peripherals.
- **Asks `ctrl_get_control_rate`** before `ctrl_init` and ticks the controller
  at the nearest whole divisor of the physics rate (which can be the physics
  rate itself). `ctrl_init` receives the rate actually used.

`Controllers/targets/sim/opus_main.c` is the reference v7 controller. It turns
`CtrlInputs` into the portable `TtMeas` (`Controllers/core/tt_types.h`), runs
the mission core, and turns the `TtCmd` (wheel torque per wheel, steer in
radians) back into actuator slots.

## Per-vehicle conventions

### Differential-drive robot
- `setpoint[0]` = forward velocity (m/s)
- `setpoint[1]` = yaw rate (rad/s)
- `wheel_vel[0]` = left wheel, `wheel_vel[1]` = right wheel (rad/s)
- `actuator[0]` = left motor, `actuator[1]` = right motor
- Geometry (wheel radius, track width) is duplicated in the controller
  (`ctrl_init` in `targets/sim/sim_main.c`) and in the Unity
  `DifferentialDriveVehicle` component — keep them consistent.

## Portability rule

Control logic (`common/`, `diffdrive_pid/`, `opus_mission/`, `core/`) includes
only its own headers and the C standard library — never `controller_api.h` or
anything Unity-specific. Only the *target* layer (`targets/sim/*.c`) touches
the ABI. The portable firmware core speaks `TtMeas` / `TtCmd` / `TtParams`
(`Controllers/core/`); a real board implements `hal/tt_hal.h` and calls the
identical core, which is what makes the same source run in sim and on
hardware. CMake's `TT_TARGET=embedded` builds just the portable libraries for
an MCU toolchain (`Controllers/cmake/arm-none-eabi.cmake`).

## Writing a controller against this spec

`UserScripts/` is the folder for controllers that are not part of the game. One
subfolder becomes one DLL named after the folder, built by the in-game
**Build & Reload** button — no CMake edit, no terminal. `UserScripts/guide.html`
is the illustrated walkthrough; `UserScripts/lib/tt_controller.h` is a
header-only convenience layer over the structs above (bounds-checked sensor and
camera reads, a PID, per-manifest motor writes). Nothing in it is required —
this document remains the contract, and a controller that includes only
`controller_api.h` is exactly as valid.
