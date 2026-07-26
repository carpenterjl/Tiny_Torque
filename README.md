# Tiny Torque

***Big physics. Tiny cars. Your code takes the wheel.***

**Tiny Torque** is an RC racing sandbox where the driver can be *you* — or
*your firmware*. Build 1/10-scale cars part by part in a KSP-style garage
(motors, tyres, suspension struts, sensors, wings, batteries, paint), lay out
your own tracks with curves, banking, jumps and boost pads, then race bots,
split-screen rivals, or your whole LAN. Under the hood is a
millimetre-accurate physics model of a real backpack-class RC car — brush-model
tyres, brushed-DC motors with back-EMF, a hobby-ESC state machine, LiPo
discharge curves — so what you learn on screen is what happens on asphalt.

And that's the twist: the game doubles as a genuine **firmware lab**. Write
controllers in portable C — structured like embedded firmware — compile them
to a native DLL, and the simulation loads them to drive your car in real time,
streaming real sensor data (ToF rangers, encoders, IMU, camera frames) over a
clean C ABI, with live graphs and CSV telemetry to analyze. The same
controller source is meant to later compile unmodified for real
microcontrollers (Arduino/ESP32, STM32, Raspberry Pi).

> Developed under the working title *AI Hardware Control Sim* — you'll still
> see that name in the Unity project and save paths.

First vehicle: a differential-drive ground robot. There is also a drivable
4-wheel **car** on an outdoor dirt **track** for hands-on play-testing. A
quadcopter and hardware-in-the-loop over serial are planned follow-ons.

## Scenes

- **Bootstrap scene** (Tools ▸ AIHWSim ▸ Create Bootstrap Scene): the
  differential-drive robot on flat ground. Boots in **Autonomous** mode running
  the C controller (`controller.dll`) — the control-code demo.
- **Track scene** (Tools ▸ AIHWSim ▸ Create Track Scene): the car on an oval
  dirt track with berms, dirt jumps, a speed hump, obstacles, and a checkered
  finish line with lap timing. Boots in **Manual** mode so you can just drive.
- **Garage scene** (Tools ▸ AIHWSim ▸ Create Garage Scene): a KSP-VAB-style
  vehicle builder. Assemble a vehicle from a preset body (Box / Wedge / Buggy)
  plus **placeable parts** — wheels and sensors shown as stylized models (wheels
  with rims/hubs and a motor can when powered; camera and ToF modules; toggleable
  aim vectors). **Grab a part** from the icon palette — organized into
  **sub-categories** (Wheels / Sensors / Aero / Power / Misc); hovering an icon
  pops a tooltip with the part's name + description and a **live rotating 3D
  preview** of the actual model, and hovering a placed part's marker shows its
  info — drag the translucent ghost onto the body surface (scroll to rotate its
  heading), and click to drop; grab an existing part's marker to move it. The
  camera stays live while you hold a part: **orbit** (RMB), **pan** (MMB), and
  **zoom** (Ctrl+scroll) all work mid-drag. A **Snap 5mm** toggle (N) quantizes
  placement to a 5 mm grid. **Mirror ✕2** (X) places symmetric twins that
  stay in sync. The **PAINT** tab paints pixels straight onto the moulded body
  shells (Shell / LowRacer / Buggy): swatches + RGB brush colour, brush size,
  a **mirror brush**, Alt+click eyedropper, per-stroke undo, and Clear — the
  livery is stored in the vehicle JSON, so it saves, snapshots, and follows the
  car into LAN games automatically. Each **wheel** carries its position, heading, radius, an *allows
  steering* / *reverse steering* opt-in (with a per-wheel steer angle), and an
  optional on-board **motor** (the "powered wheel"); add camera / ToF / encoder
  **sensors** too. Any number of wheels in any layout (3-wheelers, 6-wheelers,
  rear-steer, etc.). A live **stats** panel shows mass, wheel/powered/steered
  counts, total stall torque, and estimated top speed. **Undo/redo** (Ctrl+Z /
  Ctrl+Y), **pan** (middle mouse), **orbit** (right mouse), and **focus** (F on the
  selected part) round out the camera/edit controls. Save designs as JSON under
  `UnitySim/Vehicles/`; **Drive** spawns the design on the track. The track pause
  menu's **Garage** button returns here.
- **Track Builder scene** (Tools ▸ AIHWSim ▸ Create Track Builder Scene): a
  tile-based map editor in the same VAB style. The map floor auto-generates as a
  grid of tiles that you **paint** (click + drag) with surfaces from the FLOOR
  palette — dirt, asphalt, grass, sand, ice, mud, rumble strip, boost pad,
  checker — each with its **own physics** (grip multiplier, rolling resistance,
  boost, rumble buzz) felt through the tires. WALLS (block, tire stack, tall
  wall, fence), OBSTACLES (ramp, speed bump, platform, cone, barrier), and MISC
  (start/finish gate, ordered checkpoints, light post, spawn point) place with a
  translucent **ghost** that grid-snaps (scroll rotates 15°, Esc cancels); tire
  stacks and cones are knock-aroundable at drive time. The **SPLINE tab** draws
  smooth **curved track ribbons**: click out control points (a Catmull-Rom curve
  passes through them), drag the sphere handles to reshape, click the ribbon to
  insert points, and set per-point **width, height, and bank angle** — the ribbon
  mesh (with its own collider and per-segment surface physics) redraws to fit.
  Splines can close into loops, grow **edge walls** and red/white **kerb
  stripes**, and be repainted per segment with any floor surface using the normal
  paint tool. Items place **onto** the ribbon too — even raised, banked sections
  — aligned to the surface, and re-seat themselves whenever the spline is
  redrawn. The map is **resizable
  per edge** while editing (tiles preserved), supports undo/redo (Ctrl+Z/Y),
  **T** toggles a straight-down map view, and maps save as JSON under
  `UnitySim/Tracks/`. **Drive ▶** loads your map into the track scene — laps
  count only after all checkpoints are crossed **in order** (`CP: n/N` on the
  HUD); the car spawns at the spawn-point marker (or behind the finish line, or
  map center). The track pause menu's **Track Builder** button returns here; the
  classic oval still loads when no custom map is active.

- **Menu scene** (Tools ▸ AIHWSim ▸ Create Menu Scene): the game's entry point.
  Behind the menu a live **attract loop** plays — a handful of AI cars drive laps
  around a random race circuit while a camera slowly orbits (falls back to a
  rotating showcar if the track can't build).
  **Single Player** — a full **race setup**: pick your vehicle and track, the
  number of **AI opponents** (0–7), their **difficulty** (Easy/Medium/Hard) with
  an optional **rubber-band** catch-up, how your own car is **driven** (Manual,
  Autonomous via the C firmware controller, or Autonomous via the bot AI), and the
  **lap count** — then Race (0 opponents + 0 laps = free drive), or jump to the
  Garage / Track Builder. **Multiplayer** — a **2-player split-screen** setup
  (per-player name, vehicle, and device: keyboard or a specific gamepad; shared
  track; first-to-N-laps race or sandbox) plus LAN, **Resume Drive** (saved
  session snapshots), and **Options** (volume, quality, fullscreen, vSync, mouse
  steering, assists, sim-realism, **telemetry logging**, player names — persisted
  to `UnitySim/Saves/settings.json`).

**Single-player races vs bots**: opponents drive a variety of preset cars in
distinct paint colours, following the track's racing line (spline centerline, the
oval loop, or ordered checkpoints) with pure-pursuit steering and corner-aware
speed. They share the map with real collisions, count laps/checkpoints like you,
and appear in the live standings banner + results overlay (Keep driving / Rematch
/ Main Menu). Three dedicated **race circuits** ship as track presets — **Boost
Speedway**, **Dust Devil Rally**, and **Neon Vortex** — closed-spline loops with
boost pads, jumps, and obstacles. Bots never write to your player profile.

**Telemetry logging is opt-in.** Sensor/telemetry CSV logging is **off by
default**; enable it in **Options ▸ Log sensor/telemetry data** (applies to the
next drive) or mid-session from **pause ▸ Settings…** (logging starts once you
close the menu). When on, a drive records to a temp file you then **Save
telemetry** to `UnitySim/TelemetryLogs/`.

**LAN multiplayer** (up to 4 players): **Host LAN Game** starts a listen server
(you play too) and announces it on your network; **Join LAN Game** lists
discovered games (manual IP entry works too — including over the internet with
port 7777 forwarded). Players join into **free roam** on the host's map with
their own garage vehicles; the host's Esc menu can **change the map** for
everyone and **start a race** — all cars teleport to a grid behind the line,
a 3-2-1 countdown freezes inputs, and first-to-N-laps rules apply with live
standings and shared results (host can rematch). The host simulates all
physics; clients stream inputs and render smooth interpolated cars. Windows
Firewall will ask to allow the game on private networks the first time you host
(the game uses **UDP 7777** for transport and **UDP 47777** for LAN discovery).
Wi-Fi is fine — the streams are a few KB/s and clients render 120 ms behind the
host to absorb jitter; what matters is that everyone is on the same network and
*not* a guest network, which usually blocks PCs from seeing each other.
Telemetry/graphs/autonomous controllers remain single-player features.

**Sharing & installers.** **Tools ▸ AIHWSim ▸ Build Standalone (Dev)** makes a
development build for one-PC testing (editor hosts, build joins via 127.0.0.1).
**Tools ▸ AIHWSim ▸ Build Standalone (Release)** makes the shareable build in
`Builds/Release/` — no dev watermark, and it boots straight into the main menu.
In a shipped build, saves/vehicles/tracks/telemetry live in the per-user
`AppData/LocalLow/AIHWSim/…` folder, so it runs from any install location. Zip
the release folder to share it as a portable game, or build a Windows installer
with the included **Inno Setup** script — see `UnitySim/Installer/LAN-Setup.md`
for the full build-and-share + LAN/firewall walkthrough. The shared build ships
without a controller DLL, so *Autonomous (C firmware)* is open-loop there; Manual,
Bot AI, split-screen, and LAN all work fully.

Split-screen races share the map with real collisions, per-player lap/checkpoint
HUDs in each viewport, per-player respawn, and a results screen (Keep driving /
Rematch / Main Menu). Completed laps also feed persistent **player profiles**
(`Saves/profiles.json` — best lap per track, totals). The pause menu can **Save
snapshot** mid-drive (car poses, velocities, lap + checkpoint state, sim time —
single-player and split-screen) and the menu's **Resume Drive** picks it back
up. Autonomous C-controllers, telemetry CSV, and the graph overlay remain
single-player features.

Both driving scenes support an in-game **Manual ⇄ Autonomous** toggle (**M**)
in single-player.

**1/10-scale RC physics + aerodynamics + assists**: the whole sim models a
**backpack-class RC/autonomous car** (F1TENTH-style — ~42 cm, ~1.8 kg, 66 mm
tires, 540-class brushed motors on a 2S pack with an ESC current limit,
~10 m/s top speed), with the suspension, brakes, motors, sensors (4 m
VL53L1X-class ToF), tracks, and cameras all sized to match — 1 Unity unit is
still 1 real meter, so sensor readings and speeds (shown in **m/s**) transfer
directly to real hardware. **Aerodynamics** is modeled physically: quadratic
body drag from a per-shape drag coefficient (new **Shell** and **LowRacer**
body styles are the slippery ones) and placeable **AERO parts** in the garage —
a rear **wing** with an attack-angle slider (more angle = more rear downforce,
more drag), a front **splitter**, **side dams**, and **canards** — each
applying its force at its mounted position, so a rear wing genuinely plants
the rear axle. Wings and canards are **angle-of-attack aware** flat plates:
the force comes from the actual airflow direction in body space, so nose-up
flight over a jump changes the downforce, mounting a wing **backwards (yaw
180°) produces lift**, attached flow stalls past ~15–24°, and side dams load
asymmetrically in slides (yaw moment). The stats panel solves the drag-limited
top speed, shows downforce/drag at speed, and reads out the **aero balance**
(% of downforce ahead of the CoM); the aero inspector shows each part's lever
arm relative to the CoM. Realism is the baseline: **assist sliders in Options**
(per player — steering help, stability control, traction control, ABS, each
0–100%) add arcade forgiveness on top; in LAN your assist prefs travel with
you and apply to your car on the host, and Autonomous mode always bypasses
assists so C firmware faces the raw physics. Old full-size-scale vehicle saves
are hidden from the pickers; the bundled maps were regenerated at RC scale.
Keyboard steering is shaped like a transmitter stick (digital A/D ramps toward
lock instead of stepping — the **KB steer smoothing** slider in Options tunes
or disables it; gamepad sticks are never shaped). Dynamic track props are
honest physics objects: cones are real weighted-base cones and tire stacks are
stacks of torus tires with convex hulls, friction, and damping, so they
scatter, tumble, and come to rest when hit instead of drifting away.

Suspension is **per wheel and adjustable** in the garage: each wheel has its own
spring **stiffness**, **damping ratio**, **travel**, a friction **grip** scalar,
and a **strut angle** that physically tilts the WheelCollider mount (so the
travel is inclined and the wheel carries a camber-like lean). Each wheel also has
a **visible coil-over strut** with a tunable **length** (0 mm = rigid mount, no
strut): the body-side mount is fixed, and a longer strut drops the wheel hub
below it while a rocker **motion ratio** softens the effective wheel rate and adds
travel (the inspector shows the resulting rate/travel; the damper is derived from
the scaled spring so the damping ratio stays constant). On track the strut
visibly compresses and extends as the wheel moves. The stats panel
adds ride frequency and static sag (flagged when it would bottom out). A separate
placeable **suspension sensor** part reads a chosen wheel's spring force (N),
normalized compression (0–1), and strut angle over the sensor ABI
(`SENSOR_SUSPENSION`, `[force, comp, angle]`) — graphed, CSV'd, and firmware-readable.

The menu and garage pickers ship **built-in preset vehicles** (★-prefixed:
**Rally Buggy** — soft long-travel 4WD; **F1 Racer** — stiff, low, winged, fast;
**Crawler** — huge-travel, high-grip, low-geared; **Drift Car** — loose rear end)
and **preset maps** matched to them (**Whoop Canyon** jumps course, **Monza Mini**
smooth GP circuit, **Boulder Basin** crawler field, **Slide Yard** low-grip drift
yard). Presets are read-only; loading one clones an editable copy that Save writes
to your library.

## High-fidelity mode (real-world controller validation)

The sim can model a physical 1/10-scale car closely enough to tune closed-loop
C firmware against it. Every feature is opt-in per design (old designs behave
bit-identically):

- **Powertrain**: Coulomb friction (`kt·I0`, with a dissipative breakaway branch),
  reflected **rotor inertia** (`J·gear²` folded into wheel spin inertia), and an
  **ESC pipeline** per motor — input deadband, PWM quantization (256–2048 steps),
  optional slew, and a first-order lag — between the commanded and applied volts.
- **Battery**: a placeable garage part (mass + tray position). The first pack
  powers the motor bus; terminal voltage sags `V = V0 − R_int·ΣI`, capping motor
  commands exactly like a real 2S under launch load. `SENSOR_BATTERY` (type 7)
  streams `[terminal_V, total_current_A, soc]` to firmware.
- **Steering**: an **Ackermann %** slider (0 = parallel, 100 = true geometry —
  the inner wheel steers sharper about a turn centre on the rear-axle line).
- **Tires/surfaces**: per-wheel **load sensitivity** (grip ∝ (Fz/Fz0)^−s) and
  **ballooning** (radius grows with wheel speed); dirt/grass/sand/mud floors add
  deterministic positional **roughness** forces (identical lap after lap).
- **Mass**: "Composite mass & CoM" computes total mass, centre of mass, and the
  inertia tensor from the chassis plus every part (each part has a mass, wheels
  carry their motors, the battery dominates) — the stats panel shows CoM, F/R
  weight split, and yaw inertia; moving the battery visibly changes handling.
- **Sensors**: per-sensor Gaussian σ, ADC quantization, random-walk **drift**,
  an **update rate** (sample-and-hold) and **latency** (delay ring); IMU picks up
  motor-speed-tracking **vibration**; the ABI `wheel_vel[]` can be corrupted with
  encoder CPR + noise. All randomness is **seeded** (Options ▸ Noise seed; the
  effective seed is stamped into the CSV sidecar) so runs are byte-reproducible.
- **Control loop**: Options ▸ **Actuation delay** postpones controller commands
  by N control ticks (transport dead-time) to rehearse the sim→real phase gap.

Iteration 22 replaced the physics floor itself (these apply to **all** vehicles):

- **Brush tyre model** — PhysX WheelCollider friction is gone. Each wheel's
  spin is integrated by the vehicle (`J·ω̇ = τ_drive − τ_brake − Fx·r`) and
  tyre forces come from a slip-ratio/slip-angle brush model with a friction
  ellipse, applied at the contact patch. The WheelCollider survives as
  suspension only. Why it matters: the old curve needed ~10× the slip real
  rubber does, so encoder scale error (11.6 %) and driven-slip loss (53 %)
  were simulator artifacts; under the brush model they measure ~0.0 % and ~1 %
  — physical values (see `Opus_Car_Spec/calibration.md`). Dev A/B switch:
  `TyreModel.Enabled`.
- **ESC drive/brake/reverse state machine** — negative throttle while rolling
  is a proportional shorted-winding brake (force ∝ duty × speed, fading to
  nothing at rest, drawing nothing from the pack); reverse engages only after
  ~150 ms in neutral at rest, exactly like a hobby ESC — including for manual
  driving. Optional drag brake %, brake strength %, and reverse-lock time per
  motor in the garage.
- **Battery state of charge** — a pack with a capacity (mAh; 0 = infinite)
  runs a coulomb counter and its open-circuit voltage follows the LiPo
  discharge curve (4.2 V/cell fresh → plateau → knee), so the rail droops over
  a run and `sens/<battery>/soc` is live. Respawn restores a full pack.
- **Servo torque-speed limit** — with a stall torque set (garage ▸ Steering),
  available steering slew derates as cornering load (tyre lateral force ×
  trail) approaches stall: authority collapses exactly when the tyres work
  hardest. 0 = the legacy ideal servo.
- **Validation tooling**: press **J** on track for a live **step-response
  metrics** panel (rise time, overshoot, settling time, steady-state error
  between setpoint and measured channels); saving telemetry stamps the same
  metrics + seed + delay into the run's JSON sidecar — comparing sim vs. real
  reduces to diffing two sidecars. Start from the **★ Real Twin 1/10** preset
  (everything on at hardware-shaped values) and edit toward your car.

## The Opus Vector mission (a worked end-to-end example)

Everything above is machinery. **Opus Vector** is the first vehicle that uses it
all at once to do something measurable, driven start to finish by C firmware with
no human input.

- **The car** — `★ Opus Vector`, an F1TENTH-class 1/10 research car built entirely
  from real part datasheets. Every number traces to a row in
  [`Opus_Car_Spec/`](Opus_Car_Spec/), and every row is tagged **published** (off a
  datasheet), **derived** (computed, formula shown), or **estimated** (basis
  stated). Castle 1410-3800Kv motor, 2S 7.4 V pack, Savox SC-1251MG servo, three
  VL53L1X rangers, a BNO055 IMU, four 4096-CPR wheel encoders — 2131.5 g all up,
  itemised in [`mass_budget.md`](Opus_Car_Spec/mass_budget.md). Manufacturer PDFs
  are in `Opus_Car_Spec/datasheets/`.
- **The track** — `★ Opus Proving Ground`, a 40 × 20 m asphalt strip whose spline
  is laid out from the manoeuvre itself, so the road and the controller agree on
  where the corner is.
- **The firmware** — `Controllers/opus_mission/`, built to `opus_controller.dll`
  and named by the design's `controllerDll` field. Portable C: it includes only
  `mission_cfg.h` and the shared PID, knows nothing about Unity, and the sim-side
  adapter (`targets/sim/opus_main.c`) is the only file that touches the ABI.

The mission: arm and self-check → accelerate to 4.5 m/s → hold it for exactly
**14.5 m** → turn **45° left without slowing** → **7.5 m** more at speed → brake to
a standstill in exactly **1.5 m** (9.0 m total from the turn exit). Press **K** on
track for the mission HUD, which shows connection/arm state, fault bits, the live
phase, and the controller's odometer next to ground truth.

Measured against ground truth, not the controller's own odometer (iteration 22,
brush tyre model + ESC state machine, recalibrated by the same procedure):

| | target | actual | error |
|---|---|---|---|
| Constant-velocity leg | 14.5 m | 14.486 m | −14 mm |
| Turn | 45° | 45.19° | +0.19° |
| Post-turn leg | 7.5 m | 7.516 m | +16 mm |
| Braking distance | 1.5 m | 1.542 m | +42 mm |
| **Total from turn exit** | **9.0 m** | **9.058 m** | **+58 mm** |

Speed held 4.43–4.50 m/s across both legs *and* the turn — and, unlike iteration
21, on calibration constants a physical car would recognise (encoder scale 0.000
instead of the PhysX-artifact 0.116; traction efficiency 0.99 instead of 0.47).
The full artifact→physical story, the ESC-brake rear-grip cap, and the first
measurable `CAL_BRAKE` are written up in
[`Opus_Car_Spec/calibration.md`](Opus_Car_Spec/calibration.md).

Runs are scored unattended by `Tools ▸ AIHWSim ▸ Run Opus Mission`, which drives
one mission and writes a result JSON plus a trace CSV; it also works headless via
`-executeMethod AIHWSim.EditorTools.OpusMissionRunner.RunHeadless`.

## Sensors & motors

Vehicles carry configurable parts whose readings are streamed to the C
controller each tick (ABI v3, `ctrl_configure` + a flat sensor block + an
optional grayscale camera frame), graphed live, and logged to CSV as
`sens/<name>/<field>`. The track car ships with a default loadout (forward
camera, three ToF sensors, wheel encoders, two rear drive motors); the garage
lets you build your own.

**Motors are real, per-wheel brushed-DC motors.** Wheels are placeable parts; a
wheel is driven only if its *Powered* toggle is on. In **Autonomous** mode the
controller commands a **voltage**
per motor (signed for direction) on that motor's actuator slot, plus a steering
servo (`actuator[6]`) and brake (`actuator[7]`); the resulting torque and current
**emerge from the vehicle dynamics** via back-EMF against the wheel speed the
physics produces (so climbing a ramp draws more current). **Manual** mode maps
throttle → full-scale voltage through the same model, so it "just drives" while
sharing the exact drivetrain physics. Motor parameters are editable in the garage
as either electrical constants (Kt, R, gear, Vmax…) or datasheet figures (stall
torque, no-load speed/current). See `Docs/interface-spec.md` and the reference
controllers `Controllers/car_sensors/car_sensors.c` and `targets/sim/car_main.c`.

## 3D part models

Part visuals are stylized low-poly **Blender meshes** (bodies, tires, battery
packs, antennas) exported as FBX under `UnitySim/Assets/Resources/PartModels/`,
with the editable source in `Blender/parts.blend`. At runtime
`PartMeshLibrary` loads a mesh by name and `PartVisualFactory` falls back to the
original code-built primitives whenever an asset is missing — so the game runs
unchanged without the meshes, and every pre-existing design keeps working. The
meshes are purely cosmetic (colliders stripped, physics untouched):

- **Wheels** come in three selectable styles per wheel — *slick*, *knobby*,
  *rally* (garage → wheel inspector → *Tyre style*). Each is a five-object
  assembly: tyre (rounded shoulders, sidewall bulge, bead transition, tread
  grooves or extruded lug blocks), rim (barrel, flange lip, tapered spokes with
  real thickness), hub, lug studs and a brake disc visible through the spokes.
  All three hold an outer radius of exactly 33 mm so the runtime's
  `radius / WheelAuthorRadius` scaling is 1.0 at stock size.
- **Bodies** — the *Shell* (touring), *LowRacer* (F1TENTH) and *Buggy* shapes are
  single closed shells lofted from keyframed cross-sections and subdivided once.
  A separate roof-width parameter pulls the upper surface in so the fenders crown
  over the wheels instead of the body reading as one smooth bar; the body line,
  roof edge and skirt carry edge creases so subdivision smooths the panels
  without softening the character lines. Wheel arches are recessed pockets whose
  lip is projected onto an exact circle and pushed inboard past the tyre, so you
  look into a real wheel well. Scaled to the design's body size and tinted by
  body colour; *Box*/*Wedge* stay primitive.
- **Battery** is a true 1/10 stick pack at 138 × 47 × 25 mm — softened corners,
  heat-shrink seam band, end connector housing, power leads and a balance plug.
  It is rendered at authored size with no runtime scaling.
- **Antennas** are a placeable cosmetic part (palette → *Antenna*) — knurled SMA
  base, hex coupling nut and a tapered rubber-duck whip — with position /
  heading / tilt / size, mirror symmetry, and save/load like any other part. The
  stock car ships with a pair on the rear deck.

Imported models are pinned to a deterministic scale/orientation by
`Assets/Editor/PartModelPostprocessor.cs`, which also imports the authored
normals rather than recalculating them (the meshes ship weighted/split normals
that keep creases and tread edges crisp). Set `PartMeshLibrary.Enabled = false`
to force the primitive look everywhere.

To re-author: the build is scripted, not hand-modelled. `Blender/mcp_helpers.py`
holds the shared rig (mesh-validation report, studio renders, contract check,
FBX export with the pinned settings) and `build_wheels.py` / `build_bodies.py` /
`build_small.py` rebuild each family from its parameter tables. Verify with
`PartModelValidator`, which fails the build if any asset drifts off its authored
size or overruns its triangle budget:

```bash
"E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -quit -nographics -projectPath "UnitySim" -executeMethod AIHWSim.EditorTools.PartModelValidator.Report -logFile pmv.log
```

## Arcade mode

A deliberately unserious second mode alongside the physics-honest one: item boxes,
power-ups, weapons, track limits and a live scoreboard. **Main Menu ▸ Single
Player ▸ Arcade mode**, or the same toggle on the split-screen page.

Arcade needs a race to mean anything — item boxes and race positions both want a
finish line — so ticking the box with a free-drive selected sets the lap count to
3 rather than doing nothing. It is refused outright on **Autonomous (C firmware)**
sessions: a boost or a spin-out would corrupt the controller-validation run the
mode exists to produce. `SessionConfig.SetSinglePlayer()` clears both flags, which
is the complete leak guard — garage Drive, builder Drive and the stale-LAN guard
all funnel through it, so no free-drive can inherit a stale arcade session.

| | |
|---|---|
| Use item | **Left Shift** (keyboard) · **X / square** (gamepad) |
| Items | boost, triple-boost, homing missile, dropped banana, shield |
| Roulette | weighted by live position — leaders draw boost/banana/shield, back-markers draw missile/triple-boost |
| Track limits | all four wheels off a surface below 0.90 friction for 2.5 s → a 2 s speed cap; two wheels off at an apex is racing, and jumps are exempt |
| Points | 15/12/10/8/6/4/2/1 on finish |

Bots use items too, on a 2 Hz policy with a randomised reaction delay — boost only
when pointed straight, missile only with a target in range, banana only with
someone close behind, and never within 1.5 s of GO.

## Track props (arcade mode)

The same pipeline builds the **track** props, in `Blender/props.blend` +
`build_props.py`, exported to `Assets/Resources/TrackProps/`. Twenty-eight
assets: four shared arcade objects (item box, missile, banana peel, shield orb)
and six props for each of four themes.

| Theme | Props |
|---|---|
| **Toy Workshop** | book stack, ruler ramp, toy brick, pencil, mug, tape arch |
| **Neon Grid** | pylon, light gate, light hoop, glow barrier, data stack, spire |
| **Beach Boardwalk** | palm, surfboard ramp, boardwalk rail, tiki torch, beach ball, sandcastle |
| **Volcano Foundry** | rock arch, obsidian block, steam vent, barrel, grate ramp, crag spire |

Everything is authored at true metric size — a mug really is 100 mm tall — which
is what makes the Toy Workshop theme work: the car is a 420 mm RC car in a human
world, so the scale is the joke rather than a problem to hide.

Two rules separate a track prop from a vehicle part. First, `PartMeshLibrary`
strips every collider on import, which is right for cosmetic vehicle geometry but
leaves a wall you can drive through — so a mesh prop is always a pair, the
imported shell plus an **invisible primitive hull authored in `TrackCatalog`**.
The hull is deliberately coarse: it is what the car, the ToF sensors and the
builder's selection ray all hit, and a box beats a 2k-tri mesh collider for every
one of them. Gates (tape arch, rock arch, light gate) get three hulls, not one,
so the opening stays open. Second, props stay on the default layer rather than
the viz layer, so the on-car camera sensor can actually see the scenery.

Props sit on the ground plane with their **origin at the base contact point**,
because `TrackFactory` drops each item onto the surface it was placed on. The two
exceptions are documented in the build script: the tape arch and the light hoop
are deliberately part-buried, because a ring standing on its rim holds its bore
70–110 mm off the deck and a 100 mm car noses straight into it.

Four themed circuits ship built from these families, each a different *shape*
rather than one oval in four colours — the spline carries elevation in its points'
y, banking in `rollDeg` and width per control point, so the circuit itself is the
content and the props only dress it:

| Circuit | Length | Rise | Max grade | Narrowest | Max bank | Signature |
|---|---|---|---|---|---|---|
| ★ Workshop Grand Prix | 94 m | 1.35 m | 9.7 % | 2.2 m | 10° | climbs off the bench onto a plank run with pencils rolling loose across it |
| ★ Neon Vortex II | 141 m | 1.20 m | 5.5 % | 2.4 m | 18° | a true figure-8 — the lap crosses over itself, 1.16 m of clearance, banking inverting through the bridge |
| ★ Boardwalk Cove | 111 m | 0.74 m | 10.4 % | 2.0 m | 22° | four whoops on a 6 m wavelength into a 22° bowl, out onto a pier |
| ★ Foundry Descent | 102 m | 1.96 m | 12.0 % | 2.2 m | 16° | a boosted climb to a 1.9 m gantry, a grate bridge, then the plunge |

Gradients stay under ~12 %: at RC scale that is dramatic to look at (the car is
0.10 m tall) while costing almost nothing in speed, since the rear pair make
~50 N of thrust against ~3 N of grade resistance.

Each has three checkpoints and twelve authored item boxes — some of them on the
elevated sections, which is why `BoxRow` takes a deck height. Authoring boxes on
a map suppresses `ArcadeDirector`'s automatic placement entirely, so a hand-placed
set is authoritative. Eight themed floor surfaces come with them (workbench,
carpet, neon grid, boardwalk, wet sand, lava rock, obsidian, grate); their
friction values double as the arcade track-limit classification, so carpet, wet
sand and lava scree read as off-track without any extra authoring.

Two placement rules are invisible until they bite. `TrackFactory` drops each item
from `y + 3` and takes the *highest* hit, so an item under an overpass snaps onto
the overpass — nothing is placed beneath the Vortex crossover. And the
narrow-bore props (tape arch 0.34 m, light hoop 0.40 m, rock arch 0.46 m) stay
*off* the racing line against a 0.20 m car; the hazards that are on the line —
pencils, barrels, beach balls, blocks — are things you can hit and survive.

In the Track Builder the props live under two new palette tabs — **ARCADE** (the
item box) and **SCENERY** (all 24, grouped under a header per theme).

Validate the props and the maps with:

```bash
"E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -quit -projectPath "UnitySim" -executeMethod AIHWSim.EditorTools.TrackPresetValidator.Report -logFile tpv.log
```

`TrackPresetValidator` checks the things that otherwise fail *silently*: an item
id that no longer resolves is skipped without a word by design (that is what lets
old saves load in new builds), a floor index past the end of the catalog throws
deep inside the mesh build, and a checkpoint sequence with a gap in it simply
never completes a lap.

It also reports the geometry of every ribbon (`[TPV] GEOM`) and builds each map
for real (`[TPV] BUILD`), which covers the two ways a 3D circuit goes wrong. A
gradient the car cannot climb just looks like a car that stops, so anything over
40 % fails and over 25 % warns. And a track that crosses itself is only a bridge
if the decks clear each other: the check compares every pair of points that are
far apart *along* the curve but within 1.5 m in plan view, and fails if the gap
is under 0.35 m — enough for the 0.10 m car plus the ribbon's 0.04 m skirt. A
0.2 m step would be an invisible wall at speed. Neon Vortex II is the only
preset that trips the overpass detector, which is how you know the figure-8 is
really crossing over itself:

```
[TPV] GEOM Neon Vortex II[0]: len=140.8m rise=1.20m grade=5.5% width=2.4m bank=18deg overpass(clear=1.16m)
```

## Layout

```
UnitySim/       Unity 6 project (host: physics, sensors, telemetry, graphs)
Controllers/    Portable C firmware + CMake build (the code under test)
Tools/          Interactive HTML tools — hardware→vehicle, control design, calibration
Blender/        Editable source (parts.blend) for the 3D part models
Opus_Car_Spec/  Datasheets, mass budget and calibration log for the Opus Vector
Docs/           Interface spec and notes
```

See `Docs/interface-spec.md` for the host↔controller ABI.

## Interactive tools

`Tools/` holds a set of self-contained HTML pages for getting your real hardware
into the game and your results back out. Open `Tools/index.html` in any browser —
there is no server, no build step and no internet connection involved.

| Page | What it does |
|---|---|
| **Car Setup** | A wizard from chassis to sensors. Enter the figures off your actual datasheets and export a vehicle JSON the garage can load and drive. |
| **Control Loop Lab** | Loads a vehicle, derives its plant constants, teaches the transient equations with live plots, then generates compilable C controller files with your tuned gains baked in. |
| **Calibration Companion** | Walks the measured-calibration procedure — encoder scale, coast-down drag, traction efficiency, brake slip — fits your data and hands you the `#define` block. |
| **Telemetry Analyzer** | Drop in a CSV from `TelemetryLogs/`. Channel picker, zoomable plots, step-response metrics, and two-run overlay for sim-versus-real comparison. |
| **Motor Converter** | Kv or a torque/speed datasheet in; `Kt` and `R` out, with the algebra shown and a paste-ready motor block. |

Two things worth knowing:

- **Saving.** On Chrome or Edge the pages can write straight into your Vehicles or
  Controllers folder once you point them at it (File System Access API). Every
  other browser downloads the file and shows you the destination path.
- **Where vehicles go.** `UnitySim/Vehicles/` when running from the editor;
  `%USERPROFILE%\AppData\LocalLow\<company>\<product>\Vehicles\` for an installed
  build.

The pages carry their own copy of the game's schema and physics constants, so they
can drift from the C# and the firmware headers. A dependency-free regression test
checks them against the real thing — a saved vehicle round-tripping without losing
a field, the motor algebra reproducing `MotorModel`'s closed forms, the derived
plant constants landing on the values in `mission_cfg.h`:

```bash
node Tools/verify.js
```

Run it after changing `VehicleDesign.cs`, `MotorModel.cs`, or the vehicle constants
in `mission_cfg.h`, and update `Tools/shared/` to match if it complains.

### JGraph integration (optional)

If [JGraph](../JGraph) is installed, every page that draws a plot can hand its data
over for interactive figures and further analysis. The pages export a `.m` script
plus a `data.csv`, and JGraph runs the script headlessly:

```bash
jgraph -batch "speed_step.m" -showfigures -sd "C:\path\to\exported\folder"
```

For a live workflow, run the bridge once — it watches `Tools/jgraph-out/` and opens
any new script automatically, so clicking **Open in JGraph** pops a figure window:

```bash
powershell -ExecutionPolicy Bypass -File Tools\jgraph-bridge.ps1
```

Scripts are generated in MATLAB dialect rather than JGS on purpose: JGS's `let`
requirement and index base come from per-user settings that JGraph honours even in
batch mode, so a generated JGS script could behave differently on another machine.
Without JGraph the pages plot everything themselves — none of this is required.

## Prerequisites

- **Unity 6 LTS** (6000.0.x) via Unity Hub.
- **CMake** 3.15+ and a **64-bit C toolchain**. `build.ps1` auto-detects, in order:
  MSVC (Visual Studio Build Tools with the "Desktop development with C++"
  workload) or **mingw-w64** — `winget install BrechtSanders.WinLibs.POSIX.UCRT`
  is enough, no Visual Studio required. It must be 64-bit: Unity will not load a
  32-bit plugin, and `build.ps1` refuses a `gcc` whose `-dumpmachine` is not
  `x86_64-*`. MinGW builds link the runtime statically (`-static -static-libgcc`),
  because a DLL importing `libgcc_s`/`libwinpthread` fails to load in Unity with a
  bare "LoadLibrary failed".

## Getting started

1. **Build the controller DLLs**
   ```powershell
   cd Controllers
   ./build.ps1
   ```
   This compiles `controller.dll` (diff-drive), `car_controller.dll` (car speed
   PID), `car_sensors_controller.dll` (the ABI v2 sensor demo), and
   `opus_controller.dll` (the Opus Vector mission), and copies them into
   `UnitySim/Assets/Plugins/x86_64/`.

2. **Open the Unity project** (`UnitySim/`) in Unity Hub. Let it import.

   **One-time input setup**: the project uses the Input System package for
   gamepad support. On first open, if prompted, accept enabling the new input
   backend. Then set **Edit ▸ Project Settings ▸ Player ▸ Active Input Handling
   = Both** (Unity restarts). Keyboard works regardless; a gamepad needs this.

3. **Create the scene**: menu **Tools ▸ AIHWSim ▸ Create Bootstrap Scene**.
   This makes `Assets/Scenes/SimMain.unity` with a single `SimBootstrap` object
   that builds the ground, robot, camera, graphs, and control loop at Play time.

4. **Press Play.**
   - Drive open-loop / command setpoints with **W/A/S/D** or arrow keys
     (forward = velocity setpoint, turn = yaw-rate setpoint; the C controller
     closes the loop on the wheels).
   - Live graphs overlay top-left. Keys: **G** toggle, **P** pause,
     **[** / **]** shrink/grow the time window.
   - **Reload Controller DLL** button (top-right) hot-reloads after a rebuild —
     no need to leave Play mode.

5. **Analyze**: telemetry is recorded to a **temporary** session file that is
   overwritten each new drive and discarded when the session ends — it is only
   written to `UnitySim/TelemetryLogs/<timestamp>_<controller>.csv` (plus a JSON
   metadata sidecar) when you explicitly **Save telemetry** from the pause menu.
   Leaving a drive via the pause menu's **Garage** or **Quit** first prompts you
   to save or discard the unsaved log.

## Driving the car (Track scene)

Create the track with **Tools ▸ AIHWSim ▸ Create Track Scene**, then Play. It
starts in **Manual** mode.

| Action        | Keyboard / Mouse        | Gamepad                    |
|---------------|-------------------------|----------------------------|
| Throttle / reverse | W / S or ↑ / ↓     | Right / Left trigger       |
| Steer         | A / D or ← / →           | Left stick X               |
| Brake         | Left Ctrl                | ⓔ (east button)            |
| Handbrake     | Space                    | ⓐ (south button)           |
| Respawn       | R                        | ⓨ (north button)           |
| Manual ⇄ Auto | M                        | Start                      |

Send the car off a dirt jump, weave the cone slalom, and cross the finish line
to start the lap timer (bottom-right). Press **M** to hand control to
`car_controller.dll`, which then holds the stick-commanded speed while you still
steer; press **M** again to take back over.

## The tune/iterate loop

1. Edit gains or logic in `Controllers/` (e.g. `targets/sim/sim_main.c`,
   `common/pid.c`, `diffdrive_pid/diffdrive_control.c`).
2. `./build.ps1`
3. Click **Reload Controller DLL** in the running sim.
4. Watch the graphs; check the CSV.

## Runtime architecture

- **Fixed-rate loop** (`Core/SimulationRunner`): physics at 500 Hz, controller
  at a chosen integer division (default 100 Hz) with zero-order-hold on
  actuator commands — deterministic and representative of real loop timing.
- **Native bridge** (`Bridge/`): manual `LoadLibrary`/`GetProcAddress` on a
  shadow copy of the DLL, enabling hot reload; blittable structs mirror the C
  ABI for zero-copy `ctrl_step` calls.
- **Vehicle** (`Vehicles/DifferentialDriveVehicle`): force-based traction model
  (slip-driven longitudinal + lateral friction, friction-circle clamp) on a
  frictionless-contact chassis — no WheelCollider.
- **Sensors** (`Sensors/`): IMU + wheel velocity always, plus a configurable
  rig of part-based sensors (camera, ToF, encoders, motor feedback) assembled in
  the garage; all with configurable bias/noise/quantization.
- **Telemetry** (`Telemetry/`): ring-buffered hub feeding the GL graph overlay
  and the CSV logger.

## Roadmap

- Quadcopter vehicle + attitude/rate PID cascade and motor mixer.
- Hardware-in-the-loop: stream sensor data to a real MCU over serial, read back
  actuator commands.
- `Controllers/targets/arduino/` PlatformIO project reusing `common/` sources.
