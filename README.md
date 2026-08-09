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
  shells (Shell / LowRacer / Buggy, and the TinyTorque cars' paint panels —
  their chrome, glass and lights are immune): swatches + RGB brush colour, brush size,
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
  redrawn. Selecting a placed item gives **rotate**, a **scale** slider
  (0.2–5×) and — on anything knock-aroundable — a **Pinned** toggle that makes
  it inert scenery; Shift+scroll resizes an item while you place it. The map is
  **resizable per edge** while editing (tiles preserved, ±1 and ±5, up to 80
  tiles a side) with a **tile size** control beside it (0.5–3 m per tile, which
  is how a 112 m map fits in 56 tiles); item placement always snaps at about a
  metre regardless. It supports undo/redo (Ctrl+Z/Y),
  **T** toggles a straight-down map view, and maps save as JSON under
  `UnitySim/Tracks/`. **Drive ▶** loads your map into the track scene — laps
  count only after all checkpoints are crossed **in order** (`CP: n/N` on the
  HUD); the car spawns at the spawn-point marker (or behind the finish line, or
  map center). The track pause menu's **Track Builder** button returns here; the
  classic oval still loads when no custom map is active.

- **Menu scene** (Tools ▸ AIHWSim ▸ Create Menu Scene): the game's entry point,
  wearing the TinyTorque showroom look since the arcade UI pass — deep navy
  panels, champagne-gold accents, rounded corners, the Bahnschrift display font,
  and the title key art as the main menu's backdrop. First boot plays the
  **intro video** (StreamingAssets, skippable with any input) into the **title
  card** ("press any button"); returning from a race skips both. Behind the menu
  a live **attract loop** plays — AI cars driving a random circuit — and after
  ~20 s of idling on the main menu the panel hides so the attract runs
  full-screen, arcade style, until you touch anything. Every menu (and the
  pause/LAN/results/settings panels) is fully **gamepad-navigable**: d-pad or
  left stick moves a gold focus ring, A activates, B backs out, left/right
  nudges sliders and cycles pickers; the OS cursor hides while the pad is the
  active device. The whole UI also scales with resolution (authored at 1080p,
  crisp at 4K, no overflow at 720p), and scene changes fade instead of cutting.
  **Single Player** — a **list of modes**, each opening its own setup screen so
  every screen shows only the controls that mode actually has: **Race**,
  **Free Roam**, **Demolition**, **Capture the Flag**, **Soccer**, and
  **Simulate Controller** (run your compiled C firmware — see
  [Running your controller](#running-your-controller-and-rebuilding-it-without-leaving-the-game)),
  plus shortcuts to the Garage and Track Builder. A race screen carries your
  vehicle (or the **Showroom** — see below), the track, the number of **AI
  opponents** (0–7), their **difficulty** (Easy/Medium/Hard) with an optional
  **rubber-band** catch-up, how your own car is **driven** (Manual, Autonomous
  via the C firmware controller, or Autonomous via the bot AI), the **lap
  count** (0 opponents + 0 laps = free drive), and **Results wait** — how long
  after the *first* car finishes before the results screen appears and the
  stragglers are called DNF (default 30 s; 0 waits for the whole field, which
  a bot stuck against a wall will never satisfy). The Free Roam screen carries a
  **Map** picker of its own — see [Free roam](#free-roam-torque-falls-35-city-props-one-town).
  Every setup screen also carries a loud, magenta, **temporary Dev mode** toggle
  that treats every unlockable as owned so the collection can be tested without
  grinding for it; it overrides the two gates rather than granting anything, so
  the real profile is untouched and turning it off restores it exactly.
  *Remove it before shipping* — the removal list is on `Progression.DevUnlockAll`.
  **Multiplayer** — a **2-player split-screen** setup (per-player name, vehicle,
  and device: keyboard or a specific gamepad; shared track; first-to-N-laps race
  or sandbox) plus LAN, **Resume Drive** (saved session snapshots), and
  **Options** (volume incl. a **music** slider, quality, fullscreen, vSync,
  mouse steering, assists, sim-realism, **telemetry logging**, player names —
  persisted to `UnitySim/Saves/settings.json` — and a **Cheat Codes** page under
  EXTRAS).

- **Showroom** (from the Single Player and LAN pages): the arcade-facing car
  picker. The selected car turns on a lit podium — spin it with the right stick
  or a right-mouse drag, hold throttle to rev it, honk its horn — with
  **SPEED / ACCEL / HANDLING** bars derived from the real physics stats (top
  speed from the motor-vs-drag balance, thrust-to-mass, a composite of yaw
  agility, weight balance, ride response and steering authority — aero kits
  genuinely move the bars), a per-car **Special** slot reserved for future
  arcade abilities, and **Customize**: horn, wheel finish, topper (light bar /
  pods / antennas), aero kit and paint, saved per vehicle as a loadout in
  `Saves/progress.json` and applied whenever that car is picked. Locked cars
  show greyed with a padlock — visible on purpose.

- **Progression**: winning a race (against at least one opponent, local or LAN)
  opens a **mystery item** on the results screen — one random unlock from a
  24-item pool of preset cars (TT Patrol, TT Baja, Real Twin, Opus Vector and
  the four Legendary cars — the TT Coupe is the starter and always yours),
  horns, wheel finishes, toppers,
  aero kits and premium paints. When the pool runs dry, wins pay **XP** toward a
  player level (shown as `Lv N` in LAN rosters); podium finishes bank smaller
  grants. One global local profile (`Saves/progress.json`), shared by
  split-screen — the couch shares the toy box. **Cheat codes** exist: each
  locked item has a word (they're puns; `donut` is real), typed into Options ▸
  Cheat Codes. The rest are in `UnlockCatalog.cs`, which is the intended
  spoiler. Gating lives only in the pickers — bots still drive locked cars, LAN
  peers' designs always build, and the engineering garage is never gated.

**Single-player races vs bots**: opponents drive a variety of preset cars in
distinct paint colours, following the track's racing line (spline centerline, the
oval loop, or ordered checkpoints) with pure-pursuit steering and corner-aware
speed. The pack spreads across the road on straights — each bot carries its own
lateral bias and slow weave, sized to the local track width — and gathers back
onto a real out-in-out line through corners: wide on approach, cut to the apex,
released wide on exit. Hard bots use most of the corridor and barely weave; Easy
bots wander half the road and only half-commit to the line. The spread reserves
half a car plus a margin from every edge, so bots never farm their own
track-limit penalties. They share the map with real collisions, count laps/checkpoints like you,
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
standings and shared results (host can rematch). Windows Firewall will ask to
allow the game on private networks the first time you host (the game uses
**UDP 7777** for transport and **UDP 47777** for LAN discovery). Wi-Fi is fine —
the streams are a few KB/s; what matters is that everyone is on the same network
and *not* a guest network, which usually blocks PCs from seeing each other.
Telemetry/graphs/autonomous controllers remain single-player features.

**Every machine drives its own car.** Each player simulates the car they are
steering and streams the result at 60 Hz; the host follows each client's car
with a kinematic copy and relays it on to everyone else, who render it as an
interpolated ghost ~60 ms in the past. So your own controls answer immediately —
there is no round trip between pressing a key and seeing the car respond, and no
correction snaps, because nobody ever overrules you about your own car. This is
how racing games generally do it (and unlike a shooter, where the server is
right about where you are): the alternative — the host simulating everyone and
clients predicting — costs about 170 ms of control lag here, or a permanent
rubber-band if you correct it, because PhysX cannot rewind a single car.

The host is still the authority on everything *shared*: laps and checkpoints,
race state and standings, item pickups, and every random roll. It keeps those by
having a real collider for each client's car to be adjudicated against — which
is why the follower is a moved rigidbody and not a row in a table. When an
arcade effect lands on a car the host does not simulate, the host still rolls
the dice (which way the spin throws you, how the wreck tumbles, where the
recovery sets you down) and ships the numbers to that car's owner to apply, so
both machines agree about the same hit. Track limits are the one judgement that
moves the other way: a kinematic copy has no wheels touching the road, so each
owner tests its own car and sends the verdict up with its pose.

One consequence worth knowing: cars no longer trade momentum in a collision.
Each machine treats everyone else's car as immovable, so a shunt pushes you and
not them. That was already true between clients; it is now true of the host too.

**Sharing & installers.** **Tools ▸ AIHWSim ▸ Build Standalone (Dev)** makes a
development build for one-PC testing (editor hosts, build joins via 127.0.0.1).
**Tools ▸ AIHWSim ▸ Build Standalone (Release)** makes the shareable build in
`Builds/Release/` — no dev watermark, and it boots straight into the main menu.
In a shipped build, saves/vehicles/tracks/telemetry live in the per-user
`AppData/LocalLow/AIHWSim/…` folder, so it runs from any install location. Zip
the release folder to share it as a portable game, or build a Windows installer
with the included **Inno Setup** script — see `UnitySim/Installer/LAN-Setup.md`
for the full build-and-share + LAN/firewall walkthrough.

A release build carries the **whole C workspace**: `Controllers/` and
`UserScripts/` are copied next to the exe by `ControllerSourceShipper`, a
post-build hook, and the installer packs them along with everything else. So a
downloaded copy can write, compile and reload firmware exactly as the editor
does — *Simulate Controller ▸ Build & Reload* is not an editor-only button. What
the build cannot carry is the **compiler**: MSVC's licence forbids redistributing
it, and GCC is GPLv3 and about a gigabyte. `BUILDING_CONTROLLERS.txt`, written
beside the exe, says so and gives the one `winget` line that fixes it — or a
toolchain unpacked into `Toolchain\mingw64\bin` next to the game, which
`build.ps1` prefers over anything installed. Note that the controller DLLs
themselves are git-ignored, so a clone that has never run `build.ps1` produces a
build with no firmware in it; the post-build hook warns when that happens.
The installer installs per-user (`PrivilegesRequired=lowest`) for the same
reason — compiling a controller writes into the install folder, which Program
Files does not allow.

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
body drag whose coefficient and reference area are **measured off the body's own
geometry** — the game projects the shell's mesh onto a frontal grid at sixteen
stations down its length and reads the true silhouette area, how abruptly the
body builds and then abandons that area, how much of the outline is holes rather
than car, and which wheels are standing out in the airstream. So a bare tube
frame drags less than an enclosed cabin because it genuinely has less area, a
square-fronted box drags more than either, and reshaping a car in the garage
changes its top speed. Plus placeable **AERO parts** in the garage —
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

The menu and garage pickers ship **built-in preset vehicles** (★-prefixed). The
line-up is the three **TinyTorque show cars** — Blender-modeled vehicles imported
whole, with real chrome/gold/glass materials, emissive head and tail lights, and
their tintable paint panels:

- **TT Coupe** — RWD street sports coupe: gold rims, glass canopy, gold wing
  logo, an amber-tipped whip on the rear deck.
- **TT Baja** — 4WD tube-frame trophy buggy: balloon tyres on orange rims,
  authored shocks and A-arms, roof light pods, an orange flag whip.
- **TT Patrol** — RWD sedan: push bar, chrome steelies, grille strobes, twin
  trunk whips, and a roof light bar that strobes red/blue.

…then four **Legendary** cars, imported the same way and all four crate-only:

- **TT Rattletrap** — rusted-out wrecker: boom, hook and pulley on a plank deck,
  faded teal burning through to oxide, gap-toothed grin. Heaviest and draggiest
  car in the game, and the only one whose paint is a *baked texture* rather than
  a tintable panel (see below).
- **TT Redline** — #7 number car: gold flank flash, roundels, swan-neck rear
  wing, blue eyes. Light, stiff, quickest to turn in.
- **TT Highwing** — aero-era veteran in deep navy, #12, wing up on stalks in the
  clean air over the tail, chrome pipes and bumper. The most downforce of the
  four.
- **TT Autopia** — the 1955 Autopia Mark I ride car: pontoon flanks, oval grille
  with chrome bars, wraparound screen, bench seat, whitewalls on chrome
  hubcaps. Soft, slow-witted, and the only car with a musical horn as standard.

The first three carry the Blender **face rig** — eyes, lids, brows, teeth, gums
and a tongue, each with its own authored material and a per-car iris colour.
It imports as static geometry in a neutral expression: the FBX export is meshes
only, so the rig's driver empties do not come across and nothing blinks yet.

**One material could not come across as numbers.** Every other authored material
in this project is a set of constants, which is why the pipeline exports numbers
and rebuilds the material in C#. Rattletrap's paint is an object-space noise
multiplied by a height ramp, blending faded teal into oxide and dragging
roughness and metallic along with it — so `build_vehicles.py` bakes its colour to
`body_rattle_paint.png` (1024², smart-projected atlas) and ships it beside the
FBX, with the smoothness and metallic taken as the *measured* means of the same
mask. That texture is bound as its own accent token rather than through the
tintable paint channel, because the body material carries the livery texture and
one `mainTexture` cannot be both. Rattletrap therefore has no repaintable
panels, and the garage does not offer paint mode on it — its finish is the
character. The other three paint normally.

Also here: **Real Twin 1/10** (the calibration baseline) and **Opus Vector** (the
autonomous mission platform). Every car's body, wheels, light clusters and
antenna styles are also individual garage parts, usable on any design.

The **preset maps** are three race circuits (**Boost Speedway**, **Dust Devil
Rally**, **Neon Vortex**), the four TinyTorque themed circuits (**Downtown
Dash**, **Playroom Raceway**, **Enchanted Ascent**, **Graveyard Shift** — see
the map-pack section below), and the **Opus Proving Ground** measurement range.
Presets are read-only; loading one clones an editable copy that Save writes to
your library. (The retired vehicle presets — Rally Buggy / F1 Racer / Crawler /
Drift Car — and the retired maps — Whoop Canyon, Monza Mini, Boulder Basin,
Slide Yard, Workshop Grand Prix, Neon Vortex II, Boardwalk Cove, Foundry
Descent — still load from saved copies and render exactly as before; only the
preset rows are gone.)

## RC airplane (debug-only)

A .40-size sport trainer — 1.4 m span, 2 kg, 10×6 propeller — built from primitives
and flown from the keyboard or a gamepad. Like the full-scale Tiguan it is a **debug
vehicle**: it is not a `VehicleDesign`, no picker enumerates it, and there is no path
by which a race, a save file or a LAN session can reach it.

**To fly it:** `Tools > AIHWSim > Create RC Plane Scene`, then Play.

Controls follow **Kerbal Space Program**, and the panel does too — navball, altimeter,
throttle gauge and six stability-assist modes.

| | keyboard | gamepad |
|---|---|---|
| Pitch | `W` / `S` (W is forward, and forward is nose down) | left stick Y (back = nose up) |
| Yaw | `A` / `D` | right stick X |
| Roll | `Q` / `E` | left stick X |
| Throttle | `Shift` / `Ctrl` (**ratcheted** — it holds where you leave it) | `RT` / `LT`, analog |
| Cut · full | `X` · `Z` | `LB` · `RB` |
| SAS on/off · hold off | `T` · `F` (held) | D-pad up · — |
| SAS mode | `1`–`6` | D-pad left/right |
| View · Reset | `V` · `R` | `Select` · `North` |

Throttle integrates its input as a *rate* rather than tracking it, because a throttle
lever has no centring spring and a key and a trigger both do. Tracking directly would
idle the engine the moment you let go, and hands-off flight would be impossible. The
trigger is read as an analog value, so a light pull trims and a hard one sweeps.

**The previous map was a Mode 2 transmitter** (left stick throttle + rudder, right stick
elevator + aileron), which is what RC pilots actually fly and was right while the
aeroplane was purely a flying test article. It is a poor fit for a keyboard: a
transmitter's value is two proportional sticks, and a key is a switch.

### The panel

The **navball** is a real textured sphere — a hidden unlit ball on its own layer,
photographed each frame by a dedicated orthographic camera into a 256² RenderTexture.
The flat artificial horizon it replaces was honest only near level: it slid a quad by
pitch, so it had nothing to say about vertical flight, inverted flight, or heading at
all. A sphere has no such regions, because every attitude is just a rotation.

Nothing about it is authored. The ball carries the inverse of the aircraft's attitude,
so the nose is the centre of the disc by construction; markers project orthographically
(drop the z) and are **hidden when they fall on the far side** rather than mirrored to
the wrong place — the classic navball bug, and one that only shows itself in an attitude
you have to fly to reach. Even the skin's alignment to the mesh is *measured*: the
sphere's UV seam is read off its own vertices at build time (−90.47° as it happens)
rather than guessed and nudged until it looks right.

The **throttle** wraps the ball as an arc down its left side, idle at the bottom and
full at the top, so the setting is inside the same glance as the attitude and the fill
still climbs the way a lever does. It reads the *commanded* setting off the vehicle
rather than the stick, which is the whole point of a ratchet: the input is a rate, and
only the gauge says where the lever actually is. There is no panel behind the ball —
the render texture clears transparent and a bezel follows the limb, so the instrument
claims no area it does not use.

Because a navball is made almost entirely of sign conventions, they are pinned by a
bench rather than by eye — `[NAVB]`, 18 checks, all of which run headless in a second.
It asserts the things that look almost right when they are backwards: that the nose sits
dead centre across 24 attitudes, that astern is hidden, that 10° above the nose draws
*above* centre, that a nose-up aircraft in level flight shows prograde *below* centre,
and that 45° of bank swings the markers exactly 45°.

**Stability assist** offers six modes: Stability Assist (hold attitude), Prograde,
Retrograde, Target, Wings Level and Altitude Hold. `T` toggles, `F` suspends it while
held, `1`–`6` select. Deflect a stick past 10 % and you own that axis; let go and it
re-holds wherever you left it.

It lives on the **input** side, layered over the pilot's commands by `PlaneInput`, and
is attached only by the free-flight scene. `PlaneVehicle` states in its own summary that
it has no stability augmentation — the damping and stiffness are supposed to emerge from
panel geometry — so an autopilot hidden inside the vehicle would turn every row of the
`[AERO]` gate into a measurement of the autopilot. The scripted tests never build one.
Its gains are not new either: they are copied from the hold loops in `FlightTest.cs`,
tuned against this airframe, with their counter-intuitive signs restated at the copy.
Only two gains are new — bank-per-degree of heading error, and climb-per-metre of
altitude error — and both are marked as such. **SAS never touches the throttle**, as in
KSP: the ratchet is the pilot's, and an autopilot quietly moving it would make the gauge
a liar. It never touches the rudder either, because no tuned yaw loop exists anywhere in
this project and inventing one here would be authoring a coefficient by feel.

One honest limitation, reported rather than hidden: **an aeroplane cannot truly hold
prograde.** Putting the nose exactly on the velocity vector means flying at zero angle
of attack, and a wing at zero alpha is not carrying its weight — so prograde hold settles
into a shallow descent until the pitch clamp catches it. That is a property of a wing,
not a defect in the loop.

`V` cycles three views. It opens on the **ground station** — standing beside the strip,
watching the model — because that is how RC is actually flown, and it is the only view
in which the control reversal on a toward-you pass is real. A chase camera hides that
completely, and it is the single hardest thing about flying RC.

### How the aerodynamics works

Every lifting surface is cut into spanwise strips, and each strip asks one question per
step: *what is the air doing here?* The answer comes from `v_cm + ω × r`, so a strip on
a rolling wing sees a different vertical velocity — and therefore a different angle of
attack — than the one on the other side.

**That is the whole design.** Roll damping, pitch damping, weathercock stability, the
dihedral effect, adverse yaw and tip stall are not modelled; they are consequences of
where the strips are and which way they point. There is no authored damping coefficient
anywhere in `PanelAero.cs`, and there must never be one — the moment a rate-damping term
is added by hand, every test that measures a rate becomes a test of that number.

The propeller is coupled to the same `MotorModel` the cars use: the throttle sets a
voltage, the motor makes torque against its own back-EMF, and the prop takes torque away
as a function of shaft speed and airspeed. **RPM is never commanded** — it is the
equilibrium of those two, which is why thrust sags as the aeroplane accelerates and why
top speed is a consequence rather than a number anyone chose.

### The propeller wake

Behind the disc there is a tube of air the propeller has already accelerated, and most of
the tailplane, a third of the fin and the inboard wing are flying inside it.
`Slipstream.cs` gets that from momentum theory, which has **no free parameters** — feed it
a thrust and it returns a velocity field:

```
T = 2·ρ·A·v_i·(V + v_i)   ⇒   v_i = ½(−V + √(V² + 2T/(ρA)))
Δv_∞ = 2·v_i              r_∞ = R·√((V + v_i)/(V + 2v_i))
```

Which strips are inside the tube is a segment-circle intersection, so it is exact rather
than approximately right: the tailplane is **35.7 %** immersed at rest, the fin 34.9 %,
the wing 8.8 % (the dihedral lifts it out). Two things follow that are the reason this is
not optional:

- **The tail works with the aeroplane standing still.** At full power and zero airspeed
  the tailplane sits in 73.6 Pa of dynamic pressure — over half its cruise value. Without
  the wake it is exactly zero, and no model could raise its tail on the takeoff roll,
  which they plainly do.
- **η_h stops being a number somebody authored.** It is 1.00 with the motor off and 1.38
  at full power, so the neutral point moves **8.6 % of chord** between them. That *is* the
  throttle-dependent pitch trim every tractor-prop aeroplane has, and nothing was added to
  produce it.

Momentum theory gives the disc and the far wake exactly and says nothing about the rate of
development between them, so the shape function there is ⚠ estimated — but constrained
rather than free: whatever increment it picks, the tube radius follows from continuity, and
`[ABENCH]` checks mass flow at forty stations. It barely matters on this airframe anyway,
since the wing sits 2.0 diameters behind the disc and the tail 4.3, both past the
transition. Swirl, P-factor and the windmilling case are declared out — momentum theory
disclaims the last of those, so rather than extrapolate into it the wake is switched off
when thrust goes negative.

### The `[AERO]` gate

```bash
"E:/Unity Hub/Editor/6000.1.15f1/Editor/Unity.exe" -batchmode -projectPath UnitySim -executeMethod AIHWSim.EditorTools.FlightValidationRunner.RunHeadless -logFile aero.log -physSuite
```

Eight scripted flights, each run twice, greppable as `[AERO]`. Separate from the car's
`[PHYS]` gate on purpose: appending to it would change the summary line a build gate
watches even on a clean run. Add `-aeroOnly A3,A6` to narrow it while working on one row
— a narrowed run deliberately never prints `ALL PASS`, it says `SUBSET … NOT THE GATE`.

| test | checks | result |
|---|---|---|
| A0 ballistic drop | aero off, must fall at exactly `½·g·t·(t+dt)` | PASS — **1.5 mm** error over 3 s |
| A1 static thrust | motor/prop equilibrium, no aerodynamics at all | PASS — 11.207 N vs 11.21 predicted |
| A2 level turn | `n = cos γ / cos φ`, trigonometry only | PASS — **2.0007 g** vs exactly 2.000 |
| A3 stall | root must stall before tip; peak section C_L | PASS — **root at 29 % semi-span**, 8.89 m/s |
| A4 glide ratio | self-consistent with the model's own C_L/C_D | PASS — 9.441 vs 9.49, **0.5 %** |
| A5 phugoid | `∂T/∂V` measured by differential damping | PASS — **−0.3148** vs −0.3278 N/(m/s), **4.0 %** |
| A6 panel count | does the answer depend on the discretisation? | INFO — **0.74 %** across 14/29/58 panels |
| A7 timestep | P9's twin, 200/400/800 Hz | PASS — **0.25 %** spread, bank 0.00° |

**A3 is the row that justifies the whole model.** Every other test here could be passed by
a wing represented as one lift coefficient; what a single coefficient cannot have is a
*place* where it stalls. The wing carries −2° of washout, so the root must let go first —
and it does, at 29 % of the semi-span. That is what keeps a wing drop from becoming a spin,
and it is a consequence of the authored twist rather than a modelled behaviour.

It also produced a result nobody asked for. Measured against the free stream the wing
reached C_L **1.266**, above the section's own 1.20 maximum, which reads as impossible.
It is not — the propeller wake is blowing the inboard span. Washout raises the power-off
stall speed from 8.91 to 9.22 m/s; the wake's 1.06× blowing lowers it again to 8.95;
the measurement is **8.89**. A 0.6 % accounting for a number nothing was tuned to hit, and
the same effect that puts a lower power-on stall speed than power-off in every pilot's
handbook.

**A6 says 8 panels a side is enough**, and that decides against building a lifting-line
solve. Getting there found a real defect first: the hinge test was a binary "is this
strip's midpoint inside the aileron?", which quantised the control-surface *area* to panel
boundaries — the model flew an aileron 20 % too big at 4 panels and 18 % too small at 8.
A6 was reading that as discretisation error and reporting a confident 52.7 %
non-convergence. With the hinge taken as an exact interval overlap the spread is 0.74 %,
and `[ABENCH]` now pins the hinged area at 0.1512 m² across 4/8/16/32 panels a side.

A0's expected distance is **not** ½gt². Unity integrates semi-implicitly, which puts the
answer 36.8 mm further at 400 Hz over 3 s. Predicting that offset rather than widening a
tolerance around it is what lets the test pass to the millimetre.

A2 enforces the turn kinematically. The row's claim is narrow — *given an aircraft in a
banked level turn, does the modelled accelerometer read `cos γ / cos φ`?* — and that is a
question about the instrument, not the aerodynamics. It is the specific guard against
deriving body-frame acceleration the wrong way: differencing body-frame velocity drops
the ω×v transport term, which in a 60° turn is the entire 2 g. The car suite learned this
when its skidpad read 0.36 g against tyres delivering 1 g.

**A5 used to be this suite's open question, and the answer was that the textbook formula
was missing a term.** It measured ζ ≈ 0.16 against the classical ζ = 1/(√2·L/D) ≈ 0.078,
which implies a cruise L/D of 4.4 where the glide test measures 9.4, and that sat
unresolved for two milestones.

The classical result assumes **thrust does not change with speed**. A fixed-pitch
propeller on a fixed voltage is the opposite: fly faster, the advance ratio rises, C_T
falls, thrust drops — a force opposing every speed excursion, which is what damping is.
On this airframe ∂T/∂V = −0.33 N per m/s against a drag term of +0.29, so **the propeller
supplies more than half the damping**. The giveaway was in the old text: feed the correct
ζ back through the naive formula and it returns an L/D of 4.3, against the 4.4 that had
been written down as impossible. The measurement was never the thing that was wrong.

So A5 no longer compares ratios — it **measures ∂T/∂V and gates on it**. The same
disturbance is flown twice, once with the thrust frozen at its trim value, and since both
stages share a trim, an airframe and an α response (to 1.2 %), everything cancels but the
term under test:

```
∂T/∂V = 2·m·(σ_powered − σ_frozen) = 2 × 2.0 × (−0.12529 + 0.04658) = −0.3148 N/(m/s)
```

against −0.3278 from C_T0, J₀ and the motor constants alone — **4.0 %**, on a quantity
measured in flight versus one computed without reference to any flight. Damping *rates*
rather than *ratios*, because ζ divides by a frequency this aeroplane does not have.

**⚠ Three things this model is knowingly wrong or unproven about**, recorded rather than
tuned away:

- **The phugoid period is 8.15 s against π√2·V/g = 6.76 s, +21 %.** Not amplitude (a 7.5×
  sweep moves it 0.1 %), not the timestep (0.02 % at double rate), and reproducible to
  0 %. Lanchester's frequency assumes α is frozen; this aeroplane measurably does not
  freeze it — α moves −0.16°/(m/s) *in antiphase* with speed, cancelling part of the extra
  lift, because following a flight path that swings ±4° needs a rotation and the tail's
  pitch damping charges an α perturbation for it. `[ABENCH]`'s closed form for that lands
  at 8.82 s on a ⚠ C_mq estimated to 30 %. The direction and size are explained; nothing
  here is tight enough to gate, so the period is reported and the gate is on ∂T/∂V.
- **∂D/∂V comes out 36 % below the constant-α value** (0.186 against 0.290), which is the
  same α effect seen through drag. Consistent, but not separately confirmed. ⚠ open.
- **The airframe cannot sustain a 60° level turn under its own power** — it needs about
  4.2 N and the propeller supplies under 3. That is a real performance limit of a 0.57
  thrust-to-weight trainer, not a defect.

**Four defects the new rows found**, all fixed and all previously invisible:

- A5's estimator counted rising zero-crossings of airspeed. At ζ = 0.16 the amplitude
  falls by 0.37 per cycle, so the third crossing is already noise — and the test spent
  three milestones reporting "the phugoid damps out inside 1 cycle, too fast to time a
  period", which is **an instrument limit presented as a physical result**. Fitting
  `c + e^(σt)(a·cos ωt + b·sin ωt)` instead uses every sample rather than three of them
  and returns 0.2 % residuals off well under two cycles.

- `ResetVehicleTo` moved the Transform without calling `Physics.SyncTransforms()`, so for
  one step `PanelAero` built every strip's moment arm as
  `transform.position − Rigidbody.worldCenterOfMass` — the *whole teleport distance*. It
  showed up as −3.9 g one step after a reposition and as a lateral oscillation reaching
  180° of bank. Both looked like aerodynamics; neither was.
- The launch gate returned as soon as vertical speed and bank were small, which is a
  condition the reset establishes *by construction*. It was certifying the reset rather
  than a settled aeroplane. It now requires the condition to hold, and checks body rates.
- `HoldVerticalSpeed` drove the elevator straight from vertical-speed error — a
  proportional loop closed around two integrations, which rings. Nothing had exercised it,
  because A2 flies its turn kinematically. It is now a cascade through a clamped pitch
  attitude.

Declared omissions are listed in the class comments: quasi-steady aerodynamics (no
dynamic stall), no stall hysteresis, no Reynolds dependence, no fuselage aerodynamics
(so the model is ~5 % of chord more stable than the real aeroplane), no ground effect,
no slipstream swirl or P-factor, no wake at all from a windmilling propeller, and ground
handling well below the standard of the car's tyre model — which is why every scripted
flight test starts in the air.

### The authored scene

`Create RC Plane Scene` no longer produces one bootstrap GameObject that builds the
world at Play — it builds the world at **edit time**, with the same code the runtime
path uses. Every object — runway, pylons, aircraft, cameras, runner, SAS — is in the
hierarchy before you press Play, so the pilot's standing position, a SAS gain, a pylon
or a drone's circuit is an inspector edit rather than a script edit.

One builder serves both worlds through a small `SceneBuildContext`: its `default` IS
the old runtime behaviour (every branch reduces to the statement the builders already
contained), while the editor hands in `DestroyImmediate`, asset-backed shared materials
and a saved PhysicsMaterial — the four things that are illegal or leaky about doing at
edit time what `Awake` used to do at Play. `RcPlaneBootstrap` adopts an authored scene
through a `FlightSceneDescriptor` (serialized references — `GameObject.Find` survives
only as the fallback), **fills nulls only**, and still builds everything from code when
no descriptor exists, so the old one-object scene and every scripted test are
untouched. Re-running the menu item asks before overwriting, because the scene now
holds hand edits worth losing.

### The VTOL jet

`Tools > AIHWSim > Create VTOL Jet Scene`: the same airfield, a **Harrier-class
vectored-thrust jet** at roughly half scale — 4.6 m span, 600 kg, swept wing with
anhedral, bicycle-and-outrigger gear — flying on the **same measured panel model** as
the trainer. Nothing aerodynamic was authored to make it a jet: `JetSpec` holds a
thrust rating, a spool time constant, two nozzle stations, a travel range (0–98.5°,
the Harrier's, so it can brake and back up past the vertical) and four
reaction-control stations. Thrust is a force with a direction; the airframe is still
strips.

It spawns **hovering**, nozzles down, SAS holding attitude, throttle preset to the
predicted hover fraction — W/T from the mass table and the thrust rating, 0.654, the
first prediction the scene itself tests. `Num8`/`Num2` swing the nozzles aft and down
as a second ratchet (a nozzle lever has no centring spring either); everything else
flies exactly like the trainer.

Two physical honesty points. **At the hover the control surfaces are dead** — no
airspeed, no slipstream (a jet's tail is not in a propeller wake) — so control comes
from **puffers**, the Harrier's actual answer: a bleed budget of engine thrust ducted
to nose, tail and wingtip stations, driven by the same pilot commands the panels take,
moment = force × authored arm. The bleed is *subtracted* from the lift thrust, so full
stick in the hover genuinely costs height. And the balanced hover is a property of
**geometry** — the nozzles sit symmetric about the CG — not of a trim constant; move
the CG and the hover pitches, as it should. Authority fades to zero as the nozzles go
aft, so forward flight is pure panel control with no mode switch anywhere.

Declared validity edge: the panel model is incompressible and carries no Mach number,
so the jet is honest to about M 0.3 ≈ 100 m/s and merely silent beyond it. The engine
could push past that; the model does not stop it, it just stops being right — stated,
like the trainer's missing fuselage aero, rather than papered over with a fake drag
rise. Every `PlaneVehicle` change is behind `spec.IsJet` — a thrust-rating sentinel,
not a null check, because Unity deserializes a `[Serializable]` class field as a
non-null default — and the `[AERO]` gate came back **byte-identical** to prove the
trainer path gained nothing but the branch test.

### Weapons

The jet carries three, swapped with `Tab`, fired with `Space` (press for a missile or
a bomb stick, held for the cannon):

- **Homing missiles** with the Hydra's lock-on, automated: while missiles are
  selected, the nearest valid target inside a 15° forward cone starts acquiring on its
  own. The HUD draws a large twelve-segment circle over it — green and filling while
  the seeker works, then **solid red pulsing at 2 Hz** with a confirmation chirp.
  Guidance is lead pursuit at ~120°/s, dropping to a crawl inside the commit range so
  a hard break still defeats it — a missile that cannot be missed is a hitscan with
  theatrics. And the Hydra's hidden speed modifier is here in the open: missiles
  fired at **air** targets fly at twice the base speed. At 180 m/s and 400 Hz each
  step also carries a segment raycast, because a head-on drone closes faster than a
  trigger sphere can promise to notice.
- **A fixed cannon** — kinematic tracers, not hitscan, for the reason the arcade
  missile file states: a projectile that takes time to arrive can be dodged, led and
  watched. Heat-based: hold it too long and it locks out until it cools.
- **Carpet bombs** — a stick of six at 0.15 s intervals, pure ballistics seeded with
  the aircraft's velocity, which is all carpet bombing is: the line on the ground is
  the line you were flying. Splash falls off radially, `Landmine.cs`'s shape.

The **turret** is the "passenger" gun flown solo: `C` drops you into a free-aim belly
turret (mouse, ±160° yaw, down to −80°) while SAS holds the aeroplane; `C` again and
you are the pilot, camera and SAS restored exactly. The flight keys keep working in
the turret — SAS only holds attitude — so you can creep a hover sideways while hosing
the range. The seam is deliberately the one a LAN gunner would plug into later.

The **targets** live under an authored `Targets` group: three orange drones circling
at stacked altitudes, three trucks lapping closed Catmull-Rom loops whose waypoints
are draggable `wp*` handles in the hierarchy — their "dodging" is an authored weave
plus a ±30 % speed wander — and a cluster of barrels by the far pylons. All kinematic,
deliberately: a second `SimulationRunner` would fight over the global
`Time.fixedDeltaTime`, and the whole airfield is frictionless, so a physical truck
would slide like soap. Damage is a scene-local `WeaponTarget` component — health,
category, a death event — rather than the arena stack, which is car- and
match-coupled. Everything respawns, because a range with all its targets dead is a
range you have to restart.

The seeker's cone and selection rules are pure statics pinned by **`[LOCK]`** — the
`[NAVB]` treatment: the boundary is walked at 14° and 16°, never at exactly 15°
(on the line both answers are right and the check would gate on float noise), a
dead-heat tie is asserted stable, and the guidance closed forms (turn radius v/ω,
lateral reach inside the commit window) are checked so nobody can quietly retune the
missile into un-dodgeability.

### It does not touch the controller ABI

`PlaneVehicle` uses actuator slots `[0]`–`[3]` for throttle, aileron, elevator and
rudder, and the jet adds `[4]` for the nozzle-tilt target — slots the internal layout
note declares free. **This is an internal agreement between two components in this
repository and is not part of the versioned C ABI.** Slots `[6]` and `[7]` are left
deliberately empty because `controller_api.h` publishes them as `CTRL_STEER_ACTUATOR`
and `CTRL_BRAKE_ACTUATOR`, and putting an aileron in a slot whose published name says
"steer" is how a convention quietly becomes a lie. Weapons never enter the actuator
vector at all — firing is not a plant input. `controller_api.h`, `tt_controller.h`,
`ControllerInterop.cs` and `Docs/interface-spec.md` are all unchanged; the aeroplane
runs with `loadControllerDll = false`, so no controller can observe those slots today.

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
- **Tyre temperature and pressure** — set a cold pressure per wheel in the
  garage (▸ Tire realism) and the tyre becomes a thing that changes during a
  run. Temperature integrates from the friction power the tyre model is already
  producing, cooled by airflow; grip follows a cold/optimal/overheated window,
  so the first lap is the worst one and a sustained drift eventually goes
  greasy. Running pressure follows the temperature by the gas law — 180 kPa cold
  reads about 207 hot — and pressure in turn moves the grip optimum, the rolling
  resistance and the rolling radius, damping the centrifugal ballooning that
  inflation physically resists. A soft tyre drags more, the drag makes heat, and
  the heat takes some of the drag back.
  **0 kPa means the model is off**, which is every design saved before this
  existed; the shipped presets and new garage cars start at the 180 kPa optimum.
  Under **arcade handling** it is off unless you tick *Tyre temperature +
  pressure* beside the handling toggle, because the arcade grip floor was
  balanced against tyres that are always warm.
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

### Running your controller, and rebuilding it without leaving the game

**Main Menu ▸ Single Player ▸ Simulate Controller** is the front door: pick which
`.dll` in `Assets/Plugins/x86_64/` drives, pick a car and a track, and press
**Run controller ▶**. The car starts closed-loop under your firmware; press **M**
on track to take over by hand.

That screen — and the pause menu, mid-drive — carries a **Build & Reload** panel:

- **Build & Reload** shells out to `Controllers/build.ps1`, streams the compiler's
  output into the panel, and on success hot-swaps the DLL into the running car.
  No restart, no separate terminal. Pick which CMake target to build from the
  row above; the list is read out of `CMakeLists.txt`, so a controller you add
  yourself shows up without touching any C#.
- **Rebuild when I save a source file** watches `Controllers/` *and*
  `UserScripts/` for `.c`/`.h` saves and does the same automatically, 750 ms
  after you stop typing. Opt-in, off by default.
- **Reset the car after a reload** is also off by default, deliberately: the
  value of a hot reload is watching *the same situation* answer to new code.

This works because `NativeControllerLoader` loads a **shadow copy** of the DLL
from `%TEMP%` rather than the file itself — the compiler is free to overwrite the
original while the game holds a handle. Reloading runs `ctrl_shutdown` →
`ctrl_init` → `ctrl_configure`, so a stateful controller starts clean; if the new
DLL fails to load, the session goes open-loop and the car coasts rather than
stopping, and the reason appears in the panel.

**One thing the game cannot protect you from:** a bad pointer in your C takes
down the whole process. A managed `try`/`catch` does not catch a native access
violation — in the editor, that means Unity itself. Telemetry is saved before
every panel-triggered build for exactly this reason.

If the `Controllers/` folder is not next to the game (a standalone build copied
elsewhere), the panel says so and offers a path field instead of a button that
would do nothing.

### `UserScripts/` — where the player's own firmware lives

`Controllers/` is the game's C: four shipped controllers, the ABI header, the
CMake build. `UserScripts/` is everyone else's. It is a sibling folder, and the
whole convention is one rule:

> **One folder under `UserScripts/` = one controller = one DLL, named after the
> folder.**

```
UserScripts/
  guide.html            illustrated guide — the game can open it for you
  lib/tt_controller.h   header-only helper library, shared by every script
  MyController/
    my_controller.c     a working skeleton, commented as a tutorial
```

`MyController/` builds to `MyController.dll` and appears in the Simulate
Controller picker under that name. **Making a second controller is copying the
folder** — there is no list to join and no build file to edit. The bottom of
`Controllers/CMakeLists.txt` globs the folders with `CONFIGURE_DEPENDS`, so a new
folder triggers its own re-configure at the next build; every `.c` in a folder is
compiled and linked together, and `lib/` plus `Controllers/hal` and `common` are
on the include path (so `#include "pid.h"` works too). Names are restricted to
`[A-Za-z0-9_]`, because the same string has to be a CMake target and a Windows
file name at once.

`lib/tt_controller.h` is a genuine convenience layer, not a copy of the ABI:
bounds-checked sensor and camera reads that return a fallback rather than running
off the end of an array, a PID with a clamped integrator and
derivative-on-measurement, and `tt_drive_volts`/`tt_steer`/`tt_brake` over the
per-car motor manifest. Everything in it is `static inline`, so there is nothing
to link, and a controller that ignores it entirely is equally valid —
`car_sensors.c` is written that way on purpose.

It also carries the one thing that is not a wrapper: `TtCamera`, a fixed-size
ring of kept frames. `in->cam_pixels` is the game's own buffer, pinned only for
the duration of the `ctrl_step` call, so anything that compares one frame with
the next has to copy first. `tt_cam_update` does that copy, exposes each frame as
a 2D `px[y][x]` block with row 0 at the top, and returns true only when the
picture actually changed — which matters because the camera captures at ~10 Hz
while `ctrl_step` runs at 100, and the ABI carries no frame counter to say which
reads are new. No allocation: the DLL is rebuilt on every hot reload, and a
buffer that has to be freed is a leak per build.

Three things on the C# side make this feel like part of the game rather than a
folder convention:

- **`UserScriptCatalog`** re-implements the same folder rules the CMake glob uses
  (cross-referenced in comments at both ends), because the game has to answer
  "what can I offer to build?" on a fresh clone, before any CMake cache exists.
  It also dates each folder's sources against its built DLL — including `lib/`,
  since editing the shared header changes every controller.
- **The Simulate Controller screen** gained a **YOUR SCRIPTS** block listing every
  folder as *built and up to date* / *EDITED SINCE THE LAST BUILD* / *never
  built*, plus **Open the guide**. It is one `GUILayout.Label` over a
  Layout-built string, not one row per script — a row count that moves between a
  Layout pass and its Repaint is the census bug the whole UI is written to avoid.
  The **Controller** picker now drives the **Build** picker (`followDll`), so the
  screen cannot quietly compile one controller and race another.
- **A pre-flight check** runs before the compiler, and covers only what a
  compiler cannot tell you: a folder with no `.c` in it (refused), a `.cpp` that
  will be silently ignored, and — the one that matters — a source with no
  `ctrl_step` in it. That last case compiles and links and loads perfectly, and
  then the car coasts, because the game looks those four names up by hand. It is
  a substring test and says so in the code: it catches "you have not written it
  yet", not "you wrote it wrongly", so it warns rather than blocks.

Rebuild-on-save watches **both** roots now. They are siblings rather than nested,
so one recursive `FileSystemWatcher` cannot see both, and watching their common
parent would put Unity's `Library/` under a rebuild trigger.

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

- **Wheels** come in ten selectable styles per wheel — *slick*, *knobby*,
  *rally*, the TinyTorque *coupe* (gold rim), *baja* (balloon tyre, orange rim)
  and *steelie* (chrome police rim), plus the four Legendary wheels: *rusted*,
  *race gold*, *five-spoke* and *whitewall* (garage → wheel inspector → *Tyre
  style*). Styles 6-8 are not in that cycle because they are *finishes* over the
  slick — chrome, gold and neon, applied in the showroom. Each mesh is a
  multi-object assembly: tyre, rim, hub/barrel, studs or nut
  and a brake disc visible through the spokes. All hold an outer radius of
  exactly 33 mm so the runtime's `radius / WheelAuthorRadius` scaling is 1.0 at
  stock size.
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
  stock car ships with a pair on the rear deck. Four styles cycle in the
  inspector: the classic *Stub*, the coupe's amber-tipped *Whip*, the baja
  *Flag* whip, and the patrol's *Twin* trunk pair.
- **Lights** are a new cosmetic part category (palette → MISC → *Lights*): the
  police roof **light bar**, whose red and blue lenses strobe alternately at
  runtime, and the off-road **pod cluster**, which glows steadily. Position /
  heading / size / style, mirrorable, massed, on the viz layer — so like every
  part, a car's own camera sensor never sees them.
- **TinyTorque show-car bodies** (*Coupe*, *Baja*, *Patrol*) are full Blender
  models imported by `Blender/build_vehicles.py`, split per material so each
  object carries a token in its name: the neutral *paint* panels bind to the
  tintable body material (colour picker + livery painting work on exactly
  those), while chrome, gold, glass, gunmetal, decals and the emissive head/tail
  lights keep their authored look.

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
| Use item | **Left Shift** (keyboard) · **X / square** (gamepad) — rebindable |
| Items | boost, triple-boost, homing missile, dropped banana, shield, smoke cloud, oil slick |
| Roulette | weighted by live position — leaders draw boost/banana/shield/smoke, back-markers draw missile/triple-boost |
| Track limits | all four wheels off a surface below 0.90 friction for 2.5 s → a 2 s speed cap; two wheels off at an apex is racing, and jumps are exempt |
| Points | 15/12/10/8/6/4/2/1 on finish |

Bots use items too, on a 2 Hz policy with a randomised reaction delay — boost only
when pointed straight, missile only with a target in range, banana only with
someone close behind, and never within 1.5 s of GO.

### Getting hit

The two weapons deliberately do different things.

A **banana** spins you out: grip drops to 0.35, drive is cut so you cannot power
through it, and a yaw torque throws the tail one way or the other at random for
1.4 s. That torque is 1.2 N·m, which looks large against the car's ~0.03 kg·m²
of yaw inertia and is not — inertia decides how fast a *free* body would spin,
but the torque is actually fighting the tyres, and each one is still worth about
0.5 N·m of resisting moment even at reduced grip. The first build used 0.10 N·m
and the hit was imperceptible.

A **missile** destroys you: an explosion, an upward impulse and a tumble, 1.5 s
limp with no drive, and then the car is lifted back onto the racing line facing
forward, roughly where it was hit, with 1 s of immunity so a second missile
already in the air cannot re-kill you on arrival. Recovery uses the arc-length
spine — `Project` to find where you were, `Sample` to get a pose — and then the
factory's floor/ribbon-only raycast to land on the actual track, which matters on
Neon Vortex II's bridge and Workshop's plank where the surface below is metres
down. It is deliberately **not** a respawn to the start line: on a 100–140 m
circuit that costs most of a lap, and one missile would end a race.

A **shield** shows as a translucent bubble with three orbs circling it, and pops
with a flash when it eats a hit. It is built on the Ignore Raycast layer, like
every vehicle part visual, so a shielded car's own camera sensor is not blinded
by its own effect.

Every hit puts a banner and a brief colour wash on screen — SPUN OUT, WRECKED,
SHIELD BLOCKED — and a standing **MISSILE INCOMING** warning appears the moment
something locks onto you. That warning is not decoration: the missile is now
genuinely dodgeable, and a dodgeable missile you cannot see coming is just an
unfair one. In split-screen the whole overlay is drawn inside each player's own
viewport.

The missile homes at 2.2 rad/s — a 5 m turning radius against its 11 m/s — and
then **commits**: inside 1.5 m its steering collapses, so a late swerve makes it
miss and fly on. The first build used 3.2 rad/s, which is a 3.4 m radius, about
as tight as the car itself; it simply followed you in, and calling it dodgeable
was a claim the geometry did not support.

### Area denial: smoke and oil

Two items you leave behind you rather than fire. A **smoke cloud** grows to 0.75 m
over half a second, drifts slowly, and fogs the screen of anyone who drives
through it for 2.6 s — a green wash that *holds* at full strength and only fades
at the end, because a flash is a punch you already took while this is a state you
are in and is meant to cost you the corner. A bot caught in one stops following
the racing line and drives straight. An **oil slick** is flatter, wider (0.85 m),
lasts twelve seconds and cuts grip to 45 % while you are on it, recovering 0.4 s
after you leave. Shields deliberately block neither: a shield absorbs one *hit*,
and losing your sight or your grip is not a hit — which is the role these two have
that Shield does not already cover.

Neither carries a collider at all, which is the central design decision rather
than an omission. Containment is a distance poll in the director. A car is one
transform with a root box collider *and* four wheel colliders, so a trigger fires
several times per car per pass; re-arming an effect while you sit inside would
need `OnTriggerStay`, which stops firing the moment a parked car's rigidbody
sleeps; and LAN host followers are kinematic, which is exactly where trigger
callbacks get murky. A poll has none of those failures, costs eight racers times
sixteen hazards of `sqrMagnitude` per frame, and means a cloud can never
accidentally become a wall — which for an item whose whole point is that you
drive through it is worth more than the poll costs.

The hazard and its visual share one GameObject, so a drifting cloud carries its
effect with it instead of leaving an invisible trap behind a harmless decoration,
and the gameplay radius is copied off the visual at spawn — what catches you is by
construction what you can see.

### Drifting

Turn, pull the **handbrake**, and the car *commits*: it hops, sets itself into a
slide in the direction you were steering, and holds that slide until you let go.
The drift is **latched, not detected** — the first version watched for handbrake
plus speed plus slip angle and paid out when it happened to see all three, which
made the mechanic something the physics might grant you rather than something you
did. Now the handbrake is the commitment and the direction is read from the wheel
at that instant.

While it is held:

- **The angle is yours.** Steering further into the slide opens it out toward 34°
  of body slip; counter-steering closes it down toward 11°. A yaw controller
  holds whatever you ask for, with a much larger torque clamp for the first
  0.28 s — that is the snap into the slide, and it is a torque rather than an
  angular impulse so it feels the same whatever inertia the garage handed the
  design.
- **The drift button stops being a brake.** The handbrake torque drops to a
  quarter for the frames a slide is held. This is the difference between an arc
  and a handbrake turn: a locked rear axle bleeds the speed out of the corner
  faster than anything can put it back, which is why the first version felt like
  a punishment for drifting.
- **The arc carries.** A forward acceleration pays back the lateral scrub, fading
  out at 10 m/s so it can never take you past straight-line pace. It pushes along
  the *nose*, not along the velocity, which is also what rotates the car's motion
  onto its heading — the slide tightens onto its own line instead of washing out.
- **The assists stand down.** Countersteer and the stability yaw damper are
  turned down to a fifth, because both are machines for removing body slip angle
  and a drift is a request for body slip angle. Without this a Standard-assist
  car simply refuses to drift. Traction control and ABS are untouched — they act
  on wheel slip, not on body attitude.

A **mini-turbo** charges through three tiers, and the charge rate follows your
*commitment* rather than the clock: leaning into the slide pays about four times
what nursing it on counter-steer does. One stick axis makes both decisions — how
tight the arc is and how fast the reward builds — so holding a drift is something
you are doing rather than something you are waiting through. Sparks tint blue,
orange, then purple, and a meter above the item panel marks where the next tier
is.

The tyres also pour **smoke in the car's own colour** while the slide is held —
a pale tint of the body paint, so every drifting car signs its slide. The puffs
live in world space and finish fading after you release, leaving a short double
trail where the rear wheels were rather than a decal glued to the bumper.

Every boost — item, pad or mini-turbo — now also lights a **rear thruster
flame**: an orange plume with a near-white core that flickers in length while
the push lasts. Like every cosmetic it lives on the viz layer, so a car's own
camera sensor never sees its own exhaust. Over LAN (protocol v9) the whole
drift show travels: smoke, tier-coloured sparks, the flame and the mini-turbo
that lit it are visible on every machine, whichever machine earned them.

Releasing the handbrake straightens the car out — the same controller, aimed at
zero slip — and fires a per-tier impulse along the nose, so the exit lands as an
event pointing down the road rather than a gradual recovery pointing sideways. A
banner names what the slide was worth. A spin or a wreck mid-drift drops the
charge unpaid and skips the straighten: you are supposed to be out of control.

### Slipstream and look-back

Sit behind another car on a straight and you pick up a **slipstream**, recomputed
at the existing 5 Hz position tick. Both effects feed the same `arcadeBoostAccel`
channel the item boost uses — the draft is *maxed* in rather than added, so
drafting closes a gap instead of stacking on top of a mushroom and launching you
past the field, and both inherit the boost's top-speed fade for free.

**Look back** (**C**, or the right stick click) mirrors the chase camera's offset
while held. Held rather than toggled, so you cannot leave the camera facing
backwards and wonder why you keep hitting walls.

### Getting unstuck

A **boost pad** pushes along the car's nose for as long as a wheel is on it,
which is right while you are moving and a trap when you are not: nose into a wall
on a pad and it holds you there, out-torquing reverse and pinning the car too
straight for steering to walk it off. The pad now watches the outcome rather than
the geometry — on a pad and below 1 m/s for 0.7 s and it stands down, re-arming
once the car is genuinely rolling again at 1.6 m/s. It needs no idea of what is in
the way, and it works the same for a bot. Item boosts, drift carry and slipstream
are untouched: those last seconds and end on their own.

**Respawn** (**R**) puts the car back on the nearest point of the racing line
facing down-track, not at the start line. It uses the same arc-length spine and
floor-only raycast the missile recovery does, so it lands on the bridge rather
than under it. Your lap still dies — you have to cross the line again to arm a new
one — because keeping both the free position correction and the lap in progress
would make **R** a shortcut past any corner you were about to lose time in. On a
map with no racing line at all it falls back to the spawn point. Resets that
restart a *run* rather than rescue a car (the mode toggle, the mission harness)
still go to the spawn point as they always did.

### Arcade over LAN

Arcade runs in LAN too, host-authoritative like everything else in a LAN session:
tick **Arcade mode** on the Host LAN page and every joiner gets it. The host's
rules are the session's — a client is told them in the welcome and never consults
its own settings, so a lobby is never half arcade. Item boxes are live in free
roam as well as in races, so there is something to do between them.

The host owns every decision. It runs the whole director, and clients build the
same director with `IsAuthority` false: they roll no roulette, grant no items,
choose no recovery spot and detonate nothing. What they get is two streams — and,
since each player simulates their own car, a third message that hands them the
physics of an effect the host decided (`aihw.arc_fx`): the impulse, the tumble,
the spin's direction, the recovery pose. Applying is theirs; deciding is not.

**State**, 15 Hz and unreliable: inventories, effects, live positions, points,
projectile poses, and a bitmask of which item boxes are up. All of it idempotent,
so a dropped packet costs 66 ms of staleness and nothing else. Boxes are
identified by nothing but their index, which is why the director sorts them by
position at load — `FindObjectsByType` guarantees no ordering at all, and without
the sort two machines could disagree about which box just went down.

**Events**, reliable and exactly once: pickups, launches, hits, explosions. A
missed bang is missing and a duplicated one is two, so these cannot ride the
lossy stream. A client re-raises each one into its own director's event stream,
which means the audio and HUD layers are the same subscribers doing the same
work on both machines — they cannot tell where the decision was made.

Projectiles are streamed rather than re-simulated. A client could integrate the
same homing maths, but it would be running it against ghost positions ~120 ms
behind the host's, so its missile would chase a car that is not where the host
says it is — and the one thing a missile must agree on across machines is who it
hit. At four players that is a dozen small objects.

Client ghosts carry `ArcadeRacer` components exactly as host cars do, filled from
the stream. That is what lets the item panel, the shield bubble, the hit banners
and the incoming warning be one implementation instead of two. The one thing a
client genuinely cannot derive is whether a missile is locked onto it — it owns
no missiles, only their poses — so that arrives as a flag.

This is **protocol v14**. Every machine in a session must run the same build; the
exact version check at connection approval rejects a mismatch cleanly rather than
letting it half-work.

v14 is the Torque Falls city pack. Also not a message-layout change — but a map
crosses the wire as JSON, so a v13 client handed a town it has no meshes for
would draw eleven hundred fallback boxes, and the paving surface it has never
heard of is a floor id past the end of its own catalog.

v13 is the four Legendary cars. It is NOT a message-layout change — a body shape
and a wheel style are ints inside the design JSON, exactly like every shape
before them — but a v12 build ships none of the new FBX, so it would draw four
fallback boxes where the other screen has a wrecker, two race cars and a 1955
ride car.

v12 is the mini-game modes. It IS a wire change on three counts: the input flags
gained two bits (jump edge, boost held) so the aerials reach the host that
simulates them; `WelcomeMsg` and `SessionStateMsg` carry the match mode, target
score and time limit, because a joiner composes its scene from those and a client
that thinks it is racing while everyone else plays soccer cannot recover; and
`RosterEntry` carries a team. `MaxPlayers` also went 4 → 6 for 3v3 soccer — the
slot is a byte on the wire, so that costs nothing but two more roster rows.

v11 is the unlockable cosmetics. It is NOT a message-layout change — the five
cosmetic ids ride each car's design JSON, the same way `hornStyle` and
`liveryPng` do, so a v10 peer would parse every packet correctly. It is bumped
anyway because a v10 build has no `Resources/Cosmetics/` folder and no catalog:
it would silently drop whatever the other screen is showing, and a LAN race
where the cars do not match is worse than one that refuses to start.

v10 is horns + player levels, and unlike the last few bumps it is a REAL wire
change: `CarState` grew a trailing flags byte (bit 1 = horn sounding), so a v9
peer reading a v10 state stream would mis-frame every packet after the first
car. The horn also rides a spare bit in the input flags (client → host) and the
own-state flags (owner → host), each car's `hornStyle` travels in its design
JSON, and the hello/roster JSON carries a progression level for the `Lv N`
badge. Nothing about progression itself is networked — unlocks are local, and
the level is display-only.

v8 was the TinyTorque map packs: a map travels as its full track JSON, and v8
maps name 63 new scenery item ids (dt_/toy_/ench_/haunt_). The factory
deliberately skips unknown ids — that is what keeps old saves loading in new
builds — so a v7 peer receiving a v8 map would build it with every gate,
landmark and ghost silently missing rather than failing.

v9 is the four maps rebuilt as 1:10 ports of the Blender preview maps. That
adds three fields to the same JSON — `PlacedItem.scale`, `PlacedItem.pinned`
and `TrackDesign.ambience` — and the maps lean on all three: a v8 peer would
build 600 props at 1× (the layouts vary nearly every placement between 0.55×
and 1.9×), turn ~250 pinned decorative props into live Rigidbodies, and render
the whole map under flat daylight with no sky, fog or glow. Same disagreement,
no error message, which is exactly what the gate refuses.

v7 is the TinyTorque show cars. Not a byte of the wire format changed — but a
car's appearance travels as its full design JSON, and v7 designs can carry the
three new body shapes, three new wheel styles, antenna styles and the new light
parts. A v6 peer would deserialize one into a plain box on slick wheels and the
two machines would disagree about what the same car looks like, which is exactly
the mixed-cosmetics session the version gate exists to refuse.

v6 is drift visibility. Three spare `ArcEffect` bits carry drifting + tier down
to every client, and four spare own-state flag bits carry drifting, tier and the
mini-turbo up from the owner — so a client's slide pours smoke, tints sparks and
lights the boost flame on every machine, which under v5 only the host's own
drifts could do. No field moved and no byte was added; the bump exists because a
v5 host would silently never show a v6 client's drift. Sliding ghosts also
squeal now, but that needed no wire change at all — the skid intensity is
derived on each client from how sideways the streamed velocity is in the ghost's
own frame. Two things moved in v5, and both are worth knowing:

`ArcEffect` widened from a byte to a `ushort`, because all eight of its bits were
spoken for and the oil slick needed a ninth. That flag is not cosmetic — since v4
each client simulates its own car, so it is the *only* route by which oil reaches
a human player's physics; without it a slick would affect the host and the bots
and nobody else.

Blindness, by contrast, is **not** a flag. It goes on the wire as one byte of
remaining time, because the receiver has to rebuild an envelope rather than a
boolean: the green wash ramps in, holds, and fades over its last 0.9 s. Held as a
bit and re-armed to "now plus one sync period" like every other effect, that fade
term would have read 0.25/0.9 forever and pegged a client's tint at a third of the
alpha the host was drawing — an effect that looks like it works while not costing
the corner it exists to cost. One byte per racer, about 120 B/s at a full grid,
buys the identical envelope on both machines.

Area hazards ride the projectile stream as new kinds rather than the event stream,
because their *existence* is state and not a happening — a cloud lives for nine
seconds, so streaming it self-heals a lost packet, where a one-shot "dropped"
event would leave a client staring through a hazard the host is still catching it
with. An unknown projectile kind now renders **nothing** and logs once; it used to
fall through to a banana, which is the worst available failure — a real-looking
hazard that exists on neither machine's rules.

A race also now ends 45 s after the leader crosses, with everyone still out there
recorded DNF. One player who parks, gets stuck or disconnects badly used to hold
the whole lobby on the track indefinitely, and arcade makes that likelier rather
than less — a well-timed missile can cost most of a lap.

### Handling: Arcade or Sim

Arcade mode pins **every** car in the session, bots included, to **Full assists**
on all four channels and a 45 % tyre-grip baseline. The grip rides the existing
`arcadeGripMult` channel, already folded into µ on both friction paths, so it
costs no new physics code. Steering at Full is also most of why arcade feels less
twitchy: the lock limiter's reference speed drops from 4 to 2.5 m/s, roughly
halving the available lock at racing speed.

On top of that, the stability assist gets **triple authority** in arcade
(`arcadeStabilityMult`, neutral 1 everywhere else). The sim-sized ESC caps out at
0.75 N·m against roughly 2 N·m of tyre moment — a fair fight in sim, a lost one
in arcade, where boost pads and item boosts shove the body directly with forces
the tyres never see. This is why arcade cars used to spin out over pads and on
full-throttle launches *at any assist setting*. The boost stands down during a
drift (the slide is yaw you asked for) and during a spin-out or wreck (a hit must
out-rotate anything helping you), so none of those is retuned by it. Lap time in
arcade is meant to come from the racing line and the item luck, never from
catching slides.

**Arcade handling is now a first-class physics mode, not an arcade-race perk.**
It used to be applied only by the ArcadeDirector — which only exists in an
arcade lap race — so free roam, the derby, CTF and soccer all silently ran raw
sim physics with whatever assist preset was saved, on maps whose verges are
0.85-friction grass. That is exactly "the car slips way too much". Every rig a
machine simulates now carries a `HandlingFloor` component that re-asserts the
mode every frame, so:

* the **Handling: Arcade / Simulation** toggle sits on its own row in Single
  Player, Split-Screen and LAN Host — outside the arcade-items nest — and works
  in every mode including free roam;
* it is **live mid-session** from the Esc settings panel (solo/split; in LAN it
  is a session parameter set by the host);
* the deliberate-drift latch stands down under Sim handling — a scripted slide
  that cuts grip 30 % is an arcade mechanic and used to fire regardless.

Untick it to drive the raw brush-tyre model. That is what the mode shipped as,
and it is genuinely hard on a keyboard — which is the point of making it a
choice rather than a decision. The Options assist preset still governs sim
sessions; under arcade handling it is floored to full. C firmware is never
touched either way — firmware rigs never get a `HandlingFloor` — so a
controller under validation always faces the honest physics, which the Opus
regression re-proved bit-identical after this change.

The floor itself grew three teeth on user feedback ("slips too much, feels
light"):

* **grip 1.45 → 1.60** (`HandlingGripBonus`) — even the free-roam lawn lands at
  0.85 × 1.60 ≈ 1.36 effective µ;
* **launch control** — a new fifth assist channel (`launch`, its own Options
  slider, Standard preset 0.5): a voltage-side governor that holds the worst
  powered wheel's slip just past the tyre model's force peak below 3 m/s, so a
  floored standing start leaves at maximum force instead of lighting the rears.
  It composes with traction control rather than fighting it — TC is per-wheel,
  torque-side and stateless; this is global and integrating;
* **speed-squared downforce** (`HandlingDownforce`, 0.10 N/(m/s)²) — ≈36 % of
  the car's weight in extra tyre load at 8 m/s, applied at the centre of mass.
  Honest RC-scale aero is ~3.5 % of weight by design, which is why the arcade
  car read as floaty; load that grows with speed is the physically honest way
  to plant it without touching parking-speed handling.

Arcade handling also drops the drive command to 85 %, which takes the cars from
about 10 m/s to about 8.5. Top speed here is set purely by motor back-EMF —
steady state is `V = Kt·ω` — so scaling the command scales top speed almost
linearly, and it rides `arcadeDriveMult`, the choke point every motor command
already passes through. Launch torque scales with it too; that is the price of
costing no new physics code.

Item boost keeps its full 14 m/s² punch but now fades out approaching 11 m/s. It
is applied as a plain force on the body with no ceiling of its own, so 1.6 s of
it used to keep accelerating the car well past anything the drivetrain could
reach — which is what made boosting feel skittish rather than fast. Surface boost
**pads** are maxed in separately and are deliberately not capped, so a level's
authored pads behave exactly as they did.

## Rendering: bloom, baked liveries, posters

Three fidelity gaps closed in one pass, each a different root cause:

**Bloom.** The project runs Built-in RP in Gamma space with no post stack, where
an emissive material above 1.0 just clips to a flat bright patch — the Blender
source sells its neon at 5–19× albedo through AgX plus a compositor glare, and
the game had neither, which is why "the neon city lost its glow". The stand-in
is dependency-free, in keeping with the synth audio and procedural music:
`Shaders/AIHWSimBloom.shader` (bright-pass with a soft knee, three-level blur
chain, additive composite) driven by `Rendering/CameraBloom.cs` on every display
camera, each of which now renders HDR so authored >1 emission survives into the
pass. Never on the on-car `CameraSensor` — firmware eyes stay honest — nor the
icon/preview RTs or the builder. Split viewports bloom their own RT, IMGUI draws
after all cameras so the UI never smears, and **Options → Bloom** switches it
off live. With the glare in place the emissive multipliers were raised from
their LDR-era clamps to roughly authored × 0.5 (the authored ratio is commented
beside every value in `TrackCatalog`).

**The three TinyTorque liveries were procedural all along.** `M_Paint`,
`M_Buggy_Paint` and `M_Police_Paint` are banded masks in the source — candy
crimson with a graphite stripe and gold pinstripes; acid lime with graphite
bands; the police black-and-white with a navy flash — but the exporter labelled
them "authored 0.8 grey" and mapped them onto the flat tintable channel, so all
three cars rendered uniform grey and the police car's (always-present, correct)
navy 3D lettering was invisible against it. They are Rattletrap-class now:
`build_vehicles.py` bakes each to a 1024² texture (`body_<car>_paint.png`) with
a measured mean roughness, bound under its own token. The trade, stated
plainly: **the Coupe, Baja and Patrol no longer take garage repaint** — the
authored livery is the finish, exactly as the Rattletrap's rust already was.

**Billboards get in-game ads.** The kit authors its billboard faces blank —
that one was faithful, not broken — so `BillboardPoster` draws a poster at
runtime (procedural texture, chunky 3×5 pixel font, four seeded variants keyed
off world position so LAN peers agree which corner advertises the soda) onto a
double-sided quad seated on the measured face plate. Downtown's blank rooftop
sign face stays as authored; under bloom it now reads as the lit gold sign it
is.

Two smaller rim/cosmetic fixes travelled with this: `HideStockRim` no longer
switches off the Autopia's whitewall (it is part of the tyre, and the probe now
hard-fails any hidden tyre-family renderer), and the cosmetic topper/ornament
mounts measure the Legendary bodies' SHELL rather than their whole renderer
bounds — the Highwing's wing-on-stalks and the Rattletrap's boom were inflating
the box and floating the hats in mid-air, which the probe now proves against
the mesh with a ray check.

## Sound

The game makes noise now, and — like every mesh, texture and material in the
project — none of it is an asset. `Audio/ProceduralAudio.cs` synthesizes each
clip at runtime from oscillators, noise and envelopes, cached on first use. A
sound is a few numbers in a build script rather than a binary to find, license
and keep in sync, and the repo stays diffable.

Two rules do all the work for anything that loops: a tonal loop must contain a
whole number of cycles or the wrap clicks every period, and a noise loop has no
cycles to align so its head is cross-faded with an overlapping tail instead.
Noise uses a fixed-seed generator, so a given clip is bit-identical every run.

Every car carries motor whine pitched from its own motor speed, tyre squeal when
it slides, and an impact thud scaled by how hard it hit. Bots included, so you
hear the field around you; LAN ghosts too, pitched from the streamed speed
estimate since they have no drivetrain to read. Ghosts even squeal when they
slide — the lateral component of the streamed velocity in the ghost's own frame
is a good enough slip reading to feed the same gated skid voice a real car uses,
so a slide across the room sounds like one, from where it is. The arcade layer hangs its own
sounds off `ArcadeDirector.Event`, which had been raised from fourteen call sites
since the mode was built and had never had a subscriber.

The tyres are a stick-slip oscillator, not a hiss. Rubber breaking away grabs,
tears loose and grabs again at an audible rate, so the clip is a pair of detuned
squeal tones (their few-Hz beat is the slow "wow" a real slide has) over two
sharp resonators and a low rubber growl — filtered white noise cannot get there
however it is shaped, and the pitch drops as the slide deepens. The loop is
1.6 s with three internal modulations at different rates (vibrato, beat, swell)
that only line up once per loop, so no half-second of it resembles another.

They also stay quiet unless the car is genuinely *sliding*, not merely slipping.
`TyreSlip01` is not "how much slip is there" — every loaded tyre slips a little,
and a readout that rises with ordinary cornering squeals through every corner you
take cleanly. It is the tyre model's **own** combined slip, where 1.0 is exactly
the peak of the force curve, and it reads zero until you are past it. Wheels that
are airborne or barely moving are excluded, because neither can scrub. On top of
that the squeal opens only when the slip has *held* past a deadband for a tenth
of a second at real road speed — a noisy physics step, a kerb tap or a
standing-start scrub no longer chirps — it swells in and cuts off quickly, and
each car carries its own voice: a fixed per-car pitch offset plus a slow wander
on level and pitch, so a long slide never holds one note and two sliding cars
are two voices rather than the same loop twice.

**Every car also carries a horn** — hold **H** (or **L3** on a pad, rebindable
like any control) to sound it. Five voices, all synthesized: the standard
dual-tone "meep", a police two-tone wail (the TT Patrol's default), a truck air
horn (the TT Baja's), an original five-note musical fanfare, and a clown
squeeze-bulb. Which horn a car carries is part of its design (`hornStyle` —
cycled in the garage BODY tab or the Showroom), rides its design JSON over LAN,
and the horn state itself is synced (protocol v10), so a honk is heard by the
whole session from the car that made it. The horn sits in the SFX volume
bucket, not Engine + tyres — turning the motor drone down must not silence a
deliberate action.

**Music** is the game's first, and it is HYBRID: a persistent `MusicDirector`
crossfades a theme per scene — the menu/garage/builder share one, and each
drive scene picks its theme from the map's `ambience` key, so Downtown Dash
sounds like it looks (synthwave), the Playroom is a music-box romp, the vale a
moonlit waltz, the hollow a spooky ostinato, everything else a garage-rock
vamp, with a victory theme on the results screen. All seven themes are
**procedural chiptune loops** rendered at runtime in the ProceduralAudio
tradition (event-additive, note tails wrap the loop seamlessly, fixed seeds) —
but **drop an `.ogg`/`.wav`/`.mp3` into a `Music/` folder** (next to `Saves/`,
or the one shipped in `StreamingAssets/`) named `menu`, `generic`, `downtown`,
`toyroom`, `enchanted`, `haunted` or `results`, and your track replaces the
chiptune with zero configuration. The countdown ducks the music, pause halves
it (AudioSources ignore timeScale, so it plays on), and menus click, blip and
fanfare through their own small UI-sound set.

**Master volume**, **Sound effects**, **Engine + tyres** and **Music** are
separate sliders in Options and in the pause menu, where they take effect
immediately.

The garage and the main menu attract cars stay silent on purpose. Audio
attaches per rig in `TrackBootstrap`, not in `VehicleFactory`, so a humming
garage and a revving menu are a deliberate one-line opt-in rather than an
accident (the Showroom's rev is exactly that opt-in — a 2D engine loop, not
the car's own). Note also that Unity permits exactly one `AudioListener`, and
split-screen gives it to P1 — so in split-screen you hear the world from
player one's ear.

None of this can touch the simulation: the audio components only ever read.
`CarVehicle.TyreSlip01`, the one property added for tyre noise, is assigned at
the end of a physics step and never read back by any physics expression. The
Opus mission's headless result is byte-for-byte identical before and after.

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
leaves a wall you can drive through — so `TrackCatalog.MeshProp` cooks a
**non-convex MeshCollider per imported piece**: a static prop collides with
exactly the geometry you see. This replaced the hand-authored primitive hulls of
the first four passes, which an audit showed were the invisible-wall era in
disguise — 87 of 113 props were a single box or capsule, every house and hangar
was sealed solid porch-and-all, ramp hulls parked their feet 4–19 cm off the
ground, and Unity's "cylinder" primitive is a capsule that degenerates to a
*sphere* when it is wider than it is tall, which is how a 12.6 m volcano shipped
with a 5 m floating ball for collision. Cooking is per unique mesh and cached
(the 1 243-item town cooks in ~200 ms, logged at every build), the prop FBX
import CPU-readable so the cook also works in a player build (asserted by the
validator — the one failure mode the editor can never reproduce), and
`TrackPresetValidator.CheckColliderCoverage` now holds every static prop's
collider bounds to its renderer bounds within centimetres so the ghost-wall
class of defect stays dead. The two spirits keep their authored trigger hulls —
a concave MeshCollider cannot be a trigger, and you are meant to drive through a
ghost. Second, props stay on the default layer rather than the viz layer, so the
on-car camera sensor can actually see the scenery — and the ToF sensors now
range against true prop geometry too.

Props sit on the ground plane with their **origin at the base contact point**,
because `TrackFactory` drops each item onto the surface it was placed on. The two
exceptions are documented in the build script: the tape arch and the light hoop
are deliberately part-buried, because a ring standing on its rim holds its bore
70–110 mm off the deck and a 100 mm car noses straight into it.

## TinyTorque map packs (98 props, four themed circuits and a town)

The second prop generation came the other way around: finished Blender showcase
files in the TinyTorque_RC modelling project, re-exported for the game by
`Blender/build_map_props.py` (re-runnable, never saves the sources; name a pack
on the command line to rebuild just that one). Four themed packs are below; the
fifth, the daylight city, has its own section further down. The
script splits every showcase prop by material, renames the pieces to the token
names `AssignByName` binds materials to, re-origins each prop at its base
contact point, scales by exactly 0.1 (1 authored metre = 0.1 game metre — the
1/10 fiction, precisely), and prints per-prop JSON — bounds, token lists, tri
counts, slope profiles — that the validator rows are pasted from (collision is
the mesh itself now, so nothing else needs the numbers).

| Theme | Props (14–17 each) |
|---|---|
| **Downtown** | neon tower, city block, hangar, city gate, rock arch, volcano, jump ramp, kicker, boulder, rock, jersey barrier, traffic cone, street lamp, cycling traffic light |
| **Toy Room** | table, chair, bed, dresser, bookcase, toy box, toy gate, hoop, card bridge, plank ramp, block tower, brick, domino, crayon, ball, desk lamp, floor lamp |
| **Enchanted Kingdom** | castle, mountain peak, wizard tower, gatehouse, cottage, castle gate, vine arch, stone bridge, terrace ramp, fountain, hedge, topiary, blossom tree, boulder, crystal, fairy lamp |
| **Haunted Hollow** | mansion, chapel, barrow, crypt, cemetery gate, ruined arch, tomb ramp, slab ramp, gravestone, iron fence, dead tree, hearse, pumpkin, gas lamp, ghost, wisp |

The hero landmarks are imported at full scale on purpose — the castle is 8.7 m
tall, the volcano 12.6 m across, the peak fills a map corner — backdrop pieces
the same way the toy-room furniture towers over the car. Three props move:
the **traffic light** cycles green→amber→red (`SignalCycle`, driving the three
lens renderers through MaterialPropertyBlocks), and the **ghost** and **wisp**
hover-bob and sway (`GhostBob`). The spirits' hulls are *triggers*: the builder
can still select them, but the car drives straight through the apparition.
Animated props are excluded from static batching — a batched ghost cannot bob.
The five lamp props carry the light-post behavior, each with its point light
at the authored head height.

The four maps these packs dress are ports of the Blender project's own
**preview maps** — `TinyTorque_map.blend` and the three themed ones, built by
`tt_11_map.py`, `tt_16_toy_map.py`, `tt_17_ench_map.py` and
`tt_18_haunt_map.py` and rendered to `renders/map*/…_{plan,aerial,street}.png`.
They are laid out at the same 1:10 the props were exported at, so the districts
sit exactly where the renders put them:

| Circuit | Source | Grid | World | Lap | Districts |
|---|---|---|---|---|---|
| ★ Downtown Dash | `tt_11_map` | 38×47 @ 2 m | 76×94 m | 187 m | downtown's 90 m block grid, industrial strip west, stunt park east, badlands + volcano south |
| ★ Playroom Raceway | `tt_16_toy_map` | 48×44 @ 2 m | 96×88 m | 98 m | furniture skyline against the north wall, the bed, the dining table's forest of legs, the toybox yard, the rug circuit |
| ★ Enchanted Ascent | `tt_17_ench_map` | 56×56 @ 2 m | 112×112 m | 169 m | castle closing the axis, village ring, formal gardens, enchanted wood, tournament ground, two peaks for the horizon |
| ★ Graveyard Shift | `tt_18_haunt_map` | 54×52 @ 2 m | 108×104 m | 160 m | mansion at the head of the drive, four fenced grave blocks, chapel ruin, barrow field, pumpkin patch, dead wood, spirits |

**The four circuits now run in their true Blender orientation.** The original
ports used a layout convention that contradicted the FBX export — every prop
arrived flipped front-to-back inside an otherwise self-consistent mirror of the
plan, which never showed on rocks and towers but was the root of "some geometry
is wrong". Each map was re-ported with `meshAxes: true` (the convention the
town proved out), its spline roll arrays negated (the corners turn the other
way) and its hand-typed headings mirrored — so building fronts, gates and signs
now face the way the source renders show, and each circuit is the mirror image
of what shipped before. Lap records keep their names; the times on them were
set on the mirrored layouts.

That is 365–710 placed items per map against the ~30 the first pass shipped,
which is why ground tiles are **2 m** here: 112 m of enchanted vale is 56 tiles
that way and 112 at 1 m, past the 80-tile ceiling and four times the floor
geometry. Item placement still snaps at 1 m — the builder subdivides each tile
— and the map panel exposes tile size alongside the resize buttons.

`MapLayout` does the porting arithmetic once instead of at several hundred call
sites, so a line in `TrackPresets` reads straight across from the Python it came
from:

```
spawn(src["volcano"], c, (100, -305, 0), rot_z=24)   # tt_11_map.badlands
L.Prop("dt_volcano", 100, -305, 24);                 // TrackPresets.Badlands
```

It absorbs three conversions. The **scale** (1 authored metre = 0.1 game
metres — any other ratio puts the buildings at the wrong spacing for their own
size); the **axes**, since Blender X/Y is game X/Z and the two systems have
opposite handedness about up, so a Blender `rot_z` of +θ is a game yaw of −θ
(get this backwards and the whole map mirrors, near-invisibly in a top-down
screenshot); and the **centring**, because the source maps are not centred on
their origin but a `TrackDesign` always is.

Each map ships **exactly one spline**. Every other road in the source becomes
painted floor tiles, which is what they are over there too — flat preview
ribbons in a deletable `ROADS` collection — and a second spline would silently
steal the bot racing line, because `BotPath` follows the spline with the *most*
control points. `TrackPresetValidator` fails a preset that grows one.

Three things in the sources are deliberately not ported. **Sculpted terrain**:
the castle's plateau and the mansion's rise are displaced ground meshes and
there is no terrain system, so both landmarks stand on flat ground at the head
of their axis. **The tightest linear runs**: a 5-unit fence spacing is a picket
every 0.5 m and 328 items for four cemetery blocks, so long railings and garden
hedges run at 10–12 and read the same from anywhere a car ever is. And the
**per-seed prop variants** — three gravestone shapes, four crayon colours — are
one mesh each here, varied by scale and yaw instead.

### Atmosphere

Most of what makes those renders look like themselves is not the props: it is
the sky gradient, the haze density and the colour of the one key light. Each
theme module carries its own, and `MapAmbience` ports them:

| Map | Sky | Fog | Key light | Glows |
|---|---|---|---|---|
| Downtown Dash | dusk, warm wedge low in the south behind the volcano | 0.0030 | raking sun 1.15, `(1.00, 0.80, 0.60)` | the crater |
| Playroom Raceway | dim warm ceiling, not a sky | 0.0022 | dormer window 1.35, `(1.00, 0.86, 0.66)` | the standard lamp |
| Enchanted Ascent | deep twilight, aurora wedge north over the castle | 0.0042 | moon 0.95, `(0.62, 0.76, 1.00)` | keep, village green |
| Graveyard Shift | near-black | 0.0068 | moon 0.85, `(0.58, 0.74, 1.00)` | hall, crypt mouth, chapel |

Fog densities are the Blender haze densities **×10**, because 1 game metre is 10
authored metres. Graveyard Shift running about twice everyone else is the source
being deliberate: on that map the fog is the subject, not depth cueing. Sky and
ambient colours are lifted well above the authored linear values — those are
photographed through AgX with a compositor bloom, and Unity has neither.

The sky is a 400 m inverted sphere on `Resources/SkyGradient.shader` (unlit,
fog off, three-stop vertical gradient plus a horizon wedge), riding with the
camera so its gradient does not slide as the car crosses 112 m of map. It lives
under `Resources/` because assets there are never stripped from a player build,
and it degrades to the flat camera background if it is ever missing. The toy
map additionally builds its **room**: two 13 m wallpapered walls with skirting
and dado rail, solid, because a floor running to a horizon reads as tarmac and
the whole scale gag collapses with it.

`MapAmbience.Apply` runs inside `TrackFactory.Build`, so the builder preview and
the drive scene get identical atmosphere — the same "what you build is what you
drive" contract the factory already keeps for geometry. Maps with no ambience
key restore whatever the scene's own bootstrap set, so loading a plain circuit
after a themed one does not leave the fog behind.

### Per-item scale and pinning

`PlacedItem` carries a uniform `scale` and a `pinned` flag. Scale is not a
convenience — the source layouts vary nearly every placement between 0.55× and
1.9×, so a faithful port needs it — and it applies to the whole item root, so
the authored visual, the invisible collision hull and a lamp's light offset all
move together. Dynamic props scale their mass by the cube, or a double-size
brick would fly off like foam; a lamp cancels the scale on its own light so a
taller post lights the same pool of floor.

`pinned` means "scenery, whatever the catalog says": no Rigidbody, and eligible
for static batching. The ports place roughly 250 dominoes, bricks and pumpkins
as decorative fill, and that many live bodies buys nothing — so the fills are
pinned and a handful near each racing line stay live, to still scatter when hit.

In the Track Builder, selecting an item gives a scale slider (0.2–5×), ± steps
and a 1× reset, plus a **Pinned** toggle on anything dynamic; Shift+scroll
resizes an item while you are placing it. Rotate and scale re-pose the existing
object rather than rebuilding — on a 700-item map a full rebuild per slider tick
would make the control unusable.

Every map has three or four checkpoints and twelve authored item boxes.
Authoring boxes suppresses `ArcadeDirector`'s automatic placement entirely, so a
hand-placed set is authoritative. The themed floor surfaces from the first prop
generation (workbench, carpet, neon grid, boardwalk, wet sand, lava rock,
obsidian, grate) carry these maps too; friction values double as the arcade
track-limit classification, so carpet, mud and sand run-offs read as off-track
without any extra authoring.

Two placement rules are invisible until they bite. `TrackFactory` drops each item
from `y + 3` and takes the *highest* hit, so an item under a raised deck snaps
onto the deck. And a landmark's hull must stay well clear of the ribbon edge —
an overlapping landmark is an invisible wall, and the validator cannot see that.

In the Track Builder the props live under the **ARCADE** tab (the item box) and
the **SCENERY** tab — all 87 mesh props, grouped under one header per theme,
eight themes in palette order.

Validate the props and the maps with:

```bash
"E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -quit -projectPath "UnitySim" -executeMethod AIHWSim.EditorTools.TrackPresetValidator.Report -logFile tpv.log
```

`TrackPresetValidator` checks the things that otherwise fail *silently*: an item
id that no longer resolves is skipped without a word by design (that is what lets
old saves load in new builds), a floor index past the end of the catalog throws
deep inside the mesh build, a checkpoint sequence with a gap in it simply never
completes a lap, an item at scale 0 builds invisible, and a second spline hands
the bots the wrong road.

It also reports the geometry of every ribbon (`[TPV] GEOM`) and builds each map
for real (`[TPV] BUILD`, with item and renderer counts — the number to watch if a
map ever hitches on load), which covers the two ways a 3D circuit goes wrong. A
gradient the car cannot climb just looks like a car that stops, so anything over
40 % fails and over 25 % warns. And a track that crosses itself is only a bridge
if the decks clear each other: the check compares every pair of points that are
far apart *along* the curve but within 1.5 m in plan view, and fails if the gap
is under 0.35 m — enough for the 0.10 m car plus the ribbon's 0.04 m skirt. A
0.2 m step would be an invisible wall at speed. (The retired Neon Vortex II
figure-8 was the map that motivated the overpass detector; none of the current
presets cross over themselves, so a `[TPV] GEOM … overpass(...)` line on a new
map is worth a second look.)

## Cosmetics, crates and the championship

Forty-seven unlockable decorations from the TinyTorque pack, in five slots —
**topper** (roof), **rim** (all four wheels), **ornament** (bonnet), **bobble**
(antenna tip) and **wing** (rear deck) — themed across arcade, toybox,
enchanted and haunted, and tiered common → legendary.

They are **purely visual**. Nothing a cosmetic does touches mass, aero,
colliders or the wheel configs: `MassProperties` sees the same car, the bots
drive the same car, and the Opus mission scores the same numbers with a crown on
the roof as without one. They ride the design JSON, so an equipped item shows up
in a race, in split-screen and on a LAN peer's screen with no extra plumbing.

### The pipeline

`Blender/build_cosmetics.py` opens `TinyTorque_cosmetics.blend` read-only,
separates each of the 51 objects by material, renames the pieces to the tokens
`PartMeshLibrary.AssignByName` binds, applies the game frame and scale, and
writes one FBX per item into `Resources/Cosmetics/`. It also prints a JSON block
carrying the authored PBR of all **39 materials** — base colour, metallic,
roughness, emission colour and strength, alpha — which is pasted into
`CosmeticCatalog`. Nothing about a cosmetic's look is eyeballed; the crown in
the game is the crown that was modelled.

Two scale factors, both measured off the source car rather than assumed:
`s_item = 0.420 / <coupe body length>` = 0.092278, and
`s_rim = 0.033 / <coupe tyre radius>` = 0.069500. A rim is baked to the author
radius so it rides the same `radius / WheelAuthorRadius` factor the tyre does,
and fits any wheel size.

Mount frames come from the pack's own `MOUNTS` table, re-expressed as fractions
of the authoring car's body box and applied to whatever body box the design
actually has. On the coupe that reproduces the authored mount exactly; on a
Baja, a LowRacer or something assembled in the garage it puts the hat on the
roof instead of through it. The bobble is the exception — it reads the built
antenna's own bounds, because antenna height varies with style and size.

Two materials cannot transfer exactly. The ghost and fae spectres drive alpha
and emission off a Fresnel node, which the Standard shader has no equivalent
for, so they land on the midpoint of the authored face..edge range; the ranges
are in the comments next to them.

### Crates

Four boxes, with the pack's own weights, floors and pity counts:

| Crate | Pulls | Earned by | Floor | Pity |
| --- | --- | --- | --- | --- |
| Scrap Crate | 1 | finish any race | — | 40 |
| Chrome Case | 2 | finish on the podium (≥2 opponents) | Uncommon | 25 |
| Gold Vault | 3 | win a championship | Rare | 12 |
| Cursed Casket | 2 | win the Midnight Series | Rare | 8 |

Crates replaced the old random-item-on-win grant, and **everything is in
them**: the 20 original unlocks (cars, horns, wheel finishes, roof kits, aero
kits, paints) were given rarity tiers and joined the 47 cosmetics in one pool
and one save key space. Cheat codes still work on the original 20.

The manifest also ships a per-item `odds` table. It is deliberately not
transcribed: every entry in it is exactly `weight[rarity] / |pool[rarity]|`, so
rolling a rarity by weight and then picking uniformly inside it reproduces those
numbers — and keeps reproducing them now that the pool is bigger. The themed
Cursed Casket draws only haunted items, which is also why the legacy unlocks,
having no theme, stay out of it and leave its authored eleven intact.

A duplicate pays **scrap** instead. Scrap buys any item outright at its tier
price, from a shop whose six offers rotate daily (seeded from the calendar date,
so no server and no rerolling by restarting) — the manifest's own escape hatch
from box luck. Crates are also buyable, priced at 4× their expected duplicate
value, which makes buying one a worse deal than earning one on purpose.

### Championship

Three series of four rounds over the existing circuits — **Rookie Cup**,
**Torque Trophy** and the haunted **Midnight Series** — scored 10-8-6-5-4-3-2-1.
The roster is pinned when the series starts, bot names and difficulty included,
because a points table only means something if the same drivers contested every
round. The standings live in `progress.json`, so a series survives quitting; the
results screen swaps Rematch for **Next round**, and the last round shows the
final table. Winning outright pays the Gold Vault, and winning the Midnight
Series pays the Cursed Casket too — which is what gives the pack's "seasonal"
box an honest trigger in a game with no seasons.

### Where things are

Root menu → **Crates** opens the crate room (a turntable, per-pull reveals, the
box's real odds and its pity counter). Root menu → **Shop** spends scrap.
Showroom → **Cosmetics** equips them: a slot strip, a cycle a gamepad can drive
and an icon grid a mouse can, both writing the same value. Locked entries are
shown, not hidden — clicking one **fits it to the turntable car** so it can be
spun and inspected, without ever being written to the saved loadout.

## Mini-game modes (demolition · capture the flag · soccer)

Three sets of rules that are not a race, selected from the **Mode** picker on the
Single Player page. Each ships with the arena it was built for, and picking a
mode moves the track selection there.

| Mode | Arena | Ends when |
|---|---|---|
| **Demolition** | ★ Scrapyard Bowl | one car is still running |
| **Capture the Flag** | ★ Cargo Yard | a team reaches the capture target |
| **Soccer** | ★ Torque Dome | a team reaches the goal target |

**Demolition.** Ram people. A square nose-on hit damages the car you hit, scaled
by closing speed; a side-swipe or a wall costs *both* cars a little, which is
what stops the whole thing being a game of chicken. Repair crosses sit on an
outer ring and bomb crates on an inner one, so healing means leaving the fight
and arming a mine means going where it is. A mine drops behind you on the
use-item button and blasts everything inside about a metre. Being wrecked punts
and tumbles the car — the arcade layer's own wreck — and then parks it as a
spectator rather than deleting it, because its camera is still somebody's
viewport.

**Capture the flag.** Two teams, two plinths. Drive into the other side's flag to
carry it, drive it back to your own base to score — and your own flag has to be
home for it to count. A hard enough hit from an opponent knocks it loose where
you stand; a team-mate driving through a loose flag sends it home, an opponent
picks it up and carries on. A flag nobody rescues returns itself after 20 s so a
punt into a corner cannot deadlock the match.

**Soccer.** A ball, two goal mouths, boost pads on the wings and corner ramps.
This is the only mode that turns the **aerials** on: jump, double jump, a
directional flip inside the window after the first jump, free air roll while
airborne (steering and throttle become roll and pitch — there is nothing else
for them to do off the ground), and a boost tank that pads and kick-offs refill.
Default keys **E** jump / **Q** boost, pad **LB** / **RB**, both rebindable.

**Bots** play all three. The bot AI was a strict racing-line follower and an
arena has no line, so it gained one push-in seam — `SetChaseTarget(worldPos)` —
and an arena steering mode that keeps the same pure-pursuit core and swaps the
precomputed corridor for three whisker raycasts. What it chases is the mode's
decision, not the driver's: hunt the weakest car, run the flag home, get behind
the ball on the goal side.

**Split-screen** now goes to four: full screen alone, stacked halves for two,
quadrants for three or four. Three players get a quadrant each and leave the
fourth empty rather than stretching one of them, so everybody's field of view is
identical.

### How a mode is put together

`MatchDirector` (in `Track/`) owns what every set of rules needs and none of them
should re-implement: the start countdown that holds the grid, the
`PlayerFinished` event the crate payout hangs off, the one-way door into the
results overlay, and the overlay's frame. `RaceDirector` is now a subclass of it
and kept every lap rule it had. `ModeDirector` (in `Modes/`) adds everything that
assumes an ARENA — a roster of `MatchRacer` state bags, an authority flag, a
match clock, collision plumbing, spectate and respawn — and the three modes are
subclasses of that. The shape is `ArcadeDirector`'s on purpose, because that
shape is already proven here against LAN, bots and split-screen.

`ArenaNav` is what a racing line is to a circuit. Five systems in this codebase
quietly assume every session has an ordered centreline — bot steering, the
respawn key, item-box placement, missile targeting, wreck recovery — and an arena
has none. It is deliberately not a nav mesh: the floor slab plus the authored
spawn ring answer both questions those systems were really asking ("where can a
car be" and "where do I put this car"), and bots avoid walls by looking at them.

An arena is authored like any other map — `TrackPresets`, the same helpers, the
same `TrackFactory.Build` the editor previews — and is recognised by having **no
finish line and a ring of spawns**. `PlacedItem.order` carries the team, which is
the field checkpoints already use for their index. The track validator knows the
difference and checks arenas on their own terms (4+ spawns, an even number so a
team mode has two equal sides, no checkpoints).

**Physics is untouched.** The aerial moves live behind `CarVehicle.arcadeAerial`,
off by default, in the style of the seven `arcade*` channels that came before
them — so a race car, a LAN peer and the headless Opus rig behave exactly as they
did. The Opus mission regression is the proof, and it returns numbers identical
to the pre-change baseline.

## Free roam: Torque Falls (35 city props, one town)

A fifth **Mode** on the Single Player page, and the one whose maps are not in any
*race* track picker. `★ Torque Falls` is a 66 × 66 m town — a port of
`tt_25_city_map.py` from the modelling project — with no finish line, no
checkpoints and no racing line. There is nothing there to race, so it is not
offered as somewhere to race: `TrackPresets.TrackKind.FreeRoam` keeps it out of
the single-player, championship, split-screen and LAN lists in one place instead
of four. The Track Builder still lists it, and still opens it — it is not a map
you can race on, but it is very much a map you can edit.

Free roam has its own **Map** picker, built from both catalogues by kind
(`TrackPresets.RoamNames`, `SceneTrackCatalog.RoamNames`): the maps built for
roaming first — the town, and `▣ Sandbox`, which is a free-roam *scene* track and
so was previously unreachable from every picker in the game — then every race
map, circuits and arenas included, because a circuit with the rules taken off is
a perfectly good place to drive. The choice funnels through the same `SelectTrack`
as everything else, so all three track sources work.

The mode still hides the lap and score steppers, the countdown and the
opponents; **R** puts the car back at the nearest spawn — on the town, one of
twelve street corners, which is what `TrackRespawn` falls back to when a map has
no racing line to project onto.

### The kit

`Blender/build_map_props.py -- city` exports the fifth pack, 35 props, the same
way as the four before it: split by material, tokenised, re-origined at the base
contact point, scaled by exactly 0.1.

| Group | Props |
|---|---|
| **houses** | bungalow, two-storey clapboard, cottage, brick terrace unit, four-storey walk-up |
| **drive into** | **garage** (open front *and* back), **dealership** (showroom + service bay), **fire station**, **filling station**, **arena** (bowl, tunnel, floodlights) |
| **other buildings** | corner shop, streamline diner, warehouse, water tower, clock tower |
| **street furniture** | telephone pole, transformer pole, street lamp, traffic signal, hydrant, mailbox, bench, billboard, bus shelter, stop sign |
| **boundaries** | picket fence, chain-link fence, brick garden wall, hedge |
| **planting** | oak, maple, pine, street sapling, shrub, planter |

Two things about this pack are new, and both are pipeline rather than content:

**Materials are now measured, not read off the source by eye.** The exporter
prints a `MATJSON` block per pack with every material's authored albedo —
sRGB-encoded, the conversion the four earlier packs were given by hand — plus
smoothness as 1 − roughness and how many times its own albedo an emissive
surface emits. Seventeen of the city's forty-nine materials are procedural
(brick, clapboard, shingle, leaf, bark) with no single authored colour to read,
so the walk averages the colours their node network mixes between; the check
that it works is that `M_City_Wall5` comes back as its authored colour times
0.86, which is exactly what `mat_siding` mixes. The emission multipliers sit
between the two worlds: authored for Cycles they run 2.5–19×, sold there by a
compositor glare the game now has a stand-in for (the bloom pass), so they run
at roughly authored × 0.5 with each authored ratio in a comment beside it.

**Five props are hollow on purpose — and since the mesh-collider migration you
really can drive into all five.** The kit asserts a 4.60 × 3.90 clearance on
every aperture; collision is now the exported mesh itself, so the garage is a
genuine drive-through (open front *and* back, with its closed second bay full
of tyre stacks you can bump), the dealership's service wing, the fire station's
appliance bay and the filling station's pump lane all take a car, and the arena
tunnel is exactly as wide as it looks. `TrackPresetValidator` probes each
corridor with an exact box overlap at car height — all five now, the pump lane
included — plus a control probe straddling a wall face, so a re-export that
grows a doorsill fails the build instead of quietly bricking up the one prop
the map was designed around. Probing the real meshes also settled which bay is
which: the old hand-authored hulls had the garage's open corridor on the
mirrored side, over the roller door and the tyres.

### The town

`★ Torque Falls`: a five-by-four street grid with a clock-tower plaza and a
thirteen-unit terrace in the middle, housing on nineteen block faces, three
parks, an industrial corner round the water tower, a motor strip carrying the
garage / dealership / filling station, and the arena on its own approach road.
1 243 items over 35 meshes, 2.28 M triangles — three to four times the heaviest
themed circuit, which is what a whole town at 1:10 costs.

Two things scale differently from the four circuit ports. **Tiles are 1 m, not
2**: a road here is 20 authored units = 2 game metres, which at 2 m tiles is a
single tile wide and loses its kerbs entirely, and the new **Paving** surface is
what makes a grid read as streets rather than as a runway diagram. And **there is
no spline** — a town has no racing line, and `BotPath` follows the spline with
the most points, so inventing one would hand a bot a lap of a map that has no
laps. That is also why free roam offers no opponents: a bot dropped into the
town with no line and no arena policy would sit at its spawn.

### The axis convention, which was wrong

`MapLayout` maps Blender +Y to game +Z and negates `rot_z`; the FBX exporter maps
Blender +Y to game **−Z**. Those cannot both be right, and composed they leave
every prop flipped front-to-back inside an otherwise correct world. On the four
maps that shipped before this it never showed — their props are rocks, towers and
symmetric blocks. On a town where a hundred and twelve houses face their own
streets it is the difference between a street and a row of back gardens.

The layout takes a `meshAxes` flag that negates Z and leaves the heading alone,
which composes with the export into one consistent transform. The town shipped
with it first; the fidelity pass then re-ported the four circuits onto the same
convention (see the map-packs section), so all five ports now agree and the
flag's default only exists to make a future port read this paragraph. The
measurement behind it is on the imported asset: `city_house_a`'s door piece
sits at z = +0.214, so a prop's front is on **+Z**.

## Tiny Torque Assets (editor-only kit)

`UnitySim/Assets/TinyTorqueAssets/` is a browsable kit of every model in the
project — categorised mesh copies, generated `.mat` assets, prefabs, a Scene-view
scatter brush, two debug scenes and two starter maps. It exists because the game
has no prefabs and no material assets by design: everything is built
procedurally at `Awake` and shaded by name token, which is right for the game and
useless for looking at your own models.

**It ships nothing.** The pack sits outside every `Resources/` folder, neither
debug scene is in Build Settings, and no game script references it —
`AIHWSim.Pack.PackValidator.Report` asserts all three on every run, alongside
prefab completeness, mesh-collider coverage and a hash check that the pack's mesh
copies still match their `Resources/` originals.

Every material in it is a **clone of the material the game builds at runtime**,
taken from `PartVisualFactory`'s tables, `CosmeticCatalog.Tokens`, and — for
props, whose four procedural themes keep their tokens inline in each `ItemDef` —
whatever actually lands on the renderers when the real build path runs. No PBR
number is retyped, so the pack cannot drift from the game.

Two things are pack-only. The **24-tile soccer/arena kit** (`soc_*`) is exported
straight into the pack rather than into `Resources/`, keeps its authored 8 m grid
frame so the shell tiles still self-stack, and carries all three themes'
palettes; it is deliberately **not** registered in `TrackCatalog`, so the Track
Builder cannot place it. And the two maps —
`TinyTorque_FreeRoam` and `TinyTorque_BaseRace` — are `TrackDesign` JSON rather
than `TrackPresets` rows, so they load and Drive from the Track Builder but
appear in no in-game picker.

Rebuild with `Tools > TinyTorque Assets > Rebuild everything`, or headless via
`-executeMethod AIHWSim.Pack.PackBuildAll.RunHeadless`. Full detail in
[the pack's own README](UnitySim/Assets/TinyTorqueAssets/README.md).

## Track Studio (hand-authored scene tracks)

`Tools > Track Studio` is the **editor-side** track builder. It makes a real Unity
scene — terrain, ProBuilder geometry, Unity-spline roads, authored lighting — into a
track the game loads and races. It is a third track *source* alongside the in-game
Track Builder's `TrackDesign` tile maps and the classic procedural oval, not a
replacement for either; `TrackBootstrap` dispatches on `GameFlow.ActiveSceneTrack`,
then `GameFlow.ActiveTrack`, then the oval.

The trade is explicit. A tile map is data: it saves to JSON, crosses the LAN wire as
JSON, round-trips through a resume snapshot and opens in the in-game builder. A scene
track buys terrain and arbitrary geometry and gives up all four — it ships inside the
build and is identified across the wire by **name** (protocol **v15**), so a client
that lacks the scene is refused with a message rather than silently dropped onto the
oval.

What it adds: Unity-spline road authoring that bakes through the existing
`RibbonMeshBuilder` (so tile-map ribbons stay byte-identical); checkpoint / spawn /
finish markers with gizmos and snap-to-road; a **Physics Material Brush** that paints
`TrackCatalog.Floors` surface types onto Unity Terrain alphamaps *and* onto mesh
colliders; an **AI racing-line optimizer** (minimum curvature by projected SOR, with a
friction-limited velocity profile, apexes and brake zones) baked to a ScriptableObject
and drawn in the Scene view; a three-lap headless **calibration** run that fits the
car's grip and drive scalars; and a **sector configurator** whose targets integrate
from the same profile the lap prediction uses.

Terrain was previously invisible to the physics — `SurfaceMap` resolved only
`SurfaceTag`s and the tile floor slab, so a `TerrainCollider` hit returned baseline
friction whatever it was painted with. It now bakes each terrain's alphamap to one
floor id per texel once at bind, because `SurfaceMap.At` runs ~12 800 times a second
on a full grid at 400 Hz.

**Bots are not modified.** The baked line is analysis and visualization only, so lap
times, race balance and difficulty tiers are exactly what they were.

Validate with `-executeMethod AIHWSim.TrackTools.TrackStudioValidator.Report` and grep
`[TRK] RESULT`. Full detail in [Docs/track-studio.md](Docs/track-studio.md).

## Vehicle Studio

**Menu ▸ Single Player ▸ Vehicle Studio**, or Tools ▸ AIHWSim ▸ Create Body Editor Scene.
A car on a turntable that you can reshape, rebuild, repaint and then drive — morph
sliders and free-form vertex pulling, a parts palette with a proper transform gizmo,
per-feature colour and finish, and a test drive that comes back with every edit intact.
It is still its own scene rather than a rewrite of the garage: the garage's assembly flow
(wheels, sensors, motors, aero) is untouched and will be ported across later. The seam
between them is `VehicleLayoutData`, and it is now a field on `VehicleDesign`.

**Two deformation systems on one mesh.** A Unity blendshape frame is a *delta* against
the mesh's base vertices, so the two never contend: free-form pulls are written into
the base vertices, morph weights ride on top, and what you see is
`base + offsets + Σ(wᵢ·Δᵢ)`. One `BakeMesh` call is exactly that sum, and it feeds both
the `MeshCollider` and the drag measurement — so those two can never describe different
cars.

**Four morphs, generated rather than authored.** Nothing in this project ships a
blendshape (`PartModelPostprocessor` strips them on import), so *Nose width*, *Tail
chop*, *Roofline* and *Side pinch* are built in code from each body's own bounding box
— which means they work on all twelve offered shells *and* on the two primitive
compounds that are not FBX at all. Every delta is a pure function of vertex position, so
co-located vertices at a hard edge always move together, and the frames regenerate
bit-identically at load. That last property is what lets a saved layout store four
weights instead of a megabyte of displacement.

**Sculpting.** Left-drag on the body. The brush gathers vertices inside a radius with a
smoothstep falloff, welds them by position first (an FBX shell duplicates a vertex at
every hard edge and every UV seam — pulling one copy tears the panel), and freezes that
set for the stroke, so the stroke does not crawl as the surface runs away from it.
Radius, strength and push direction (surface normal / vertical / lateral) are on the
panel.

**The collider updates on release, never during a drag.** Assigning a `MeshCollider`'s
`sharedMesh` makes PhysX cook, which is milliseconds — doing it sixty times a second
through a drag is the same slideshow the track tools found when re-cooking a road per
brush stroke. One rule covers slider drags, sculpt strokes, loads and resets alike:
something changed *and* the mouse is up.

**Triplanar body material** (`Assets/Resources/Shaders/AIHWSimTriplanar.shader`, the
project's first custom surface shader). A body being sculpted has no stable UV layout,
and the editor's mesh is several prefab parts merged into one, so their UVs no longer
relate to each other at all. World-space triplanar projection sidesteps the question:
texel density stays even across a panel that has just been dragged out 30 %. World space
is correct only *because the editor's body never moves* — a driving car would swim, and
that is the one line to change at port time.

**Measured drag, live.** The panel shows the deformed body's Cd, frontal area and Cd·A
beside the undeformed catalogue row, re-measured from the same bake the collider gets,
once per edit. It is read-only: `CarVehicle.EffectiveAero` is untouched, and a driving
car still gets its drag from the catalogue silhouette cache, which is keyed by body key
and knows nothing about deformation. Closing that gap is still open.

### Parts

**A feature is a renderer group, and that one definition does both jobs.** The shipped
shells are joined *per material* — `body_redline` arrives as `paint_1..8`, `dark_1..9`,
`em_tail_1..2`, `glass_1` — while the two manifest assets are named *per part*:
`body_patrol` carries `Police_PushBar`, `Police_HeadLights`, `Police_Mirrors`,
`_spotlens`, twenty-eight pieces in all. `FeatureChannels.NameOf` reduces either to a
channel, and a channel is both the thing you paint and the thing you can lift off a shell
and bolt to another car. (The pieces *were* modelled as parts in Blender —
`build_lights`, `build_aero`, `build_scoop` — and joined per material on the way out, so
the group is the finest cut the shipped FBX support. Richer semantic parts are an export
job through Asset Studio, not something the in-game editor can invent.)

**The palette enumerates four sources and types out none of them**: the 47-item cosmetics
pack, every harvestable group on all thirteen shells, the procedural aero / antenna /
light / battery builders, and the wheel catalogue. A body or a cosmetic that Asset Studio
commits appears here with no code change.

**The gizmo** is three arrows and three plane squares to move with, three discs to turn
with, and three stalked cubes plus a centre cube to resize with — screen-constant in
size, with a 5 mm / 15° / 0.05× snapping toggle and a part-axes / car-axes switch. An
arm stretches **one axis only**, so a wing can be widened without being thickened; the
centre cube takes all three together, and `Size 1×` puts a part back to square.

Three rules worth knowing. A drag whose ray runs within 2° of the axis being dragged is
**refused rather than clamped** (that ratio of two vanishing numbers is where every
misbehaving gizmo misbehaves). Snapping quantises the *result* in the frame of the drag,
so a snapped part is on the grid rather than a grid-step from wherever it started. And
**the frame a drag measures in is frozen at mouse-down** — the gizmo is re-posed from the
spec every frame and so chases the part it is moving, and measuring against its live
transform subtracts the motion just produced: the part lands, the gizmo follows, the next
frame reads the displacement as already spent and puts the part back. That is a one-frame
oscillation no arithmetic check can see, because every function involved is correct; the
bench catches it by holding the pointer still and stepping the loop twice.

Handles get their own raycast pass ahead of parts, so a handle in front of the thing it
moves wins the click — and the scale arms start clear of the centre cube, or they swallow
the click meant for it.

**Removing a feature empties its submesh** rather than hiding a renderer — so a spoiler
you delete leaves the collider bake and the drag figure too, which is what deleting it
means. It is reversible: the triangles are kept, not destroyed.

### Paint

Per channel: colour, metallic, gloss, glow, and a procedural finish (checker, stripes,
carbon weave, grid, dots, speckle — generated masks, multiplied by the colour, so one
texture serves every colour). Materials are **cloned from the authored one**, never built
fresh, so painting the police car's chrome red gives red chrome rather than red plastic —
the normal map, the metallic map and the render state all survive.

### The crash frame (FRAME tab)

**Generate** wraps the body's current baked shape in a point-mass lattice that hugs the
surface: every triangle is rasterised into samples (pitch/8 apart, so a large panel
contributes everywhere, not just where it happens to hold a vertex), samples bucket into
grid cells, and a cell whose normals spread more than ~50° **halves, twice if needed** —
flat panels get large cells, curves and corners get small ones, which is also why the
node cloud reads as the car's silhouette rather than a box of scaffolding. A node is the
*centroid of its cell's surface samples* (on the skin, not a grid corner floating off
it); beams follow sample adjacency along the surface, so force spreads the way the shell
is actually connected, and separate mesh islands are bridged by their closest node pair.
The grid is anchored so **x = 0 is a cell plane at every level**, which keeps a symmetric
body's frame symmetric with no mirror bookkeeping. The **Detail slider** maps to the base
cell pitch; its fine end is set by the runtime beam budget, not by taste, and generation
refuses outright past 6 000 beams rather than shipping a frame the 400 Hz step cannot
carry.

Every node's **mass**, every beam's **spring, damping ratio ζ and break strain** are
editable; nodes drag with the same gizmo parts use (wrapped in a `PropPlacement`, so the
benched gizmo needed zero changes), `N` adds a node under the cursor, `L` links two, and
Delete removes either. Defaults are derived, not guessed: node mass is an equal share of
a 0.5 kg shell, and the default spring pins each node's aggregate natural frequency at
40 Hz — comfortably under the 400 Hz driving step's ~127 Hz stability ceiling, with the
10× spring slider still inside the runtime's substep headroom. The overlay is **two
meshes and one sphere** (one `MeshTopology.Lines` mesh for every beam, one combined
octahedron mesh for every node) and picking is pure ray math — no colliders, so a
thousand handles never crowd the sculpt brush's raycast. Sculpting the body after
generating marks the frame **stale** and says so; regenerating is a deliberate two-press,
because it discards manual edits.

**Damage is tuned by feel, in the tab.** The **Damage slider** (0.1× → 10×, log, neutral
in the middle) is the one number the driving path scales its hit response by: above
neutral, hits inject more speed *and* the yield and break thresholds drop by the same
factor; below it a wall crash stays elastic and springs back out. **Crash test** arms the
cursor — every click on the body is a wall-grade whack, played through the *same solver
class the track builds*, denting the actual body mesh live — and **Repair** (or leaving
the tab, or any edit) puts the pristine mesh back; crash-test dents never touch the
document. On the road, the **respawn key repairs the car**: dents and detached chunks
reset with the run, like tyre temperature does.

**On the road, contacts dent the car.** A car whose design carries a frame gets a
`CarSoftLattice`: the root box's collision events (wheels are raycast suspensions and
raise none) become quantized hits, energy-capped so a crash crumples and never explodes;
the solver — semi-implicit Euler in the car's local frame, substeps chosen from the
stiffest node so ω·h ≤ 0.5, springs auto-softened past the 8-substep budget — spreads
the load through the beams; strain past yield flows into permanent dents, and a beam
breaks one-way on either an instantaneous snap or spent ductility (permanent stretch past
its break strain). A feature channel that loses 60 % of its supporting beams **detaches
as a debris chunk** (its submesh, at its current deformed shape, wearing its current
paint) that collides with the world but is ignore-listed against every car's chassis box.
Asleep — which is every frame the car is not actively crumpling — the whole system is one
branch; dents are per-session and never written back into the design.

**A hit is a field, and momentum is its width.** Every node within a radius of the
contact takes velocity `v_peak·(1 − (d/R)²)²` — full push at the contact, smoothly
nothing at R — so the crush spreads through the panel instead of poking the three
vertices under the bumper and hoping the beams carry it (the anchor and dampers kill that
ring within a few centimetres, which is what "the forces don't propagate" looked like).
Node speed saturates for stability, so **extra impulse buys area, not speed**: R grows
with √J in units of the mean beam length, which is what makes a 100 km/h wall feel
different from a shove and keeps a crash looking the same at every Detail setting. One
collision can also be several impacts — contact points **cluster** (up to three, greedy
in index order), so a broadside dents nose and tail rather than the middle of the car
where nothing touched. Injections are plain velocity additions, so simultaneous sources
superpose exactly.

The inner loops are **Burst jobs on the worker threads**: beam forces are gathered per
node through a CSR adjacency built once, so nothing is ever scattered into a neighbour
and the parallel schedule has no race to lose, and every job is `FloatMode.Strict` so the
arithmetic a LAN peer replays is reproduced, not merely approximated. Measured on the
`[LATT]` fixture (656 nodes, 1 962 beams): **212 µs → 18 µs per awake step**, with every
dent and break identical to the single-threaded version.

None of it touches the physics gates: no collider is added or resized, mass/CoM/inertia
never change, no force ever reaches the car's rigidbody, and cars without a frame —
every pre-frame design, every physics test, the Opus mission — build none of this at
all. Over LAN each machine reports its own car's hits (`LatticeHitMsg`, protocol 17,
owner-authoritative like OwnState, token-bucketed); peers replay the same quantized
bytes into the same deterministic solver, so ghosts dent identically. The protocol bump
means a pre-frame build cannot join a post-frame session — the version handshake refuses
it up front rather than letting the dents drift. Gate:
`AIHWSim.EditorTools.LatticeBench.Report`, grep `[LATT] RESULT` — determinism run twice
bit-equal, energy-decay at worst-case stiffness, plasticity monotonicity, break-once,
binding, the detach boundary, and µs per awake step.

### Driving it, and saving it

**Test drive** attaches the layout to a real `VehicleDesign` and hands it to the ordinary
`VehicleFactory`; Pause ▸ Garage on the track returns to the studio with the car intact
(`GameFlow.ReturnScene`). The scene is in Build Settings for exactly that reason.

**On the driving side the morphs are baked into the vertices and there is no
`SkinnedMeshRenderer` at all.** A blendshape's contribution is `weight/100 × delta`
summed over frames, which is the sum `DeformedBodyFactory` computes directly — so the car
on the track is geometry-identical to the editor's bake, with no per-frame skinning. The
bench pins that: it builds the same layout both ways and compares vertex by vertex.

**Deformation is visual and aerodynamic, never collision.** Cars in this project collide
as a root `BoxCollider`; making collision follow a sculpted mesh would change how every
car drives rather than how one looks. Parts mount as cosmetic children exactly like the
antennas and light clusters beside them — no mass, no collider, no aero.

**Nothing that predates the studio moves.** `VehicleDesign.bodyLayout` deserialises to a
layout whose `IsEmpty` is true for every design ever written, and every apply site tests
exactly that before taking the new path — so "no regression" is a property of the data's
shape, not of a flag somebody has to remember to set.

**Save vehicle** writes a real design through `VehicleLibrary` to
`<project>/UnitySim/Vehicles/`, so a studio car appears in the garage's load list and can
be raced. That is where vehicles live: hand-editable, diffable, and present in a shipped
build — unlike the Unity `Assets/` folder, which is the editor-only Asset Studio path for
making permanent *content*.

**Layouts save as small readable JSON** under `<project>/BodyLayouts/`, beside
`Vehicles/` and `Tracks/`: the base body key, four morph weights, the wheelbase, a
*sparse* list of the vertices actually pulled, the parts, paint and hidden features, and
the crash frame's nodes and beams (layout v4 — rest lengths, dampers and vertex bindings
are derived at load, never stored, for the same staleness reason morph targets are not).
Morph weights are matched back **by name**, so adding or reordering a morph cannot apply
a roofline weight to a nose slider; vertex offsets are refused outright if the base
mesh's vertex count has changed, because an index into a re-exported mesh does not
address the point it used to. A part whose source key this build does not recognise
survives being loaded, edited around and saved again.

Gate: `-executeMethod AIHWSim.EditorTools.BodyDeformBench.Report`, grep `[BDEF] RESULT`.
It runs scene-free and covers the morph frames (including bit-identical regeneration),
the falloff and weld arithmetic, sparse apply and refusal, the JSON round trip, the bake,
and whether a deformation moves the measured drag in the right direction.

## Remote control (external apps)

An external Windows program can drive a car in this game and read its sensors back,
over named pipes. It is **off by default** — turn it on with *Options → Remote
Control* in the menu, or from the pause panel mid-race; the toggle starts and stops
the server live, no restart. While it is on the menu shows the pipe it is serving,
and the game keeps simulating unfocused so the controlling app can hold the window.

Two pipes, because the two kinds of traffic want opposite things. `TinyTorque.Control`
carries newline-delimited JSON — requests and their replies, every request answered
with an `ack` or an `err` carrying a machine-readable code. `TinyTorque.Telemetry`
carries hand-packed little-endian binary frames, which is what a 100 Hz channel
stream and 128×128 camera captures need. This is the same split the LAN code uses
and for the same reason.

Control is a **per-vehicle takeover**: the app acquires a vehicle explicitly and only
then may steer it, at one of two levels — `drive` (normalized pedals and steering,
assists still apply) or `raw` (the actuator vector a firmware would write, volts per
motor). Local input for that car is ignored while held and restored on release; other
cars stay locally driven. A held car that stops hearing from its app for 0.5 s brakes
itself, and a client that dies mid-corner hands control back rather than leaving the
car pinned at its last command.

Beyond driving, the surface covers the whole session: list and load tracks, spawn and
despawn vehicles, subscribe to telemetry channels at a chosen rate, stream sensor
camera frames, read and set tunables, assists, solver settings and game settings, and
push a whole `VehicleDesign` — which either applies live or comes back as a structured
refusal explaining why (a LAN car and a bot cannot be rebuilt under you).

[`Docs/ipc-protocol.md`](Docs/ipc-protocol.md) is the spec: every message with its
field table, the binary layouts, the error codes, and a checklist for anyone writing
a client. Two traps in there are worth reading before you write one — the server pipes
*must* be `PipeOptions.Asynchronous` or the writer parks behind the reader and nothing
is ever answered, and a serializer that emits an object's declared type rather than
its runtime type will silently ship messages containing nothing but their type tag.
`Tools/ipc-test-client.ps1` is a working PowerShell client that handshakes, lists
vehicles, acquires one, pulses the steering and prints telemetry frames.

Gate: `-executeMethod AIHWSim.EditorTools.IpcProtocolValidator.Report`, grep
`[IPC] RESULT`. It round-trips every message through JsonUtility, packs and unpacks
every frame type, checks the protocol constants are still append-only, and drives the
real server against an in-process pipe client for connect, busy, disconnect and
reconnect — all without entering play mode.

## Layout

```
UnitySim/       Unity 6 project (host: physics, sensors, telemetry, graphs)
Controllers/    Portable C firmware + CMake build (the code under test)
UserScripts/    Your own controllers — one folder each, built by the in-game
                button. Start at UserScripts/guide.html
Tools/          Interactive HTML tools — hardware→vehicle, control design, calibration
Blender/        Editable source (parts.blend) + the export scripts for parts,
                map props and cosmetics
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

   After the first time you can skip this: **Single Player ▸ Simulate
   Controller ▸ Build & Reload** runs the same script from inside the game and
   hot-swaps the result into the running car.

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
| Handbrake / drift | Space                | ⓐ (south button)           |
| Respawn (to nearest track point) | R     | ⓨ (north button)           |
| Use item (arcade) | Left Shift           | ⓧ (west button)            |
| Look back     | C (held)                 | Right stick click          |
| Horn (held)   | H                        | Left stick click           |
| Manual ⇄ Auto | M                        | Select                     |
| Pause         | Esc                      | Start                      |

Every menu is pad-navigable (d-pad/left stick + Ⓐ select / Ⓑ back), and the
two editors take the pad too: **left stick** moves the selected part or prop
in the camera plane, **LB/RB** rotates it, **LT/RT** raises/lowers a garage
part (props auto-drop, so in the Track Builder the triggers **scale** instead),
**right stick** pitches a garage part (sensor aim / antenna tilt / wing angle)
or orbits the builder camera, **Ⓐ** cycles the selection, **Ⓑ** deselects,
and in the garage **Ⓧ** toggles mirror mode and **Ⓨ** frames the selection.
Editor pad bindings are fixed; the driving controls above are rebindable in
Options / pause ▸ Settings ▸ Controls.

Send the car off a dirt jump, weave the cone slalom, and cross the finish line
to start the lap timer (bottom-right). Press **M** to hand control to
`car_controller.dll`, which then holds the stick-commanded speed while you still
steer; press **M** again to take back over.

### Rebinding

Every control above is rebindable from **Options** or from **Esc ▸ Settings**
mid-race, with **WASD** and **Arrows** layouts and a reset. `Core/KeyTable` is the
single canonical key list both input backends resolve through, so no driving
control names a key anywhere else — a missed call site would be a control that
silently ignores its rebind, which is the one failure mode this layer exists to
make impossible.

Three deliberate exceptions:

- The four driving axes keep an **alternate** binding, because W-or-↑ and A-or-←
  both worked before rebinding existed and quietly dropping that would read as the
  feature having broken the controls.
- **Escape always pauses**, whatever `Pause` is bound to. Pause is the only route
  to the screen that holds the bindings, so binding it somewhere unreachable would
  otherwise lock you out of the one place that could undo it.
- On a gamepad only the **digital** actions rebind. Throttle and steering stay on
  the triggers and the left stick — binding an analog axis to a button is offering
  to make the controller worse.

The developer overlays (**G** graph, **J** metrics, **K** mission, **P**
pause-graph, **[** / **]** window size) are pinned on purpose. They are tools
rather than controls, and documenting a fixed key is only honest if it is one.

### Reverse, throttle shaping and assists

Holding **S** from speed now brakes, stops and reverses on that single press. The
ESC models a real hobby unit, which holds neutral for a dwell before arming
reverse and resets that dwell on any non-zero command — so a reverse command
acting as a brake kept resetting its own lockout, and the player had to release
and press again. The fix is at the *input* layer: `CarInput` performs the neutral
blip the player was performing by hand. The ESC state machine itself is untouched
byte-for-byte, because the Opus mission's brake calibration depends on it.

Digital keyboard throttle is ramped like a transmitter trigger (≈0.45 s to full),
the same treatment steering already had. Stabbing S from full throttle still
passes through zero at the faster *release* rate, so braking stays crisp. Gamepad
triggers are never shaped — that would only throw away fidelity they already have
— and both ramps have a 0–100 % slider in Options, where 0 % restores the old
instant step.

Driving assists now default to **Standard** in *every* session type, not just
arcade — plain races used to run with the assist sliders at zero. Presets are
Off / Standard / Full / Custom, and moving any slider flips you to Custom, so the
preset is a shortcut rather than a cage. Every strengthened assist is the identity
function at and below the arcade floor and only gains authority above it, which
preserves the arcade tuning mechanically rather than by re-deriving numbers.
Firmware rigs are skipped explicitly at every application site: C code always
faces the raw physics.

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

- Quadcopter vehicle + attitude/rate PID cascade and motor mixer. The RC airplane
  already lays most of the groundwork: `AirData`, `PropellerModel` (four of them, plus
  the motor coupling and torque reaction), `IPilotInputSource`, `FlightTelemetry`,
  `FlightCameraRig`, `FlightTestEnvironment` and the `[AERO]` runner all transfer. A
  quadcopter needs no lifting surfaces, which is exactly why those live in their own
  files.
- Finish the flight test set: A3 (stall speed and root-before-tip progression — the one
  test that justifies a spanwise model over a single coefficient), A6 (panel convergence
  and roll rate), A7 (timestep sweep).
- Propeller slipstream by momentum theory. Without it the aeroplane has no elevator or
  rudder authority at zero airspeed, which is the whole reason a real model can lift its
  tail on the take-off roll; it also makes the neutral point move with throttle, which is
  the throttle-dependent pitch trim every tractor-prop aircraft has.
- Hardware-in-the-loop: stream sensor data to a real MCU over serial, read back
  actuator commands.
- `Controllers/targets/arduino/` PlatformIO project reusing `common/` sources.
