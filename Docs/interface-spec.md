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
| `MOTOR` (3)         | `[voltage_V, current_A, torque_Nm]` feedback; also an **actuator** (see `actuator_index`, and `range_*` = ±maxVoltage) |
| `IMU` (4)           | `[gx,gy,gz, ax,ay,az]` (mirror of gyro/accel) |
| `CAMERA` (5)        | no floats — frame via `cam_pixels`/`cam_*`    |
| `SUSPENSION` (6)    | `[spring_force_N, compression_01, angle_deg]` |
| `BATTERY` (7)       | `[terminal_V, total_current_A, soc_01]` — bus voltage sags with load across the pack's internal resistance; a controller can voltage-compensate its motor commands |

Type tags are append-only (an old controller iterating the manifest simply
ignores unknown tags), so appending `SUSPENSION`/`BATTERY` did not change the
ABI version — it remains **v3**.

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

`debug[i]` is graphed/logged as `dbg/<name_i>`, where names come from
`ctrl_get_debug_names()` in order.

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

Control logic (`common/`, `diffdrive_pid/`) includes only `pid.h` /
`diffdrive_control.h` and the C standard library — never `controller_api.h` or
anything Unity-specific. Only the *target* layer (`targets/sim/sim_main.c`)
touches the ABI. A future `targets/arduino/` reads real peripherals and calls
the identical `diffdrive_update()`, which is what makes the same source run in
sim and on hardware.

## Writing a controller against this spec

`UserScripts/` is the folder for controllers that are not part of the game. One
subfolder becomes one DLL named after the folder, built by the in-game
**Build & Reload** button — no CMake edit, no terminal. `UserScripts/guide.html`
is the illustrated walkthrough; `UserScripts/lib/tt_controller.h` is a
header-only convenience layer over the structs above (bounds-checked sensor and
camera reads, a PID, per-manifest motor writes). Nothing in it is required —
this document remains the contract, and a controller that includes only
`controller_api.h` is exactly as valid.
