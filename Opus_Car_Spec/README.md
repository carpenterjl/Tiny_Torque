# Opus Vector — 1/10-scale autonomous RC platform

This folder is the engineering record behind the **Opus Vector** vehicle preset in the
simulator (`UnitySim/Assets/Scripts/Garage/VehiclePresets.cs`). Every number the preset
feeds to the physics engine traces to a row in [`sim_mapping.md`](sim_mapping.md), and
every row is tagged with where it came from.

The car is an **F1TENTH-class research platform**: a 1/10 touring/rally rolling chassis
carrying a single brushless drive motor, a steering servo, a Raspberry Pi 5, and the
sensor suite an autonomous RC car actually uses — three time-of-flight rangers, a 9-axis
IMU, four wheel encoders and a forward camera — under a 3D-printed shell.

## The three-tag rule

RC vendors publish *marketing* specifications, not *engineering* datasheets. Castle
publishes Kv, mass and dimensions for the drive motor and states nothing about winding
resistance, no-load current or rotor inertia — parameters the simulator's DC machine
model requires. Rather than quietly inventing them, every parameter carries a tag:

| Tag | Meaning |
|---|---|
| **published** | Copied from a manufacturer datasheet or product page. Source URL given. |
| **derived** | Computed from published values by a stated formula. The formula is shown in [`derived_parameters.md`](derived_parameters.md). |
| **estimated** | Neither published nor derivable. A defended engineering estimate, with its basis stated and its sensitivity noted. |

Anything tagged **estimated** is a place where a bench measurement on the real car would
improve the model. [`calibration.md`](calibration.md) records the measurements that were
actually taken against the simulator.

## Bill of materials at a glance

| Subsystem | Part | Mass | Tag |
|---|---|---:|---|
| Drive motor | Castle Creations 1410-3800Kv, 4-pole 12-slot sensored | 239 g | published |
| ESC | 1/10-class sensored brushless ESC, 2–3S | 82 g | estimated |
| Steering servo | Savox SC-1251MG low-profile digital | 44.5 g | published |
| Battery | 2S 7.4 V LiPo shorty pack, ~5200 mAh | 265 g | estimated |
| Range ×3 | STMicroelectronics VL53L1X ToF | 2 g ea | published |
| IMU | Bosch Sensortec BNO055 | 3 g | published |
| Encoders ×4 | Magnetic quadrature, 1024 lines / 4096 counts | 6 g ea | estimated |
| Camera | Raspberry Pi Camera Module 3 | 12 g | estimated |
| Compute | Raspberry Pi 5, 8 GB + active cooler | 71 g | published |
| Structure | Rolling chassis, deck, printed shell, wiring | 1094 g | mixed |
| | **Total** | **2131 g** | see [`mass_budget.md`](mass_budget.md) |

## Modelling compromises (read before trusting the sim)

1. **One real motor becomes two simulated motors.** The simulator gives every powered
   wheel its own motor; the real car has a single centre motor driving both rear wheels
   through a differential. The preset therefore uses **two rear motors carrying the real
   motor's electrical constants with the ESC current limit split in half** (30 A each,
   60 A total). Total wheel torque and total battery draw then match the real
   single-motor drivetrain. Without the split the car would have exactly twice the real
   thrust. The behavioural difference that remains is that the sim pair acts as a
   *locked* torque split rather than an open differential — it matters in a corner, not
   in a straight line, and the mission's turn is gentle enough that it is negligible.

2. **The brushless motor is modelled as its DC equivalent.** The simulator implements a
   brushed DC machine (back-EMF, winding resistance, Coulomb and viscous friction, rotor
   inertia). A three-phase BLDC driven by a sinusoidal/trapezoidal ESC is well
   represented by that model at the shaft, using `Kt = Ke = 60/(2π·Kv)` and the
   line-to-line resistance. What is *not* represented: commutation ripple, phase
   advance, and the difference between sensored and sensorless startup.

3. **Three ToF rangers stand in for a planar LiDAR.** Most F1TENTH builds carry a 2D
   LiDAR. The simulator models directional range finders, not a scanning LiDAR, so the
   preset specifies three real VL53L1X sensors (forward, ±32°) rather than pretending a
   LiDAR exists. This is a real and buildable configuration, just a cheaper one.

4. **The mission controller does not use the camera or the rangers.** They are present,
   sampled and logged because the real vehicle has them, but the manoeuvre in
   `Controllers/opus_mission/` is dead-reckoned from wheel encoders and the IMU. The
   forward ranger is used only as an emergency abort.

## Folder index

| File | Contents |
|---|---|
| [`bill_of_materials.md`](bill_of_materials.md) | Every part, its published specifications, and its source URL |
| [`derived_parameters.md`](derived_parameters.md) | The formulas turning published specs into simulator parameters |
| [`mass_budget.md`](mass_budget.md) | Line-by-line mass build-up and the resulting centre of mass |
| [`sim_mapping.md`](sim_mapping.md) | Real parameter → simulator field, with tags |
| [`opus_vector_parameters.json`](opus_vector_parameters.json) | The same data, machine-readable |
| [`calibration.md`](calibration.md) | Measurements taken against the simulator, and what they corrected |
| [`datasheets/`](datasheets/) | Manufacturer PDFs — see [`datasheets/SOURCES.md`](datasheets/SOURCES.md) |
