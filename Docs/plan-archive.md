# Plan archive

Completed implementation plans for Tiny Torque, newest first, exactly as written and
approved at the time. They are kept for the reasoning, not as current documentation --
where a plan and the code disagree, the code is right. The shipped behaviour they
describe is documented in [README.md](../README.md).

Active work lives in the session plan file, not here; a plan moves into this archive
once its milestones are done.

Covering the project bootstrap through the KSP flight HUD pass (42 plans).
Last updated 2026-08-01. (The RC-airplane F0–F9 and slipstream/phugoid G-series plans
completed between Track Studio and the HUD pass but their session files were
overwritten before archiving; their results live in README's RC-airplane section and
the [AERO]/[ABENCH] gates.)

---

# KSP-style flight HUD + controls: navball, SAS modes, throttle gauge, WASDQE (2026-08-01)

## Context

The RC airplane flies, validates ([AERO] 8×2, [ABENCH] 79 checks), and had a minimal
dev-tier HUD. The flight GUI and controls were reworked to feel like Kerbal Space
Program: a real 3D **navball**, a top-center **altimeter**, **clickable SAS mode
buttons** flanking the ball, a **throttle gauge** with Shift/Ctrl ratcheted throttle,
and **WASDQE** = pitch/yaw/roll.

### Decisions taken with the user

| | |
|---|---|
| SAS set | **Plane-appropriate**: Stability Assist, Prograde, Retrograde, Target, Wings-Level, Altitude Hold (no orbit-frame modes) |
| Key map | **Replace keyboard AND gamepad** with KSP-like layouts (no coexist toggle) |
| Navball | **Real 3D sphere + RenderTexture** via hidden camera (not 2D IMGUI) |

### Standing constraints

Zero diff on the four ABI files, ProjectSettings, PhysicsTuning, FlightTest + A0–A7,
car-side files; [AERO] and [PHYS] byte-identical. Flight keys stay out of
KeyBindings/DriveAction; InputReader's static smoothers untouched. Actuator slots 6/7
empty. No SAS inside PlaneVehicle (it declares "no stability augmentation"); SAS on
the input side. No new aerodynamic coefficients; SAS gains copied from the tuned
FlightTest hold loops with sources cited.

### Design facts

`PlaneInput.ReadManualCommands` is called from `SimulationRunner.ControlStep` inside
FixedUpdate at controlRateHz — hooking SAS there puts it on the control-rate path the
FlightTest loops were tuned at. Tuned loops copied verbatim: wings-level
`roll = bank*0.030 + rollRate*0.004` (both POSITIVE — +z-euler = banked LEFT);
vertical-speed cascade `wanted = clamp(−vsErr*1.2, −14, 10)` deg then
`pitch = (pitchDeg − wanted)*0.06 + rate*0.010` (+x-euler = nose DOWN; never close
vspeed→elevator directly — documented PIO, −3.9 g). MenuNav must NOT wrap the SAS
buttons (BeginFrame claims the gamepad; Poll reads the exact sticks the aircraft
flies with) — plain GUI.Button + discrete bindings. Navball layer 31 raw unnamed
(zero TagManager diff); `PartVisualFactory.VizLayer` unusable (flight camera renders
every layer).

## Milestones (all ✅)

- **H1 — Controls remap.** KSP keyboard (W/S pitch — W is forward, forward is nose
  down; A/D yaw, Q/E roll, Shift/Ctrl ratchet, X/Z snap) + gamepad (analog triggers
  drive the throttle RATE, left stick roll/pitch, right stick X yaw). [ABENCH] 79
  unchanged.
- **H2 — HUD skeleton, game-facing tier.** Altimeter, throttle gauge, six SAS
  buttons; GarageSkin + UIScale; engineering panel left dev-tier and unscaled.
- **H3 — Navball.** Procedural equirect texture (sky/ground, pitch ladder, heading
  band, 3×5 glyphs extended with digits) + unlit sphere on layer 31 through an
  on-demand ortho camera into a 256² RT + prograde/retrograde/target markers with
  far-side HIDING (never mirroring). **The seam is measured, not authored**:
  `MeasureSeam` reads the primitive sphere's own UVs → −90.466°, no magic constant
  to drift. **Added beyond plan: [NAVB], 18 headless checks** — nose dead centre
  across 24 attitudes, astern hidden, 10° above the nose above centre, nose-up level
  flight puts prograde BELOW centre, 45° of bank swings markers exactly 45°.
  Writing it caught `AngleAxis(θ)*att` (world-axis roll — rolls about the velocity
  vector itself, which is invariant, so the check passed vacuously at zero) vs
  `att*AngleAxis(θ)` (body-axis); both forms now asserted, the degenerate one
  deliberately. A visibility test at exactly 90° from the nose is ill-posed (z==0
  coin toss) — 80° and 91° instead.
- **H4/H5 — SAS, six modes** (landed together — same shape). Applied from
  `PlaneInput.ReadManualCommands` (control-rate by construction), attached only by
  RcPlaneBootstrap. T toggle, F momentary, 1–6 select, d-pad on pad. Per-axis stick
  breakout at 0.10 with continuous re-capture — deflect to steer, release to
  re-hold. Never touches throttle (KSP-accurate; the gauge must not lie) or rudder
  (no tuned yaw loop exists; inventing one is authoring a coefficient by feel).
  Exactly two NEW gains, both marked: headingToBank 0.8, altitudeToClimb 0.25.
  **Prograde hold is physically impossible for an aeroplane** — nose-on-velocity is
  zero alpha, zero alpha is no lift — so the mode settles into a shallow descent
  until the pitch clamp catches it; reported, not tuned away.
- **H6 — Regression + proof.** [AERO] 8×2 **byte-identical** to the F9 baseline
  (A0 44.25671387 · A1 11.20665359 · A2 2.0007081 · A3 8.89444828 · A4 9.44067669 ·
  A5 −0.31484383 · A6 0.73810291 · A7 0.25063953) — the SAS layer provably absent
  from the scripted tests. [PHYS] 10×2 byte-identical (P1 0.76573724 ·
  P6a 46.97132874 · P7 5.99544668 · P5 0.85303092 · P9 0.01400405). [NAVB] 18,
  [ABENCH] 79. Zero diff proven on every frozen file. Diff exactly 5 modified +
  4 new files, all flight-tree.

## Post-plan additions (same change-set)

The throttle gauge became an **arc wrapped round the navball** (KSP's layout): 44
tangent quads under a hand-composed GUI matrix — `GUIUtility.RotateAroundPivot`
composes OUTSIDE `GUI.matrix` and reads its pivot in screen pixels, which inside a
UIScale block turned the arc about the wrong point and scaled it twice (v1 slid off
the bottom of the screen on a radius of its own); `saved * TRS(centre,rot) *
TRS(−centre)` instead, and the same trap awaits `ScaleAroundPivot`. The dark square
behind the ball was deleted (the RT already clears transparent; it was framing
nothing) and replaced with a procedural soft-edged bezel annulus following the limb —
one texture, because a circle is the one shape IMGUI draws badly and a texture draws
for free.

---

# Track Studio: a Unity-Editor track builder, scene-native tracks, surface brush and racing-line baker (2026-07-30)

## Context

Tracks in this game are `TrackDesign` data: a tile grid of floor indices, `PlacedItem`s from
`TrackCatalog`, and `SplineSpec` ribbons. `TrackFactory.Build` turns that into a live scene at
`Awake`. The in-game Track Builder authors it, and `TrackDesign` JSON is *also* the LAN wire
format (`NetSession.cs:571`) and the resume-snapshot format (`PauseMenu.cs:332`).

That model cannot express what the user is now building. They installed
`com.unity.splines` 2.8.4, `com.unity.terrain-tools` 5.3.2 and ProBuilder 6.0.9, hand-painted
**13 Terrains** into `TTA_Sandbox`, and built a `SplineExtrude` road there — then hand-wrote a
branch into `TrackBootstrap.Awake:63-110` to make that scene drivable at all. It hardcodes a
player named "Jacob" and early-returns before bot paths, respawn, arena nav and the race
director. **Terrain is completely outside the runtime pipeline**: no script references `Terrain`,
and `SurfaceMap.At` resolves surfaces only via `SurfaceTag` or a tile lookup on the floor slab,
so a `TerrainCollider` hit silently returns baseline friction no matter what it is painted with.

This plan makes a hand-authored Unity scene a first-class track: authored with Unity Splines,
marked up with gizmos, painted with real surface types, and analysed by a racing-line solver
that is calibrated against the actual car.

**Decisions taken (asked):** scene-native tracks (not compile-to-`TrackDesign`); the brush paints
**both** Terrain and mesh colliders; the baked racing line is **bake + visualize only —
`BotDriver` is not modified**, so race feel and every existing regression stay put; **Circuit +
FreeRoam** kinds only (Arena refused with a clear message); and the `TTA_Sandbox` hack is
**replaced** by the real path.

**Hard constraints**
- The tile-map path is untouched. `TrackFactory.Build`, `TrackDesign`, `TrackPresets`,
  `TrackBuilderUI` and the in-game builder behave exactly as today.
- `BotDriver` is not modified. Bot lap times and race balance must not move.
- Opus mission stays bit-identical: legA −13.615608 mm, turn +0.187378°, legB +15.803814 mm,
  total +58.145523 mm, fault 0.
- Scene tracks are **not** editable in the in-game Track Builder. Accepted.

---

## Naming and layout

New menu root **`Tools/Track Studio/`** (not `Tools/AIHWSim/`, which is a flat list of one-shot
actions; not "Track Builder", which is the shipped in-game tile builder — two things with that
name is a support problem). Log tag **`[TRK]`**, joining `[PMV]` `[TPV]` `[COS]` `[PACK]`.

```
Assets/Scripts/Track/          runtime: descriptor, markers, RacingLineAsset,
                               TerrainFloorTable, SectorTimer, RaceLineFollower,
                               RacingLineAutorun, PathCurvature
Assets/Scripts/TrackEd/        runtime: UnitySplineSampler (next to SplineMath)
Assets/Editor/TrackStudio/     editor: window, brushes, tools, solver, runner, validator
Assets/TrackData/              baked .asset files (RacingLines/, Sectors/)
```

Namespace `AIHWSim.TrackTools` for editor code; existing `AIHWSim.Track` / `AIHWSim.TrackEd`
for runtime. No asmdefs (the project has none) — `Unity.Splines` is `autoReferenced`, so it is
already visible everywhere.

**Anything a player build must read cannot live under an `Editor/` folder.** `ScatterPreset` is
deliberately editor-only; `RacingLineAsset`, `TerrainFloorTable`, `TrackSectorSet` and
`TelemetryZoneSet` are the opposite and go in `Assets/Scripts/Track/`.

---

## M0 — Housekeeping and a live bug

- [ ] **Fix `EditorBuildSettings`.** It currently holds *six* entries: `TTA_Sandbox` is
      registered twice, the second as `TinyTorqueAssets/Scenes/TTA_Sandbox.unity` with an
      **all-zero GUID** (missing the `Assets/` prefix). A zero-GUID entry fails a player build,
      and `PackValidator.CheckIsolation` fails on the pack scene being registered at all — so
      `[PACK]` is red right now for reasons unrelated to this work.
      Root cause: `Assets/Editor/SceneBuilderMenu.cs:116-144 CreateMapDebugScene` calls
      `AddSceneToBuild` with an un-prefixed path, and `AddSceneToBuild` compares paths with
      `==` so it appends instead of matching. Fix both: normalise the path in
      `AddSceneToBuild`, and drop the `AddSceneToBuild` call from `CreateMapDebugScene`.
- [ ] Once `TTA_Sandbox` becomes a real scene track (M2) it must be **registered** in Build
      Settings — so `PackValidator.GameScenes` and `CheckIsolation` need updating in the same
      breath, and its doc comment ("the pack must ship nothing") rewritten to say that a scene
      promoted to a game track is no longer pack content.

---

## M1 — The runtime scene-track spine

- [ ] **`Assets/Scripts/Track/SceneTrackDescriptor.cs`** — one component per track scene:
      display name, `TrackKind` (Circuit | FreeRoam), ambience key, `TerrainFloorTable`
      reference, optional `RacingLineAsset`, a `sceneFallbackFloor` (default asphalt), and the
      baked bot corridor (`Vector3[] centerline`, `float[] halfWidths`, `bool closed`).
      Checkpoints / spawns / finish are **child marker components, not arrays** — they need
      Scene-view transforms, gizmos and per-object Undo, all of which come free from being
      GameObjects. The descriptor collects them at `Awake` via `GetComponentsInChildren`.
- [ ] **`Assets/Scripts/Track/SceneTrackBuilder.cs`** — turns the descriptor into a
      `BuiltTrack` (contract at `TrackFactory.cs:8-38`), field by field. It builds gate triggers
      with the same geometry `TrackFactory.MakeGateTrigger` uses (finish 1.6 m, checkpoint
      1.35 m, 1 m tall, centred 0.5 m up), attaches `LapTimer` / `Checkpoint`, and fills
      `spawns`.
      **`floorCollider` is the one hole**: a terrain scene has no floor slab. Its only
      unguarded consumer is `ArenaNav` (`ArenaNav.Drop` has no raycast fallback) — which is
      exactly why Arena is out of scope for v1. `TrackFactory.DropToSurface`'s two other callers
      (`ArcadeDirector.cs:340`, `TrackRespawn.cs:113`) already fall back to
      `Physics.RaycastAll`, so they are fine with it null. Leave it null, and have the
      validator refuse `TrackKind.Arena` with a message naming this.
- [ ] **`GameFlow.cs`** — add `public static string ActiveSceneTrack { get; private set; }`
      alongside `ActiveTrack`, with a setter pair that guarantees **exactly one of the two is
      ever non-null**. `LoadTrack()` gains a one-line dispatch, so its ~9 call sites
      (`MenuUI` ×4, `NetSession` ×3, `Championship`, `PauseMenu`) do not change.
- [ ] **Scene loading: the track scene loads `Single`, `TrackScene` loads additively on top.**
      `RenderSettings`, `LightmapSettings`, skybox, fog and baked reflection probes come from
      the *active* scene, and a hand-authored scene's entire look lives there — loading it
      Single makes it active with no `SetActiveScene` bookkeeping. `TrackScene` carries the
      composition (`TrackBootstrap` with inspector-authored `physicsRateHz = 400`,
      `controlRateHz = 100`), and there must be exactly one copy of that rather than a
      re-authored one per track scene. Delete `TrackScene`'s own `Directional Light` and
      `Main Camera` on the additive load.
      *Verify the two-loads-in-one-frame ordering before committing to it* — a `Start()`
      assertion in the descriptor is cheap insurance either way.
- [ ] **`TrackBootstrap.Awake:131`** becomes a three-way branch. **Delete the hand-written
      `TTA_Sandbox` block at `:63-110`** — `SessionConfig.ResolvePlayers()` already synthesizes
      a single merged-input slot from `GameFlow.ActiveDesign` when the roster is empty, so the
      hardcoded `PlayerSlot { name = "Jacob" }` was never needed. The one load-bearing piece
      *was* the skybox fix: **`MapAmbience.ApplyCamera` (`MapAmbience.cs:326-333`) forces
      `CameraClearFlags.SolidColor` unconditionally**, which is what eats an authored skybox.
      Give it a "scene owns the sky" opt-out and use that from the scene-track path
      (three call sites: `TrackBootstrap.cs:566`, `:1124`, `:1399`).
- [ ] **`BotPath.Build`** — new tier 0: if a `SceneTrackDescriptor` supplies a corridor, use it
      with its real per-node half-widths. The existing tier-3 checkpoint fallback still works for
      a scene track with no corridor, but its constant `GateHalfWidth = 1.0f` makes bots drive a
      coarse line, so prefer the corridor. Tiers 1-3 unchanged; the point list must stay
      byte-identical between the two overloads (`BotPath.cs:30-36`).
- [ ] **Menu.** `MenuUI.RefreshLists` (`:114-139`) adds scene tracks to `_tracks` with a
      distinguishing prefix (presets already use `"★ "`); `ResolveTrack` (`:169-174`) gains a
      scene-name branch ahead of `TrackPresets.Resolve` / `TrackLibrary.Load`. The scene list
      comes from a small registry asset so the menu does not scan Build Settings at runtime.

---

## M2 — Surfaces: terrain and meshes

- [ ] **`Assets/Scripts/Track/TerrainFloorTable.cs`** — a runtime `ScriptableObject` mapping
      `TerrainLayer` → `TrackCatalog.Floors` index, plus a default. Keyed on the **asset
      reference**, not the layer name — a name-keyed code table breaks silently on the one
      thing an artist actually changes. Referenced from the descriptor so both the editor brush
      and the runtime read one source of truth.
- [ ] **`SurfaceMap` — bake once at bind, then pure array arithmetic.** `At` is called per
      grounded wheel per physics step: at 400 Hz with an 8-car grid that is **12 800 calls/sec**,
      so a per-call `terrainData.GetAlphamaps(x, z, 1, 1)` is unacceptable twice over (a managed
      `float[1,1,layers]` allocation each time, plus an engine interop call per wheel).
      New `BindScene(SceneTrackDescriptor)` walks every `Terrain`, calls `GetAlphamaps` **once**
      per terrain, resolves the dominant layer per texel, maps it through the table, and stores a
      flat `byte[] floorIds`. Runtime lookup is two multiplies and an array index.
      - Resolution order becomes: `SurfaceTag` → terrain texel → tile slab → scene fallback →
        baseline. The `_design == null` early return at `:57-59` must be **dropped** (it defeats
        scene tracks entirely); guard the positional branch individually instead.
      - "Which terrain" is answered by a `Dictionary<Collider,int>` — `hit.collider` *is* that
        terrain's `TerrainCollider`, so no spatial index is needed and overlapping terrains cost
        nothing. Cache negatives too, exactly as `_tagCache` does.
      - `terrainLayers` is **per-terrain**, so layer index 0 may be Dirt on one and Grass on the
        next. Map the layer *asset*, never a raw index.
      - `GetAlphamaps` indexes `[z, x, layer]` — y-then-x. Getting this wrong yields a track
        that is correct along one axis and transposed along the other, which reads as "grip is
        randomly wrong in patches".
      - Memory: 512² × 13 terrains = 3.4 MB. Time the bake with a `Stopwatch` and log it
        (`TrackFactory.cs:268` does this for mesh cooking); if it ever exceeds ~250 ms the escape
        hatch is a side asset baked at import. Do not build that yet.
      - **No second-level position cache** — four wheels would thrash a single-entry memo, and
        the hash+compare costs more than the two multiplies it saves. Say so in the comment so
        nobody adds one later thinking it is free.
      - Free consequence worth documenting on the descriptor tooltip: `frictionMult` doubles as
        the arcade track-limit classifier (`ArcadeConfig.OffTrackFrictionThreshold = 0.90f`,
        `ArcadeDirector.cs:1639`). Painting grass makes it count as off-track automatically;
        painting a whole terrain dirt (1.00) means **track limits never trigger anywhere**.
- [ ] **Promote `TTA_Sandbox`** to a real scene track: descriptor, terrain floor table, spawn.
      This is the M1+M2 acceptance test — it should gain bots, respawn and surface physics that
      the hand-written branch skipped.

---

## M3 — Spline authoring

- [ ] **`Assets/Scripts/TrackEd/UnitySplineSampler.cs`** — samples a `SplineContainer` into the
      project's existing `SplineMath.Sample` shape (`pos`, `tan`, `roll`, `width`, `dist`,
      `surfaceType`, `pointIndex`), so everything downstream is unchanged.
- [ ] **Road mesh: reuse `RibbonMeshBuilder`, fed by the sampler.** Not `SplineExtrude` —
      its `ExtrusionShapes.Road` is a fixed 4-vertex normalized cross-section with no per-segment
      surface, no kerb submesh and no edge walls, and critically it emits no `SurfaceTag`, which
      is how the ribbon gets its friction today. `RibbonMeshBuilder` already splits a ribbon into
      contiguous same-surface runs, each with its own `MeshCollider` + `SurfaceTag`, plus kerb
      stripes as submesh 1 and optional walls. Refactor it additively to accept samples directly.
      *Regression fence:* the existing `SplineSpec` path must produce a **byte-identical** mesh
      before and after — hash a preset's ribbon vertex arrays either side.
- [ ] Width / banking / surface per segment ride on Unity Splines' idiomatic `SplineData<T>`
      embedded data (it has no such fields natively), authored by a custom `EditorTool` with
      handles. `TrackSplineAuthoring` (runtime component) holds the settings and the baked
      corridor it hands to the descriptor.
- [ ] `Tools/Track Studio/1. Bake ribbon`.

---

## M4 — Checkpoints, spawns, finish

- [ ] Marker components in `Assets/Scripts/Track/` with `OnDrawGizmos` — gate width, heading
      arrow, order label, team colour for spawns. A custom `Editor` per type plus an `EditorTool`
      for placement.
- [ ] **Snap to the spline by default**, per-marker toggle for free placement: place at an
      arc-length `t`, auto-orient to the tangent. `yawDeg` is the direction cars travel *through*
      a gate, so a hand-rotated gate is a common and silent authoring error.
- [ ] Ordering UI in the Track Studio window: a reorderable list that renumbers densely.
      **Dense `0..n-1` is required, not cosmetic** — `LapTimer.NotifyCheckpoint` (`:76-83`) only
      advances on `index == NextCheckpoint`, and `:125` refuses to count a lap until every
      checkpoint is hit, so one gap makes the track permanently un-lappable with no error.

---

## M5 — Physics Material Brush

- [ ] **`Assets/Editor/TrackStudio/SurfaceBrushWindow.cs`**, structurally copied from
      `ScatterBrushWindow` (`SceneView.duringSceneGui` subscribe/unsubscribe;
      `GUIUtility.GetControlID(FocusType.Passive)` unconditionally first;
      `HandleUtility.AddDefaultControl` only during `EventType.Layout`;
      `HandleUtility.GUIPointToWorldRay`; `Handles.DrawWireDisc` + `view.Repaint()` each frame;
      paint gated on `(MouseDown||MouseDrag) && button == 0 && !ev.alt`; `ev.Use()` only on an
      actual paint). One aim raycast serves both targets — unlike the scatter brush it is
      recolouring an existing collider, not finding ground for a new object.
- [ ] **Mesh target:** add or edit `SurfaceTag.floorType` on the hit collider.
      `Undo.RecordObject` before the mutation — a fourth Undo API the scatter brush never needed
      because it only ever creates and destroys.
- [ ] **Terrain target:** paint the mapped `TerrainLayer` into the alphamap.
      `TerrainData.SetAlphamaps` + `Undo.RegisterCompleteObjectUndo` on the `TerrainData` is
      **heavy**; coalesce a whole stroke into one undo entry and one write on mouse-up rather
      than per stamp, and write only the dirty sub-rect. *Measure `SetAlphamaps` GPU-flush
      behaviour in 6000.1 before choosing per-stamp vs. coalesced.*
- [ ] The floor palette is `TrackCatalog.Floors` — 18 entries, index is the persisted id.
      Show `frictionMult` in the picker so the off-track threshold (0.90) is visible while
      painting.

---

## M6 — Racing line solver and visualization

- [ ] **`Assets/Scripts/Track/PathCurvature.cs`** — extract the signed-curvature recipe from
      `BotDriver.cs:189-216` (`SignedAngle · Deg2Rad / arcLen`, ±2-node box smooth) into a shared
      helper. A copy, not a refactor of `BotDriver` — that file stays untouched — with a doc
      comment naming the source and a validator check that the two agree numerically.
- [ ] **Corridor:** centerline + per-node usable half-width, minus `CarHalfWidth = 0.20` and
      `EdgeMargin = 0.30` (`BotDriver.cs:73-82`).
- [ ] **Line: minimum curvature over lateral offsets, projected SOR.** Parameterise node *i* by
      a scalar `n_i ∈ [−wL_i, +wR_i]` along the node normal; minimise
      `J(n) = Σ ‖p_{i-1} − 2p_i + p_{i+1}‖²`, which is linear least squares in `n` with a
      pentadiagonal normal-equation matrix. Solve by projected SOR rather than a direct banded
      solve: a closed loop makes the system **cyclic** (SOR just indexes modulo N — no seam case),
      the box constraint makes it a QP (clamping after each coordinate update *is* projected
      Gauss-Seidel), and `H_ii = 12` exactly for unit normals so `ω ≈ 1.3` needs no per-track
      tuning. N ≈ 750 at 0.4 m spacing; 200-400 sweeps is a few milliseconds.
      *Not minimum-time:* that needs the velocity profile inside the loop and becomes a
      nonconvex NLP we would have to write and debug, for a few percent on a fixed-width corridor.
- [ ] **Expose a shortest-path blend** (`ε`, default 0.15). Pure min-curvature runs wide on every
      corner exit, including hairpins into short straights — wrong for 1/10-scale cars on tight
      circuits at ~9.5 m/s. One extra gradient term. `ε = 1` collapsing the line to the inside of
      everything is also the cheapest test that the term is wired correctly.
- [ ] **Velocity profile:** per-node `mu = Floors[surface].frictionMult · muScale` sampled at the
      *offset* position (the whole point of a racing line is that it moves onto the kerb);
      `v_curve = sqrt(g·(mu + tanφ) / (|κ|·(1 − mu·tanφ)))` including bank; then a forward pass
      (drive-limited) and backward pass (brake-limited) on the friction ellipse, iterated 2-4
      times to a fixed point on a closed loop.
      Note `maxBrakeTorque = 0.8 N·m` over 4 wheels at r ≈ 0.033 m on ~1.8 kg gives ~54 m/s² —
      an order of magnitude above `µ·g`, so **brakes are friction-limited everywhere on this
      car**; `brakeUse` is the fraction of the friction circle a real car uses, and gets fitted.
      *Unverified:* the sign of `SplineSpec.rollDeg` ("+ve = right edge down") relative to turn
      direction. Gate banking behind a flag and confirm empirically — a sign error makes banked
      corners read *slower*, which is a very confusing bug.
- [ ] **Apexes** = local maxima of `|κ|` above `KappaRef = 0.18`, ≥1.5 m apart, taking the node of
      minimum `v`. **Braking zones** = contiguous runs where the backward pass binds.
- [ ] **`RacingLineAsset`** (runtime `ScriptableObject`, `Assets/TrackData/RacingLines/`):
      points, curvature, speed, apex indices, brake zones, predicted lap, calibration block, and
      a `bakeHash` + `sceneGuid` for staleness.
- [ ] Scene-view visualization: speed-coloured ribbon, apex markers, brake-zone bars.

---

## M7 — Headless calibration run

- [ ] **The sim's job is calibration, not search.** A black-box optimizer (drive many candidate
      lines, keep the fastest) needs 50-200 laps per track and produces an output nobody can
      explain. Calibration needs **3 laps** and yields four numbers with physical meaning that
      transfer to every track built with the same car.
- [ ] **`Assets/Editor/TrackStudio/RacingLineCalibrationRunner.cs`** — follows
      `OpusMissionRunner` exactly: custom flags off `Environment.GetCommandLineArgs()`, a
      **request file** rather than static fields (entering play mode domain-reloads), no `-quit`,
      and `EditorApplication.delayCall += Exit` from the `EnteredEditMode` callback.
- [ ] **`Assets/Scripts/Track/RacingLineAutorun.cs`** — `[RuntimeInitializeOnLoadMethod]`,
      consumes-**then-deletes** the request file (a killed run must not arm the next launch),
      attaches a watcher that rides `FixedUpdate` and never calls `Physics.Simulate`.
- [ ] **Car spawn: the `MenuAttract.cs:95-119` recipe verbatim** — `VehicleFactory.Build` →
      `SetSpawn` → `CarInput` → a `SimulationRunner` with `loadControllerDll = false`,
      `logCsv = false`, `physicsRateHz = 400`. `RaceLineFollower : IDriverInputSource` tracks the
      profile with pure pursuit. **It never respawns** — a lap that teleports is garbage data;
      stuck >2 s under 0.3 m/s aborts and reports.
- [ ] **Three laps:** 1 warm-up (discarded), 2 measurement, 3 repeat. Because surface roughness is
      a deterministic positional noise field (`CarVehicle.cs:1419-1431`), **lap 3 must reproduce
      lap 2 to a few ms** — a free repeatability assertion. `|t3 − t2| > 20 ms` means something
      non-deterministic is in the loop, so **fail rather than fit noise**.
- [ ] Fit `muScale` from committed-cornering samples only (`|κ| > 0.5·KappaRef`, `slipNorm` near 1
      — `TyreModel` normalises so `slipNorm = 1` *is* the force-curve peak), plus `a0`, `vMax`,
      `brakeUse`. At most three passes. Write back with `Undo.RecordObject` + `SetDirty`.

---

## M8 — Sectors, splits, telemetry zones

- [ ] Sectors as arc-length ranges on `TrackSpine` (`Project`, `Sample`, `Gap`, `TotalLength`
      already exist). `TrackSectorSet` ScriptableObject; targets derived from the velocity profile.
- [ ] **Do not touch `LapTracker`.** It is `[Serializable]`, round-trips through `JsonUtility` in
      snapshots, and rides the LAN protocol — adding `float[] SectorTimes` is a snapshot and
      possible wire break. Instead `Assets/Scripts/Track/SectorTimer.cs` records locally:
      subscribe to the existing `LapTimer.LapCompleted` event, detect boundary crossings at 10 Hz
      via `TrackSpine.Project` + a sign change in `Gap`. Zero new colliders, zero wire change —
      splits are a presentation concern, and a remote car's splits recompute from the position
      that already replicates. Rejoin guard: accept a crossing only if `s` advanced less than half
      a lap and the boundary is the next expected one.
- [ ] Register exactly **three** telemetry channels — `race/sector`, `race/sector_time`,
      `race/sector_delta` — not one per sector: sector count is track-dependent and
      `CsvLogger.Begin` freezes column order from the registered set, so per-sector channels would
      change the CSV layout per track. Named zones go in a `TelemetryZoneSet` publishing
      `zone/<suffix>`. **`CsvLogger` joins column names with `,` and does no quoting**, so assert
      names are unique, non-empty and comma/newline-free.

---

## M9 — Validator, docs, verification

- [ ] **`Assets/Editor/TrackStudio/TrackStudioValidator.cs`**, `[TRK]`, edit-mode so `-quit` is
      correct (say why in the doc comment, the way `OpusMissionRunner` explains the opposite).
      Checks: checkpoint density `{0..n-1}`; gate width ≥ corridor width; spawn count/spacing/
      heading; every `SurfaceTag` and `TerrainFloorTable` id within `[0, Floors.Length)`; every
      `TerrainLayer` in the scene has a table row; racing-line freshness (`sceneGuid` + `bakeHash`
      + every node inside the corridor) — **the stale-bake-after-a-spline-edit case is the single
      most likely real failure**; calibration honesty (`residual ≤ 2%`, `lapRepeatDelta ≤ 20 ms`);
      sector monotonicity and target sum; telemetry name hygiene; solver determinism (re-run and
      reproduce to 1e-4); and `CarHalfWidth`/`EdgeMargin` parity with `BotDriver`.
      Refuse `TrackKind.Arena` with a message naming the `floorCollider` gap.
- [ ] `Docs/` + repo `README.md` section; archive this plan as entry 41 in `Docs/plan-archive.md`
      when complete (splice from the END marker; the char-count line is self-referential, so use
      a fixed-point loop).
- [ ] **Full gate:** compile 0 `error CS` → `[PMV] ALL PASS` → `[TPV] ALL PASS` → `[COS] ALL PASS`
      → `[PACK] ALL PASS` → `[TRK] ALL PASS` → **Opus mission bit-identical, fault 0** →
      `[BuildMenu] Release build succeeded`.
      Then manual: drive `TTA_Sandbox` as a real scene track; paint ice on a corner and confirm
      the grip drop; bake a line and check it runs out-in-out; run the calibration headlessly;
      confirm bot lap times on `Boost Speedway` are unchanged (the `BotDriver`-untouched proof).

---

## Deviations, stated up front

- **Scene tracks cannot be opened in the in-game Track Builder**, and cannot be sent over LAN as
  data. LAN must ship a **scene name** instead of `trackJson` — otherwise `ActiveTrack` is null,
  `trackJson` is `""`, and the client silently falls through to the **classic oval** while the
  host drives terrain. That is a **protocol bump, v14 → v15**, and a client without the scene must
  be refused with a clear message rather than desynced.
- **Snapshots** (`PauseMenu.cs:332`) must store the scene name too, or Resume drops to the oval.
- **Arena scene tracks are refused in v1.** `ArenaNav.Drop` reads `built.floorCollider.bounds`
  with no raycast fallback, and a terrain scene has no floor slab; giving arenas a playfield
  volume is a separate pass.
- **`BotDriver` is not modified**, so bots still use the runtime out-in-out heuristic even on a
  track with a baked line. Wiring it in is a deliberate follow-up decision once the line is
  visible.
- **The curvature recipe is duplicated**, not shared, to keep `BotDriver` untouched. The validator
  asserts the two agree.

---

# Handling, honest collision, and the rendering-fidelity pass (2026-07-29)

## Context

The user play-tested the city/free-roam build and reported three problem areas.
Three exploration passes pinned every symptom to a verified root cause:

1. **"Slips way too much; wants an explicit Arcade/Sim physics toggle + Forza-style
   assists; car should feel heavier."** The toggle already exists —
   `SessionConfig.ArcadeHandling` (Core/SessionConfig.cs:107) — but it is only
   applied through `TrackBootstrap.BuildArcade → ApplyArcadeHandling`
   (TrackBootstrap.cs:250–301), which requires an arcade **lap race**. MenuUI.cs:694
   forces arcade off for free roam and arenas, so those modes run base grip (1.0 not
   1.45), base ESC (clamp 0.3 not 2.25 N·m), and whatever assist preset is saved
   (default Standard = half strength) — on a town whose grass verges are 0.85 µ.
   No launch control exists anywhere; downforce is ~3.5 % of weight by design.
2. **"Buildings not enterable, ramps with invisible walls, ghost walls."** 87 of 113
   static mesh props have a single-primitive hull: 60 full-footprint boxes (every
   house/store/hangar sealed solid), 27 capsules of which 5 degenerate to **spheres**
   (`HullCyl` makes a capsule; h<2r collapses — dt_volcano is a 12.6 m mesh with a
   5 m ball at y 2.3). Ramp slab hulls have feet parked 0.038–0.192 m off the ground;
   `ench_ramp_bridge`/`haunt_ramp_slab` sit on solid understructure boxes that wall
   off the slope; the city arena ring floats 0.42 m inside the visible stands.
3. **"Missing text/colors/materials, bare billboards, neon lost its glow, rims."**
   Five distinct causes: (a) the three TinyTorque liveries (`M_Paint`,
   `M_Buggy_Paint`, `M_Police_Paint`) are *procedural* in Blender and export as the
   flat tintable "paint" token → grey cars (the police "POLICE" lettering is present
   and correct — it's navy-on-grey invisible); (b) no bloom, no HDR, Gamma space —
   `_EMISSION` above 1 just clips, so neon reads flat (nothing regressed; structural);
   (c) `HideStockRim`'s 3-token keep-list hides Autopia's `whitewall_1`, which is
   part of the **tyre**; (d) cosmetic mounts are coupe-fractions of whole-body
   renderer bounds, so Highwing's stalk wing / Rattletrap's boom float the hats into
   mid-air; (e) billboards are authored **blank in the source kit** (faithful).

**User decisions (asked):** billboards get **in-game generated ad art**; the four
themed circuits' backwards-facing props (last pass's axis bug) **get fixed this
pass** — each map re-ports into its true Blender orientation (mirror of current).

**Hard constraints:** Opus mission numbers bit-identical (leg A −13.615608 mm …
total +58.145523 mm); protocol stays v14 (nothing below crosses the wire); IMGUI
Layout-snapshot discipline; persistence via GameSettings JSON; full headless suite
green (compile / PMV / TPV / CosmeticProbe `Report` / Opus mission / BuildRelease).

**Design-agent verifications that de-risk the plan:**
- The arcade channels have **zero writers outside ArcadeDirector** in
  director-less sessions (`ArcadeRacer.RestoreCar`, `AerialControl` (aerial+boost
  only), and nothing else; `CarVehicle.ResetVehicleTo` touches none) — so a
  per-frame floor component is safe and cannot be stomped.
- **Opus Proving Ground and all three arenas place zero `MeshProp` items** (cones,
  tire stacks, walls, ramps — all procedural). The collider migration cannot
  perturb the regression or arena physics by construction.
- `AssistApplier.ApplyLive` (Core/AssistApplier.cs:82-83) overwrites `car.assists`
  with raw slider values, silently dropping the arcade floor mid-session — a latent
  bug the new per-frame floor fixes for free.

---

## M0 — plan housekeeping

- [x] Spliced the mini-games plan into `Docs/plan-archive.md` as entry 39
      (newest-first, 551 059 chars), titled and dated, flagged for #251/#252 left
      pending. No plan files exist for the Legendary-cars or city passes (planned
      inline — noted in the memory). `plan-archive` memory updated. Script:
      `scratchpad/archive_splice5.py`.

---

# Part A — handling

## M1 — Arcade handling as a mode-independent, live setting

- [x] **New `Core/HandlingFloor.cs`** (MonoBehaviour). Added in
      `TrackBootstrap.BuildPlayerRig` beside the assist floor
      (TrackBootstrap.cs:883-890) for every rig this machine simulates (same
      ownership rule as BuildArcade :265), **skipping firmware slots** (same checks
      as AssistApplier.cs:37-38). Per-frame `Update()`:
      - director session (`rig.arcade != null`): write `racer.gripBase/driveBase/
        stabilityBase` = handling consts or 1f — bases only; ApplyEffects keeps its
        per-frame channel path.
      - no director: write `car.arcadeGripMult/arcadeDriveMult/arcadeStabilityMult`
        directly. **Never touch** boost/yaw/assistMult/handbrakeMult/aerial (owned
        by AerialControl + director).
      - both: when on, re-assert the assist floor via `AssistApplier.ApplyFloor`
        (bots included, mirroring ApplyArcadeHandling's deliberate bot inclusion).
- [x] Retire `TrackBootstrap.ApplyArcadeHandling` (:284-301) — its work moves to
      HandlingFloor; keep BuildArcade's Register loop.
- [x] **Gate the drift latch on handling mode**: `ArcadeDirector.ApplyEffects`
      (:1183) passes `disabled: wrecked || spun || !SessionConfig.ArcadeHandling`
      to `UpdateDrift` (the disabled path is the existing wreck path — ends a held
      drift cleanly).
- [x] **MenuUI: lift the toggle out of the `if (_spArcade)` nest** in all three
      places (Single Player :695-709, Split :946-956, LAN Host :1165-1173) — its
      own always-visible row ("Handling: Arcade / Simulation"), reachable in free
      roam and arenas. Layout-snapshot anything conditional.
- [x] **SettingsPanel (:120-127): read-only label becomes a live toggle** (persist
      `spArcadeHandling` + Save; on transition off, `AssistApplier.ApplyLive` to
      restore slider values). LAN sessions keep the read-only label (flag is a
      join-time session parameter).

Verify: compile; free roam grips and the toggle flips it live; drift latch dies
under Sim handling; Opus headless bit-identical.

## M2 — Launch control, downforce, heavier tune

- [x] **`CarVehicle`**: `AssistSettings` gains `launch`. Private `_launchScale`
      (default 1f); apply as `volts *= _launchScale;` immediately after the proven
      Opus-neutral seam `volts *= arcadeDriveMult;` (CarVehicle.cs:1242). New
      `UpdateLaunchControl(in AssistSettings, float dt)` at the **end** of
      `StepPhysics` (fresh `w.slipRatio`, one-step-latency governor): `launch<=0 →
      scale=1`; arm below `LaunchEngageSpeed`, release-ramp above
      `LaunchReleaseSpeed`; integrate toward worst powered wheel slip =
      `LaunchSlipTarget`, clamp `[LaunchFloor, 1]`. Reset `_launchScale=1` in
      `ResetVehicleTo` (:1123-1124 area). Composes with TC (per-wheel, torque-side,
      stateless) without fighting: LC holds average slip at the peak, TC trims
      transients.
- [x] **`CarVehicle`**: new channel `arcadeDownforce` (N per (m/s)², default 0);
      in `StepPhysics` next to `ApplyAerodynamics()` (:1589):
      `if (arcadeDownforce > 0f) AddForce(-up * arcadeDownforce * v²)` at COM
      (no pitch moment; branch-skipped at 0 → bit-identical).
- [x] **`AssistTuning`**: `LaunchSlipTarget 0.12f` (1.2 × KappaPeak — comment the
      provenance), `LaunchGain ~4f`, `LaunchFloor 0.30f`, `LaunchEngageSpeed 3.0f`,
      `LaunchReleaseSpeed 4.0f`, `LaunchReleaseRate ~2f`.
- [x] **`ArcadeConfig`**: `HandlingAssists` gains `launch = 1f`;
      `HandlingGripBonus 1.45f → 1.60f`; new `HandlingDownforce = 0.10f`
      (≈6.4 N at 8 m/s ≈ 36 % of the 1.8 kg car's weight — comment the
      arithmetic). HandlingFloor owns `car.arcadeDownforce` in both session kinds.
- [x] **Plumbing**: GameSettings p1/p2 launch floats (field-initializer
      back-compat); `SessionConfig` presets (Standard 0.5 / Full 1.0); "Launch
      ctrl" slider in Options + SettingsPanel P1/P2 blocks (unconditional
      controls — IMGUI-safe).

Verify: compile; **Opus headless bit-identical (the critical gate)**; manual
standing starts asphalt/grass, launch 0 vs 1. Feel numbers are play-tune consts.

# Part B — collision

## M3 — Geometry-accurate prop collision (mesh colliders)

- [x] **`PartModelPostprocessor.cs:58`**: TrackProps become readable
      (`file.StartsWith("body_") || path.Contains("/Resources/TrackProps/")`);
      bump `GetVersion()` so all 126 FBX reimport.
- [x] **`TrackCatalog.MeshProp` (:277-294)**: when the mesh instantiates and the
      `hull` lambda is **null**, auto-add a non-convex `MeshCollider` per
      `MeshFilter` piece (`sharedMesh = mf.sharedMesh`, null PhysicMaterial —
      friction parity with the old hulls; BodyPainter.cs:105-113 precedent). A
      non-null `hull` opts out (invoked, no auto). Then **delete the hull lambdas
      from all ~111 static scenery items**, keeping: `haunt_ghost`/`haunt_wisp`
      trigger hulls (concave meshes can't be triggers), `MeshPropDynamic` items
      (non-convex + Rigidbody is illegal — they keep primitives), primitive
      fallbacks. `city_arena` goes auto provisionally — restore a residual ring
      via opt-out only if the new validator says the stands mesh is open at car
      height. While in `Hull`: use `DestroyImmediate` when not playing (kills the
      edit-mode Destroy noise in validators).
- [x] **`PartModelValidator`**: assert `ModelImporter.isReadable == true` for every
      FBX under Resources/TrackProps — the guard against the editor-passes/
      player-build-fails runtime-cook trap.
- [x] **`TrackPresetValidator`**: (i) `CheckDriveIns` gains a `city_gas`
      canopy-bay case (measure the corridor in-editor); the existing 4 should pass
      with exact apertures. (ii) New `CheckColliderCoverage`: for every static
      non-trigger mesh item, build it and assert combined collider bounds ≈
      combined renderer bounds (shortfall < ~5 cm, overshoot < ~10 cm per axis) —
      catches ghost gaps (dt_volcano's ball) and stray hulls (arena ring) forever.
      (iii) BUILD line gains collider count; `TrackFactory.BuildItems` gains a
      Stopwatch cook-time log (watch the 1 243-item town; if > ~250 ms, pre-warm
      with `Physics.BakeMesh` jobs at the top of `TrackFactory.Build`).

Verify: PMV + TPV ALL PASS (two new checks live); Opus bit-identical (proving
ground places zero mesh props — belt and braces); BuildRelease; then a **player
build** smoke-drive: garage/autoshop/firehouse/arena tunnel/gas canopy + every
themed ramp foot.

# Part C — rendering

## M4 — Bloom (dependency-free, Built-in RP)

- [x] **New `Assets/Resources/Shaders/AIHWSimBloom.shader`** ("Hidden/AIHWSim/
      Bloom": bright-pass w/ threshold+soft-knee, separable blur H/V, additive
      composite). Resources placement ships it in builds.
- [x] **New `Scripts/Rendering/CameraBloom.cs`**: `OnRenderImage` — descriptor-
      derived temp RTs at ½/¼/⅛, 3-iteration chain; early-out blit when
      `!SettingsStore.Current.bloom || _mat == null` (one-time warn on stripped
      shader). Static `Attach(Camera)` sets `allowHDR = true` + adds component.
      HDR threshold ~1.0–1.15 (authored >1 emission drives glow); LDR fallback
      threshold ~0.80 — both named consts.
- [x] **Attach at**: TrackBootstrap.cs:527/1077/1084/1340 (drive + split
      viewports), MenuBootstrap.cs:98, MenuAttract.cs:142, GarageBootstrap.cs:89,
      ShowroomRig.cs:73, CrateRig.cs:54. **Never**: CameraSensor (firmware eyes),
      PartPreviewRig/icon RTs, SimBootstrap, TrackBuilderBootstrap.
- [x] **GameSettings**: `bool bloom = true` + toggle in Options video block +
      SettingsPanel. IMGUI draws after all cameras — bloom cannot smear the UI;
      split-screen is per-camera-RT so no cross-viewport bleed (verify 2P
      visually).

Verify: compile; BuildRelease (shader resolves in player); A/B toggle in menu/
garage/drive/2P split; firmware HUD's sensor preview shows no bloom; Opus
bit-identical.

## M5 — Glow retune (data-only, strictly after M4)

- [x] Raise emissive multipliers toward authored ratios (source 5–19×, game
      0.3–2.2×) in one tuning table per family, authored values in comments:
      `TrackCatalog.T` glow args, `PartVisualFactory.Em` sites,
      `CosmeticCatalog.Mat`. Start ≈ authored × 0.5; user tunes with bloom on.

## M6 — Baked liveries (coupe / buggy / police)

- [x] **`Blender/build_vehicles.py`**: remap `M_Paint`/`M_Buggy_Paint`/
      `M_Police_Paint` from the tintable "paint" token to per-car baked tokens;
      add `"bake_token"` per config — exactly the Rattletrap template (:188-202,
      `bake_token_to_texture` :403-490). Re-export the three cars; commit
      `body_<key>_paint.png` beside the FBX.
- [x] **`PartVisualFactory`**: three lazily-built materials mirroring `RustPaint`
      (:151-167) — white tint, Resources texture, measured rough/metal from the
      exporter's printout; bind tokens in the accent tables (follow every
      "rustpaint" reference as the wiring checklist).
- [x] **`CarVehicle.HasPaintableBody` (:831-841)**: add the three shapes beside
      `Rattle` — a baked shell can't also drive `_bodyMat.mainTexture`.
- [x] **Deviation to state loudly**: TT Coupe (the starter car), Baja and Patrol
      lose garage repaint/tint — the authored livery replaces the colour picker
      (Rattletrap precedent). POLICE lettering becomes visible for free.

Verify: compile; CosmeticProbe PASS; garage visual (liveries render, paint mode
absent for the three); PMV; BuildRelease.

## M7 — Whitewall fix + probe hardening

- [x] **`HideStockRim` (PartVisualFactory.cs:311)**: keep-list gains `whitewall`.
      First print hidden-renderer names per wheel FBX via the probe and decide
      from the printout whether any `dark`/`steel` piece is tyre-side.
- [x] **`CosmeticProbe`**: hidden count → hidden **names**; hard FAIL if any
      hidden name contains tire/tyre/whitewall; extend the matrix to **all**
      presets × all 10 rims, explicitly the four Legendary cars (their wheels
      have no `brake` piece — assert by name list, not structure). The Autopia
      case must fail before the fix and pass after (proves the assertion bites).

## M8 — Measured cosmetic mounts for the Legendary bodies

- [x] **`build_vehicles.py`**: measure roof plane / nose ornament point / wing
      mount per Legendary body; print a paste-ready C# table (the VEHJSON-paste
      contract, VehiclePresets.cs:253-259).
- [x] **`CosmeticMounts`**: per-`BodyShape` override table of absolute local
      mount points, used when present; coupe-fraction path stays as fallback.
      Kills the bounds inflation from Highwing's stalk wing / Rattletrap's boom.
      Bobble keeps the antenna-tip rule with a measured rear-deck fallback.
- [x] **CosmeticProbe**: mount check — body meshes are readable, so cook a temp
      MeshCollider (BodyPainter pattern) and assert a downward ray from above each
      mount hits body geometry within ~15 mm. Visual pass on all four cars with
      topper/ornament/wing/bobble.

## M9 — Billboard ad art (user decision: generate in-game)

- [x] **New poster-texture generator** (TrackBuilder-style, cached by variant):
      seeded ad variants — stripe/block motifs + crude lettering via a small
      in-code pixel font ("TINY TORQUE", "SPEED SODA", …), mild emission so M4
      bloom lifts the lit face. Precedent: NoiseTexture/StripeTexture/Chevron +
      the attic wallpaper (MapAmbience.cs:423).
- [x] **`city_billboard`**: build lambda adds a thin poster **quad overlay**
      sized to the face (avoids unknown FBX UVs entirely). A tiny
      `BillboardPoster` component picks the variant in `Start()` from a hash of
      world position (posed by then; deterministic per placement; LAN-consistent
      since layouts are identical).
- [x] Stretch (same mechanism): a neon-word overlay for `dt_bld_block`'s blank
      roof signface.

# Part D — the mirrored circuits

## M10 — Re-port the four themed circuits into true orientation

- [x] Switch Downtown, Toy Room, Enchanted Kingdom and Haunted Hollow presets to
      `new MapLayout(d, seed, shiftX, shiftZ', meshAxes: true)`, re-deriving each
      `shiftZ'` from the authored bounds exactly as Torque Falls did (TPV
      in-bounds check verifies). `Road()`/`ForTiles()` were already made
      negation-safe last pass.
- [x] Grep each port for any placement bypassing `L.Heading`/`L.Prop` (raw yaw or
      static `MapLayout.Yaw`) and convert.
- [x] **Temporary probe** (CityAxisProbe pattern, delete after): dump position/yaw
      of a handful of asymmetric props per map (doors, signs, building fronts) and
      check against the Blender source layout dump — the same proof used for
      Torque Falls (facing rows at z −24.6 yaw 0 / z −18.2 yaw 180).
- [x] Note for README: circuits now match the Blender source orientation; the
      previous builds were mirror images. Lap records keep their names; times were
      set on the mirrored layout.

Verify: TPV ALL PASS (in-bounds, spline, coverage); bot lap sanity on one circuit
(BotPath follows the mirrored spline consistently — same CP order).

---

## M11 — Docs + full verification

- [x] README: handling rewrite under Arcade mode (mode-independent + live +
      launch control + downforce + grip 1.60), mesh-collision section replacing
      the hull prose, "Rendering: bloom, baked liveries, posters" section,
      mirrored-circuits note, free-roam drive-in + emission updates. Protocol
      stays v14 (nothing crossed the wire).
- [x] Memory: `cosmetics-pipeline` extended (TrackProps CPU-readable + runtime
      MeshCollider cooking + coverage/drive-in checks; all five ports on
      meshAxes with the flip recipe; the X-mirror measurement; three more
      baked liveries + repaint exclusions).
- [x] Full gate GREEN: compile 0 `error CS` (the [Script Updater] pre-pass
      lines in the build log are benign) → `[PMV] RESULT ALL PASS (204)` +
      `READABLE all 126` → `[TPV] RESULT ALL PASS (12)` with five DRIVEIN
      clear + `COVER 111` → `[COS] RESULT ALL PASS` (incl. the new tyre-hidden
      and topper-mount checks) → **Opus completed fault 0, bit-identical**
      (legA −13.615608 mm, turn +0.187378°, legB +15.803814 mm, total
      +58.145523 mm) → `[BuildMenu] Release build succeeded`.
- [ ] User play-test: handling feel consts (`HandlingGripBonus`,
      `HandlingDownforce`, launch consts, glow table, bloom
      threshold/intensity) are the expected tuning surface, plus the in-player
      smoke drive (drive-ins, ramp feet, bloom toggle, mirrored circuits).

**Deviations, stated:** P8 measures the Legendary SHELL by excluding appendage
tokens (hwwhite/steel/gunmetal/face) instead of an exporter mount table — same
measurement, self-maintaining; the probe's ray check is topper-only and
report-only for open-top shapes (the Baja has no roof; the hat perches at cage
height). The dt_bld_block neon-word overlay (stretch) was skipped — under bloom
the authored blank gold sign face reads as lit. The DRIVEIN corridors moved to
the props' true bays: probing the real meshes showed the old hand-authored
hulls (and corridors) sat on the X-mirrored side — the garage's "open" corridor
ran over the closed roller-door bay and its tyre stacks.

---

# Two UI fixes, then three mini-game modes — Demolition, CTF, Soccer (2026-07-28)

**Archived with two milestones still open** — #251 `Modes/ModeNetLink.cs` (stream ball/flag pose + per-car health to LAN clients) and #252 (split-screen menu rows 3–4 + per-viewport MatchHud) are carried as pending tasks, and the user play-test was undriven at archive time.


## Context

Two things are broken or missing in screens the last pass touched, and then the
game gets its first non-racing rules.

**The bugs.** In the Showroom, picking a cosmetic **rim** either does nothing at
all or puts something visibly wrong on the wheel — the other four cosmetic slots
work. And the **Garage has no way out**: `DrawTopBar` (GarageUI.cs:1120) offers
Name/Save/Load/New/Undo/Redo/Drive and nothing else, no `GameFlow.LoadMenu` call
exists anywhere under `Scripts/Garage/`, the Garage scene has no `PauseMenu`, and
Escape is only consumed mid-part-drag (GarageUI.cs:491). The only exit is to
start a drive. The user also wants the Garage re-dressed in the Showroom's
layout language while we are in there.

**The feature.** Three multiplayer mini-games — a demolition derby, capture the
flag, and a Rocket-League-style soccer mode — playable against **bots**, in
**4-way split-screen**, and over **LAN**, with the **full aerial** driving model
(jump, double jump, flips, air roll) for soccer. All three ship in this pass.

The reconnaissance that shapes the design:

- **There is no game-mode concept at all.** `SessionMode` (SessionConfig.cs:9)
  describes *who plays*, never *what the rules are*. Rules are four booleans and
  an int on a static class, and the composition is hard-coded in
  `TrackBootstrap.Awake` behind `if (SessionConfig.TargetLaps > 0 && _lapTimer != null)`.
- **But `ArcadeDirector` is already a working second rules object** — 1741 lines
  that never touch `RaceDirector`: `Instance` + `IsAuthority` + `Register(rig) →
  per-car state bag` + an `Update()` authority loop + a typed event stream + one
  `ArcadeNetLink` that is the only thing knowing both arcade and network. **The
  three modes are built as siblings of `ArcadeDirector`, not of `RaceDirector`.**
- **The single biggest obstacle is the implicit "every session has a racing
  line".** `TrackSpine`/`BotPath` is load-bearing for five systems that are not
  about laps: bot steering, `TrackRespawn` (`Available => _spine != null`),
  arcade box placement, missile targeting and wreck recovery. An arena has no
  line, so simply "not building a RaceDirector" silently disables respawn and bot
  navigation too. An arena analogue must exist before any mode is playable.
- **No teams anywhere** (`grep -rni "\bteam\b"` over `Assets/Scripts` → zero
  hits), and **one spawn point per map** (`TrackFactory.ResolveSpawn` takes the
  *first* `ItemBehavior.Spawn`; everything else is a 2-wide grid derived from it).
- **No health, damage or impact concept anywhere** — and exactly one
  `OnCollisionEnter` in the codebase, `VehicleAudio.cs:292`, whose own comment
  ("needs no cooperation from CarVehicle") is the recipe for derby damage.
- **No aerial anything.** There is a drift hop (`ArcadeConfig.DriftHopImpulse`)
  and three boost sources, but no airborne state, no air torque, no flip. This is
  genuine new vehicle code and it touches the physics the Opus regression guards.
- **Networked non-car objects already work**: the `ArcProjState` projectile
  stream carries four kinds, host-authoritative, with clients rebuilding
  *visual-only* copies that "must never be able to detonate anything". A ball, a
  flag, a health pack and a mine are four more kinds.
- Ceilings today: **2 local players** (`BuildPlayerCamera` splits top/bottom for
  index 0/1 only; the menu hard-codes two rows) and **4 over LAN**
  (`NetSession.MaxPlayers`). 3v3 needs 6.

Non-negotiables carried from the codebase: only the UI layer may consult
`Progression`; `VehiclePresets.Resolve`, `VehicleLibrary`, `VehicleFactory`,
`SessionConfig` and `NetSession` stay progression-blind. IMGUI stays pass-safe
(snapshot on Layout, never `ExitGUI` at runtime). Anything new on the wire bumps
the protocol, which is checked with strict equality on Hello.

---

## Milestone 0 — plan housekeeping

- [x] Spliced into `Docs/plan-archive.md` as entry 38 (525 124 chars), headings
      promoted a level, titled with the date and flagged play-test-undriven;
      header coverage line and the `plan-archive` memory updated; the Appendix
      deleted from this file. Script: `scratchpad/archive_splice4.py`.

---

# Part 1 — the two fixes

## Milestone 1 — Showroom rims

**DONE — and the probe named the cause.** Every rim mounted correctly all along:
right scale (`norm 0.0409` on all 60 car×rim combinations), renderers enabled,
`hid 3` proving `HideStockRim` worked. But **every rim sat 0.9–4.9 mm INSIDE the
tyre's outer face.** These rims are narrower than the stock rim faces they
replace (41 mm disc against a 66 mm tyre), so seated at the hub plane they drop
into the tyre's aperture and hide behind the sidewall — which reads as "nothing
changed" on a stock wheel and "something's wrong" on a wider one. Seating them
on the measured tyre face fixes it: now `proud 0.7 mm` (stock), `1.0 mm`
(coupe), `1.1 mm` (baja), `[COS] RESULT ALL PASS`.

**Deviation:** the load-failure warning went only into `PartMeshLibrary.Load`,
not also into `CosmeticCatalog.Build`. Load caches misses, so warning there
fires exactly once per key; warning in Build would fire on every call for every
wheel, every rebuild.

Ruled out along the way, so nobody re-checks them: the FBX orientation (Unity
extent `[0.0227, 0.0409, 0.0409]` — thin on the axle, disc on Y/Z), the scale
factor, `ApplyRims`' left/right half-turn (byte-identical to `BuildWheelViz`'s),
the mount timing (`root.SetActive(true)` at VehicleFactory.cs:198 runs `Awake` →
`BuildWheels` synchronously, 13 lines before the cosmetics), `ApplyCosmetics`'
write of `d.cosRim`, and the material tokens.

- [x] `Assets/Editor/CosmeticProbe.cs` — a play-mode validator (the editor does
      not run `Awake` in edit mode, and `CarVehicle.Awake` is what builds the
      wheel holders, so an edit-mode probe would measure a car with no wheels).
      Builds all 6 designs × 10 rims and reports per wheel: renderers on/off,
      stock renderers hidden, disc size normalised to the author radius, and the
      rim face against the tyre face.
- [x] `HideStockRim` gained its primitive-fallback arm (match the rim family by
      shared material, leaving the motor can's `Can` material alone). The probe
      shows the mesh path was already hiding 3 renderers per wheel, so this was
      latent, not the live fault — but it is real.
- [x] `ApplyRims` iterates `car.WheelCount`, reading the design only for radius
      and side.
- [x] **The fix:** new `PartVisualFactory.TyreHalfWidth(holder, radius)` measures
      the wheel mesh's own half-width along the axle — deliberately ignoring the
      motor can, a holder sibling that sticks out three times as far — and
      `ApplyRims` offsets the rim so its face lands `RimProudFrac` (5 %) past it.
      Falls back to `BuildWheelViz`'s exact `radius * 0.4` on the primitive path.
- [x] `PartMeshLibrary.Load` warns once per missing key. `LocalRendererBounds`
      was promoted to a shared `PartVisualFactory` helper; `CosmeticMounts` and
      `CosmeticProbe` both use it instead of each keeping a private copy of the
      eight-corner walk.
- [x] `!RECESSED` is now a hard FAIL in the probe, so this exact defect cannot
      come back silently.

## Milestone 2 — Garage: a way out, in the Showroom's clothes

The Showroom's back is a *page* change inside one scene (`ResultBack` →
`CloseShowroom` → `GoTo` → `ScreenFade.Dip`). The Garage is a *separate scene*,
so it uses the scene-level equivalent — `ScreenFade.To(GameFlow.LoadMenu)`, the
same call `RaceDirector.cs:357` and `PauseMenu.cs:260` already make.
`UiRuntime`/`ScreenFade` are already alive in the Garage scene via
`[RuntimeInitializeOnLoadMethod]`, so this needs no setup. **Converting the
Garage into a `Page.Garage` is explicitly not in scope** — `GarageBootstrap`
builds its own floor, lights and `Camera.main`, all of which would fight
`MenuAttract`.

- [x] Layout snapshots added (`_showLoadDraw`, `_leftTabDraw`, `_selDraw`,
      `_selTypeDraw`) and read by everything that decides which controls exist:
      the load popup, the BODY/PARTS/PAINT switch, and the inspector switch —
      the last through a new `SelectedDraw(type, count)`, so a list click that
      changes the selection mid-pass can no longer offer Repaint a different
      inspector than Layout registered, nor index the new list with the old
      index.
- [x] `Assets/Scripts/UI/PanelLayout.cs` — `LeftRect`, `RightRect`,
      `BottomBarRect`, `HintRect`, `NoteRect`, reproducing the Showroom's exact
      geometry so nothing moves. `ShowroomUI` and `CrateOpenUI` now use it too.
- [x] `GarageUI` re-dressed: the top bar is gone and every verb moved to a
      Showroom-shaped bottom bar (`← Back`, name, Save, Load, New, undo, redo,
      `Drive ▶`); the left column is the build tabs with the stats box at its
      foot; the right column is the parts list + inspector. The Load popup now
      opens upward from the bar, and `_barRect` replaced `_topRect` in
      `PointerOverUI` so clicks still do not fall through to the orbit camera.
- [x] `MenuNav.BeginFrame("garage")` / `EndFrame()` wired, with only the bottom
      bar's controls wrapped — the pad already owns the car through the
      part-manipulation layer, so the palette and inspectors stay mouse-driven.
      Pad East deselects a part when one is selected and backs out when none is,
      which is the only way to keep one button doing both without a single press
      doing both at once.
- [x] Back auto-saves (DoDrive's precedent — no dirty flag exists to prompt
      from), then `ScreenFade.To(GameFlow.LoadMenu)`. Escape from the idle state
      does the same; it previously only cancelled an in-progress part drag.
- [x] `TrackBuilderUI` got the same escape hatch: `← Back` at the head of its
      existing top bar, plus Escape-from-idle. **Deviation:** it keeps its top
      bar rather than gaining a bottom one — the user asked for the Garage to be
      re-laid out, and half-re-laying-out the builder would leave the two
      editors looking less alike, not more.

---

# Part 2 — the mini-game modes

**Status: playable solo and in split-screen; LAN is partly done.** Everything
below is built and compiles clean, and the verification suite is green. Two
pieces are NOT finished and are listed under "Left undone" at the end — read that
before play-testing LAN.

## Milestone 3 — mode foundation — DONE

- [x] `MatchMode` enum lives in `Core/SessionConfig.cs` beside `SessionMode` and
      `DriveControl`, **not** in `AIHWSim.Modes`: SessionConfig has to name it,
      and Core must not depend on the layers above it. `SessionConfig` gained
      `Match`, `TargetScore`, `TimeLimitSec`, `IsArenaMatch`, `IsTeamMatch`;
      `PlayerSlot` gained `team`.
- [x] `Track/MatchDirector.cs` — the shared base (countdown, `FreezeCars`,
      `PlayerFinished`, the one-way `EnterResults`, the results frame,
      `RestartMatch`/`ResetMatch`). `RaceDirector` is now a subclass and its lap
      rules did not move. Lives in `Track/` rather than `Modes/` so the
      dependency runs one way: Modes → Track → Core.
- [x] `Modes/ArenaNav.cs` — spawn ring, floor bounds, centre, radius,
      `TrySpawn(team, index)`, `TryNearestFree`, `Drop`. `TrackRespawn` gained an
      arena branch, so the R key and bot unsticking keep working with no line.
- [x] Multi-spawn with no new schema: `BuiltTrack.spawns` is every `Spawn` item
      with `PlacedItem.order` carried through as the team. The single-spawn
      answer a race uses is untouched.
- [x] `TrackBootstrap.Awake`'s rules tail is now `BuildMatchDirector()` — one
      method per mode, plus `HoldGrid` and `HookCratePayout` shared by all of
      them. `ArenaNav.SetTrack` is wired into all three composition paths.

## Milestone 4 — impact, health, death — DONE

- [x] `Modes/CarImpact.cs` classifies every collision into ram / side / wall from
      `relativeVelocity` and the contact normal, on the car's own GameObject and
      with no cooperation from `CarVehicle` — VehicleAudio's trick.
- [x] `Modes/MatchRacer.cs` (health, alive, team, score, place, carrying,
      heldMines) + `PlayerRig.match`. `Modes/ModeConfig.cs` holds every tunable.
- [x] Death reuses the arcade wreck's punt+tumble, then `Spectate` freezes and
      hides the car rather than destroying it — its camera is still a viewport.

## Milestone 5 — pickups — DONE

- [x] `Modes/ArenaPickup.cs` (repair cross / bomb crate) off the `ArcadeItemBox`
      template, including the LAN rule that a client destroys the collider at
      Awake. **Deviation:** one component with a `Kind` rather than two files —
      the spin, the respawn clock and the LAN rule are the whole of both.
- [x] `Modes/Landmine.cs` off the `Banana` template, with an area blast and
      falloff instead of a single-car spin.
- [x] Visuals are `TrackBuilder` primitives. **Noted deviation** from the Blender
      pipeline, the same way the arcade props started.

## Milestones 6, 7, 9 — the three modes — DONE

- [x] `DerbyDirector` — ram damage, side/wall damage to both, pickups on two
      rings, mine drop on the use-item button, elimination from the bottom of the
      places, last standing wins.
- [x] `CtfDirector` + `Modes/Flag.cs` — bases derived from each team's spawn
      centroid, polled containment (never triggers — see the note in `Flag`),
      carry/drop/return/score, 20 s auto-return.
- [x] `SoccerDirector` + `Modes/SoccerBall.cs` + goal volumes — authority-only
      dynamic ball, celebration freeze, kick-off reset, aerials enabled here and
      nowhere else.

## Milestone 8 — aerial driving — DONE

- [x] `CarVehicle` gained `arcadeAerial` (off by default), `Grounded`,
      `AerialJump`, `AerialFlip`, `AerialTorque` — in the style of the seven
      `arcade*` channels, and the linear half of the flip goes through the centre
      of mass exactly as `ArcadeImpulse(Vector3)`'s comment demands.
- [x] `Modes/AerialControl.cs` owns the state machine (jump → double jump →
      directional flip inside the window → free air roll) and the boost tank, in
      FixedUpdate so a jump's height does not depend on frame rate.
- [x] `DriveAction.Jump` / `.Boost` through the whole seam: `KeyBindings`,
      `InputReader`, `IDriverInputSource` and all four implementations,
      `InputState` on the wire, `ClientInputSender`. **Deviation:** the defaults
      are E and Q, not Space and Shift — those are handbrake and use-item, and a
      duplicate default reads as a broken binding table.
- [x] **Opus mission returns numbers IDENTICAL to the pre-change baseline**
      (leg A −13.6156 mm, turn +0.1874°, leg B +15.8038 mm, total +58.1455 mm),
      which is the proof the channel is genuinely inert.

## Milestone 10 — arena bots — DONE

- [x] `BotDriver.SetChaseTarget(worldPos, speedScale, holdSeconds)` — a push-in
      seam in `SetBlind`'s style, plus an arena steering mode that keeps the
      pure-pursuit core and swaps the corridor for three whisker raycasts and a
      back-up recovery.
- [x] `Modes/BotPolicy.cs` — derby hunts the weakest reachable car, CTF picks
      between running home / rescuing its own flag / chasing the carrier /
      taking theirs, soccer drives at the point behind the ball on the goal side
      and boosts only when lined up and far out.

## Milestone 11 — LAN — PARTLY DONE

- [x] `ProtocolVersion` 11 → **12**, `MaxPlayers` 4 → **6**.
- [x] Mode, target score and time limit on `WelcomeMsg` AND `SessionStateMsg`
      (a mid-session rules change has to reach a client the way a lap-count
      change already does), applied through a new `ApplyMatchRules`.
      `RosterEntry.team`. Jump/boost bits in the input flags.
- [x] `NetPack.ProjBall / ProjFlag / ProjPickup` kinds reserved on the
      projectile stream.
- [ ] **`Modes/ModeNetLink.cs` is NOT written.** See "Left undone".

## Milestone 12 — split-screen, menu, HUD — PARTLY DONE

- [x] `TrackBootstrap.ViewportFor(index, count)` — quadrants for 3-4 local
      players, stacked halves for 2, full screen for 1.
- [x] `MenuUI`: a **Mode** picker at the top of Single Player that moves the
      track selection to that mode's arena, a score-to-win stepper for CTF and
      soccer, laps hidden in an arena, arcade items disabled in an arena, and
      `ApplyMatchRules()` splitting the roster into two sides after it is built.
- [ ] Split-screen menu rows 3 and 4, and the per-viewport `MatchHud`. See
      "Left undone".

## Milestone 13 — arenas — DONE

- [x] **Scrapyard Bowl** (derby, 40×40, walled ring, 8 spawns), **Cargo Yard**
      (CTF, 44×52, mirrored ends, 4+4 spawns), **Torque Dome** (soccer, 40×56,
      goal mouths, wing boost pads, corner ramps, 3+3 spawns) — all authored as
      `TrackPresets` functions with the existing helpers.
- [x] `TrackPresetValidator` taught what an arena is (no finish + a spawn ring),
      and checks them on their own terms: 4+ spawns, an even number so a team
      mode has two equal sides, and no checkpoints.

## Milestone 14 — verify — GREEN

- [x] Headless compile: **0 `error CS`**.
- [x] `[PMV] RESULT ALL PASS (161 assets)`.
- [x] `[TPV] RESULT ALL PASS (11 presets)` — the three arenas included.
- [x] `[COS] RESULT ALL PASS` — the M1 rim probe.
- [x] **Opus mission `completed: true, fault: 0`**, numbers bit-identical to the
      baseline.
- [x] `[BuildMenu] Release build succeeded`.
- [x] `README.md`: a "Mini-game modes" section and the protocol table at v12.
- [ ] User play-test.

## Left undone — read before play-testing

1. **LAN arena matches are host-only in practice.** The rules, the roster, the
    teams and the aerial inputs all cross the wire, so a LAN *race* is unaffected
    and a LAN arena match runs correctly ON THE HOST. But nothing yet streams the
    ball's or the flag's pose, or per-car health, to a client: `SoccerBall`
    already builds itself kinematic on a client and exposes `ApplyRemote`, and
    the `ProjBall`/`ProjFlag` kinds are reserved — what is missing is the
    `ModeNetLink` component that publishes them on the host and applies them on
    the client, modelled on `ArcadeNetLink`. Until that exists a client will see
    a ball that never moves.
2. **The split-screen menu still offers two local players**, though the
    viewports handle four. `MenuUI.DrawMultiplayer` hard-codes `DrawPlayerRow(1)`
    and `(2)`; rows 3 and 4 plus the device-conflict check are the remaining
    work.
3. **No per-viewport health bars.** Each director draws a centred live banner
    (health, score, who is carrying what) which works in solo and is readable in
    split-screen, but the planned `MatchHud` drawn per viewport through the
    `ArcadeFeedback.Draw(Rect, …)` seam was not built.
4. Mode furniture — flag, ball, goals, pickups, mines — is procedural
    `TrackBuilder` geometry, not authored Blender props.

---

# TinyTorque cosmetics: 47 unlockables, 4 crates, championship + scrap economy (2026-07-28)

**Archived with the user play-test still UNDRIVEN** — every milestone shipped and verified headlessly (compile, PMV, TPV, Opus, release build), but nobody had looked at a cosmetic in the running game when the next pass started.

## Context

`E:\EE Projects\AI_3D_Modeling\TinyTorque_RC` shipped a cosmetics pack the game
had never seen: **47 unlockable decorations** in five slots (topper, rim,
ornament, bobble, wing), themed across arcade/toybox/enchanted/haunted, plus
**4 crate models**. `models/TinyTorque_cosmetics.json` is the catalogue (slots,
rarities, mount frames, per-box weights/floor/pity, dupe values, direct costs);
`models/cosmetics_fbx/` holds 51 FBX (one merged object each, ≤5 material slots,
**39 distinct `M_Cos_*` materials**); the `materials()` table in
`scripts/tt_20_cosmetics.py` carries the authored PBR — base colour, metallic,
roughness, coat, sheen, **emission colour + strength**, and two Fresnel-alpha
ghost shaders.

The old unlock system was a placeholder: `UnlockCatalog` listed 20 items,
`Progression.OnWin()` granted one at random from whatever was still locked, and
cheat codes short-circuited the lot. No currency, no crates, no championship —
the Showroom's "Special:" row still read *coming soon*.

Built, per four decisions taken with the user:

1. **Everything goes into the crates.** The 20 legacy items got rarity tiers and
   joined the 47 cosmetics; the random-item-on-win grant was deleted. Principled
   rather than a fudge: the manifest's per-item `odds` are exactly
   `weight[rarity] / |pool[rarity]|`, so *weights + uniform-within-rarity*
   reproduces the authored table bit-for-bit and extends cleanly as the pool
   grows. Authored `weights`, `floor` and `pity` implemented verbatim.
2. **A real championship mode**, so the Gold Vault's "win a championship event"
   and the Cursed Casket's "seasonal" both have honest triggers. Duplicates
   recycle into **Scrap**; Scrap buys from a rotating shop.
3. **Locked cosmetics are previewable**: clicking one fits it to the Showroom
   turntable car, never written to the saved loadout.
4. **Cosmetics ride into races and over LAN** as fields on `VehicleDesign`
   (protocol v10 → v11), **purely visual** — no mass, no aero, no collider — so
   `MassProperties`, the bots and the Opus regression were untouched.

Colour fidelity was a hard user requirement. The project has no `.mat` assets
and imports FBX with `materialImportMode: None`, so the Blender values were
**exported as data, never hand-transcribed**, exactly as `build_vehicles.py`
already prints its numbers for pasting into `VehiclePresets`.

## Milestone 1 — Blender export (`Blender/build_cosmetics.py`, new)

**DONE.** `s_item = 0.092278` (0.420 / measured body 4.55145), `s_rim = 0.069500`
(0.033 / measured tyre 0.474817), 51 FBX, 39 materials, 82 964 tris total (max
4 268 on rim_cog). Unity imported all 51 with `materialImportMode: 0`,
`useFileScale: 0`, `globalScale: 1`. **One deviation:** the export flags are
`build_vehicles.py`'s (`apply_unit_scale=False, global_scale=0.01,
axis_forward='-Z', axis_up='Y', bake_space_transform=True,
mesh_smooth_type='EDGE'`), NOT the RC pack's own — the game's importer contract
is set by `PartModelPostprocessor`, and matching the pack would have imported
everything at 100x. Each of the 51 `C_*`/`B_*` objects is separated by material
into children named `<matkey>_<n>` so `PartMeshLibrary.AssignByName` binds them;
rims keep their own frame (axis +Y, origin at the hub) and their own measured
scale. `PartModelPostprocessor.IsPartModel` extended with `Resources/Cosmetics/`.

## Milestone 2 — `Garage/CosmeticCatalog.cs`

**DONE.** 39 materials built lazily from the M1 JSON via `TrackBuilder.StandardMat`
(`_Color`, `_Metallic`, `_Glossiness = 1 − roughness`, emission → `_EMISSION` +
colour × strength, `alpha < 1` → Fade). 47 `CosmeticItem` rows and 4 `CrateDef`
rows transcribed from the manifest. The token array is emitted **longest-first**
(`glow_gold` before `gold`), which is load-bearing for a first-match substring
matcher. **One entry point, not two:** a crate id is just another mesh key, so
the planned separate `BuildCrate` would have been the same three lines twice.

## Milestone 3 — mounting

**DONE.** `VehicleDesign` gained five string fields; JsonUtility back-compat is
automatic and they ride the design into races, split-screen, snapshots and LAN
with no extra plumbing. Mount frames are **derived from the design**, not from
the manifest's coupe-relative numbers: re-expressed as fractions of the
authoring car's body box, exact on the coupe and sane on anything else. The
bobble reads the built antenna's own bounds. Rims parent to the wheel viz holder
and hide the stock face. All geometry on `VizLayer`; zero physics.
`NetSession.ProtocolVersion` 10 → 11.

## Milestone 4 — economy

**DONE.** `PlayerProgress` v2 (`scrap`, unopened `crates`, per-box `pity`,
championship state; a v1 file migrates additively). `UnlockCatalog` gained
rarities and the 47 cosmetics as `UnlockKind.Cosmetic` — one catalog, one save
key space, cheat codes preserved. `CrateSystem.Open` honours weights, theme
filtering, `floor` and `pity`. The random-item-on-win grant is gone; wins award
crates.

## Milestone 5 — championship

**DONE.** Three series × 4 rounds over the existing circuits (Rookie Cup, Torque
Trophy, Midnight Series), points 10/8/6/5/4/3/2/1, roster pinned for the series,
standings in `progress.json`. `RaceDirector.DrawResults` grew "Next round ▶" and
a final standings screen. Payouts: Scrap Crate = finish any race · Chrome Case =
podium with ≥2 opponents · Gold Vault = win a championship · Cursed Casket = win
the Midnight Series.

## Milestones 6-8 — crate opening, Showroom cosmetics, scrap shop

**DONE.** `CrateRig` (a lit turntable parked far from the scene, own camera),
`CrateOpenUI` (inventory → pick → open → one reveal per pull on the item's real
3D model, with "DUPLICATE +N scrap"), `AwardReveal` recast as the crate-earned
notice on both results screens. The Showroom gained a slot strip over a
`PartIconFactory` icon grid; locked entries show a padlock, rarity, description
and a **Buy for N scrap** button, and fit to the turntable when clicked without
touching the saved loadout. The legacy "Topper" cycle was renamed **"Roof kit"**
to free the slot name. `Page.Shop`: 6 offers reseeded daily from the local date,
plus the four crates.

## Milestone 9 — verify

**ALL GREEN.** Compile 0 `error CS`; `[PMV] RESULT ALL PASS (161 assets)` with
the 51 cosmetics added as ±10 % extent windows; `[TPV] RESULT ALL PASS
(8 presets)`; Opus mission `completed: true, fault: 0` (leg A −13.6 mm, turn
+0.19°, total +58 mm) — the proof that "purely visual" held; release build
succeeded. Note for next time: the **Opus runner takes no `-quit`** (play mode
has to keep the process alive; with `-quit` it exits before the mission writes
its result).

**Deviations recorded during M4–M8:**
- `CrateSystem` draws from the FULL pool per tier, not the locked-only pool the
  plan described. Locked-only would have changed the authored per-item odds the
  moment anything was owned; drawing from everything and paying `dupe_value` on
  a repeat is what the manifest's own `duplicates` rule says, and it reproduces
  the `odds` table exactly.
- Crate prices are not in the manifest (crates are earned there, not sold), so
  the shop derives them: expected duplicate value × pull count × 4. Buying a
  crate is deliberately a worse deal than earning one.
- The crate room is reached from the ROOT menu, not from inside the Showroom —
  it needs the whole screen for its own rig, exactly like the Showroom does, and
  nesting one full-screen 3D page inside another would have fought the fade.

**Left undriven when archived:** the user play-test — every cosmetic reads with
its authored colours and glow; locked items preview but never equip; crates open
at the right odds; a championship runs end to end and pays a Gold Vault; and
cosmetics show in-race and on a LAN peer.

---

# Fix contaminated part-preview icons + showroom car sunk in the podium (2026-07-28)

**Archived with the user eyeball pass still UNDRIVEN** — the icon and podium fixes compiled clean but had not been looked at in the running game when the cosmetics pass started.


## Context

Two rendering bugs reported after the arcade UI pass:

1. **Part icons in the Track Builder and Garage palettes show the wrong/clipped
   geometry** — each icon is contaminated by other models. Root cause
   (`Garage/PartIconFactory.cs:57-104`): `Snapshot()` builds every icon's model
   at the same fixed origin `(0,-500,0)` and tears down with **deferred**
   `Object.Destroy`, but `cam.Render()` is **immediate** — and both palettes
   batch-build all icons in a single frame (`GarageUI.cs:108-114` in `Start()`;
   `TrackBuilderUI.cs:1120` lazily inside one OnGUI repaint). So icon N renders
   with models 1..N-1 still parked at the same point: clean first icon, then
   increasingly clipped garbage — cached permanently by key. The recent large
   map props amplified it (bigger bounds → wider frustum swallows more strays).
   `TrackIconFactory.cs:36-37` also defers `Destroy(col)` so temp colliders are
   live during the render.

2. **Showroom vehicle clips under the podium floor.** Codebase convention
   (`MenuBootstrap.cs:66`, `GarageBootstrap.cs:79`) is *car origin at y=0,
   floor top at y=−0.078 ("~ wheel contact")*. `ShowroomRig.cs:52-57` builds
   the podium with its **top at stage y=0** and (:88-98) parents the car at
   stage y=0 too — so the lower ~78 mm (wheels + underside) is buried in the
   disc. Default-design math: tyre bottom = wheel `localPos.y` (−0.015) +
   `SuspensionGeometry.HubOffsetLocal(...).y` (−0.030) − `radius` (0.033) =
   **−0.078**. Wheel sizes vary per design (TT Baja), so the fix should be
   computed per design, not hardcoded.

No physics, wire, or save-format changes — preview-only code.

## Milestone A — plan housekeeping

- [x] Splice the finished arcade UI plan (kept verbatim below the ARCHIVE
      marker at the END of this file) into `Docs/plan-archive.md` as the newest
      entry, titled
      `# Arcade UI/theme overhaul: reskin, splash video, music, horns, showroom, progression, controller nav (2026-07-28)`,
      noting its 9-item play-test checklist was still undriven when archived
      (this session's two bugs came out of the user starting it). Bump the
      archive header char/plan counts; update the plan-archive memory. Delete
      the archived material from this file.

## Milestone B — icon isolation (`Garage/PartIconFactory.cs`)

- [x] In `Snapshot()` teardown, the three temporaries are now **deactivated
      immediately** (`SetActive(false)`) before the deferred
      `Object.Destroy(root/camGo/lightGo)`. **Deviation from the planned
      `DestroyImmediate`:** `SetActive(false)` takes effect on the same
      statement, achieves exactly the same isolation, and avoids
      `DestroyImmediate` inside `OnGUI` (the track builder builds its icons
      from a repaint). It also fixes a second symptom the plan hadn't named —
      the leftover **icon lights** accumulated too, so later icons were
      progressively over-lit.
- [x] `TrackIconFactory` needed no change: its temp colliders live on the
      snapshot root, which is now deactivated before the frame ends, so they
      can't touch physics either.
- [x] No cache invalidation needed — both caches are in-memory only
      (`PartIconFactory._cache`, `TrackIconFactory._cache`), rebuilt each run.
- [x] Framing math untouched: bounds are computed per-item, so with the strays
      gone every icon frames exactly its own model (large props included).

## Milestone C — showroom rest height (`Menu/ShowroomRig.cs`)

- [x] Added private static `RestHeight(VehicleDesign d)`: max over `d.wheels`
      of `-(w.localPos.y + SuspensionGeometry.HubOffsetLocal(w.localPos.x,
      w.suspAngleDeg, w.suspLength).y - w.radius)`, falling back to the
      codebase's `DefaultRest = 0.078f` for a wheel-less design. (No such
      helper existed — `-0.078f` was hardcoded in the two bootstraps; this
      reproduces it for the default design and adapts to big-wheel cars.)
- [x] **Deviation from the planned fix:** rather than raising the car, `Show`
      now calls `DropFloor(RestHeight(design))`, which lowers the podium disc
      and gold rim by the rest height. This is the codebase's own convention
      (car origin at 0, ground at −0.078 — `MenuBootstrap.cs:66`,
      `GarageBootstrap.cs:79`), and it leaves the car's transform completely
      untouched, so the camera aim (tuned at car-origin + 0.06), the turntable
      pivot and the rev squat all keep working exactly as before. Raising the
      car instead would have shifted it 78 mm off the framing the camera was
      tuned against. `Build()` seeds `DropFloor(DefaultRest)` so the empty
      podium is already at the right height before the first car lands.
- [x] Leave `MenuBootstrap`/`GarageBootstrap` floors alone.

## Milestone D — verify

- [x] Batch compile: **0 `error CS`**, `Exiting batchmode successfully now!`
      (return code 0).
- [x] No PMV/TPV/Opus reruns — preview-only code paths (no physics, no assets,
      no wire, no save format). Stated in the summary.
- [ ] User eyeball pass: Garage palette icons each show exactly their own
      part; Track Builder tabs (esp. the themed-prop tabs) each icon shows
      exactly its own prop; Showroom cars (TT Coupe + TT Baja for the wheel
      extremes) sit on top of the podium disc, no clipping, squat/rev intact.

---

# Arcade UI/theme overhaul: reskin, splash video, music, horns, showroom, progression, controller nav (2026-07-28)

**Archived with the play-test checklist still UNDRIVEN** — the two preview-rendering bugs fixed in the next pass came out of the user starting it.


## Context

The game plays like an arcade racer but still presents like an engineering tool:
grey/orange IMGUI panels, no title art, no music, silent menus, mouse-only
navigation, and vehicle selection is a `◀ name ▶` cycle row. The user wants the
whole presentation brought up to the TinyTorque brand — the title art
(`E:\EE Projects\AI_3D_Modeling\TinyTorque_RC\renders\TinyTorque_Title.png`:
deep navy, champagne-gold 3D logo, glossy showroom floor) sets the art
direction, and `TinyTorque_Game_Intro_audio.mp4` (4.9 MB, H.264+AAC, composed
soundtrack) is the intro video.

User decisions (AskUserQuestion): **music = hybrid** (drop-in .ogg/.wav/.mp3
files with a procedural chiptune fallback per theme); **unlock pool = cosmetics
+ preset cars** with **TT Coupe as the default unlocked car**, plus a **cheats
menu** with pun codes (`donut` → TT Patrol, user-specified); **progression =
one global local profile** (no per-server scoping — drops the host-GUID work);
**all four extras**: global UI scaling, menu SFX, cursor auto-hide on gamepad,
screen-fade transitions. Suggestion accepted: menu idles into the existing live
attract loop after ~20 s.

Non-negotiables, as every pass: no physics changes; Opus regression
bit-identical; append-only enums/serialized ints; gating lives ONLY in UI
pickers — `VehiclePresets.Resolve`, `VehicleLibrary`, `VehicleFactory`,
`SessionConfig`, `NetSession` stay progression-blind.

## Key facts established while exploring

- **UI is 100% runtime IMGUI**; `Garage/GarageSkin.cs` is the single styling
  authority (palette consts, runtime 4×4 `Solid(Color)` textures, shared
  styles, `Slider01`) — every OnGUI class sets `GUI.skin = GarageSkin.Skin`, so
  a palette/font/texture swap there reskins everything at once. Font is Unity's
  default; `Font.CreateDynamicFontFromOSFont("Bahnschrift", …)` works on this
  Windows-only target with no TTF asset.
- **No gamepad menu navigation exists anywhere** — no focus concept at all.
  IMGUI hard rule (documented at `SettingsPanel.cs:63-67`): control count must
  match between Layout and Repaint; state that changes controls may only mutate
  on Layout (or in `Update()`).
- **Fixed pixel layout, no `GUI.matrix`.** `GUI.matrix` scale auto-transforms
  IMGUI hit-testing, but the custom pixel-space tests must be audited:
  `GarageUI.PointerOverUI` (:1779), `TrackBuilderUI.PointerOverUI` (:1384),
  `SplitScreenHud` camera-pixelRect math, `ArcadeFeedback` view rects. 3D
  picking rays stay in raw screen pixels.
- **No music, no audio files, no VideoPlayer**; `com.unity.modules.video` is
  NOT in `Packages/manifest.json`; no StreamingAssets folder (the installer
  packs `Builds/Release/*` recursively, so one ships automatically). All sound
  is `ProceduralAudio.cs` (loop rules: tonal = whole cycles via `LoopCycles`,
  noise = head↔tail crossfade via `LoopFade`). `SfxPlayer` is deliberately
  scene-local and gates on `timeScale > 0`. The only persistent object today is
  `NetSession` — there is no coroutine host for file loading.
- Race-end hooks: `RaceDirector.PlayerFinished` (place int, local) and
  `NetSession.RaceEnded` + `RaceEndMsg.rows[].place` (fires on host AND
  clients). Persistence is JSON via `SaveSystem` under `AppPaths.BaseDir/Saves`
  (`progress.json` is greenfield). JsonUtility field initializers = the
  back-compat idiom.
- LAN wire: `NetPack.WriteInput` flag byte has bit 8 free; `WriteOwnState` has
  bit 64 free; `CarState` needs one appended flags byte → structural change →
  **protocol v9 → v10**. `vehicleJson` rides whole, so `hornStyle` is free.
- `MenuAttract` (live 3D circuit + 4 bot cars) is the menu background and keeps
  running; `MenuBootstrap`'s showcar-turntable fallback is the proven whole-car
  preview path. `PartPreviewRig` culls to VizLayer only — a whole-car showroom
  camera needs normal layers. `OrbitCamera` exists.
- `VehicleStats.Compute` returns estTopSpeedMs / totalStallTorqueNm /
  totalMass / yawInertia / frontWeightPct / rideFreqHz / steered — enough to
  derive SPEED/ACCEL/HANDLING bars.
- The previous two passes (map props + map ports) are uncommitted in the tree;
  this pass stacks on them.

## Milestone 0 — plan housekeeping

- [x] Splice the finished map-port plan (below the archive marker at the END of
      this file — match it at the END, never the first hit) into
      `Docs/plan-archive.md` as one newest-first entry titled
      `# Rebuild the four themed maps as faithful ports of the Blender maps (2026-07-27)`,
      noting its 8-box play-test checklist was still undriven when archived.
      Bump the archive header counts (483885 chars, 35 plans); update the
      plan-archive memory.
- [x] Delete the archived material from this file.

## Milestone 1 — Arcade skin foundation

- [x] `Garage/GarageSkin.cs`: navy/gold palette — Bg `(0.055,0.075,0.13)`,
      Panel `(0.09,0.12,0.19)`, Btn `(0.13,0.17,0.26)`, BtnHover
      `(0.19,0.24,0.35)`, Accent `(0.94,0.78,0.36)` champagne gold, AccentDim
      `(0.55,0.43,0.18)`, Text `(0.93,0.91,0.86)`. Font →
      `CreateDynamicFontFromOSFont` chain Bahnschrift → Impact → Arial. New
      `Rounded(Color, radius)` 9-slice corner textures for button/box; new
      `Title` style (gold, 26, centered) and `FocusRing` style (rounded gold
      outline, transparent center). The rebuild-if-destroyed guard covers the
      new textures via the same cache.
- [x] **New** `UI/UIScale.cs` (`AIHWSim.UI`): `S = clamp(Screen.height/1080,
      0.6, 2.5)`, `Begin()/End()` GUI.matrix wrap, `W/H` UI-space screen size,
      `GuiPointer()` for the custom rect tests. Wrap every game-facing OnGUI
      (MenuUI, PauseMenu, LanSessionMenu, RaceDirector, ArcadeHud, LanHud,
      LapTimer, SplitScreenHud, GarageUI, TrackBuilderUI, TrackBootstrap exit
      prompt); convert the four pixel-space hit-tests; leave dev overlays
      (Graph/Metrics/Mission/SensorHud) unscaled; leave picking rays unscaled.
- [x] **New** `UI/UiRuntime.cs`: DontDestroyOnLoad GO `TinyTorqueRuntime`
      (lazy `Ensure()`), hosting `ScreenFade`, `MusicDirector` (M3),
      `CursorAutoHide`. NO AudioListener. `ScreenFade.To(mid, out, in)` for
      scene loads (unscaled time, `GUI.depth = -1000`), `Dip(mid)` 0.12 s for
      page changes. `CursorAutoHide`: pad input hides cursor, mouse movement
      shows it; exposes `PadIsLastInput`.
- [x] **New** `UI/MenuNav.cs` — the IMGUI gamepad focus system. Input polled
      once per frame (d-pad/left-stick with 0.4 s/0.12 s repeat, South =
      submit, East = back, via `PadTable` + new `InputReader.LeftStick()`
      any-pad helpers, which landed here instead of M7 because MenuNav needs
      them). Focus movement applies at the top of the frame's first Layout
      pass; wrappers add zero controls; disabled controls are skipped; back is
      consumed from host `Update()`; suppressed while
      `SettingsPanel.Capturing`; ring drawn Repaint-only, pad-input only.
      **Deviation from the planned design:** activation does NOT use
      `GUIUtility.ExitGUI()` — its runtime abort semantics for the paired
      Repaint are undocumented, and a layout-cache mismatch there takes the
      whole menu down. Instead activation is consumed on a Layout pass and
      every host dispatches its control flow off a **Layout-snapshotted** copy
      of its page/tab state (`_pageDraw` in MenuUI): a mid-Layout page switch
      draws the old page for the rest of that frame — Layout and Repaint always
      agree — and the new page owns the next frame, the same timing a mouse
      click has always had. One nav owner per frame (first `BeginFrame`
      claims it); non-owners' wrappers degrade to plain mouse controls.
      Proof wiring: all of MenuUI's `MenuButton`s route through
      `MenuNav.MenuButton`, so every page's plain buttons are already
      pad-navigable; remaining control shapes convert in M2.
- [x] `Audio/ProceduralAudio.cs`: append `UiMove` (30 ms 900 Hz tick),
      `UiSelect` (660→990 blip), `UiBack` (700→450 fall), `UiDeny` (220 Hz
      double-buzz), `UiUnlock` (0.9 s rising arpeggio + sparkle fanfare),
      `UiLevelUp` (0.6 s triad swell). `Audio/SfxPlayer.cs`: add
      `PlayUi(key, vol, pitch)` — `Play2D` without the timescale gate (pause
      menu must click). MenuNav wrappers fire move/select/back.

## Milestone 2 — Splash video, title card, menu shell

- [x] `Packages/manifest.json`: add `"com.unity.modules.video": "1.0.0"`.
      Create `Assets/StreamingAssets/` with `TinyTorque_Intro.mp4` (copy of the
      renders mp4) and `Music/README.txt` (naming convention). Copy
      `TinyTorque_Title.png` → `Assets/Resources/UI/TinyTorque_Title.png`.
- [x] **New** `Menu/SplashSequence.cs`: states Video → Title → Done;
      `static bool ShownThisBoot` so returning from a race skips it.
      VideoPlayer built from code: `VideoSource.Url` from streamingAssetsPath,
      `RenderTexture` render mode drawn letterboxed via IMGUI,
      `audioOutputMode = AudioSource` through a 2D source so master volume
      applies; `errorReceived`/`loopPointReached` → advance (codec failure
      still boots). Title state: title PNG aspect-cover + pulsing gold
      `PRESS ANY BUTTON`. Any key/pad/mouse skips video → title → menu.
      `MenuBootstrap.Awake` creates MenuUI via the splash completion callback
      on first boot, immediately thereafter; `Finish()` starts menu music.
- [x] `Menu/MenuUI.cs`: retitle to `TINYTORQUE` / gold `RC SERIES`; Root page
      draws the title texture as a dimmed backdrop strip over the live attract.
      Convert ALL pages to MenuNav wrappers (MenuButton → Button, CyclePicker →
      Cycle, options toggles/sliders); page changes through `ScreenFade.Dip`,
      scene loads through `ScreenFade.To`. Idle 20 s on Root → hide the panel
      (small "press any button" bug), any input restores. Fresh-install default
      `lastVehicle = "★ TT Coupe"` (`GameSettings` initializer; old files keep
      their value).
- [x] Reskin + nav: `PauseMenu`, `SettingsPanel` (wrappers; rebind capture
      untouched), `LanSessionMenu`, `RaceDirector.DrawResults` (pad-navigable
      Keep driving / Rematch / Main Menu — needs BeginFrame/EndFrame too).
      All four dispatch off Layout-snapshotted flags per the M1 pattern; the
      Root-page menu sits low over the full title key art (the panel keeps the
      text wordmark on every other page); Cycle/Stepper/Slider01/Toggle
      wrappers cover the cycle pickers, ± steppers, options sliders, preset
      rows and rebind buttons across MenuUI/PauseMenu/SettingsPanel/
      LanSessionMenu.

## Milestone 3 — Music (files + procedural fallback)

- [x] `Persistence/GameSettings.cs`: `public float musicVolume = 0.7f;`.
      Slider in MenuUI Options and `SettingsPanel.Draw` audio block (separate
      code paths — both).
- [x] **New** `Audio/MusicDirector.cs` on `TinyTorqueRuntime`: two 2D sources,
      1.5 s crossfade, volume = `musicVolume × fadeWeight` (master rides
      `AudioListener.volume` — never multiplied in). Theme keys `menu, generic,
      downtown, toyroom, enchanted, haunted, results`. Resolution per key:
      `AppPaths.BaseDir/Music/<key>.(ogg|wav|mp3)` →
      `streamingAssetsPath/Music/<key>.*` → `ProceduralMusic.Get(key)`; loader
      = `UnityWebRequestMultimedia.GetAudioClip` coroutine (the runtime GO is
      the missing host); writes a `Music/README.txt` into BaseDir on first run.
      Scene hook via `SceneManager.sceneLoaded`: Menu/Garage/Builder → `menu`,
      Track → `ThemeFor(GameFlow.ActiveTrack)` mapping `TrackDesign.ambience`
      (`downtown/toyroom/enchanted/haunted`, else `generic`). Race hooks:
      countdown ducks to 0.4, GO restores, results → `results` theme (local via
      RaceDirector, LAN via `RaceEnded`); pause ducks ×0.5. AudioSources ignore
      timeScale, so music plays through pause by nature.
- [x] **New** `Audio/ProceduralMusic.cs`: deterministic chiptune renderer
      (event-additive: notes render into one shared buffer and tails WRAP
      modulo the loop length, which is what makes the loop seamless without
      cycle-counting every voice; needed two new manifest modules,
      `unitywebrequest` + `unitywebrequestaudio`, for the file loader)
      (pattern sequencer → one seamless 16-bar mono buffer; pulse lead,
      triangle bass, square pad, noise percussion; fixed-seed LCG like
      ProceduralAudio). Themes: `menu` "Showroom Shine" 92 BPM D-major glossy
      arps; `downtown` 122 BPM A-minor synthwave, driving 8th bass; `toyroom`
      132 BPM C-major music-box romp; `enchanted` 100 BPM 3/4 F-Lydian waltz,
      bell pad; `haunted` 96 BPM D-harmonic-minor ostinato + theremin-ish lead;
      `generic` 128 BPM G-Mixolydian garage-rock vamp; `results` 8-bar C-major
      victory fanfare. Lazy build (~30 s ≈ 5 MB mono float); keep ≤ 2 themes
      cached, evict on switch.

## Milestone 4 — Horns (5 styles, bindable, per-vehicle, LAN v10)

- [x] `ProceduralAudio.cs` loopable horn clips: `HornNormal` 420+522 Hz
      dual-tone; `HornSiren` two-tone wail (whole modulation cycles per loop);
      `HornTruck` 110/220/330 Hz air horn + breath noise; `HornMusical` 5-note
      original fanfare loop; `HornClown` honk-a-honk squeeze-bulb cycle.
- [x] `Garage/VehicleDesign.cs`: `public int hornStyle = 0;`
      (normal/siren/truck/musical/clown). Presets: TT Patrol = siren,
      TT Baja = truck.
- [x] Input chain (every layer, no silent stale path): `KeyBindings` —
      `DriveAction.Horn = 11`, `horn = KeyCode.H`, `padHorn =
      PadButton.LeftStickPress` (L3; lookBack already owns R3), all switches +
      `PadActions` + `ApplyLayout`; `InputReader.HornHeld()` (+ key/pad
      variants — Held, hold-to-sound); `IDriverInputSource.HornHeld()` in
      `PlayerInputSource` + all implementers (`BotDriver` false,
      `NetworkInputSource` latched, gated source gates it); `CarInput` pushes
      `hornHeld` into the car's `VehicleAudio` and exposes `HornHeldNow`;
      `SettingsPanel.KeyActions` gains Horn (pad list follows PadActions).
- [x] `Audio/VehicleAudio.cs`: `hornStyle`, `hornHeld`, `externalHorn` fields;
      lazy third `MakeLoop` voice, volume-gated like skid, gain bucket =
      `SfxGain` (not engineVolume). Attach sites set style: `TrackBootstrap`
      :328/:774, `ClientCarView` from its design. GarageUI BODY tab: horn cycle
      row + test button.
- [x] LAN v10: `InputState.hornHeld` (input flag bit 8), `OwnStateMsg.hornOn`
      (flag bit 64), `CarState` + appended flags byte (bit 1 horn) —
      writers/readers all in `NetPack`; confirm the `16 + n*80` buffer
      headroom. `OwnStateSender`/`ClientInputSender` set it; `BroadcastState`
      relays; host pushes remote `hornOn` → that rig's
      `VehicleAudio.externalHorn`; `ClientCarView` sets `externalHorn`
      (latest-value, no lerp). `ProtocolVersion = 10` + history paragraph.

## Milestone 5 — Showroom (vehicle select)

- [x] **New** `Menu/ShowroomRig.cs`: podium parked at `(0, -600, 0)` (the
      PartPreviewRig trick) — glossy navy floor disc, gold rim ring, key/fill/
      rim lights, own on-demand camera (depth 10, normal layers — NOT the
      VizLayer-only mask) over the still-running attract.
      `SetDesign` → `VehicleFactory.Build(..., previewKinematic: true)`;
      turntable 10°/s + `spinVelocity` injected from right-stick X / LMB drag,
      exponential decay.
- [x] **New** `Menu/ShowroomUI.cs` + `Page.Showroom`: left = vehicle list
      (presets + library, 🔒 badges on locked); right = stats + customization;
      bottom Select/Back. Entry: SP/LanHost/LanJoin vehicle row becomes
      `Vehicle: <name> [Showroom ▶]`; split-screen rows keep compact (filtered)
      cycle pickers. Rev: hold RT/W → 2D engine loop pitch-ramp 0.4→1.8 +
      cosmetic body squat. Horn preview button. Locked car: stats visible,
      Select disabled, "Win races to unlock (or know the magic word…)".
- [x] Stats bars from `VehicleStats.Compute`: SPEED = `clamp01(estTopSpeedMs /
      18)`; ACCEL = `clamp01(((stallNm / rw) / mass) / 30)` (rw = mean powered
      wheel radius, fallback 0.033); HANDLING = `0.35·agility + 0.25·balance +
      0.20·response + 0.20·steerAuthority` where agility =
      `clamp01(0.030·mass/yawInertia)`, balance = `1 − |frontPct−50|/35`,
      response = `clamp01((rideFreqHz−1.5)/3.5)`, steerAuthority =
      `clamp01(steered/2)`; calibrate constants so the five presets order
      sensibly. SPECIAL row = flavor placeholder ("Siren's Call" Patrol, "Big
      Air" Baja, "Precision Ghost" Opus — no gameplay yet).
- [x] Cosmetic loadouts: `VehicleLoadout { vehicleName, hornStyle=-1,
      wheelStyle=-1, paintIdx=-1, topper=0, aeroKit=0 }` persisted in
      `PlayerProgress.loadouts` (ProgressStore skeleton lands here, M6 extends
      it). Applied ONLY by `Progression.ApplyLoadout(design, name)` at UI call
      sites (StartSinglePlayer / MakeSlot / LAN connect / showroom preview) —
      never inside `VehiclePresets.Resolve` or `VehicleLibrary.Load`. Topper
      slots = none / light-bar / pods / whip / twin-flags; aero kits = none /
      street (splitter + 4° wing) / track (splitter + canards + 10° wing) —
      kits genuinely change stats, labeled "affects handling". Paint choice
      clears `liveryPng` only on explicit selection (−1 = don't touch).

## Milestone 6 — Progression, mystery items, cheats

- [x] **New** `Persistence/ProgressStore.cs`: `PlayerProgress { version,
      unlocked, redeemedCodes, xp, level, wins, racesFinished, loadouts }` in
      `Saves/progress.json`; static `Progression` façade — `IsUnlocked`
      (unknown ids = true), `TryRollMystery`, `AddXp` (level n→n+1 costs
      100·n), `OnWin`, `LastAward` for the results overlay. One global profile,
      shared by split-screen — documented.
- [x] **New** `Persistence/UnlockCatalog.cs` — 20 locked items with pun codes:
      `car_patrol`/`donut` (user-specified), `car_baja`/`bajablast`,
      `car_realtwin`/`twinning`, `car_opus`/`magnumopus`,
      `horn_siren`/`pullover`, `horn_truck`/`convoy`, `horn_musical`/`freebird`,
      `horn_clown`/`clowncar`, `wheel_style_6`/`hubcapital` chrome,
      `wheel_style_7`/`rollmodel` gold, `wheel_style_8`/`glowgetter` neon,
      `antenna_style_2`/`flagship`, `antenna_style_3`/`doubletrouble`,
      `light_style_0`/`lightsout`, `light_style_1`/`podrace`,
      `aero_kit_street`/`groundeffect`, `aero_kit_track`/`downforce`,
      `paint_gold`/`midastouch`, `paint_midnight`/`navyseal`,
      `paint_hotpink`/`flamingo`. Always free: stock design, TT Coupe, user
      Vehicles/*.json, wheel styles 0–5 (0–2 generic + 3–5 show-car wheels,
      which already exist), antenna 0–1, standard palette. Bots keep drawing
      locked cars (a tease; `MakeBotSlot` untouched). Wheel styles 6–8 are
      **appended** cosmetic variants — existing meshes with chrome/gold/
      emissive-neon material tints in `PartVisualFactory.WheelStyleKey`/
      `BuildWheelViz` (append-only int, old saves untouched).
- [x] Award hooks: local — alongside the `PlayerFinished` wiring in
      `TrackBootstrap`, `!isBot && place == 1 && Players.Count > 1` →
      `Progression.OnWin()` (mystery roll until pool empty, then +100 XP; small
      grants for podium/finish). LAN — `RaceEnded` on host and clients: local
      slot's row `place == 1` → same. Results overlays (RaceDirector + LAN)
      draw the MYSTERY ITEM reveal: 1.2 s name-cycling (Repaint-only text, no
      layout change), `UiUnlock` fanfare; XP case shows `+XP` fill and LEVEL UP
      with `UiLevelUp`.
- [x] Picker filtering (UI-only): `MenuUI.RefreshLists` filters locked presets;
      clamp `_vehicleIdx`; locked `lastVehicle` falls back to index 0. Showroom
      shows locked greyed. Garage stays fully ungated (engineer sandbox).
      Enforcement check at review: grep `Progression.` — allowed surfaces are
      MenuUI, ShowroomUI, the results overlays (AwardReveal), the cheats page,
      TrackBootstrap's two award hooks, and NetSession's two Hello/roster
      sites, which read `Current.level` for the DISPLAY badge only — gating
      still never touches resolve/build/net behaviour.
- [x] Roster levels: `HelloMsg`/`RosterEntry` + `public int level = 1;` (rides
      the v10 bump); `Lv N` beside names in the LAN session roster + results
      (LanSessionMenu — the panels that show names; LanHud's race banner left
      unbadged to keep it glanceable).
- [x] `Page.Cheats` from Options ("EXTRAS · Cheat Codes"): text field + Redeem
      (Enter submits); normalize trim/lower/despace; hit → unlock + `UiUnlock`
      + flourish; already-had → notice; miss → `UiDeny` + decaying panel shake
      (Repaint-only offset).

## Milestone 7 — Controller support in the editors

- [x] `InputReader`: stick/trigger helpers (`LeftStick()`, `RightStick()`,
      `TriggerAxis()`) so editors never touch `Gamepad.current` directly.
      (Landed in M1 — MenuNav needed them first.)
- [x] Garage (`GarageUI.Update` → new `UpdatePadInput()`, selected part, no
      active mouse drag): left stick = camera-relative X/Z move (0.15 m/s ×
      stick, unscaled dt); LT/RT = localPos.y down/up; LB/RB = yaw ∓ 90°/s;
      right-stick Y = pitch (`aimEuler.x` sensors / `tiltDeg` antennas /
      `angleDeg` wings — the fields the inspector edits); A = cycle-select
      part, B = deselect, X = mirror, Y = focus. Undo: `PushUndo("pad<n>")` on
      the first edited frame of a burst, key advanced on release (one undo
      step per hold). **Deviations:** mirror twins stay linked via an explicit
      `FindTwin(mirrorGroup)` sync (position x/yaw mirrored, height/pitch/tilt
      copied) rather than routing through the mouse-drag commit path — the
      drag machine is pointer-shaped end to end; and the editors' PANELS stay
      mouse/keyboard (no MenuNav conversion inside the two editors) — the pad
      layer is the hands-on-the-car/track half, which is what the user's
      binding spec described.
- [x] Builder (`TrackBuilderUI`): left stick = move selected item
      (`RepositionItem`, no rebuild); LT/RT = scale ∓ (same clamped value +
      "scale" undo tag as the slider, so pad and slider coalesce identically —
      items auto-drop, so triggers map to scale here; deliberate deviation
      from the garage's up/down, documented in the selection panel's new pad
      hint line); LB/RB = yaw; right stick = camera orbit via the new
      `OrbitCamera.PadOrbit(Vector2)` (shared class — the garage camera gets
      it for free); A = select next item, B = deselect. Panel nav stays
      mouse/keyboard, same deviation as the garage. Bindings fixed (not
      rebindable) this pass.

## Milestone 8 — Verification + docs

- [x] **V1 compile**: five incremental batch compiles across the milestones,
      all ending 0 `error CS` (one real failure caught and fixed on the way:
      `UnityWebRequestMultimedia` needed the `unitywebrequest` +
      `unitywebrequestaudio` manifest modules; plus one CS0136 shadowing slip
      in ShowroomUI).
- [x] **V2/V3**: `[PMV] RESULT ALL PASS (110 assets)` and
      `[TPV] RESULT ALL PASS (8 presets)`.
- [x] **V4 Opus regression**: **bit-identical** — the result JSON diffs empty
      against the map-port pass's run (itself identical to R6/R4): legA
      −13.615608215332032 mm, turn +0.1873779296875°, legB
      +15.803813934326172 mm, brake +42.34135055541992 mm, total
      +58.14552307128906 mm, drift −42.4041748046875 mm, completed true,
      fault 0, phase 10.
- [x] **V5 release build**: `Build Finished, Result: Success.`,
      `Assembly-CSharp.dll` stamped Jul 28 03:05,
      `_Data/StreamingAssets/` carries `TinyTorque_Intro.mp4` +
      `Music/README.txt`, `AIHWSim/SkyGradient` still serialized into the
      build; installer needs no edit (recursive pack).
- [x] README: menu/UI overhaul + Showroom + progression sections, horns +
      music in §Sound, the horn row + pad tables under Controls, protocol
      v9 → v10 in both places with a v10 history paragraph.

## Play-test checklist (user)

- [ ] Boot the release build: Unity logo → intro video (any input skips) →
      title card → menu with music. Second trip to the menu skips the splash.
- [ ] Pad-only session: navigate every page (root → race setup → Options →
      Cheats → Showroom → pause → results → LAN lobby incl. a pad rebind)
      with the gold focus ring, A/B, and left/right on sliders/pickers —
      watching the console for any IMGUI layout-mismatch exceptions, which
      are the MenuNav failure signature. Then the same screens mouse-only.
- [ ] Window sizes: 720p windowed, 1080p, and (if available) 1440p/4K — panel
      sizes, editor panel-edge clicks (PointerOverUI), split-screen HUD boxes.
- [ ] Music: menu theme, one themed map each (four different songs), generic
      on a race circuit, results sting, countdown duck, pause duck, the Music
      slider live — then drop an .mp3 named `downtown` into the save-folder
      Music directory and hear it override.
- [ ] Horns: all five from the garage test button; hold-to-sound in a drive;
      TT Patrol sirens by default. LAN ×2+: remote horns audible from the
      right car with the right voice; a v9 build vs v10 host is refused.
- [ ] Progression: win vs bots → mystery reveal; `donut` unlocks TT Patrol
      (and a wrong word buzzes + shakes); locked cars show padlocked in the
      Showroom and absent from the quick pickers; a Showroom loadout (paint +
      horn + wheels + aero) survives restart and shows in-race; split-screen
      P2 and a LAN client win both credit the local profile.
- [ ] Showroom feel: spin (right stick + RMB drag), rev, honk; stats bars
      move when the aero kit changes.
- [ ] Editors on pad: move/rotate/raise a garage part (mirror twin follows),
      scale a builder prop with triggers, orbit with right stick, undo bursts
      are single steps, interleaved mouse+pad editing stays consistent.
- [ ] Old saves: existing user vehicles/maps load; an old settings.json keeps
      its bindings and picks.

## Known risks

1. **MenuNav is the highest-risk piece** — IMGUI has no focus concept.
   Mitigations are structural: focus moves on Layout only, activation +
   `GUIUtility.ExitGUI()`, back in `Update()`, wrappers add zero controls,
   explicit-rect focus ring. Built first, soaked on the Root page.
2. **GUI.matrix hit-tests**: the two `PointerOverUI`s and `SplitScreenHud` are
   the three that will actually bite; picking rays must stay unscaled.
3. **First VideoPlayer use**: Windows H.264 via WMF is reliable; the
   `errorReceived → skip` path keeps a codec-less machine booting.
4. **Audio ownership**: one AudioListener (scene cameras); the runtime GO
   carries sources only; SfxPlayer stays scene-local; music volume never
   multiplies master.
5. **CarState grows a byte** — single choke point in `NetPack`, but every
   machine needs the rebuilt standalone (v10 refuses v9, as designed).
6. **Unlock gating leak-proofing** — `Progression.` referenced only from the
   four allowed UI surfaces; everything below the menu layer stays
   progression-blind so headless regression and LAN internals cannot change.
7. Chiptune quality ceiling is "good chiptune" — drop-in files override it with
   zero code, which is the point of hybrid.

---

# Rebuild the four themed maps as faithful ports of the Blender maps (2026-07-27)

**Archived with the play-test checklist still UNDRIVEN.**

## Context

The last pass imported 63 props from the four TinyTorque Blender **prop kits**
and hand-authored four compact circuits (44–48 tiles ≈ 44×48 m, ~30 items
each) that use them. But the source project also ships four fully laid-out
**preview maps** — `TinyTorque_map.blend`, `_toy_map`, `_ench_map`,
`_haunt_map`, built by `scripts/tt_11_map.py`, `tt_16_toy_map.py`,
`tt_17_ench_map.py`, `tt_18_haunt_map.py`, rendered to
`renders/map*/…_{plan,aerial,street}.png`. Those renders are what the maps are
supposed to look like, and the current in-game circuits are not them: a
handful of props on a small loop, under one flat daylight directional light, on
a blue background.

The request: make the in-game maps look **exactly** as the renders lay them
out; add the environmental features / shading / lighting that give each map its
ambient feel; scale the default map size up to fit; and make items rescalable
in the Track Builder.

The Blender maps are ~750–1000 units at 1 unit = 1 m, with 353–830 placements
each over ~17 prop meshes. The props are already imported at **0.1** (1
authored metre = 0.1 game metre), so a faithful port is the same layout
divided by 10 — 76–124 game metres per side, 300–700 items per map. Each
theme module also carries its own sky gradient, ground palette, haze density,
key sun and glow points (`tt_15_mapkit.py`: `sky`, `mat_ground`, `haze`,
`key_sun`, `wash`, `glow_point`), which is where most of the look actually
lives — the enchanted vale is "a dim moon-blue sun and every warm note in
frame is a lit window", the hollow runs "twice the haze density of the other
maps", the attic is an interior with 13 m wallpapered walls.

User decisions: **full districts** (every district ported, scatter counts as
authored), **faithful long laps** (the circuit runs the roads exactly where the
renders draw them — 200–300 m laps, 2–3× today's), **full ambience** (sky
dome, fog, themed lights, glow points, themed ground, plus the toy room's
actual walls/skirting/dado rail).

Non-negotiables, same as the last two passes: no physics/vehicle changes;
Opus regression bit-identical to R4 (Opus Proving Ground untouched); floor
indices and ItemDef ids append-only (old saved user maps must keep loading);
the three dedicated race circuits (Boost Speedway, Dust Devil Rally, Neon
Vortex) untouched.

## Key facts established while exploring

- `TrackDesign.tileSize` (default 1 m) is honoured everywhere that matters —
  `TileCenter`, `WorldToTile`, `SurfaceMap.Lookup`, the floor slab. Setting it
  to **2.0** is how a 112 m map fits in 56 tiles instead of 112, which keeps
  tile counts at 1.8k–3.1k (today's 44×44 = 1.9k). `Resize` clamps 4..60 and
  must go to 4..80.
- Roads in the source maps are flat preview ribbons at z≈0.04 in a deletable
  `ROADS` collection. **Only the racing line becomes a spline**; every other
  road is painted floor tiles. This matters because `BotPath.Build`
  (Core/BotPath.cs:50) picks the spline with the **most control points**, not
  the longest — a decorative second spline would silently steal the bot line.
  Every themed preset therefore ships exactly one spline.
- Road widths already match at 1:10: arcade `ROAD_W` 22 → 2.2 m, toy 30 → 3.0,
  ench 20 → 2.0, haunt 18 → 1.8. Inside the builder's existing 0.5–3 m width
  slider.
- `PlacedItem` has no scale field, but the Blender layouts use per-placement
  `scale=rng.uniform(...)` throughout — so per-item scale is a *prerequisite*
  for a faithful port, not just a builder feature.
- `toy_domino`, `toy_brick`, `haunt_pumpkin`, `dt_cone` are dynamic ItemDefs.
  The layouts place ~250 of them as decorative fill (83-piece domino kerb ring,
  150-piece floor scatter, 61 pumpkins). 250 Rigidbodies is a physics problem,
  hence a per-item **pinned** flag.
- Cost is dominated by `StaticBatchingUtility.Combine`, not by `Instantiate`
  (600 prefab instantiates ≈ 25 ms). Expected map build 0.3–0.8 s.
- The builder rebuilds the whole preview on every item edit
  (`TrackBuilderBootstrap.RebuildAll`). Rotate and scale are pure transform
  changes — they must not trigger a rebuild once maps are this big.
- `Orbit.maxDistance = 60f` (TrackBuilderBootstrap.cs:76) hard-clamps
  `FrameMap`, so a 124 m map cannot be framed today.
- Built-in Render Pipeline, everything on `Shader.Find("Standard")` (fog-aware).
  LAN `MaxPayloadSize` is 256 KB against a ~70 KB 600-item trackJson.

## Milestone 0 — plan housekeeping

- [x] Splice the finished map-pack plan into `Docs/plan-archive.md` as ONE new
      newest-first entry titled
      `# Import TinyTorque map prop packs + four themed circuits (2026-07-27)`,
      with a bold note that its play-test checklist was still undriven when
      archived. Archive header updated to 34 plans / 466459 chars.
      (Trap hit and repaired: the splice matched the archive marker where
      Milestone 0 merely *mentions* it, swallowing the active plan into the
      entry — match the marker at the END of the file, not the first hit.)
- [x] Delete the archived material from this file.

## Milestone 1 — Per-item scale + pinning + bigger maps

Data and builder work that the ports depend on.

- [x] `TrackEd/TrackDesign.cs`: `PlacedItem` gains `public float scale = 1f;`
      and `public bool pinned;` (pinned = "scenery, never gets a Rigidbody").
      New `TrackDesign.EnsureItems()` repairs old JSON (JsonUtility gives a
      missing float 0, so `scale <= 0 → 1`); call it everywhere `EnsureFloor`
      is called, and unconditionally inside `TrackFactory.Build`. `ambience`
      string field (Milestone 2) lands here too. `Resize` clamp 4..60 → 4..80.
- [x] `TrackEd/TrackFactory.cs`: apply `it.scale` to the item root
      (`go.transform.localScale`) — hulls, mesh and lamp offset all inherit it.
      Skip the Rigidbody block when `it.pinned`; scale `def.dynamicMass` by
      `scale³` so a 2× brick is not feather-light; scale the `ItemBehavior.Light`
      range/intensity.
- [x] `TrackEd/TrackGhost.cs`: `Scale` property applied in `SetPose`.
- [x] `TrackEd/TrackBuilderUI.cs`:
      - SELECTION panel: `Scale ×N` readout, `−`/`+` (×1.15 steps), a
        0.2–5 slider reusing `SliderRow` (:1166), and a `Pinned` toggle shown
        only for `def.dynamic` items.
      - Shift+scroll scales the ghost while placing (plain scroll still rotates).
      - MAP panel: `±5` resize buttons beside the existing `±1`, and a tile-size
        `◀ 2.0 m ▶` control (1.0 / 1.5 / 2.0 / 3.0). Changing tile size re-lays
        the grid and moves the map edge under fixed item positions — show a
        status warning naming how many items fall outside.
      - `SnapPose` (:585) subdivides each tile into `k = max(1, round(tileSize))`
        cells so a 2 m map still snaps placement at 1 m. `k == 1` reproduces
        today's tile-centre snapping exactly.
- [x] `TrackEd/TrackBuilderBootstrap.cs`: `RepositionItem(int index)` — find the
      item root by `PlacedItemMarker` and re-apply pose+scale without rebuilding;
      rotate/scale use it. `Orbit.maxDistance` derived from the design span
      (`Mathf.Max(60f, span * 1.2f)`), refreshed in `SetDesign`/`FrameMap`.

## Milestone 2 — Map ambience (sky, fog, lights, room)

- [x] **New** `TrackEd/MapAmbience.cs` — `AmbienceDef` (sky top/horizon/ground
      colours + optional horizon wedge and its compass yaw, ambient colour, fog
      colour + density, key-light colour/intensity/euler, surround-ground
      colour, a glow-point list, and an `extras` hook) plus five defs keyed
      `""`/`downtown`/`toyroom`/`enchanted`/`haunted`. Values ported from the
      Blender modules: e.g. arcade dusk ramp `(0.050,0.038,0.040)` →
      `(0.140,0.078,0.056)` → `(0.026,0.048,0.105)` with the warm south wedge;
      ench aurora wedge `(0.130,0.320,0.340)`; haunt near-black with a moon.
      **Fog densities are the Blender haze densities × 10** (1 game m = 10
      Blender m): arcade 0.0030, toy 0.0022, ench 0.0042, haunt 0.0068.
      Key lights from each module's `key_sun`: arcade sun 3.4 W warm
      `(1.0,0.80,0.60)`; ench moon 2.1 `(0.62,0.76,1.00)`; haunt moon 2.2
      `(0.58,0.74,1.00)`; toy window 2.6 `(1.0,0.86,0.66)` raking low.
      Glow points from `glow_point`: crater, castle keep, village green,
      mansion hall, crypt mouth, chapel, attic standard lamp.
- [x] **New** `Assets/Resources/SkyGradient.shader` — unlit, `Cull Front`,
      `ZWrite Off`, `Fog { Mode Off }`, three-stop vertical gradient plus a
      horizon wedge. Lives under `Resources/` so it is guaranteed into the
      build (the vehicle pass's shader-stripping trap). `MapAmbience` builds an
      inverted 400 m sphere with it under `built.root`; if the shader fails to
      load it silently falls back to today's behaviour (flat camera background).
- [x] `MapAmbience.Apply(design, root)` also sets `RenderSettings.fog`
      (`FogMode.Exponential`), `fogColor`, `fogDensity`, `ambientLight`, and
      creates/retunes the scene's directional key light and the glow points.
      Called from `TrackFactory.Build` so the builder preview and the drive
      scene get identical atmosphere — the same "what you build is what you
      drive" contract the factory already keeps.
- [x] `extras` for `toyroom` builds the room shell: north wall at z = +43 and
      west wall at x = −47, 13 m tall, striped wallpaper via
      `TrackBuilder.StripeTexture`, skirting + dado-rail boxes, real box
      colliders. This is what stops the floor reading as tarmac.
- [x] `TrackFactory.BuildSurround` takes the ambience ground colour and grows
      to 400 m.
- [x] `Core/TrackBootstrap.cs` (`BuildLighting`, the three camera builders),
      `TrackEd/TrackBuilderBootstrap.cs` (`BuildLighting`, `BuildCamera`) and
      `Menu/MenuAttract.cs` (`BuildCamera`) defer to the ambience: camera
      background = horizon colour, far clip ≥ 900 m to clear the dome.

## Milestone 3 — Four faithful map ports

- [x] **New** `TrackEd/MapLayout.cs` — the Blender placement helpers ported to
      C#, so the presets read like the source modules: `Along(line, spacing,
      offset, start)`, `SegDist`, `RoadDist`, `Scatter(...)` over a seeded
      `System.Random`, `PaintLine(design, pts, widthTiles, type)`,
      `PaintEllipse`, plus `Row`/`Ring` sugar. Deterministic per seed so TPV
      and the LAN JSON are stable build-to-build.
- [x] `TrackEd/TrackPresets.cs`: the four themed builders rewritten as 1:10
      ports (names unchanged — `MenuAttract` and the `lastTrack` setting
      reference them). Grid sizes, all at `tileSize = 2`:

      | Preset | Source | Grid | World | Districts ported |
      | --- | --- | --- | --- | --- |
      | ★ Downtown Dash | `tt_11_map` | 38×47 | 76×94 m | downtown block grid, industrial strip, stunt park, badlands + volcano |
      | ★ Playroom Raceway | `tt_16_toy_map` | 48×44 | 96×88 m | furniture skyline, bed, dining, toybox yard, rug circuit, floor scatter |
      | ★ Enchanted Ascent | `tt_17_ench_map` | 56×56 | 112×112 m | castle plateau, village ring, formal gardens, enchanted wood, tourney ground, peaks |
      | ★ Graveyard Shift | `tt_18_haunt_map` | 54×52 | 108×104 m | mansion rise, 4-block cemetery, chapel ruin, barrow field, pumpkin patch, dead wood, spirits |

      Ench and haunt shift ~10 m in Z (and ench's two backdrop peaks pull in
      from z 69–76 to ≈60) so each map stays centred on the origin inside the
      grid.
- [x] Per map, in order: paint the district ground (asphalt city / sand
      badlands; wood floor + carpet rug ellipse; grass vale + dirt causeway;
      dirt hollow + mud), paint the road network as floor tiles with
      `PaintLine`, then add **one** closed racing spline following the source
      roads — arcade: main avenue south + the badlands loop; toy: the rug's
      printed oval; ench: causeway → garden lane → village ring, closed with
      one added leg; haunt: drive → cemetery link → cemetery spine → barrow
      road, which already closes on the existing roads.
- [x] Then the district functions, mirroring the source module function for
      function, carrying the authored `rot_z` and `scale`. Decorative fills of
      dynamic props (domino kerb ring, floor bricks, pumpkin rows) ship
      `pinned = true`; ~15–25 live dynamic props per map stay near the racing
      line so they still scatter when hit.
- [x] Finish / spawn / 3–5 checkpoints and `BoxRow`s spread along the longer
      lap. Landmark hulls stay ≥ 3 m clear of the ribbon (TPV cannot see
      overlap). Budget ≤ 700 items per map, reported by TPV.
- [x] Each builder gets `d.ambience = "<key>"`.

## Milestone 4 — Load cost, validation, protocol

- [x] `TrackEd/TrackFactory.BuildFloor` splits: the drive scene
      (`interactive: true`) builds ONE merged mesh per floor type instead of
      3.1k tile GameObjects; the builder keeps per-tile renderers because
      `RepaintTile` needs them. `Track/TrackBuilder.cs` gains a shared cached
      cube mesh so tiles stop going through `CreatePrimitive` + `Destroy`.
- [x] `Editor/TrackPresetValidator.cs`: report item count, renderer count and
      world extent per preset; new failures for **more than one spline** on a
      themed preset, for the max-`Count` spline not being the closed one (the
      BotPath trap), for items outside the map (already), and for
      `scale <= 0`. Existing grade/overpass checks unchanged.
- [x] `Net/NetSession.cs`: `ProtocolVersion` 8 → 9 with a history paragraph —
      `PlacedItem.scale`/`pinned` and `TrackDesign.ambience`/`tileSize` all
      ride in the full trackJson, and a v8 peer would build a v9 map at scale
      1, un-pinned (250 stray Rigidbodies) and with no atmosphere.

## Milestone 5 — Verification + docs

- [x] **V1 compile**: batch `-batchmode -nographics -quit`, 0 `error CS`
      (PowerShell does not wait — poll or `-Wait`; build the argument list as
      ONE quoted string or the project path splits at its space).
- [x] **V2 `PartModelValidator.Report`**: `[PMV] RESULT ALL PASS` (110 assets,
      unchanged — no new FBXs this pass).
- [x] **V3 `TrackPresetValidator.Report`**: `[TPV] RESULT ALL PASS` (8
      presets), with the new item/extent lines recorded for the README.
- [x] **V4 Opus regression**: `-batchmode`, NO `-nographics`, NO `-quit`, poll
      the JSON — bit-identical to R4 (legA −13.615608215332032 mm, turn
      +0.1873779296875°, legB +15.803813934326172 mm, brake +42.34135055541992
      mm, total +58.14552307128906 mm, drift −42.4041748046875 mm, completed
      true, fault 0, phase 10).
- [x] **V5 release build** via `BuildMenu.BuildRelease` (forced by v9); verify
      the `Assembly-CSharp.dll` timestamp.
- [x] `README.md`: rewrite the map-pack section — the four maps as ports of the
      Blender preview maps (source module, grid, world size, districts), the
      ambience table (sky/fog/key light per theme), per-item scale + pinning,
      the tile-size control and the new 80-tile ceiling, protocol v9.

## Play-test checklist (user)

- [ ] Each map reads like its render from the builder's top-down view (T) and
      from the car: districts in the right places, landmarks on the right
      horizon, the toy room unmistakably indoors.
- [ ] A clean lap on each: checkpoints ring in order, the ribbon never runs
      into a landmark hull, gates clear the roofline.
- [ ] Fog depth feels right per map — especially Graveyard Shift, which is
      deliberately ~2× the others and may need the density tuned by eye.
- [ ] Emissives (neon, lit windows, lava, jack-o'-lanterns, crystals) read
      against the new dark ambients **in the release player**, not just the
      editor.
- [ ] Builder: select a prop → scale slider resizes it live with no rebuild
      hitch; Pinned toggle stops a cone rolling; ±5 resize and the tile-size
      control behave; a 112 m map frames with F.
- [ ] Old saved user maps still load and their items are still 1×.
- [ ] LAN: v8 vs v9 → "Version mismatch"; two v9 machines render the same
      atmosphere and the same 600 props.
- [ ] Map load time is acceptable on the target machine (expected 0.3–0.8 s).

## Known risks

1. **Load cost is dominated by static batching**, not instantiation. If a
   600-item map hitches on load, the lever is batching in chunks rather than
   cutting props.
2. **BotPath picks the spline with the most control points.** One spline per
   themed preset, asserted by TPV.
3. **Fog + dark ambient can bury the props.** The Blender maps get away with
   near-black because they are photographed with a compositor bloom; Unity has
   none. Ambient floors may need lifting above the ported values — tune by eye
   and record the final numbers in the README.
4. **A custom shader is a new dependency.** Keeping `SkyGradient.shader` under
   `Resources/` guarantees inclusion, and the dome degrades to the current flat
   background if it fails to load.
5. **Tile-size changes move the map edge under fixed items.** Warned in the UI,
   not prevented; `Resize` culling is untouched.
6. **Painted roads stair-step on a 2 m grid.** Honest for a tile map and
   invisible at car height, but it will show in the top-down view.
7. The scatter uses a seeded `System.Random`, not Python's Mersenne Twister —
   the same densities and regions, not the same individual positions.

---

# Import TinyTorque map prop packs + four themed circuits (2026-07-27)

**Archived with the play-test checklist still UNDRIVEN.**

## Context

Four themed Blender prop packs exist at
`E:\EE Projects\AI_3D_Modeling\TinyTorque_RC\models` (`TinyTorque_props.blend`
= neon downtown + desert rocks + volcano, `TinyTorque_toy_props.blend`,
`TinyTorque_ench_props.blend`, `TinyTorque_haunt_props.blend`; ignore
`.blend1` backups and the pre-exported `*_props_fbx` folders — those FBXs are
single multi-material objects, still offset on the showcase line, and unusable
by the game's token pipeline). Each blend is a showcase (props lined up along
X under a `PROPS` collection with `PL_*` preview lights), NOT a laid-out map:
63 `P_*` props total (14 downtown, 17 toy, 16 enchanted, 16 haunted), authored
at full-scale metres, each ONE mesh with 2–9 material slots.

The request: bring all 63 props into the Track Builder palette, and ship four
new themed circuit presets built from them. The rail already exists end to
end: Blender → `Resources/TrackProps/*.fbx` → `PartModelPostprocessor`
(materialImportMode=None; already covers TrackProps/ — PartModelPostprocessor.cs:23)
→ `PartMeshLibrary.TryInstantiate` + `AssignByName` → `TrackCatalog.MeshProp`
(authored visual + invisible primitive hull + primitive fallback) →
`TrackBuilderUI` theme-header palette (auto-generalises: TrackBuilderUI.cs:981
loops `TrackCatalog.Themes`) → `TrackPresets` (`(name, Func<TrackDesign>)[]`).
Maps ship over LAN as full trackJson (NetSession.cs:518); unknown item ids are
skipped silently → exact-equality ProtocolVersion gate must bump 7 → 8.

**User decisions**: 4th theme named **"Downtown"**, map **"★ Downtown Dash"**;
retire the 4 oldest presets (Whoop Canyon, Monza Mini, Boulder Basin, Slide
Yard — their matching cars were removed last pass) AND the 4 old arcade
circuits (Workshop Grand Prix, Neon Vortex II, Boardwalk Cove, Foundry
Descent); ANIMATE ghost/wisp (hover-bob) and traffic light (green→amber→red
cycle); hero landmarks at FULL 1/10-world scale (castle 8.7 m, volcano 12.6 m
footprint — backdrop pieces, like the toy-room desk objects already are).

Non-negotiables: no physics/vehicle changes; Opus regression bit-identical to
R4 (Opus Proving Ground preset untouched); floor indices append-only; existing
ItemDefs (tw_/ng_/bb_/vf_ and all primitives) STAY — only map presets are
retired, so old saved user maps keep loading; retired preset names must fall
back gracefully wherever a lastTrack setting references them.

## Naming / catalog map

- Mesh keys = item ids (`Assets\Resources\TrackProps\`): downtown pack gets a
  `dt_` prefix (its files are unprefixed and `cone` would collide with the
  primitive cone item): `dt_arch_gate, dt_arch_rock, dt_barrier, dt_bld_block,
  dt_bld_hangar, dt_bld_tower, dt_cone, dt_ramp_jump, dt_ramp_kicker,
  dt_rock_large, dt_rock_small, dt_street_lamp, dt_traffic_light, dt_volcano`.
  The other packs keep their file names verbatim (`toy_*`, `ench_*`,
  `haunt_*` — 17 + 16 + 16, all unique).
- Theme headers (append to `TrackCatalog.Themes`): "Downtown", "Toy Room",
  "Enchanted Kingdom", "Haunted Hollow".
- Map presets: "★ Downtown Dash", "★ Playroom Raceway", "★ Enchanted Ascent",
  "★ Graveyard Shift".
- Post-removal preset list (8): Boost Speedway, Dust Devil Rally, Neon Vortex,
  the four new circuits, Opus Proving Ground.
- Tokens: material name minus theme prefix, lowercased (`M_Prop_NeonCyan` →
  `neoncyan`, `M_Toy_Book0` → `book0`, `M_Haunt_GhostDim` → `ghostdim`).
  AssignByName is first-match substring → **every token array is sorted
  longest-first** (ghostdim before ghost, stonepale/stonemoss/stonedark before
  stone, neongold before gold, rocktop/rockmoss before rock).

## Scale + hulls (single source of truth = the exporter's printed JSON)

Uniform `s = 0.1` (1 blend metre = 0.1 game metre — the exact 1/10 fiction;
the vehicles came out at 0.087–0.092 via their own length pins, close enough
that props read correctly beside them). Axis map as ever: Blender X/Y/Z →
Unity X/Z/Y. Origin per prop = bbox bottom-centre (TrackFactory.ItemPose snaps
roots onto the drop surface — same rule as build_props.py documents), so the
export applies `T(-cx,-cy,-minz)` then `S(0.1)`.

Representative scaled sizes: dt_cone 0.075 tall, gravestone 0.31, hedge 0.50,
pumpkin 0.28, toy_brick 0.48, street lamp 0.81 tall, toy_table 2.9×1.8×2.0
(drive under it), castle 8.7 tall, barrow 11.3 wide, volcano 12.6 wide,
ench_peak 15.2 wide — landmarks are backdrop, placed off-track inside the
44-tile maps.

The exporter prints per prop: scaled bounds (→ PMV rows + hull sizes), token
list, tri count, and for every `ramp`/`bridge`/`terrace` prop an 8-station
top-surface height profile along the long axis (→ slope direction + rotated
slab hulls; toy_ramp_bridge and dt_ramp_jump are up-over-down, kicker/plank/
slab/tomb/terrace are single slopes). Hulls are hand-authored in C# from that
JSON — box/cylinder per `TrackCatalog.HullBox/HullCyl`, multi-hull (two legs +
lintel) for the drive-through gates/arches: dt_arch_gate, dt_arch_rock,
toy_gate, toy_hoop, ench_gate, ench_arch_vine, ench_gatehouse, haunt_gate,
haunt_arch_ruin.

## Milestone 0 — plan-file housekeeping

- [x] Append everything below the `MATERIAL TO ARCHIVE` marker (end of this
      file) as ONE new entry at the TOP of `Docs/plan-archive.md` (newest-
      first, after header + first `---`). Title:
      `# Import TinyTorque Blender vehicles + preset overhaul (2026-07-27)`.
      Bold note at entry top: **play-test checklist still undriven when
      archived**. Update the archive header char-count/plan-count line.
      Splice via script — the file is ~440 KB, do not re-emit it.
- [x] Delete the archived material from this file.

## Milestone 1 — Blender exporter + 63 FBXs + validator rows

**New file** `E:\EE Projects\Tiny_Torque\Blender\build_map_props.py` —
re-runnable, never saves the source blends, modelled on `build_vehicles.py`
(same Blender 5.2 binary, same FBX args as `mcp_helpers.py:597-602`). Loops
all four blends in one `--background` run.

Per prop (`P_*` in the `PROPS` collection):
- [x] Duplicate, apply transform `S(0.1) @ T(-cx, -cy, -minz)` (origin at
      base-contact centre), `separate(type='MATERIAL')` (guard the poll like
      build_vehicles.py), `material_slot_remove_unused`, rename pieces
      `<token>_<n>` from the material→token rule above — **fail loudly on any
      material not in the theme's known list**; export
      `Resources/TrackProps/<key>.fbx`.
- [x] Special case dt_traffic_light: the `sigoff` piece holds BOTH dark lenses
      — `separate(type='LOOSE')`, classify by height (top → `sigred`, middle →
      `sigamber`; the green lens is already its own `siggreen` material), so
      the cycle script can drive each lamp.
- [x] Print one JSON block per prop: key, scaled size, token list, tris, ramp
      height profile where applicable. Everything downstream (hulls, PMV rows,
      budgets) is pasted from this output, never hand-derived.
- [x] `Assets\Editor\PartModelValidator.cs`: 63 new track-prop Spec rows
      (maxExtent = scaled max dimension + ~10 %, budgets from measured tris:
      castle 18172 → 19000, toy_bookcase 8124 → 9000, ench_gate 6416 → 7000,
      haunt_mansion 5416 → 6000, most others ≤ 3000). Landmark extents up to
      15.5 are fine — MaxExtent is per-row.

Checkpoint: FBXs import, `PartModelValidator.Report` ALL PASS, compile clean.

## Milestone 2 — Catalog: materials, 63 ItemDefs, animations

All in `TrackCatalog.cs` unless noted; the `T(key, r, g, b, smooth, glow)`
keyed material factory (:283) and `MeshProp`/`MeshPropDynamic` idioms already
exist — this milestone is data, not new machinery.

- [x] Theme materials via `T()`: colors from the inventory dump's principled
      values; emissives get `glow` scaled to taste (house style caps ~2.0, cf.
      VfLava). ~20 materials per theme are real colors; the placeholder
      0.8-grey procedural ones (walnut, pine, ply, card, facade, basalt, the
      stone/slate family, thatch, hedge/leaf, grime, marble, shingle,
      deadwood, snow…) get hand-picked flat colors in-theme.
- [x] `ItemDef` gains `public Vector3 lightPos = new Vector3(0f, 0.8f, 0.25f);`
      and TrackFactory's `ItemBehavior.Light` case (:267-273) uses it instead
      of the hardcoded offset — default preserves every existing lamp.
- [x] 63 new ItemDefs, `category = Scenery`, `theme` = the four new headers,
      labels human ("Rock arch", "Book tower", "Gas lamp"…). Hulls from the M1
      JSON. Notable behaviors:
      - **Dynamic** (MeshPropDynamic): dt_cone (bottomHeavy, 0.03 kg),
        toy_ball (sphere, 0.05), toy_crayon (capsule along X, 0.03),
        toy_domino (box, 0.02), toy_brick (box, 0.04), haunt_pumpkin (0.06).
      - **Light behavior** (+ lightPos at the authored head height):
        dt_street_lamp, toy_lamp, toy_floor_lamp, ench_lamp, haunt_gaslamp.
      - **Drive-through spirits**: haunt_ghost, haunt_wisp — hull collider
        `isTrigger = true` (selectable in the builder like item_box, but the
        car passes through).
      - Gates/arches: multi-hull legs + lintel (list in the scale section).
      - Ramps: rotated slab hulls from the printed profiles; the deck face is
        the only surface that must be right (tw_ruler_ramp precedent, :430).
- [x] `Themes` array += the four new headers (palette generalises itself).
- [x] **New** `Assets\Scripts\Track\GhostBob.cs` — cosmetic transform bob
      (~±0.04 m) + slow yaw sway, phase from GetInstanceID like
      LightBarStrobe; attached inside the ghost/wisp build lambdas (the
      LightBarStrobe precedent — icons snapshot one frame, harmless).
- [x] **New** `Assets\Scripts\Track\SignalCycle.cs` — finds its sigred/
      sigamber/siggreen renderers by bound material, cycles green 4 s → amber
      1 s → red 3 s via per-renderer MaterialPropertyBlock `_EmissionColor`
      (never the shared materials); attached in dt_traffic_light's lambda.

Checkpoint: compile; every prop places in the builder with authored look,
palette shows four new theme headers with icons.

## Milestone 3 — Four circuit presets + removals + protocol v8 + README

`TrackPresets.cs` — follow the WorkshopGrandPrix idiom (:398): closed spline
with per-point widths/roll/surfaces, floors painted for run-off (below 0.90
friction = arcade off-track for free), authored BoxRows, dense checkpoint
order, finish + spawn. All ~44×44 tiles.

- [x] **★ Downtown Dash** (Downtown) — asphalt street circuit under the neon
      towers; buildings + arch_gate gantry ring the lap, dt_ramp_jump
      crossover, cone/barrier chicanes, street lamps + cycling traffic lights
      down the straights, volcano + rock arch in the desert corner (sand
      run-off).
- [x] **★ Playroom Raceway** (Toy Room) — wood floor, carpet run-off; lap
      threads UNDER the table and chair (2 m legs), climbs a ramp_plank onto
      an elevated run past the bookcase, toy_gate start arch, hoop gate,
      dominos/bricks/crayons loose on the line, bed + dresser + block tower
      as landmarks, floor lamp lighting.
- [x] **★ Enchanted Ascent** (Enchanted Kingdom) — grass/dirt park circuit
      climbing terrace + bridge ramps to a gatehouse pass; castle on the far
      hill, peak in the corner, cottage/fountain/hedges/topiary/trees lining,
      crystal + lamp glow, ench_gate start.
- [x] **★ Graveyard Shift** (Haunted Hollow) — dirt/mud night circuit;
      haunt_gate start, gravestone + fence rows through the cemetery esses,
      ramp_tomb jump, mansion/chapel/barrow/crypt as landmarks, hearse parked
      trackside, bobbing ghost over a crossing (drive-through), wisps +
      gaslamps + pumpkins (dynamic) along the lap.
- [x] Retire 8 presets: delete WhoopCanyon, MonzaMini, BoulderBasin,
      SlideYard, WorkshopGrandPrix, NeonVortexII, BoardwalkCove,
      FoundryDescent methods + `All` rows. Grep menu/settings paths for a
      lastTrack fallback (vehicle-side had a latent bug here — MenuBootstrap
      pattern); verify `TrackPresets.Resolve` returning null degrades
      gracefully everywhere it's called.
- [x] `NetSession.cs`: `ProtocolVersion = 8` + history comment (v7 peers
      would silently drop all 63 item ids from a received trackJson).
- [x] `README.md`: maps section rewrite (8 presets), builder palette section
      (four new themes, 63 props, animated ghost/traffic light), protocol v8.

## Milestone 4 — Verification

- [x] **V1 compile**: batch `-batchmode -nographics -quit`, 0 `error CS`
      (PowerShell doesn't wait — `Start-Process -Wait` or poll).
- [x] **V2 PartModelValidator.Report**: `[PMV] RESULT ALL PASS` (110 assets);
      tighten provisional budgets to printed counts.
- [x] **V3 TrackPresetValidator.Report**: `[TPV] RESULT ALL PASS` for the new
      8-preset list (catches unknown item ids, floor overruns, checkpoint
      gaps, out-of-bounds items — exactly the silent failures new presets
      risk).
- [x] **V4 Opus regression**: `-batchmode`, NO `-nographics`, NO `-quit`,
      poll JSON — bit-identical R4 (legA −13.615608215332032 mm, turn
      +0.1873779296875°, legB +15.803813934326172 mm, brake
      +42.34135055541992 mm, total +58.14552307128906 mm, drift
      −42.4041748046875 mm, completed true, fault 0, phase 10).
- [x] **V5 release build** via BuildMenu.BuildRelease (forced by v8); verify
      Assembly-CSharp.dll timestamp.

## Play-test checklist (user)

- [ ] Each new map drives a clean lap: checkpoints ring in order, ramps
      launch, gates clear the roofline, landmarks sit off-track.
- [ ] Builder: all four theme headers show with icons; each prop places,
      rotates, deletes; dynamic props (cone/ball/domino/brick/crayon/pumpkin)
      knock around; ghost/wisp are drive-through but selectable.
- [ ] Traffic light cycles green→amber→red; ghost + wisp bob; street/gas/
      floor lamps cast light at night-ish maps.
- [ ] Retired maps gone from pickers; a lastTrack pointing at one falls back
      gracefully; old saved user maps (with tw_/ng_/bb_/vf_ props) still load.
- [ ] LAN: v7 vs v8 → "Version mismatch"; two v8 machines both render a new
      map's scenery fully.
- [ ] On-car camera sees scenery (props stay on the parent layer, not
      VizLayer — MeshProp already does this).
- [ ] Emissives (neon, lava, windows, jack-o-lantern) glow in the release
      player (same shader-variant-stripping risk as the vehicle pass).

## Known risks

1. Multi-material showcase objects: solved structurally by
   separate-by-material in the exporter + fail-loud on unknown materials.
2. First-match substring tokens: longest-first ordering rule; the exporter
   prints each prop's token list so the C# arrays are pasted, not remembered.
3. Landmark palette icons (15 m peak) may frame oddly in TrackIconFactory
   snapshots — cosmetic only; accept or nudge icon framing if ugly.
4. Trigger hulls for ghost/wisp rely on the builder's selection raycast
   hitting triggers (Physics.queriesHitTriggers default true; item_box
   precedent says it works).
5. Procedural-shaded materials export as 0.8 grey — flat colors are
   hand-picked in C#; authored look is approximated, not sampled.
6. Old themed props + retired-map ids stay in the catalog forever (saved user
   maps depend on them) — only preset list rows are deleted.
7. TrackFactory drops items from y+3 onto the surface below; landmarks with
   big hulls must not be placed overlapping the ribbon or they become walls —
   preset authoring discipline, TPV catches out-of-bounds but not overlap.


---
# Import TinyTorque Blender vehicles + preset overhaul (2026-07-27)

**Archived with the play-test checklist still UNDRIVEN.**

## Context

Three finished Blender car models exist at
`E:\EE Projects\AI_3D_Modeling\TinyTorque_RC\models` (`TinyTorque_car.blend`,
`TinyTorque_buggy.blend`, `TinyTorque_police.blend`; ignore `.blend1` backups).
They are to enter the game two ways: (a) split into garage components — body,
wheels, lights, antenna — usable on any car, and (b) as three complete drivable
base-car presets, geometry exactly as authored, scaled to game size. Four old
presets are removed. Authored materials (chrome, gold, glass, emissive lights)
are preserved; the neutral 0.8-grey paint channel stays tintable.

The game already has the rail: Blender scripts → FBX → `Resources/PartModels`
→ `PartModelPostprocessor` (materialImportMode=None, isReadable for `body_*`)
→ `PartMeshLibrary` (Resources.Load, Sanitise, `AssignByName` token→material
binding) → `PartVisualFactory`/`CarVehicle`. Bodies are an append-only enum,
wheel styles an append-only int, presets a `(name, Func<VehicleDesign>)[]`.
Appearance ships over LAN as full vehicleJson, gated by exact ProtocolVersion
equality → bump 6 → 7.

All three blends share one rig: `<NAME>_ROOT` → `<NAME>_BODY` (body meshes) +
`W_xx_STEER → SPIN → SIDE` wheel empties (tire/rim/disc under SIDE, calipers
under STEER), +X forward, +Z up, ~4.4–4.84 units long. Every paint material
(`M_Paint`/`M_Buggy_Paint`/`M_Police_Paint`) is authored 0.8 grey = the tint
channel.

**User decisions**: remove Rally Buggy, F1 Racer, Crawler, Drift Car (keep
Real Twin 1/10 + Opus Vector — mission harness requires "Opus Vector" to
resolve); tintable paint channel (accents locked as authored); Light parts =
police bar + buggy pods only (contoured head/tail lights stay baked into their
bodies, emissive); police strobes ANIMATED (alternating red/blue pulse).

Non-negotiables: no physics changes (CarVehicle edits are enum/visual/catalog
only); Opus regression must stay bit-identical to R4; enum ordinals and
wheelStyle ints append-only; new JSON fields must default to legacy behavior.

## Naming / catalog map

- `BodyShape { Box, Wedge, Buggy, Shell, LowRacer, Coupe, Baja, Patrol }`
  (CarVehicle.cs:12; ordinals 5/6/7).
- Mesh keys (`Assets\Resources\PartModels\`): `body_coupe`, `body_baja`,
  `body_patrol` (must start `body_` — isReadable rule,
  PartModelPostprocessor.cs:56), `wheel_coupe`, `wheel_baja`, `wheel_patrol`,
  `light_bar`, `light_pods`, `antenna_whip`, `antenna_flag`, `antenna_twin`.
- Wheel styles: 3=coupe, 4=baja, 5=patrol (PartVisualFactory.WheelStyleKey:70
  + GarageUI styleNames:1129).
- `AntennaSpec.antennaStyle` int (new, defaults 0): 0 stub, 1 whip+amber tip,
  2 flag whip, 3 twin.
- New `LightSpec.style`: 0 bar, 1 pods. New `PartType.Light`.
- Presets: add "TT Coupe", "TT Baja", "TT Patrol".

## Scale math (single source of truth = the Blender script's printed JSON)

Uniform `s = 0.42 / bodyLengthX`; nominal coupe 0.0955, baja 0.0917, patrol
0.0868. Axis map: Blender +X (nose) → Unity +Z, Blender Y → Unity X, Blender Z
→ Unity Y. Export origin: wheel-set centre laterally/fore-aft, height so
wheel centres land at Unity y = −0.045 (stock authoring contract).

| | s | wheel z ± | wheel x ± | wheel r |
|---|---|---|---|---|
| Coupe | 0.0955 | 0.1375 | 0.0998 | 0.0453 |
| Baja | 0.0917 | 0.1449 | 0.1192 | 0.0551 |
| Patrol | 0.0868 | 0.1319 | 0.0855 | 0.0412 |

Wheel FBXs export at author radius exactly 0.033 (scale 0.033/tireR: 0.06950
car/police, 0.05487 buggy); runtime rescales by radius/0.033. Presets set
`bodySize = (0.20, 0.10, 0.42)` exactly → `bodySize / BodyMeshAuthorSize` =
identity → mesh renders as-authored, undistorted (collider/aero stay nominal).

## Milestone 0 — plan-file housekeeping

- [x] Append everything below the `MATERIAL TO ARCHIVE` marker (end of this
      file) as ONE new entry at the TOP of `Docs/plan-archive.md` (newest-first,
      after header + first `---`). Title:
      `# LAN visual parity (protocol v6) + arcade bot racing line (2026-07-27)`.
      Bold note at entry top: **play-test checklist still undriven when
      archived**. Update the archive header char-count/date line. Splice via
      script — the file is ~420 KB, do not re-emit it.
- [x] Delete the archived material from this file.

## Milestone 1 — Blender build script + 11 FBX exports + validator rows

**New file** `E:\EE Projects\Tiny_Torque\Blender\build_vehicles.py` —
re-runnable, never saves the source blends. Run with Blender 5.2
(`C:\Program Files\Blender Foundation\Blender 5.2\blender.exe --background
--python build_vehicles.py`); the blends are 5.x-era, the 3.1 install may not
open them. Mirror the FBX args of `mcp_helpers.py:597-602` exactly
(use_selection, apply_unit_scale=False, global_scale=0.01, axis_forward='-Z',
axis_up='Y', bake_space_transform=True, MESH only, mesh_smooth_type='EDGE',
use_tspace=True).

Per car:
- [x] **Body FBX**: duplicate every MESH under `<NAME>_BODY` EXCLUDING the
      light group (baja: Buggy_LightCans+Lenses; patrol: Police_Bar*/strobes)
      and antenna group (coupe: Car_Antenna/Tip/AntMount; baja:
      Buggy_Whip+Flag; patrol: Police_Antennas). STEER/SPIN/SIDE subtrees are
      not under `_BODY`, so uprights/calipers stay out; buggy shocks/arms
      parented to `_BODY` come along as static geometry (accepted — presets use
      suspLength 0 so no procedural strut doubles the authored one). Apply
      transforms, `separate(type='MATERIAL')` (guarantees 1 material/object —
      Car_Body has 2 slots and AssignByName only sets slot 0), rename objects
      `<token>_<n>` from a material→token table (paint/dark/chrome/gold/glass/
      em_head/em_tail/tube/orange/em_amber/em_red/em_blue/barwhite…),
      **fail loudly on unmapped materials**; assert UVs exist on every paint
      object; scale by s, origin per the table; export nose → Unity +Z.
- [x] **Wheel FBX**: front-left `W_xx_SIDE` subtree only (tire, rim, barrel,
      disc, nut — NO calipers/uprights: the whole viz holder takes the spin
      quaternion (CarVehicle.cs:1594) and a baked caliper would orbit).
      Centre at wheel centre, scale to author r 0.033, axle +X, rim face +X
      (PartVisualFactory flips 180° per side). Tokens: tire, rim→gold/orange/
      chrome per car, disc→brake, nut→chrome.
- [x] **Light FBX** (baja pods, patrol bar): centred on own bounds, scaled by
      s (authored at game size — rendered unscaled like antenna_stub). Tokens:
      dark/chrome/em_red/em_blue/barwhite/em_head.
- [x] **Antenna FBX**: coupe whip+amber tip (origin at ANT_BASE), baja
      whip+flag, patrol both whips baked as one part (origin at midpoint).
      Tokens: whip/base/em_amber/flag.
- [x] **Print a JSON block** per car: s, Unity-space wheel positions/radius,
      light + antenna mount points → pasted into the M6 presets, never
      hand-derived.
- [x] `Assets\Editor\PartModelValidator.cs` Specs (:50): rows for all 11 new
      keys — bodies pin Z=0.420 (X free, real widths 0.19–0.20), wheels pin
      Y=Z=0.066; tri budgets provisional, tightened after the first `[PMV]`
      report prints real counts.

Checkpoint: FBXs import, `PartModelValidator.Report` ALL PASS, compile clean.

## Milestone 2 — Bodies: enum, accent materials, painter filter

- [x] `CarVehicle.cs`: append enum values (:12); `BodyMeshKey` (:699) 3 new
      cases. `BuildBodyVisual` (:645): for the three new shapes, replace the
      flatten-to-_bodyMat loop (:676-680) with `AssignBodyAccents(inst)` —
      walk renderers by name token; `paint` → `_bodyMat` (and only those into
      `_bodyRenderers`, so livery/SetBodyMaterial keep working); other tokens →
      shared accent materials; unmatched → `_bodyMat`. Old shapes bit-identical.
      Expose `public IReadOnlyList<MeshRenderer> PaintRenderers`.
- [x] `PartVisualFactory.cs`: new lazy shared accent materials (pattern
      :26-45): Chrome, Gold, DarkTrim, Glass (transparent fade like
      MakeGhostMat), Tube, OrangeAccent; emissive (EnableKeyword "_EMISSION"):
      HeadLight (white), TailLight (red), Amber, RedStrobe, BlueStrobe,
      BarWhite. Colors from the blend values.
- [x] `AeroDynamics.cs`: `BodyCd` (:41) Coupe 0.48 / Baja 0.85 / Patrol 0.55;
      `BodyClA` (:55) Coupe 0.004 / Patrol 0.003 / Baja 0.
- [x] `GarageUI.cs` DrawBodyTab (:915-929): 8 shape buttons — wrap into rows
      of 4.
- [x] `BodyPainter.cs` Attach (:101-109): cook MeshColliders only for
      renderers in `car.PaintRenderers` (all body_* meshes are readable —
      today's unfiltered loop would let a stroke on the canopy stamp garbage
      into the shared livery texture via the glass mesh's UVs).

Checkpoint: compile; three new shapes in garage with chrome/gold/glass/
emissive look; color slider + PAINT tab touch only paint panels.

## Milestone 3 — Wheel styles

- [x] `PartVisualFactory.WheelStyleKey` (:70): 3/4/5 → coupe/baja/patrol.
      Extend the AssignByName call (:105-107) with ("gold", Gold),
      ("orange", OrangeAccent), ("chrome", Chrome) ahead of existing tokens.
- [x] `GarageUI.cs:1129`: styleNames += "Coupe", "Baja", "Steelie".

## Milestone 4 — Antenna styles

- [x] `VehicleDesign.cs` AntennaSpec (:121): `public int antennaStyle = 0;`
      (old JSON → 0 = stub).
- [x] `PartVisualFactory.BuildAntennaViz` (:320): style param → key switch
      (1 whip / 2 flag / 3 twin / else stub); tokens += ("em_amber", Amber),
      ("flag", OrangeAccent). Primitive fallback unchanged.
- [x] `VehicleFactory.CreateAntennaVisual` (:201) passes style;
      `PartGhost.ForAntenna` (:46) + its GarageUI callers (:342, :348, :401).
- [x] `GarageUI.DrawAntennaInspector` (:1281): style cycle button
      (Stub/Whip/Flag/Twin) + RebuildPreview.
- [x] `SymmetryUtil.MirrorInto(AntennaSpec)` (:121): **explicit
      `dst.antennaStyle = src.antennaStyle;` — MirrorInto copies fields by
      hand, not MemberwiseClone; without it a mirrored twin resets to stub.**

## Milestone 5 — Light part category (+ animated strobe)

Template: every AntennaSpec touch point (newest category, hits them all).

- [x] `VehicleDesign.cs`: `LightSpec { name, localPos, yawDeg, style, sizeScale
      =1, mirrorGroup=-1, massKg=0, Clone() }` + `List<LightSpec> lights`
      (old JSON → empty).
- [x] `PartMarker.cs:6`: append `Light`.
- [x] `PartVisualFactory.BuildLightViz(parent, style, sizeScale)`:
      TryInstantiate light_bar/light_pods (default VizLayer — invisible to the
      on-car camera like every part), AssignByName (dark/chrome/em_red/em_blue/
      barwhite/em_head), primitive fallback (box + two emissive cubes). For
      style 0 attach new `LightBarStrobe` MonoBehaviour: alternates red/blue
      emission ~3 Hz via **MaterialPropertyBlock on its own renderers** (never
      the shared RedStrobe/BlueStrobe materials — those are shared by every
      bar and the palette icon). Cosmetic only; no network state.
- [x] `VehicleFactory`: build loop + `CreateLightVisual` + `Built.lightVisuals`
      (antenna pattern :163-167, :201).
- [x] `GarageBootstrap`: PreviewLights, markers, SetPartVisible (antenna
      pattern :29/:154/:190/:257).
- [x] `GarageUI`: palette entry in MISC ("light", "Lights", "Roof light bar /
      pod cluster — cosmetic, emissive."), StartDrag/StartPlacing/drop/twin/
      pending/marker/parts-list/inspector (style cycle Bar/Pods, size, mass)/
      move/delete/name-pool — clone each antenna site (:339-348, :397-402,
      :451, :544-563, :617, :630/:644, :728, :1093-1099, :1109, :1374, :1536,
      :1555).
- [x] `PartGhost.ForLight(style, sizeScale, yaw)`.
- [x] `SymmetryUtil`: FindTwin/MirrorInto/SyncTwin for LightSpec — **explicit
      `dst.style = src.style;`**.
- [x] `MassProperties`: `LightMass = 0.012f` + lights loop (:37/:81 pattern).
- [x] `PartIconFactory.BuildFor` (:30): `"light" => BuildLightViz(p, 0, 1f)`
      (snapshot cullingMask is VizLayer-only and TryInstantiate defaults to
      VizLayer, so the icon renders — no fix needed).

## Milestone 6 — Presets, removals, menu fix, protocol v7, README

- [x] `VehiclePresets.All` becomes: Real Twin 1/10, TT Coupe, TT Baja,
      TT Patrol, Opus Vector. Delete RallyBuggy/F1Racer/Crawler/DriftCar
      builder methods. New builders (wheel/mount numbers pasted from the M1
      JSON; sensors = camera + front ToF + AddEncoders; bodyColor default
      (0.8, 0.8, 0.8) = authored silver):
      - **TT Coupe**: Coupe body, mass 1.7, wheels ±0.0998 x / ±0.1375 z,
        r 0.0453, style 3, fronts steer, rears powered, stiff susp
        (400 N/m, ζ 0.7, travel 0.025), antennaStyle 1 at scaled ANT_BASE.
      - **TT Baja**: Baja body, mass 1.95, wheels ±0.1192 / ±0.1449, r 0.0551,
        style 4, 4WD, fronts steer, soft susp (200, ζ 0.55, travel 0.05),
        light pods (style 1) on roof, antennaStyle 2.
      - **TT Patrol**: Patrol body, mass 1.8, wheels ±0.0855 / ±0.1319,
        r 0.0412, style 5, fronts steer, rears powered, susp 350/ζ 0.65/0.03,
        light bar (style 0) on roof, antennaStyle 3.
- [x] Latent-bug fix folded in: `MenuBootstrap.ResolveShowDesign` (:72-81)
      tries `VehiclePresets.Resolve` before defaulting (MenuUI.ResolveVehicle
      :96-101 pattern) — a ★ preset as last vehicle now shows on the menu.
- [x] `NetSession.cs:51`: `ProtocolVersion = 7` + house-style history comment
      (v6 peers lack the new shapes/styles/light parts and would render a
      received vehicleJson wrong).
- [x] `README.md`: preset section rewrite (:239-245), grep for removed preset
      names, note the three new cars + light/antenna parts + v7.

## Milestone 7 — Verification

- [x] **V1 compile**: batch `-batchmode -nographics -quit`, 0 `error CS`
      (delete UnityLockfile if stale; PowerShell doesn't wait — poll).
- [x] **V2 PartModelValidator.Report** batch: `[PMV] RESULT ALL PASS`; tighten
      M1 tri budgets to real counts.
- [x] **V3 Opus regression**: `-batchmode -executeMethod
      AIHWSim.EditorTools.OpusMissionRunner.RunHeadless` — NO `-nographics`,
      NO `-quit`, poll JSON. Bit-identical R4: legA −13.615608215332032 mm,
      turn +0.1873779296875°, legB +15.803813934326172 mm, brake
      +42.34135055541992 mm, total +58.14552307128906 mm, drift
      −42.4041748046875 mm, completed true, fault 0, phase 10. (Opus Vector
      untouched; enum append preserves ordinals; body-build changes are
      branch-gated to the new shapes.)
- [x] **V4 release build** via BuildMenu.BuildRelease (forced by v7); verify
      Assembly-CSharp.dll timestamp. In the built player check emissives +
      transparent glass render (runtime Standard-shader variants can be
      stripped from builds; fallback = a Resources-referenced material
      carrying the keywords).

## Play-test checklist (user)

- [ ] Each new preset drives: wheel arches align, wheels sized right, body
      undistorted, no floating/sunken stance.
- [ ] Garage: color picker + PAINT strokes hit only paint panels on all three
      bodies (glass/chrome/gold/lights immune); undo/redo; save/load
      round-trip; old saved designs still load.
- [ ] Light bar strobes alternate red/blue; pods glow; parts place/mirror/
      delete cleanly on any body; palette icons render.
- [ ] Antenna styles cycle; mirrored antenna keeps its style.
- [ ] Removed presets gone from menu + garage; last-vehicle set to a removed
      name falls back gracefully; menu showcar now displays ★ presets.
- [ ] LAN: v6 vs v7 → "Version mismatch"; two v7 machines see each other's
      new cars correctly (light parts, wheel styles, paint).
- [ ] On-car camera feed: new bodies visible to own camera as intended (body
      stays on car layer); light parts NOT in the feed (VizLayer).

## Known risks

1. Multi-material objects: solved structurally by separate-by-material in the
   exporter + a 1-slot assert; AssignByName only sets slot 0.
2. Buggy shocks/arms baked into the body won't articulate with steering/
   suspension travel — accepted (small visual disconnect at full lock).
3. Camera sensor sees the taller buggy cage / patrol roof at frame top —
   by design (body on car layer); Opus mission unaffected (LowRacer).
4. Runtime emission/fade shader variants may be stripped from release builds —
   explicit V4 check + known fallback.
5. AssignByName is first-match substring — tokens chosen unambiguous
   (em_head/em_tail, never bare "light"); exporter fails loudly on unmapped
   materials.
6. Police twin antenna is one centred part (mirroring a per-side whip would
   yield four) — quirky off-centre placement accepted.
7. Old body_buggy etc. FBXs and enum values stay shipped — saved designs from
   removed presets must keep rendering.

---
# LAN visual parity (protocol v6) + arcade bot racing line (2026-07-27)

**Archived with the play-test checklist still UNDRIVEN.** The `- [ ]` items
at the bottom were never driven; they remain valid things to check.

## Context

Two play-test asks. (1) The arcade visual effects should be **fully online**:
today a remote car's drift smoke, tier sparks and mini-turbo are invisible on
other machines — `driftDir`/`driftTier` are owner-local and never sent, and a
client's mini-turbo is an acknowledged gap ([ArcadeNetLink.cs:198-202]). Boosting
has **no visual at all**, even locally — just physics + a local audio loop — so
part of this is a new boost-flame effect. Ghost cars also slide silently
(`VehicleAudio` hard-zeroes skid on the ghost path). (2) The bots should read
more arcade: spread out across the track on straights, but follow a real
**apex-cutting racing line** (outside–inside–outside) through corners. Today
they weave a fixed ±0.9 m sine around the centerline that never fully converges
in corners (`off *= 1 - 0.5*curv`) and ignores track width (authored widths
2.2–4.4 m; track-limit penalties apply to bots).

User decisions: ghost skid audio YES; new boost visual YES (local + synced);
apex-cutting line (not just converge-to-center); spread scaled to local track
half-width with difficulty personality (Easy wide/sloppy, Hard tight/fast).

Non-negotiables: `CarVehicle.cs` is untouched by this pass. Bots are local-only
(never LAN, never Opus mission — `MissionAutorun` uses `DriveControl.Firmware`,
no bots, no arcade layer), but the Opus regression runs anyway because
`VehicleAudio`/`BotPath`/`BotDriver` are in the diff. Protocol bumps 5 → 6, which
forces the standalone release rebuild.

The shield bubble is the template for everything in Task A: it is the one
car-attached visual that already works on ghosts, via
`if (!DrivesPhysics(r)) { UpdateShieldViz(r, dt); continue; }`
([ArcadeDirector.cs:1124]). Ghost cars already carry a real `ArcadeRacer` and
the correct `bodyColor` (roster `vehicleJson` → `VehicleFactory`), so
`DriftSmoke`'s per-car tint is free remotely.

## Milestone 0 — plan-file housekeeping

- [x] Append everything below the `MATERIAL TO ARCHIVE` marker (end of this
      file) as ONE new entry at the TOP of `Docs/plan-archive.md` (newest-first,
      right after the header + first `---`). Title:
      `# Arcade pass 3 — play-test pass + follow-ups 1-4 (2026-07-26/27)`.
      Add a bold note at the entry top: **the play-test checklists were still
      undriven when archived** — keep every `- [x]` unchecked. Update the archive
      header's char-count/date line (plan-archive.md:11-12). Append via editor
      insertion — the file is ~400 KB, do not re-emit it.
- [x] Delete the archived material from this file, leaving only this plan.

## Task A — arcade visuals/audio fully online

### Wire delta (bit-exact, no packet grows)

`ArcEffect : ushort` (NetMessages.cs:249-267; 512+ free):
- bit 9 / 512 `Drifting` — car committed to a slide (owner-truth)
- bits 10-11 / 1024+2048 — 2-bit drift tier; encode `fx |= (ArcEffect)((tier&3)<<10)`,
  decode `((int)fx>>10)&3`; meaningful only with `Drifting` set (tier 0 = charging).

`OwnStateMsg` flags byte (currently 1=penalized, 2=warned; NetMessages.cs:431):
- bit 2 / 4 `drifting` (`racer.Drifting`)
- bits 3-4 / 8+16 drift tier
- bit 5 / 32 `miniTurbo` (`Clock < racer.driftBoostUntil`)

No new Boost bit: `ArcEffect.Boost` already means `r.Boosting`, which includes
`driftBoostUntil` — the gap was only that the host never *learned* about a
client's drift. `NetSession.ProtocolVersion` 5 → 6 (+ version-history comments
in NetSession.cs:29-42 and NetMessages.cs:12-24). `LanDiscovery` reads the
constant directly — no second site.

### Milestone A1 — wire + state plumbing (compiles standalone, no visuals)

Files: `Net/NetMessages.cs`, `Net/NetSession.cs`, `Net/OwnStateSender.cs`,
`Net/ArcadeNetLink.cs`, `Arcade/ArcadeRacer.cs`.

- [x] `NetMessages.cs`: enum bits, `OwnStateMsg` fields
      (`drifting`/`driftTier`/`miniTurbo`), pack/unpack, header comment.
- [x] `NetSession.cs`: `ProtocolVersion = 6` + v6 history paragraph.
- [x] `ArcadeRacer.cs`: `public float remoteDriftUntil; public int
      remoteDriftTier; int remoteDriftTierShown = -1;` +
      `RemoteDrifting => ArcadeDirector.Clock < remoteDriftUntil`; reset all in
      `ClearAll()`.
- [x] `OwnStateSender.cs` (:63-76): fill the three fields from `rig?.arcade`
      (null-safe → zeros in non-arcade sessions).
- [x] `ArcadeNetLink.cs` host uplink: subscribe `S.OwnStateReceived` in the host
      branch (unsubscribe in `OnDestroy`). On receive for slot's racer `r`:
      if drifting → `r.remoteDriftUntil = Clock + EffectHold; r.remoteDriftTier
      = tier;` if miniTurbo → `r.driftBoostUntil = max(r.driftBoostUntil,
      Clock + EffectHold)` (that one write makes `EffectsOf`'s existing
      `r.Boosting` relay it to everyone).
- [x] `EffectsOf` (:193-212): `bool drifting = r.Drifting || r.RemoteDrifting;`
      set `Drifting` + tier bits (owner's `driftTier` if local, else
      `remoteDriftTier`). Rewrite the stale "invisible by construction" comment
      (:198-202).
- [x] `OnSync` per-racer: for `a.slot != S.LocalSlot` only (owner-truth, same
      reasoning as penalized/warned at :267-271):
      `r.remoteDriftUntil = Hold(a.effects, ArcEffect.Drifting, clock);
      r.remoteDriftTier = ((int)a.effects >> 10) & 3;`
      Hold (0.25 s) bridges a dropped 15 Hz packet — smoke can't strobe.

### Milestone A2 — boost flame (new, local + ghost) and ghost drift VFX

Files: `Arcade/ArcadeVfx.cs`, `Arcade/ArcadeRacer.cs`, `Arcade/ArcadeDirector.cs`.

- [x] `ArcadeVfx.BuildBoostFlame(Transform car)` — mirror
      `BuildDriftSparks`/`BuildShield`: root `"BoostFlame"` parented to the car,
      collider-free primitives on `PartVisualFactory.VizLayer`, additive
      `BurstSkin`. Car is 0.42 m long, rear ≈ z −0.21: outer plume sphere
      (0, 0.02, −0.26) scale (0.10, 0.08, 0.26); hot core (0, 0.02, −0.22) scale
      (0.055, 0.045, 0.15); side jets (±0.06, 0, −0.20) scale (0.04, 0.035, 0.10).
      Colors (ArcadeVfx, cosmetic consts stay local per the DriftSmoke rule):
      outer `(1, 0.55, 0.15)` emission ×2, core `(1, 0.92, 0.62)` ×3.
- [x] `ArcadeRacer`: `[NonSerialized] public Transform boostViz;` +
      `HideBoostFlame()` (idempotent destroy, shape of `HideDriftSparks`
      :257-261); call from `ClearAll()`.
- [x] `ArcadeDirector.UpdateBoostViz(r, dt)` — `UpdateShieldViz` pattern:
      on = `r.car != null && Clock >= r.wreckedUntil && r.Boosting`; lazy build,
      hide when off; length-only pulse `1 + 0.30*sin(Clock * 12Hz * 2π)`
      (consts `BoostFlamePulseHz = 12f`, `BoostFlamePulseAmp = 0.30f` in the
      director). Works on ghosts because `OnSync` Hold-arms `boostUntil`.
- [x] `UpdateGhostVfx(r, dt)` replacing the gate at :1124
      (`if (!DrivesPhysics(r)) { UpdateGhostVfx(r, dt); continue; }`):
      `UpdateShieldViz` + `UpdateBoostViz`; drive `driftSmoke.emitting` from
      `r.RemoteDrifting` (lazy `DriftSmoke.Attach` — works unchanged on ghosts,
      reads only transform + bodyColor); lazy sparks build + retint only when
      `remoteDriftTier != remoteDriftTierShown`; `HideDriftSparks()` when not
      drifting.
- [x] Extract `ShowDriftSparks`'s tint body (:1390-1404) into
      `TintDriftSparks(r, tier)` — one color table, two callers.
- [x] Add `UpdateBoostViz(r, dt)` to the owned path next to `UpdateShieldViz`
      (:1174).

### Milestone A3 — ghost skid audio

Files: `Audio/VehicleAudio.cs`, `Net/ClientCarView.cs`.

- [x] `VehicleAudio`: add `public float externalSlip01;`. Refactor the car
      branch's skid computation (:140-164) into a shared
      `ComputeSkid(float slip01, float speed)` — deadband, onset hold, depth,
      speed gain, both Perlin wanders, byte-identical logic. Car branch passes
      `(car.TyreSlip01, √(fwd²+lat²))`; ghost branch passes
      `(externalSlip01, |externalSpeed|)` instead of hard-zeroing.
- [x] `ClientCarView`: each Update after posing, lateral-slip proxy from the
      interpolated velocity — `local = InverseTransformDirection(velWorld)`,
      `slipDeg = atan2(|local.x|, max(1, |local.z|))`, mapped through
      `SkidSlipMinDeg = 8f` / `SkidSlipMaxDeg = 30f` (brackets the drift band
      11°–34°) into `_audio.externalSlip01`. No wire change. If extrapolation
      chirps in testing, zero the proxy while extrapolating.
- [x] Deliberate decoupling: ghost smoke = synced flag only; ghost audio =
      motion proxy only. A hit-spin ghost squeals without smoking — correct.

### Milestone A4 — docs

- [x] README:647 protocol v5 → v6 + one-line note (drift smoke/sparks,
      mini-turbo and boost flame visible on every machine); Drifting section:
      boost flame + remote visibility + ghost skid voice; Sound section touch.

## Task B — arcade bot racing line

### Milestone B1 — widths out of BotPath

Files: `Core/BotPath.cs`, `Core/TrackBootstrap.cs` (+ optionally
`Menu/MenuAttract.cs`).

- [x] New `BotPath.Build` overload with `out List<float> halfWidths`; existing
      6-arg signature delegates and discards (MenuAttract compiles unchanged).
      Point list stays **byte-identical** — TrackRespawn, the director's spine
      and item-box layout consume it.
      Spline source: `halfWidths.Add(s.width * 0.5f)` in the loop at :36.
      Classic oval: `OvalHalfWidth = 1.25f` (half of TrackBootstrap.roadWidth).
      Checkpoint gates: `GateHalfWidth = 1.0f`.
      **The `pts.Reverse()` at :73 must reverse `halfWidths` too.**
- [x] `TrackBootstrap`: capture widths at the local-session build site and pass
      to the `BotDriver` ctor (:776).

### Milestone B2 — BotDriver racing line

File: `Core/BotDriver.cs`. Ctor gains optional
`IReadOnlyList<float> halfWidths = null` (compat with MenuAttract.cs:52,:94).

- [x] Precompute alongside `_cum`: `_half[i]` (clamped [0.3, 5], default
      `DefaultHalfWidth = 1.1f` when null) and **signed** per-node curvature
      `_kappa[i]` (rad/m, + = right): `SignedAngle(seg_in, seg_out, up)` over
      local arc length, wrap when closed, box-smoothed ±2 nodes.
- [x] Continuous arc position `s` (project onto the near segment) replaces
      quantized `_cum[near]` for the weave phase and for `KappaAt(s)` /
      `HalfAt(s)` lerped lookups.
- [x] Offset math (replaces :217-223 only — the unsigned `curv` speed logic
      :207-213/:236 stays untouched, isolating risk to lateral placement):
      ```
      usable = max(0, HalfAt(s) − CarHalfWidth(0.20) − EdgeMargin(0.30))
      La     = max(2, v * anticipationSec)
      cHere  = min(1, |KappaAt(s)|    / KappaRef)      // KappaRef = 0.18
      cAhead = min(1, |KappaAt(s+La)| / KappaRef)
      line01 = cHere*sign(kHere) − (1−cHere)*cAhead*sign(kAhead)
      offRacing = lineGain * clamp(line01, −1, 1) * usable
      weaveGate = 1 − max(cHere, cAhead)
      offWeave  = (bias + amp*sin(s*freq+phase)) * usable * weaveGate
      off = clamp(offRacing + offWeave, −usable, usable)
      ```
      Out-in-out falls out of the sign arithmetic (approach: −sign(kAhead) =
      outside; apex: +sign(kHere) = inside; S-curves hand over automatically).
      Narrow bridges collapse everything toward center via `usable` —
      track-limit-safe by construction. Delete `MaxOffset`.
- [x] `Params`/`ForDifficulty` new fields:
      `lineGain` E .50 / M .75 / H .92; `weaveAmpFrac` .45/.28/.12;
      `weaveBiasFrac` .30/.20/.08; `anticipationSec` .55/.80/1.05. Ctor
      randomization becomes fractional (`_offBias = ±weaveBiasFrac`,
      `_offAmp = 0.5–1 × weaveAmpFrac`); `_offFreq`/`_offPhase` unchanged.
- [x] Do NOT disturb: blind early-return (:183-200), frozen (:170-177),
      reverse/stuck branch (:244-259), `SpeedScale` (RaceDirector.cs:154).
- [x] Sanity-check the sign convention once with a debug bot before tuning:
      a flipped `SignedAngle` sign apexes on the outside.

## Verification

- [x] **V1 compile** (after A-milestones and after B2): Unity batch
      `-batchmode -nographics -quit -logFile <scratchpad>\compile.log`.
      PowerShell does NOT wait on Unity.exe — poll for exit / log tail in a
      later call; 0 `error CS`. Delete `UnitySim/Temp/UnityLockfile` first if a
      crashed run left one.
- [x] **V2 Opus regression**: `-batchmode -executeMethod
      AIHWSim.EditorTools.OpusMissionRunner.RunHeadless` — NO `-nographics`,
      NO `-quit`; poll for the result JSON. Must be bit-identical to R4:
      legA −13.615608215332032 mm, turn +0.1873779296875°,
      legB +15.803813934326172 mm, brake +42.34135055541992 mm,
      total +58.14552307128906 mm, drift −42.4041748046875 mm,
      completed true, fault 0, phase 10.
- [x] **V3 release standalone rebuild** (forced by v6):
      `-batchmode -quit -executeMethod AIHWSim.EditorTools.BuildMenu.BuildRelease`
      → `UnitySim/Builds/Release/AI Hardware Control Sim.exe`; verify timestamp.
- [x] **V4** README edits done; archive entry from Milestone 0 in place.

## Play-test checklist (user)

LAN (editor host + rebuilt standalone):
- [ ] Drift on A → B shows car-colored smoke at the ghost's rear, sparks at
      commit, tier recolors land, all stops ≤ ~¼ s after release; no strobing.
- [ ] CLIENT mini-turbo → flame lights on the host's screen (the formerly
      impossible direction).
- [ ] Item boost / host mini-turbo → flame on every machine; never in the car's
      own CameraSensor feed.
- [ ] Ghost skid audio: sliding ghost squeals positionally, wanders, dies
      promptly; clean fast driving silent; spin-out squeals without smoke.
- [ ] v5 standalone vs v6 host → "Version mismatch"; v5 beacons filtered.
- [ ] Wreck mid-boost: flame extinguishes everywhere; shield+flame+smoke at
      once doesn't visually explode.

Bots (local; a spline circuit + classic oval + checkpoint tile map):
- [ ] Hard bots run visible out-in-out through hairpins, use the full road on
      wide sections; Easy bots wander and stay slower.
- [ ] No bot farms track-limit penalties anywhere — watch narrow bridges/planks
      (pack should single-file near center).
- [ ] S-curves cross smoothly, no zig-zag (if oscillating: widen κ smoothing or
      lower lineGain).
- [ ] Menu attract loop sane (default-width fallback). Rubber-banding, stuck
      reverse, blind behavior unchanged.

## Known risks

1. Host mirrors a client's mini-turbo into `driftBoostUntil`, which echoes back
   as `Boost` and Hold-arms the owner's own `boostUntil` — harmless (owner is
   already Boosting; flame lingers ≤ 0.25 s + RTT). Comment, not fix.
2. `arcadeBoostAccel` is written before the `DrivesPhysics` gate (:1110 vs
   :1124) — inert on kinematic ghost/follower bodies, but the boost mirror
   widens when it's non-zero; note in a comment.
3. Ghost drift start lags ≤ 66 ms + RTT (15 Hz tier stream) — accepted.
4. Ghost skid proxy during extrapolation can transiently read slip — 8° floor +
   0.09 s onset hold should silence it; gate on extrapolation if not.
5. Width source: BotPath picks the longest spline; if it's narrower than the
   visual road, bots run conservative — correct failure direction.

---
# Arcade pass 3 — play-test pass + follow-ups 1-4 (2026-07-26/27)

**Archived with every play-test checklist still UNDRIVEN.** The `- [ ]` items
below were never driven; the pass was superseded by the LAN-visual-parity /
bot-racing-line plan before a play-test session happened. They remain valid
things to check.

# Arcade pass 3 — play-test pass

The build work is done and archived. **Arcade pass 3** — area-denial items,
kart-feel mechanics, the reverse fix, keyboard throttle shaping, assist presets,
LAN protocol v5, rebindable keys and the shared settings panel — is in
[`Docs/plan-archive.md`](../../../EE%20Projects/Tiny_Torque/Docs/plan-archive.md)
as the newest entry, with every step's deviations recorded.

What survives here is the part a compiler cannot answer.

## What is verified

- **Every step compiles clean** — 13 of 13, editor closed, 0 `error CS`.
- **The Opus mission is bit-identical to the R4 reference row** after M1, after
  M3 and again at the end: leg A −13.6156 mm, turn +0.1874°, leg B +15.8038 mm,
  brake +42.3414 mm, total +58.1455 mm, drift −42.4042 mm, `completed: true`,
  `fault: 0`. `CarVehicle.cs` and `MotorPart.cs` are both in the diff, so this is
  the load-bearing check rather than a formality.
- **`TrackPresetValidator.Report`: 12/12 PASS**, no failures.
- **Standalone release player rebuilt** at
  `UnitySim/Builds/Release/AI Hardware Control Sim.exe` — required, because
  protocol v5 refuses a v4 client.

## What is NOT verified — the play-test list

**Nothing in this pass has been driven.** Compile-clean plus an unmoved Opus
mission proves the physics is safe; it says nothing about whether any of it feels
right. In rough order of how likely each is to need a number changed:

- [ ] **Smoke balance.** 0.75 m radius, 9 s, on a 100–140 m RC circuit. The risk
      is that it is unavoidable rather than un-fun. `MaxHazardsPerPlayer` is 1;
      tune `SmokeRadius` and `SmokeLifetime` first.
- [ ] **The drift, which is now latched rather than detected** (see below). Entry
      is handbrake + >3.5 m/s + >0.30 of steering, so the old "can a keyboard even
      reach the slip threshold" question is gone — but four new ones replace it,
      listed in their own section.
- [ ] **The reverse blip.** From 8 m/s, hold S: brake → stop → reverse on that
      one press. At speed S must still just brake and not blip.
- [ ] **Oil slick:** grip visibly drops inside, recovers on exit, never a wall.
- [ ] **Blinded bot into a wall** — it should sit there for the blind duration
      and resume, and specifically must NOT respawn out of the cloud.
- [ ] **Look-back framing** — does the mirrored offset actually show the car
      behind, or the sky?
- [ ] **Assists.** A plain (non-arcade) race now has Standard assists, which is a
      deliberate feel change for every existing install. Full should read as a
      well-sorted touring car, not as being on rails. Arcade sessions must feel
      exactly as they do today (the identity-at-floor rule).
- [ ] **Throttle:** holding W ramps in ~0.45 s; the Options slider at 0 restores
      today's instant step; gamepad triggers unchanged.
- [ ] **The pause panel at 1920×1080 and in a small editor game view.** It gained
      `GarageSkin`, which changes every metric, and it already overflowed before
      that. The body scrolls now, so the failure should be ugly rather than
      invisible — but eyeball it.
- [ ] **Rebinding end to end:** rebind a key, drive with it, restart the game and
      confirm it persisted; Reset-to-defaults restores WASD; the Arrows layout
      works; Escape still pauses after binding Pause elsewhere.
- [ ] **LAN, two machines** (editor host + the rebuilt standalone): smoke and
      slick appear on both, blindness triggers on the same car on both screens,
      the settings panel is reachable from `LanSessionMenu`, and a v4 client is
      refused with a version mismatch.
- [ ] **Regression by hand:** split-screen (per-viewport tint, independent
      reverse), garage, builder, snapshots, the diff-drive scene, a firmware
      session.

## Follow-up 4: arcade grip, drift smoke, tyre voice (2026-07-26)

Three play-test items: arcade spins out too easily even at Full assists (boost
pads, hard launches), drifts should pour car-coloured tire smoke, and the skid
sound is repetitive and trigger-happy.

1. **Anti-spin.** Arcade now pins every car to Full assists (floor was
   .80/.70/.90/.90), `HandlingGripBonus` 1.25 → 1.45, and a new
   `CarVehicle.arcadeStabilityMult` (neutral 1) gives the ESC **3×** gain and
   clamp in arcade — the sim-sized 0.75 N·m cap loses against pad/boost forces
   that shove the body without going through the tyres. Stood down to 1 during
   drift, spin-out and wreck, so those mechanics are untouched.
2. **Drift smoke.** New `Arcade/DriftSmoke.cs`: pooled world-space puffs at the
   rear wheels, tinted `Lerp(bodyColor, white, 0.4)`, emitting only while the
   slide is held; the component persists on the car so the trail fades out
   after release. Shares the hazards' alpha material via
   `ArcadeVfx.DriftSmokeSkin`.
3. **Tyre voice.** `VehicleAudio`: squeal opens only after slip holds past a
   0.10 deadband for 0.09 s at >1.2 m/s road speed; volume scales to full at
   4.5 m/s; swells in (6/s), cuts fast (14/s); per-car pitch offset
   (0.93–1.08) plus slow Perlin wander on level and pitch.
   `ProceduralAudio.BuildSkid`: loop 0.7 → 1.6 s, detuned squeal pair (~3 Hz
   beat) + ~2 Hz swell, all whole-cycle so the wrap stays seamless.

Compiles clean (0 `error CS`); **Opus bit-identical to R4** with `CarVehicle.cs`
in the diff (legA −13.615608, turn +0.187378, total +58.145523, drift
−42.404175, fault 0).

- [ ] **Can you still spin out?** Boost pad mid-corner and a 100 % launch from
      standstill — the two reported cases. If it still goes,
      `HandlingStabilityBoost` (3) and `HandlingGripBonus` (1.45) are the knobs.
- [ ] **Is it now too planted?** The car should still lean on its tyres, not
      rail-ride. If corners feel glued, drop the boost before the grip.
- [ ] **Does the drift still work at the new grip?** Entry, angle band and the
      carry were tuned at 1.25 grip; the drift multiplies 0.70 into 1.45 now
      (≈1.02 effective vs ≈0.88 before). If slides refuse to hold, lower
      `DriftGripMult`.
- [ ] **Spin-outs and wrecks still read as hits** — the stability stand-down
      should keep the banana/missile rotation exactly as it was.
- [ ] **Steering feel at speed**: noticeably calmer, still enough lock for the
      hairpins.
- [ ] **Smoke**: colour reads as the car's, trail sits where the wheels were,
      fades after release, never visible to a car's own CameraSensor, and pausing
      holds it rather than eating it.
- [ ] **Tyre sound**: no more chirp spam on clean cornering; a held slide
      swells, wanders and dies promptly on grip; two bots sliding sound like two
      cars; the oil-drop one-shot (same clip) is not now comically long.

## Follow-up 3: pad pins and respawn (2026-07-27)

Two play-test bugs, both about being stuck.

1. **Boost pads pinned you against walls.** The pad pushed along `transform.forward`
   unconditionally while a wheel was on it, out-torquing reverse and holding the
   car too straight to steer off. New pin latch in `CarVehicle`: on a pad and
   below `PadPinSpeed = 1.0` m/s for `PadPinSeconds = 0.7`, the pad stands down
   until forward speed passes `PadFreeSpeed = 1.6`. Pads are accumulated into a
   separate `padBoost` and maxed into `boost` afterwards, so item boost, drift
   carry and slipstream are untouched. The speed test is signed, not absolute —
   otherwise reversing off would re-arm the pad and shove you back in.
2. **Respawn went to the start line.** New `Core/TrackRespawn` (spine + surface
   drop, composed by `TrackBootstrap` at all three scene-build sites) and
   `CarVehicle.ResetVehicleTo(pos, rot)`. `ResetVehicle()` still exists and still
   goes to spawn, so `SimulationRunner` and the mission harness are unchanged.

Compiles clean (0 `error CS`); **Opus bit-identical to R4** with `CarVehicle.cs`
in the diff.

- [ ] **Pin escape:** drive nose-first into a wall on a pad. Should free itself in
      well under a second and let you reverse and steer out.
- [ ] **Pad still boosts normally** at speed, and crawling onto one from a
      standstill in open track still gets the shove (the 0.7 s should never
      elapse there, because the pad accelerates you past 1.0 m/s first).
- [ ] **Respawn placement** on each of the four circuits, especially Neon Vortex
      II's bridge and Workshop's plank — must land ON the elevated surface.
- [ ] **Respawn facing** on a hairpin: `_respawnHint` should keep it on the side
      the car came down, not snap across the corner.
- [ ] **A bot's stuck recovery** now uses the same path — confirm a wedged bot
      rejoins near where it was rather than at the start line.
- [ ] **LAN:** a client respawning should snap on the host's screen too
      (`OwnStateSender` bumps the epoch off `VehicleReset`, which the new path
      still raises).

## Follow-up 2: making the drift carry (2026-07-27)

Second round of play-test feedback — "still feels a bit off", with a Mario-Kart
reference spec. Three structural gaps against it, fixed:

1. **The handbrake was still braking the rear axle for the whole slide.** New
   `CarVehicle.arcadeHandbrakeMult` (neutral 1) drops it to `DriftHandbrakeMult
   = 0.25` while committed. This is the big one: no carry acceleration can
   outrun a locked rear axle, so the arc died and the exit was slow, which is
   most of what "off" meant.
2. **Charge was a stopwatch.** It now scales with the same stick axis that picks
   the angle — `DriftChargeInto = 1.5` at full lock in, `DriftChargeOut = 0.35`
   on full counter-steer. Tier gates moved to 0.9 / 1.9 / 3.0 charge (≈0.6 /
   1.3 / 2.0 s at full commitment), meter full-scale 3.5.
3. **The exit did not pop.** `DriftExitImpulse = 0.55` N·s per tier along the
   nose, through the CoM via a new parameterless `CarVehicle.ArcadeImpulse`
   overload. The positioned overload would have somersaulted the car — a
   horizontal push on a few centimetres of CoM lever against ~0.01 kg·m² of
   pitch inertia is hundreds of deg/s.

Also `DriftCarryTopSpeed` 8.5 → 10 with an explicit `DriftCarryFadeBand = 2.5`:
several designs cruise past 8.5, so the carry was fading to zero at exactly the
speeds a corner is taken at.

**Compiles clean (0 `error CS`). The Opus regression has NOT been re-run** —
`CarVehicle.cs` is in the diff, so it is still owed. Three attempts failed on a
broken editor install (`mono-2.0-bdwgc.dll` then `Unity.dll` failing to load)
while a `UnityHubSetup-3.19.5-x64` installer was running. Re-run once that is
done, and delete `UnitySim/Temp/UnityLockfile` first if a crashed batch run left
one behind.

- [ ] **Re-run the Opus mission** and confirm it is still bit-identical to R4.
- [ ] **Does the drift keep its speed now?** The whole point of change 1. Compare
      a drifted corner against the same corner driven normally — drifting should
      be roughly even, never slower.
- [ ] **Charge-rate coupling.** Does leaning in vs counter-steering visibly change
      how fast the meter fills? `DriftChargeInto/Out` are the knobs.
- [ ] **The exit impulse.** Should read as a kick, not a teleport, and must not
      pitch the car — if the nose moves at all, the CoM overload is wrong.
- [ ] **Is the rear still loose enough?** With the brake mostly gone, the slide is
      now carried by `DriftGripMult = 0.70` and the yaw controller alone. If the
      car grips up and refuses to hold an angle, lower the grip multiplier before
      raising the handbrake back.
- [ ] **Tier timing.** 0.6 / 1.3 / 2.0 s at full commitment — reachable in the
      corners these tracks actually have?

## Follow-up: the intentional drift (2026-07-27)

Play-test feedback asked for the drift to be something you *do*. It was rebuilt
from a detector into a latched state machine: turning while pulling the handbrake
commits the car to a slide in that direction, the angle is steerable between 11°
and 34°, a carry acceleration keeps the arc from scrubbing to a halt, release
straightens the car out and pays the mini-turbo, and a charge meter shows the
tiers filling. Compiles clean; **Opus is bit-identical to R4 again** with
`CarVehicle.cs` in the diff. Everything below is still undriven.

- [ ] **Entry threshold.** `DriftEntrySteer = 0.30` against the keyboard's own
      smoothed steering ramp. Too low and braking in a straight line latches a
      drift by accident; too high and a gentle corner will not commit.
- [ ] **The kick.** `DriftYawKick = 0.95 N·m` for 0.28 s. Should read as the car
      *setting* itself, not as being hit — the spin-out's 1.2 N·m is the ceiling
      it must stay under, and it should feel clearly gentler than one.
- [ ] **Is the angle steerable, or does it sit at one radius?** This is the whole
      mechanic. `DriftYawGain`/`DriftAngleMin/MaxDeg` are the knobs.
- [ ] **Does the arc carry?** `DriftCarryAccel = 4.5` capped at 8.5 m/s. A drift
      should not be strictly slower than not drifting, and must not be faster.
- [ ] **The exit straighten** — does the car come out pointing down the road with
      the boost lit, or still sideways?
- [ ] **The assist stand-down** (`DriftAssistMult = 0.20`). At Standard and at
      Full, does the drift still work? That multiplier is the only reason it can.
      Also confirm the assists come *back*: a car that stays loose after the drift
      means `arcadeAssistMult` is not being re-asserted.
- [ ] **The hop** (`DriftHopImpulse = 1.1 N·s`) on a light and on a heavy design —
      it is a fixed impulse, so a very light car hops higher. Must not launch.
- [ ] **The charge meter** at 1920×1080 and in a split-screen half.
- [ ] **LAN:** a client's own mini-turbo now pays into `driftBoostUntil`, which the
      host's sync no longer stomps — this was broken before and is worth
      confirming from the client seat. The host will not *see* a client's drift
      boost trail; that is cosmetic and known.

## Known risks carried forward

1. **Protocol bump.** Every machine must run the same build. Handled by the
   `hello.ver` equality check, but it is the largest blast radius in the pass.
2. **Assist default moved to Standard** for every existing install. Deliberate;
   Options ▸ Preset ▸ Off restores the old behaviour exactly.
3. **`_setpoints[0]` is now shaped** by the throttle smoother, so `car_pid` and
   `car_sensors` see a smoothed operator dial. Opus is provably unaffected
   (`opus_main.c:47` ignores setpoints entirely).

---
# Arcade pass 3 — area-denial items, kart-feel mechanics, and a keyboard overhaul

## Context

Play-test feedback, one feature request and three control complaints:

1. **A Smoke Cloud power-up** — "a giant fart cloud" you drop behind you that blocks
   the view (green screen tint on the victim) and makes bots drive straight instead
   of following the racing line.
2. **Reverse often doesn't work on keyboard** — you have to press S several times
   before the car moves.
3. **Keyboard driving is very hard**; wants throttle ramping ("acceleration/gearing")
   and stronger, more numerous assists.
4. **Settings should be reachable in-game**, not only from the main menu — and,
   confirmed in planning, that includes **rebindable keys with default/custom layouts**.

Confirmed with the user: build **all four** suggested extras as well — **drift boost
(mini-turbo)**, **rear-view look-back**, **slipstream/draft**, and an **oil slick**
power-up; assists default to **Standard on** in every session type; **LAN is in scope**
(so `ProtocolVersion` 4 → 5 and the standalone client must be rebuilt — the same rebuild
already owed from the owner-authoritative LAN work).

Two root causes were confirmed in code during planning, and both are worth stating
because they change what the fix is:

- **The reverse bug is the ESC lockout, not the input.** `MotorPart.cs:159` resets
  `_neutralTime = 0` on *any* non-zero command — including while a reverse command is
  acting as a brake. So: drive forward (`_reverseArmed = false` at `:173`) → hold S →
  brake branch → car stops → falls into the "ESC holds neutral" branch at `:177-182`
  **and stays there while S is held**. The player must release for ~170 ms (ESC lag +
  the 150 ms `escReverseLockMs` dwell) and press again. Tapping faster never works.
  **The same bug hits bots**: `BotDriver`'s stuck-recovery sets `_throttle = -0.6f`
  (`BotDriver.cs:201`) after driving forward, its `freed` check needs `|v| > 0.8f`, and
  the ESC never lets it move — which is why bots escalate to a respawn (`:229`).
- **Assists are off outside Arcade.** `ApplyArcadeHandling` (`TrackBootstrap.cs:184-200`)
  is only called from `BuildArcade` (`:165`). Every other session runs the raw
  `p1Assist*` values, which **default to 0**. Plain races have no assists at all.

The load-bearing constraint throughout: **the Opus mission regression must not move.**
`SimulationRunner.ControlStep` calls `ReadManualCommands` only when
`Mode == DriveMode.Manual` (`:408-411`), so the input layer is provably invisible to
firmware — which is why the reverse fix belongs there and not in the ESC.

---

## Part 1 — Two area-denial power-ups

`ItemKind.SmokeCloud = 6`, `ItemKind.OilSlick = 7` (append-only — the byte goes on the
wire). They share one deployable + containment-poll mechanism; the slick is nearly free
once the cloud exists.

**The deployables carry no collider at all.** Hits are resolved by a director-side
distance poll in `UpdateAreaHazards()`, called from `Update` beside `ExpireBananas`.
This is deliberate and solves three problems the trigger route creates:

- The car root `BoxCollider` **and** all four `WheelCollider`s are children of one
  transform, so `OnTriggerEnter` fires several times per car (documented at
  `ArcadeItemBox.cs:11-15`). The box dodges this by disabling on first touch and the
  banana by dying; a *persistent* area can do neither.
- Re-arming the effect while you sit inside needs `OnTriggerStay`, which stops firing
  when a stationary car's Rigidbody sleeps.
- LAN host followers are kinematic and stream-posed (`HostCarFollower.cs:70`); a
  distance test is unambiguous where kinematic-vs-kinematic triggers are not.

Cost is ≤8 racers × ≤16 hazards of `sqrMagnitude` per frame. It also means the cloud
can *never* become a wall — there is nothing to collide with.

**New files:** `Arcade/AreaHazard.cs` (gameplay identity: `objId`, `ownerSlot`,
`ownerCar`, `droppedAt`, `expiresAt`, `kind`, and a `Radius` that grows in over
`SmokeGrowSeconds` so an unexpanded puff can't blind from two car-lengths away),
`Arcade/AreaHazardViz.cs` (animation only — growth, drift, churn, fade — modelled on
`ArcadeBurst.cs`, using one shared `MaterialPropertyBlock` on a cached `Renderer[]` so
N clouds don't leak N materials).

**Effects** live as absolute-`Clock` deadlines on `ArcadeRacer`: `blindUntil` +
`blindStartedAt`, `slickUntil`. All must be reset in `ClearAll()` (`:136-153`).
Re-armed every frame while inside, so *leaving* the hazard starts recovery. The rising
edge fires `ShowHit("SMOKED!" / "OIL SLICK!")` and raises the event exactly once.

- **Blind (human)** is *pull*-based — `ArcadeFeedback` reads `blindUntil` at draw time,
  so nothing goes in the director's per-frame loop and it is automatically correct in
  split-screen and on LAN clients.
- **Blind (bot)** is *pushed* below the `DrivesPhysics` gate at `ApplyEffects:968`,
  because control only exists where the car is simulated.
- **Slick** multiplies into the neutral-branch `arcadeGripMult` write at `:986-988`
  — it must be re-asserted every frame there, not written once, or `ApplyEffects`
  stomps it the next frame.

**Green tint** — a new `DrawBlind` in `ArcadeFeedback`, called *first* in `Draw` (`:48`)
so the hit flash and banner stay legible on top. Unlike `DrawFlash`'s linear decay this
is **hold-then-fade** (`BlindTintAlpha = 0.62` vs the flash's 0.30) — a flash is a punch
you already took, this is a state you are in and has to actually cost you the corner.
The envelope is anchored to `blindStartedAt`, **not** `blindUntil - clock`, because on a
LAN client `blindUntil` is refreshed to `clock + 0.25` by every sync packet and a
remaining-time envelope would sit permanently mid-fade. Both HUD callers
(`ArcadeHud.cs:47` full-screen, `SplitScreenHud.cs:77` per-viewport) get it with **no
signature change and no edit to either file**.

**Bot blinding** — `public float BlindUntil` on `BotDriver`, in the `SpeedScale` style
(`:71`): a settable field, **never a constructor argument**, because `MenuAttract.cs:94`
also builds a `BotDriver` and must keep compiling. The branch goes in `Compute`
immediately after the `_car.Frozen` early-out (`:147-154`) and *before* the `_reversing`
block (`:198`), and returns early — so the aim/offset/pure-pursuit block is skipped
entirely. Steer decays toward centre over ~0.5 s (not an instant zero, which snaps
straight and reads as a magic correction; not a latch of the last steer, which makes a
bot blinded mid-corner carve a perfect circle). It must also zero `_stuckTimer`,
`_reversing`, `_reverseTimer` and `_respawnLatch`, exactly as the `Frozen` block does —
**otherwise a blinded bot that noses a wall respawns itself out of the cloud**, which is
a free escape and a teleport nobody asked for.

**VFX** — a `SmokeSkin` material: the `BurstSkin` recipe (`ArcadeVfx.cs:56-75`) with
`_DstBlend = OneMinusSrcAlpha` instead of `One`, and no emission. That one constant is
the whole difference between a cloud and a fireball — additive can only *add* light, so
additive "smoke" glows; smoke has to *occlude*. Eight offset spheres of varying size at
**fixed** (not random) offsets, so host and clients render the same cloud. Root on
`PartVisualFactory.VizLayer` so on-car `CameraSensor`s aren't blinded — note the
deliberate asymmetry with `DropBanana`, which builds at layer 0; a 1.5 m opaque blob in
front of a camera sensor is a different proposition from a peel. Authored-FBX path via
`ArcadeVfx.TryMesh("arc_smoke", …)` first, so dropping a mesh in later needs no code.

**Audio** — `ProceduralAudio.Smoke`: a one-shot (not a loop, so `LoopFade` isn't
involved). Very low `LowPass` cutoff (~0.06 vs `BuildBoost`'s 0.35) is what makes it
read as breath rather than steam; one *low*-Q resonator gives body without pitch (the
opposite of `BuildSkid`, which needs a pitch centre for a squeal); slow envelope attack
(~0.18 vs `BuildImpact`'s 0.005) is the difference between a pressure release and a
gunshot.

**Tables that must extend** (each is a silent failure if missed):
`ArcadeConfig.cs:221-223` Lead/Mid/Back weight arrays → **8 elements each**;
`ArcadeDirector.cs:917`'s hardcoded `% 5` → `% ArcadeConfig.RouletteFaceCount` (new
const, so item 9 is a one-line change); `DisplayName` (`:251`); the `Use` switch
(`:368-388`); `ShouldBotUse` (`:831-856`); `ArcadeEventKind` (append from 15).

Shields deliberately do **not** block either hazard — a shield absorbs one *hit*, and a
sight or grip impairment isn't a hit. This gives both items a role Shield doesn't
already cover.

## Part 2 — Kart-feel mechanics

- **Drift boost (mini-turbo).** Charge while the handbrake is held above
  `DriftMinSpeed` with a lateral slip angle past `DriftMinAngle`; release grants a boost
  scaled by tier (~0.7 s / 1.5 s / 2.4 s → blue / orange / purple, sparks tinted by
  tier). Lives in `ArcadeDirector.ApplyEffects` below the `DrivesPhysics` gate and feeds
  the **existing** `arcadeBoostAccel` channel, so it inherits the `BoostTopSpeed` fade
  and adds no new physics path. Needs one new read-only getter on `CarVehicle`
  (`public bool Handbraking => _handbrake;`). This is the biggest keyboard-feel win in
  the batch: it makes the slide the point instead of the failure.
- **Slipstream.** At the existing 5 Hz position tick, a racer within `DraftRange` behind
  another and inside a heading cone gets a modest draft accel, maxed into the same boost
  channel. Systemic catch-up that isn't an item.
- **Rear-view look-back.** `ChaseCamera` gains `public bool lookBack` — mirror the
  offset's Z and the look target. Needs a 7th member on `IDriverInputSource`
  (`bool LookBackHeld()`), which means touching all five implementations
  (`PlayerInputSource`, `BotDriver` → false, `NetworkInputSource`, `GatedInputSource`)
  — the exact pattern `UseItemPressed()` already established. `CarInput` gains a
  `chase` reference wired in `TrackBootstrap`.

## Part 3 — The reverse bug

**Fix at the input layer, not in the ESC.** The state machine is physically correct and
the Opus mission's brake calibration (task #126) depends on it byte-for-byte. The
alternative — a `MotorParams.escReverseAtRestMs` field with a 0=legacy sentinel — has
illusory safety: the fix is only *felt* once presets set it non-zero, and the moment
they do, Opus Vector's braking moves and `SENSOR_MOTOR` reports different numbers to
firmware. That's a physics change smuggled in as a bug fix. Shelve it.

Instead, automate the neutral blip the player is currently performing by hand:

- **`Sensors/MotorPart.cs`** — one read-only `public bool ReverseArmed => _reverseArmed;`.
  Nothing inside the class reads it; the state machine is untouched. This is needed
  because the observable symptom ("commanded reverse, car isn't moving") is
  indistinguishable from "reversing into a wall" and from "reverse already works at
  0.05 m/s" — any heuristic without it introduces a new, subtler bug.
- **`Core/CarInput.cs`** — a 3-state `ShapeReverse(throttle)` (`Idle → Blipping →
  Passthrough`) wrapping `source.Throttle()` at `:80`. It engages only when the driver
  wants reverse, `|ForwardSpeed| < 0.25 m/s`, and **no motor reports `ReverseArmed`** —
  so it can never cancel a legitimate brake (holding S at speed still runs the
  proportional brake branch, which is correct), and rolling backwards down a hill is
  already handled because `MotorPart.cs:171` accepts reverse when `wheelOmega < -2`
  regardless of arming. Blip duration is read from the car's own
  `escReverseLockMs` + a lag margin, because `GarageUI` exposes that on a slider and a
  hardcoded 200 ms would silently fail on a tuned ESC.

No oscillation: once reverse engages and `|v| > 0.25` the state returns to `Idle`; if
the car then hits a wall with S still held, `_reverseArmed` is still true (only cleared
by `v > 0`), so the gate fails and no second blip fires. Per-`CarInput` state, so
split-screen players are independent. **Fixes bots for free** through the shared
`IDriverInputSource` seam.

## Part 4 — Keyboard throttle shaping

New `Core/ThrottleSmoother.cs`, cloned from `SteerSmoother` — same `Step(target, now)`
signature, same `Time.time` integration, same `towardCenter` predicate (copy it verbatim
rather than inventing one, so stabbing S from full throttle passes through zero at the
*release* rate and braking doesn't feel delayed).

Rise ~2.2/s (≈0.45 s to full — deliberately ~2.5× slower than steering's 5.5: steering
must be quick to be usable, throttle must not), release ~6.0/s, and **reverse rises
faster** (~3.5/s) since it's a short deliberate manoeuvre, not something you modulate.

Applied at exactly two sites, **keyboard only**: `InputReader.Throttle()` (split into
`ThrottleAnalog()` / `ThrottleDigitalRaw()` mirroring the existing `Steer()` shape) and
`PlayerInputSource`'s `case InputDeviceKind.Keyboard`. New
`GameSettings.kbThrottleSmoothing = 1f` (field initializer, matching how
`kbSteerSmoothing` shipped).

Untouched and verified: `BotDriver.Throttle()` returns its own `_throttle`;
`NetworkInputSource` carries a float already shaped on the sender; gamepad triggers stay
raw (shaping analog *removes* fidelity); firmware never calls this path. **One
consequence to name:** `CarInput.Update` derives `_setpoints[0]` from
`source.Throttle()`, so the operator target-speed dial seen by Autonomous controllers is
now shaped. Opus is provably unaffected (`opus_main.c:47` ignores setpoints entirely);
`car_pid`/`car_sensors` see a smoothed dial, which is an improvement — but it is a
behaviour change on the demo path and goes in the release note.

## Part 5 — Assists

**(i) Presets.** `GameSettings.assistPreset = 1` (initializer is 1, not 0, deliberately:
an existing `settings.json` has zeroed `p1Assist*` fields, so without this an upgrading
player keeps getting nothing and never finds the feature). `SessionConfig` gains
`AssistPreset { Off, Standard, Full, Custom }` and `PresetValues(p)` — Standard =
steer .45 / stability .50 / traction .60 / abs .60. Reroute `P1Assists`/`P2Assists`
(`SessionConfig.cs:122-134`) through it. **That's the architectural win**: those two
methods are the single choke point every entry path already funnels through
(`MenuUI:259/510/591`, `NetSession:204`, `ResolvePlayers:116`), so the preset reaches
every session type with zero edits to those files. Moving any slider sets the preset to
`Custom` in the same `changed` handler, so sliders stay live and the preset is a
shortcut, not a cage.

**(ii) A universal floor.** New `ApplyAssistFloor(rig)` in `TrackBootstrap`, called
right after `built.car.assists = slot.assists` (`:733`), max-ing in the saved preset for
**local human** rigs in *every* session type. Skips bots (they keep their own line) and
skips `DriveControl.Firmware` explicitly — mirroring `ArcadeDirector.Register`'s refusal
at `:167`, and not leaning on `assistsActive` alone, which would be fragile if a
firmware rig were ever mode-toggled. It lives here rather than in the menu path for the
same reason `ApplyArcadeHandling` does: snapshot resume rebuilds a roster without ever
visiting the menu, and the LAN host builds assists directly at `:304`.

**(iii) A stronger top end, bounded by construction.** Governing rule:

> **Every strengthened assist is the identity function at and below the Arcade floor
> (steer .80 / stability .70 / traction .90 / abs .90), and only gains authority above it.**

That preserves the Arcade tuning mechanically rather than by re-deriving numbers. Done
in two steps so it's a data change rather than surgery: first extract today's magic
numbers into `Vehicles/AssistTuning.cs` with **byte-identical values** (`CarVehicle.cs`
`:1030`, `:1036`, `:1203-1208`, `:1290`), then three targeted moves:

1. **Stability clamp is the real ceiling** — at stability 1.0 the ESC torque is clamped
   to ±0.30 N·m, against `ArcadeConfig.SpinTorque = 1.2f` which had to beat ~2 N·m of
   tyre resisting moment. 0.30 is barely a nudge. Ramp to 0.75 over `[0.70, 1.0]`.
2. Steering limiter reference speed 4 → 2.5 over `[0.80, 1.0]`.
3. Traction onset 0.25 → 0.12 over `[0.90, 1.0]`; ABS likewise.

At Full this should read as a well-set-up touring car, **not as being on rails** — the
tyre model still decides whether you make the corner and top speed is untouched.
Explicitly deferred: an understeer-limited yaw-rate term. It is the biggest "car goes
where I point it" lever and also a genuine new force term, so it gets argued on its own.

**(iv) Live re-application.** New `Core/AssistApplier.ApplyLive(rigs)` — `TrackBootstrap`
`:733` is otherwise the only write, so a slider moved mid-session does nothing until the
next scene load. Counts **local human** rigs only when mapping P1/P2 (indexing the whole
list would map P2's sliders onto the first bot in a bot race). `SettingsStore.Apply()`
is deliberately *not* taught about assists — those are per-rig scene state, not engine
state.

## Part 6 — Rebindable keys

Today every binding is hardcoded twice — Input System (`kb.wKey`) and legacy
(`KeyCode.W`) — in both `InputReader` and `PlayerInputSource`. The rebind layer is a
canonical table both backends resolve through.

- **New `Core/KeyBindings.cs`** — `[Serializable] class KeyBindings` with one **int
  field per action** holding a `KeyCode` (canonical: stable, and `ToString()` already
  gives display names like `LeftShift`), each with a field initializer equal to today's
  binding. JsonUtility can't serialize a Dictionary, and named fields also give the
  project's standard back-compat for free. Plus `Core/KeyTable.cs`: a static
  `(KeyCode, Key, displayName)` table covering the ~60 bindable keys, which is both the
  backend bridge (`Keyboard.current[key]` for Input System, `Input.GetKey(keyCode)` for
  legacy) and the label source.
- **`InputReader`** gains generic `Held(KeyCode)` / `Pressed(KeyCode)` that dispatch
  through the table; every hardcoded read becomes a table lookup. `PlayerInputSource`'s
  Keyboard kind uses the same table — one physical keyboard, so one global binding set
  (two keyboard players already clash today and remain unsupported).
- **Bindable actions:** throttle ±, steer ±, brake, handbrake, respawn, use item, look
  back, mode toggle, pause. **Dev/overlay hotkeys stay fixed** (G graph, J metrics,
  K mission, P pause-graph, `[`/`]` window) — they're tools, not controls, and pinning
  them keeps the docs honest.
- **Layouts:** `WASD` (default), `Arrows`, `Custom`, plus a Reset-to-defaults button.
- **Gamepad:** a parallel int per action over a curated `PadButton` enum, same
  press-to-capture flow.
- **UI:** a KEYS section in the settings panel — click a row → "press a key…" capture
  (Input System scans `Keyboard.current.allKeys` for `wasPressedThisFrame`; legacy scans
  the table), Esc cancels, duplicates are flagged rather than silently swallowed.

## Part 7 — The in-game settings panel

`PauseMenu.DrawSettings()` already exists (`:207-245`) with three volume sliders and the
telemetry toggle. Three real defects to fix alongside extending it:

1. **It already overflows.** `h` is `_showSettings ? 360f : 290f` (`:83`) while the
   ten-button stack alone is ~350 px — `BeginArea` clips silently. Widen to ~440 px,
   raise `h`, and add a scroll view the same shape as `MenuUI._optionsScroll`
   (`:705`/`:792`).
2. **No skin.** `PauseMenu.OnGUI` never sets `GUI.skin`, while `ArcadeHud.cs:38` and
   `LanSessionMenu.cs:34` both do — and either can be on screen simultaneously. Add
   `GUI.skin = GarageSkin.Skin`. **This is the one visually risky edit**: GarageSkin's
   box/button metrics differ, so every fixed size needs retuning in the same step.
3. **No sharing.** Move `MenuUI.AssistSlider` (`:804-813`) to `GarageSkin.Slider01`
   (named to avoid colliding with `GarageUI`'s differently-shaped `Slider`), update
   MenuUI's six call sites, delete the private copy.

Factor the body into a static **`Core/SettingsPanel.Draw(rigs)`** that both menus call
from inside their own `BeginArea` — because **LAN replaces `PauseMenu` with
`LanSessionMenu` entirely** (`LanSessionMenu.cs:8-13`), so a pause-only panel is
invisible to LAN players. `PauseMenu.DrawSettings` becomes a one-line delegate;
`LanSessionMenu` gains a `Settings…` toggle and a `rigs` list wired by `TrackBootstrap`.

Contents: volumes, kb steer + throttle smoothing, assist preset + the four P1 sliders
(P2 only when `runners.Count > 1`), the key-binding section, and a **read-only** arcade
handling status line (it's consumed at rig build and can't be flipped mid-session). Not
mirrored: quality/fullscreen/vsync (need a reload) and noise seed / actuation delay (sim
knobs that must not move mid-run). The `changed` branch calls
`Apply(); Save(); AssistApplier.ApplyLive(rigs);`.

Worth noting in code: since protocol 4 each client simulates its own car, so a live
assist change in LAN applies to the car that machine actually simulates and needs no
wire update — a direct consequence of owner-authority.

## Part 8 — LAN

`ArcEffect : byte` (`NetMessages.cs:237-249`) has **all 8 bits used**. Widen to
`ushort` — four lines (underlying type, new flags at 256/512/1024, the `(byte)` write at
`:319`, the `out byte` read at `:331`) — costing 1 byte per racer per sync (~120 B/s at
a full grid) and restoring 8 bits of headroom. `ProtocolVersion` 4 → 5; the existing
`hello.ver` check at `:274` refuses a mismatched client cleanly.

Rejected: deriving blindness client-side from the streamed hazard position. The client
would test containment against **its own interpolated ghosts, ~60-120 ms behind the
host**, so the two machines would disagree about when the screen goes green, and the
human blind (client-decided) and bot blind (host-decided) would run off two different
sources of truth for one rule. **The host is authoritative for everything here** — it
owns hazard lifetime and bot behaviour, and has a good 60 Hz position for every car.

Also: `NetPack.ProjSmoke = 3` / `ProjSlick = 4`; `ArcadeNetLink.SpawnProjectileViz`
(`:288-294`) becomes a switch with an **honest default** — today an unknown kind renders
a *banana*, which is the worst possible failure (a real-looking hazard that doesn't
exist); `EffectsOf`, `OnSync` (blind taken unconditionally including for `LocalSlot`,
unlike penalized/warned), `Publish`, `OnRemoteEvent`.

---

## Files

**New:** `Arcade/AreaHazard.cs`, `Arcade/AreaHazardViz.cs`, `Core/ThrottleSmoother.cs`,
`Core/KeyBindings.cs`, `Core/KeyTable.cs`, `Core/SettingsPanel.cs`,
`Core/AssistApplier.cs`, `Vehicles/AssistTuning.cs`.

**Modified (principal):** `Arcade/ArcadeConfig.cs`, `Arcade/ArcadeDirector.cs`,
`Arcade/ArcadeRacer.cs`, `Arcade/ArcadeVfx.cs`, `Arcade/ArcadeFeedback.cs`,
`Arcade/ItemKind.cs`, `Arcade/ArcadeEvent.cs`, `Arcade/ArcadeAudio.cs`,
`Audio/ProceduralAudio.cs`, `Core/InputReader.cs`, `Core/PlayerInputSource.cs`,
`Core/CarInput.cs`, `Core/BotDriver.cs`, `Core/ChaseCamera.cs`, `Core/PauseMenu.cs`,
`Core/TrackBootstrap.cs`, `Core/SessionConfig.cs`, `Sensors/MotorPart.cs`,
`Vehicles/CarVehicle.cs`, `Persistence/GameSettings.cs`, `Menu/MenuUI.cs`,
`Garage/GarageSkin.cs`, `Net/NetMessages.cs`, `Net/NetSession.cs`,
`Net/ArcadeNetLink.cs`, `Net/LanSessionMenu.cs`, `README.md`.

**Reused rather than rewritten:** the `arcadeBoostAccel` / `arcadeGripMult` channels and
the every-frame re-assert in `ApplyEffects`; `ArcadeRacer.ShowHit`; `ArcadeFeedback`'s
two-caller design; `ArcadeBurst`'s self-destruct + property-block fade; `ArcadeVfx.Piece`
/ `TryMesh`; `SteerSmoother`'s shape and `towardCenter` predicate; `SessionConfig
.P1Assists/P2Assists` as the assist choke point; `ArcadeConfig.Roll`'s length-driven
loop; `MenuUI.AssistSlider`; the `IDriverInputSource` seam.

## Milestones

Tick a box when its headless batch compile is clean (editor closed, 0 `error CS`) and
its play-test note passes. Each milestone is independently shippable — stop after any
one of them and the game is in a coherent state.

### M0 — Archive the plan history · ✅ done

- [x] Moved **28** completed plans into git-tracked `Docs/plan-archive.md` (359 KB),
      newest-first, byte-identical — split with a script, not retyped.
- [x] Added `plan-archive` (reference) + `plan-file-one-plan-at-a-time` (feedback)
      memories, both indexed in `MEMORY.md`.
- [x] Trimmed this file to active work only: 393 KB → 32 KB, one top-level heading.

*From here on, one plan at a time in this file; finished plans move to the archive
rather than accumulating.*

### M1 — Arcade content · ✅ code complete, **not yet play-tested**

*Every step compiles clean and the Opus mission is unmoved, but nothing here has
been driven yet. The play-test list is under Verification: smoke, oil, drift,
slipstream, look-back, and the blinded-bot-into-a-wall case.*

- [x] **1. Data + constants, inert.** ✅ compile clean (0 `error CS`, rc 0).
      `SmokeCloud = 6` / `OilSlick = 7` + `ItemKindExt.IsAreaHazard`; `HazardDropped/
      Hit/Expired = 15..17` (one set for both hazards, told apart by `ArcadeEvent.item`
      — two sets would be three ways to say the same thing); `ArcadeConfig` hazard /
      blind / slick / drift / draft blocks + `RouletteFaceCount = 7`; `DisplayName`;
      `% 5` → `% RouletteFaceCount`; `ArcadeRacer.blindUntil` / `blindStartedAt` /
      `slickUntil` + `Blinded` / `OnSlick` + `ClearAll` resets.
      *Weight arrays still length 6 — the items stay unrollable until step 3.*
- [x] **2. VFX + audio.** ✅ compile clean (0 `error CS`, rc 0).
      `ArcadeVfx.AlphaSkin` + `SmokeSkin`/`SlickSkin`, `BuildSmoke`/`BuildSlick`
      (both authored at radius 1, fixed puff table — no random, so LAN machines
      build the same shape), `AreaHazardViz` (one shared property block; runs on
      `ArcadeDirector.Clock`, **not** `Time.time` like `ArcadeBurst`, because a
      9 s animation mirrors a gameplay deadline and a pause must hold both),
      `ProceduralAudio.Smoke` + switch arm + builder, `ArcadeAudio`
      `HazardDropped`/`HazardHit` cases (oil reuses `BananaDrop` + `Skid`).
      *Still inert — nothing spawns a viz until step 3.*
      *Deviation: built the slick's viz here too, so step 3 stays pure gameplay.*
- [x] **3. Hazard gameplay.** ✅ compile clean (0 `error CS`, rc 0).
      `AreaHazard` (no collider; grows; flat distance + `HazardVerticalBand` so a
      cloud on a bridge can't blind cars underneath), director `_hazards` +
      `DropHazard` + `OnHazardHit` + `RemoveHazard` + `UpdateAreaHazards` +
      `ResetArcade` teardown, `Use` + `ShouldBotUse` cases, weight arrays now 8
      (smoke/oil weighted toward the front of the field, like the banana).
      New consts: `HazardVerticalBand`, `SlickLingerSeconds`.
      *Two things worth remembering:* the hazard shares its GameObject with the
      viz, so a drifting cloud carries its effect with it instead of leaving an
      invisible trap behind; and `viz.selfDestruct` is cleared on director-owned
      hazards, because otherwise both would race to destroy the same object in a
      frame and the loser either orphans the hazard or swallows its expiry event.
      *→ Playable, minus tint and bot blind.*
- [x] **4. Blind + slick consumers.** ✅ compile clean (0 `error CS`, rc 0) **and
      the Opus mission regression passes unchanged**: leg A −13.6 mm, turn +0.19°,
      leg B +15.8 mm, brake +42.3 mm, total +58.1 mm, drift −42.4 mm,
      `completed: true, fault: 0` — the documented R4 reference row in
      `Opus_Car_Spec/calibration.md` to the millimetre.
      `ArcadeFeedback.DrawBlind` (hold-then-fade anchored to `blindStartedAt`,
      drawn first so the flash and banner stay legible over it — no signature
      change, so both HUD callers got it for free), `BotDriver.SetBlind` + the
      early-return branch, the director push below the `DrivesPhysics` gate,
      slick multiplied into the neutral `arcadeGripMult` write.
      *Deviation: `SetBlind(seconds, release, throttle)` instead of a public
      `BlindUntil` field, so the tuning stays in `ArcadeConfig` and `BotDriver`
      holds no arcade knowledge beyond the seam it already had for
      `RequestUseItem`. (Note for later: `CarInput.cs:52` already reaches into
      `Arcade.ArcadeDirector`, so Core→Arcade is not a boundary this preserved —
      it's a locality argument, not a layering one.)*
- [x] **5. Kart mechanics.** ✅ compile clean (0 `error CS`, rc 0).
      Drift boost: `CarVehicle.Handbraking` + `LateralSpeed`, `ArcadeRacer`
      drift state, `ArcadeVfx.BuildDriftSparks`, director `UpdateDrift` /
      `ShowDriftSparks` / `EndDrift` — charge needs handbrake **and** speed
      **and** slip angle (handbrake alone pays out for a straight-line stab, slip
      angle alone pays out for every fast corner), pays into the existing
      `arcadeBoostAccel` so it inherits the `BoostTopSpeed` fade, and a spin or
      wreck mid-drift drops the charge unpaid.
      Slipstream: `UpdateDraft` at the 5 Hz position tick, `r.draftAccel`
      re-applied per frame and **maxed** into the boost channel, not added.
      Look-back: `IDriverInputSource.LookBackHeld()` across all four
      implementations, `InputReader.LookBackHeld` (C / right-stick click — the
      last two free bindings), `ChaseCamera.lookBack` mirroring the offset Z so
      the existing follow lerp carries the whole transition, `CarInput.chase`
      bound inside the three `TrackBootstrap` camera builders (the camera already
      knows its car, which is the only thing that gets split-screen right).
      *Deviation: `GatedInputSource` does NOT gate look-back — a camera that
      refuses to move during the countdown just reads as a broken key.*
      *Old plan text below for reference:* Drift-boost charge/tiers/sparks, slipstream,
      `ChaseCamera.lookBack` + `IDriverInputSource.LookBackHeld()` across all five
      implementations + `CarInput.chase` wiring.

### M2 — LAN · ✅ code complete, **not yet tested on two machines**
*(the standalone client rebuild is still owed — it lands in M5)*

- [x] **6.** ✅ compile clean (0 `error CS`, rc 0). `ArcEffect` → `ushort` with
      `Slicked = 256`, `ProtocolVersion` 4 → 5, `ProjSmoke = 3`/`ProjSlick = 4`
      published from `director.Hazards`, `SpawnProjectileViz(in ArcProjState)` as
      a switch returning **null** on an unknown kind (with a once-per-kind
      warning) instead of today's banana, `EffectsOf` + `OnSync` + `OnRemoteEvent`.
      *Deviation, and the one real design decision in this step: blindness is
      NOT a flag bit. It goes on the wire as `blindLeftDs`, one byte of remaining
      time. `ArcadeFeedback.DrawBlind`'s fade term is `(blindUntil - clock) /
      BlindFadeSeconds`, so a bit re-armed to `clock + EffectHold` like every
      other effect would peg it at 0.25/0.9 forever — a client would have sat at
      28% of the intended alpha for the whole blinding, looking like it worked
      while not costing the corner the item exists to cost. One byte per racer
      (~120 B/s at a full grid, the same order as the ushort widening) buys the
      identical envelope on both machines and needs no change to `DrawBlind`.*
      *Two things worth remembering:* the `Slicked` flag is load-bearing rather
      than cosmetic — since protocol 4 a client simulates its own car, so it is
      the ONLY route by which oil reaches a human client's physics; and the
      client's hazard viz gets `selfDestruct = false` **and** `drift = zero`,
      because the stream owns the pose and a locally-drifting cloud would wander
      off the area actually catching people.

### M3 — Driving feel · ✅ code complete, **not yet play-tested**

*All four steps compile clean, and the Opus mission after step 10 is bit-identical
to the run after M1 and to the R4 reference row — leg A −13.6 mm, turn +0.19°,
brake +42.3 mm, total +58.1 mm, `fault: 0`. `CarVehicle` is in this diff, so that
result is the load-bearing check, not a formality.*

- [x] **7. Reverse fix.** `MotorPart.ReverseReady` + `ReverseBlipSeconds`
      (read-only, read by nothing inside the class — `StepDrive` is untouched),
      `CarInput.ShapeReverse` as a 3-state machine.
      *Deviations: named `ReverseReady`, not `ReverseArmed`, and it returns TRUE
      when the state machine is disabled — with no lockout there is nothing to
      wait for, and the caller wants "would reverse engage?", not "is the flag
      set?". Blip length comes from `ReverseBlipSeconds` on the motor rather than
      CarInput reading ESC fields, so a tuned `escReverseLockMs` from the garage
      slider still works. Applied ONLY in `ReadManualCommands`, not in the
      `Update` setpoint path — so the Autonomous target-speed dial is untouched
      and the fix is provably invisible to firmware.*
- [x] **8. Throttle shaping.** `ThrottleSmoother` (rise 2.2 / release 6.0 /
      reverse-rise 3.5, `towardCenter` copied verbatim so stabbing S from full
      throttle passes through zero at the release rate and braking stays crisp),
      `kbThrottleSmoothing = 1f`, `InputReader.Throttle` split into
      `ThrottleAnalog` + `ThrottleDigitalRaw`, `PlayerInputSource`'s exclusive
      keyboard case, one Options slider.
      *The legacy `SafeAxis("Vertical")` went on the DIGITAL side — that axis is
      keyboard-driven and already carries the Input Manager's own smoothing.*
- [x] **9. `AssistTuning` extraction — zero value change.** All twelve literals
      named and gathered; six call sites in `CarVehicle` now reference them.
- [x] **10. Assist presets + floor + strength.** `assistPreset = 1`,
      `AssistPreset`/`PresetValues` + rerouted `P1Assists`/`P2Assists` (the choke
      point every entry path already funnels through, so the preset reached every
      session type with no edits to the menu, LAN or resolver), `ApplyFloor` at
      both rig-build sites, the four identity-at-floor ramps, `AssistApplier
      .ApplyLive`, and a preset row in Options that flips to Custom on any slider.
      *Deviation: Full is steer .90 / stability .90 / traction .95 / abs .95, not
      the .85/.85/.90/.90 I first wrote — that version sat exactly ON the traction
      and ABS floors, so it would have got none of the new authority and felt
      identical to Arcade. Standard (.45/.50/.60/.60) is below every floor and is
      therefore pure old behaviour at a new level.*

### M4 — Settings + rebinding · ✅ code complete, **not yet play-tested**

- [x] **11. Key bindings.** ✅ compile clean (0 `error CS`, rc 0). `KeyTable`
      (canonical `KeyCode`, both backends resolve through it), `PadTable` +
      `PadButton`, `KeyBindings` on `GameSettings.keys`, `InputReader` and
      `PlayerInputSource` fully routed, WASD / Arrows layouts.
      *Deviations, all three load-bearing:*
      (a) *the four driving axes keep an ALT binding* — W-or-Up and A-or-Left both
      worked before, and quietly dropping that would read as rebinding having
      broken the controls;
      (b) *the legacy `Vertical`/`Horizontal` axes are GONE from the driving
      reads.* An Input Manager axis carries its own hardcoded key list, so it
      would have kept driving from W and the arrows whatever the player rebound —
      precisely the silent-stale-binding failure risk #3 names. `KeyTable`'s
      legacy path reads the bound keys directly instead;
      (c) *`PausePressed` accepts Escape unconditionally*, whatever `pause` is
      bound to. Pause is the only route to the screen that contains the bindings,
      so a player who bound it somewhere unreachable would otherwise have locked
      themselves out of the one place that could undo it.
      *Also: `PlayerInputSource`'s exclusive-Keyboard kind now CALLS the shared
      raw readers instead of naming keys a second time — the two copies of every
      binding read were exactly how one of them would go stale. Gamepad rebinding
      covers the digital actions only; throttle and steering stay on the triggers
      and the left stick, because binding an analog axis to a button is offering
      to make the controller worse.*
- [x] **12. Settings panel.** ✅ compile clean (0 `error CS`, rc 0).
      `GarageSkin.Slider01` (MenuUI's `AssistSlider` is now a one-line alias, so
      eleven call sites stayed put for a zero-behaviour-change move),
      `Core/SettingsPanel.Draw(rigs, height)`, `PauseMenu` gained
      `GUI.skin = GarageSkin.Skin` + new sizing + a whole-body scroll view + the
      one-line delegate, `LanSessionMenu` gained a `Settings…` toggle, a scroll
      view and a `rigs` list wired at both LAN bootstrap sites.
      *The overflow fix is the SCROLL VIEW, not the new height.* The old panel
      clipped because a ten-button stack is taller than 290 px; picking a bigger
      number would only move the cliff, so the body scrolls and the height merely
      has to be reasonable.
      *Rebind capture is polled on `EventType.Layout` only* — OnGUI runs several
      times per frame and IMGUI requires the control count to match between the
      Layout and Repaint passes, so the state change belongs in the pass that is
      allowed to change layout. *`SettingsPanel.Capturing` is checked by both
      menus before acting on their own Esc*, or cancelling a rebind would also
      close the menu you were rebinding from.
      *Conflicts are reported, not prevented:* deliberately putting two actions on
      one key is legitimate, and the only difference between that and a typo is
      whether it was meant.

### M5 — Docs + regression · ☐

- [ ] **13.** README (item table, assists, controls, protocol note), full Opus mission
      run, two-machine LAN smoke test, memory update.

## Verification

- **Physics unchanged (the gate):** run the Opus mission headless before and after and
  diff the result JSON. Use `-batchmode` **only, never `-nographics`** — the Opus rig
  carries a camera sensor and hard-crashes without a graphics device with a stack that
  looks like a physics fault. `TrackPresetValidator.Report` still `ALL PASS`.
- **Smoke:** drop one; it grows, drifts, fades, and is never solid — drive straight
  through it. Human victim gets a held green wash that fades; a bot victim stops
  following the line, drives straight, and **does not respawn** even if it noses a wall.
- **Oil slick:** grip visibly drops inside, recovers on exit; the slick is not a wall.
- **Reverse:** from 8 m/s, hold S — the car brakes, stops, and reverses **on that single
  press**. At speed S still brakes (doesn't blip). Bots wedged on a barrier now reverse
  out instead of teleporting.
- **Throttle:** holding W ramps in ~0.45 s; slider at 0 restores today's instant step;
  gamepad triggers unchanged; Autonomous unaffected.
- **Assists:** a plain (non-arcade) race now has Standard assists; Full is markedly
  harder to spin; Arcade sessions feel exactly as they do today (the identity-at-floor
  rule).
- **Drift:** handbrake through a corner → sparks tier up → release gives a real kick.
  Slipstream closes a gap on a straight. Look-back shows the car behind.
- **Settings:** Esc → Settings mid-race; sliders apply **immediately** to the car you're
  driving; nothing clips out of the panel; rebind a key, drive with it, restart the game
  and it persists; Reset-to-defaults restores WASD. Same panel reachable in a LAN
  session via `LanSessionMenu`.
- **LAN:** editor host + rebuilt standalone client — smoke and slick appear on both
  machines, blindness triggers on the same car on both screens, an old client is refused
  with a version mismatch.
- **Regression:** split-screen (per-viewport tint, independent reverse), garage,
  builder, snapshots, the diff-drive scene, and a firmware session all unchanged.

## Risks (ranked)

1. **Protocol bump.** Every machine must run the same build. Known and cleanly handled
   by the existing `hello.ver` check, but it is the largest blast radius here.
2. **`PauseMenu` gaining `GarageSkin`.** Changes the metrics of a panel that *already*
   overflows. Purely cosmetic but easy to leave looking broken — eyeball at 1920×1080
   and in a small editor game view.
3. **Key rebinding touches every input read.** A missed call site keeps a stale
   hardcoded key and silently ignores the rebind. Mitigated by making `InputReader` the
   only file that names a `KeyCode` and grepping for direct `kb.` / `KeyCode.` uses.
4. **Assist default moves to Standard** for every existing install — a deliberate feel
   change; name it in the release note.
5. **Smoke balance.** The poll costs nothing; the risk is a 0.75 m cloud on a narrow RC
   circuit being unavoidable and un-fun. Tune radius and duration first; keep
   `MaxSmokePerPlayer = 1`.
6. **Blinded bot into a wall** sits there for the blind duration, then resumes. Bounded,
   and strictly better than teleporting out of a cloud.
7. **`_setpoints[0]` shaping** is the one place Autonomous sees the throttle smoother.
   Opus is provably safe; `car_pid`/`car_sensors` see a smoothed dial.
8. **`MotorPart.cs` in the diff.** Zero behavioural risk (a property nothing reads), but
   it puts a regression-sensitive file in the changeset — call it out in the commit.

## What must NOT change

- **The Opus mission.** Nothing on the Autonomous path: the DLL branch of `ControlStep`
  (`:412-447`), `CtrlInputs`/`CtrlOutputs`, sensor sampling order, `NoiseModel` seeding.
  **The ESC state machine in `MotorPart.StepDrive` (`:126-189`) stays byte-identical** —
  that is the entire argument for fixing reverse at the input layer.
  `assistsActive = (Mode == Manual)` stays; `ArcadeDirector.Register` keeps refusing
  `DriveControl.Firmware` rigs (`:167`).
- **Firmware ABI.** No new `SensorType`, no change to the actuator convention
  (`[motor]=volts`, `[6]=steer`, `[7]=brake`), no header edit.
- **Non-arcade physics.** Step 9 is a pure rename at identical values; step 10's ramps
  are identity at and below the Arcade floor. `TyreModel`, `MotorModel`, suspension and
  aero untouched.
- **Append-only enums** — `ItemKind`, `ArcadeEventKind`, `SensorType`, `NetPack.Proj*`.
- **`MenuAttract.cs:94`** — no required constructor argument may be added to `BotDriver`.

---
# LAN latency — owner-authoritative own-car simulation + stream quick wins

## Context

First LAN play-test succeeded but the client's controls feel laggy. Measured from
code, a client keypress → on-screen response is **~170–240 ms** (host player:
~30–45 ms):

- **120 ms (73 %)** — the client runs **no car physics at all**; its own car is a
  `ClientCarView` ghost replayed `RenderDelay = 0.12` s behind the estimated host
  clock (`ClientCarView.cs:17`). The delay is correct for *other* players' cars;
  on your own it buys nothing.
- **~17 ms** — `ClientInputSender` samples input only at its 30 Hz send tick, and
  its `_accum = 0f` reset degrades the rate below 30 Hz at most frame rates
  (`ClientInputSender.cs:15,35-47`). The same accumulator bug sits in
  `NetSession.BroadcastState` (:639) and `ArcadeNetLink` (:76).
- **~17 ms** — interpolation smear across 33 ms snapshot spans (30 Hz stream).
- **~1 ms** — the actual LAN wire. The network itself is not the problem.

The user gathered the classic server-authoritative catalog (prediction,
reconciliation, input history). Half already exists (inputs-not-transforms,
unreliable streams / reliable events, remote interpolation at 120 ms, brief
extrapolation). The missing half — client prediction + rewind reconciliation —
is the shooter/Rocket-League model and is near-infeasible here:
`Physics.Simulate` is scene-global (cannot rewind one car), WheelCollider solver
state cannot be snapshotted (`RestoreState` restores 4 fields of dozens,
`CarVehicle.cs:905-916`), the sim is non-deterministic across machines (unsynced
`NoiseModel` seeds, ballooning `wc.radius` writes, collider-identity surface
lookups, host-only arcade RNG), and no tick numbers exist on the wire.

**Decision (user-confirmed): owner-authoritative own car** — what essentially
every shipped racing game does (Forza, Mario Kart, iRacing; Rocket League is the
exception and only because it runs its own deterministic Bullet physics). Each
machine simulates its own car locally (zero added control latency, no correction
snaps ever) and streams state; the host keeps authority over laps, race state,
item adjudication, and all randomness. Quick wins are bundled.

## Architecture

- **Owner (client)**: full physics rig for its OWN car — exactly the
  `BuildLanRig` shape (`TrackBootstrap.cs:271-328`): `previewKinematic:false`,
  `GatedInputSource(PlayerInputSource(Merged))`, `SimulationRunner` with
  bot-style flags, no controller DLL. `built.car.assists = p.assists` (:283)
  works verbatim — roster entries carry assists. Streams `aihw.ownstate` C→H at
  60 Hz. Performs its own teleports (R respawn, wreck recovery, race grid)
  locally and bumps a **per-car epoch byte** so everyone else snaps.
- **Host**: its own car unchanged (still ~30–45 ms feel). Each remote slot
  becomes a kinematic **`HostCarFollower`** — interpolates received owner states
  and `MovePosition/MoveRotation`s each FixedUpdate (at 400 Hz a fixed step is
  ~2.8 cm at 11 m/s, far under the 0.25 m lap/checkpoint gate thickness —
  `TrackBootstrap.cs:937`, `TrackFactory.cs:308` — so host-side triggers for
  laps, item boxes, missiles and bananas keep firing with no tunnelling). Host
  **relays owner states verbatim** in `aihw.state` (a kinematic body reports
  zero velocity — never re-derive).
- **Arcade**: host still decides everything (roulette, hits, all `_rng` draws);
  effect *physics* on a client-owned car is forwarded to the owner via a new
  reliable `aihw.arc_fx` message; track-limit detection moves to the owner for
  its own car (a follower's WheelColliders don't simulate, so
  `UpdateTrackLimits`' `GetGroundHit` reads are dead there).
- **`ProtocolVersion` 3 → 4** (`NetSession.cs:29`) — exact-match approval
  rejects mixed builds loudly.

## Wire changes (`Net/NetMessages.cs`)

- **`aihw.state` v4** (H→all, UnreliableSequenced, 30 → **60 Hz**). Header
  unchanged (global epoch u8 + hostTime f32 + count u8). Per-car `CarState`
  49 → **62 B**: `slot u8 | carEpoch u8 | pos 3f | rot 4f | vel 3f | angVel 3f |
  steerDeg f | wheelRadPerSec f`. 4 cars @60 Hz ≈ 15 kB/s — trivial on LAN.
  `carEpoch` comes from a new `NetSession._carEpochs[]` for host-simulated slots
  and is relayed verbatim for client-owned slots.
- **`aihw.ownstate`** (new, C→H, UnreliableSequenced, 60 Hz, **66 B**): `carEpoch
  u8 | ownerTime f32 | pos 3f | rot 4f | vel 3f | angVel 3f | steerDeg f |
  wheelRadPerSec f | flags u8` (bit0 penalized, bit1 warned — owner-side
  track-limit results). No slot field — host maps sender clientId → slot.
- **`aihw.arc_fx`** (new, H→one owner, ReliableSequenced, JSON): `{ kind
  (1=Spin, 2=Wreck, 3=Recover), slot, impulse, torqueImpulse, spinTorqueSigned,
  pos, rot }` — carries the host-rolled randomness so the owner replays it
  exactly. `ArcEvtMsg` stays as-is (all-machines cosmetic event).
- **`aihw.input`** unchanged in layout (13 B), rate 30 → 60 Hz — stays alive as
  the carrier for use-item/respawn edges + dead-man; throttle/steer/brake are no
  longer the drive path for owned slots.
- Handlers registered in `NetSession.RegisterHandlers` (:314-330), host-only /
  client-only guarded.

## Files

**New**
- `Net/HostCarFollower.cs` — snapshot buffer + smoothed owner-clock offset (port
  of `ClientCarView`'s idiom, :61-63); FixedUpdate `MovePosition/MoveRotation`
  with FollowDelay ≈ 0.05 s; snap + flush on `carEpoch` change;
  `SnapAwaitEpoch(pos, rot)` for grid starts (teleport, then discard in-flight
  ownstates still carrying the old epoch); `collisionDetectionMode =
  ContinuousSpeculative`; wheel visuals from streamed steerDeg/wheelRadPerSec
  (reuse `ClientCarView.UpdateWheelVisuals` logic); polls its slot's
  `NetworkInputSource` for edges — `UseItemPressed()` →
  `ArcadeDirector.RequestUse`, `RespawnPressed()` → `_lapTimer.ResetTimer(car)`
  + `rig.arcade?.ClearAll()`; `IsStale` (>0.5 s silent) → hold pose.
- `Net/OwnStateSender.cs` — client, on the own rig: 60 Hz (`_accum -= Interval`),
  reads own Rigidbody + `CurrentSteerAngle` + wheel speed; owns the per-car
  epoch with public `BumpEpoch()`; subscribes `car.VehicleReset += BumpEpoch`
  (covers R respawn); reads penalized/warned off `rig.arcade`.

**Modified**
- `Net/NetMessages.cs` — as above.
- `Net/NetSession.cs` — version 4; `StreamInterval = 1/60` (:109) + accumulator
  fix (:639); `_carEpochs[]` + `BumpCarEpoch(slot)`; per-slot latest-ownstate
  store; `OnOwnState` → `event OwnStateReceived`; `BroadcastState` (:643-669)
  relays stored owner states for client-owned slots, derives from rigid bodies
  only for host-simulated slots; `SendOwnState`, `SendArcFxTo(slot, msg)`,
  `OnArcFx` → `event ArcFxReceived`.
- `Core/TrackBootstrap.cs` —
  - `BuildLanHostScene` (:213-241): local slot unchanged; remote slots → new
    `BuildLanFollower(p)` (VehicleFactory `previewKinematic:true` +
    `HostCarFollower` + bare `PlayerRig{netSlot}` — no CarInput/runner/camera);
    route `OwnStateReceived` to followers; `rig.car.VehicleReset += () =>
    BumpCarEpoch(0)` for the host's own R.
  - `OnLanPlayerJoined` (:392) builds a follower; `OnLanPlayerLeft` (:399)
    already null-safe on `rig.runner`.
  - `TeleportToGrid` (:411-428): follower rigs → `SnapAwaitEpoch` (setting
    velocity on a kinematic body is invalid); host rigs → `RestoreState` as
    today + `BumpCarEpoch(slot)`.
  - `BuildLanClientScene` (:431-467): own slot → `BuildLanRig(p)` instead of
    `AddGhost` (`_lapTimer` is already null — `CarInput` tolerates it); real
    `VehicleAudio` path instead of the ghost's streamed-speed path; camera on
    the real car; `hud.ownCar = rig.car` (`LanHud.cs:82` already prefers it);
    create `OwnStateSender`; subscribe `session.RaceStarted` → own pose from
    `RaceStartMsg.poses` → `car.RestoreState` + `SetSpawn` +
    `sender.BumpEpoch()`; `OnCarState` (:502) skips the local slot; ghosts for
    everyone else unchanged.
  - `BuildArcade` handling gate (:162): also apply `ApplyArcadeHandling` to the
    client's own dynamic rig (gripBase/driveBase/assist floor, :181-197).
- `Net/ClientCarView.cs` — accept per-car epoch (snap on either global or car
  epoch change, :50-59); dry-buffer extrapolation (:91-97) also integrates
  rotation by angVel; smooth ~0.1 s blend-back after an extrapolation stretch;
  `RenderDelay` 0.12 → **0.06** (60 Hz stream: covers one lost packet
  (2 × 16.7 ms) + LAN jitter + one render frame). Remote cars go ~170–240 →
  ~70–90 ms behind; the own car goes to zero (local sim).
- `Net/ClientInputSender.cs` — sample analog inputs **every Update** into
  fields (edges already latch); `Interval = 1/60`; `_accum -= Interval`.
- `Net/ArcadeNetLink.cs` — accumulator fix (:76). Host: set
  `director.RemoteOwned = slot => slot != LocalSlot`; `director.EffectDispatch`
  → `SendArcFxTo`; `OwnStateReceived` flags →
  `director.NotifyRemoteTrackLimit`; on `Recovered` for a host-simulated slot →
  `BumpCarEpoch` (fixes the existing wreck-recovery lerp-streak). Client:
  `ArcFxReceived` → Spin: `ArcadeImpulse` + store `spinTorqueSigned`; Wreck:
  impulse + `ArcadeTorqueImpulse`; Recover: `RestoreState(pos, rot, 0, 0)` +
  `racer.RestoreCar()` + `sender.BumpEpoch()`. `OnSync` (:175-198): do NOT
  overwrite the own slot's penalized/warned (owner is source of truth).
- `Arcade/ArcadeDirector.cs` — net-ignorant hooks: `Func<int,bool> RemoteOwned`
  + `event EffectDispatch`. `ApplySpin` (:529-543) / `ApplyWreck` (:555-587) /
  `RecoverFromWreck` (:599-631): the `_rng` draws stay host-side always; for a
  remote-owned victim, raise `EffectDispatch` instead of touching the body
  (deadlines still mirrored via the existing arc_sync hold bits).
  `ApplyEffects` (:842-907): replace the `IsAuthority` gate (:873) with per-racer
  "drives physics" = (authority && !RemoteOwned) || (client && own slot) — the
  owner runs the grip/yaw/drive channel writes + penalty drag on its own car.
  `UpdateTrackLimits` (:949-1011): authority processes non-remote-owned racers;
  a non-authority director processes exactly its own slot (the client's own rig
  has live WheelColliders and `SurfaceMap` built from the same track JSON). New
  `NotifyRemoteTrackLimit(slot, penalized, warned)` (authority): sets follower
  racer flags + raises the OffTrack events on edges so the board/audio work
  everywhere unchanged.
- `Vehicles/CarVehicle.cs` — Awake (:346): `collisionDetectionMode =
  isKinematic ? ContinuousSpeculative : ContinuousDynamic` (ContinuousDynamic is
  invalid on kinematic bodies; also fixes today's latent warning on ghosts).

**Untouched by design**: `CarInput`, `NetworkInputSource`, `ArcadeItemBox`
(host adjudicates pickups vs the follower), `Missile`/`Banana` (host triggers
fire vs the follower's colliders), `LapTimer`/`Checkpoint`, split-screen, SP.

## The user's technique list, mapped

1. Send inputs not transforms — kept for edges/dead-man; the own car now sends
   *state* (owner authority inverts this for the owned car, as racing games do).
2. Fixed-tick both ends — the client now runs the same 400 Hz physics for its car.
3. Prediction + reconciliation — replaced by owner authority (strictly better
   feel for racing; no correction snaps, no rollback infrastructure).
4. Snapshots with pos/vel/rot/angvel — angVel added; 60 Hz.
5. Remote interpolation 100–150 ms in the past — kept, tightened to 60 ms.
6. Extrapolate then blend back — added (rotation via angVel + eased re-entry).
7. Unreliable streams / reliable events — already correct, unchanged.
8. 1–2 s history — interpolation buffers keep ~1 s; no input replay needed.

## Steps (headless compile after each; test rig = editor host + dev standalone client, rebuilt after step 1)

1. **Protocol groundwork + quick wins** (still host-authoritative, fully
   playable): NetMessages v4, NetSession version/60 Hz/per-car epochs/handlers
   (inert), all three accumulator fixes, ClientInputSender per-frame sampling +
   60 Hz, ClientCarView per-car epoch + angVel extrapolation + RenderDelay 0.06,
   CarVehicle CCD fix, VehicleReset/Recovered → epoch bumps. *Play-test: LAN as
   before but smoother; host R/wreck-recovery now snap on clients.*
2. **Client own rig**: BuildLanClientScene own-slot full rig, OwnStateSender,
   RaceStarted grid handler, LanHud.ownCar, camera, skip own slot in OnCarState,
   arcade handling on the own rig. *Play-test: own car instantaneous; the host's
   view of that car is still input-driven (transient divergence — expected until
   step 3).*
3. **Host follower + relay** (authority actually moves): HostCarFollower,
   BuildLanFollower + join path, OwnStateReceived routing, edge polling,
   BroadcastState relay, TeleportToGrid SnapAwaitEpoch. *Play-test: 2-player
   race — laps count from follower crossings, both screens agree, respawn/grid
   snap everywhere.*
4. **Arcade effect forwarding**: director hooks + ApplySpin/ApplyWreck/Recover
   dispatch, ApplyEffects owner gate, owner-side track limits +
   NotifyRemoteTrackLimit, ArcadeNetLink wiring both sides. *Play-test: full
   arcade LAN.*
5. **Polish + docs + regression**: extrapolation blend-back easing, stale-owner
   HUD marker, README LAN section, regression list.

## Edge cases

- **Late join mid-race**: joiner builds its rig at the slot pose and streams;
  host builds a follower; not in `_raceEntries` → informal racing (existing
  behavior preserved).
- **Owner silent (lag)**: follower buffer dry → extrapolate ≤ 0.1 s, then hold;
  host relays the last state so every machine freezes it identically. Owner
  disconnect → follower destroyed via the existing `PlayerLeft` path.
- **Countdown**: already works — `InputsFrozen` gates the owner's own
  `GatedInputSource`; the grid pose arrives via reliable RaceStart before GO.
- **Ghost-as-wall collisions**: a kinematic follower is infinite-mass to the
  host's dynamic car (and symmetrically on every machine). This is the standard
  each-client-owns-its-collision-response model; accepted for v1
  (`maxDepenetrationVelocity = 2` + speculative CCD soften it). Note in docs:
  host↔client shunts no longer exchange momentum — client↔client never did.
- **Grid teleport vs in-flight ownstates**: `SnapAwaitEpoch` discards states
  until the owner's post-teleport epoch appears (the epoch rides every packet;
  RaceStart is reliable, so the bump always happens).

## Verification (play-test script)

1. Non-arcade: client steering/throttle response is instant; 3 laps each —
   lap/CP counts match both HUDs; client R → instant local teleport, snaps (no
   lerp streak) on host + a second client; host R → same on clients.
2. Race: grid freeze, countdown, GO, results identical both sides, DNF grace.
3. Arcade: client box pickup (host roulette, same item both sides); client boost
   instant; host missiles client → local tumble + recovery onto the line;
   client bananas host → host spins; client on grass 2.5 s → penalty drag +
   banner locally, Penalized on the host board; shield block mirrored.
4. Robustness: pull client cable 2 s → its car freezes identically everywhere,
   resumes; kill the client → follower despawns; old build refused (version 4).
5. Regression: SP oval + custom map, split-screen, bot race, arcade solo,
   firmware/Opus run (400 Hz untouched), garage preview (kinematic CCD change),
   the host's own-car feel unchanged, menu attract loop.

## Risks (ranked)

1. **Lap triggers off the follower** — mitigated (fixed-step MovePosition +
   speculative CCD + 0.25 m gates); fallback if ever seen: positional
   gate-crossing check between consecutive follower snaps.
2. **Ghost-as-wall collision feel** — the most visible change from today's
   host-side dynamic-vs-dynamic contacts; revisit with a momentum-exchange fx
   message if play-tests complain.
3. **Adjudication windows** (~50–80 ms follower delay): pickups/hits judged
   against a slightly-past pose. Host is law; shield/invuln rules unchanged.
4. **Epoch/ordering bugs** — concentrated in `SnapAwaitEpoch` + the BumpEpoch
   sites; test deliberately with artificial delay.
5. **Owner-side track limits divergence** — both machines build `SurfaceMap`
   from identical track JSON via the same `BuildEnvironment` path; low risk.
6. **Arcade sustain tails** via the 0.25 s arc_sync hold mirror —
   cosmetic-length differences only, self-correcting; explicit durations in
   ArcFxMsg later if felt.

---

# Arcade pass 2 — pace, game audio, a dodgeable missile, hit feedback

## Context

Second play-test. The escalated hit model and the visible shield landed ("the
missile effect and banana peel are spot on now"), and the Arcade/Sim handling
toggle works. Four things remain, three of them new features.

1. **Cars are still too fast and skittish**, in corners and especially on boost.
   Confirmed in code: there is **no top-speed clamp anywhere in the project** —
   `_body.linearVelocity` is never written outside resets. Top speed is set
   purely by motor back-EMF (~10 m/s). Worse, item boost is
   `_body.AddForce(transform.forward * mass * 14f, Force)` at
   `CarVehicle.cs:1250-1251` with **no ceiling, no grounded check and no traction
   limit** — a 1.6 s boost just keeps accelerating the car past its natural top
   speed. A 1.25× grip bonus cannot rescue a car arriving at a corner sized for
   8 m/s while doing 15.
2. **The project has no audio at all.** Verified exhaustively: zero
   `AudioSource` / `AudioClip` / `PlayOneShot` / `AudioMixer` in the entire
   script tree, and no `.wav`/`.ogg`/`.mp3` anywhere under `Assets`. Every
   `AudioListener` reference is just "add one so Unity stops warning". The
   `masterVolume` slider in Options (`GameSettings.cs:83`) drives an
   `AudioListener.volume` that has literally nothing to hear.
3. **The missile is effectively undodgeable.** 3.2 rad/s at 11 m/s is a 3.4 m
   turning radius — about as tight as the car itself, so it simply follows you
   in. And nothing tells the victim it is coming.
4. **No feedback when you get hit.** The spin-out and the wreck both read
   physically now, but nothing on screen says what just happened or why.

Confirmed with the user: slow the cars by **scaling drive ~15 %**; audio covers
**arcade SFX plus engine and tyre sound** (synthesized in code — no audio files
exist and none will be downloaded); the missile gets a **slower turn rate plus a
late-commit window**; hit feedback is **banner + screen flash + incoming-missile
warning**.

Scope note: LAN arcade (task #142) is still unbuilt and `LanHud` contains no
arcade drawing at all, so the HUD work here is local/split-screen only. Engine
audio does reach LAN, because `ClientCarView` already streams enough state.

## Part 1 — Arcade pace

### Drive scale (the "overall slow down")

`CarVehicle.cs:961` — `volts *= arcadeDriveMult` — is already the single choke
point every drive command passes through, manual, bot, autonomous and LAN host
alike, and scaling it scales top speed essentially linearly (steady state is
`V = Kt·ω_motor`). So no new physics channel is needed.

The one trap: `ArcadeDirector.ApplyEffects` **hard-resets `arcadeDriveMult = 1f`
every frame** at `ArcadeDirector.cs:779` for any car that is neither wrecked nor
spun. A baseline written anywhere else would be stomped within one frame. The
fix is the exact pattern `gripBase` already uses (`ArcadeRacer.cs:31`):

- `ArcadeConfig.HandlingDriveScale = 0.85f`, in the existing
  `// ---- arcade handling ----` block beside `HandlingGripBonus`.
- `ArcadeRacer.driveBase = 1f`, documented as the twin of `gripBase`.
- `ArcadeRacer.RestoreCar()` writes `car.arcadeDriveMult = driveBase` (it
  currently writes a hard `1f`).
- `ApplyEffects` three-way branch: normal → `driveBase`; spun →
  `SpinDriveMult * driveBase`; wrecked → `0f`.
- `TrackBootstrap.ApplyArcadeHandling` sets `racer.driveBase =
  ArcadeConfig.HandlingDriveScale` next to the existing `gripBase` line.

Because it is set inside `ApplyArcadeHandling`, it inherits every guarantee that
method already documents: gated on `SessionConfig.ArcadeHandling`, never applied
to firmware rigs (`Register` refuses them), and covering humans, bots,
split-screen and snapshot-resume from one call site. **Sim handling is untouched
— the cars stay exactly as fast as they are today.**

### Boost speed ceiling (the "too fast on boost")

Boost keeps its punch; it stops being a runaway. In `ApplyEffects`, fade
`arcadeBoostAccel` out as the car approaches a target speed instead of writing a
flat `BoostAccel`:

```
new ArcadeConfig: BoostTopSpeed = 11f;   BoostFadeBand = 1.5f;
factor = Clamp01((BoostTopSpeed - car.ForwardSpeed) / BoostFadeBand)
car.arcadeBoostAccel = active ? BoostAccel * factor : 0f;
```

Deliberately done in the director, not in `CarVehicle`: the surface **boost pads
are maxed in separately** at `CarVehicle.cs:1087` from `surf.boostAccel`, so
capping the item boost leaves level-design boost pads (9 m/s²) exactly as they
are. Zero change to the physics file for this part.

## Part 2 — Audio (new subsystem, no assets)

New folder `Assets/Scripts/Audio/`, namespace `AIHWSim.Audio`. Everything is
synthesized at runtime and cached, following the same "generated in code, no
assets, cached shared instances" rule the whole project already runs on
(`TrackBuilder.CheckerTexture`, `ArcadeVfx`'s cached materials).

**`ProceduralAudio.cs`** — static clip factory + cache, keyed by name.
Primitives: `Tone` (sine/saw/square + harmonics), `Sweep`, `NoiseBurst` with a
one-pole low-pass, and an ADSR envelope helper. Built at
`AudioSettings.outputSampleRate` via `AudioClip.Create` + `SetData`. Two rules
that matter: **looping clips must contain a whole number of cycles** at their
base frequency or the loop point clicks, and noise loops cross-fade tail into
head. Clips: `pickup`, `reveal`, `boost`, `missile_fire`, `explosion`,
`banana_drop`, `spin`, `shield_up`, `shield_block`, `warn_beep`, plus the loops
`engine`, `skid`, `boost_loop` and the one-shot `impact`.

**`SfxPlayer.cs`** — a small scene-singleton with a pool of ~10 `AudioSource`s
(3D, `dopplerLevel = 0` — a chase camera plus doppler warbles badly at RC
speeds) plus one 2D source for "this happened to you". `PlayAt(clip, pos, vol)`
and `Play2D(clip, vol)`. Pooled rather than `PlayClipAtPoint`, which allocates a
GameObject per call.

**`VehicleAudio.cs`** — per-car motor whine + tyre skid + impact thud.
- Motor: looping `engine` clip, `pitch` mapped from motor ω (`car.WheelOmega(i)`
  × the motor's `gearRatio`, both already reachable — `MotorPart.StepDrive`
  uses the same call), volume from throttle plus an idle floor. Pitch clamped to
  [0.4, 3.0].
- Skid: looping `skid` clip, volume from tyre slip. This needs one new read-only
  accessor — `CarVehicle.TyreSlip01`, a normalized max-over-wheels aggregate
  assigned once at the end of `StepPhysics`. It is a pure readout: it is never
  read back by any physics expression, so the mission and firmware runs are
  numerically untouched.
- Impact: `OnCollisionEnter` on the same GameObject as the Rigidbody, volume
  scaled by `impulse.magnitude`. Unity delivers collision callbacks to every
  component on that object, so `CarVehicle` needs no change.
- `public static bool Enabled = true` kill switch, matching the
  `PartMeshLibrary.Enabled` / `TyreModel.Enabled` precedent.

**`Arcade/ArcadeAudio.cs`** — subscribes `ArcadeDirector.Event`
(`ArcadeDirector.cs:49`), which is **already raised from 14 call sites and has
zero subscribers today** — the hook exists, nothing consumes it. Maps event
kinds to clips: your own events play 2D, everyone else's play 3D at `evt.pos`.
Adds two continuous sounds it owns directly rather than by event: the boost
loop while `boostUntil` is live, and a repeating warning beep while a missile is
locked onto you.

**Wiring.** `VehicleAudio` is attached explicitly in
`TrackBootstrap.BuildPlayerRig` (every rig, humans and bots) and on LAN ghosts in
`Net/ClientCarView` (pitch from its existing `SpeedEstimate`, since ghosts run no
drivetrain). `ArcadeAudio` is created beside `ArcadeHud` in the arcade block at
`TrackBootstrap.cs:136-139`. **Garage and menu stay silent** — the garage preview
is kinematic so it would be silent anyway, and a suddenly-noisy main menu is a
worse default than a quiet one. Both are a one-line opt-in later.

**Volume.** `GameSettings` gains `sfxVolume = 0.8f` and `engineVolume = 0.7f`
(field initializers, so old `settings.json` picks them up), with two Options
sliders next to the existing master. The engine drone is exactly the thing
someone wants to mute independently. Also fix a real bug this feature exposes:
`PauseMenu.cs:216` calls `SettingsStore.Save()` but not `Apply()`, so a volume
change made from the pause menu silently does nothing until the next scene load.

**Split-screen and one listener.** `TrackBootstrap.cs:596-613` deliberately gives
P1's camera the only `AudioListener` (Unity allows one). P2's car is still heard,
positionally, from P1's ear. That is the correct and only option without an audio
mixer, and it is called out in the docs rather than papered over.

## Part 3 — A dodgeable missile

`ArcadeConfig`: `MissileTurnRate` 3.2 → **2.2** rad/s (5.0 m radius — still
corners, no longer glued), plus new `MissileCommitRange = 1.5f` and
`MissileCommitTurnRate = 0.6f`. In `Missile.FixedUpdate` (`Missile.cs:63-69`)
pick the rate from the distance to the target, so inside the commit range a
well-timed swerve genuinely makes it miss. A miss is then a real miss: the
missile flies on until `MissileLifetime` and may re-home.

Fairness half: `ArcadeDirector` gains `public bool IncomingMissile(CarVehicle)`,
**extracted from the identical `m.target == car` scan the bot shield policy
already runs** at `ArcadeDirector.cs:694-696`, so there is one implementation
rather than two. It drives both the HUD warning and the warning beep.

## Part 4 — Hit feedback

State on `ArcadeRacer`, not an event subscription — so the solo HUD and the
per-viewport split-screen HUD read the same fields and neither duplicates
plumbing (events stay the audio layer's concern):

- `hitLabel` / `hitColor` / `hitUntil` — set by the director in `ApplySpin`
  ("SPUN OUT!"), `ApplyWreck` ("WRECKED!"), `BreakShield` ("SHIELD BLOCKED!").
- `flashColor` / `flashUntil` — a short full-viewport colour wash on the hit.
- Cleared in `ClearAll` alongside the other deadlines.

`ArcadeHud` draws the banner and the flash full-screen (solo), plus a persistent
"⚠ MISSILE INCOMING" line while `director.IncomingMissile(car)`. Positioned clear
of the existing `DrawTrackLimitWarning` band at `Screen.height * 0.62f`.
`SplitScreenHud.DrawPlayerBox` draws the same three inside `rig.camera.pixelRect`
— it already does the bottom-left→top-left rect flip. While here, hoist
`ArcadeHud`'s two per-frame `new GUIStyle(...)` allocations into cached statics.

## Files

**New:** `Audio/ProceduralAudio.cs`, `Audio/SfxPlayer.cs`, `Audio/VehicleAudio.cs`,
`Arcade/ArcadeAudio.cs`.

**Modified:** `Arcade/ArcadeConfig.cs` (drive scale, boost ceiling, missile
commit), `Arcade/ArcadeDirector.cs` (`driveBase` in `ApplyEffects`, boost fade,
`IncomingMissile`, hit-feedback state), `Arcade/ArcadeRacer.cs` (`driveBase`,
feedback fields), `Arcade/ArcadeHud.cs`, `Arcade/Missile.cs` (commit window),
`Core/TrackBootstrap.cs` (`driveBase`, `VehicleAudio`, `ArcadeAudio`),
`Core/SplitScreenHud.cs`, `Core/PauseMenu.cs` (the `Apply()` fix),
`Vehicles/CarVehicle.cs` (**one read-only `TyreSlip01` property — no behaviour
change**), `Net/ClientCarView.cs` (ghost engine audio),
`Persistence/GameSettings.cs`, `Menu/MenuUI.cs` (two Options sliders),
`README.md`.

**Reused rather than rewritten:** `ArcadeDirector.Event` (the unused hook), the
`gripBase` baseline pattern, `ApplyArcadeHandling` as the single per-car
application site, the bot policy's inbound-missile scan, `SplitScreenHud`'s
viewport rect maths, `AudioListener.volume` ← `masterVolume`.

**Untouched by design:** `TyreModel`, `MotorModel`, the C ABI and every
controller, `RaceDirector`, `LapTimer`, the garage, boost pads, Sim handling.

## Steps

Headless batch compile after each (editor closed, `dangerouslyDisableSandbox`,
0 `error CS`).

1. **Pace** — `HandlingDriveScale` + `driveBase` through
   `ApplyArcadeHandling`/`RestoreCar`/`ApplyEffects`; boost speed ceiling.
   Play-test: cars are calmer, boost lifts hard then plateaus, Sim handling
   unchanged.
2. **Missile + feedback** — turn rate and commit window; `IncomingMissile`;
   `ArcadeRacer` feedback state; `ArcadeHud` and `SplitScreenHud` banner, flash
   and warning.
3. **Audio core** — `ProceduralAudio`, `SfxPlayer`, `GameSettings` volumes,
   Options sliders, `PauseMenu.Apply()` fix.
4. **Vehicle audio** — `CarVehicle.TyreSlip01`, `VehicleAudio`, wiring in
   `BuildPlayerRig` and `ClientCarView`.
5. **Arcade audio** — `ArcadeAudio` off the event stream, boost loop, warning
   beep.
6. **Docs + validate** — README, headless compile, `TrackPresetValidator`,
   editor relaunch.

## Verification

- Headless compile clean after every step; `[TPV] RESULT ALL PASS (12 presets)`
  green at the end.
- **Pace:** same circuit, Arcade handling on — top speed reads ~8.5 m/s on the
  HUD instead of ~10, and corners are holdable on a keyboard. Boost accelerates
  hard and then plateaus rather than running away. Flip to Sim handling: speed
  and feel are exactly as today.
- **Boost pads unchanged:** drive over a level's boost pad — same kick as before
  (they are maxed in separately and are not capped).
- **Missile:** fire at a bot on a straight — it tracks and hits. Have a bot fire
  at you and swerve late — it misses, flies on, and may come back around. The
  incoming warning appears the moment you are locked.
- **Feedback:** banana → "SPUN OUT!" with an amber flash; missile → "WRECKED!"
  red; shield eats a hit → "SHIELD BLOCKED!". In split-screen both appear inside
  the correct half and never bleed across the divider.
- **Audio:** motor whine rises and falls with speed on your car and audibly on
  bots nearby; tyres scrub audibly when you provoke a slide; a wall thump scales
  with how hard you hit it; every arcade action has a distinct sound. Options
  sliders and the pause-menu volume all take effect **immediately**. Master
  volume 0 silences everything.
- **The regression that matters:** the Opus mission is unchanged. Run it headless
  and diff the CSV and the `StepMetrics` sidecar against a pre-change run —
  `TyreSlip01` is write-only from the physics side and audio never touches the
  Rigidbody, so any drift means something leaked. Also re-check a plain non-arcade
  race, a firmware session, the garage and the diff-drive scene.

## Risks

1. **Engine audio now attaches in every track session, including autonomous
   firmware runs.** Mitigated by construction — audio components only read — and
   by the `VehicleAudio.Enabled` kill switch. The Opus CSV diff is the proof.
2. **Looping synthesized clips click** if the loop point is not phase-continuous.
   Handled by generating whole cycles and cross-fading noise loops; audible
   immediately if wrong.
3. **Cutting drive 15 % also cuts launch torque 15 %**, so the cars accelerate
   slightly softer as well as topping out lower. That is the accepted trade of
   riding the voltage channel; if it reads as sluggish rather than calm, the
   alternative is a drag-based ceiling that keeps full launch punch, and
   `HandlingDriveScale` is one constant to revert.
4. **A dodgeable missile risks becoming a useless missile** on twisty circuits.
   Mitigated by keeping 2.2 rad/s (still a 5 m radius) and confining the
   commit window to the last 1.5 m; both are single constants.

---

# Arcade mode — play-test fixes: hit model, shield visual, handling toggle

## Context

First play-test of the arcade layer surfaced four problems. Three have confirmed
root causes in the code; the fourth is a design gap rather than a bug.

1. **Banana does nothing.** It shares `ArcadeDirector.ApplyHit` with the missile,
   and that method is far too weak to be felt (below). Its trigger geometry is
   also marginal: a 0.07 m sphere at surface + 0.02 overlaps the car's root
   `BoxCollider` (which spans ground + 0.028 → 0.128) by only ~6 cm, and it is
   dropped 0.30 m behind a car whose box half-length is 0.21 m — 2 cm of
   clearance behind the bumper.
2. **Missile "no effect except maybe a small nudge."** `ApplyHit` sets
   `arcadeGripMult = 0.35` and `SpinTorque = 0.10 N·m`. The car's yaw inertia is
   ~0.03 kg·m², but each tyre still generates ~0.5 N·m of resisting moment, so
   0.10 N·m loses to the tyres outright. The hit is real; it is just invisible.
3. **Shield never appears.** `ArcadeVfx.BuildShieldOrb` is written but **called
   from nowhere** — grep confirms the only other `shieldUntil` reads are the HUD
   string and the two block checks. There is no world visual whatsoever.
4. **Cars are hard to control, bots included.** `MenuUI.cs:288` builds every bot
   slot with `assists = new AssistSettings()` — the comment says "bots race on
   raw physics" — and the human's assists come from Options sliders that default
   to 0. Arcade therefore runs the full I22 brush-tyre model with every assist
   off. Correct for the sim; wrong for arcade.

Confirmed with the user: a missile hit **recovers the car onto the racing line**
(Mario Kart, not a start-line respawn); handling is a **menu toggle** (Arcade /
Sim) rather than always-on; the shield is a **bubble plus the three authored
orbs**; and a spin-out **cuts throttle** so it reads as a punishment.

Scope note: LAN arcade (task #142) is still unbuilt, so all of this is
local/host-authority only. Everything below is written behind
`ArcadeDirector.IsAuthority` so the LAN step inherits it unchanged.

## Part 1 — Escalate the hit model (issues 1 and 2)

Replace the single `ApplyHit` with two distinct outcomes. Both keep flowing
through `ArcadeRacer` deadlines against `ArcadeDirector.Clock`, so nothing about
pause/restart handling changes.

**New `CarVehicle` field — the one new physics channel:**
```csharp
/// <summary>Arcade drive-torque scale. MUST default to 1.</summary>
public float arcadeDriveMult = 1f;
```
Applied at the single motor-command site (`CarVehicle.cs:942`, beside the
existing `Frozen` check): `volts *= arcadeDriveMult`. At 1 the expression is
identity, so non-arcade sessions, firmware runs and the Opus mission are
untouched — the same rule the other four arcade fields already follow.
Deliberately **not** `Frozen`: that forces full brakes and is owned by the race
countdown, and a wreck should coast and tumble, not stop dead.

**Banana → spin-out.** `ArcadeConfig`: `SpinTorque 0.10 → 1.2` N·m,
`SpinSeconds 1.2 → 1.4`, new `SpinDriveMult = 0f`. Direction stays randomised
per hit (`spinTorqueSigned`), which is the "random rotation" asked for. 1.2 N·m
against ~1.0 N·m of resisting tyre moment at the reduced grip nets a visible,
recoverable spin — expect to feel-tune this in the 0.8–1.5 range.

**Banana trigger geometry** — `BananaRadius 0.07 → 0.13`, spawn at
surface + 0.05, `BananaDropOffset 0.30 → 0.55` so it clears the rear bumper by a
real margin instead of 2 cm. A temporary `Debug.Log` behind an
`ArcadeConfig.LogHits` flag confirms during play-test that the trigger fires at
all; if it does not, the cause is the layer collision matrix and not this code,
and we look there instead.

**Missile → wreck and recover.** New state on `ArcadeRacer`:
`wreckedUntil`, `recoverAt`, `invulnUntil`. `OnMissileHit` calls a new
`ApplyWreck(victim, dir)`:
- explosion VFX at the impact point (new `ArcadeVfx.BuildExplosion` — an
  expanding emissive shell + sparks on `VizLayer`, self-destructing after ~0.8 s;
  collider-free like every other builder in that file);
- a strong up-and-outward impulse plus a large random yaw/roll torque so the car
  visibly tumbles;
- `arcadeDriveMult = 0`, `arcadeGripMult = SpinGripMult` for `WreckSeconds`
  (~1.5 s) — limp, not braked;
- then **recovery**: `Spine.Project(car.position)` for the arc position,
  `Spine.Sample(s + 1 m)` for a forward-facing pose, `DropToSurface` (the
  director's existing helper, which prefers `TrackFactory.DropToSurface` so cars
  and props are never hit) to land it on the ribbon, then teleport upright with
  zero velocity. `CarVehicle.RestoreState` already does exactly this teleport
  (Discrete-mode switch included) and is reused verbatim — do **not** call
  `ResetVehicle`, which returns the car to the start line and would cost the lap.
- `invulnUntil = Clock + 1 s` after recovery so a second missile already in
  flight cannot instantly re-kill; checked at the top of both hit handlers
  alongside the shield check.

Fallback: `Spine == null` (classic oval, no bot path) → recover in place at the
car's own position, upright and facing its pre-hit heading. Arcade already
requires a lap-timed map, so this is a rare edge, not the common path.

## Part 2 — Shield visual (issue 3)

`ArcadeVfx` gains `BuildShieldBubble(parent)`: a ~0.55 m sphere on `VizLayer`
with a translucent additive material (Standard, Fade, low alpha, emissive rim)
and no collider. `BuildShieldOrb` finally gets called — three instances parented
to a spinner transform.

`ArcadeRacer` gains `Transform shieldViz`. The director creates it when
`shieldUntil` is set, spins the orb ring in `ApplyEffects`, and destroys it when
the shield expires **or** is consumed by a block (both existing sites already
zero `shieldUntil`, so one check in `ApplyEffects` covers all three paths).
`ClearAll` destroys it too, so a respawn or race restart cannot leave one
orbiting a car with no shield.

`VizLayer` (2, Ignore Raycast) matters here: the on-car `CameraSensor` culls that
layer, so a shielded car's own camera sensor is not blinded — the same reason
every vehicle part visual lives there.

## Part 3 — Arcade handling toggle (issue 4)

**Session plumbing**, mirroring the existing `SessionConfig.Arcade` /
`TrackLimits` pair exactly:
- `SessionConfig.ArcadeHandling` (bool, default **true**), cleared in
  `SetSinglePlayer()` with the others — that one call is already the complete
  leak guard.
- `GameSettings.spArcadeHandling = true` (field initializer, so old
  `settings.json` reads as Arcade).
- `MenuUI`: a "Handling: Arcade / Sim" cycle row indented under the Arcade
  toggle on both the single-player and split-screen pages, beside the existing
  Track-limits row.
- `SessionSnapshot` carries it (the Resume path does not call
  `SetSinglePlayer`, so it must assign explicitly — same trap the Arcade flag
  already documents).

**Application**, in `TrackBootstrap.BuildArcade` after every rig is registered —
one place that covers humans, bots, split-screen and (later) the LAN host,
rather than at slot-creation in `MenuUI`, which the snapshot-resume path skips:
```csharp
// Arcade handling: raise each car to the assist floor and the grip
// baseline. Per-channel max, so a player who set higher assists in
// Options keeps them. Firmware rigs are never registered, so C
// controllers still face raw physics.
```
New constants in `ArcadeConfig`:
```csharp
public static readonly AssistSettings HandlingAssists =
    new AssistSettings { steer = 0.8f, stability = 0.7f, traction = 0.9f, abs = 0.9f };
public const float HandlingGripBonus = 1.25f;
```
The grip bonus rides the **existing** `arcadeGripMult` channel — already
multiplied into µ on both the brush path (`CarVehicle.cs:1166`) and the
change-gated legacy path (`:1200`) — so there is no new physics code and no new
friction-write site. It needs one small change to how that channel is driven:
`ArcadeRacer` gains `gripBase` (1 or 1.25), `ApplyEffects` writes
`spun ? SpinGripMult * gripBase : gripBase`, and `RestoreCar()` restores
`gripBase` rather than a hard-coded 1.

With Handling = Sim, `gripBase` is 1 and no assists are forced — the current
behaviour exactly, which is what makes this toggle honest rather than a
one-way ratchet.

## Files

**Modified:** `Arcade/ArcadeConfig.cs` (hit/wreck/handling constants),
`Arcade/ArcadeDirector.cs` (ApplyHit split, wreck + recovery, shield viz,
handling floor hook), `Arcade/ArcadeRacer.cs` (wreck/invuln/shieldViz/gripBase),
`Arcade/ArcadeVfx.cs` (bubble, explosion, orb wiring), `Arcade/Banana.cs`
(trigger size), `Vehicles/CarVehicle.cs` (`arcadeDriveMult` + one motor line),
`Core/TrackBootstrap.cs` (handling floor in BuildArcade), `Core/SessionConfig.cs`,
`Menu/MenuUI.cs`, `Persistence/GameSettings.cs`, `Persistence/SessionSnapshot.cs`,
`README.md`.

**Reused rather than rewritten:** `TrackSpine.Project`/`Sample` (recovery pose),
`ArcadeDirector.DropToSurface` → `TrackFactory.DropToSurface` (landing on the
ribbon), `CarVehicle.RestoreState` (the teleport), `AssistSettings` and its four
existing assist implementations, `PartMeshLibrary` + the `arc_shield_orb` FBX.

**Untouched by design:** `TyreModel`, the C ABI and every controller,
`RaceDirector`, `LapTimer`, the garage, the four circuit layouts.

## Steps

Headless batch compile (editor closed, 0 `error CS`) after each.

1. **Hit model** — `arcadeDriveMult` + the motor line; ApplyHit split into
   spin vs wreck; banana trigger geometry and constants. Play-test: banana spins
   you out; missile wrecks and drops you back on the line.
2. **Shield visual** — bubble + orbs, lifecycle in the director, `ClearAll`
   teardown.
3. **Handling toggle** — SessionConfig/GameSettings/MenuUI/snapshot plumbing,
   `ArcadeConfig` floor, `BuildArcade` application, `gripBase` through
   `ApplyEffects`/`RestoreCar`.
4. **Docs + validate** — README arcade section, `TrackPresetValidator` re-run,
   full compile, editor relaunch.

## Verification

- Headless compile clean after every step; `[TPV] RESULT ALL PASS (12 presets)`
  still green at the end (nothing here touches presets, so a regression there
  means something leaked).
- **Banana:** drop one, let a bot drive over it — the bot visibly spins and
  loses drive. Drive over your own after the 0.4 s grace — same. With
  `ArcadeConfig.LogHits` on, exactly one log line per contact.
- **Missile:** fire at a bot — explosion, tumble, ~1.5 s limp, then it reappears
  upright on the racing line facing forward, roughly where it was hit, having
  lost several seconds but not a lap. Fire two in quick succession — the second
  does not re-kill during the 1 s invulnerability.
- **Shield:** pop one — a glowing bubble and three orbiting orbs are visible
  from the chase camera; taking a hit removes both the shield and the visual in
  the same frame; respawning mid-shield leaves nothing orbiting.
- **Handling:** race the same circuit twice, Arcade then Sim. Arcade should hold
  a line on the Boardwalk Cove banked bowl on keyboard without catching a slide;
  Sim should feel exactly as it does today, and bots should visibly spin more.
- **No leaks — the important one:** a non-arcade race, a firmware (Autonomous C)
  session and the Opus mission all behave identically to before. `arcadeDriveMult`
  and `gripBase` are the two new multipliers on the physics path; confirm the
  mission's CSV and `StepMetrics` sidecar are unchanged.

---

# Iteration 24 — Arcade mode: power-ups, four themed maps, and a Blender track-prop pipeline

## Context

Tiny Torque is a physics-honest RC simulator; every map to date is a *test surface* (ovals, proving grounds, rally loops) and every race is a clean time trial. The user wants a second, deliberately unserious mode alongside it — **Mario-Kart-style arcade**: power-ups picked up from item boxes, player-to-player weapons (homing missile, dropped banana), track limits, a live scoreboard, and themed maps with real visual identity rather than painted tiles. The explicit deliverable is **the assets for ≥3 new themed maps, implemented into both the shipped map presets and the map-builder palette**.

Nothing in the sim conflicts with this: arcade is a session flag, the physics layer is untouched at neutral values, and the track-item pipeline already gives a new `ItemDef` a palette icon, placement ghost, snapping, undo, save/load and preset authoring for free. The work is (a) a new arcade gameplay layer, (b) the project's **first mesh-backed track props** (today every track item is a runtime primitive), and (c) four themed maps built from them.

**Decisions confirmed with the user:** build the playable item loop this iteration (instant replay deferred to i25, hooks left in); **all four themes** — Toy Workshop, Neon Grid, Beach Boardwalk, Volcano Foundry; props authored to the **full hard-surface SubD standard** from Iteration 20; **LAN included now** (host-authoritative, ProtocolVersion → 3).

## Blocking prerequisite (verified)

`Blender/mcp_helpers.py:21` still has `PROJECT = r"E:\EE Projects\AI Hardware Control Sim (Unity)"`. That directory exists on disk, is **empty**, and is **not** a junction — so `export_part()` today writes FBX into a dead tree Unity never scans, silently. Fix `PROJECT` to `E:\EE Projects\Tiny_Torque` before any Blender work.

---

## Part A — Track-prop mesh pipeline

Track props are the first meshes outside the vehicle. Two facts force a small amount of new plumbing:

- `PartMeshLibrary.Sanitise` (`PartMeshLibrary.cs:68-75`) **destroys every collider** and forces `VizLayer = 2` (Ignore Raycast). Track items need collision, and they must stay visible to on-car camera sensors (which cull layer 2) and hittable by ToF raycasts.
- `TrackBuilder.Box/Cylinder` create colliders by default — the opposite convention.

**Resolution:** a mesh-backed prop = *imported visual shell on the default layer* + *invisible primitive collision hull authored in the `build` lambda*. New helper in `TrackCatalog`:

```csharp
// Mesh-then-primitive fallback, mirroring PartVisualFactory.BuildWheelViz.
// hull() always runs: collision is authored, never imported.
private static void MeshProp(Transform p, string key, Material fallback,
    (string token, Material mat)[] tokens, Action<Transform> hull,
    Action<Transform> primitiveFallback)
```

Changes:
- **`Vehicles/PartMeshLibrary.cs`** — `Load`/`TryInstantiate`/`Has` gain an optional `string root = "PartModels/"`; cache keyed on `root + key`. Callers unchanged.
- **New `Assets/Resources/TrackProps/`** — prop FBX live here, keeping the vehicle contract table separate from the prop one.
- **`Assets/Editor/PartModelPostprocessor.cs:19`** — `IsPartModel` also matches `Resources/TrackProps/`. `isReadable` stays `body_`-only.
- **`Assets/Editor/PartModelValidator.cs`** — `Spec` gains `float? MaxExtent`. Props are checked on **max extent + triangle budget**, not exact axes (props have no runtime scale contract; the check exists to catch the ×100 FBX scale trap and budget creep). Budgets: small prop ≤1500 tris, medium ≤3000, hero landmark ≤6000.
- **`TrackEd/TrackCatalog.cs`** — `FloorTypeDef` gains `Color emission` (default black) applied in `Mat`; `ItemDef` gains `string theme = ""` (palette grouping) and `string meshKey = ""`.

---

## Part B — Arcade gameplay layer

New namespace `AIHWSim.Arcade`, folder `Assets/Scripts/Arcade/`.

### Where state lives

`ArcadeRacer : MonoBehaviour` on the **car root** (held item, roulette, effect timers, position, points, off-track accumulator, spine hint), plus one convenience field `public Arcade.ArcadeRacer arcade;` on `PlayerRig`. A component dies with its car — important because LAN roster churn destroys cars mid-session (`TrackBootstrap.cs:283`), and because a trigger callback hands you a `Collider`, so `other.GetComponentInParent<CarVehicle>()` → `car.GetComponent<ArcadeRacer>()` is two cheap calls instead of the linear rig scan at `TrackBootstrap.cs:585`.

### Classes

| Class | Type | Created by |
|---|---|---|
| `ItemKind` | `enum : byte` (append-only — goes on the wire): `None, Boost, Missile, Banana, Shield, TripleBoost` | — |
| `ArcadeConfig` | static consts: roulette weights by position, magnitudes, durations, points table, thresholds, `BotsUseItems` kill switch | — |
| `ArcadeRacer` | MonoBehaviour, pure state bag (no `Update`) | `ArcadeDirector.Register` |
| `ArcadeDirector` | MonoBehaviour scene singleton — the only brain | `TrackBootstrap` |
| `TrackSpine` | plain class; cumulative arc length + `Project(world, ref hint)` seeded ±20 nodes | `ArcadeDirector` from `BotPath.Build` |
| `ArcadeItemBox` | MonoBehaviour on a trigger volume | `TrackFactory` (authored) / `ArcadeDirector` (auto-placed) |
| `Missile`, `Banana` | MonoBehaviour, kinematic + trigger | `ArcadeDirector` |
| `ArcadeVfx` | static procedural meshes/materials | — |
| `ArcadeHud` | MonoBehaviour `OnGUI` (solo/split only) | `ArcadeDirector` |
| `ArcadeEvent` / `ArcadeEventKind` | struct + `enum : byte` — one payload for both LAN and the future replay log | — |

Construction gate mirrors `RaceDirector` (`TrackBootstrap.cs:104`): `SessionConfig.Arcade && SessionConfig.TargetLaps > 0 && _lapTimer != null`.

### The four `CarVehicle` edits (the only physics risk)

```csharp
public float arcadeBoostAccel;      // m/s², folds into the boost-pad max
public float arcadeGripMult = 1f;   // MUST have the 1f initializer
public float arcadeYawTorque;       // N·m about world up (spin-out)
public void ArcadeImpulse(Vector3 impulse, Vector3 worldPoint);  // _body is private
```

1. `CarVehicle.cs:928` — `float boost = 0f;` → `float boost = arcadeBoostAccel;`. Everything downstream (the per-wheel max at `:1041`, the single application at `:1202`) is untouched; item boost and pad boost **max**, they don't stack.
2. `:1140` (brush path) and `:1174` (legacy path) — append `* arcadeGripMult` to the µ product. The legacy write is already change-gated at `:1175`, so a constant `1f` writes no `WheelFrictionCurve`.
3. After the stability-assist block at `:1216` — `if (arcadeYawTorque != 0f) _body.AddTorque(0f, arcadeYawTorque, 0f, ForceMode.Force);`, reusing the `:1215` idiom.

At neutral values every expression reduces to its current form. **Do not** reuse `Frozen` for spin-out (it forces full brakes and is owned by `RaceDirector.FreezeCars`) and **do not** touch `SetGrip` (`:1373`, ITunable-only, writes curves directly and would fight the change gate).

### Item lifecycle

`Empty → (box touched) Rolling 0.9 s → Held(kind, charges) → (use) effect → Empty`. A box grants only when `!rolling && held == None`, depletes on the first accepted touch (the car root `BoxCollider` **and** every `WheelCollider` are children of the same root, so one box fires `OnTriggerEnter` several times per car), and respawns after 4 s. Roulette weights key on `livePosition` — leaders get boost/banana/shield, back-markers get missile/triple-boost.

- **Missile** — kinematic homing, not a Rigidbody (no per-frame pose on the wire) and not hitscan (must be dodgeable). `SphereCollider(isTrigger)` + kinematic `Rigidbody`, moved in `FixedUpdate` by `RotateTowards` + constant 11 m/s (≈1.4× the Hard bot's 9.5 m/s), ground-hugged by a raycast every 5th step, 6 s lifetime. Target = nearest racer **ahead** on the spine within 25 m, latched at fire time; no target ⇒ flies straight, same code path. Owner false-positives blocked by three independent guards: skip `other.isTrigger` (so finish/checkpoint gates never detonate it), reference-compare `car != _ownerCar`, and a 150 ms arm delay, plus a 0.45 m muzzle offset (chassis half-length is 0.21 m).
- **Banana** — one `SphereCollider(isTrigger)` + kinematic body. Explicitly **not** the `TrackFactory` dynamic-prop pattern (`:200-224`), whose non-trigger Rigidbodies are shoveable. 0.4 s owner grace then it can hit its owner (correct Mario Kart behaviour, and stops parking on it as a shield); 25 s lifetime; max 2 per player.
- **Hit** — shield consumes and blocks; otherwise `arcadeGripMult = 0.35`, `arcadeYawTorque = ±SpinTorque` (sign randomized), 1.2 s, plus one `ArcadeImpulse` punt.

### Track limits

Sampled at **10 Hz** in `ArcadeDirector` (a gameplay rule, not physics): per wheel, `car.GetWheel(i).GetGroundHit(out hit)` → `SurfaceMap.At(hit)`. A wheel is off when ungrounded or `frictionMult <= 0.90` — which classifies grass/sand/ice/mud (and the new wet-sand, carpet, lava) as off and dirt/asphalt/rumble/boost/checker as on, with **no new per-tile authoring**, and works on spline ribbons because `SurfaceMap.At` resolves `SurfaceTag` first. The car is off only when **all** wheels are off (two wheels on grass at an apex is racing). Jump guard: all-ungrounded only counts after 1.0 s.

Hysteresis: `offTrackTime` accumulates while off, decays 2× while on; warning at 1.0 s, penalty at 2.5 s, then 3 s cooldown. **Penalty = speed cap** (3.5 m/s for 2 s) applied as a soft rearward drag through `ArcadeImpulse` — not a hard velocity clamp, which could destabilize the brush-tyre impulse clamps. Deliberately **orthogonal** to lap validation: `LapTimer`'s ordered-checkpoint gate is already the anti-shortcut mechanism, and coupling them would double-punish and require editing a file shared with non-arcade sessions.

*Known limitation to document:* `SurfaceMap.At` returns `Baseline` (frictionMult 1) off the tile map, so **track limits are a no-op on the classic oval** (which is berm-walled anyway). The subsystem disables itself and hides its HUD when `SurfaceMap.Active == null`.

### Scoreboard

Live position at **5 Hz**: `LapCount * spine.TotalLength + spine.Project(pos)`, sorted. Strictly finer than `RaceDirector.Progress()` (`:129`), which quantizes to checkpoint fractions — **leave `Progress()` alone**, it drives bot rubber-banding and changing it would alter existing non-arcade races. Points `{15,12,10,8,6,4,2,1}` awarded on finish.

Three additive `RaceDirector` changes:
1. `public event Action<PlayerRig,int> PlayerFinished;` raised at `:159`.
2. `public bool arcade;` — suppresses `DrawBanner` (`:193`) so two centred banners don't collide; adds a points column to `DrawResults` (`:207`).
3. `public float resultsGraceSeconds;` — **fixes a real bug for arcade**: `_showResults` only fires when *all* entries finish (`:160`), so one repeatedly spun-out bot holds the results screen hostage forever. Once the first car finishes, start a countdown; on expiry mark the rest DNF. Default 0 ⇒ non-arcade behaviour byte-identical. `NetSession.AllEntriesFinished` (`:711`) has the identical shape and gets the same grace.

HUD placement reuses the existing per-viewport rect maths rather than adding an `OnGUI` pass: solo → `ArcadeHud`; split-screen → one label inside `SplitScreenHud.DrawPlayerBox` (`:27`); LAN → inside `LanHud.DrawOwnBox` (`:29`) and `DrawRaceBanner` (`:55`). `_lapTimer.showDefaultHud = false` in arcade, as split-screen and LAN already do.

### Input

`IDriverInputSource` (`PlayerInputSource.cs:20`) gains `bool UseItemPressed();` — six implementations:
- `PlayerInputSource`: Keyboard → **LeftShift** (verified free: LeftCtrl brake, Space handbrake, R respawn, M/G/J/K/P overlays); Gamepad → **buttonWest** (the only free face button — South handbrake, East brake, North respawn); Merged → new `InputReader.UseItemPressed()`.
- `BotDriver`: a settable latch (`RequestUseItem()`), same shape as `_respawnLatch` (`:99-110`). The *policy* lives in `ArcadeDirector.UpdateBots()` at 2 Hz with a 0.3–1.2 s randomized reaction delay — bots need whole-field visibility that `BotDriver` doesn't have. Rules: boost when `|steer| < 6°` and moving; missile when a spine target is within 15 m; banana when someone is within 8 m behind; shield when a missile is inbound; never within 1.5 s of GO.
- `NetworkInputSource` / `GatedInputSource`: latch and gate exactly as `respawnEdge`.

One line in `CarInput.Update` beside the respawn edge: `if (source.UseItemPressed()) ArcadeDirector.Instance?.RequestUse(car);`.

### LAN (host-authoritative)

The host already simulates every car, so it runs the entire lifecycle for all rigs. **Clients run none of it** — `ArcadeItemBox.Awake` self-destructs on a non-host, mirroring the existing precedent where `BuildLanClientScene` *destroys* `LapTimer`/`Checkpoint` (`TrackBootstrap.cs:313-314`).

- Input: bit 4 in the flags byte at `NetPack.WriteInput` (`NetMessages.cs:149`) — the byte already exists, message stays 13 bytes. A previously-always-zero bit changing meaning is exactly why **`ProtocolVersion` → 3** (`NetSession.cs:25`; the exact-equality check at `:218` then rejects mixed builds cleanly).
- Three new messages via the existing `BroadcastJson`/`SendJson` helpers (`:300-326`), templated on `HostPublishLap` (`:569`): `aihw.arc_state` (event-driven + 2 Hz keepalive), `aihw.arc_evt` (one-shots), `aihw.arc_snapshot` (sent to a joining client only).
- Projectiles cost **two messages each**: the client spawns a collider-less visual and runs the same deterministic homing integration against its local ghost; divergence over ~120 ms of `ClientCarView.RenderDelay` is cosmetic.
- `NetSession.LapStanding` (`:48`) gains `points, arcPos, held, charges, effectMask` so the client HUD reads one model. `LapMsg` untouched.
- `BuildLanHostScene` (`:133`) never calls `BotPath.Build` — add it, or LAN has no spine for positions or homing.

### Session plumbing

`SessionConfig` gains `public static bool Arcade;` and `public static bool TrackLimits;`, **both cleared in `SetSinglePlayer()`** — that one call is a complete leak guard, since `GarageUI.cs:1596`, `TrackBuilderUI.cs:820` and the stale-LAN guard at `TrackBootstrap.cs:58` all funnel through it. `GameSettings` gains `spArcade` / `spTrackLimits` (JsonUtility keeps field initializers, so no version bump). `MenuUI` gets a toggle in `DrawSinglePlayer` and `StartSplitScreen`, disabled when `_spControl == Firmware`; LAN clients receive it via `WelcomeMsg`/`SessionStateMsg`. `SessionSnapshot` carries it (the Resume path does **not** call `SetSinglePlayer`, so it must assign explicitly). Firmware rigs are refused twice: greyed in the menu, and skipped in `ArcadeDirector.Register` — a boost or spin-out would corrupt a controller-validation run and its `StepMetrics`.

---

## Part C — Blender assets (full hard-surface SubD standard)

New `Blender/props.blend` + `Blender/build_props.py`, following the established scripted-parameter-table convention (`build_wheels.py` / `build_bodies.py` / `build_small.py`), reusing `mcp_helpers.py`'s rig: `clean_mesh` → `shade_auto_smooth` → apply Weighted Normal → `mesh_report` (must come back with 0 n-gons / 0 non-manifold / 0 loose verts) → `check_contract` → `export_part` → `render_part`/`contact_sheet`. `mcp_helpers.py` needs the `PROJECT` fix plus optional `fbx_dir` on `export_part` and a `PROPS_BLEND` constant.

**Authoring contract** (unchanged from I20): metres, Blender X = width / Y = length / Z = up → Unity X / Z / Y; one collection per asset = FBX filename = `Resources.Load` key; objects named `<materialtoken>_<descriptor>` so `AssignByName` drives shared runtime materials; export with the pinned 9 args (`apply_unit_scale=False, global_scale=0.01, axis_forward='-Z', axis_up='Y', bake_space_transform=True, mesh_smooth_type='EDGE'`). Props sit on the ground plane with origin at the base contact point (not the centroid) — `TrackFactory.ItemPose` snaps the root to the dropped surface point.

### Shared arcade family (4 assets, needed by every map)

| Key | Description | Size | Budget |
|---|---|---|---|
| `arc_item_box` | Chamfered floating cube, recessed glyph panel on all faces, emissive inner core | 0.24³ m, hovers 0.10 m | ≤2000 |
| `arc_missile` | Nose cone, ribbed body, four swept fins, exhaust nozzle | 0.16 m long | ≤1200 |
| `arc_banana` | Splayed three-lobe peel, stem, curled tips | 0.12 m across | ≤1000 |
| `arc_shield_orb` | Faceted gem orbiting the car (3 instances) | 0.05 m | ≤600 |

### Theme families — 6 props each, 24 total

Each family covers the same four roles so a map can be built entirely from it: **wall** (blocks the racing line), **ramp/obstacle** (interactive), **hazard** (dynamic or punishing), **hero landmark** (silhouette, placed 2–4× per map), plus two decorative fillers.

- **Toy Workshop** — `tw_book_stack` (wall), `tw_ruler_ramp` (ramp), `tw_brick_wall` (wall), `tw_pencil` (dynamic roller), `tw_mug` (hero), `tw_tape_arch` (gate/landmark). The RC car in a giant human world — the theme that *justifies* the 1/10 scale instead of fighting it.
- **Neon Grid** — `ng_pylon` (wall, emissive), `ng_arch_gate` (hero gate), `ng_ring_float` (hoop), `ng_barrier_glow` (wall), `ng_data_cube` (decor cluster), `ng_spire` (hero). Cheapest family to author; simple forms carry it.
- **Beach Boardwalk** — `bb_palm` (hero), `bb_surfboard_ramp` (ramp), `bb_plank_wall` (wall), `bb_tiki_torch` (light post), `bb_beach_ball` (dynamic), `bb_sandcastle` (obstacle).
- **Volcano Foundry** — `vf_rock_arch` (hero), `vf_obsidian_block` (wall), `vf_steam_vent` (hazard), `vf_barrel` (dynamic), `vf_grate_ramp` (ramp), `vf_crag_spire` (hero).

**Budget honesty:** 28 assets at SubD standard is the largest Blender push in the project's history (I20 rebuilt 8). Two mitigations, both cheap: hard tri budgets enforced by the validator, and `StaticBatchingUtility.Combine` extended to the items root for interactive builds (today it covers only the floor, `TrackFactory.cs`) — a 60-prop map is otherwise ~300 draw calls. If a family runs long, ship it in a follow-up: the maps degrade gracefully to primitives via the fallback, they don't break.

---

## Part D — Themed maps + builder integration

### New floor types (append-only, indices 9+)

`wood` (1.10), `carpet` (0.80 + rollingResist, run-off), `neon` (1.15, emissive grid), `plank` (1.05), `wetsand` (0.45), `lavarock` (0.95, rough), `obsidian` (1.20), `metalgrate` (1.10, bump). Note the friction values double as track-limit classification: carpet, wet sand and lava-adjacent surfaces land below the 0.90 threshold and read as off-track for free — no new field, no new authoring.

### Builder palette

`ItemCategory` gains **`Arcade`** and **`Scenery`**; tabs become `FLOOR | WALLS | OBST | MISC | ARCADE | SCENERY | SPLINE` in rows of 4 + 3. Inside SCENERY the grid is grouped by `ItemDef.theme` headers, so four themes share one tab instead of four.

`TrackBuilderUI` is the **only** place with hard-coded tab indices — `TabNames` (`:26`), the row-split loops (`:834`/`:836`), and every `_tab == 0` / `_tab == 4` / `(ItemCategory)(_tab - 1)` comparison (`:100, :106, :112, :131, :414, :841-843, :847-852, :983`). Replace the arithmetic with an explicit tab→category table rather than shifting indices; that removes the whole class of bug.

`ItemBehavior` gains **`ItemBox`** (append-only) with one `case` in `TrackFactory.BuildItems`'s switch (`:226`) attaching a trigger via the existing `MakeGateTrigger` plus `ArcadeItemBox`. `PlacedItem.itemId` is a **string** and unknown ids are skipped quietly (`:189`), so an old build loading a new track simply omits the boxes — safe both directions.

**Auto-placement fallback:** when an arcade session loads a track with no authored item boxes, `ArcadeDirector` places rows of 3 along the `TrackSpine` at even arc-length intervals (±0.5 m lateral). Without this, arcade only works on re-authored maps and every existing preset and user map would need editing.

### Four themed presets

Added to `TrackPresets.All` — one table entry plus one private method each, built with the existing `New/PaintRect/It/Spline/Oval` helpers:

- **★ Workshop Grand Prix** — wood floor, carpet run-off, closed spline through book-stack chicanes, a ruler-ramp jump, tape-arch gate, mug landmarks.
- **★ Neon Vortex II** — near-black neon grid, emissive pylon walls, banked spline sweepers, ring hoops on the straights, spire skyline.
- **★ Boardwalk Cove** — plank spline over sand with a wet-sand shortcut, palm-lined corners, surfboard ramp, tiki-torch lighting.
- **★ Foundry Descent** — obsidian ribbon over lava rock, grate-ramp jumps, steam-vent hazards, rock-arch hero corner.

Each ships with finish, 3–4 ordered checkpoints, spawn, and ~9 authored item boxes. Gate `yawDeg` follows the heading-of-travel convention documented at `TrackPresets.cs:399-419`.

---

## Files

**New:** `Assets/Scripts/Arcade/` (`ItemKind, ArcadeConfig, ArcadeRacer, ArcadeDirector, TrackSpine, ArcadeItemBox, Missile, Banana, ArcadeVfx, ArcadeHud, ArcadeEvent`), `Assets/Scripts/Net/ArcadeNetMessages.cs`, `Assets/Scripts/Replay/PoseSampler.cs`, `Assets/Resources/TrackProps/*.fbx` (28), `Blender/props.blend`, `Blender/build_props.py`.

**Modified:** `Vehicles/CarVehicle.cs` (4 edits + `ArcadeImpulse`), `Vehicles/PartMeshLibrary.cs` (root param), `Core/TrackBootstrap.cs` (3 composition paths + `BotPath.Build` in LAN), `Core/PlayerInputSource.cs` (+ `InputReader`, `BotDriver`, `NetworkInputSource`, `GatedInputSource`, `CarInput`, `ClientInputSender`), `Core/SessionConfig.cs`, `Core/SplitScreenHud.cs`, `Track/RaceDirector.cs`, `Net/NetSession.cs` (version, handlers, `LapStanding`, finish grace), `Net/NetMessages.cs`, `Net/LanHud.cs`, `TrackEd/TrackCatalog.cs`, `TrackEd/TrackFactory.cs`, `TrackEd/TrackBuilderUI.cs`, `TrackEd/TrackPresets.cs`, `Menu/MenuUI.cs`, `Persistence/GameSettings.cs`, `Persistence/SessionSnapshot.cs`, `Core/PauseMenu.cs`, `Assets/Editor/PartModelPostprocessor.cs`, `Assets/Editor/PartModelValidator.cs`, `Blender/mcp_helpers.py`, `README.md`, memory `project-overview.md`.

**Untouched by design:** the C ABI and every controller, `SimulationRunner`, `LapTimer`, `Checkpoint`, `TyreModel`, `MotorModel`, the garage, the diff-drive scene.

## Steps

Headless batch compile (editor closed, 0 `error CS`) after every step.

1. **Pipeline prerequisites** — fix `mcp_helpers.PROJECT`; `PartMeshLibrary` root param; `TrackProps/` folder + postprocessor + validator `MaxExtent`; `FloorTypeDef.emission`, `ItemDef.theme/meshKey`, `TrackCatalog.MeshProp`.
2. **Arcade foundations + the four `CarVehicle` edits** — `ItemKind`, `ArcadeConfig`, `ArcadeEvent`, `ArcadeRacer`, `TrackSpine`, session flags. **Validate here in isolation** — this is the only step that can regress physics.
3. **Input seam** — `UseItemPressed` across all six implementations + `CarInput` line. Still inert.
4. **First playable** — `ArcadeDirector`, `ArcadeItemBox` (auto-placed), boost + banana, solo vs bots, primitive placeholder visuals.
5. **Full item set** — missile, shield, triple-boost, bot use policy.
6. **Track limits.**
7. **Scoreboard** — `RaceDirector` additive changes incl. the DNF grace, `ArcadeHud`, `SplitScreenHud` line. Split-screen falls out here.
8. **LAN** — ProtocolVersion 3, input bit, three messages, `LapStanding`, `LanHud`, `BotPath.Build` in the host branch, client suppression.
9. **Replay hooks** — extract `PoseSampler.SampleRig` from `NetSession.BroadcastState` (`:527`), `ArcadeDirector.Clock`.
10. **Blender: arcade family** — 4 assets, contract + validator entries, wired into the item box / missile / banana.
11. **Blender: theme families** — 24 props, one family per sub-step with renders and mesh reports.
12. **Maps + builder** — new floor types, `ItemCategory.Arcade`/`Scenery`, tab table refactor, `ItemBehavior.ItemBox`, item-box `ItemDef`, 24 prop `ItemDef`s, four presets, items-root static batching.
13. **Docs + regression** — README arcade section, memory, full regression pass, editor relaunch.

## Verification

- **Physics unchanged (step 2 gate):** run the Opus mission headless before and after, diff the CSV and `StepMetrics` sidecar. Any drift means an arcade expression isn't identity-neutral.
- **Assets:** per prop, Blender `mesh_report` clean and `check_contract` within 2 mm; then headless `PartModelValidator.Report` → every prop PASS on extent + tri budget, `[PMV] RESULT ALL PASS`.
- **Solo arcade:** race 5 bots on each new map — boxes grant, roulette favours the back-marker, missile locks the car ahead and is dodgeable, banana spins you out, shield blocks once, bots use items without suiciding, results show places and points, and a spun-out bot no longer hangs the results screen.
- **Track limits:** cut the grass/carpet/wet-sand for 3 s → warning then speed cap; two wheels off at an apex → nothing; a jump → nothing.
- **Builder:** ARCADE and SCENERY tabs show icons for every new prop grouped by theme, ghosts place and snap, item boxes save/load, and a track authored with boxes suppresses auto-placement.
- **Split-screen:** per-viewport item UI, one shared board, both players pick up independently.
- **LAN:** editor hosts + dev build joins; client fires and is hit; missiles and bananas appear on both machines; a mid-race joiner receives the snapshot; a leaver mid-flight doesn't throw; an old build is rejected with a version mismatch.
- **No leaks:** garage Drive, builder Drive, free-drive and a firmware session all show zero arcade UI and no item boxes.
- **Regression:** the eight existing presets, classic oval, diff-drive scene, garage, snapshots, telemetry and the Opus mission all behave as before.

## Risks

1. **LAN authority** — client-side triggers, mid-race join, leave-mid-flight, and teleport/epoch churn. Mitigated by host-only boxes with an `Awake` self-destruct, a join snapshot, null-target degradation to dumb-forward, and `ResetArcade()` on `RaceStarted`/`RestartRace`.
2. **`CarVehicle` regression** — four edits inside `StepPhysics` sit next to the I22/Opus calibration. All are identity-preserving at neutral values, no statement reordering, `arcadeGripMult` **must** initialize to `1f`, and step 2 is validated alone before anything is wired.
3. **Blender scope** — 28 SubD assets is the largest asset push yet. Hard tri budgets, one family per sub-step, and graceful primitive fallback if a family slips to a follow-up.
4. **Bots using items badly** — policy centralized in the director with steer/speed gates, randomized reaction delay, a no-use window around GO, and a one-flag kill switch.
5. **Draw calls** — extend static batching to the items root; a 60-prop map is otherwise ~300 draws.
6. **`TrackBuilderUI` tab indices** — the one place with hard-coded arithmetic; refactored to an explicit table rather than shifted.
7. **Enum persistence** — `ItemBehavior` and floor types are append-only and item ids are strings with quiet unknown-id skipping, so old↔new track JSON degrades cleanly in both directions.

## Deferred to iteration 25 — instant replay of the winner's highlights

Per the agreed scope. The hooks left in place make it small: `PoseSampler.SampleRig` reuses the existing `CarState` record that `ClientCarView` already interpolates (the playback engine is already written); `ArcadeEvent` is already a timestamped highlight log (hits, boosts, overtakes); `ArcadeDirector.Clock` gives one monotonic race clock immune to `Time.timeScale`; and `ChaseCamera` is purely `target`-driven, so a replay camera only needs to repoint at a puppet transform. Remaining work is a ring buffer, highlight selection, a camera director, and the results-overlay button.

---

# Iteration 23 — Interactive HTML tools: hardware→vehicle wizard, control-loop lab, calibration, telemetry (+ JGraph hooks)

## Context

The repo is now `E:\EE Projects\Tiny_Torque` (renamed, git-tracked). The user wants **interactive documents a player/engineer opens in a browser** that translate their *real* RC hardware into the game and back:

1. **Car Setup wizard** — click through hardware choices, enter datasheet specs, export a valid `VehicleDesign` JSON into the game's Vehicles folder.
2. **Control Loop lab** — load a vehicle JSON, then interactively teach transient equations / design trade-offs and draft compilable C `.h`/`.c` controller files matching the repo's firmware conventions.
3. User-accepted extras: **Calibration companion**, **Telemetry analyzer**, **Motor datasheet converter**, and **JGraph integration** (the user's MATLAB-like app at `E:\EE Projects\JGraph`, CLI-capable) for showing figures / live algorithm design when installed.

Decisions confirmed: plain HTML files in a new top-level **`Tools/`** folder (double-click to open, no server, tracked in git); vehicle export via **File System Access API directory picker with download fallback** + on-screen destination-path instructions.

## Constraints & shared foundation

- **Fully offline single-repo pages**: vanilla JS/CSS, no CDN, no build step. Pages run from `file://`, which blocks `fetch()` of local files but allows `<script src>` of siblings — so shared logic lives in `Tools/shared/*.js` loaded via script tags.
- **Styling**: dark theme with the game's KSP-orange accent (`#FF9E33`-ish, matching GarageSkin `(1,0.62,0.20)`), Tiny Torque branding/tagline in the header of every page.
- **Persistence between pages**: `localStorage` carries the working vehicle JSON (so Setup → Control Lab hand-off is one click) plus the persisted File System Access directory handle lives in IndexedDB.

### `Tools/shared/` modules (the load-bearing part)

- **`tt-schema.js`** — the complete `VehicleDesign` schema as data: every field with default, type, units, slider min/max (from GarageUI), sentinel semantics, and enum tables (`BodyShape` Box=0…LowRacer=4; `SensorType` Tof=1…Battery=7 — no 0; `AeroKind`; `wheelStyle`; `motorEntryMode`). JSON generator honouring the JsonUtility rules discovered in exploration: **`motor`/`motorDatasheet` are structs — emit all 16 / all 4 fields or omit the key entirely**; class-level keys may be omitted (defaults apply); enums as ints; `Color` needs `a`; filename = sanitized `name` + `.json`; `mass > 50` designs are hidden by `VehicleLibrary.List()`. Also a validator (range check + cross-checks like "encoder wheelIndex < wheels.length", "motorEntryMode 1 requires all 4 datasheet fields > 0").
- **`tt-motor.js`** — motor math shared by 3 pages: `kt = 60/(2π·Kv)`; datasheet→constants `R = Vn²/(τs·ω0 + Vn·I0)`, `Kt = τs·R/Vn` (mirror of `MotorModel.ApplyDatasheet`, incl. its clamps); derived plant constants exactly as `mission_cfg.h` derives them: `BEMF_V_PER_MS = kt·gear/r`, `FORCE_PER_AMP = BEMF_V_PER_MS·η`, `FORCE_PER_AMP_ALL = ×N_motors`, effective mass `m_eff = m + N·J·gear²/r²`; the **single-motor-through-a-diff convention** from the Opus preset (two sim motors, halve extensive quantities: R×2 [parallel paths], I0/2, Imax/2, J/2; kt and gear unchanged) implemented as one function.
- **`tt-sim.js`** — small fixed-step numerical core used by the interactive plots: DC-motor + vehicle longitudinal plant (`V → I = (V − kt·gear/r·v)/R → F → m_eff·v̇`, with ESC deadband/lag and drag polynomial), a JS port of `pid_update` (same conditional-integration anti-windup + derivative-on-measurement as `Controllers/common/pid.c` so on-screen behaviour matches the firmware), and step-metrics (rise 10–90 %, overshoot, settling ±5 %, ss error — the `StepMetrics.Compute` rules).
- **`tt-plot.js`** — dependency-free canvas line-plot widget (axes, autoscale, legend, hover cursor, zoom/pan) reused by control-lab, calibration and telemetry pages.
- **`tt-export.js`** — save layer: File System Access API (`showDirectoryPicker`, handle persisted in IndexedDB, permission re-request) with `Blob` download fallback; shows the two destination paths (editor: `<repo>\UnitySim\Vehicles\`; installed build: `%USERPROFILE%\AppData\LocalLow\<company>\<product>\Vehicles\`).
- **`tt-style.css`** — shared theme.

## The pages

### `Tools/index.html` — hub
Landing page: Tiny Torque header, card per tool with one-line description, quick-start notes (how vehicle JSONs get into the game, how DLLs get built).

### `Tools/car-setup.html` — Hardware → Vehicle wizard
Step-based wizard (left nav: Chassis → Wheels & Suspension → Drivetrain → Steering servo → Battery → Sensors → Aero/Extras → Review & Export), each step being "pick a configuration, then fill your part's datasheet numbers":

- **Chassis**: body shape picker (5 shapes with small SVG silhouettes), dimensions, chassis vs total mass (`useCompositeMass` explained), body colour.
- **Wheels**: layout presets (4-wheel RWD/FWD/AWD from wheelbase + track inputs) generating the four `WheelSpec`s; per-wheel radius/style/steering; suspension entered as natural quantities (spring rate, damping ratio ζ, travel) with the ride-frequency/sag readout formula from `VehicleStats`.
- **Drivetrain**: the key translator. Choose real topology — *one motor + diff* (Opus convention: auto-halving of extensive quantities across two sim motors, with the rationale shown) or *per-wheel hub motors*. Motor entry in **datasheet mode** (Kv or no-load rpm, nominal V, stall torque or R, no-load current) with the embedded converter showing derived kt/R live; ESC section (current limit, PWM steps, deadband, lag, drag brake, brake strength, reverse lock) with plain-language explanations of each.
- **Servo**: speed spec (s/60° → deg/s) and stall torque (kg·cm → N·m converter), Ackermann %.
- **Battery**: cell-count buttons (2S/3S…), capacity, internal resistance, mass, tray position.
- **Sensors**: catalog of real parts as one-click presets (VL53L1X ToF, BNO055 IMU noise figures, AS5047P/AMT102 encoders with CPR×4 quadrature note, Pi Camera) pre-filling noise/rate/latency the way the Opus preset does (e.g. ToF ±25 mm spec → σ=0.008 as 3σ; hardware encoder counters keep rate/latency/noise at 0), plus manual entry. Encoder `wheelIndex` picker bound to the wheel list.
- **Live preview**: top-down + side SVG schematic of the car redrawn from the working design (body outline, wheel rectangles at `localPos`, sensor dots with aim wedges, battery box, CoM marker from a JS port of the `MassProperties` point-mass sum) — instant feedback that positions are sane.
- **Stats panel**: top speed (drag-aware bisection like `VehicleStats`), stall force, weight distribution, ride frequency.
- **Review & Export**: validation report, pretty JSON preview, save via `tt-export.js`, plus "Send to Control Lab" (localStorage) and Import (load an existing vehicle JSON to edit).

### `Tools/control-lab.html` — Control Loop designer (the teaching page)
Loads a vehicle JSON (file picker, localStorage hand-off, or a bundled "Stock RC" example). Immediately derives and displays the plant constants (`BEMF_V_PER_MS`, `FORCE_PER_AMP_ALL`, `m_eff`, electrical/mechanical time constants) with every formula rendered and the numbers substituted — the same derivation chain as `mission_cfg.h`.

Interactive lessons, each = short math exposition + live plot + sliders:
1. **The DC motor transient** — first-order response, back-EMF as speed feedback, why τ_mech = m_eff·R·r²/(kt²·gear²·N); slider the motor params and watch the open-loop step.
2. **Speed control: feed-forward + PID** — build up `V = BEMF·v + R·I(F_req)` feed-forward, show it's ~95 % of the demand at cruise, then add the PID trim; kp/ki/kd sliders drive the `tt-sim.js` plant with live rise/overshoot/settling readouts; demonstrate anti-windup by toggling it against a saturating step.
3. **Steering / yaw rate** — kinematic bicycle feed-forward `δ = atan(L·ψ̇/v)` + yaw PID; trade-off discussion (understeer absorption, why FF-first).
4. **Braking to a mark** — `v_ref = √(2·a·s_rem)` distance-parameterised profile, ESC shorted-winding brake limits (force ∝ duty·v → fades at low speed), friction-brake blending.
5. **Trade-offs sidebars** throughout: FF vs pure PID, derivative-on-measurement vs on-error, deadband compensation, sample-rate/latency effects (the sim exposes a delay slider).

**C code generation** (per-lesson pieces assembling into one download set), following the repo conventions verbatim: a `<name>_cfg.h` with `VE_*`/`GA_*`/`LI_*` prefixed constants **derived from the loaded vehicle JSON** (wheel r, kt, R, gear, N motors, mass_eff, rail V, deadband, max steer) and the user's tuned gains from the sliders; `<name>.h`/`<name>.c` portable library (includes only `<math.h>`, `pid.h`, own cfg; caller-owned State struct; commented function skeletons with the speed/steer loops actually implemented from the lessons); `targets/sim/<name>_main.c` ABI glue cloned from the `car_main.c` pattern (memset-first, readiness guard, `ctrl_configure` name-based sensor binding with the user's actual sensor names from the JSON, `DBG_*` enum ↔ `ctrl_get_debug_names` lock-step); plus the ready-to-paste `add_controller(...)` CMake line, `build.ps1` invocation, and the note to set `controllerDll` in the vehicle JSON. Files save via directory picker (ideally straight into `Controllers/<name>/`) or individual downloads.

### `Tools/calibration.html` — Calibration companion
Walks the measured-calibration procedure from `Opus_Car_Spec/calibration.md` (which the exploration confirmed is the transferable artifact), for the sim car or the real one: encoder scale factor (steady-speed run: ground truth vs odometer → `CAL_SCALE`), coast-down drag polynomial (enter v(t) samples from a coast run → least-squares c0/c1/c2 fit in JS with the fit overlaid on the data), traction efficiency (steady-state force balance), brake calibration. Each stage: instructions, input table (manual or CSV paste), fit plot, and an accumulating `#define` block the user copies into their `_cfg.h`. Explains the two modelling lessons (slip loss lives in traction-eff not the drag poly; effective mass ≠ vehicle mass).

### `Tools/telemetry.html` — Telemetry analyzer
Drag-and-drop a CSV from `TelemetryLogs/` (+ optional JSON sidecar): parse header channels, channel multi-picker, stacked zoomable plots (`tt-plot.js`), automatic step-response metrics on a selected setpoint/measurement pair (same rules as `StepMetrics`), and **two-file compare mode** (overlay run A/B — the sim-vs-real diff workflow). Sidecar metadata (seed, delay ticks, baked metrics) shown side-by-side.

### `Tools/motor-converter.html` — standalone datasheet converter
Thin page over `tt-motor.js`: datasheet inputs → kt/R/derived table with formulas shown, the single-motor-diff halving helper, and a copy-as-JSON-fragment button (a complete 16-field `motor` object ready to paste into a vehicle JSON).

### JGraph integration (explored — concrete)
JGraph (`E:\EE Projects\JGraph`) is a .NET 8 MATLAB-workflow clone with a genuinely headless CLI: `jgraph.exe -batch "<statement-or-script-file>" [-showfigures] [-sd dir] [-logfile f]`; exit codes 0/1/2; script engines by extension (`.jgs`, `.m`, `.csx`, `.py`); reads CSV via `readcsv`; exports figures via `exportfigure("out.svg")` (png/jpg/svg/pdf). **No socket/pipe/stdin surface exists** — a fresh process per script is the only entry, which fits a file-based bridge exactly. Two facts shape the design:
- **JGS dialect is user-configurable** (`let`-required and 0/1 index base come from `%AppData%\JGraph\settings.json`, honoured even in `-batch`) → **generate `.m` scripts**, which always run under fixed MATLAB rules.
- **No control-systems builtins** (no `tf/step/bode/lsim` at script level) → the pages compute time/frequency responses in JS (`tt-sim.js`) and hand JGraph **plain data arrays via CSV** + a plotting `.m` script; JGraph adds its interactive figure windows, export quality, and further scripted analysis (`fft`, `filter`, `freqz` etc. are available).

Implementation:
- Control-lab, calibration and telemetry pages get an **"Open in JGraph"** button: writes (directory picker / download) a `data.csv` + generated `<name>.m` that `readcsv`s it and reproduces the current figure with titles/legends, plus a shown copy-paste command: `jgraph -batch "<name>.m" -showfigures -sd "<dir>"`.
- **`Tools/jgraph-bridge.ps1`** (optional live mode, documented on the hub page): locates `jgraph.exe` (PATH, then the known dev-tree path), then watches `Tools\jgraph-out\` and launches `jgraph -batch <newfile> -showfigures` on each new `.m` — so with the bridge running and the directory picker pointed at `jgraph-out`, clicking the button pops a live JGraph figure window. Degrades to nothing when JGraph isn't installed (pages plot with `tt-plot.js` regardless).

## Files

**New:** `Tools/index.html`, `Tools/car-setup.html`, `Tools/control-lab.html`, `Tools/calibration.html`, `Tools/telemetry.html`, `Tools/motor-converter.html`, `Tools/shared/{tt-style.css, tt-schema.js, tt-motor.js, tt-sim.js, tt-plot.js, tt-export.js}`, `Tools/jgraph-bridge.ps1`.
**Modified:** `README.md` (new "Interactive Tools" section), memory `project-overview.md` (Tools folder).
**Unity/C side: zero changes** — the tools produce files the existing game/toolchain already consumes.

## Steps

1. **Shared foundation** — `Tools/shared/` (style, schema+generator+validator, motor math, sim core, plot widget, export layer) + `index.html`. Verify: unit-check the JSON generator output against `UnitySim/Vehicles/Real Twin 2.json` field-for-field; kt/R math against the Opus preset numbers.
2. **Car Setup wizard** — full page; verify by exporting a vehicle into `UnitySim/Vehicles/` and loading it in the garage (drives, stats match the page's predictions).
3. **Motor converter** — thin standalone page (mostly done by step 1's module).
4. **Control Loop lab** — lessons + live sim + C generator; verify generated controller compiles: `gcc -fsyntax-only` then a real `build.ps1` build of a generated example, load it on a car, drive in Autonomous.
5. **Calibration companion** — procedure pages + least-squares fits; verify fits against the known i22 calibration numbers (feed it the recorded coast/cruise data → reproduces c0/c1/c2, CAL_SCALE class values).
6. **Telemetry analyzer** — CSV parse/plots/metrics/compare; verify with a real `TelemetryLogs` CSV + sidecar.
7. **JGraph integration + docs** — "Open in JGraph" (`.m` + CSV generation) on the three plotting pages, `jgraph-bridge.ps1`, README + hub-page docs, memory update. Verify with the real `jgraph.exe` at `E:\EE Projects\JGraph\src\JGraph.Cli\bin\Debug\net8.0\jgraph.exe`.

## Verification

- Export → garage round-trip: a wizard-built car loads in the game with no console errors, hidden-filter safe (mass ≤ 50), stats agree.
- Generated C: `build.ps1 -Target <name>` produces a loadable DLL; the car drives closed-loop in Autonomous with the taught speed/steer loops.
- Calibration page reproduces the i22 constants from recorded data; telemetry page renders an Opus mission trace CSV and computes plausible step metrics.
- JGraph: exported script opens/renders in JGraph via its CLI; bridge watcher round-trip works.
- All pages function offline from `file://` double-click in Chrome/Edge (directory picker) and Firefox (download fallback).

## Risks

- **JsonUtility strictness** — struct-vs-class emission rules are the sharp edge; mitigated by generating from the schema module and the field-for-field diff against a known-good file in step 1.
- **File System Access API** is Chromium-only — fallback path is first-class, with explicit destination instructions.
- **Schema drift** — future game iterations adding fields won't break the tools (unknown keys ignored, omitted keys default), but the hub page notes the tools' schema version; keep `tt-schema.js` in the same commit as schema changes.
- **JGraph coupling is one-directional and process-per-script** (confirmed: no IPC surface) — the bridge gives a "live" feel but true bidirectional sync would need a `-serve` mode added to JGraph itself; out of scope here, noted as a future option on the hub page.

---

# Iteration 22 — Brush tyre model + electrical realism (battery SoC, ESC state machine, servo load)

## Context

Iteration 21's calibration campaign proved the sim's fidelity floor is the **PhysX WheelCollider friction curve**: holding 4.5 m/s cost 53 % of commanded wheel force to longitudinal slip (`VE_TRACTION_EFF 0.47`) and the free-rolling front encoders under-read ground distance by 11.6 % (`CAL_SCALE 0.116`) — where a real RC tyre loses a few percent and 1–4 % respectively (calibration.md's own prediction). The stylized two-segment slip curve, not rubber physics, sets those magnitudes, so the calibrated constants are simulator artifacts that would not transfer to hardware. The user asked what's missing for sim-to-real correspondence and chose two upgrades:

1. **Custom brush tyre model replacing WheelCollider friction — for ALL vehicles** (user-confirmed; one physics path, presets get a feel retune, a global dev kill-switch stays for A/B during bring-up).
2. **Electrical realism bundle**: battery state-of-charge discharge (capacitymAh is currently reserved/dead), an ESC forward/brake/reverse state machine with drag brake — **default on for ALL vehicles** (user-confirmed, including manual driving: reverse requires passing through neutral), and a servo torque-speed limit (currently a pure 480 °/s slew).

The acceptance test is built in: after the swap, re-running the Opus Vector calibration procedure (Opus_Car_Spec/calibration.md, unchanged) must produce **physical** constants — CAL_SCALE in the 1–4 % range, traction efficiency ≳0.9 — and the mission must still complete fault-free.

## Part 1 — Brush tyre model (`Vehicles/TyreModel.cs` + CarVehicle rewrite)

**Architecture: WheelCollider stays, friction leaves.** The WheelCollider is kept for what it does well — suspension raycast, spring/damper force, `GetGroundHit` (contact point, normal, `hit.force` = live Fz) — and its friction is disabled by writing **stiffness 0** on both curves (PhysX: zero stiffness = zero tyre force). Everything else becomes ours:

- **Own wheel spin state.** `Wheel` gains `float omega` (rad/s) and `float spinAngle` (viz). Integration per wheel per `StepPhysics` (400 Hz, dt = 2.5 ms): `J·ω̇ = τ_drive − τ_brake − F_x·r − τ_roll`, with `J = 0.5·0.05·r² + cfg.extraSpinInertia`. The wc.mass inflation hack (CarVehicle.cs:659–661) is deleted — `wc.mass = 0.05f` flat; the reflected rotor inertia now lives where it belongs, in `J`.
- **Slip computation.** Contact-patch velocity = `_body.GetPointVelocity(hit.point)` projected into the steered wheel frame on the ground plane → `vx, vy`. Slip ratio `κ = (ω·r − vx)/max(|vx|, 0.2)`, slip angle `α = atan2(−vy, max(|vx|, 0.2))`.
- **Force model** (brush/Pacejka-lite with friction ellipse), in `TyreModel.Evaluate(in TyreState, in TyreParams, out Fx, out Fy)`:
  - Peak grip `µ = surf.frictionMult · cfg.gripMult · (Fz/Fz0)^(−cfg.loadSensitivity)` — this **replaces** the stiffness-churn load-sensitivity site (937–948) and its `lastMult` cache entirely.
  - Normalized magic-formula-shaped curve: linear region up to `κ_peak ≈ 0.10` / `α_peak ≈ 0.12 rad`, peak µ·Fz, gentle fall to ~0.85·peak at full slide. Combined slip via friction-ellipse scaling of the two pure-slip forces. Defaults tuned so asphalt (frictionMult 1.15) yields µ ≈ 1.1 — real RC rubber.
  - **Low-speed guards** (the classic slip-model traps, mandatory): below ~0.3 m/s blend the slip force toward a contact-patch damper; brake static-hold — when `|ω|` is small and brake torque exceeds what the tyre can react, clamp ω to exactly 0 rather than letting the integrator flip its sign in one step.
- **Application.** `AddForceAtPosition(Fx·fwdOnGround + Fy·rightOnGround, hit.point)`; reaction `−Fx·r` feeds back into ω̇. Rolling resistance (`surf.rollingResist·rollScale`, line 898 today) becomes a torque opposing ω instead of `brakeTorque`. The friction brake (`brake + handbrake`, lines 888/982) becomes a torque in the integrator with the static-hold clamp; **`wc.motorTorque`/`wc.brakeTorque` are never written again** (MotorPart.cs:104 changes to depositing torque into the wheel's integrator).
- **ω re-sourcing — all five `wc.rpm` readers** switch to the integrated omega via a `CarVehicle.WheelOmega(int)` accessor: MotorPart.cs:83 (back-EMF) and :51 (`MotorOmega` for IMU vibration), WheelEncoderSensor.cs:51, CarVehicle.cs:954 (ballooning LP) and :1074 (ABI `wheel_vel`).
- **Wheel viz spin.** `GetWorldPose` rotation no longer carries spin (wc has no torque). `UpdateVisual` keeps the pose **position** (suspension) but composes rotation itself: holder yaw/strut tilt + `currentSteer` + accumulated `spinAngle += ω·dt` roll.
- **Assists.** TC (974–976) and ABS (977–979) read our computed κ instead of `hit.forwardSlip`; same thresholds initially. Stability/steering assists, anti-roll (1044–1057), aero, rumble/roughness forces, suspension sensors (`GetSuspensionForce/Compression`), and ballooning's `wc.radius` write all survive unchanged — they never depended on PhysX friction.
- **`SetGrip` tunable** (1150–1160) re-maps to scaling the lateral µ/cornering stiffness in TyreParams (same slider, same intent).
- **Dev kill-switch:** `public static bool TyreModel.Enabled = true` (the `PartMeshLibrary.Enabled` pattern). False = legacy stiffness values restored and custom forces skipped — for A/B during bring-up only, stripped or left dormant after.
- **Unaffected (verified by exploration):** ClientCarView/NetSession (streams a kinematic estimate, NetSession.cs:538), BotDriver (interface-only), VehicleStats (calls MotorModel directly), diff-drive scene (WheelCollider-free already; its own slip model at DifferentialDriveVehicle.cs:126–128 is prior art in-repo).

## Part 2 — Battery SoC discharge

- **`VehicleFactory.cs:153–157`**: copy `capacitymAh` onto the car (today it stops at the JSON — the exploration confirmed it is never read).
- **`CarVehicle`**: coulomb counter `_battAhUsed += BatteryCurrent·dt/3600` in the battery block (799–802); `Soc = 1 − used/capacity` (clamped). Open-circuit voltage from a piecewise per-cell LiPo curve (4.20 V full → 3.70 nominal plateau → 3.30 knee → 3.00 empty vs SoC), `cells = round(nominalV/3.7)`; `V0 = cells·OCV(Soc)` replaces the constant `batteryNominalV` in the sag formula. **Sentinel: `capacitymAh = 0` = infinite = today's pinned rail** (old JSON unchanged). `ResetVehicle` restores full charge (determinism per run — mirrors `BatteryCurrent = 0` at :733).
- **`BatterySensor.cs:37`**: the hardcoded `1f` becomes the live SoC — firmware now sees a real `[V, A, soc]` triple.
- **Garage**: `Capacity mAh` slider (0–8000, "0 = ∞") in `DrawBatteryInspector` (GarageUI.cs:1303–1337). Opus preset already carries 5200 mAh (VehiclePresets.cs:460–468) — it simply goes live.

## Part 3 — ESC drive/brake/reverse state machine (all vehicles)

New `MotorParams` fields (struct; absent-in-old-JSON = 0): `escDragBrakePct` (0 = coast at neutral — today), `escBrakeStrengthPct` (0 → treated as 100), `escReverseLockMs` (0 → default 150 ms). The state machine is the model for everyone (no per-design flag, per user decision); a static `Legacy` dev switch mirrors the tyre one for A/B.

In `MotorPart.StepDrive` (78–106), after the existing deadband/PWM/slew/lag pipeline, the signed voltage is interpreted through a per-motor state machine instead of being fed straight to the DC model:

- **Drive** (cmd sign matches motion, or from rest): DC model unchanged — forward behaviour, top speed, and the VehicleStats solver are untouched.
- **Brake** (cmd opposes rotation while `|ω_motor|` above threshold): shorted-winding proportional brake — `duty = |v|/vmax · brakeStrength`, `I_brake = min(duty·kt·|ω_motor|/R, maxCurrent)`, `τ = −sign(ω)·kt·I_brake·gear·eff`. This is what a hobby ESC actually does; it can never spin the wheel backwards. Brake current circulates in the bridge, so it contributes **0** to pack current (small correctness fix vs today's `|I|` sum).
- **Reverse**: only after neutral (`|cmd| < deadband`) has been held `escReverseLockMs` with ω ≈ 0 — then negative volts drive the DC model. Manual feel change (reverse needs a beat in neutral) is the user-accepted consequence.
- **Drag brake**: at neutral with the motor spinning, apply `dragBrakePct` duty of the same shorted-winding brake.

**Opus firmware follows** (`Controllers/opus_mission/opus_mission.c` `longitudinal()`): the braking branch currently computes a regen force and inverts `V = bemf + R·I` — under the state machine, a negative command is a brake *duty*, so the branch becomes `duty = F_brake_wanted·R/(kt_total·ω_wheel·gear·…)` → `v_cmd = −duty·vmax`, with `EN_REGEN_CAP_N` re-derived (the cap is now `kt²·ω/R`-shaped: strong at speed, fading to nothing at rest — which is *why* the friction-brake blend exists). `mission_cfg.h` constants re-derived in Part 5's recalibration.

## Part 4 — Servo torque-speed limit

- **`VehicleDesign`**: `public float servoStallNm = 0f;` (0 = legacy pure slew — old JSON unchanged). Factory copies to CarVehicle.
- **`CarVehicle` steer loop (855–885)**: the brush model hands us per-wheel lateral force, so the load is real: `τ_load = Σ_steered |Fy|·trail` (pneumatic + mechanical trail ≈ 4 mm at RC scale) + a small constant friction term. Available slew = `steerRateDegPerSec · max(0, 1 − τ_load/servoStallNm)` feeding the existing `MoveTowards` (:882). At stall the servo holds but cannot add lock — steering authority collapses exactly when cornering load peaks, which is the real failure mode.
- **Garage**: `Servo stall N·m` slider 0–2 ("0 = ideal") beside the `Servo °/s` row (GarageUI.cs:950).
- **Opus preset**: Savox SC-1251MG datasheet is already cited (bill_of_materials.md §3): stall 0.883 N·m @ 6 V, no-load 667 °/s. Set `steerRate = 667`, `servoStallNm = 0.883` — the current hand-derated 600 °/s becomes emergent instead of assumed.

## Part 5 — Preset retune + Opus recalibration (the acceptance test)

1. **Feel pass** on the five presets on their themed maps, in-editor (computer-use, per the established workflow): Stock RC neutral, F1 planted, Drift Car still slides (low rear `gripMult` now maps to low rear µ — verify), Crawler still crawls, Rally Buggy lands jumps. Tune `TyreModel` global constants first, per-preset `gripMult` second.
2. **Re-run the Opus calibration procedure** exactly as written in `Opus_Car_Spec/calibration.md` (that procedure surviving unchanged is the point): re-measure CAL_SCALE (expect **0.01–0.04**), coast-drag polynomial, `VE_TRACTION_EFF` (expect **≳0.9**), `VE_MASS_EFF` (unchanged — rotor reflection is model-independent), and the new ESC-brake constants. Update `mission_cfg.h`, run the mission via `Tools ▸ AIHWSim ▸ Run Opus Mission`, iterate to `fault 0` completion with errors comparable to run 9 (±50 mm class).
3. **Docs**: calibration.md gains an "Iteration 22 recalibration" section contrasting the artifact constants with the physical ones (that before/after table IS the fidelity claim); README physics section; `sim_mapping.md` gap list updated (battery discharge, ESC commutation rows move from "unrepresented" to "modelled"); memory `project-overview.md` + `opus-vector-mission.md`.

## Files

**New:** `Vehicles/TyreModel.cs`.
**Modified:** `Vehicles/CarVehicle.cs` (the big one: wheel integrator, force application, steer-load servo, battery SoC, viz spin, assist re-sourcing), `Vehicles/MotorModel.cs` (MotorParams fields), `Sensors/MotorPart.cs` (ESC state machine, torque sink, ω source), `Sensors/WheelEncoderSensor.cs` (ω source), `Sensors/BatterySensor.cs` (live SoC), `Garage/VehicleFactory.cs` (capacity + servo copy), `Garage/VehicleDesign.cs` (`servoStallNm`), `Garage/GarageUI.cs` (capacity, drag-brake/brake-strength/reverse-lock, servo-stall rows), `Garage/VehicleStats.cs` (only if the solver needs the brake-path guard), `Garage/VehiclePresets.cs` (Opus/Real Twin values, retune), `Controllers/opus_mission/opus_mission.c` + `mission_cfg.h` (ESC-brake branch + recalibrated constants), `Opus_Car_Spec/calibration.md` + `sim_mapping.md` + `bill_of_materials.md` (drag-brake row), `README.md`, memory files.

## Steps (headless compile checkpoint after each; editor runs via computer-use as in i21)

1. **Tyre core** — TyreModel.cs, Wheel.omega/spinAngle, integrator, slip/force evaluation, application, low-speed guards, stiffness-0 + kill-switch, viz spin. Straight-line drive + brake test on the oval.
2. **ω re-sourcing + assists** — five rpm readers, TC/ABS on computed κ, SetGrip remap, rolling resistance as torque, delete the stiffness-churn site. Encoder sanity: `sens/enc_f*/vel` tracks `veh/speed` within a few %.
3. **ESC state machine** — MotorParams fields, StepDrive states, pack-current fix, manual reverse-lockout feel check.
4. **Battery SoC** — factory copy, coulomb counter + OCV curve, sensor, garage row, reset semantics.
5. **Servo load** — field, load estimate from Fy, garage row, Opus/Real Twin datasheet values.
6. **Preset feel retune** (editor play, all five presets).
7. **Opus firmware + recalibration** — brake branch rewrite, full calibration rerun, mission to fault-0.
8. **Docs + memory + regression** (split-screen, LAN ghost, bots, diff-drive scene untouched, garage).

## Verification

- Headless compile 0 `error CS` per step; `gcc -fsyntax-only` unchanged (no ABI change — same structs, same enum).
- **The two artifact constants become physical:** measured CAL_SCALE ∈ [0.01, 0.04]; measured traction efficiency ≳ 0.9 at 4.5 m/s. This is the pass/fail line for Part 1.
- Opus mission completes `fault 0` with leg errors in the ±50 mm class after recalibration.
- Battery: a long run shows `sens/battery1/soc` falling and top speed sagging; `capacitymAh 0` designs unchanged.
- ESC: manual reverse requires a beat in neutral; negative throttle at speed brakes without spinning wheels backwards; drag brake 0 coasts exactly as today.
- Servo: at speed + full lock the steer rate visibly derates; `servoStallNm 0` designs identical.
- Regression: presets drivable and in-character, split-screen/LAN/bots/diff-drive/garage unaffected, kill-switches A/B cleanly.

## Risks

- **Low-speed slip-model instability** is the classic failure of custom tyre code — mitigated by the specified guards (slip-denominator floor, low-speed force blend, brake static-hold) and the already-small 2.5 ms step; test standing starts, full-ABS stops, and shuffling at walking pace explicitly.
- **Handling change for all existing content** — accepted by the user; kill-switches allow A/B; presets retuned in Step 6.
- **The Opus firmware brake rework is a real firmware change** — its inverse model must match the new ESC brake physics, and every `VE_*`/`EN_*` brake constant is re-derived by measurement, not edited by feel. The calibration procedure already exists and transfers.
- **Wheel viz** must not regress in the kinematic contexts (garage preview, LAN ghost) that never run the integrator — they keep the existing pose paths.

---

# Iteration 21 — "Opus Vector": a datasheet-accurate RC car + a C closed-loop mission controller

## Context

Every vehicle in the sim so far has been *plausible* rather than *sourced* — `MotorParams.Default()` is a hand-tuned 540-class stand-in, sensor noise figures are round numbers, and no preset traces to a real bill of materials. At the same time the repo's headline claim — "write firmware in C, it drives the sim, the same source later runs on hardware" — has never been demonstrated by a controller that actually *does* something: all three existing controllers (`sim_main.c`, `car_main.c`, `car_sensors.c`) are single-mode reactive loops that need a human on the sticks to mean anything.

This iteration closes both gaps at once. It adds **one vehicle preset built entirely from real, cited part datasheets**, a **test range to run it on**, and a **multi-phase closed-loop C controller** that drives a precise dead-reckoning manoeuvre start to finish with zero human input:

> arm and self-check → accelerate to 4.5 m/s → hold 4.5 m/s for exactly **14.5 m** → turn **45° left without slowing** → after straightening, exactly **7.5 m** more at speed → brake to a full stop in exactly **1.5 m** (9.0 m total from the turn exit).

That manoeuvre is a genuine controls exercise: it needs odometry, heading estimation, a grip-limited braking profile, and a yaw-rate loop — all in portable C against the existing ABI, with the arcade assists force-disabled so the firmware faces raw physics.

**Decisions confirmed with the user:**
- **Toolchain:** no 64-bit C compiler exists on this machine (CMake 4.4 is installed; there is no `cl.exe`, and the only gcc is 32-bit MinGW 6.3). With the user's approval this iteration **installs mingw-w64 via winget** and extends `build.ps1` to use it, so the DLL is actually built, loaded and driven this session.
- **The 14.5 m datum:** the car accelerates in a separate run-up zone, and the 14.5 m is measured as **true constant-velocity travel** after 4.5 m/s is reached.
- **Spec folder:** `Opus_Car_Spec/` gets transcribed spec sheets with source URLs *and* the downloaded manufacturer datasheet PDFs (each file named and sourced before download).

Naming (my choice, per the user): vehicle **"Opus Vector"**, track **"Opus Proving Ground"**, firmware **`opus_controller.dll`**.

## The real vehicle being modelled

An **F1TENTH-class 1/10 autonomous research car** — the archetype the sim's RC-scale physics was already sized for (0.42 m body, 66 mm wheels, ~2 kg, 2S). Every parameter in the preset traces to a row in `Opus_Car_Spec/`, and every row is tagged **published** (straight off a datasheet) or **derived** (computed from published values, with the formula shown) or **estimated** (with the basis stated). That published/derived/estimated distinction is the point of the folder — RC vendors publish Kv, mass and dimensions but almost never winding resistance, no-load current or rotor inertia, and pretending otherwise would be dishonest.

Bill of materials → sim mapping (final numbers land during Step 1 from the datasheets):

| Subsystem | Real part | Key published spec | Where it lands in the sim |
|---|---|---|---|
| Drive motor | Castle Creations 1410-3800Kv 4-pole sensored brushless | 3800 Kv, 36 mm × 52.7 mm, 239 g, 2–3S | `kt = 9.5493/Kv` (derived) → ≈0.00251 N·m/A; `resistance`, `noLoadCurrent`, `rotorInertia` derived/estimated with method shown |
| ESC | Castle Sidewinder SV3 (or QuicRun 10BL120) | continuous/burst current | `maxCurrent` = **half** the real limit per sim motor (see split below) |
| Steering servo | Savox SC-1251MG | 0.09 s/60° @6 V, 9.0 kg·cm, 44.5 g | `steerRate` ≈ 600 °/s (derated from 667 °/s no-load) |
| Battery | 2S 7.4 V LiPo shorty pack | capacity, mass, cell IR | `BatterySpec` `nominalV 7.4`, `internalR ≈ 0.02`, real `massKg` |
| Range ×3 | ST **VL53L1X** ToF | 4 m, 50 Hz, ±25 mm, 27° FoV | `range 4`, `updateRateHz 50`, `latencyMs 20`, `noiseStd` from the ±25 mm spec |
| IMU | Bosch **BNO055** | gyro 0.014 °/s/√Hz, 0.3 °/s noise | `imuVibration`, IMU noise figures |
| Wheel encoders ×4 | Magnetic quadrature (AS5047P / AMT102-V class) | CPR | `cprTicks 4096`, `encoderGearRatio 1` |
| Camera | Pi Camera Module 3 / Arducam OV9281 | resolution, fps | `camWidth/Height/Fov/RateHz` |
| Compute | Raspberry Pi 5 8 GB (or Jetson Orin Nano) | board mass | PCB line in the mass budget |
| Shell | 3D-printed PLA/PETG lexan-style body | printed volume × density | mass budget; `bodyShape = LowRacer` |

**The one modelling compromise, documented in the spec folder:** the sim gives every powered wheel its own motor, but the real car has **one** motor driving both rear wheels through a diff. The preset therefore uses **two rear sim-motors carrying the real motor's electrical constants with `maxCurrent` set to half the real ESC limit each**, so total wheel torque and total battery draw match the real single-motor drivetrain. Without the halving the car would have exactly twice the real thrust.

`useCompositeMass = true` with `mass` set to the bare chassis figure, so `MassProperties.Compute` produces the total from the real per-part masses (this matters — four of the five existing presets leave the flag false and are ~0.7 kg heavy as a result).

## The C mission controller

New target **`opus_controller.dll`**, built from portable C that includes only `controller_api.h` plus the existing `common/pid.c`:

```
Controllers/opus_mission/  mission_cfg.h  odometry.c/h  heading.c/h
                           longitudinal.c/h  lateral.c/h  arming.c/h  mission.c/h
Controllers/targets/sim/opus_main.c        # ABI glue only, mirrors car_main.c's shape
```

CMake gains one `add_controller(opus_controller targets/sim/opus_main.c opuslib)` line following the existing `carlib` pattern. No malloc, one static state struct, MCU-portable.

**Phases:** `BOOT → ARM_STATIC → ARMED → LAUNCH → CRUISE_A(14.5 m) → TURN(45°) → CRUISE_B(7.5 m) → BRAKE(1.5 m) → CREEP → HOLD → DONE`, plus `FAULT`. Every distance target is an **absolute odometer value** and nothing is reset at a phase boundary, so the 10 ms control grid costs no accuracy.

**Odometry — the accuracy-critical part.** Integrate the two **unpowered front** wheels' encoder **tick counters**, not `ang_vel` and not `wheel_vel[]`. Verified in `Sensors/WheelEncoderSensor.cs`: `dest[offset+1] = wrapped` ticks is the *only* un-noised channel (noise is applied to velocity), and the re-derived velocity carries no information the tick delta doesn't. `wheel_vel[]` is worse still — `CarVehicle.SampleSensors` re-quantises and re-noises it. At `cprTicks = 4096` one tick is **0.0506 mm** and cruise is 888 ticks per control period against a 32768 half-wrap, so wrap detection is unambiguous. Three corrections the naive version misses:
- **Forward-Euler bias.** `_accumAngleRad += omega * dt` sampled before `StepPhysics` is a left-endpoint sum, so a decelerating leg measures short by `(dt/2)·Δv` — exactly **22.5 mm** on the 4.5 → 0 braking leg. Corrected continuously with `+ 0.5·dt·(v_legstart − v_now)`.
- **Free-rolling front slip is the dominant error, not quantisation.** `wheelDampingRate = 0.0002` makes each front wheel react ~0.83 N of drag through its contact patch, so the encoder reads slower than the ground by ~1–4 % — i.e. **0.14–0.6 m over 14.5 m**, one to two orders of magnitude larger than everything else combined. Handled by a measured scale calibration (below), and optionally by lowering `wheelDampingRate` for unpowered wheels.
- **`balloonPct = 0` on the front wheels.** Ballooning rewrites `wc.radius` at runtime while the encoder integrates `rpm`; 3 % growth at speed is 129 mm of error over 14.5 m. Rears keep it for drivetrain realism.

**Heading.** Primary source is the **front-encoder differential** `Δψ = (Δs_R − Δs_L)/t_front` — geometric, drift-free, ±0.02° per tick over the 45° turn — fused (complementary, τ ≈ 0.3 s) with `gyro[1]`. The gyro's sign is **learned at runtime** rather than assumed: accumulate `Σ gyro[1]·ψ̇_enc` and latch the sign once confident. (Verified from `ImuSensor.Read`: gyro is body-frame Unity XYZ with Y up, and Unity being left-handed means a **left turn gives negative `gyro[1]` and needs a negative `actuator[6]`** — but the controller asserts this instead of trusting it.)

**Longitudinal.** Force-based rather than voltage-based: the speed loop produces an acceleration, which becomes a force, a per-motor current, and finally a voltage through the inverse model `V = (kt·gear/r)·v + R·I`. At 4.5 m/s the back-EMF feed-forward is 4.05 V of a 4.17 V demand — **97 % feed-forward**, which is why this loop can be accurate; `pid.c` supplies only a ±4 m/s² trim. Braking follows a distance-parameterised profile `v_ref = √(2·a·s_rem)` with `a = 6.75 m/s²` (4.5 m/s → 0 in 1.5 m), which is **43 % of the 1.6 g all-wheel friction budget** — comfortable — plus a `−v·T_dead` lead term for the 20 ms of loop dead time. Regen alone cannot do it (rear-only would need 94 % of peak rear grip as the car slows), so the friction brake blends in, peaking at only **`brake_cmd ≈ 0.06`**. Hard rule: **brake released for the final 40 mm**, where a `CREEP` phase closes on position (0.05 mm resolution) instead of velocity and places the car to ±0.5 mm against its own odometer.

**The turn.** Target `a_lat = 4.0 m/s²` → **R = 5.06 m, ψ̇ = 50.9 °/s, kinematic steer 3.44°, arc 4.65 m, 1.03 s**. The binding constraint is *not* grip (20 % of the sideways budget) but **inner-wheel load**: at 8 m/s² the lateral transfer equals the static corner load and the inner front lifts, killing the odometer mid-turn. A trapezoidal yaw-rate reference makes `∫ψ̇_ref dt = π/4` exact by construction; a yaw-rate PID on top of kinematic feed-forward absorbs the ~3° of understeer that cannot be known in advance. Turn exit latches the datum for the 7.5 m leg, so that leg is exact by definition and the residual heading error is *reported*, not propagated.

**Arming.** Honest about what a stationary car can prove: `ARM_STATIC` (3 s) checks that `ctrl_configure` ran, that ≥2 front encoders and ≥1 motor with a sane `actuator_index`/voltage range are in the manifest, battery volts and idle current, `dt` stability, IMU plausibility (`|accel| ≈ 9.81`, `|gyro| < 0.02`), no NaNs, and — brake released for 2 s — that the counters stay put. **Encoder liveness can only be proven by moving**, so the first 0.5 m of `LAUNCH` also verifies both front counters advance and agree within 10 %. Faults surface as a bitmask.

**Debug channels** (all 16 named, auto-graphed and CSV-logged): `state, fault, odo_m, leg_rem_m, v_meas, target_speed, v_err, yaw_deg, yaw_rate, steer_cmd, motor_v, i_cmd, brake_cmd, batt_v, slip_pct, stop_err_mm`. Slot 5 is named `target_speed` deliberately — `SimulationRunner.SaveTelemetry` falls back to `StepMetrics.Compute(Hub, "dbg/target_speed", "veh/speed")`, so that name buys free rise/overshoot/settling metrics in the CSV sidecar.

## Unity-side touchpoints (small and surgical)

1. **`Vehicles/CarVehicle.cs` — publish ground truth.** `PublishTelemetry` writes `veh/pos_x`, `veh/pos_z` (both already registered in `SimulationRunner.RegisterChannels` and **never written** — a real dead-channel bug) plus a new `veh/yaw_deg`, which must also be registered explicitly (`CsvLogger.Begin` snapshots columns but `Commit` iterates live, so a late auto-registered channel widens rows past the header). This is the instrument that verifies "exactly 14.5 m".
2. **`Garage/VehicleDesign.cs` + `Core/TrackBootstrap.cs` — a design names its firmware.** New `public string controllerDll = "";`; `BuildPlayerRig` replaces the hard-coded `"Plugins/x86_64/car_controller.dll"` with a `SafeDllName()` helper (rejects `/`, `\`, `..`; appends `.dll`; falls back to today's default when empty). Old JSON loads unchanged.
3. **`Vehicles/CarVehicle.cs` + `Core/SimulationRunner.cs` — re-init on respawn.** Verified: `CarInput.Update` calls `ResetVehicle()` on **R** even in Autonomous mode, and `ResetVehicle` zeroes body velocity and wheel torques but **not** WheelCollider spin — and `WheelEncoderSensor._accumAngleRad` has no reset path at all. The odometer keeps counting phantom metres after a respawn. Fix: a `CarVehicle.VehicleReset` event that `SimulationRunner` subscribes to, re-running `ctrl_init` + `ConfigureControllerSensors()` and clearing the actuation-delay ring. (The controller also self-detects the teleport via the accel spike, as a backstop.)
4. **`Core/SimulationRunner.cs` — `GraphProfile.Mission`.** The `Car` profile graphs `sens/tof_*` and `cmd/<motor>/volt`, almost none of which this run cares about. New panes: speed, distance, yaw, electrical, slip/stop-error.
5. **New `Telemetry/MissionHud.cs`** — bound in `TrackBootstrap`'s solo-human branch beside `SensorHud`/`MetricsOverlay`. Shows connection/arm state and fault bits from `dbg/state`/`dbg/fault`, the live phase, `dbg/odo_m` **next to the ground-truth distance from `veh/pos_x/pos_z`**, and the latched stop error. Degrades to "no mission controller" when those channels are absent, so it is harmless on other cars. This is the user-visible answer to "add a way of verifying it's connected".
6. **Called out for approval, not assumed:** `MakeWheel` setting `wheelDampingRate = cfg.powered ? 0.0002f : 0.00002f`. It is physically right (an undriven wheel has bearing drag, not gearbox drag) and cuts the dominant odometry error ~10×, but it is a **global physics change** that slightly alters every existing vehicle's coast-down. Easy to drop if unwanted — the calibration path works without it.

## The track — "Opus Proving Ground"

A new code preset in `TrackEd/TrackPresets.cs`. A **spline ribbon**, not painted tiles: tiles are axis-aligned 1 m squares so a 45° leg would staircase, and the ribbon's `SurfaceTag` guarantees consistent asphalt grip under the braking phase (which is what makes 1.5 m repeatable).

Geometry, laid out from the mission itself: 5 m run-up → 14.5 m straight → 5.06 m-radius 45° left arc (4.65 m of path) → 9.0 m exit leg → run-off. That is ~29.4 m in X by ~7.8 m in Z, so a **40 × 20 m** map (well inside the `Resize` clamp of 60) with the path centred. Ribbon width 3.0 m, asphalt, kerb stripes through the corner, grass painted outside.

Styling: spawn pad at the start facing +X (yaw 90, matching the spawn arrow's local +Z convention), a start gate, distance-marker boards every 5 m set well back from the line, a highlighted apex marker at the turn, a `finish` gate placed exactly on the stop point as the visual target, light posts, and tyre-stack run-off. **Gate `yawDeg` will be set to the heading of travel through each gate** — the existing presets all get this wrong (their checker strips lie *along* the road rather than across it) and this preset should not copy the bug.

## Files

**New:** `Opus_Car_Spec/` (spec markdown per subsystem, `opus_vector_parameters.json`, `mass_budget.md`, `sim_mapping.md`, `calibration.md`, `datasheets/*.pdf`); `Controllers/opus_mission/*` (7 files); `Controllers/targets/sim/opus_main.c`; `UnitySim/Assets/Scripts/Telemetry/MissionHud.cs`.
**Modified:** `Controllers/CMakeLists.txt`, `Controllers/build.ps1`; `Garage/VehiclePresets.cs`, `Garage/VehicleDesign.cs`; `TrackEd/TrackPresets.cs`; `Vehicles/CarVehicle.cs`; `Core/SimulationRunner.cs`, `Core/TrackBootstrap.cs`; `README.md`; memory `project-overview.md` + `toolchain-64bit-requirement.md`.
**Reused, not rewritten:** `common/pid.c` (conditional-integration anti-windup, derivative-on-measurement), the `add_controller` CMake function, `VehiclePresets`/`TrackPresets` `(name, Func<>)` table pattern, `TrackPresets`' `New`/`PaintRect`/`It`/`Spline` helpers, `SensorRig` manifest assembly, `NativeControllerLoader` shadow-copy hot reload, `StepMetrics`.

## Steps

Each Unity step ends with a headless batch compile (editor closed, poll for the Unity process to exit, grep `error CS`); each C step ends with a rebuild of all four DLLs.

1. **Toolchain.** `winget search`/`install` a 64-bit mingw-w64 (WinLibs POSIX-UCRT or LLVM-MinGW) — a system change the user has pre-approved; confirm `-dumpmachine` reports `x86_64-w64-mingw32`. Extend `build.ps1` with a non-MSVC path: direct `gcc -shared -m64 -O2 -static -static-libgcc` (static linking is **required** — a DLL importing `libgcc_s`/`libwinpthread` fails to load in Unity), keeping today's `cmake -A x64` path when `cl.exe` exists. Smoke-test by building the two existing controllers and loading one in the editor.
2. **`Opus_Car_Spec/`.** Research and cite each part; list every PDF (filename, source URL, size) before downloading; write the spec sheets, the mass budget, the published/derived/estimated parameter table, the JSON, and the diff-vs-diff drivetrain rationale.
3. **Unity host touchpoints** — items 1–5 above (+6 if approved). No behaviour change for existing content beyond the two dead channels starting to carry data.
4. **"Opus Vector" preset** in `VehiclePresets.cs`, every field traced to the spec folder; `controllerDll = "opus_controller.dll"`.
5. **"Opus Proving Ground" preset** in `TrackPresets.cs`. Drive it manually first to confirm the ribbon, spawn heading and grip.
6. **The C controller** — `mission_cfg.h` and the six modules, then `opus_main.c`, then the CMake target. Build.
7. **Calibrate and tune.** Run in Autonomous, capture CSV, measure the odometer scale `K_SCALE` against `veh/pos_x/pos_z` on a steady 4.5 m/s leg and the brake-slip term on a braking run, bake both into `mission_cfg.h` with the measurement written up in `Opus_Car_Spec/calibration.md`, then iterate the three distances to spec.
8. **Docs + memory.** README sections for the preset, the track and the mission firmware; update `project-overview.md` and correct `toolchain-64bit-requirement.md` (CMake is now installed and a 64-bit chain exists).

## Verification

- **Toolchain:** `opus_controller.dll` exists in `Assets/Plugins/x86_64/`, and the editor logs `controller: LOADED` rather than `Running open-loop (no controller)`.
- **Arming:** on entering the track the HUD shows CHECKING for ~3 s then ARMED with `fault = 0`, and the car does not move. Unplugging the DLL (rename it) must show the fault path, not a silent roll-away.
- **The mission, from the CSV and the graph overlay** — this is the acceptance test, measured against **ground truth** (`veh/pos_x/pos_z`), not the controller's own odometer:
  - constant-velocity leg = 14.5 m ± target tolerance, with `veh/speed` flat at 4.5 m/s across it;
  - `veh/yaw_deg` changes by 45° ± 1° through the turn with **no dip in `veh/speed`**;
  - post-turn leg = 7.5 m at 4.5 m/s, braking distance = 1.5 m, total 9.0 m from the turn-exit datum;
  - final `dbg/stop_err_mm` latched and reported honestly.
  Expected accuracy after calibration: **±3–5 mm on the final 1.5 m, ±10–15 mm on the 9.0 m and the 14.5 m**; uncalibrated it is ±150 mm, which is why Step 7 is a step and not a footnote.
- **Repeatability:** with a fixed `noiseSeed`, two runs produce near-identical CSVs. (Determinism is not accuracy — both get reported separately.)
- **Respawn:** press **R** mid-run; the controller must disarm, re-arm and re-run cleanly rather than carrying phantom odometry.
- **No regressions:** the other four presets and the seven existing tracks build and drive unchanged; `car_controller.dll`/`car_sensors_controller.dll` still load on cars with no `controllerDll` set; split-screen/LAN/garage/builder untouched; headless compile clean.

## Risks

- **The odometer scale factor is the whole game.** Free-rolling front-tyre slip is 1–2 orders of magnitude larger than every other error source. Mitigated by an explicit measured calibration step — which is also the honest outcome, because *the same effect dominates on real hardware*, and the calibration procedure written here transfers verbatim to the physical car.
- **mingw-built DLL fails to load** if it imports the GCC runtime — pinned by `-static -static-libgcc` and caught immediately by the Step 1 smoke test.
- **`brake_cmd` corrupting the odometer.** The brake applies to *all* wheels (verified in `StepPhysics`), so the fronts slip under braking. Bounded by the 60 %-of-rear-grip regen blend (peak `brake_cmd ≈ 0.06`, 7.8 % of the front lock threshold) and eliminated for the last 40 mm by releasing the brake entirely during `CREEP`.
- **The encoder→wheel binding is a name contract.** `SensorInfo` carries no wheel index, so the controller matches `enc_f*` for the fronts. Documented as a hard requirement of the Opus preset, with a runtime cross-check (the undriven pair is the slower pair under acceleration) that warns rather than aborts. An ABI v4 `wheel_index` append is the clean fix if this ever bites twice.
- **Encoder realism settings must stay off.** Copying `RealTwin`'s 50 Hz / 20 ms sensor realism onto the *encoders* would inject 90 mm of odometry lag at 4.5 m/s. Real quadrature counters are read synchronously, so `updateRateHz = 0, latencyMs = 0` is the more realistic choice, not a cheat — and it is called out in the preset with a comment.
- **Vendor data gaps are real.** RC manufacturers publish Kv, mass and dimensions but not winding resistance, no-load current or rotor inertia. The spec folder labels every such value derived or estimated and shows the method; it will not present a computed number as a datasheet number.
- **A 45° turn at 4.5 m/s is inner-wheel-load limited, not grip limited.** Holding `a_lat` at 4.0 m/s² keeps the inner front at ~51 % of static load; pushing toward 8 m/s² lifts it and destroys the heading and odometry sources mid-turn.

---

# Iteration 20 — Hard-surface SubD rebuild of all eight part meshes

## Context

Iteration 19 made the three body shells *smooth* — but it did so by lofting superellipse rings and drowning them in a Subdivision Surface. That is precisely the "sculpted blob" the user's new **Blender Procedural Modeling Guidelines** forbid: the shells have no panel lines, no characteristic creases, no crisp wheel-arch edges, no flat regions, and their form is *defined* by subdivision rather than merely smoothed by it. The wheels, battery, and antenna (iteration 14) are compound-primitive-grade and equally short of the standard.

The user has supplied a full hard-surface authoring standard (industrial-designer workflow: blocking → primary forms → secondary forms → panel lines → creases → support loops → bevels → SubD → normals cleanup → validation) and wants the game assets re-worked to it. References remain `example car models/car_example*.jpg` — a Tamiya touring chassis (blue stick pack, 5-spoke rims, slick touring tyres) and two F1TENTH/JetRacer builds (knobby tyres, mesh-spoke rims, rubber-duck antennas).

**Confirmed scope (user):**
- Rebuild **all 8 existing FBX**: `body_shell`, `body_lowracer`, `body_buggy`, `wheel_slick`, `wheel_knobby`, `wheel_rally`, `battery_stick`, `antenna_stub`. The still-procedural parts (camera, ToF, encoder, suspension sensor, coil-over strut, aero wing/splitter/dam/canard) stay primitive this iteration.
- **Real battery, smaller per-wheel motor**: battery becomes a true 1/10 pack at **138 × 47 × 25 mm**; the motor can (a *procedural* part of the wheel viz) is recalibrated to a realistic **2836-class brushless ≈ 28 mm ⌀ × 36 mm**, not a full 540 — the game models one motor *per wheel*, and four 36 × 50 mm cans next to 66 mm tyres would look absurd.
- **Balanced poly budget**: body ~4–6k tris, wheel ~2–3k, battery/antenna ≤ 600. Clean control cage + SubD level 2 on bodies, level 1 elsewhere, applied before export. Worst case ≈ 16k tris/car → ~64k for a 4-car split-screen/LAN scene — comfortably within budget.

**This is an art/asset iteration.** Physics is untouched: aero drag/lift is keyed on the `BodyShape` **enum** via `AeroDynamics.BodyCd/BodyClA`, and all collision comes from the root `BoxCollider` + per-wheel `WheelCollider`. Part meshes are cosmetic geometry only. The C# changes below are three small, surgical touch-ups — no gameplay logic changes.

## Blocking prerequisite

**Blender must be running with the MCP addon connected (localhost:9876) and `Blender/parts.blend` open.** Verified *not* connected right now (`get_objects_summary` → connection refused). No modeling can start until it is.

## The authoring standard (applied identically to every asset)

Each asset follows the user's progression, executed via `execute_blender_code`:

1. **Reference analysis** — measure the target against the reference photo and the real-world dimension table (below). Never invent proportions.
2. **Primitive blocking** — a cube/cylinder cage with *only* the loops needed for width, height, wheelbase, roofline, hood, rear profile. The low-poly cage must already read as the object.
3. **Primary → secondary forms** — bulk shape, then fender flares, side-sill curve, roof scoop, diffuser.
4. **Panel lines** — shallow **inset** geometry (uniform, parallel, consistent width), not deep carved grooves.
5. **Creases + support loops** — every hard edge gets a support loop or small bevel; flat regions (underbody, battery sides, motor end caps, spoke faces) get loops so they stay *perfectly flat* through SubD. No reliance on sharp shading alone.
6. **Small details + uniform bevels** — bolts, vents, mount bosses, wire exits, connector recesses; small bevels only, never oversized.
7. **Mirror** — model one side, apply the Mirror modifier only once topology is final.
8. **SubD apply** — level 2 (bodies) / level 1 (rest), applied, then **Shade Auto Smooth** + a **Weighted Normal** modifier.
9. **Cleanup + validation** — merge doubles, recalculate normals outside, remove interior faces / loose verts / non-manifold edges, then a scripted mesh report (tri count, quad %, n-gons, non-manifold edges, loose verts, doubles) that must come back clean before export.

Topology rules held throughout: all-quads in the cage, continuous loops, minimal poles, no triangles/long-thin faces/star vertices; wheel openings stay **circular** with consistent arch thickness; large surfaces (hood, roof, doors, rim barrel, motor can) stay mathematically smooth with no waviness.

## Hard contract every asset must honour (drop-in replacement)

Confirmed from `Vehicles/CarVehicle.cs:441-493`, `Vehicles/PartMeshLibrary.cs`, `Vehicles/PartVisualFactory.cs:84-148,255-320`, `Assets/Editor/PartModelPostprocessor.cs`:

| Asset | Frame + origin | Authored size | Notes |
|---|---|---|---|
| `body_*` | Blender X = width, +Z = up, length along Y with **nose at −Y** (→ Unity nose +Z) | **X = 0.200 m, Y = 0.420 m** exactly; Z (height) free | Runtime scales per-axis by `bodySize / BodyMeshAuthorSize (0.20, 0.10, 0.42)`. Single object, one material slot, **non-overlapping UV map** (Smart UV Project, angle 66°, margin 0.02) — the garage paint mode cooks a MeshCollider and reads `RaycastHit.textureCoord`. |
| `wheel_*` | **Axle along +X**, origin at wheel centre | Outer tyre radius **exactly 0.0330 m** (66 mm ⌀) | Scaled uniformly by `radius / 0.033`. Ballooning rescales the holder's Y/Z, so the axle must stay +X. |
| `battery_stick` | Long axis **+Z**, origin at pack centre | **0.047 (X) × 0.025 (Y) × 0.138 (Z) m** | Built at identity with **no runtime scale** — authored size *is* rendered size. |
| `antenna_stub` | **+Y up**, origin at the SMA base | ~0.095–0.110 m tall | Runtime applies tilt about X and a 0.6–1.6 size scale. |

- **Object naming drives materials.** `PartMeshLibrary.AssignByName` matches case-insensitive substrings, so every sub-object must be named with a recognised token: bodies are single-object; wheels use `tire_*` / `rim_*` (+ new `hub_*`, `stud_*`, `brake_*`); battery uses `wrap_*` / `cell_*` / `term_*` / `nub_*` / `lead_*`; antenna uses `base_*` / `sma_*` / `whip_*`. The FBX's own materials are ignored.
- **Same filenames**, overwritten in place under `UnitySim/Assets/Resources/PartModels/`.
- **FBX export (the ×100 gotcha — non-negotiable):**
  ```python
  bpy.ops.export_scene.fbx(filepath=path, use_selection=True,
      apply_unit_scale=False, global_scale=0.01,      # cancels the m→cm bake
      axis_forward='-Z', axis_up='Y', bake_space_transform=True,
      object_types={'MESH'}, use_mesh_modifiers=True,
      mesh_smooth_type='EDGE', use_tspace=True, path_mode='COPY')
  ```
  `PartModelPostprocessor` forces `useFileScale=false, globalScale=1`, so Unity reads the FBX's raw numbers; the default `apply_unit_scale=True` bakes a ×100 metre→cm conversion and imports everything 100× oversized. `mesh_smooth_type='EDGE'` (was `'FACE'`) carries the auto-smooth/weighted-normal split normals through.

## Per-asset design

### Bodies (~4–6k tris each, SubD 2)

Cage built from a blocked half-shell, mirrored. All three get: crisp **wheel-arch edges** with consistent arch thickness, a **side body line** running nose→tail, **panel-gap insets** at the hood/door/deck breaks, a defined **front splitter** lip and **rear diffuser**, and a flat underbody that stays flat through SubD.

- **`body_shell` (touring, `car_example.jpg`)** — GT/touring lexan silhouette: low sloped hood with a shallow centre crease and hood vents, curved windscreen rise into a smooth roofline, tapered rear deck with an integrated ducktail lip, muscular flared fenders over all four arches, side skirts. Authored height ≈ **0.075 m**.
- **`body_lowracer` (F1TENTH, `car_example3.jpg`)** — very low aero wedge: pointed splitter-led nose ramp, flat deck for the compute stack, a small central canopy bump, faired side pods with air ducts, rear diffuser. Authored height ≈ **0.045 m**.
- **`body_buggy` (`car_example2.jpg`)** — off-road: taller rounded cab, prominent flared arches with a hard flare edge, hood scoop, chunkier rocker/side-nerf line, rear wing shelf. Authored height ≈ **0.095 m**. Fender flares may exceed the 0.200 m core width by up to ~8% (as today) — the *core* body stays 0.200.

### Wheels (~2–3k tris each, SubD 1)

Each is a two-object pair (`tire_<style>` + `rim_<style>`) plus a small `hub_<style>` / `stud_<style>` where visible. Per the guidelines the **rim** gets a centre hub, lug area, real-thickness spokes (never paper-thin), a rim lip, an inner barrel, and brake clearance; the **tyre** gets rounded shoulders, a slight sidewall bulge, a bead transition, and tread grooves.

- **`wheel_slick`** — 5-spoke touring rim + slick/shallow-groove tyre, ~26 mm tread width (`car_example.jpg`).
- **`wheel_knobby`** — dish rim + directional lugged off-road tyre, wider ~34 mm tread (`car_example2.jpg`).
- **`wheel_rally`** — mesh/multi-spoke rim + fine rally tread, ~28 mm (`car_example3.jpg`).

All three keep the outer radius at exactly 0.0330 m so `radius / WheelAuthorRadius` scaling stays 1.0 at default.

### `battery_stick` (~400–600 tris, SubD 1)

A real 1/10 **138 × 47 × 25 mm** pack: softened corner radii (never a sharp cube), heat-shrink wrap read as a slightly inset seam band, main power leads exiting one end into a connector housing, and a small balance-lead plug with a recessed connector. Matches the blue stick pack in `car_example.jpg`.

### `antenna_stub` (~250–400 tris, SubD 1)

Rubber-duck whip per `car_example2/3.jpg`: knurled **SMA base** with a hex flat and a chamfer, a hinge/elbow, and a gently tapered flexible whip with a rounded tip. ~100 mm tall along +Y.

## Code touchpoints (small, surgical)

1. **`Vehicles/PartVisualFactory.cs` — motor can.** `BuildMotorCan` (line ~137) currently yields ≈ 23 mm ⌀ × 36 mm at the default 33 mm radius. Recalibrate the multipliers so the default radius produces **≈ 28 mm ⌀ × 36 mm** (keep it proportional to radius so oversized wheels still look sane), and add a small chamfered end-cap piece. Stays procedural — the motor is out of the mesh scope.
2. **`Vehicles/PartVisualFactory.cs` — material token maps.** Extend the `AssignByName` calls for the wheel (line ~95), battery (~260) and antenna (~297) with the new sub-object tokens (`brake`, `barrel`, `lip`, `shoulder`, `connector`, `plug`, `wire`, `hex`) so added detail geometry picks up the right shared material instead of the fallback.
3. **`Assets/Editor/PartModelPostprocessor.cs`.** Add explicit `mi.importNormals = ModelImporterNormals.Import;` so the authored weighted/split normals are used rather than recalculated (today it relies on the default). `isReadable = file.StartsWith("body_")` and everything else stays.
4. **`Assets/Editor/PartModelValidator.cs`.** Upgrade `Report()` from a bare log to a **pass/fail check** against an expected-size table (per the contract above, ±2 mm tolerance) plus a triangle-count readout, so the headless verification step is unambiguous.

**No changes to:** `CarVehicle.cs` (`BodyMeshAuthorSize` stays `(0.20, 0.10, 0.42)` — the rebuilt bodies normalize *to* it), `PartMeshLibrary.cs`, `AeroDynamics.cs`, `VehicleDesign.cs`, presets, or any gameplay/net/telemetry code.

## Files

- **Modified (assets):** `Blender/parts.blend`; all 8 FBX under `UnitySim/Assets/Resources/PartModels/`.
- **New (safety backups):** `Blender/<asset>.fbx.bak2` for all eight (the repo is not under git; `.bak` already holds the pre-i19 bodies).
- **Modified (code):** `Vehicles/PartVisualFactory.cs`, `Assets/Editor/PartModelPostprocessor.cs`, `Assets/Editor/PartModelValidator.cs`, `README.md`, memory `blender-fbx-pipeline.md`.

## Steps (each ends with a Blender render check; C#/Unity checkpoints where noted)

1. **Prep + baseline** — connect Blender, open `parts.blend`, back up all 8 FBX to `Blender/*.fbx.bak2`, snapshot current object names/dimensions, and set up a reusable studio render + a scripted **mesh-validation** helper (tri/quad/n-gon/non-manifold/loose/doubles report) and a scripted export helper with the settings above.
2. **Wheels** — `wheel_slick`, then `knobby`, then `rally`. Render iso/front/side per style; validate; export.
3. **`body_shell`** — the flagship. Blocking → forms → panel lines → creases → SubD → validate → render iso/side/top/front → export.
4. **`body_lowracer`** and **`body_buggy`** — same pipeline, reusing the shell's cage strategy.
5. **`battery_stick` + `antenna_stub`** — smaller assets, same standard, at the new real-world battery size.
6. **Code touch-ups + validation** — the four C# items above; headless `PartModelValidator.Report` (editor closed) must show every asset at its contract size with 0 `error CS`; then relaunch the editor.
7. **Docs + memory** — README asset paragraph, update `blender-fbx-pipeline.md` with the new authoring standard, sizes, and `mesh_smooth_type='EDGE'`.

## Verification

- **In Blender, per asset:** the scripted mesh report comes back clean (majority quads, 0 n-gons in the cage, 0 non-manifold edges, 0 loose verts, 0 doubles); renders from iso / front / side / top match the reference proportions; a **rotating-light reflection pass** shows smooth highlights with no lumps, pinching, or waviness, and creases/panel lines still visible after SubD.
- **In Unity (headless, editor closed):** `Unity.exe -batchmode -quit -projectPath <UnitySim> -executeMethod AIHWSim.EditorTools.PartModelValidator.Report -logFile <log>` → every asset PASSes its expected size (bodies 0.200 × ~h × 0.420, wheels 0.066 across, battery 0.047 × 0.025 × 0.138, antenna ~0.10 tall), tri counts within budget, 0 `error CS`.
- **Play-test (user):** Garage → cycle Shell / LowRacer / Buggy and all three wheel styles; body scales to `bodySize` and tints by body colour; wheels sit correctly in the arches and spin/steer on track; the **PAINT tab** still paints the shell (UVs intact); the battery reads as a real pack in the tray; antennas stand correctly. Drive on track, split-screen, and as a LAN ghost to confirm all four viz contexts. `PartMeshLibrary.Enabled = false` still falls back to primitives.

## Risks

- **Body length normalization is a visible change.** Today's shells import 0.461–0.477 m long against a 0.420 m nominal — i.e. ~13% longer than `bodySize.z`, giving unrealistic overhang past the 0.304 m wheelbase. Normalizing to exactly 0.420 makes the slider honest and the arch/wheel alignment correct, but bodies will look slightly shorter than they do now. Intended; called out here so it isn't a surprise.
- **Existing painted liveries will not survive.** `liveryPng` is a 256² texture baked against the *old* UV unwrap; a new unwrap re-lands those pixels. Mitigation: keep island layout as close as practical, and note it in the README. (Only affects vehicles the user has actually painted.)
- **The ×100 FBX scale trap** — burned us in i19; pinned by `apply_unit_scale=False, global_scale=0.01` and caught by the upgraded validator.
- **Bigger battery may intersect small bodies.** At 138 mm in a 420 mm body it fits, but a user who shrank `bodySize` could see it poke through. Check the default `localPos (0, −0.02, −0.05)` in the garage preview and adjust the default if needed.
- **SubD 2 on bodies is the poly ceiling** — validate tri counts per asset against the budget before export; drop to level 1 + a bevel pass if any body overruns ~6k.
- **Blender session state resets between MCP calls** — re-open `parts.blend` and re-resolve objects at the start of every step; save with `bpy.ops.wm.save_mainfile` after each asset.

---

# Iteration 19 — Smooth, aerodynamic body-shell meshes (Blender re-model of all three bodies)

## Context

The vehicle body shells authored in iteration 14 (`body_shell`, `body_lowracer`, `body_buggy` FBX under `UnitySim/Assets/Resources/PartModels/`, source `Blender/parts.blend`) are low-poly compound primitives — they read as *blocky*, not like the smooth curved lexan bodies of a real 1/10 RC car (see `example car models/car_example*.jpg`: a Tamiya touring shell + two F1TENTH/JetRacer builds). The user wants each body **re-modeled in Blender as one smooth, aerodynamic mesh shell** — flowing curves, rounded fenders/arches, a proper roofline/canopy — while staying a drop-in replacement for the existing pipeline. Confirmed scope: **refine all three** bodies (Shell first, then LowRacer, then Buggy); **paint is for the Blender verification renders only** — in-game colour stays driven by the design's `bodyColor`/livery, so the FBX carries shape + UVs only.

**This is an art/asset task — no gameplay code changes.** The pipeline (verified by code read) already loads any mesh under that name; a smoother shell just has to honour the same authoring contract. A smoother shell does **not** change physics: aero drag/lift is keyed on the `BodyShape` enum via `AeroDynamics.BodyCd/BodyClA` (`Assets/Scripts/Vehicles/AeroDynamics.cs:41-64`), independent of mesh geometry — intentionally left untouched.

## Authoring contract every refined shell MUST honour (drop-in replacement)

Confirmed from `CarVehicle.BuildBodyVisual` (`Assets/Scripts/Vehicles/CarVehicle.cs:433-493`), `PartMeshLibrary` (`Assets/Scripts/Vehicles/PartMeshLibrary.cs`), and `PartModelPostprocessor` (`Assets/Editor/PartModelPostprocessor.cs`):

- **Exact bounding box 0.20 (X, width) × 0.10 (Y, height) × 0.42 (Z, length) m**, and the **same local center/origin** as the current mesh — the runtime scales the instance per-axis by `bodySize / BodyMeshAuthorSize` where `BodyMeshAuthorSize = (0.20, 0.10, 0.42)` (`CarVehicle.cs:484`). Wrong bounds ⇒ the body renders the wrong size on every car.
- **Orientation +Y up, −Z forward** (nose toward −Z) — the instance is placed at identity pose (`PartMeshLibrary.cs:62-63`); the mesh itself carries the orientation.
- **Single object, one material slot** — `BuildBodyVisual` assigns one shared `_bodyMat` (carrying `bodyColor`/livery) to every child `MeshRenderer` (`CarVehicle.cs:464-468`). Multiple slots are fine visually but pointless; keep it to one clean mesh.
- **Clean, non-overlapping UV map** — required for the in-game paint mode: `BodyPainter` cooks a runtime MeshCollider from the shell and reads `RaycastHit.textureCoord` (`Assets/Scripts/Garage/BodyPainter.cs:103-109,205-219`), and the postprocessor makes only `body_*` meshes CPU-readable (`PartModelPostprocessor.cs:42-43`). No UVs / overlapping UVs ⇒ paint lands in the wrong place.
- **Same filenames** — overwrite `body_shell.fbx` / `body_lowracer.fbx` / `body_buggy.fbx` in place; Unity auto-reimports and the `body_` prefix keeps them readable. Keep triangle counts modest (~1.5–4k each; up to 4 cars render at once in split-screen/LAN).

## Prerequisite (blocking)

**Blender must be running with the MCP addon connected (localhost:9876) and `Blender/parts.blend` open.** It is currently *not* connected (verified: `get_objects_summary` → connection refused). No modeling can start until it is. Also: there is **no checked-in FBX export script** — iteration 14 exported via the Blender dialog — so this iteration (re-)establishes a deterministic scripted export via `bpy.ops.export_scene.fbx`.

## Approach — per body (done in Blender via `execute_blender_code`)

Work one body at a time (Shell → LowRacer → Buggy), each as its own compile/verify checkpoint. For each:

1. **Inspect the existing object** in `parts.blend` (bounds, center, object name, current UVs) so the replacement matches its footprint/origin exactly.
2. **Model a smooth shell** with bmesh cross-section profiles lofted along Z + a **Subdivision Surface** modifier (1–2 levels) and **Shade Auto Smooth**, then cut/inset **rounded wheel arches**, giving curvature without facets:
   - **body_shell (touring):** sloped hood at the nose, curved windshield rise into a smooth roofline, tapered rear deck with a subtle integrated ducktail lip, bulged rounded fenders over all four arches, side-sill curve. Closest match to `car_example.jpg`.
   - **body_lowracer (F1TENTH):** very low sleek aero wedge — pointed low nose ramp, smooth central canopy bump, faired rear, rounded side pods; occupies the lower part of the 0.10 height envelope.
   - **body_buggy:** rounded cab/cockpit, curved hood, flared rounded wheel arches, higher stance; smoothed off-road silhouette.
3. **Apply modifiers**, then **normalize** the mesh so its bounding box is exactly 0.20×0.10×0.42 with the original center (scale/translate in object mode, apply transforms).
4. **Smart UV Project** (angle limit 66°, island margin 0.02); verify islands don't overlap.
5. **Apply a paint material** in Blender (nice automotive colour + light spec) purely so the verification render reads well — this material is *not* used in-game.
6. **Render** a 3/4 view via `render_viewport_to_path` and confirm it reads as a smooth RC body before export.
7. **Back up then export**: copy the current `body_*.fbx` to `Blender/body_*.fbx.bak` first (safety — repo isn't git; the old blocky mesh is otherwise overwritten), then `bpy.ops.export_scene.fbx(filepath=<PartModels>/body_<x>.fbx, use_selection=True, apply_unit_scale=True, global_scale=1, axis_forward='-Z', axis_up='Y', mesh_smooth_type='EDGE', bake_space_transform=True)`.
8. **Save** `parts.blend` (`bpy.ops.wm.save_mainfile`).

## Files

- **Modified (assets):** `Blender/parts.blend`; `UnitySim/Assets/Resources/PartModels/body_shell.fbx`, `body_lowracer.fbx`, `body_buggy.fbx` (overwritten in place — Unity regenerates the `.meta` import settings via the postprocessor).
- **New (safety backups):** `Blender/body_shell.fbx.bak`, `body_lowracer.fbx.bak`, `body_buggy.fbx.bak`.
- **No C# changes.** `AeroDynamics.cs` (enum-keyed drag/lift), `CarVehicle.cs`, `PartMeshLibrary.cs`, `VehicleDesign.cs`, presets — all untouched.

## Verification

- **In Blender:** `render_viewport_to_path` per body → each reads as a smooth, curved shell (no facets), correct proportions, nose at −Z.
- **In Unity (headless):** with the editor closed, run `Unity.exe -batchmode -quit -projectPath <UnitySim> -executeMethod AIHWSim.EditorTools.PartModelValidator.Report -logFile <log>` and confirm each `body_*` logs `size≈(0.200,0.100,0.420)` with the expected center (proves scale + orientation survived re-export) and 0 `error CS`. Then relaunch the editor.
- **Play-test (user):** Garage → set body to Shell / LowRacer / Buggy → the car shows the new smooth shell scaled to `bodySize`, tinted by body colour; wheels poke through the arches; the PAINT tab still lets you paint the shell (UVs intact); drive on track and spawn as a split-screen/LAN ghost → the mesh appears in all viz contexts; toggling `PartMeshLibrary.Enabled = false` still falls back to the primitive body. Physics/lap/telemetry unchanged (geometry-only edit).

## Risks

- **Bounds/origin drift on re-export** → mitigated by explicit normalize-to-0.20×0.10×0.42 + original center before export, and the `PartModelValidator` bounds check catches any drift.
- **UV loss breaks in-game paint** → Smart UV Project + overlap check every body; the paint tab is the acceptance test.
- **Orientation flip** (nose ends up +Z, or lying on its side) → author −Z-forward/+Y-up and pin it with the export axes; the validator's center + a garage glance confirm it.
- **Triangle bloat from subsurf** → apply at 1–2 levels only, keep ~1.5–4k tris/body.
- **Overwriting the only copy of the old meshes** → `.fbx.bak` backups + `parts.blend1` autosave before export.

---

# Iteration 18 — Animated main-menu attract loop + shareable Windows installer

## Context

Two user requests: (1) the **main menu should show cars driving around a map** behind the UI instead of the single static rotating showcar, and (2) **package the game as a Windows installer** the user can hand to friends to play **LAN** matches.

Everything needed for (1) already exists — bot AI (`BotDriver`/`BotPath`), the track factory (`TrackFactory.Build`), and code-built map/car presets (`TrackPresets.All`, `VehiclePresets.All`) — so the menu just needs to compose them into a lightweight attract scene reusing the exact rig wiring the game already uses for bot opponents (`SimulationRunner` with the bot flags). For (2), the build tooling is a bare Development build that boots into the wrong scene; the work is a release build that boots into the menu, per-user-writable save paths, an Inno Setup script, and LAN/firewall docs.

**Confirmed decisions:** package = **Windows installer (Inno Setup)**; the shared build ships **no controller DLL** (Manual / Bot AI / split-screen / LAN all work; only "Autonomous (C firmware)" falls back to open-loop — documented).

---

## Part 1 — Menu attract loop (cars driving on a map)

Today `Menu/MenuBootstrap.cs` builds a dark plane + one kinematic showcar (spun 12°/s) with a tight close-up camera; `Menu/MenuUI.cs` draws a **centered, non-dimming** IMGUI panel over whatever the camera renders — so any live 3D scene behind it shows through around the panel. We replace the showcar with a small self-driving track scene.

**New `Menu/MenuAttract.cs`** (MonoBehaviour) — `public bool Build()` returns false on any failure so the caller can fall back:
- **Pick a map**: random from a curated list of **closed-loop spline** presets (`Whoop Canyon`, `Monza Mini`, `Boost Speedway`, `Neon Vortex`) via `TrackPresets` — these give clean, closed bot paths. (Skip `Boulder Basin` = no spline, `Slide Yard` = open horseshoe.)
- **Build the track**: `TrackFactory.Build(design, interactive: false)`. Non-interactive is deliberate — it still creates the **floor slab + ribbon MeshColliders** (needed so wheels rest on the ribbon) but skips the `LapTimer`/checkpoint triggers, so **no lap HUD `OnGUI` bleeds onto the menu**. Gives `BuiltTrack { root, spawnPos, spawnRot }`.
- **Bot path**: `BotPath.Build(design, lapTimer:null, ovalPath:null, built.spawnPos, built.spawnRot*Vector3.forward, out bool closed)` — samples the preset's longest spline centerline directly from `design.splines`.
- **Spawn 3–4 cars**: bodies cycled from `VehiclePresets.All` (like `MenuUI.MakeBotSlot`), recolored `Color.HSVToRGB((k*0.137f)%1f, 0.65f, 0.95f)` for paint variety, positioned at staggered points along `_botPath` (e.g. every ~`count/N` of the path) facing the path tangent, dropped just above the surface. Each car:
  - `VehicleFactory.Build(design, pos, rot, previewKinematic:false)` (non-kinematic so physics runs),
  - `AddComponent<CarInput>()` with `car`, `source = new BotDriver(car, _botPath, closed, BotDifficulty.Medium)`,
  - a `SimulationRunner` GameObject wired **exactly like the bot rig** in `TrackBootstrap.BuildPlayerRig` (`TrackBootstrap.cs:417-443`): `physicsRateHz=400, controlRateHz=100, vehicleBehaviour=car, inputBehaviour=carInput, sensorRig=built.rig, loadControllerDll=false, allowModeToggle=false, showModeBox=false, logCsv=false, loggable=false, startInManual=true`. (`SimulationRunner.Awake` sets `Time.fixedDeltaTime=1/400` for RC-scale stability — no separate physics-rate wiring needed.) No per-car camera.
- **Camera**: one elevated 3/4 camera (`Camera.main`, keeps the sole `AudioListener`) that **slowly orbits the track centre**. Frame distance/height from the runtime path bounds (centroid + max radius of `_botPath`) so it fits any preset; orbit a few °/s in `Update`. `clearFlags = SolidColor`, dark background.

**Modify `Menu/MenuBootstrap.cs`**: keep `BuildLighting()`; replace the `BuildShowcar()`+`BuildCamera()` calls with `var attract = gameObject.AddComponent<MenuAttract>(); if (!attract.Build()) { BuildBackdrop(); BuildShowcar(); BuildCamera(); }` — so a broken preset can never leave a black menu; the existing showcar path stays as the fallback.

Performance note: 3–4 RC cars at 400 Hz + one ribbon mesh is the same load as a 4-car split-screen/LAN race, which already runs fine.

## Part 2 — Shareable Windows installer + LAN readiness

Findings that must be fixed: boot scene is `GarageScene` (build index 0), **MenuScene is index 3** → a build launches into the garage, not the menu; all save/vehicle/track/telemetry I/O writes **next to the exe** (`Directory.GetParent(Application.dataPath)`), which fails under `Program Files`; the app has `runInBackground=0`, so a host that alt-tabs **pauses the authoritative sim and stalls the LAN match**.

1. **Release build tooling** — `Assets/Editor/BuildMenu.cs`: add `[MenuItem("Tools/AIHWSim/Build Standalone (Release)")] BuildRelease()`:
   - Scene list built explicitly with **MenuScene moved to index 0** (find it in `EditorBuildSettings.scenes`, prepend; keep Garage/TrackBuilder/Track after). Boots into the menu.
   - `BuildPipeline.BuildPlayer(scenes, "Builds/Release/AI Hardware Control Sim.exe", StandaloneWindows64, BuildOptions.None)` — no Development flag/watermark.
   - Reveal on success. Runnable headless during implementation via `Unity.exe -batchmode -quit -executeMethod AIHWSim.EditorTools.BuildMenu.BuildRelease`.
2. **Per-user-writable save paths** — new `Persistence/AppPaths.cs` static: `BaseDir => Application.isEditor ? Directory.GetParent(Application.dataPath).FullName : Application.persistentDataPath`. Point the four existing base-dir sites at it: `Persistence/SaveSystem.cs`, `Garage/VehicleLibrary.cs`, `TrackEd/TrackLibrary.cs`, and `Telemetry/CsvLogger.cs` (the `Save()` path only; its scratch file already uses `temporaryCachePath`). Editor keeps today's next-to-project paths (dev workflow + existing content unaffected); a **player build** writes to `%USERPROFILE%/AppData/LocalLow/<company>/<product>` — writable from any install location. Built-in maps/cars are code presets (`TrackPresets`/`VehiclePresets`), so friends always have starter content regardless of the save dir.
3. **Host-when-unfocused** — set `Application.runInBackground = true` once at startup (e.g. in `MenuBootstrap.Awake` / `NetSession` init, or `PlayerSettings.runInBackgroundMode` in ProjectSettings) so hosting doesn't freeze on alt-tab.
4. **Product identity (optional polish)** — set `productName = "AI Hardware Control Sim"` and a `companyName` in `ProjectSettings` (window title + the LocalLow folder name). Unity splash stays (Personal edition).
5. **Inno Setup installer** — new `Installer/AIHWSim.iss`:
   - `[Files]` recurse-packs `Builds/Release/*` (the exe + `AI Hardware Control Sim_Data/` + `MonoBleedingEdge/` + `UnityPlayer.dll`).
   - Install to `{autopf}\AI Hardware Control Sim` (Program Files; saves go to LocalLow now, so this is safe), Start-Menu + optional Desktop shortcut, uninstaller, `AppId` GUID, version from `bundleVersion`.
   - Compiled with the free **Inno Setup** compiler (ISCC) → a single `Setup.exe` to share. Document the one-time Inno Setup install + `iscc Installer\AIHWSim.iss` command.
6. **LAN / firewall docs** — `README.md` (+ a short `Installer/LAN-Setup.md`): friends run the installer, all machines on the **same LAN/subnet**; **allow the app on Private networks** when Windows Firewall prompts (or pre-add rules) for **UDP 7777** (game transport) and **UDP 47777** (discovery beacon); host clicks **Host LAN Game**, others **Join** (auto-discovery, or manual host IP; port-forward 7777 for internet play). All clients must run the **same version** (`NetSession.ProtocolVersion` gates mismatches at connect). Autonomous-C-firmware caveat: DLL not shipped → that one mode is open-loop; drop a 64-bit `car_controller.dll` into `AI Hardware Control Sim_Data/Plugins/x86_64/` to enable it later.

## Files

**New:** `Menu/MenuAttract.cs`, `Persistence/AppPaths.cs`, `Installer/AIHWSim.iss`, `Installer/LAN-Setup.md`.
**Modified:** `Menu/MenuBootstrap.cs` (attract + fallback), `Assets/Editor/BuildMenu.cs` (release build, menu-first scenes), `Persistence/SaveSystem.cs` + `Garage/VehicleLibrary.cs` + `TrackEd/TrackLibrary.cs` + `Telemetry/CsvLogger.cs` (AppPaths base dir), `ProjectSettings` (runInBackground, product/company name), `README.md`.
**Reuse:** `BotDriver`/`BotPath`, `TrackFactory.Build`, `TrackPresets.All`/`VehiclePresets.All`, `VehicleFactory.Build`, `CarInput`, `SimulationRunner` (bot flags), the `Directory.GetParent(Application.dataPath)` idiom being centralized.

## Steps (headless compile checkpoint after each — editor closed, grep `error CS`, wait for Unity exit)

1. **Menu attract** — `MenuAttract.cs` (track + bots + orbit camera), `MenuBootstrap` wiring with showcar fallback. → Play-test the Menu scene: cars drive laps behind the panel; a bad preset falls back to the showcar.
2. **Save paths + release build + runInBackground** — `AppPaths.cs` + 4 call sites, `BuildRelease`, runInBackground/product name.
3. **Installer + docs** — `AIHWSim.iss`, `LAN-Setup.md`, README; produce the Release build headless, then (user) compile the installer with ISCC.

## Risks

- **Menu cars falling through / spawning off-ribbon** — mitigated by spawning on `_botPath` points (on the spline) just above the surface; ribbon MeshColliders exist even in `interactive:false` builds. `Build()` is wrapped so any exception falls back to the static showcar.
- **Lap HUD on the menu** — avoided by `interactive:false` (no `LapTimer` created).
- **Save-path move** only affects **player builds** (editor unchanged); persisted user content in a shared build lives in LocalLow — intended and Program-Files-safe. Built-ins are code presets, always present.
- **Version skew on LAN** — already gated by `ProtocolVersion`; docs stress "same installer version."
- **Inno Setup not installed** — it's a separate free tool; the plan documents the install + `iscc` step. The Release build (a portable folder) is independently runnable/zippable even before the installer is compiled.

## Verification

- Headless batch compile (0 `error CS`) after each step; relaunch editor at end.
- **Menu:** open MenuScene ▸ Play — 3–4 recolored cars drive laps on a random circuit behind the menu panel; camera slowly orbits; navigating pages (Single Player/Options) is unaffected; force a bad preset once to confirm the showcar fallback.
- **Build:** run `BuildRelease` (or headless `-executeMethod`) → `Builds/Release/…exe` launches straight into the **menu**; create a vehicle/track and confirm files appear under `…/AppData/LocalLow/<company>/AI Hardware Control Sim/{Vehicles,Tracks,Saves}` (not next to the exe).
- **Installer:** `iscc Installer\AIHWSim.iss` → run `Setup.exe`, install, launch from the Start-Menu shortcut, uninstall cleanly.
- **LAN:** two machines (or editor + installed build) on one subnet — host, allow the firewall prompt, the other auto-discovers and joins; drive together; host alt-tabs and the match keeps running (runInBackground); a mismatched version is rejected at connect.

---

# Iteration 17 — Single-player races vs AI bots + telemetry-logging toggle

## Context

Two user requests: (1) **single-player races against bot opponents** on preset maps — bots drive a variety of cars/paint themes around the track through checkpoints (F1/Forza style); the race is configured from the main menu (opponents, map, player car, manual/autonomous driving, laps) and shows results. (2) Telemetry/sensor CSV logging must become an **explicit opt-in setting, default OFF**, toggleable from Options (main menu → Options) and from a new pause-menu Settings panel; when enabled mid-session it starts after the menu closes.

Confirmed decisions: **3 bot difficulty levels (Easy/Medium/Hard) + a toggleable rubber-banding** catch-up assist; **up to 7 opponents (8-car grid)**; **several brand-new race circuits** (not just enhancing the existing 4 presets); the player's **Autonomous** option offers **both** the existing C-firmware controller *and* the bot AI driving their car.

Everything needed already exists as N-player-ready infrastructure — the work is a new bot `IDriverInputSource`, a race-setup UI that populates `SessionConfig.Players` with human + bot slots, disentangling "multiple cars" from "split-screen humans" in `TrackBootstrap`, new circuit presets, and the logging-toggle plumbing. `RaceDirector`, `LapTimer` (per-car), and `SpawnPose` (N-car grid) are already generic.

## The one structural risk — the `split` conflation

`TrackBootstrap` uses `bool split = slots.Count > 1` to mean "split-screen humans," which drives per-viewport cameras, `SplitScreenHud`, and disabling the DLL/Tune/mode box. A bot race is `slots.Count == 8` but has exactly **one local human**. Fix: compute session facts from slot roles, not raw count.
- New `PlayerSlot` fields (all default to today's behavior): `bool isBot = false`; `enum DriveControl { Human, BotAI, Firmware } control = Human`; `int botDifficulty = 1` (0/1/2). New `SessionConfig` field `bool RubberBand`.
- In `Awake` (TrackBootstrap.cs:57-58): `int localHumans = count of slots where !isBot && isLocal`; `bool splitScreen = localHumans > 1`. Use **`splitScreen`** (not `slots.Count>1`) for: the `SplitScreenHud` block (:67), `pause.tunableBehaviour = splitScreen ? null : humanRig.car` (:79), and the `OnGUI` DLL-box gate (:718, change `_rigs.Count>1` → `splitScreen`). Ensure `_rigs[0]` / `_runner` / pause target the **human** rig (human is slot 0 by construction; keep a `_humanRig` reference).

## Bot AI — new `Core/BotDriver.cs` (`BotDriver : IDriverInputSource`)

Mirrors `Net/NetworkInputSource.cs` (a synthesized input source). Constructor: `(CarVehicle car, IReadOnlyList<Vector3> path, bool closed, BotDifficulty diff)`. Pure-pursuit:
- **Steering**: find nearest path index to `car.transform.position`; advance a difficulty-scaled look-ahead arc-distance to an aim point; `Steer()` = clamped signed heading error between `car.transform.forward` and `(aim − pos)` projected on the ground (normalized by the car's steer authority). Reuses `car.transform` + `car.ForwardSpeed` (both already public); no new `CarVehicle` getters needed for pure-pursuit.
- **Throttle/Brake**: target speed = `diff.baseSpeed` reduced by upcoming path curvature (angle over the next few samples) so bots lift/brake for corners; `Throttle()`/`Brake()` compare target vs `car.ForwardSpeed`.
- **Stuck recovery**: if speed ≈ 0 or path-distance too large for ~2 s, `RespawnPressed()` returns true once (CarInput already calls `car.ResetVehicle()` + `lapTimer.ResetTimer(car)`).
- `Handbrake()` false, `MouseSteerDelta()` 0.
- **Difficulty table** (`enum BotDifficulty` + params): Easy/Medium/Hard scale `baseSpeed` (≈0.7/0.85/1.0 of a nominal RC top speed), look-ahead distance, corner caution, and steer gain.
- **Rubber-banding** (optional): `public float SpeedScale = 1f` multiplies target speed; `RaceDirector` updates each bot's `SpeedScale` per frame from its gap to the human (trailing → >1, leading → <1, clamped) **only when `SessionConfig.RubberBand`**. Keeps the bot decoupled from race state.

**Path source** — new static helper `Core/BotPath.cs` `Build()` returns the ordered centerline + `closed` for the current environment:
- Spline map: `SplineMath.SampleAll(GameFlow.ActiveTrack.splines[0])` (longest spline) → `sample.pos` list, `closed = spline.closed`. (`SplineMath.SampleAll` at TrackEd/SplineMath.cs.)
- Oval: expose `TrackBootstrap.SamplePath()` points (already a closed loop) to the bot.
- Tile map w/o spline: ordered `Checkpoint` gate world-positions (by `.index`) + finish, treated as a closed coarse-waypoint loop.
`TrackBootstrap` builds the path once after the environment exists and passes it into each bot rig.

## Menu race-setup UI — `Menu/MenuUI.cs`

Extend `DrawSinglePlayer()` (:134), which already has vehicle/track/laps pickers, into a full race setup by adding (mirroring the existing `_spLaps` stepper and `CyclePicker`):
- **Opponents** stepper 0–7 (`_spBots`).
- When opponents > 0: **Difficulty** cycle (Easy/Medium/Hard, `_spDiff`) and **Rubber-band** toggle (`_spRubber`).
- **Driving** cycle: Manual / Autonomous (C firmware) / Autonomous (Bot AI) (`_spControl`).
- Existing **Laps** stepper + Drive/Race button.
Persist the new choices in `GameSettings` (`spBots`, `spDiff`, `spControl`, `spRubber` — old JSON defaults fine) so they're remembered.

Rewrite **`StartSinglePlayer()`** (:157) to populate `SessionConfig.Players` explicitly (like `StartSplitScreen`/`MakeSlot` at :256/:278), replacing the empty-roster reliance:
- Clear `Players`; add **human slot 0**: chosen design, `isBot=false`, `control` = Manual→Human / firmware→Firmware / botAI→BotAI, `assists = P1Assists`, `profileId = player1Name`.
- Add **k = 1..opponents bot slots**: design cycled through `VehiclePresets.All` builders with a **randomized `bodyColor`** (paint-theme variety), `name = "Bot k · <preset>"`, `isBot=true`, `control=BotAI`, `botDifficulty` = pick.
- `SessionConfig.Mode = SinglePlayer`, `TargetLaps = laps`, `RubberBand = _spRubber`. `ResolvePlayers()` returns the populated list unchanged (Mode≠SplitScreen).
- Also make **Garage/Builder Drive** clear `SessionConfig.Players` (call `SetSinglePlayer()`) so a stale bot roster never leaks into a free-drive.

## `TrackBootstrap.BuildPlayerRig` — role-aware rig

Add a `bool splitScreen` param (and use `slot.isBot`/`slot.control`):
- **Input source** (:367): `control==BotAI` → `new BotDriver(built.car, path, closed, diff)` (record it for rubber-banding); else `new PlayerInputSource(...)`. For `Firmware`, source is unused (runner runs Autonomous via DLL).
- **Camera/HUD**: `slot.isBot` → **no camera, no graph, no SensorHud/MetricsOverlay** (opponents are rendered by the human's camera, they don't need their own view). Human + `!splitScreen` → full `BuildCameraAndGraph` (today's SP path). Human + `splitScreen` → `BuildPlayerCamera` viewport.
- **Runner flags**: bot or split → `loadControllerDll=false, allowModeToggle=false, showModeBox=false, logCsv=false`. Human-`Firmware` → `startInManual=false, loadControllerDll=true, showModeBox=true` (existing autonomous path). Human-`Manual`/`BotAI` → `startInManual=true`. Human non-split → `logCsv` gated by the logging setting (below).
- **Profile records**: guard `HookLapRecords` (:502) with `if (rig.slot.isBot) continue;` so bots don't write to `profiles.json`.
- `RaceDirector` wiring (:86) is unchanged — it already ranks all rigs (bots included) by lap count and shows the results overlay; give it the `BotDriver` list so it can drive rubber-banding when enabled.

## Feature 2 — telemetry-logging toggle (default OFF)

1. **`Persistence/GameSettings.cs`**: add `public bool logTelemetry = false;` (old settings.json keeps the default). Not an engine setting → no `Apply()` change.
2. **`Menu/MenuUI.cs` `DrawOptions()`** (:520): add a "Log sensor/telemetry data" `GUILayout.Toggle` in the SIM-REALISM group, same `changed` → `SettingsStore.Apply()/Save()` pattern.
3. **Gate `logCsv`** at `TrackBootstrap` human-rig branch (:406) and `SimBootstrap.cs` (:51): `runner.logCsv = logCsv && SettingsStore.Current.logTelemetry;`. (Bot/split already false.)
4. **`SimulationRunner.EnableLogging()`** — new public method: if `_csv == null`, `_csv = new CsvLogger(Hub); _csv.Begin(ControllerName(), BuildMetadata(), logLabel);` (Hub + channels already exist post-`Start`). Idempotent.
5. **`Core/PauseMenu.cs`**: add a Settings sub-panel (mirror the `_showTune` pattern — `bool _showSettings`, a "Settings…" button, a `DrawSettings()` block) exposing the same `logTelemetry` toggle (+ persist). In `SetPaused(false)` (the single choke point where the menu closes and `timeScale` returns to 1), if the setting is now ON, call `EnableLogging()` on each runner that has no `_csv` yet — so logging **starts after the menu closes**. (Disabling mid-session applies next session; documented.)

## New race circuits — `TrackEd/TrackPresets.cs`

Add several brand-new closed-**spline** circuits to `All[]` (closed loops give bots a clean racing line) built from the existing catalog, each with **boost pads** (`Boost = 7` floor, already defined at TrackPresets.cs:20 but unused by any preset), **jumps** (`ramp`), **obstacles** (cones/barriers/tire stacks), ordered **checkpoints** + **finish** + **spawn**. Examples: a fast asphalt speedway with boost straights + a jump chicane; a mixed-surface rally loop (dirt/grass/boost) with ramps; a technical circuit with tight checkpoints. They appear automatically in the menu track picker via `TrackPresets.DisplayNames()/Resolve()` — no menu wiring needed. Reuse the paint/spline/item helpers already used by Whoop Canyon / Monza Mini (TrackPresets.cs:98/129).

## Files

**New:** `Core/BotDriver.cs`, `Core/BotPath.cs`.
**Modified:** `Core/SessionConfig.cs` (PlayerSlot roles + `RubberBand`), `Menu/MenuUI.cs` (race-setup UI + `StartSinglePlayer` roster + Options toggle), `Core/TrackBootstrap.cs` (splitScreen vs bot roles, BuildPlayerRig branches, path build, HookLapRecords guard, expose SamplePath, rubber-band feed), `Track/RaceDirector.cs` (rubber-band update; standings unchanged), `Core/SimulationRunner.cs` (`EnableLogging`), `Core/PauseMenu.cs` (Settings panel + enable-after-close), `Persistence/GameSettings.cs` (`logTelemetry` + sp race prefs), `Core/SimBootstrap.cs` (`logCsv` gate), `TrackEd/TrackPresets.cs` (new circuits), `Garage/GarageUI.cs` + `TrackEd/TrackBuilderUI.cs` (Drive clears roster), `README.md`.
**Reuse:** `IDriverInputSource` seam, `PlayerInputSource`/`NetworkInputSource` templates, `SplineMath.SampleAll`, `LapTimer` per-car trackers + `LapCompleted`, `RaceDirector` standings/results, `SpawnPose` N-grid, `VehiclePresets.All`, `CsvLogger.Begin`, `SettingsStore`, `GarageSkin` UI.

## Steps (headless compile checkpoint after each — editor closed, grep `error CS`, wait for Unity exit)

1. **Session roles + logging setting foundation** — `PlayerSlot.isBot/control/botDifficulty`, `SessionConfig.RubberBand`; `GameSettings.logTelemetry` + sp race prefs; `SimulationRunner.EnableLogging`; `SimBootstrap` + `TrackBootstrap` `logCsv` gate. (No behavior change yet: rosters still single-slot, logging default OFF.)
2. **Bot AI** — `BotPath.Build`, `BotDriver` (pure-pursuit + difficulty + SpeedScale), expose `TrackBootstrap.SamplePath`.
3. **TrackBootstrap role-aware composition** — `localHumans`/`splitScreen`, `BuildPlayerRig` bot/human/firmware branches, path build + BotDriver wiring, HookLapRecords bot guard, DLL-box/Tune gates.
4. **Menu race setup** — `DrawSinglePlayer` opponents/difficulty/rubber-band/driving controls, `StartSinglePlayer` human+bot roster, Options logging toggle, Garage/Builder Drive roster clear.
5. **Rubber-banding + PauseMenu Settings** — `RaceDirector` per-frame `SpeedScale` feed; `PauseMenu` Settings panel + `EnableLogging` on close.
6. **New race circuits** — several closed-spline circuits with boost/jumps/obstacles/checkpoints in `TrackPresets`.
7. **Docs + validate** — README, final headless compile, editor relaunch.

## Risks

- **`split` conflation** — the core change; audit every `split`/`_rigs.Count>1` site (camera, HUD, Tune, DLL box, mouse-steer) to key off `splitScreen`/`isBot`, not raw count. Play-test SP free-drive and 2-human split after step 3 to confirm no regression.
- **Bot pathing on tile maps w/o spline** — checkpoint-only waypoints are coarse; bots may cut corners. Mitigate with look-ahead smoothing; prefer the new spline circuits for real racing (as chosen).
- **8-car WheelCollider load** — RC-scale solver is light; profile after step 3. Bots skip camera/HUD/CSV/graph so per-bot overhead is just physics + one input tick.
- **Back-compat** — all new `PlayerSlot`/`GameSettings` fields default to today's behavior; legacy entry paths (garage/builder/press-Play) still synthesize a single human slot; logging default OFF means existing runs simply stop auto-writing CSVs (intended).
- **Stale roster leak** — Garage/Builder Drive must clear `Players`; verify no path leaves a bot roster active for a free-drive.

## Verification (user play-test)

1. Main menu → Single Player: set 5 opponents, Hard, rubber-band on, a new race circuit, your car, Manual, 3 laps → Race. Grid of 6 varied/recolored cars lines up; countdown/standings banner shows; bots follow the racing line, take jumps, hit boost pads, and complete laps through checkpoints; you race them; results overlay ranks all 6 with Keep driving / Rematch / Main Menu.
2. Driving = Autonomous (C firmware) → your car runs the DLL path (open-loop fallback if unbuilt); = Autonomous (Bot AI) → your car self-drives via the bot AI (works with no DLL); Manual → you drive.
3. Rubber-band off → a fast Hard bot pulls away; on → the pack stays closer.
4. Logging: default OFF → no CSV written on a normal drive. Options → enable "Log sensor/telemetry data" → next drive logs; pause → Settings → enable mid-session → logging begins after closing the menu; `TelemetryLogs/` gets a file on Save telemetry.
5. Regression: SP free-drive (0 opponents, 0 laps) unchanged; 2-human split-screen still splits correctly; garage/builder Drive spawns just your car (no leftover bots); classic oval + diff-drive scenes unaffected; M-toggle/Tune/graphs intact for the solo human.

---

# Fix — vehicle gets stuck when fully stopped on grass/ice/mud

## Context

Play-test report: the car sometimes gets stuck on grass/ice/mud tiles when fully stopped and won't start moving. Root cause is confirmed in the surface physics: in `CarVehicle.StepPhysics`, per-tile **rolling resistance** is added to each grounded wheel's `brakeTorque` **unconditionally, with no speed gating** (`CarVehicle.cs:869`, `b += surf.rollingResist;` → `w.col.brakeTorque = b;` at `:950`). Modeling rolling resistance as a constant brake makes it a **parking brake** at zero speed. Worse, unpowered wheels carry zero motor torque, so *any* `brakeTorque > 0` locks them into a skid — even grass's tiny `0.005 N·m` flips the front wheels from rolling to skidding, and the low-power RC drivetrain (traction-limited to ~0.8 N·m/wheel) can't overcome the combined drag to launch. Catalog values (`TrackCatalog.cs`): grass `rollingResist = 0.005`, sand `0.018`, mud `0.045`; ice has **no** rolling resistance (`frictionMult = 0.30`, `rollingResist = 0`).

Intended outcome: the car launches freely from a standstill on any surface, while rolling resistance still drags a *moving* car as designed. Ice stays intentionally very slippery (user-confirmed) — no catalog change; the same fix guarantees ice is never truly locked either.

## Fix — velocity-gate rolling resistance (one file)

**`Vehicles/CarVehicle.cs`** — in `StepPhysics`, before the `foreach (var w in _wheels)` loop (after line ~805 where `boost`/`brake` are set), compute a single per-step ramp from the body's horizontal speed:

```csharp
// Rolling resistance must oppose MOTION, not act as a parking brake: ramp it
// in from 0 at a standstill to full above ~0.3 m/s. At zero speed this keeps
// idle/unpowered wheels from locking into a skid (they carry no motor torque),
// so the car launches freely on grass/sand/mud.
Vector3 hv = _body.linearVelocity; hv.y = 0f;
float rollScale = Mathf.Clamp01(hv.magnitude / RollResistRampSpeed); // RollResistRampSpeed = 0.3f const
```

Then change the application at `CarVehicle.cs:869`:

```csharp
b += surf.rollingResist * rollScale;   // was: b += surf.rollingResist;
```

Add a private const `RollResistRampSpeed = 0.3f` near the other tuning fields. Nothing else changes — magnitudes stay as deliberately sized in `TrackCatalog`; boost/rumble/roughness and the friction-multiplier path are untouched; the baseline (oval / diff-drive, where `SurfaceMap.At` returns `Baseline` with `rollingResist = 0`) is unaffected.

## Why this is sufficient and low-risk

- At standstill `rollScale = 0` → unpowered wheels get `brakeTorque = 0` → roll free → the rear motor launches the car. Above 0.3 m/s the full rolling drag returns, so grass/sand/mud still feel draggy and slow the car exactly as before.
- Ice is unchanged (it has no rolling resistance and, per the decision, stays at `frictionMult = 0.30`); the gate additionally guarantees ice can never be locked by a residual brake.
- Single-surface-block edit, no data/ABI/serialization change, no effect on any other scene or the autonomous control path.

## Verification

- Headless batch compile (0 `error CS`): `& "E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -quit -nographics -projectPath "E:\EE Projects\AI Hardware Control Sim (Unity)\UnitySim" -logFile <log>`; then relaunch the editor.
- Play-test: build a track with grass, mud, sand, and ice patches. Stop fully on each, then apply throttle — the car pulls away cleanly on grass/sand/mud (previously stuck); ice spins up and slides away (still slippery, not frozen). Confirm a moving car still visibly slows on mud/sand/grass (rolling drag intact) and that asphalt/dirt and the classic oval are unchanged.

---

# AI Hardware Control Sim — Project Bootstrap Plan

## Context

Goal: a Unity-based physics simulation where **control code written in portable C** (structured like real embedded firmware) drives simulated vehicles — starting with a differential-drive RC car, then a quadcopter — with **live in-game graphs** and **CSV telemetry logging** for later analysis. The same controller source should eventually compile unmodified for real hardware (Arduino/ESP32, STM32, RPi) via a hardware abstraction layer (HAL).

User is an experienced Unity developer. No project or assets exist yet — everything is built from primitives.

## Architecture Overview

```
┌─────────────────────────── Unity (C#) ───────────────────────────┐
│  Fixed-step sim loop → Sensor sim → [Controller Bridge] →        │
│  Actuator sim → Vehicle physics → Telemetry (graphs + CSV)       │
└──────────────────────────────┬───────────────────────────────────┘
                        P/Invoke (manual LoadLibrary for hot-reload)
┌──────────────────────────────┴───────────────────────────────────┐
│  controller.dll — portable C, written against a HAL API          │
│  Same source later builds via PlatformIO/CMake for real MCUs     │
└───────────────────────────────────────────────────────────────────┘
```

### Key decisions (confirmed with user)
- **Control link**: C compiled to a native DLL, loaded by Unity. C ABI boundary.
- **Timing**: fixed-timestep physics (start at 500 Hz, `Time.fixedDeltaTime = 0.002`), controller stepped at a configurable rate (e.g. 100 Hz–1 kHz) via an accumulator inside `FixedUpdate`. Deterministic, repeatable runs.
- **Telemetry**: live scrolling graph overlay in Unity **and** CSV logging per run.
- **First vehicle**: differential-drive ground robot from primitives; quadcopter second.

## Directory Layout

```
E:\EE Projects\AI Hardware Control Sim (Unity)\
├── UnitySim\                  # Unity project (Unity 6 LTS, 3D core template)
│   └── Assets\
│       ├── Scripts\
│       │   ├── Core\          # SimClock, SimulationRunner, run config
│       │   ├── Bridge\        # NativeControllerLoader, ControllerBridge
│       │   ├── Vehicles\      # DifferentialDriveVehicle (later QuadcopterVehicle)
│       │   ├── Sensors\       # EncoderSensor, ImuSensor (noise + bias models)
│       │   ├── Actuators\     # DcMotorModel (first-order lag, torque curve, saturation)
│       │   └── Telemetry\     # TelemetryHub, GraphOverlay, CsvLogger
│       └── Plugins\x86_64\    # controller.dll copied here by build script
├── Controllers\               # C firmware workspace (its own git-friendly tree)
│   ├── hal\hal.h              # HAL API the firmware is written against
│   ├── common\                # shared control lib: pid.c/h, filters, mixers
│   ├── diffdrive_pid\         # first controller: wheel-speed / heading PID
│   ├── targets\
│   │   ├── sim\               # sim entry: implements the DLL export layer
│   │   └── arduino\           # (later) PlatformIO project wrapping same sources
│   ├── CMakeLists.txt
│   └── build.ps1              # configure + build + copy DLL into UnitySim
└── Docs\                      # interface spec, tuning notes
```

## The C ABI (contract between Unity and firmware)

`Controllers/hal/controller_api.h` — plain-C structs, fixed layout, no allocation across the boundary:

```c
typedef struct {            // Unity → controller (per tick)
    float time_s, dt_s;
    float gyro[3], accel[3];        // IMU
    float wheel_vel[4];             // encoder-derived rad/s
    float setpoint[4];              // user commands (e.g. target v, ω)
} CtrlInputs;

typedef struct {            // controller → Unity (per tick)
    float actuator[8];              // normalized -1..1 motor commands
    float debug[16];                // free channels, auto-graphed by name
} CtrlOutputs;

EXPORT int  ctrl_init(float control_rate_hz);
EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out);
EXPORT void ctrl_shutdown(void);
EXPORT const char* ctrl_get_debug_names(void); // comma-separated labels for graphs
```

Firmware logic (PID, filters) lives in `common/` and includes only `hal.h` — never Unity- or sim-specific headers. The `targets/sim/` layer adapts HAL calls to the CtrlInputs/CtrlOutputs structs; a future `targets/arduino/` layer adapts them to real peripherals. This is what makes "write once, run in sim or on hardware" real.

## Unity-side design

- **NativeControllerLoader** (`Bridge/`): loads the DLL with `kernel32 LoadLibrary`/`GetProcAddress` instead of `[DllImport]`, because DllImport pins the DLL for the editor's lifetime. Manual loading enables **rebuild-and-hot-reload without restarting Unity** — essential for the tune/iterate loop. Unload on play-mode exit; watch the DLL file timestamp and offer reload.
- **SimulationRunner** (`Core/`): owns the accumulator that steps the controller at its configured rate inside `FixedUpdate`; marshals sensor readings into `CtrlInputs`, applies `CtrlOutputs` to actuator models. Handles controller crash isolation (try/catch around the native call, stop sim on fault).
- **DifferentialDriveVehicle** (`Vehicles/`): Rigidbody + primitive box/cylinders; wheels as torque applied through a simple DC-motor model (no WheelCollider — custom friction is more predictable and portable to the quad later). Keyboard/gamepad sets the setpoint (target velocity + turn rate); the C controller closes the loop.
- **Sensors** (`Sensors/`): ground truth from physics + configurable Gaussian noise, bias, and quantization so sim-tuned filters survive contact with real hardware.
- **TelemetryHub** (`Telemetry/`): central ring buffer keyed by channel name. Sources: all CtrlInputs/CtrlOutputs fields + named debug channels from `ctrl_get_debug_names()`. Consumers:
  - **GraphOverlay** — UI Toolkit panel with scrolling line plots (mesh-based line drawing, ~4 configurable plot panes, channel picker, pause/zoom).
  - **CsvLogger** — one CSV per run in `UnitySim/TelemetryLogs/<timestamp>_<controller>.csv`, header row from channel names, plus a small JSON sidecar with run metadata (controller DLL hash, rates, vehicle config).

## Build toolchain (Controllers/)

- CMake + MSVC (or clang) → `controller.dll`; `build.ps1` builds and copies to `UnitySim/Assets/Plugins/x86_64/` (and to a hot-reload staging path while the editor runs).
- Structure the CMake so each controller folder is a target sharing `common/` + `targets/sim/`.
- Arduino/STM32 targets are out of scope for the first milestone but the layout above is what makes them a drop-in later (PlatformIO project referencing the same `common/` sources).

## Implementation order

1. **Scaffold**: directory layout, Unity project (Unity 6 LTS via Unity Hub — user creates it if Hub isn't scriptable), `Controllers/` CMake skeleton, git init at the root with a Unity + build-artifacts .gitignore.
2. **Sim core**: SimClock/SimulationRunner with fixed 500 Hz physics and configurable controller rate; a flat ground plane test scene.
3. **Vehicle v1**: differential-drive robot from primitives with DC-motor + friction model; drivable open-loop from keyboard to validate physics.
4. **Bridge**: controller_api.h, NativeControllerLoader with hot-reload, a trivial pass-through C controller proving the round trip.
5. **First real controller**: PID wheel-speed + heading controller in C (`common/pid.c`), tuned live.
6. **Telemetry**: TelemetryHub, GraphOverlay, CsvLogger — graph setpoint vs. actual, motor outputs, PID terms via debug channels.
7. **Docs**: interface spec in `Docs/` so future targets (Arduino, quad) follow the contract.

(Quadcopter, HIL-over-serial, and PlatformIO hardware targets are explicitly follow-on milestones, not part of this bootstrap.)

## Verification

- Open-loop: drive the robot with keyboard, confirm stable physics at 500 Hz and sane motor response.
- Closed-loop: load the PID controller DLL, command a step change in target velocity; graphs show setpoint vs. measured wheel speed converging; deliberately detune gains and watch oscillation appear (proves the loop is real).
- Hot reload: change a gain in C, run `build.ps1`, reload in-editor without restarting play mode session.
- CSV: after a run, open the log and confirm timestamps are uniform at the control rate and channels match the graph.

---

# Iteration 2 — Track map, drivable car, and Manual Mode

## Context

Iteration 1 produced an autonomous diff-drive robot on a flat plane for testing C control code. This iteration adds a way to *play-test the game itself*: an outdoor dirt-track map, a proper 4-wheel car you drive by hand, and controller/keyboard/mouse support. A **Manual Mode** (default) lets a human drive directly; a toggle (**M**) hands control to the loaded C controller (**Autonomous**). This validates the vehicle/physics feel and gives an environment (jumps, obstacles, finish line, lap timing) that future control algorithms will be scored against.

Decisions (confirmed with user): 4-wheel car on Unity WheelColliders; add the Input System package; in-game Manual/Autonomous toggle defaulting to Manual; track includes a bordered loop, ramps/dirt jumps, obstacles, and a finish line with lap timing. The existing diff-drive scene and its autonomous demo are kept unchanged.

## Design

### Input (dual-backend, no hard package dependency)
- New `Core/InputReader.cs` static helper exposing `Throttle()`, `Steer()`, `Brake()`, `Handbrake()`, `Respawn()`, `ModeTogglePressed()`, `MouseSteerDelta()`. Internally uses `#if ENABLE_INPUT_SYSTEM` (Gamepad/Keyboard/Mouse `.current`) and `#if ENABLE_LEGACY_INPUT_MANAGER` (`Input.GetAxis`/`GetButton`), merging both so keyboard **and** gamepad work simultaneously. This makes the code compile and run regardless of the project's Active Input Handling setting, and is reused by both car and diff-drive input.
- Add `com.unity.inputsystem` to `UnitySim/Packages/manifest.json`. **One-time manual step** (documented, not scriptable): Edit ▸ Project Settings ▸ Player ▸ Active Input Handling = **Both** (Unity restarts). Without it, gamepad falls back to keyboard-only via the legacy path — still functional.

### Vehicle interfaces (generalize the control seam)
- New `Vehicles/IManualDriver.cs`: `void ReadManualCommands(float[] actuatorOut)` — fills the actuator buffer directly from human input.
- New `Vehicles/ISetpointSource.cs`: `float[] Setpoints { get; }` — high-level targets for Autonomous mode.
- `Core/VehicleInput` (existing) additionally implements both: manual = left/right from throttle±steer; setpoints unchanged. Refactor it to read via `InputReader` so it no longer hard-depends on legacy Input.

### CarVehicle (`Vehicles/CarVehicle.cs`, implements `IControlledVehicle`)
- Chassis `Rigidbody` + `BoxCollider`; 4 child `WheelCollider`s at the corners with arcade-tuned suspension (spring/damper/suspensionDistance), friction curves, `forceAppPointDistance`, plus mild downforce and an anti-roll bar for stable jump landings. Wheel-visual cylinders updated from `WheelCollider.GetWorldPose`.
- **Actuator convention**: `actuator[0]`=throttle [-1,1], `actuator[1]`=steer [-1,1], `actuator[2]`=brake [0,1]. `StepPhysics(dt)` applies latched (ZOH) commands: `motorTorque` to driven wheels, `steerAngle` to fronts, `brakeTorque`; handbrake locks the rear.
- `SampleSensors`: `wheel_vel[0..3]` = each `WheelCollider.rpm` → rad/s; IMU via the existing `Sensors/ImuSensor`. `PublishTelemetry`: speed (m/s), steer angle, wheel rpm. `ResetVehicle`: respawn upright at the spawn/last checkpoint, zero velocities (bound to Respawn input).
- New `Core/CarInput.cs` implements `IManualDriver` (throttle/steer/brake/handbrake from `InputReader`, optional mouse steering) **and** `ISetpointSource` (setpoint[0]=throttle·maxSpeed as target speed, setpoint[1]=steer) so Autonomous mode has meaningful targets.

### SimulationRunner mode switch (`Core/SimulationRunner.cs`)
- Add `enum DriveMode { Manual, Autonomous }`, `public bool startInManual = true`, and an **M**-key/gamepad toggle in `Update`.
- Replace the concrete `VehicleInput input` field with `MonoBehaviour inputBehaviour`; resolve `IManualDriver`/`ISetpointSource` in `Awake`.
- In `ControlStep`: **Manual** → `manualDriver.ReadManualCommands(_actuators)`, skip the DLL; **Autonomous** → existing DLL path using `ISetpointSource`. Telemetry/CSV record in both modes; add `mode` and `cmd/throttle`,`cmd/steer` channels. On-screen HUD shows current mode.

### Track map (procedural primitives + generated materials)
- New `Track/TrackBuilder.cs` (static helpers) and `Core/TrackBootstrap.cs` (mirrors `SimBootstrap`): builds dirt ground, a rounded-rectangle **bordered loop** (road segments + berm walls from a parametric path), **ramps/dirt jumps** (`MakeRamp` = tilted boxes; rounded humps from flattened cylinders), **obstacles** (cone-ish cylinders, blocks, barriers), and a **finish line** (checkered box via a runtime-generated `Texture2D` + posts + trigger volume). Materials generated in code (dirt brown, road, berm, orange cones).
- New `Track/LapTimer.cs`: trigger at the finish line; `OnTriggerEnter` detects `CarVehicle` (via `GetComponentInParent`), debounced by a min-lap-time; tracks current/last/best lap and lap count; renders via `OnGUI`.
- Spawns the car in Manual mode with `ChaseCamera` (existing) and the `GraphOverlay` (existing).

### Car C controller (keeps the "write control code" story intact for the car)
- New `Controllers/car_pid/car_control.{h,c}`: reuses `common/pid.c`; PID on average wheel speed → throttle, steer passthrough. Debug names: `target_speed,speed_err,throttle`.
- New `Controllers/targets/sim/car_main.c`: implements the ABI (`ctrl_*`) mapping `setpoint[0]`→target speed, `wheel_vel` avg→measured speed, outputs `actuator[0]`=throttle, `actuator[1]`=steer.
- `Controllers/CMakeLists.txt`: add a second SHARED target `car_controller` (→ `car_controller.dll`) alongside `controller`, both copied to `Plugins/x86_64`. The Track scene's runner sets `dllRelativePath = Plugins/x86_64/car_controller.dll`; the diff-drive scene keeps `controller.dll`. If unbuilt, Autonomous falls back to open-loop (already handled).

### Editor
- `Assets/Editor/SceneBuilderMenu.cs`: add **Tools ▸ AIHWSim ▸ Create Track Scene** (new scene with one `TrackBootstrap`). Keep the existing bootstrap menu item.

## Files

New: `Vehicles/CarVehicle.cs`, `Vehicles/IManualDriver.cs`, `Vehicles/ISetpointSource.cs`, `Core/CarInput.cs`, `Core/InputReader.cs`, `Core/TrackBootstrap.cs`, `Track/TrackBuilder.cs`, `Track/LapTimer.cs`, `Controllers/car_pid/car_control.{h,c}`, `Controllers/targets/sim/car_main.c`.
Modified: `Core/SimulationRunner.cs` (mode switch, interface-based input), `Core/VehicleInput.cs` (implement interfaces, use `InputReader`), `Core/SimBootstrap.cs` (wire input via interfaces), `Assets/Editor/SceneBuilderMenu.cs`, `Controllers/CMakeLists.txt`, `UnitySim/Packages/manifest.json`, `README.md`.
Reuse: `common/pid.c`, `Core/ChaseCamera.cs`, `Telemetry/TelemetryHub` + `GraphOverlay` + `CsvLogger`, `Sensors/ImuSensor` + `NoiseModel`, the `IControlledVehicle` seam and `SimulationRunner` loop.

## Verification

- **Build**: `Controllers/build.ps1` produces both `controller.dll` and `car_controller.dll` (needs CMake + a 64-bit toolchain — see the toolchain note; local MinGW is 32-bit).
- **Manual drive**: Create Track Scene ▸ Play. Drive the car with WASD/arrows + mouse and with a gamepad (left stick/triggers); confirm steering, throttle, braking, handbrake, and **R** respawn. Send it off a ramp and confirm it jumps and lands stably.
- **Track**: verify berms bound the loop, obstacles/cones are hittable, and the finish line renders checkered.
- **Lap timing**: cross the finish line twice; HUD shows a plausible lap time and updates best lap; rapid re-crossing is debounced.
- **Mode toggle**: press **M** to switch to Autonomous; with `car_controller.dll` loaded the car holds the stick-commanded speed and steer; graphs show `dbg/target_speed` vs `veh/speed` converging. Press **M** to resume Manual.
- **Regression**: the original Create Bootstrap Scene (diff-drive) still runs and its autonomous PID demo is unchanged.
- **CSV**: a Track-scene run logs `cmd/throttle`, `cmd/steer`, `veh/speed`, and `mode` at the control rate.

---

# Iteration 3 — Configurable sensors + Garage (vehicle assembly) mode

## Context

The sim's premise is "write firmware in C against realistic sensors." Right now the sensor set is hard-coded (IMU + 4 wheel velocities baked into the ABI). This iteration makes sensors **user-configurable parts**: a camera (aimable, streams a real grayscale frame to the controller), directional time-of-flight distance sensors, wheel encoders, and motor electrical feedback (voltage/current/torque). A new KSP-style **Garage** mode lets the player assemble a vehicle from a preset body shape, wheels, and drag-placed/aimed/configured sensors, save it as JSON, and drive it on the existing track.

Decisions (confirmed with user): camera sends a **full grayscale image buffer** across the ABI; **preset body shapes** (no mesh import yet); **JSON designs + garage flow** (named save/load library, Drive button, Garage button in the track pause menu); implemented in **two phases** — Phase A (sensor framework + ABI v2 on the existing car) is testable before Phase B (garage builder) starts.

Constraint carried over: no 64-bit C toolchain on this machine, so controller DLLs can't be built locally. Sensor data is still fully verifiable in Manual mode via telemetry graphs/CSV and the camera HUD; the C side is validated by syntax-checking with the local 32-bit gcc.

## Phase A — Sensor framework + ABI v2

### ABI v2 (`Controllers/hal/controller_api.h`, mirrored in `Bridge/ControllerInterop.cs`)

Keep the existing fixed fields (back-compat for diff-drive) and append a generic, manifest-described sensor block:

```c
#define CTRL_ABI_VERSION 2
enum { SENSOR_TOF = 1, SENSOR_ENCODER = 2, SENSOR_MOTOR = 3, SENSOR_IMU = 4, SENSOR_CAMERA = 5 };

typedef struct SensorInfo {          /* one entry per configured sensor */
    char  name[32];                  /* user-chosen sensor name          */
    int   type;                      /* SENSOR_* enum                    */
    int   data_offset, data_count;   /* slice of CtrlInputs.sensor_data  */
    float range_min, range_max;      /* configured output value range    */
} SensorInfo;

typedef struct CtrlInputs {
    float time_s, dt_s;
    float gyro[3], accel[3];
    float wheel_vel[4];
    float setpoint[4];
    /* --- v2 --- */
    const float* sensor_data;        /* flat array, layout per manifest  */
    int   sensor_count;              /* entries in the manifest          */
    int   sensor_data_len;           /* total floats in sensor_data      */
    const unsigned char* cam_pixels; /* grayscale row-major, or NULL     */
    int   cam_width, cam_height;
} CtrlInputs;

/* New OPTIONAL export — host calls it once after ctrl_init if present: */
CTRL_EXPORT void ctrl_configure(const SensorInfo* sensors, int count);
```

- C# side: `SensorInfo` as a blittable struct (fixed char[32]); `CtrlInputs` grows the pointer/int fields. During `Step`, pin the flat sensor `float[]` and the camera `byte[]` with `fixed` blocks around the native call (no GC allocs).
- `NativeControllerLoader`: resolve `ctrl_configure` via `GetProcAddress` as optional (null if absent — old controllers keep working). `SimulationRunner.LoadController` calls it with the vehicle's manifest after `ctrl_init`.
- Per-sensor data layouts (documented in the header): ToF → `[distance_m]` (range_max when no hit); Encoder → `[angular_vel_rad_s, tick_count]` per wheel; Motor → `[voltage_V, current_A, torque_Nm]` per driven axle; Camera → no floats (frame goes via `cam_pixels`), manifest entry carries width/height in `data_count`/`range_*` unused.

### Sensor components (`Assets/Scripts/Sensors/`)

- `SensorComponent` (abstract MonoBehaviour): `SensorName`, `Type`, `DataCount`, `Sample(float dt, float[] dest, int offset)`, `RangeMin/Max`, `PublishTelemetry(hub)`, `OnDrawGizmos` aim visualization. Placed as child GameObjects on the vehicle so position/aim come from the transform.
- `TofSensor`: raycast along `transform.forward` (configurable max range, optional n-ray cone with min-of aggregation, layer mask excludes the vehicle itself, `NoiseModel` on the reading). Output: distance in meters, `range_max` on no-hit.
- `WheelEncoderSensor`: wraps one `WheelCollider`; outputs angular velocity (rad/s) quantized to a configurable CPR plus a monotonically wrapping tick count — reuses the quantization idea from the existing `EncoderSensor`.
- `MotorFeedbackSensor`: derives voltage/current/torque from `CarVehicle`'s latched throttle command and applied motor torque via a simple DC-motor electrical model (configurable Vbat, Kt, R).
- `CameraSensor`: child `Camera` → small `RenderTexture` (default 64×48, configurable up to 128×96), FOV + aim configurable. Each control tick (decimated to a configurable sensor frame rate, default 10 Hz) it grayscale-converts into a reusable `byte[]` (`ReadPixels` at these sizes is cheap; convert `(r+g+b)/3`). Exposes `Pixels/Width/Height` + a `Texture2D` for the HUD picture-in-picture (drawn in `OnGUI`).
- `SensorRig` (on the vehicle root): discovers all child `SensorComponent`s, assigns `data_offset`s, builds the `SensorInfo[]` manifest and the flat `float[]`, samples everything at control rate, registers/publishes `sens/<name>/<field>` telemetry channels, exposes the single (first) camera's buffer for the ABI.

### Host loop changes

- `SimulationRunner`: new `public SensorRig sensorRig` (resolved in `Start` from the vehicle GO); `ControlStep` samples the rig and fills the v2 fields inside `fixed` pins; `RegisterChannels` adds the rig's channels; graph profile `Car` gains a "Sensors" pane (ToF distances). Camera PiP + ToF readout bars drawn by a small `SensorHud` component.
- `TrackBootstrap`: attach a default sensor loadout to the built car (1 forward camera on a mast, 3 ToF: front/front-left/front-right, encoders on all wheels, motor feedback) so Phase A is testable without the garage.
- `CarVehicle`: expose `WheelCollider` accessors + applied motor torque/throttle for the encoder/motor sensors (small public getters; no behavior change).

### C example (`Controllers/car_sensors/`)

New controller source demonstrating the v2 API: stores the manifest from `ctrl_configure`, finds sensors by name, brakes proportionally when the front ToF distance drops below a threshold, and computes camera mean-row brightness to steer toward the track (line-follow-lite). Added as a third CMake target `car_sensors_controller`. Syntax-checked with local gcc (`-fsyntax-only`); building the DLL still needs the 64-bit toolchain.

## Phase B — Garage (vehicle assembly) mode

### Design data (`Assets/Scripts/Garage/VehicleDesign.cs`)

`[Serializable]` POCO tree, JSON via `JsonUtility`:

- `VehicleDesign`: `name`, `bodyShape` (enum: **Box, Wedge, Buggy** — preset primitive compounds), `bodySize` (Vector3), `bodyColor`, `mass`, `WheelSetup` (radius, trackWidth, wheelbase, motorTorque, steerAngle, driveBias), `List<SensorSpec>`.
- `SensorSpec`: `name`, `type`, `localPos`, `aimEuler` (yaw/pitch), plus per-type config (`range`, `coneRays`, `cprTicks`, `camWidth/Height/Fov/RateHz`, `rangeMin/Max`) — flat fields, unused ones ignored per type (JsonUtility-friendly).
- Persistence: `<project>/UnitySim/Vehicles/<name>.json` (sibling of `TelemetryLogs`), `VehicleLibrary` static helper (List/Load/Save/Delete + a built-in default design matching today's car).

### VehicleFactory (`Assets/Scripts/Garage/VehicleFactory.cs`)

Single code path that turns a `VehicleDesign` into a live vehicle: builds the unscaled root + Rigidbody + `CarVehicle` (parameterized by the design instead of hard-coded), body visual per preset shape, wheels, then instantiates each `SensorSpec` as a child GameObject with the right `SensorComponent`, position, and aim, and adds the `SensorRig`. Used by **both** the garage preview and the track spawn. `TrackBootstrap.BuildCar` refactors to call it with `GameFlow.ActiveDesign ?? default`.

### Garage scene + UI

- `GarageBootstrap` (mirrors `TrackBootstrap`): flat showroom floor, soft lighting, orbit camera (`OrbitCamera`: drag-RMB rotate, wheel zoom), the design preview via `VehicleFactory` (physics frozen — `Rigidbody.isKinematic`), and `GarageUI`.
- `GarageUI` (IMGUI, consistent with PauseMenu style):
  - **Left panel** — part palette: body shape buttons, body size/color/mass sliders, wheel setup sliders, and "Add sensor" buttons (Camera / ToF / Encoder / Motor feedback).
  - **Placement** — after "Add", the sensor ghost follows the mouse; a raycast against the body places it on the surface (LMB confirms). Existing sensors are click-selectable (mouse ray → nearest sensor gizmo).
  - **Right panel** — selected-sensor inspector: name field, aim yaw/pitch sliders (live gizmo/frustum line preview), per-type config sliders, output range fields, Delete.
  - **Top bar** — design name field, Save / Load (list of `Vehicles/*.json`) / New, and **Drive** (saves, sets `GameFlow.ActiveDesign`, loads the track scene).
- `GameFlow` (static): `ActiveDesign` carrier across scene loads + `LoadTrack()` / `LoadGarage()` helpers.
- Scene wiring: `SceneBuilderMenu` gains **Tools ▸ AIHWSim ▸ Create Garage Scene** and programmatically adds GarageScene + TrackScene to `EditorBuildSettings.scenes` so `SceneManager.LoadScene` works in play mode.
- `PauseMenu`: add a **Garage** button (stops the run, loads the garage with the current design).

## Files

**Phase A** — New: `Sensors/SensorComponent.cs`, `Sensors/TofSensor.cs`, `Sensors/WheelEncoderSensor.cs`, `Sensors/MotorFeedbackSensor.cs`, `Sensors/CameraSensor.cs`, `Sensors/SensorRig.cs`, `Core/SensorHud.cs`, `Controllers/car_sensors/car_sensors.c`. Modified: `Controllers/hal/controller_api.h`, `Bridge/ControllerInterop.cs`, `Bridge/NativeControllerLoader.cs`, `Core/SimulationRunner.cs`, `Core/TrackBootstrap.cs`, `Vehicles/CarVehicle.cs`, `Controllers/CMakeLists.txt`, `Docs/` interface spec.

**Phase B** — New: `Garage/VehicleDesign.cs`, `Garage/VehicleLibrary.cs`, `Garage/VehicleFactory.cs`, `Garage/GarageBootstrap.cs`, `Garage/GarageUI.cs`, `Garage/OrbitCamera.cs`, `Core/GameFlow.cs`. Modified: `Core/TrackBootstrap.cs` (spawn via factory), `Core/PauseMenu.cs` (Garage button), `Assets/Editor/SceneBuilderMenu.cs`.

Reuse: `NoiseModel`, `TelemetryHub`/`GraphOverlay`/`CsvLogger`, `ITunable` (body/wheel params stay live-tunable), `InputReader`, existing headless batch-compile validation workflow.

## Verification

- **Phase A / Manual**: Play the track scene; camera PiP shows the forward view and pans when steering; ToF HUD bars shrink approaching a wall/cone and graphs show `sens/tof_front/dist` dropping; encoder channels track `wheel/…` velocities with visible quantization at low CPR; motor V/I/torque channels respond to throttle. CSV contains all `sens/*` channels.
- **Phase A / ABI**: headless batch compile clean; `gcc -fsyntax-only` passes on `controller_api.h` v2 + `car_sensors.c`; diff-drive scene still runs (v1 controller loads, `ctrl_configure` absent → skipped).
- **Phase B**: Create Garage Scene ▸ Play: build a vehicle (Wedge body, resized, recolored, 2 ToF + 1 camera placed and aimed), save → JSON appears in `UnitySim/Vehicles/`; reload it; **Drive** spawns exactly that vehicle at the track start line with its sensors live; pause ▸ Garage returns; re-entering Drive after edits reflects changes. Respawn/lap-timer/mode-toggle regressions re-checked on a factory-built car.

---

# Iteration 4 — Real per-wheel DC motors (voltage-driven)

## Context

Today the car's drivetrain is arcade: `actuator[0]=throttle` → `throttle · maxMotorTorque · frontDriveBias` → `WheelCollider.motorTorque` (in `CarVehicle.StepPhysics`), and the "Motor" part is a *feedback-only* sensor that back-computes V/I/τ from that torque. The user wants the reverse and more physical: each driven wheel is a **real brushed-DC motor**, the controller commands **voltage** per motor, and the resulting torque/current **emerges from the vehicle dynamics** (mass, grip, slope) via the motor's back-EMF against the wheel speed the physics produces. This makes the "write firmware, drive real motors" story genuine and lets a controller stall, current-limit, or torque-vector.

Decisions (confirmed with user): motors are **per-wheel garage parts** (a wheel is driven iff it has a Motor part; up to 4 independent); autonomous steering supports **both** a front-wheel steer servo **and** independent per-wheel voltages; **Manual** mode maps throttle→full-scale voltage through the *same* DC model (no volts exposed to the human) and thereby overrides the controller; motor parameters are **switchable** between electrical constants and datasheet figures. Carry-over constraint: no 64-bit C toolchain here, so the voltage controllers are gcc syntax-checked only — Manual mode fully exercises the motor physics without any DLL.

## DC motor model

New static helper `Vehicles/MotorModel.cs` holding the canonical **constants**: `Kt` (N·m/A, with Ke = Kt), `R` (Ω), `gearRatio`, `maxVoltage`, `noLoadCurrent` I₀, `viscousDamping` b, `efficiency`. Per physics step, for a motor on wheel *i* with latched voltage V:

```
ω_wheel  = wc.rpm · 2π/60
ω_motor  = ω_wheel · gearRatio
V        = clamp(V, −maxVoltage, +maxVoltage)
I        = clamp((V − Kt·ω_motor) / R, −maxVoltage/R, +maxVoltage/R)   // back-EMF; stall-current clamp
τ_wheel  = (Kt·I − b·ω_motor) · gearRatio · efficiency
wc.motorTorque = τ_wheel        // emergent: back-EMF rises with speed → torque falls; load slows ω → more current
```

Coasting (V≈0 with ω>0) yields negative current → natural engine/regen braking. `MotorModel` also provides **datasheet↔constants conversion** (closed-form): from nominal V, stall torque τ_s, no-load speed ω₀, no-load current I₀ → `R = Vₙ²/(τ_s·ω₀ + Vₙ·I₀)`, `K = τ_s·R/Vₙ`; and the inverse for display. The garage stores constants as source-of-truth and recomputes them when edited in datasheet mode.

## Vehicle & control-path changes

- **`Sensors/MotorFeedbackSensor.cs` → becomes the driven motor** (rename to `MotorPart`, still a `SensorComponent` so the rig keeps discovering it and publishing `sens/<name>/{voltage,current,torque}`). Adds the DC params, a latched `commandedVoltage`, an `actuatorIndex`, `wheelIndex`, `maxVoltage`, and `float StepDrive(float dt)` that runs the model above, sets its wheel's `motorTorque`, and caches V/I/τ for `Sample()`.
- **`Vehicles/CarVehicle.cs`**: drop the arcade drive (`maxMotorTorque`, `frontDriveBias`) from `StepPhysics`. Add `BindMotors(IList<MotorPart>)` (called by the rig) and a latched `_cmd[8]`. `SetCommands(actuators)` copies the array; `StepPhysics` then, per bound motor, applies `_cmd[motor.actuatorIndex]` volts via `StepDrive`; drives a **steer servo** (`_cmd[6]∈[-1,1] → maxSteerAngle`, slew-limited by a new `steerRateDegPerSec`) on the front wheels; and a **foot brake** (`_cmd[7]∈[0,1] → maxBrakeTorque`, plus handbrake on the rears). Expose `IReadOnlyList<MotorPart> Motors`. `ResetVehicle` clears motor state + steer angle. `GetTunables` drops motor torque/bias, gains steer-rate.
- **Actuator convention (single sink for both modes)**: `actuator[0..M-1]` = motor **volts** in manifest order (each motor's `actuatorIndex`), `actuator[6]` = steer `[-1,1]`, `actuator[7]` = brake `[0,1]`.
- **`Core/CarInput.cs`** (Manual): `ReadManualCommands` fills that same array from `InputReader` — for each `car.Motors[k]`, `actuator[k.actuatorIndex] = throttle · k.maxVoltage`; `actuator[6]=steer`; `actuator[7]=brake`; handbrake still via `SetHandbrake`. So Manual drives real motors at full scale and naturally overrides the controller. `Setpoints` (target speed, steer) stay for Autonomous operator intent.
- **`Core/SimulationRunner.cs`**: unchanged control flow (fills `_actuators` from the DLL, calls `SetCommands`). Telemetry: replace `cmd/throttle` with per-motor commanded-voltage channels (published from `CarVehicle.PublishTelemetry`) plus `cmd/steer_deg`, `cmd/brake`; update the Car graph "Commands" pane and add a "Motor current/torque" pane sourced from `sens/<motor>/{current,torque}`.

## ABI v3

- `Controllers/hal/controller_api.h`: bump `CTRL_ABI_VERSION` to 3; add `int actuator_index;` to `SensorInfo` (the `actuator[]` slot a motor reads; −1 for non-actuators). For MOTOR entries the existing `range_min/range_max` carry `−maxVoltage/+maxVoltage`. Document the actuator layout (volts + reserved steer@6 / brake@7). Mirror the new field in `Bridge/ControllerInterop.cs` (append `actuator_index` after `range_max`; prior offsets unchanged, still blittable).
- **Controllers**: update `Controllers/targets/sim/car_main.c` (add `ctrl_configure` to learn motor indices + Vmax; PID on target-speed → a voltage magnitude written to every motor; steer passthrough to `actuator[6]`) and `Controllers/car_sensors/car_sensors.c` (per-motor voltage from the speed PID, ToF-proportional brake on `actuator[7]`, camera steer on `actuator[6]`). Refresh their `ctrl_get_debug_names`. Both gcc `-fsyntax-only` checked; DLLs still need the 64-bit toolchain.

## Garage

- **`Garage/VehicleDesign.cs`** `SensorSpec` motor fields: replace `supplyVoltage/torqueConstant/resistance` with the full set — `maxVoltage`, `kt`, `resistance`, `gearRatio`, `noLoadCurrent`, `viscousDamping`, `efficiency`, a `motorEntryMode` (Constants/Datasheet), and datasheet cache (`stallTorque`, `noLoadRpm`, `nominalVoltage`). Default design keeps the two rear motors (RWD, steered fronts) tuned to move the ~900 kg car; migrate the old three fields.
- **`Garage/VehicleFactory.cs`**: the Motor branch of `CreateSensor` sets all DC params onto `MotorPart`.
- **`Garage/GarageUI.cs`**: Motor inspector gains a **Constants ⇄ Datasheet** toggle (sliders for the active set, live-converted via `MotorModel`), wheel-assignment, and Vmax. Add `steerRateDegPerSec` to the wheel panel (extends `WheelSetup`).
- **`Sensors/SensorRig.cs`**: when building the manifest, assign each motor an `actuatorIndex` (ordinal among motors), set its `range_*` to ±Vmax, write `actuator_index` into `SensorInfo`, and call `vehicle.BindMotors(...)` so `CarVehicle` has the motor list before the first `StepPhysics`.
- **`Sensors/WheelEncoderSensor.cs`**: add an optional `gearRatio` (default 1 = wheel shaft) so an encoder can report motor-shaft ticks; already per-wheel.

## Files

New: `Vehicles/MotorModel.cs`. Renamed/reworked: `Sensors/MotorFeedbackSensor.cs` → `Sensors/MotorPart.cs`. Modified: `Vehicles/CarVehicle.cs`, `Core/CarInput.cs`, `Core/SimulationRunner.cs`, `Sensors/SensorRig.cs`, `Sensors/WheelEncoderSensor.cs`, `Garage/VehicleDesign.cs`, `Garage/VehicleFactory.cs`, `Garage/GarageUI.cs`, `Bridge/ControllerInterop.cs`, `Controllers/hal/controller_api.h`, `Controllers/targets/sim/car_main.c`, `Controllers/car_sensors/car_sensors.c`, `Docs/interface-spec.md`, `README.md`.

Reuse: `NoiseModel` (motor feedback noise), `WheelCollider.rpm`/`motorTorque` (the physics that closes the electrical loop), `SensorRig`/`TelemetryHub`/`CsvLogger`, the headless batch-compile + gcc syntax-check workflow. Note: `Actuators/DcMotorModel.cs` (a normalized-command lag model that computes its own speed) is **not** reused — it doesn't couple to WheelCollider physics.

## Verification

- **Manual (no DLL needed)**: Play the track; throttle accelerates the car through the motors. `sens/<motor>/current` and `/torque` spike on launch and while climbing a ramp, then fall as speed builds (back-EMF) — the emergent-load proof. Reverse works (negative voltage). Steering servo slews rather than snapping. `cmd/<motor>/volt` matches throttle·Vmax.
- **Datasheet math**: unit-sanity in the garage — entering a datasheet motor then switching to Constants shows Kt/R consistent with `R = Vₙ²/(τ_s·ω₀ + Vₙ·I₀)`; round-tripping back is stable.
- **ABI**: headless Unity compile clean; `gcc -fsyntax-only` passes on `controller_api.h` v3 + both updated controllers; diff-drive scene still runs (its v1 controller ignores the new field).
- **Garage**: add a Motor part, assign it to a wheel, set params, Drive → that wheel is powered; a wheel with no Motor free-rolls; front-motor + steer on the same wheel works (FWD).
- **Regression**: respawn, lap timer, Manual⇄Autonomous toggle, and the sensor HUD/telemetry from Iterations 2–3 still work on a factory-built car.

---

# Iteration 6 — KSP-VAB-style Garage

## Context

The garage works but doesn't *feel* like a vehicle builder: wheel visuals aren't posed in the kinematic preview (the dark cylinders sit at identity pose — the "wheels don't render properly" bug), sensors are just marker spheres with a stick, placement is click-to-teleport + sliders, and the UI is plain grey IMGUI. The user wants the garage to feel like KSP's VAB: proper stylized part visuals (wheels with rims/hubs/motor cans; cameras and ToF as recognizable little models, aim vectors kept but toggleable), grab-and-drag placement with a ghost preview, mirror symmetry, a dark KSP-ish skin with an icon part palette, a live engineer's stats readout, undo/redo, and pan/focus camera controls.

Decisions (confirmed with user): **full drag-and-drop** placement; **2x mirror symmetry** across the centerline; **stylized compound primitives** (not full procedural meshes); **all four** extra features (UI reskin, stats panel, undo/redo, camera pan+focus). Everything stays runtime-code-generated, IMGUI, single build path (`VehicleFactory`) so visuals appear identically in garage and on track. Carry-over constraint: validation = headless batch compile; user play-tests.

Six steps, each independently compilable (headless compile checkpoint after each).

## Step 1 — Stylized part visuals + wheel-pose fix

**New `Vehicles/PartVisualFactory.cs`** — static builder for compound-primitive part bodies, the single visual source for garage AND track. All primitives collider-stripped (pattern of `CarVehicle.AddBodyPiece`) and placed on **layer 2 (Ignore Raycast)** so nothing interferes with garage raycasts or track physics. Cached shared Standard-shader materials (tire near-black, rim light grey, hub dark grey, motor-can silver, housing dark, lens glossy black, PCB green, emitter dots).
- `BuildWheelViz(holder, radius, powered)` — tire cylinder (Euler(0,0,90), width ≈0.24) + two contrast rim discs + hub + 5 lug studs on a bolt circle + (if powered) a motor-can cylinder on the inboard axle.
- `BuildCameraViz(parent)` — housing box (0.16×0.12×0.10) + lens barrel along +z (kept ≤0.10) + glass disc.
- `BuildTofViz(parent)` — PCB slab (0.10×0.02×0.08) + emitter/receiver dots protruding +z.
- `BuildEncoderViz(parent)` — tiny ticked disc.
- `MakeGhostMat(tint)` — transparent Standard material (used in Step 3).

**Modify:**
- `Vehicles/CarWheelConfig.cs`: add `public bool powered;`
- `Vehicles/CarVehicle.cs`: `MakeWheel` (~313-321) uses `PartVisualFactory.BuildWheelViz`; new `PoseWheelVisualsFromConfig()` called at end of `Awake` — sets each viz to `TransformPoint(cfg.localPos)` + yaw rotation. **This fixes the garage wheel-pose bug**: preview never runs `StepPhysics`, so the config pose persists; on track the first `StepPhysics`/`GetWorldPose` overwrites it as today. Add `GetWheelVisual(int i)` accessor (used in Step 3).
- `Garage/VehicleFactory.cs`: wheel-config loop sets `powered = w.powered`; `CreateSensor` calls the matching `Build*Viz` on each sensor GameObject.
- `Sensors/CameraSensor.cs`: `cam.cullingMask &= ~(1 << PartVisualFactory.VizLayer);` so part viz never occludes any sensor camera.

## Step 2 — Camera pan + focus

- `Core/InputReader.cs`: add (dual-backend pattern) `MiddleMouseHeld`, `LeftMouseHeld`, `LeftMouseReleased`, `FocusPressed` (F), `UndoPressed` (Ctrl+Z), `RedoPressed` (Ctrl+Y), `CancelPressed` (Esc), `MirrorTogglePressed` (X — avoids M mode toggle).
- `Garage/OrbitCamera.cs`: MMB pan (`pivot -= (right·dx + up·dy) · 0.0016·distance`, gated on `!blockDrag`); `FocusOn(worldPoint, newDistance)`.
- `Garage/GarageUI.cs`: F with a selection → resolve part world pos (wheel transform or sensor transform) → `Orbit.FocusOn(pos, 4f)`. `GarageBootstrap` stores `built.sensors` as `public SensorComponent[] PreviewSensors` in `RebuildPreview`.

## Step 3 — Drag-and-drop placement + ghost + aim-vector toggle

**New `Garage/PartGhost.cs`** (plain class): ghost hierarchy built by `PartVisualFactory` with materials swapped to `MakeGhostMat`; no colliders, layer 2 (raycasts pass through to body). `ForWheel(radius, powered)` / `ForSensor(kind)`, `SetPose`, `SetValid(bool)` (green vs red tint), `Yaw` field, `Destroy()`.

**Modify `Garage/GarageBootstrap.cs`:** `ShowAimVectors` flag + `SetAimVectorsVisible(bool)` (dir-line list kept from `MakeMarker`); `SetPartVisible(type, index, visible)` (hides marker + part viz while its ghost is dragged).

**Modify `Garage/GarageUI.cs`** — drag state machine replacing the click block in `Update`:
`enum DragState { Idle, MouseDownOnMarker, PlacingNew, DraggingExisting }`
- Idle: press on marker → MouseDownOnMarker; press on body with selection → existing click-to-move fallback.
- MouseDownOnMarker: release <6 px → select (today's behavior); move >6 px held → DraggingExisting (hide part, spawn ghost seeded with spec yaw).
- PlacingNew: palette buttons create a *pending* spec (not yet added to the design); ghost follows the surface.
- Per-frame during drag: `RaycastAll`, take the hit on `PreviewRoot`; compute local pos/normal exactly like `MoveSelectedToBody` (wheels +0.05 normal offset, sensors +0.06 + aim from normal, yaw overridden by ghost `Yaw`); scroll wheel rotates `Yaw` in 15° steps; `Orbit.blockDrag = true` for the whole drag; no body hit → red ghost, drop refused.
- Drop (release / click) with valid pose → commit spec (+add if new), `PushUndo`, one `RebuildPreview`, select part. Esc cancels (restores hidden part). No rebuilds mid-drag.

## Step 4 — Mirror symmetry

**Data model:** add `public int mirrorGroup = -1;` to both `WheelSpec` and `SensorSpec` (stable shared id — survives list removals/reorders, unlike paired indices; JsonUtility-flat; old JSONs default to −1 = unlinked; `Clone()` carries it automatically).

**New `Garage/SymmetryUtil.cs`:** `NextGroupId`, `FindTwin` (wheel/sensor), `MirrorInto(src,dst)` (copies all fields then mirrors: `localPos.x` negated; wheel `yaw = -yaw`, `reverseSteering` flipped, name +"_m", motor params verbatim; sensor `aimEuler.y/.z` negated, pitch kept), `SyncTwin(design, edited)`; `CenterDeadzone = 0.05`.

**Behavior:** the mirror toggle (`_mirrorMode`, UI toggle + X hotkey) governs *new placements only*; **linked twins always stay synced** while linked (inspector edits and drag-drops call `SyncTwin` before rebuild — no master/slave confusion). Placement in mirror mode with `|x| > deadzone` → assign group id, create twin, show a second mirrored ghost during the drag; inside deadzone → single unlinked part. Delete removes the twin too. Inspector shows "Mirrored (group N)" + a **Break link** button.

## Step 5 — KSP-style UI reskin + icon palette

**New `Garage/GarageSkin.cs`:** lazily built shared `GUISkin` — dark bg `(0.10,0.11,0.13,0.96)`, KSP-orange accent `(1,0.62,0.20)`, runtime 1×1/rounded `Texture2D` backgrounds for box/button/toggle/label/textField/slider(+thumb); exposes `Skin`, `Header/Panel/Tab/TabActive/StatLabel` styles.

**New `Garage/PartIconFactory.cs`:** icon thumbnails as **RenderTexture snapshots of the real part visuals** (zero drift from models): temp root at (0,−500,0), build viz via `PartVisualFactory`, temp camera at a 3/4 view with transparent bg → 64×64 `Texture2D`, cached by key (`wheel`, `wheel_powered`, `camera`, `tof`, `encoder`); generated lazily on first `OnGUI`.

**Modify `Garage/GarageUI.cs`:** `GUI.skin = GarageSkin.Skin` at top of `OnGUI`; left panel becomes **category tabs** (BODY | PARTS via `GUILayout.Toolbar`) — BODY tab = current shape/size/color/mass/steering sliders; PARTS tab = 2-column 64 px icon grid whose buttons enter the `PlacingNew` drag state; hovered-part name label. Global toggles row: **Aim vectors** and **Mirror ✕2**. All new rects registered in `PointerOverUI`.

## Step 6 — Stats readout + undo/redo

**New `Garage/VehicleStats.cs`:** `Compute(design)` → `{totalMass (mass + 20/wheel — WheelCollider mass is 20), wheels, powered, steered, totalStallTorqueNm (Σ kt·(Vmax/R)·gear·eff), estTopSpeedMs (min over powered wheels of (Vmax/kt)/gear · radius; 0 if none)}`. Drawn in a bottom-left panel, recomputed each `OnGUI`.

**New `Garage/DesignHistory.cs`:** undo/redo stacks of `JsonUtility.ToJson` snapshots (capacity 50); coalescing — same `changeKey` within 0.7 s is skipped, so a slider drag = one step (aligned with the 0.15 s rebuild debounce). Capture the **pre-mutation** snapshot via `GarageBootstrap.PushUndo(key)`:
- add / drop / delete / body-shape / twin-create / break-link → always push;
- `Slider()` helper pushes `"slider:"+label` on first change (coalesced);
- Load/New clears history; undo/redo suppressed mid-drag. Ctrl+Z/Ctrl+Y in `Update` → `bootstrap.SetDesign(history.Undo/Redo(D))`.

## Files

**New:** `Vehicles/PartVisualFactory.cs`, `Garage/PartGhost.cs`, `Garage/SymmetryUtil.cs`, `Garage/GarageSkin.cs`, `Garage/PartIconFactory.cs`, `Garage/VehicleStats.cs`, `Garage/DesignHistory.cs`.
**Modified:** `Vehicles/CarVehicle.cs`, `Vehicles/CarWheelConfig.cs`, `Garage/VehicleFactory.cs`, `Garage/GarageBootstrap.cs`, `Garage/GarageUI.cs`, `Garage/VehicleDesign.cs`, `Garage/OrbitCamera.cs`, `Core/InputReader.cs`, `Sensors/CameraSensor.cs`, `README.md`.
**Reuse:** `TrackBuilder.StandardMat`/`CheckerTexture` texture-generation idiom, `PartMarker` + `SetHighlight` selection infra, `MoveSelectedToBody` surface math, `RequestRebuild` debounce, `VehicleDesign.Clone` JSON round-trip, dual-backend `InputReader` pattern.

## Verification

After each step (Unity closed): headless batch compile (`-batchmode -quit -nographics -logFile`, no `-executeMethod`), grep for `error CS`, relaunch editor at the end.

User play-test script:
1. Garage: stock car shows styled wheels correctly posed on the body (bug fix); powered rears show motor cans; camera/ToF appear as little models.
2. Toggle aim vectors off/on.
3. Palette: click wheel icon → ghost slides over body, scroll rotates heading, click drops; grab an existing sensor and move it; Esc cancels; sliders still fine-tune.
4. Mirror on (X): place wheel off-center → mirrored twin (flipped x/yaw/reverse-steering); edit radius on one → both change; delete one → both gone; save/reload preserves links; old saves load unlinked.
5. Stats panel updates live with powered toggles / motor params (plausible mass, stall torque, top speed).
6. Ctrl+Z collapses a slider drag to one step; Ctrl+Y restores.
7. MMB pans, F frames selection, RMB orbit + zoom unchanged.
8. Drive: track spawn shows the same styled parts, wheels spin/steer normally (GetWorldPose), sensor HUD camera unoccluded, telemetry + diff-drive scene unchanged.

---

# Iteration 7 — Track Builder (tile-based map editor)

## Context

The track is currently a single hard-coded procedural oval (`TrackBootstrap`). The user wants a **Track Builder** mode styled like the garage: build a track/environment from pre-fabricated tile "parts" organized in categories (Floor / Walls / Obstacles / Misc), each with an icon palette, and drive the result live — custom maps load into the same track scene the way custom vehicles do (`GameFlow` carrier). The floor is a defined grid that auto-generates and is only ever *replaced*, never deleted, so there's always a click surface. Floor tiles carry their own physics (friction affecting the tires) and runtime-generated texture; obstacles include interactive pieces (ramps, speed bumps); walls come in several styles; misc includes start/finish flag, ordered checkpoints, lights, and a spawn marker.

**Decisions (confirmed with user):** orbit camera + top-down toggle (T); ONE track scene drives both the classic oval and custom maps (oval stays; pause menu gains a Track Builder button); flat floor — all elevation via placed parts; start/finish + **ordered checkpoints** (0 checkpoints degrades to today's line timing); map size configurable at creation AND per-edge resizable while editing (tiles preserved); floor painting = click + drag-paint; items grid-snap (tile centers/edges) with 15° scroll rotation; custom maps are standalone worlds (tile floor + surrounding dirt ground); with no start/finish the car spawns at map center — and a **spawn marker** misc item overrides spawn/respawn independent of the finish flag; floor surface set = dirt, asphalt, grass, sand, ice, **mud, rumble strip, boost pad**, checker.

Constraints carried over: IMGUI, runtime primitives only, Built-in RP, single Assembly-CSharp, JsonUtility, headless batch-compile validation, diff-drive scene untouched. No 64-bit C toolchain (irrelevant here — no ABI change).

## Architecture

Mirror the garage triad: **data** (`TrackDesign`) → **shared factory** (`TrackFactory`, used by builder preview AND drive scene) → **builder scene** (`TrackBuilderBootstrap` + `TrackBuilderUI`). A static `TrackCatalog` is the single source of truth for floor types and placeable items, consumed by the factory, palette icons, and ghosts. New folder `Assets/Scripts/TrackEd/` (namespace `AIHWSim.TrackEd`).

**Key physics decision — floor:** ONE invisible `BoxCollider` slab for the whole map (top at y=0) + collider-less thin visual boxes per tile (top flush at y=0). Friction lookup is **positional** (WheelHit point → tile index → catalog), not per-collider. Zero per-tile colliders, painting is a pure `sharedMaterial` swap (no rebuild), and the `CarVehicle` hook is one static call.

## New files (`Assets/Scripts/TrackEd/` unless noted)

### `TrackDesign.cs` — serializable data
- `PlacedItem { string itemId; float x, z; float yawDeg; int order; Clone() }` (order = checkpoint sequence, −1 otherwise).
- `TrackDesign { name; int width=20, length=20; float tileSize=4; int[] floor; List<PlacedItem> items; }` — floor row-major `idx = z*width + x`, value = floor-type id. `Clone()` = JSON round-trip; `Default(w,l)` = all dirt.
- Grid helpers: `FloorAt/SetFloor`, `TileCenter(tx,tz)` (map centered on origin), `WorldToTile`.
- `Resize(addWest, addEast, addSouth, addNorth, fillType=0)` — new array, copy overlapping window, fill new cells, offset ALL item positions by half the width/length delta (origin-centered map), cull items outside new bounds. All resize math lives here.

### `TrackCatalog.cs` — static part catalog
- `FloorTypeDef { id, label, frictionMult, rollingResist, boostAccel, Func<Texture2D> makeTexture, lazy Material Mat }` — **array index is the persisted id; append-only, never reorder**.
- Floor types: 0 dirt (1.0 baseline, speckled brown), 1 asphalt (1.15, dark grey), 2 grass (0.85), 3 sand (0.6 grip + rollingResist), 4 ice (0.3, pale blue smooth), 5 mud (0.55 grip + heavy rollingResist), 6 rumble strip (1.05, red/white stripes + slight bump feel), 7 boost pad (1.0, chevron texture, `boostAccel` > 0), 8 checker (1.15, reuse `TrackBuilder.CheckerTexture`).
- `ItemDef { id, label, ItemCategory (Wall/Obstacle/Misc), ItemBehavior (None/Finish/Checkpoint/Light/Spawn), SnapMode (TileCenter/TileEdge), Action<Transform> build }`. Behavior components are attached by `TrackFactory`, not `build`, so ghosts/icons stay inert.
- Items — Walls: `wall_small` (0.8 m block), `tire_stack` (3 squashed black cylinders), `wall_tall` (4×2×0.4), `fence` (posts + 2 rails, 4 m). Obstacles: `ramp` (reuse `TrackBuilder.Ramp`, 4 m, 16°), `speed_bump` (half-buried cylinder, per the oval's Hump), `platform` (4×0.5×4), `cone`, `barrier`. Misc: `finish` (checker strip + posts + trigger — the oval's BuildFinishLine pieces), `checkpoint` (2 posts + floating numbered marker + trigger), `light_post` (pole + head + real `Light`), `spawn` (arrow pad marker; at most one).

### `TrackFactory.cs` — single build path
- `BuiltTrack { root; LapTimer lapTimer; Vector3 spawnPos; Quaternion spawnRot; }`
- `Build(TrackDesign d, bool interactive)`:
  - surrounding 300 m dirt Plane at y = −0.06;
  - ONE floor slab BoxCollider (top y=0) with a mid-friction PhysicMaterial (body/obstacle contacts), carrying `SurfaceMap` (bound when `interactive`);
  - per-tile collider-less visual Box (0.05 thick, top y=0), `sharedMaterial = Floors[type].Mat`, kept in a `Renderer[,]` for O(1) repaint;
  - items via `BuildItemVisual(def, parent)` at (x,0,z)+yaw, root tagged `PlacedItemMarker { int index; }`; when `interactive`: finish → trigger + `LapTimer`, checkpoint → trigger + `Checkpoint`, light → configure Light, spawn → recorded;
  - spawn priority: **spawn marker** > 6 m behind finish (+0.7 up, facing along) > map center facing +Z;
  - when `interactive`, `StaticBatchingUtility.Combine` on the floor-visual root.
- `BuildItemVisual` also used by ghost + icon factory.

### `Track/SurfaceMap.cs` — friction provider
- `static SurfaceMap Active` (OnEnable/OnDisable); holds design + floor collider. `static FrictionAt(in WheelHit hit)` / `SurfaceAt(hit)` returning `(frictionMult, rollingResist, boostAccel)`; returns baseline when `Active == null` **or `hit.collider != FloorCollider`** (so ramps/platforms and the oval/diff-drive scenes behave exactly as today).

### `Track/Checkpoint.cs`
- `[RequireComponent(Collider)]`, `int index; LapTimer timer;` — `OnTriggerEnter` guarded by `GetComponentInParent<CarVehicle>()` → `timer.NotifyCheckpoint(index)`.

### `TrackLibrary.cs` — clone of `VehicleLibrary` for `TrackDesign`, folder `<projectRoot>/Tracks/`.

### `TrackBuilderBootstrap.cs` — scene owner (mirrors GarageBootstrap)
- `Design = GameFlow.ActiveTrack?.Clone() ?? TrackDesign.Default()`; lighting/camera like garage; `_history = new DesignHistory<TrackDesign>()` with PushUndo/TryUndo/TryRedo/ClearHistory; `RequestRebuild()` 0.15 s debounce; `RebuildAll()` → `TrackFactory.Build(Design, interactive:false)`, cache `Renderer[,]` + floor collider; `RepaintTile(tx,tz)` = material swap only.
- Top-down toggle (T): save/restore (yaw,pitch,distance), snap to pitch 89.5 / fit distance. F refocuses map center.

### `TrackBuilderUI.cs` — IMGUI editor (GarageUI as template, reuse `GarageSkin`)
- `enum EditState { Idle, Painting, PlacingNew, DraggingExisting }`; `PointerOverUI()` rect caching; `Orbit.blockDrag` gating.
- Left panel tabs **FLOOR | WALLS | OBSTACLES | MISC**. Floor tab: icon grid of the generated textures; selecting enters persistent paint mode. Item tabs: snapshot icons → `PlacingNew` ghost.
- **Painting:** LMB-down on floor slab raycast → `PushUndo("paint")` at stroke start, then per-frame Bresenham-walk between last/current tile, `SetFloor` + `RepaintTile`; mouse-up ends the stroke (new coalesce key per stroke).
- **Placement:** `TrackGhost` (PartGhost pattern: VizLayer 2, no colliders, `MakeGhostMat`, SetValid green/red) snapped to tile center or nearest edge midpoint (edge snap seeds perpendicular yaw); scroll = ±15°; LMB commits (checkpoints get `order = max+1`; second `finish`/`spawn` → invalid red), Esc cancels.
- **Select/move/delete:** raycast item colliders → `PlacedItemMarker`; click selects (highlight), drag moves via ghost (hide original), Delete key / button removes.
- **Right panel:** map name, size readout, per-edge resize `W−/W+ E−/E+ S−/S+ N−/N+` (PushUndo → `Resize` → RebuildAll, clamp 4..50); ordered checkpoint list with ▲/▼ reorder; selected-item info.
- **Top bar:** New / Save / Load (TrackLibrary list) / **Drive ▶** (`GameFlow.ActiveTrack = Design.Clone(); GameFlow.LoadTrack();`) / Undo/Redo / hints. Ctrl+Z/Y via existing InputReader.

### `TrackGhost.cs`, `PlacedItemMarker.cs`, `TrackIconFactory.cs`
- Icons: floor = the generated textures directly; items = `PartIconFactory.Snapshot(t => TrackFactory.BuildItemVisual(def, t), 64)` (make `Snapshot` public), cached per id.

## Modified files

- **`Garage/DesignHistory.cs`** → `DesignHistory<T> where T : class` (mechanical; JsonUtility generic calls); update the one instantiation in `GarageBootstrap`.
- **`Garage/PartIconFactory.cs`** — expose `Snapshot` publicly.
- **`Garage/OrbitCamera.cs`** — `public float maxPitch = 80f` replacing the pitch-clamp literal (builder sets 89.5; garage unchanged).
- **`Core/InputReader.cs`** — add `TopDownTogglePressed()` (T) and `DeletePressed()` (Delete) via the dual-backend `KeyPressed` helper.
- **`Core/GameFlow.cs`** — `TrackBuilderSceneName` const, `static TrackDesign ActiveTrack` (null = classic oval), `LoadTrackBuilder()`.
- **`Core/TrackBootstrap.cs`** — split Awake into `BuildOvalEnvironment()` (existing code moved verbatim) vs `BuildCustomEnvironment()` (`TrackFactory.Build(GameFlow.ActiveTrack, interactive:true)` → spawn/lapTimer from `BuiltTrack`); shared tail (BuildCar → runner → HUD → PauseMenu) unchanged. `_lapTimer` may be null on finish-less maps — verify `CarInput` tolerates a null lapTimer.
- **`Vehicles/CarVehicle.cs`** — per-tile surface hook: promote forward stiffness 1.6 to `_fwdStiffness`; per wheel per FixedUpdate, `GetGroundHit` → `SurfaceMap.SurfaceAt` → if mult changed (per-wheel `lastMult` cache) reassign forward/sideways `WheelFrictionCurve.stiffness = base * mult`; `SetGrip` resets caches. Rolling resistance → small extra `brakeTorque` per wheel on that surface; boost → forward `AddForce` while any wheel is on a boost tile (short cooldown); rumble → small position-based sinusoidal vertical perturbation for bump feel. Baseline path (oval/diff-drive: `Active == null`) is a no-op — zero behavior change.
- **`Track/LapTimer.cs`** — `CheckpointCount`, `NextCheckpoint`, `NotifyCheckpoint(idx)` (in-order increments, out-of-order ignored); finish crossing invalid while `NextCheckpoint < CheckpointCount`; `ResetTimer` clears; HUD adds `CP: n/N` line only when checkpoints exist. Oval (count 0) byte-for-byte behavior.
- **`Track/TrackBuilder.cs`** — add `NoiseTexture(a,b,size)` and `StripeTexture(...)` generators alongside `CheckerTexture`.
- **`Core/PauseMenu.cs`** — `PendingExit.TrackBuilder` + "Track Builder" button routed through `RequestExit` (telemetry save prompt covers it); `DoExit` → `Time.timeScale=1; GameFlow.LoadTrackBuilder();`.
- **`Assets/Editor/SceneBuilderMenu.cs`** — **Tools ▸ AIHWSim ▸ Create Track Builder Scene** (TrackBuilderBootstrap, `TrackBuilderScene.unity`, AddSceneToBuild). *Manual step: user runs it once.*
- **`README.md`** — new scene + feature description.

## Step breakdown (headless compile checkpoint after each)

1. **Data + persistence + generic history** — TrackDesign, TrackCatalog (+ texture helpers), TrackLibrary; `DesignHistory<T>` + GarageBootstrap update.
2. **Runtime build path + lap logic** — TrackFactory, PlacedItemMarker, SurfaceMap, Checkpoint, LapTimer upgrade. Nothing calls it yet.
3. **Drive integration + surface physics** — GameFlow.ActiveTrack/LoadTrackBuilder, TrackBootstrap branch, CarVehicle surface hook (friction/rolling/boost/rumble), PauseMenu button.
4. **Builder scene skeleton** — TrackBuilderBootstrap, OrbitCamera.maxPitch, InputReader keys, SceneBuilderMenu entry.
5. **Builder UI core** — TrackBuilderUI tabs + floor paint (click/drag + Bresenham) + undo/redo + top bar (New/Save/Load/Drive), TrackIconFactory.
6. **Item placement** — TrackGhost, snap + rotate, select/move/delete, finish/checkpoint/spawn behavior end-to-end, checkpoint reorder panel.
7. **Polish** — per-edge resize UI, StaticBatchingUtility.Combine, checkpoint HUD, validation (single finish/spawn, red ghost), README.

## Risks

- **Draw calls** (40×40 = 1600 tile renderers): one shared Material per floor type (9 total) keeps dynamic batching effective; drive scene additionally static-combines the floor. Builder keeps individual renderers for O(1) repaint; fall back to per-type combined meshes if profiling demands.
- **Resize math** (origin-centered map → item offsets on asymmetric resize): isolated inside `TrackDesign.Resize`.
- **Catalog id stability**: floor ids are persisted array indices — append-only, documented in TrackCatalog.
- **Null lapTimer** on finish-less maps — check `CarInput`'s reset path in step 3.

## Verification

After each step: headless batch compile (`-batchmode -quit -nographics -logFile`, no `-executeMethod`), grep `error CS`, wait for the Unity process to exit before reading the log; relaunch editor at the end.

User play-test script:
1. Tools ▸ AIHWSim ▸ Create Track Builder Scene ▸ Play: default 20×20 dirt map appears with orbit + T top-down toggle.
2. Paint: select asphalt, click and drag a loop; sweep fast — no gaps; Ctrl+Z undoes the whole stroke.
3. Place items: ramp/speed bump/walls snap to grid, scroll rotates 15°; tire stack topples when hit; fence/tall wall block the car.
4. Misc: place finish + 3 checkpoints + a spawn marker; reorder checkpoints; second finish refused (red ghost).
5. Drive ▶: car spawns at the marker; laps only count after crossing CPs in order (HUD `CP: n/N`); skipping a checkpoint invalidates the crossing.
6. Surfaces: asphalt grips, ice slides, sand/mud slow the car, boost pad kicks, rumble strip buzzes.
7. Resize the map ± on each edge — painted tiles/items stay put; save, New, reload — identical.
8. Pause ▸ Track Builder returns to the builder with the same map (telemetry save prompt intact); classic oval scene (no ActiveTrack) drives exactly as before; garage undo/redo still works (generic history); diff-drive scene untouched.

---

# Iteration 8 — Main menu, save system, 2-player split-screen (LAN-ready)

## Context

The game boots straight into whichever scene is open and has no persistence beyond vehicle/track JSON libraries. The user wants multiplayer on one map — **2-player split-screen now**, LAN/player-hosted servers later — which requires a **main menu** (simple IMGUI, expandable), **saves** (settings/options, player profiles + lap records, and session snapshots for SP *and* split-screen), and SP/MP session setup. Decisions (confirmed with user): race + sandbox gameplay (per-player lap timing, simple results); 2 players, horizontal split, P1 keyboard / P2 gamepad; networking is **architected only** this iteration (Unity NGO chosen for iteration 9 — no netcode package/code now); autonomous C-controller DLLs stay single-player; snapshots cover SP and split-screen.

Exploration inventory (verified): `VehicleFactory.Build` and `CarVehicle` are N-car ready; `NativeControllerLoader` is multi-instance safe (Guid shadow copies); the blockers are the fully-static `InputReader` (only `*.current` devices), full-screen `Screen.*` IMGUI HUDs, single `Camera.main`+AudioListener per bootstrap, `GameFlow`'s single ActiveDesign, LapTimer arming on ANY CarVehicle with shared state, `CsvLogger`'s fixed temp filename, and PauseMenu's single-runner coupling. No PlayerPrefs/settings exist anywhere. Input System 1.11.2 installed; no netcode packages.

## Scene flow (target)

MenuScene (NEW, boot scene) → Root: **Single Player** (vehicle+track pickers / Garage / Track Builder) | **Multiplayer** (split-screen setup; Host/Join LAN greyed "coming soon") | **Resume Drive** (snapshot list) | **Options** | **Quit**. TrackScene pause menu gains **Main Menu** and **Save snapshot**; race results overlay offers Keep driving / Rematch / Main Menu. Garage/builder Drive keeps going straight to the track. SimMain diff-drive scene untouched.

## New files

- **`Core/SessionConfig.cs`** — the LAN seam. `enum SessionMode { SinglePlayer, SplitScreen }` (LanHost/LanClient later); `[Serializable] PlayerSlot { name, VehicleDesign design, InputDeviceKind deviceKind, int gamepadIndex, string profileId, bool isLocal }`; static `SessionConfig { Mode, List<PlayerSlot> Players, int TargetLaps /*0=sandbox*/, ResolvePlayers() }`. `ResolvePlayers()` synthesizes one Merged-input slot from `GameFlow.ActiveDesign` when SP/empty — preserving every legacy entry path (garage Drive, builder Drive, pressing Play in TrackScene directly). GameFlow.ActiveDesign/ActiveTrack are KEPT; SessionConfig layers on top. Iteration 9's NetworkManager fills the same Players list from connections (isLocal=false).
- **`Core/PlayerInputSource.cs`** — `interface IDriverInputSource { Throttle, Steer, Brake, Handbrake, RespawnPressed, MouseSteerDelta }` (a future NetworkInputSource implements it too). `PlayerInputSource(kind, gamepadIndex)`: **Merged** delegates verbatim to static InputReader (byte-identical SP feel); **Keyboard** = Keyboard.current only; **Gamepad** = `Gamepad.all[index]` resolved per call (hot-plug safe, zeros when missing). Dual-backend `#if` idiom kept; under legacy-only compile Keyboard/Gamepad degrade to merged (MP page greys Start with a note). Static InputReader keeps all mouse/editor/UI hotkeys, PausePressed, ModeTogglePressed, and the merged axes.
- **`Core/PlayerRig.cs`** — plain per-player aggregate `{ slot, car, input, runner, sensorRig, camera, lapTimer }` built by TrackBootstrap.
- **`Core/SplitScreenHud.cs`** — one MonoBehaviour drawing per-player boxes inside each camera's `pixelRect` (GUI-space flip `Screen.height - yMax`): name, speed, `Lap n/N`, `CP i/c`, last/best. Replaces LapTimer HUD/SensorHud/GraphOverlay/runner box in split-screen.
- **`Track/LapTracker.cs`** — `[Serializable] { LapCount, CurrentLap, LastLap, BestLap = -1 /*sentinel, no Infinity in JSON*/, NextCheckpoint, Armed, [NonSerialized] lastCrossTime }` — reused directly by snapshots.
- **`Track/RaceDirector.cs`** — MonoBehaviour; inert when targetLaps==0 or no lapTimer. Subscribes `LapTimer.LapCompleted`; tracks finish order + total time; live standings banner; when all players finish, GarageSkin results overlay: Keep driving / Rematch (`GameFlow.LoadTrack()`, SessionConfig persists) / Main Menu. Works in SP when laps > 0.
- **`Menu/MenuBootstrap.cs`** — Awake: `SettingsStore.Apply()`, dark backdrop + slowly rotating kinematic showcar via `VehicleFactory.Build` (last-used design), camera, `MenuUI`.
- **`Menu/MenuUI.cs`** — IMGUI pages (GarageSkin): Root; SinglePlayer (vehicle cycle-picker over `["Stock Default"]+VehicleLibrary.List()`, track picker over `["Classic Oval"]+TrackLibrary.List()`, laps field, Drive/Garage/Track Builder); Multiplayer (2 rows: name/vehicle/device with live `Gamepad.all` validation — no two slots on one physical device, Merged disallowed in MP; shared track; laps; Start Split-Screen; greyed Host/Join LAN); Options (master volume, quality level, fullscreen, vSync, mouse steer, default names — mutate → Apply → Save live); Resume (snapshot rows with Load/Delete).
- **`Persistence/SaveSystem.cs`** — static JSON I/O under `<project>/Saves/` (same BaseDir idiom as VehicleLibrary): `LoadJson<T>/SaveJson<T>` (try/catch, pretty-print), `Snapshots/` subdir with List/Load/Save/Delete (`snapshot_yyyyMMdd_HHmmss.json`).
- **`Persistence/GameSettings.cs`** — `[Serializable] GameSettings { version, masterVolume, qualityLevel(-1=default), fullscreen, vSync, mouseSteer, player1Name, player2Name, lastVehicle, lastTrack, lastLaps, p1DeviceKind, p2DeviceKind, p2GamepadIndex }` + static `SettingsStore { Current (lazy), Save(), Apply() /*AudioListener.volume, QualitySettings, Screen.fullScreen, vSyncCount*/ }` with `[RuntimeInitializeOnLoadMethod]` ApplyOnBoot — no DontDestroyOnLoad object needed.
- **`Persistence/ProfileStore.cs`** — `PlayerProfile { name, totalLaps, totalDriveTime, List<TrackRecord>{trackName, bestLap=-1} }` in a `ProfileList` wrapper (JsonUtility needs it); `Get(name)` create-on-use, `RecordLap(profile, track, time)` → true on new best, saves profiles.json; track key = `GameFlow.ActiveTrack?.name ?? "Classic Oval"`.
- **`Persistence/SessionSnapshot.cs`** — `PlayerSnapshot { name, profileId, vehicleJson /*embedded design dump*/, deviceKind, gamepadIndex, position, rotation, linearVelocity, angularVelocity, LapTracker lap }`; `SessionSnapshot { version, savedUtc, mode, trackName, trackJson /*""=oval*/, targetLaps, simTime, players }`.

## Modified files

- **`Core/GameFlow.cs`** — add `MenuSceneName`, `LoadMenu()`, `public static SessionSnapshot PendingSnapshot`.
- **`Core/InputReader.cs`** — `PausePressed()` iterates `Gamepad.all` (either player can pause). Nothing else.
- **`Core/CarInput.cs`** — `public IDriverInputSource source;` (defaults to Merged in Start → SP unchanged); all input calls via `source.*`; respawn calls `lapTimer.ResetTimer(car)`; mouseSteer from settings in SP.
- **`Core/SimulationRunner.cs`** — flags defaulting to today's behavior: `allowModeToggle`, `showModeBox`, `loadController`, `logLabel` (→ CsvLogger), plus `SimTime` getter + `RestoreSimTime(t)`. Split-screen sets all off + `logCsv=false`.
- **`Telemetry/CsvLogger.cs`** — temp file `current_drive_{label}.csv` (label ""=today's name); label in Save() dest too.
- **`Track/LapTimer.cs`** — per-car refactor: `Dictionary<CarVehicle, LapTracker>`, `GetTracker(car)`, identity from `GetComponentInParent<CarVehicle>`, `NotifyCheckpoint(car, index)`, `ResetTimer()` (all) + `ResetTimer(car)`, `event Action<CarVehicle, LapTracker> LapCompleted`, `RestoreTracker(car, state)`, `bool showDefaultHud = true` (SP HUD byte-preserved incl. "cross line to start"; MP sets false).
- **`Track/Checkpoint.cs`** — pass the car: `timer.NotifyCheckpoint(car, index)`.
- **`Core/PauseMenu.cs`** — `runners` list (+`rigs`); `PendingExit.MainMenu`; **Main Menu** + **Save snapshot** buttons; telemetry prompt covers any dirty runner; Restart restarts all runners + all trackers + race; Tune + Save telemetry hidden in split-screen; Main Menu guarded by `Application.CanStreamedLevelBeLoaded` (menu scene may not exist yet).
- **`Core/TrackBootstrap.cs`** — the structural center: resolve slots → build environment once → `SpawnPoses(n)` (n=2: ±2.2 m along spawn-right; stagger 4 m behind when road too narrow) → per-slot `BuildPlayerRig` (car, CarInput+PlayerInputSource, runner with SP/MP flags, camera: SP = today's exact path with GraphOverlay+SensorHud; MP = P1 Camera.main top half rect (0,0.5,1,0.5) keeping the only AudioListener, P2 new camera without listener bottom half, ChaseCamera each, SplitScreenHud instead of graphs/hud) → shared PauseMenu with all runners → LapCompleted → ProfileStore hook → RaceDirector when laps>0 → consume `GameFlow.PendingSnapshot` (teleport cars + velocities, RestoreTracker, RestoreSimTime) → controller OnGUI box SP-only.
- **`Vehicles/CarVehicle.cs`** — `RestoreState(pos, rot, vel, angVel)` mirroring ResetVehicle's Discrete-mode teleport but setting saved velocities.
- **`Garage/GarageUI.cs`** + **`TrackEd/TrackBuilderUI.cs`** — `DoDrive` one-liners: `SessionConfig.Mode = SinglePlayer` (clear stale MP roster).
- **`Assets/Editor/SceneBuilderMenu.cs`** — **Tools ▸ AIHWSim ▸ Create Menu Scene** (`MenuScene.unity` + AddSceneToBuild). *Manual step: user runs it once (and ideally makes it build index 0).*
- **`README.md`** — menu/saves/split-screen docs.

## Save file layouts (`<project>/Saves/`)

`settings.json` = flat GameSettings. `profiles.json` = `{"profiles":[{name, totalLaps, totalDriveTime, records:[{trackName, bestLap}]}]}`. `Snapshots/snapshot_<stamp>.json` = SessionSnapshot with embedded vehicle/track JSON dumps (survives library edits) and serialized LapTrackers; `-1` sentinels instead of Infinity (JsonUtility-fragile).

## Scoping decisions

Split-screen disables: CSV telemetry, GraphOverlay, SensorHud, M-mode-toggle, controller DLLs, Tune panel. Pause stays global (`Time.timeScale`) — correct for couch play. Custom map without a finish → sandbox only (MP page notes it; no RaceDirector). Snapshot respawn poses = freshly computed start slots (respawn-after-resume returns to start line).

## Risks

SP regression surface (mitigate: Merged delegates to unchanged statics, all new flags default legacy, play-test SP after steps 3–4); `Gamepad.all` hot-plug index drift (resolved per call, re-pickable in menu); two runners writing identical `Time.fixedDeltaTime` (harmless, comment it); JsonUtility wrappers/no-Infinity; snapshot restore of mid-air velocity under CCD (reuses proven Discrete-teleport; test a mid-jump save); IMGUI overlay stacking (GUI.depth; pause/results early-out the HUD); menu scene missing until manual step (guarded button).

## Step breakdown (headless compile checkpoint after each; user play-tests between)

1. **Persistence foundation** — SaveSystem, GameSettings+SettingsStore, ProfileStore, SessionSnapshot (types only).
2. **Main menu + options** — MenuBootstrap, MenuUI (Root/SP/Options live; MP Start + Resume greyed), GameFlow LoadMenu, SceneBuilderMenu entry, PauseMenu Main Menu button. *Manual: user creates the Menu scene.*
3. **Per-player input layer** (invisible) — PlayerInputSource/IDriverInputSource, CarInput source field, SimulationRunner flags + SimTime, CsvLogger label, PausePressed all-pads. SP must feel identical.
4. **Per-car lap timing + records** — LapTracker, LapTimer dictionary refactor + LapCompleted, Checkpoint identity, per-car respawn reset, ProfileStore hook. SP laps unchanged; profiles.json accumulates bests.
5. **Split-screen** — SessionConfig, PlayerRig, SplitScreenHud, TrackBootstrap N-player refactor, PauseMenu runners list, MenuUI MP Start enabled, Garage/Builder one-liners.
6. **Race mode + results** — RaceDirector, laps fields SP+MP, Restart race reset.
7. **Session snapshots** — CarVehicle.RestoreState, PauseMenu Save snapshot, MenuUI Resume page, TrackBootstrap PendingSnapshot consumption.

## Verification

After each step: headless batch compile (0 `error CS`), wait for Unity exit before reading log; relaunch editor at the end. Play-test script:
1. Create Menu scene ▸ Play: menu shows rotating showcar; Options changes (volume/quality/fullscreen) apply live and survive restart via `Saves/settings.json`.
2. SP: menu → pick vehicle+track → Drive; garage/builder round-trips unchanged; M toggle + controller DLL + graphs + telemetry prompt all work as before.
3. Laps: SP lap/CP/best behavior identical; `Saves/profiles.json` gains bests; second profile via MP name.
4. Split-screen: 2 players (keyboard + pad) on oval and a custom map — independent steering/respawn, collisions, per-viewport HUDs, single AudioListener (no console error), pause pauses both, telemetry/graphs absent.
5. Race: first-to-3 shows standings then results overlay; Rematch rebuilds; laps=0 = sandbox.
6. Snapshot: save mid-drive (SP and MP, one mid-jump), quit to menu, Resume — poses, velocities, lap/CP state, sim time intact.
7. Regression: diff-drive SimMain untouched; pressing Play directly in TrackScene still works (ResolvePlayers synthesizes SP slot).

---

# Iteration 9 — Spline track ribbons (curved tracks in the editor)

## Context

The track builder is tile-based, so curves are blocky. The user wants **curved track pieces that follow/redraw to fit a spline with a defined width**, eventually expandable to **3D splines with per-segment rotation** (banking, elevated corkscrews). Decisions (confirmed with user): true **procedural ribbon mesh** (own MeshCollider + per-surface physics) layered above the tile floor — tiles stay as terrain; **click-through** editing (Catmull-Rom through dropped points, drag to reshape, live redraw); **3D-ready pipeline now** (mesh/data support full 3D + per-point roll), editor v1 places flat but exposes per-point **height and bank sliders**; extras: **closed loops, auto edge walls, kerb edge stripes, per-point width**; and — user's addition — **items/obstacles and surface painting must work ON the ribbon even when raised** (a speed bump or checkpoint on an elevated curve; repaint a stretch to ice), with placed items following spline redraws.

Runtime-generated meshes are consistent with the project's code-generated-only rule. Constraints carried over: IMGUI, JsonUtility (missing fields default → old saves load clean), headless compile checkpoints, split-screen/snapshots must keep working (they carry TrackDesign JSON verbatim).

## Key decisions

- **Per-point surface** (`List<int> surface`; segment i..i+1 uses point i's value). The mesh is emitted as one Mesh per contiguous surface **run**, each its own GameObject + MeshCollider + `SurfaceTag` — so `SurfaceMap` needs only a collider→floorType probe (CarVehicle untouched), materials stay the shared `FloorTypeDef.Mat`, and "repaint a stretch to ice" = flip point surfaces + re-mesh.
- **Kerb stripes** = second submesh (two ~0.10 m strips just inside the edges, +0.005 raised) with one shared lazy KerbMat (`TrackBuilder.StripeTexture`); keeps floor materials shared.
- **Edge walls** = one extruded strip Mesh (0.35 m tall × 0.20 m cross-section, both edges) + non-convex MeshCollider per spline; no SurfaceTag.
- **Item placement on ribbon**: free placement (no grid snap) when the ray hits a ribbon, yaw still 15°-stepped; `PlacedItem` gains `float y`. At build time every item is **dropped by raycast** (from stored y+3 downward against slab + ribbon colliders only, `Physics.SyncTransforms()` first) and oriented `FromToRotation(up, hitNormal) * yaw` — items "follow/redraw to fit" automatically; flat maps behave byte-identically (y=0, normal=up).
- **Ribbon cross-section**: top at `point.y + 0.03`, with a 0.15 m downward skirt (solid shallow slab) — at ground level the ribbon sinks into the floor slab so wheels never catch a knife edge; no z-fighting.
- **Drag preview**: pooled collider-less thin boxes on VizLayer per sample during point drags (no collider cooking); real re-mesh on the 0.15 s debounce with **targeted** `RebuildSpline(index)` (full RebuildAll only on release/commit).

## New files (`Assets/Scripts/TrackEd/` unless noted)

- **`SplineSpec.cs`** — `[Serializable] { List<Vector3> points (y = height); List<float> rollDeg; List<float> widths (default 6); List<int> surface; bool closed, edgeWalls, edgeStripes; }` + `EnsureArrays()` (pad/truncate parallel lists), `AddPoint` (copies last point's roll/width/surface), `InsertPoint(i)` (interpolates), `RemovePoint`, `Clone()`.
- **`SplineMath.cs`** — static Catmull-Rom `Position/Tangent(spec, seg, t)` (open = clamped ends, closed = wrapped); `SampleAll(spec, step≈1.5m)` → `Sample { pos, tan, roll, width, dist, surface }` (adaptive count per segment from an 8-point length estimate; scalars lerped per segment, surface NOT interpolated); `ComputeFrames(samples, closed, out right, out up)` — **parallel-transport frames** (no flipping), user roll applied about the tangent after transport; **closed-loop closure**: measure the seam angle mismatch θ and distribute `−θ·(dist/total)` as corrective roll across all samples; the last ring reuses the first ring's verts/frames so there's no crack.
- **`RibbonMeshBuilder.cs`** — `Build(SplineSpec, Transform parent, bool colliders)`: per surface run → `Run_k { MeshFilter, MeshRenderer [Floors[type].Mat, KerbMat?], MeshCollider (non-convex), SurfaceTag }`. Cross-section: top edge verts `pos ± right·w/2 + up·0.03`, bottom verts −0.15 skirt + side quads; explicit top normals = up; UVs u = across-meters/4, v = dist/4 (tiles the 4 m floor textures naturally); kerb UV v = dist/1. Optional `Walls` child (one mesh, both edges). Run boundaries duplicate the boundary ring; closed splines merge first/last run when same surface. A tiny `RibbonMeshMarker : MonoBehaviour` destroys the generated Mesh in OnDestroy (leak fix). Consts `TopOffset = 0.03`, `Skirt = 0.15`.
- **`SplinePointMarker.cs`** — `{ int spline, point; }` on clickable point-handle spheres (small SphereCollider).
- **`SplineRunMarker.cs`** — `{ int spline; int[] sampleToPointIndex; float[] sampleDist; Vector3[] samplePos; }` baked at mesh build, so the UI maps a ribbon hit → nearest sample → owning control point (paint + insert) without recomputing.
- **`Track/SurfaceTag.cs`** — `{ public int floorType; }`.

## Modified files

- **`TrackEd/TrackDesign.cs`** — `List<SplineSpec> splines = new()`; `EnsureSplines()` (null→new, EnsureArrays each; call alongside EnsureFloor in factory/bootstrap); `Resize` offsets every spline point by (dx, 0, dz), culls a spline only when ALL points fall outside; `PlacedItem` gains `float y` (old JSON → 0).
- **`Track/SurfaceMap.cs`** — in `At()`, before the slab check: `Dictionary<Collider,int> _tagCache` probe (miss → `GetComponent<SurfaceTag>()`, cache type or −1); ≥0 → SurfaceInfo from `TrackCatalog.Floors[type]` (extract shared struct-fill helper). Cache cleared on Bind/OnDisable. **CarVehicle untouched.**
- **`TrackEd/TrackFactory.cs`** — `Build`: `EnsureSplines()`; after BuildFloor, `BuildSplines` (colliders in BOTH modes — the builder needs them for placement/paint raycasts); `BuiltTrack` gains `List<GameObject> splineRoots`. `Physics.SyncTransforms()` before BuildItems; item pose = drop-raycast against slab + ribbon colliders (collider-targeted `.Raycast`, take highest hit), oriented to the hit normal. `MakeGateTrigger` refactored to take a resolved pose (gate at `hit + normal·2`, surface-aligned — fine to ±45° bank). `ResolveSpawn` drops spawn/finish onto the surface the same way (raised-ribbon spawns work). StaticBatching stays scoped to the tile floorRoot (ribbons excluded).
- **`TrackEd/TrackBuilderBootstrap.cs`** — `EnsureSplines()` in Awake/SetDesign; `RebuildSpline(int)` targeted rebuild (destroys just that spline's root, re-runs RibbonMeshBuilder; bumps a separate `SplineVersion` so item highlights don't churn); during point drags, debounce ticks build preview-only (colliders:false), full cook on release.
- **`TrackEd/TrackBuilderUI.cs`** — the big edit:
  - Tabs become FLOOR / WALLS / OBST / MISC / **SPLINE** (3+2 rows). New `EditState.DrawingSpline` / `EditState.DraggingPoint`.
  - **SPLINE tab**: New Spline (defaults width 6/asphalt, `PushUndo`) → DrawingSpline: each map click appends a point (live redraw); Enter/double-click/Esc ends (<2 points = discard). Spline list with select/delete.
  - **Point handles**: sphere markers (`SplinePointMarker`) when a spline is selected, recreated on rebuild; click selects, drag moves in XZ — pointer ray projected onto the horizontal plane `y = point.y` (handles work on raised points); insert = click the ribbon run of the selected spline (nearest sample → segment); Del removes the selected point.
  - **Right-panel spline inspector**: closed / edge walls / stripes toggles; per-selected-point Width 2–12 m, Height 0–8 m, Bank −45…+45° sliders (coalesced undo); surface picker for the point + "apply to whole spline"; delete spline.
  - **Paint-on-ribbon**: FLOOR-tab strokes that hit a ribbon run set `surface[pointIdx] = brush` via `SplineRunMarker` mapping (drag walks samples like the tile Bresenham walk); `RequestRebuild` (runs can merge/split — no O(1) swap).
  - `RayToFloorPoint` generalizes to `RayToSurfacePoint(out point, out normal, out onRibbon)` (slab + every ribbon MeshCollider via `.Raycast`, nearest wins). Ghosts on ribbon: free placement, normal-aligned, commit stores `it.y`. Slab path (grid snap) unchanged. F frames the selected spline.
  - Spline selection highlight = MPB tint on run renderers keyed to `SplineVersion` (mirrors the existing `_seenRebuild` pattern).
- **`TrackEd/TrackIconFactory.cs`** (optional polish) — small generated S-curve texture for the SPLINE tab.
- **`README.md`** — spline feature docs.

## Compatibility

Old saves/snapshots: missing `splines` → empty list, missing `y` → 0; flat maps behave identically. Split-screen: ribbons are plain shared scene geometry. Oval/diff-drive scenes: SurfaceTag probe returns −1 → unchanged. Spline `surface` uses the same append-only floor ids (clamped).

## Risks

- **Wheel flicker at ribbon/slab seams** → skirt keeps the wheel on the ribbon while over it; 3 cm step at open ends is a small bump (optional end-taper in step 7).
- **Closed-loop roll continuity** → PTF closure correction + shared seam ring; test a banked closed loop specifically.
- **MeshCollider cook cost during drags** → preview-only meshes while dragging, cook on release; 1.5 m sampling keeps meshes tiny.
- **Same-frame factory raycasts** → `Physics.SyncTransforms()`; MeshCollider is queryable on sharedMesh assignment.
- **Mesh leaks on rebuild** → `RibbonMeshMarker.OnDestroy` destroys generated meshes.
- **Steep-bank gates** (>45°) — out of scope, code comment.

## Step breakdown (headless compile checkpoint after each)

1. **Data + math** — SplineSpec, SplineMath, TrackDesign (splines/EnsureSplines/Resize/PlacedItem.y). No behavior change.
2. **Mesh + factory + surface physics** — RibbonMeshBuilder (runs, UVs, skirt; walls/stripes stubbed), SurfaceTag + SurfaceMap probe, TrackFactory BuildSplines + item drop-raycast + surface-aligned gates/spawn. Verifiable by hand-adding a spline to a saved JSON and driving it.
3. **Editor: draw + drag (flat)** — SPLINE tab, DrawingSpline/DraggingPoint, point handles/markers, insert/delete, box-strip drag preview, RebuildSpline, undo. **End-to-end flat curve here.**
4. **Inspector + paint-on-ribbon** — height/bank/width/surface sliders, closed-loop toggle with roll closure, FLOOR-tab ribbon painting.
5. **Ribbon item placement UX** — RayToSurfacePoint, normal-aligned ghosts, free placement + `y` commit; gates on raised ribbon verified.
6. **Extras** — edge-wall extrusion + colliders, kerb stripe submesh + KerbMat, inspector toggles.
7. **Polish + compat** — mesh-leak component, Resize culling rules, pre-spline save load, split-screen on a spline track, optional tab icon, README; final compile + editor relaunch.

## Verification

User play-test script: 1) SPLINE tab → click out a curve → ribbon appears with chosen width; drag points and watch it redraw. 2) Close the loop, add rumble/ice stretches with the paint tool, set a corner's bank + height — drive it (banked elevated corner holds the car; ice stretch slides). 3) Place a speed bump, checkpoint gate, and cones ON the raised ribbon — they sit flush and aligned; move a control point — they re-drop onto the moved surface. 4) Enable edge walls + stripes. 5) Race it split-screen; save/resume a snapshot mid-lap on the ribbon. 6) Load a pre-spline map (e.g. GP Circuit) — identical; oval + diff-drive scenes unchanged.

---

# Iteration 10 — LAN multiplayer (Unity Netcode, host-authoritative)

## Context

Iteration 8 architected multiplayer around `SessionConfig`/`PlayerSlot`/`IDriverInputSource` with LAN explicitly deferred (menu buttons greyed "coming soon"). The user now wants it built. Decisions (confirmed): **up to 4 players** (host plays, listen server); **auto-discovery** on the LAN (UDP broadcast list on the Join page) + **manual IP** fallback (also enables internet play with port forwarding); **flow designed by the user** — joiners drop into **free roam** on the host's current map, the **host controls the session in-game** (change map → everyone reloads; **Start Race** → everyone teleports to a **grid behind the start line**, 3-2-1 countdown with frozen inputs, first-to-N-laps race, results for all, back to free roam). Host-authoritative: the host simulates ALL cars (WheelCollider physics + surface model); clients send inputs at 30 Hz and render interpolated ghost cars. No client prediction in v1 (LAN latency).

**Key architecture decision — NGO as transport + custom messaging ONLY (no NetworkObjects/prefabs).** The project is 100% runtime-generated (no prefab assets), and cars are arbitrary `VehicleDesign` compounds that a network prefab couldn't represent — design JSON must be transferred and rebuilt locally regardless. So: runtime-created `NetworkManager` + `UnityTransport`, `ConnectionApproval = true`, `EnableSceneManagement = false`, `PlayerPrefab = null`, and ALL sync via `CustomMessagingManager` named messages (JSON via JsonUtility for low-rate control messages; hand-packed `FastBufferWriter` binary for the 30 Hz input/state streams). Two hard gotchas the implementation must honor: **`transport.MaxPayloadSize` must be raised (256 KB) on BOTH ends** or fragmented track-JSON sends fail silently (default cap 6144 B); and named-message handlers die on `Shutdown()` — re-register per session.

## New files — `Assets/Scripts/Net/` (namespace `AIHWSim.Net`)

- **`NetSession.cs`** — DontDestroyOnLoad hub created by the menu: runtime NetworkManager+UnityTransport creation/config, `StartHost(port=7777)` / `StartClient(ip, port)` / `Leave()` (Shutdown + destroy + LoadMenu; static `LastDisconnectReason` for the menu). Roster `NetPlayer { clientId, slot, name, vehicleJson, sceneReady }` (host = slot 0); connection approval (reject >4 or protocol-version mismatch; approval payload = tiny `{ver, name}` JSON — vehicle JSON comes post-connect). Session state machine `LanState { FreeRoam, Countdown, Racing, Results }` + `TargetLaps` + `CountdownEndTime`; `InputsFrozen` during countdown. 30 Hz host state broadcaster (per car: pos/rot/vel/steer/wheel-speed + a `byte epoch` bumped on every teleport so clients flush interpolation buffers) and client receiver → `ClientCarView` registry. Shared standings model `LapState[4]` filled from host LapTimer events / client messages — the HUD reads only this on both roles. Host entry points: `HostChangeMap(TrackDesign)`, `HostStartRace(laps)`, `HostKick(clientId)`. Connect/disconnect callbacks mutate roster + rigs and rebroadcast.
- **`NetMessages.cs`** — catalog (all `aihw.*`): `hello` C→H (name+vehicleJson, ReliableFragmentedSequenced), `welcome` H→C (yourSlot, trackJson, state, laps, roster), `roster` H→all, `ready` C→H (after client scene build / map change), `map` H→all (trackJson; ""=oval), `race_start` H→all (laps, countdownSec, grid poses for instant client snap), `lap` H→all (slot lap/cp/last/best — also on checkpoint + respawn), `race_state` H→all (standings on change), `race_end` H→all (results rows); binary 30 Hz: `input` C→H UnreliableSequenced (throttle/steer/brake + flags byte: handbrake, respawn-edge ≈ 13 B), `state` H→all UnreliableSequenced (epoch, hostTime, n × {slot, pos, rot, vel, steerDeg, wheelRadPerSec} ≈ 200 B @ 4 cars).
- **`NetworkInputSource.cs`** — host-side `IDriverInputSource` per remote rig, fed by `input` messages; zeros when frozen or stale (>0.5 s dead-man brake); respawn edge-latch. Plus `GatedInputSource` wrapping any source with the countdown freeze (host's own input + client's local sampler → everyone freezes symmetrically).
- **`ClientInputSender.cs`** — client-only: samples a merged `PlayerInputSource` (gated) at 30 Hz, sends `input`, latches respawn between sends.
- **`ClientCarView.cs`** — per ghost car: interpolation ring buffer rendered at `estimatedHostTime − 0.12 s` (lerp/slerp; ≤100 ms velocity extrapolation when dry; smoothed host-clock offset); hard-snap + flush on epoch change; wheel-spin/steer visuals from streamed wheelRadPerSec/steerDeg via `GetWheelVisual(i)`.
- **`LanDiscovery.cs`** — plain `System.Net.Sockets` UDP (no Unity API off main thread): host broadcasts `{ver, gameName, port, players, maxPlayers, trackName}` to `255.255.255.255:47777` @1 Hz; Join page listens on a background thread (ReuseAddress; loopback works for same-machine testing) pushing into a `ConcurrentQueue` drained by the menu; entries expire after 4 s.
- **`LanSessionMenu.cs`** — replaces PauseMenu in LAN scenes (PauseMenu untouched, never created there — no timeScale pause/snapshots/telemetry/Tune in LAN). Esc-toggled, non-pausing, GarageSkin. Both roles: player list + Leave Session. Host extras: laps stepper + Start Race (disabled while racing or when the map has no finish line), Change Map (TrackLibrary list + Classic Oval), Kick. Also draws the countdown overlay and results overlay (host: Rematch / Free roam; clients: waiting-for-host + local dismiss).
- **`LanHud.cs`** — single-viewport HUD (SplitScreenHud pattern) for own name/speed/lap/CP/last/best from the shared standings model + race banner; same component both roles.
- **`NetRace.cs`** — host-only ~50-line race core over `LapTimer.LapCompleted` (entries fixed at race start — late joiners spectate; first-to-N; finish order; all-finished → `race_end`). `RaceDirector` is untouched and never instantiated in LAN (free roam keeps `TargetLaps = 0` so the bootstrap branch skips it naturally).
- **`Assets/Editor/BuildMenu.cs`** — **Tools ▸ AIHWSim ▸ Build Standalone (Dev)**: `BuildPipeline.BuildPlayer(EditorBuildSettings.scenes, "Builds/Dev/AIHWSim.exe", StandaloneWindows64, Development)` — the one-PC test path (editor as host + build as client via discovery or 127.0.0.1).

## Modified files

- **`Packages/manifest.json`** — add `"com.unity.netcode.gameobjects": "2.4.4"` (transport arrives as a dependency; fall back to the newest 2.x UPM resolves for 6000.1 — every API used exists since 2.0). Isolated as step 1 (first compile downloads packages — slow, needs network).
- **`Core/SessionConfig.cs`** — `SessionMode` gains `LanHost = 2, LanClient = 3`. `ResolvePlayers()` already honors explicit rosters — no change.
- **`Core/TrackBootstrap.cs`** — the big edit: Awake branches per mode. *LanHost*: rigs for every roster slot — local slot gets the camera (full-screen, no graphs/SensorHud); remote slots get full physics rigs, no camera, `carInput.source = NetSession.InputSourceFor(slot)`; host's own source gated; split-screen runner flags; `LanSessionMenu` + `LanHud` instead of PauseMenu; `_lapTimer.showDefaultHud = false`; no snapshot consume, no RaceDirector; rigs registered with NetSession. *LanClient*: build the track from received JSON (`interactive: true` — then **`Destroy()` the LapTimer and Checkpoint components** so kinematic ghosts can't fire host-only triggers; destruction, not disabling — physics callbacks fire on disabled behaviours); all roster cars as `previewKinematic: true` ghosts + `ClientCarView`; no CarInput/SimulationRunner anywhere; ChaseCamera on own ghost; `ClientInputSender` + `LanHud` + `LanSessionMenu`; send `ready`. Plus: `SpawnPose` → **4-slot grid** (2×2: ±2.2 m lateral, rows 5 m back; single-file when tileSize < 3); `HookLapRecords` filters `rig.slot.isLocal` (host records only its own laps; clients record theirs from `lap` broadcasts); runtime `AddLanPlayer/RemoveLanPlayer` (+ client `AddGhost/RemoveGhost`) for join/leave mid-scene; `TeleportToGrid()` for race start (RestoreState + SetSpawn + ResetTimer, epoch bump).
- **`Menu/MenuUI.cs`** — MP page: split-screen block stays; live **Host LAN Game** / **Join LAN Game** → new pages. Host page: name/vehicle/track pickers → `Mode=LanHost`, one local slot, NetSession StartHost + StartBroadcast → LoadTrack. Join page: name/vehicle + discovery list (`gameName · trackName · n/4`) + manual IP + Connect → StartClient; "Connecting…" with 10 s timeout; **scene load deferred until `welcome` arrives** (NetSession sets ActiveTrack + Mode + roster, then LoadTrack). Root shows `LastDisconnectReason` when returning.
- **Untouched by design**: PauseMenu, RaceDirector, SplitScreenHud, LapTimer, CarVehicle, SimulationRunner, GameFlow (LAN routes around them; 4 runners coexist exactly as split-screen's 2 do).

## Flows (concrete)

**Join**: approval (`{ver,name}`) → `hello` (vehicle JSON) → host assigns lowest free slot, `AddLanPlayer`, `welcome` to joiner + `roster` to rest → client loads scene, builds ghosts, `ready` → included in `state` stream. Late join during a race = free-roam spectator (not in NetRace's fixed entries; HUD shows the race banner). **Map change**: `map` broadcast → everyone (host included) sets ActiveTrack + reloads TrackScene (NetSession + roster persist); clients re-`ready`; epoch bump. **Race**: `TeleportToGrid` + state→Countdown (3 s, inputs frozen) + `race_start` (grid poses; clients snap + flush) → GO → NetRace over host LapTimer → `lap`/`race_state` → all finished → `race_end` + results → host dismiss → FreeRoam. **Leave/host-quit**: roster/rig removal + rebroadcast; clients on host-quit get `LastDisconnectReason` → menu.

## Step breakdown (headless compile after each)

1. **Package** — manifest edit + a `NetSmoke.cs` referencing `Unity.Netcode.NetworkManager`. Isolated (package download).
2. **Net plumbing** — NetMessages, NetSession (lifecycle/transport config incl. MaxPayloadSize/approval/handshake/roster/events/logging), SessionConfig enum.
3. **Menu + discovery + build tooling** — LanDiscovery, MenuUI LAN pages, BuildMenu. *Test: host in editor, join from dev build — connection + track transfer verified via logs. Windows Firewall will prompt (allow private networks) for editor AND build.*
4. **Scene composition + per-tick sync** — TrackBootstrap LanHost/LanClient + grid, NetworkInputSource/GatedInputSource, ClientInputSender, ClientCarView, state broadcast/receive, minimal LanSessionMenu (Leave). **Milestone: 4-player free-roam join-and-drive.**
5. **Lap sync + HUD** — client LapTimer/Checkpoint destruction, lap broadcasts, standings model, LanHud, local-only profile records.
6. **Session control** — full LanSessionMenu (change map / start race / kick), map-change flow, grid/countdown/NetRace/results/rematch, late-join spectator rule.
7. **Hardening + polish** — mid-race leaver = DNF, host-quit path, stale-input brake, ghost wheel visuals, discovery expiry/refresh, join timeout UX, README; final compile + editor relaunch.

## Risks

NGO version resolution on 6000.1 (fall back to newest 2.x; APIs are 2.0-era); first batchmode compile downloads packages (network required); `MaxPayloadSize` cap breaking fragmented sends (fixed by design, top verify item in step 3); handler re-registration after Shutdown; Windows Firewall prompts (user must Allow); teleport rubber-banding (epoch flush); client ghost trigger noise (components destroyed client-side; host has no ghosts); runtime rig addition mid-scene (SimulationRunner's Start-resolution pattern already tolerates it).

## Verification

1. Step 3: editor hosts, dev build joins via discovery (and via 127.0.0.1) — roster + track JSON arrive (log check).
2. Step 4: both machines drive in free roam on the host's custom (spline) map; client ghost motion is smooth; respawn (R) works from the client; disconnect/reconnect works.
3. Step 6: host changes map in-game → both reload onto it; host starts a 3-lap race → both snap to the grid, countdown freezes inputs, laps/CP count per player on both HUDs, results show on both; rematch; late joiner during a race free-roams as spectator.
4. Step 7: client quits mid-race (DNF in results); host quits (client returns to menu with message); profiles.json on each machine gains only that machine's laps.
5. Regression: SP, split-screen, garage, builder, snapshots, oval, diff-drive — all unchanged (LAN routes around them).

---

# Iteration 11 — RC-scale physics, aerodynamics + body customization, arcade assists

## Context

The sim's premise is "write firmware for a real small autonomous vehicle" — but the physics is tuned as a ~900 kg full-size car (bodySize 3.2 m, 48 V motors, 28 m/s). The user wants the physics scale to accurately represent **backpack-carriable RC/autonomous cars** (≤10 lb). Decisions (confirmed): reference vehicle = **1/10-scale RC / F1TENTH class** (~40 cm, ~1.6-1.8 kg, 2S 7.4 V, 540-class brushed motor, ~10 m/s); realism is the base model with **per-player arcade assist sliders in Options** (steering assist / stability / traction control / ABS — in LAN each player's prefs travel in `hello` and the host applies them to that player's car); aero parts = **all four** (adjustable rear wing, front splitter, side dams, canards) plus **new streamlined BodyShape presets** with per-shape drag coefficients; old-scale content: **regenerate built-ins (stock design + 6 themed maps), drop old user vehicle saves** (hide from lists, no converter).

Everything scales ÷4 linearly; speeds drop ~2.5×, so tracks get relatively faster — the intended F1TENTH-corridor feel. Diff-drive scene (SimBootstrap) untouched.

## Part 1 — RC physics reference constants (`Vehicles/CarVehicle.cs` unless noted)

| Constant | Old | New | Why |
|---|---|---|---|
| bodySize | (1.6, 0.6, 3.2) | **(0.20, 0.10, 0.42)** | 1/10 footprint |
| bodyMass | 900 | **1.6 kg** | +4×0.05 wheels ≈ 1.8 total |
| wheel radius (WheelSpec default) | 0.35 | **0.033** | 66 mm RC tire |
| `wc.mass` (line ~323) | 20 | **0.05** | body:wheels 8:1 (PhysX guideline) |
| suspensionDistance | 0.25 | **0.03** | ~30 mm travel |
| suspensionSpring | 35000 | **300 N/m** | corner mass 0.4 kg → sag 13 mm ≈ 0.44 travel (targetPosition 0.5 kept); fₙ ≈ 4.4 Hz |
| suspensionDamper | 4500 | **15** | ζ = 15/(2√(300·0.4)) ≈ 0.68 near-critical |
| maxBrakeTorque | 2500 | **0.8 N·m** | lock threshold ≈ 0.23 N·m |
| handbrakeTorque | 4000 | **1.2 N·m** | |
| antiRoll | 12000 | **8** | force = Δtravel(0..1)·antiRoll; ≈ axle weight |
| centerOfMass | (0,−0.4,0) | **(0,−0.03,0)** | battery low; wheels at y −0.045 |
| forceAppPointDistance | 0.15 | **0.02** | ⅔ radius above contact |
| steerRateDegPerSec | 220 | **480** | RC servo ~60°/0.1 s |
| steerAngle default | 28 | keep | |
| linearDamping | 0.05 | **0.02** | real drag now explicit (Part 3) |
| downforce field (40) | — | **deleted** — replaced by aero model + `aeroMult` tunable |
| friction curves (1.6/2.0 stiffness, slip points) | keep | slip ratios are dimensionless |
| bump buzz `Sin(s*16f)` (line ~462) | 16 | **120** | spatial period ≈ 0.05 m ≈ 1.6 wheel radii, same ratio |

**Motor** (`Vehicles/MotorModel.cs` `MotorParams.Default`): maxVoltage **7.4**, kt **0.003** (Kv≈3180 rpm/V), R **0.09 Ω**, gear **8**, I0 **1.2 A**, visc **1e-6**, eff **0.85**, **NEW field `maxCurrent = 40 A`** (ESC current limit; clamp `I` to ±min(V/R, maxCurrent) in `WheelTorque` when > 0; old JSON deserializes 0 = unlimited → back-compat). Sanity: no-load 23,200 rpm ✓, top ≈ 10 m/s ✓, stall 82 A → ESC-clamped wheel τ 0.82 N·m → traction-limited launch with realistic wheelspin ✓. Mirror clamp in `VehicleStats.Compute`; `VehicleStats.WheelColliderMass` 20 → **0.05**.

**Critical WheelCollider small-scale traps:**
1. **`wc.wheelDampingRate` — hard blocker.** Unity default 0.25 → 75 N·m damping at ω=300 rad/s vs a 0.8 N·m motor: the car would never move. Set **0.0002** in `MakeWheel` (≈0.06 N·m at top speed — plays rolling-loss role).
2. **Radius floor clamps** `Max(0.05f, …)` eat a 33 mm wheel — CarVehicle.cs:320, PartVisualFactory:73, VehicleStats:48 → all **0.01f**.
3. **New `Core/PhysicsTuning.cs`** — `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`: `Physics.defaultContactOffset = 0.002` (default 0.01 = 30% of wheel radius → phantom contacts), `defaultSolverIterations = 10`, `defaultSolverVelocityIterations = 2`, `defaultMaxDepenetrationVelocity = 2`. Global (also affects diff-drive scene — harmless, it's force-based with no WheelColliders; note in code comment).
4. `_body.maxAngularVelocity = 40` (default 7 rad/s caps body yaw in spins; wheel spin is internal to WheelCollider, unaffected) + `maxDepenetrationVelocity = 2` in Awake.
5. `TrackBootstrap.physicsRateHz` 200 → **400**. SimBootstrap stays 500 (untouched).
6. Residual risk: low-speed shimmy/creep — if seen in play-test: rate 500, wheelDampingRate 0.0005, or tiny idle brake < 0.05 m/s.

## Part 2 — World rescale (÷4)

- **`TrackEd/TrackDesign.cs`** — tileSize default 4 → **1**. Tile clamps [4,60] keep.
- **`TrackEd/SplineSpec.cs`** — DefaultWidth 6 → **1.5**. `SplineMath` sample step ~1.5 m → **0.4 m**.
- **`TrackEd/RibbonMeshBuilder.cs`** — TopOffset 0.03 → **0.008** (0.03 = full wheel radius step!), Skirt 0.15 → **0.04**, StripeWidth 0.35 → **0.09**, WallHeight 0.35 → **0.09**.
- **`TrackEd/TrackCatalog.cs`** — all item dims ÷4: wall_small 0.2³; tire stack (0.21,0.035,0.21) stacked 0.07; wall_tall 1.0×0.5×0.1; fence 1 m span; ramp Ramp(…,1f,0.75f,0.04f,16°); speed bump ~0.042 proud; platform 1×0.125×1; cone 0.18/0.07; barrier 0.6×0.25×0.125; finish strip 1.5 wide posts 0.375; checkpoint bar 1.35 @y0.6; light pole 0.45, lamp range 5; spawn pad 0.6². Floors: grass rollingResist 12 → **0.005**, sand 45 → **0.018**, mud 140 → **0.045** (N·m per wheel: target decel × 1.8 kg × r/4); boostAccel 9 keep (mass-scaled AddForce, unit-correct); rumble bumpAmp keep (fraction of weight).
- **`TrackEd/TrackFactory.cs`** — gate trigger up*2 → **up*0.5**, box heights 4 → 1; gate widths 6.4/5.4 → **1.6/1.35**; spawn y 0.7 → **0.08**, behind-finish 6 → **1.5**; dynamic item rb.mass 6 → **0.08**.
- **`Core/TrackBootstrap.cs`** — physicsRateHz 400; SpawnPose columns ±2.2 → **±0.55**, rows 5 → **1.25**, single-file 4.5 → **1.2**, narrow threshold tileSize<3 → **<0.75** (TeleportToGrid inherits); chase offsets (0,5,−9) → **(0,1.1,−2.2)** (3 sites incl. LAN); classic oval: radii 45/28 → **12/7.5**, roadWidth 9 → **2.5**, berms/ramps/hump/cones/blocks/barriers/finish ÷4, spawn −dir·1.5+up·0.08, trigger up*0.5.
- **`Core/ChaseCamera.cs`** — offset (0,3,−4) → **(0,0.7,−1.5)**, lookAtHeight 0.5 → **0.12**.
- **`Core/CarInput.cs`** — maxSpeed 25 → **12**. **`Track/LapTimer.cs`** — minLapTime 5 → **3**.
- **HUD units → m/s** (robotics convention): SplitScreenHud, LanHud, VehicleStats top-speed readout `{x:0.0} m/s`. Telemetry channels unchanged.
- **Garage**: `GarageBootstrap` floor y −0.35 → **−0.078**, markers 0.16/0.14 → **0.05/0.045** (+SetHighlight baseSize), dir line ÷4; `OrbitCamera` distance 8 → **1.3**, min 3.5 → **0.35**, max 22 → **6**, zoomSpeed ÷4; GarageUI focus distance 4 → **1.0**, placement normal offsets 0.05/0.06 → **0.012/0.008**; `SymmetryUtil.CenterDeadzone` 0.05 → **0.01**; `PartIconFactory` wheel sample radius 0.3 → 0.033 (auto-framed, robust).
- **GarageUI slider ranges**: Body W **0.12–0.35**, H **0.04–0.18**, L **0.25–0.60**, Mass **0.8–5 kg**, Servo **60–1200**; wheel X **±0.20**, Y **−0.09..0.03**, Z **±0.32**, radius **0.02–0.07**; sensor pos ±0.18/−0.05..0.25/±0.30, ToF range **0.2–8** (VL53L1X-class); motor Vmax **3.7–12**, Kt **0.001–0.02**, R **0.02–1**, visc **0–0.0005**, new **Imax 0–100 (0=∞)**; datasheet stall τ **0.02–1.5**, noLoad rpm **5000–40000**.
- **`Vehicles/PartVisualFactory.cs`** — wheel tread halfWidth 0.12 → **0.4·radius** (proportional); camera housing (0.024,0.018,0.015); ToF PCB (0.02,0.005,0.014); encoder disc 0.02; fixed offsets ÷4–6.
- **`Garage/VehicleDesign.cs` defaults** — WheelSpec localPos (0.083,−0.045,0.152) r 0.033; SensorSpec localPos (0,0.05,0.18) range **4**; bodySize/mass/steerRate per Part 1. CarVehicle legacy fallbacks (wheelBase 0.30, trackWidth 0.166, radius 0.033, yOffset −0.045); check `CarWheelConfig` default radius. `Sensors/TofSensor.maxRange` 20 → 4.
- **`Menu/MenuBootstrap.cs`** — showcar camera ~(0.5,0.28,−0.65), floor ÷4.
- **Step-6 sweep**: TrackBuilderUI/Bootstrap (top-down fit distance, point-handle spheres, drag-preview strip 0.35 wide, F-focus), `Net/ClientCarView` any snap-distance threshold, GarageBootstrap aim-line lengths.

## Part 3 — Aerodynamics

**Body drag/lift — new `Vehicles/AeroDynamics.cs`** (static): ρ=1.225; frontal area A = bodySize.x·y·0.9; per-BodyShape Cd table **Box 0.9, Wedge 0.65, Buggy 0.8, Shell 0.45, LowRacer 0.55** + base ClA (downforce area) **0 / 0.002 / 0 / 0.004 / 0.006**. In StepPhysics: q = ½ρv²; drag `AddForceAtPosition(−v̂·q·Cd·A, geometric center)` (center sits ~0.03 above CoM → free mild pitch/yaw realism; ~0.6 N @10 m/s, no instability risk); base downforce −up·q·ClA·aeroMult at CoM. Old downforce field + line deleted; Tune menu entry becomes **`Aero mult` 0–3 (default 1)**.

**Aero parts** — `Garage/VehicleDesign.cs`:
```csharp
public enum AeroKind { Wing, Splitter, SideDam, Canard }   // in AeroDynamics.cs
[Serializable] public class AeroSpec { name; kind; Vector3 localPos; yawDeg;
    mirrorGroup = -1; angleDeg = 8f /*Wing,Canard*/; sizeScale = 1f; Clone(); }
public List<AeroSpec> aero = new();   // old JSON → empty ✓
```
Coefficients (at sizeScale 1; force ∝ sizeScale²): **Wing** ClA = 0.0008·angle (clamp 15° → 0.012 m²; 0.74 N @10 m/s ≈ 4% weight), Cd0A 0.0003, angle slider 0–20°; **Splitter** ClA 0.004 / Cd0A 0.0004; **SideDam** ClA 0.0015 / 0.0002; **Canard** ClA 0.0002·angle / 0.0002. `CarVehicle.aeroParts` (flat runtime structs, set by VehicleFactory like wheels); per part per StepPhysics: `AddForceAtPosition(−up·down − v̂·drag, TransformPoint(localPos))` — a rear wing plants the rear axle specifically.

**Garage integration**: `PartType` (Garage/PartMarker.cs) gains **Aero** — extend selection/markers/SetPartVisible/SetHighlight in GarageBootstrap+GarageUI (marker color purple). Palette += `wing / splitter / sidedam / canard`; StartPlacing maps to pending AeroSpec; placement raycast = sensor path (normal offset 0.008, scroll yaw). `PartVisualFactory`: BuildWingViz (plate 0.20×0.006×0.05 pitched −angle + endplates + struts), BuildSplitterViz (lip 0.16×0.005×0.04), BuildSideDamViz (fin), BuildCanardViz; `PartGhost.ForAero`; PartIconFactory keys (auto-framed). Inspector `DrawAeroInspector`: pos/yaw, Angle° 0–20 (Wing/Canard), Size ×0.6–1.6, mirror row, delete. `SymmetryUtil`: FindTwin/MirrorInto/SyncTwin(AeroSpec) (negate x + yaw), include `d.aero` in NextGroupId. **New BodyShapes**: append `Shell, LowRacer` to enum (append-only int persist ✓); `BuildShellBody` (touring lexan look), `BuildLowRacerBody` (F1TENTH flat deck + nose wedge); BODY tab auto-enumerates.

**Stats panel** (`VehicleStats`): drag-aware top speed — bisect v where `Σ WheelTorque(Vmax, v/r)/r == q·(bodyCdA + Σ partCdA)`; report top speed, downforce @ top, drag @ top.

## Part 4 — Assist sliders (per-player, Options)

`[Serializable] struct AssistSettings { float steer, stability, traction, abs; }` (0–1 each, default 0 = pure realism). `CarVehicle.assists` + `assistsActive`, applied in StepPhysics:
1. **Steering assist** — speed-sensitive limit `steerCmd *= Lerp(1, Min(1, 4/max(0.5,v)), a.steer)` + countersteer `+= a.steer·Clamp(−Atan2(vLocal.x,|vLocal.z|)·0.5, ±0.35)` when |vz|>1.
2. **Stability (ESC)** — `yawIntent = v/wheelbase·tan(steer)`; `AddTorque(−up·(yawRate−yawIntent)·0.08·a.stability)` clamped ±0.3 N·m.
3. **Traction control** — per powered wheel: forwardSlip > 0.25 → `motorTorque *= 1 − a.traction·Clamp01((slip−0.25)/0.35)`.
4. **ABS** — braking with forwardSlip < −0.3 → brake `*= 1 − a.abs·Clamp01((−slip−0.3)/0.4)`.

**Autonomous mode: assists forced OFF** (firmware faces raw physics) — `SimulationRunner` sets `assistsActive = (mode == Manual)`.

**Plumbing**: `GameSettings` += 8 flat floats `p1Assist*/p2Assist*` (old settings.json → 0 ✓); `MenuUI.DrawOptions` ASSISTS P1/P2 percent sliders; `PlayerSlot.assists` filled in MakeSlot / ResolvePlayers (p1) / snapshot resume (re-read settings — assists aren't saved state); `TrackBootstrap.BuildPlayerRig` applies to car. **LAN**: `HelloMsg` += 4 floats (client's p1 settings); `NetPlayer` carries them; `BuildLanRig` applies host-side; host slot 0 from local settings; **`NetSession.ProtocolVersion` 1 → 2** (physics changed wholesale — old builds must not mix). Split-screen: P1/P2 each their own set.

## Part 5 — Content regen

- **Stock design** `VehicleDesign.Default()`: "Stock RC", **LowRacer**, (0.20,0.09,0.42), 1.6 kg, steerRate 480; fronts steer @(±0.083,−0.045,0.152), rears powered (RWD) @(±0.083,−0.045,−0.152) r 0.033; cam_front (0,0.09,0.05) pitch 8°; tof_front (0,0.03,0.21) range 4, 3 rays; tof_left/right (±0.06,0.03,0.19) yaw ∓32° range 4; 4 encoders.
- **6 themed maps** (`UnitySim/Tracks/*.json`): mechanical transform via one-off script — tileSize 4→1, item x/y/z ÷4, spline points & widths ÷4 (floor arrays/yaw/order scale-free); load each in builder to verify.
- **Old vehicle saves — hide**: `VehicleLibrary.List()` skips designs with parsed `mass > 50` (one Debug.Log). Save fresh "Stock RC.json". Old snapshots: acceptable to break (Saves/ has only settings.json today).

## Step breakdown (headless compile checkpoint after each)

1. **RC physics core** — MotorParams.maxCurrent + MotorModel clamp + new defaults; CarVehicle constant set + wheelDampingRate + radius clamps + maxAngularVelocity; `Core/PhysicsTuning.cs`; VehicleStats; VehicleDesign/WheelSpec/SensorSpec defaults + TofSensor; TrackBootstrap rate 400; CarInput 12.
2. **World rescale** — TrackDesign/SplineSpec/SplineMath/RibbonMeshBuilder/TrackCatalog/TrackFactory/TrackBootstrap(oval+grid+cameras)/ChaseCamera/LapTimer/bump wavelength/garage scene+sliders+offsets/PartVisualFactory/SymmetryUtil/PartIconFactory/MenuBootstrap/HUD m/s. → **Play-test 1: SP stock RC on rescaled oval + garage editing.**
3. **Aero core** — AeroDynamics.cs, CarVehicle drag/lift (delete old downforce, add aeroMult tunable), AeroSpec + VehicleFactory + drag-aware VehicleStats.
4. **Aero garage UX + bodies** — PartType.Aero end-to-end (palette/ghost/icons/markers/inspector/mirror), Shell/LowRacer + Cd table. → **Play-test 2: wing angle ↑ → top speed ↓, corner grip @ speed ↑.**
5. **Assists** — struct + StepPhysics logic, GameSettings + Options page, PlayerSlot plumbing, autonomous gating, HelloMsg/NetPlayer/BuildLanRig + ProtocolVersion 2. → **Play-test 3: split-screen P1 assists 0 / P2 assists 1.**
6. **Content + hardening** — regen 6 maps + stock JSON, VehicleLibrary filter, TrackBuilder/ClientCarView constant sweep, README, regression pass (SP / split / LAN / garage / builder / autonomous DLL / diff-drive zero-diff / telemetry). Final compile + editor relaunch.

## Risks

- **WheelCollider at 33 mm radius**: wheelDampingRate default is the known killer (fixed by design); contact offset + solver iterations set globally; residual shimmy risk has staged mitigations (§1 item 6).
- Suspension spring/damper/mass move as an ensemble — never retune one alone.
- Old JSON compat: maxCurrent=0 sentinel, aero list defaults empty, assist floats default 0 — all JsonUtility-safe.
- LAN mixing old/new builds → ProtocolVersion bump rejects at approval.
- Themed-map transform is mechanical but must be verified visually in the builder (ramp/bump proportions).

## Verification (user play-test script)

1. Garage: stock RC shows a believable 40 cm car, orbit/zoom framed right, sliders span sensible RC ranges; stats read ~10 m/s top, realistic stall torque.
2. Drive oval: launch has brief wheelspin then pulls to ~10 m/s; brakes lock decisively; suspension visibly works over the speed bump; respawn/laps fine at 400 Hz.
3. Aero: place rear wing at 15° → stats top speed drops ~1 m/s, high-speed corners noticeably plant the rear; splitter counters understeer; Shell vs Box body → measurable top-speed difference.
4. Assists: with sliders 0 the car spins when provoked; stability 1 catches it; TC kills launch wheelspin; ABS prevents lockup; M-toggle to Autonomous ignores assists.
5. Multiplayer: split-screen with different assist settings per player; LAN host+client race on a regenerated themed map (old build refused with version mismatch).
6. Regression: diff-drive scene unchanged; C controller drives the RC car via existing voltage ABI (7.4 V commands); telemetry CSV sane; old vehicle JSONs hidden from pickers.

---

# Post-I11 fixes round 2 — keyboard steer feel + real prop physics/visuals

## Context

Two play-test reports after the RC rescale:

1. **"Driving feels twitchy on keyboard."** The physics IS correctly at RC scale — the problem is input, not scale. Keyboard steering is a raw digital step: `InputReader.Steer()` / `PlayerInputSource` Keyboard-kind return an instant ±1, and the 480°/s servo reaches full 28° lock in ~60 ms. On a 0.30 m wheelbase at 10 m/s, tan(28°)·v/L ≈ 17 rad/s of commanded yaw — undrivable with binary keys. Real RC transmitters are proportional sticks with expo; the fix is **transmitter-style shaping of the keyboard axis only** (ramped rise, faster return-to-center), leaving gamepad analog raw and Autonomous/firmware untouched. Slowing the servo (`steerRateDegPerSec`) would be wrong — it would dull gamepad + assists too.

2. **"Cones and tire stacks look wrong and float/drift forever after being hit."** Root cause found: `TrackBuilder.Cylinder` uses Unity's Cylinder primitive, which carries a **CapsuleCollider** — and a capsule scaled to a squashed tire (0.21, 0.035, 0.21) or cone degenerates into a **sphere** (height < 2·radius). Every "tire" and "cone" is physically a marble. On top of that, `TrackFactory.BuildItems` dynamic branch (TrackFactory.cs:199-208) sets only mass 0.08 + interpolation: no linear/angular damping, no PhysicsMaterial on the colliders. A frictionless-rolling sphere with zero damping rolls forever — exactly the reported float/drift. Fix = real procedural meshes (torus tires, actual cone) with **convex MeshColliders**, plus damping + friction material on the dynamic bodies.

## Part 1 — Keyboard steer shaping (twitchiness)

**New `Core/SteerSmoother.cs`** (plain class, no MonoBehaviour):
```csharp
public sealed class SteerSmoother {
    // Rise ~0.18 s to full lock, release ~0.07 s back to center,
    // direction reversal passes through zero at release rate then rises.
    public float Step(float target, float now);   // integrates using (now - _lastTime), clamped dt ≤ 0.1
}
```
Rates as public consts (`RiseRate ≈ 5.5f`, `ReleaseRate ≈ 14f`). Uses `Time.time` deltas so it works both from per-frame and per-control-step callers; a 0 delta (same-tick double call) is a no-op — no double integration.

**Modify `Core/InputReader.cs`** — split `Steer()` internals:
- `SteerAnalog()` → gamepad stick only (raw).
- `SteerDigitalRaw()` → keyboard keys ±1 (+ legacy `SafeAxis("Horizontal")`, which Unity already smooths).
- `Steer()` becomes `MaxMag(SteerAnalog(), _kbSmoother.Step(SteerDigitalRaw(), Time.time))` with one static `SteerSmoother` (static state is fine — there is exactly one physical keyboard). Existing callers unchanged.

**Modify `Core/PlayerInputSource.cs`** — Keyboard-kind `Steer()` runs its keyboard read through a per-instance `SteerSmoother`; Gamepad-kind stays raw; Merged already inherits the smoothed static path. Covers SP, split-screen keyboard player, and the LAN client sender (which samples a PlayerInputSource).

**Options knob** — `Persistence/GameSettings.cs`: add `public float kbSteerSmoothing = 1f;` (0 = off/instant, 1 = full shaping; field-initializer default survives old JSON — JsonUtility keeps ctor defaults for missing fields). `MenuUI.DrawOptions` gains one "KB steer smoothing" slider (global, next to the assists block). SteerSmoother scales its rise time by the setting (0 → passthrough).

Notes: mouse steering and Autonomous mode are untouched (firmware reads raw physics; smoothing lives purely in the human keyboard path). The servo (`steerRateDegPerSec = 480`) and garage Servo slider stay as-is — they model the real servo and are already user-tunable.

## Part 2 — Prop visuals + honest physics (cones, tire stacks)

**New mesh generators in `Track/TrackBuilder.cs`** (runtime-generated, cached by parameter key like the texture helpers):
- `TorusMesh(float majorR, float minorR, int segMajor = 20, int segMinor = 10)` — a real tire shape.
- `ConeMesh(float height, float baseRadius, int segments = 20)` — closed cone (apex slightly rounded not needed; flat disc base).
- `MeshObj(string name, Mesh mesh, Material mat, Transform parent, Vector3 pos, Quaternion rot, bool convexCollider)` — MeshFilter+Renderer, optional convex MeshCollider sharing the same mesh.

**`TrackBuilder.Cone(...)` rebuilt** (shared by catalog + classic oval slalom cones at TrackBootstrap.cs:643): square base slab (Box, collider-less) + orange `ConeMesh` body + a thin white band (short squashed collider-less cylinder ring at ~55% height) → looks like a real traffic cone. One convex MeshCollider on the cone body is the only collider (its hull covers the base closely enough). Oval cones stay static — no behavior change there beyond looks.

**`TrackCatalog` tire_stack rebuilt**: each `Tire{i}` = `TorusMesh(0.105 - minor, 0.035)` lying flat via `MeshObj(..., convexCollider: true)` + a collider-less darker inner-wall visual (or nothing — the torus hole reads as a tire by itself). Convex hull of a torus ≈ rounded disc: knocked tires roll on edge briefly, wobble over, and settle flat — the real behavior. Keep 3 stacked, keep `dynamic = true`, keep one body per tire (stacks knock apart, which the user likes).

**`TrackFactory.BuildItems` dynamic branch hardened** (TrackFactory.cs:199-208):
- New shared `_propPhys` PhysicsMaterial (dynamicFriction 0.7, staticFriction 0.8, bounciness 0.15, frictionCombine Average) assigned to every collider under a dynamic item.
- Rigidbody: `linearDamping = 0.3f`, `angularDamping = 1.5f` (rolling/tumbling bleeds off), keep mass via new `ItemDef.dynamicMass` (default 0.08; cone 0.03 so it flies satisfyingly), `rb.centerOfMass` lowered for the cone (bottom-heavy like a real weighted cone — it wobbles and tends to settle rather than launch).
- Rigidbody placement rule unchanged in effect: cone is now a single collidered child, tires are 3 collidered children — the existing "one rb per collidered child" loop still does the right thing.

Ghost/icon safety: `TrackGhost` strips ALL colliders generically (TrackGhost.cs:28) and `TrackIconFactory` snapshots visuals only — MeshColliders need no special handling.

## Files

Modified: `Core/InputReader.cs`, `Core/PlayerInputSource.cs`, `Persistence/GameSettings.cs`, `Menu/MenuUI.cs`, `Track/TrackBuilder.cs`, `TrackEd/TrackCatalog.cs`, `TrackEd/TrackFactory.cs`, `README.md`.
New: `Core/SteerSmoother.cs`.
Untouched by design: `CarVehicle` (servo + assists already correct), autonomous/ABI path, `TrackGhost`/`TrackIconFactory`, oval physics.

## Verification

Headless batch compile (0 `error CS`, wait for Unity exit), relaunch editor. Play-test script:
1. Keyboard drive at speed: tapping A/D gives progressive lock, holding reaches full lock in ~0.2 s; slaloms are controllable; gamepad stick feel unchanged; Options slider at 0 restores the old instant response.
2. Autonomous (M): C controller steering unaffected (raw command path).
3. Builder + drive: cones look like traffic cones (base, band), tire stacks look like stacked tires with holes.
4. Hit a tire stack: tires scatter, tumble, roll a short arc, wobble flat and STOP; they sleep (no perpetual drift). Hit a cone: it tips/flies, bounces, settles — doesn't glide away.
5. Untouched props sit stably at load (no self-rolling); classic oval slalom cones render the new look, still static.
6. Regression: split-screen keyboard player smoothed, gamepad player raw; LAN client keyboard input smoothed before sending.

---

# Iteration 12 — Per-wheel suspension, suspension sensor, preset vehicles + themed maps

## Context

User request: (1) adjustable suspension per wheel — damping ratio and a suspension **angle** that physically tilts the WheelCollider mount (inclined travel + camber-like lean); (2) a way to **measure spring force / compression / angle per wheel** — confirmed as a new placeable garage **sensor part** over the existing sensor ABI (firmware-readable, graphed, CSV'd); (3) **preset vehicles as code built-ins** (stable buggy/rover, F1-style aero racer, other RC types) selectable in menu + garage; (4) **themed built-in maps** matched to each preset. Confirmed choices: tilted mounts + ratio-based tuning; sensor part; code built-ins; one themed map per preset.

Today suspension is three vehicle-wide constants in `CarVehicle` (`suspensionDistance=0.03`, `suspensionSpring=300`, `suspensionDamper=15`, L83-86) applied identically in `MakeWheel` (L395-439); wheel holder rotation is yaw-only (L400). No per-wheel grip, no suspension telemetry, one built-in vehicle (`VehicleDesign.Default()`) and builder-authored track JSONs.

## Step 1 — Per-wheel suspension data model

**`Garage/VehicleDesign.cs`** — append to `WheelSpec` (field initializers = old-JSON back-compat, matching today's behavior exactly):
```csharp
public float suspStiffness = 300f;    // N/m
public float suspDampingRatio = 0f;   // 0 = legacy sentinel: raw damper 15
public float suspTravel = 0.03f;      // m
public float suspAngleDeg = 0f;       // strut tilt about wheel-local Z; + = top leans inboard (side-relative)
public float gripMult = 1f;           // friction stiffness scalar (fwd+side); <=0 treated as 1
```
**`Vehicles/CarWheelConfig.cs`** — mirror the five fields.
**`Garage/SymmetryUtil.cs`** `MirrorInto(WheelSpec)` (L52-64) — plain-copy all five (angle is side-relative, so copy IS the mirror).

## Step 2 — CarVehicle applies per-wheel suspension + grip

**`Vehicles/CarVehicle.cs`**:
- `MakeWheel` (L395): holder rotation L400 → `Euler(0, cfg.yaw, tiltZ)` with `tiltZ = (cfg.localPos.x >= 0 ? -1f : 1f) * cfg.suspAngleDeg` (clamped ±30). L404 → `wc.suspensionDistance = Max(0.005f, cfg.suspTravel)`. Spring block L412-416: `spring.spring = cfg.suspStiffness`; damper = `cfg.suspDampingRatio > 0 ? cfg.suspDampingRatio * 2f * Sqrt(cfg.suspStiffness * cornerMass) : suspensionDamper` where `cornerMass = _body.mass / wheelCount` is computed once in `BuildWheels` (L210-226) and passed in (body mass is known there).
- **gripMult at all three friction write sites**: initial curves L422/L427 (`fwd.stiffness = FwdStiffness * g`, `side.stiffness = _gripStiffness * g`), per-surface reassign L573-581 (multiply both by `w.cfg` grip), and `SetGrip` L709-716. Store the sanitized value (`<=0 → 1`) on the `Wheel` runtime class (L91-98) at build time. Surface-change cache `lastMult` logic unchanged.
- `ApplyAntiRoll` (L655-668): divisor `suspensionDistance` → `col.suspensionDistance` (per-wheel now). The local-space hit-point formula stays valid under tilt (travel is along the collider transform's up by definition).
- New accessors for the sensor: `public float GetSuspensionCompression(int i)` (ApplyAntiRoll formula, 0 when airborne) and `public float GetSuspensionAngle(int i)` (cfg value).
- Keep the three vehicle-wide fields as legacy fallbacks; don't delete.

**`Garage/VehicleFactory.cs`** `Build` L57-72: copy the five new fields into `CarWheelConfig`.

## Step 3 — Garage inspector + stats

**`Garage/GarageUI.cs`** `DrawWheelInspector` (~L683), existing `Slider` helper (L971): **Stiffness** 50–2000 N/m, **Damping ζ** 0.1–2.0 (if stored 0, display/write 0.65 on first touch), **Travel** 0.01–0.08 m, **Susp angle°** −30…+30, **Grip ×** 0.3–2.0.
**`Garage/VehicleStats.cs`**: add `rideFreqHz = Sqrt(kAvg/mCorner)/(2π)` and `sagPct = mCorner·9.81/(kAvg·travelAvg)·100` to `StatsResult` (L7-17) + `Compute` (L31); show in `DrawStatsPanel` (GarageUI L837), flag sag > 80 % ("bottoms out").

## Step 4 — Suspension sensor (ABI append)

- **`Controllers/hal/controller_api.h`** (L34-40): append `SENSOR_SUSPENSION = 6`; layout comment `[force_N, compression_01, angle_deg]`. ABI version stays 3 (append-only enum, structs unchanged — same rule as prior appends).
- **`Bridge/ControllerInterop.cs`** (L10-17): mirror `Suspension = 6`.
- **New `Sensors/SuspensionSensor.cs`** (clone of `WheelEncoderSensor` shape): `wheelIndex`, `Type=Suspension`, `DataCount=3`, `FieldNames={"force","comp","angle"}`, `Bind` via `vehicle.GetWheel(wheelIndex)`; `Sample`: `GetGroundHit(out hit)` → `dest[0]=grounded?hit.force:0`, `dest[1]=car.GetSuspensionCompression(i)`, `dest[2]=car.GetSuspensionAngle(i)`. SensorRig gives `sens/<name>/{force,comp,angle}` channels (graphs + CSV) for free.
- Garage part chain: `VehicleFactory.CreateSensor` (L139) explicit case **before** the ToF default; `GarageUI` Palette entry (L52) + `StartPlacing` sensor branch (L233) + inspector case (L709, wheel-index picker copied from encoder); `PartVisualFactory.BuildSuspensionViz` (small coil-over: cylinder + 2-3 stacked torus-ish discs, encoder-viz complexity, template L149); `PartIconFactory` key (L22).

## Step 5 — Vehicle presets (code built-ins)

**New `Garage/VehiclePresets.cs`**: `static (string name, Func<VehicleDesign> build)[] All` —
- **Stock RC** = `VehicleDesign.Default()`.
- **Rally Buggy**: Buggy body, k≈180, ζ 0.55, travel 0.06, slight nose-up rake, 4WD moderate motor, +ToF/camera loadout, suspension sensor on FL.
- **F1 Racer**: LowRacer, k≈900, ζ 0.9, travel 0.012, front+rear Wing `AeroSpec`s, hot motor (higher Vmax/lower gear), RWD, gripMult 1.2.
- **Crawler**: Box body, k≈150, travel 0.08 (max), gripMult 1.6 all wheels, 4WD high-reduction slow motor, suspension sensors on all 4.
- **Drift Car**: Shell, stiff (k 600/ζ 0.8), rear gripMult 0.55 / front 1.0, RWD.

**`Menu/MenuUI.cs`** `RefreshLists` (L62) + `StartSinglePlayer` (L139): vehicle list = `[""] + presets ("★ "-prefixed) + VehicleLibrary.List()`; resolve ★ names via `VehiclePresets`. **`GarageUI`** Load list: same prepend; loading a preset sets the design name to the preset name so Save writes a user copy under `Vehicles/` (presets themselves are never files — always available, effectively read-only).

## Step 6 — Track presets (code built-ins)

**New `TrackEd/TrackPresets.cs`**: same `(name, Func<TrackDesign>)` pattern, built from existing catalog only (RC scale: tileSize 1 m):
- **Whoop Canyon** (buggy): ~40×30 dirt; closed spline lap with alternating point heights 0–0.4 m (whoops), 2 ramp jumps + platform landings, sand run-offs, tire-stack corners, finish + 3 CPs, spawn.
- **Monza Mini** (F1): ~50×35 grass; wide (3 m) smooth asphalt closed spline, ≤8° banking on two sweepers, cone chicane, barriers, rumble apex strips.
- **Boulder Basin** (crawler): ~30×30 dirt/mud patchwork; no spline — platform stacks 0.05–0.25 m, speed-bump ridges, ramp ascents to a summit finish, wall_small squeeze gates.
- **Slide Yard** (drift): ~35×35 ice+asphalt patchwork; wide open horseshoe spline, cone clip points, tire-stack outer walls.

**`Menu/MenuUI.cs`** track picker + **`TrackEd/TrackBuilderUI.cs`** Load list: prepend ★ presets ("" stays Classic Oval); loading one in the builder yields an editable clone, Save writes a user copy.

## Files

New: `Sensors/SuspensionSensor.cs`, `Garage/VehiclePresets.cs`, `TrackEd/TrackPresets.cs`.
Modified: `Garage/VehicleDesign.cs`, `Vehicles/CarWheelConfig.cs`, `Garage/SymmetryUtil.cs`, `Vehicles/CarVehicle.cs`, `Garage/VehicleFactory.cs`, `Garage/GarageUI.cs`, `Garage/VehicleStats.cs`, `Vehicles/PartVisualFactory.cs`, `Garage/PartIconFactory.cs`, `Bridge/ControllerInterop.cs`, `Controllers/hal/controller_api.h`, `Menu/MenuUI.cs`, `TrackEd/TrackBuilderUI.cs`, `README.md`.

## Risks

- **Tilted WheelCollider mount**: raycast runs along the tilted down-axis — >30° meaningfully shortens vertical travel (cos θ) and shifts the contact patch; clamp ±30° and note steer rotates about the tilted axis (mild caster effect — acceptable/realistic).
- **ζ→damper needs corner mass** — compute in `BuildWheels`, never per-wheel in isolation.
- **gripMult must ride every `stiffness` write** (3 sites) or surface changes silently revert it.
- **Enum append**: explicit `case Suspension` in `CreateSensor` before the ToF `default:`; enum ids append-only in header + interop identically.
- Old vehicle JSON: all new fields default to today's exact behavior; `gripMult<=0` guard covers hand-edited JSON.

## Verification

Headless batch compile after each step (0 `error CS`, wait for Unity exit); relaunch editor at end. Play-test: (1) old saved vehicle drives identically; (2) garage: set FL angle 25° → visible lean, mirror syncs FR, stats show ride freq/sag; (3) place suspension sensor, drive over speed bump → force spike on graph + CSV columns; (4) sensor manifest shows type 6 × 3 floats (log/graph check; DLL untestable locally per toolchain constraint); (5) each preset vehicle on its themed map — buggy soaks whoops, F1 fast + planted on Monza Mini, crawler climbs Boulder Basin, drift car slides Slide Yard; (6) save-as preset under new name → appears in library; presets remain pristine.

---

# Iteration 13 — High-fidelity physics for real-world controller validation

## Context

The user wants to rebuild a real 1/10-scale RC vehicle in the sim and validate closed-loop C firmware such that sim-tuned controllers transfer to real hardware. A three-way code audit (powertrain, tire/chassis, sensors) confirmed these gaps vs. the user's realism checklist:

**Present already:** back-EMF/R/Kt/current-limit motor model, viscous damping, datasheet conversion, tuned slip curves + per-surface + per-wheel grip, servo slew, per-wheel ζ-based suspension + anti-roll, rumble strips, Gaussian noise + constant bias + optional quantization, encoder CPR ticks, camera frame-rate gate.

**Absent (to build):** Coulomb friction Tc (noLoadCurrent exists but is never used at runtime — MotorModel.cs), rotor/drivetrain inertia J, ESC latency/PWM quantization/deadband (commands apply same physics tick), battery sag/internal resistance (fixed 7.4 V rail), Ackermann steering (parallel today, CarVehicle.cs ~591), load-sensitive grip, tire ballooning, general surface roughness, per-part mass/CoM/inertia (single scalar + fixed CoM + default tensor; no battery part), sensor drift, per-sensor sample rates + latency, IMU vibration noise, deterministic noise seeding, and the ABI `wheel_vel[]` is perfectly clean (CarVehicle.SampleSensors ~726-736).

**User-confirmed scope:** accept all deferrals (suspension stiction, SoC discharge, camera image noise, off-diagonal inertia); INCLUDE the validation tooling (step-response metrics overlay + CSV sidecar stamping).

## Guiding decisions

- **ABI stays v3.** Only append `SENSOR_BATTERY = 7` to the enum (same append-only pattern as Suspension=6). Mirror in `Bridge/ControllerInterop.cs`; `gcc -fsyntax-only` the header.
- **Back-compat everywhere:** every new serialized field's old-JSON value (0/false/empty) reproduces today's behavior exactly; `VehicleDesign.Default()` / `MotorParams.Default()` opt NEW designs into realism. Mirrors the existing `maxCurrent=0=unlimited` sentinel pattern.
- **Toggle homes:** per-motor electrical → `MotorParams`; per-wheel tire → `WheelSpec`/`CarWheelConfig`; vehicle-level (Ackermann %, composite mass, IMU vib, wheel_vel corruption) → `VehicleDesign`; harness-level (noise seed, actuation delay) → `GameSettings` + CSV metadata.

## Step 1 — Powertrain core: Coulomb friction, rotor inertia, ESC model

Files: `Vehicles/MotorModel.cs`, `Sensors/MotorPart.cs`, `Vehicles/CarWheelConfig.cs`, `Vehicles/CarVehicle.cs` (MakeWheel), `Garage/VehicleFactory.cs`, `Garage/GarageUI.cs` (motor inspector).

New `MotorParams` fields (old JSON → 0 = legacy; `Default()` values in parens): `coulombScale` (1), `rotorInertia` kg·m² (5e-6), `escPwmSteps` int (1024), `escDeadbandV` (0.10), `escTimeConstMs` (5), `escSlewVPerS` (0 — off, lag dominates).

**Coulomb in `WheelTorque`:** `Tc = coulombScale·kt·max(0,noLoadCurrent)` (motor-shaft N·m). If `|ω_motor| > 0.5 rad/s`: `τ_motor = kt·I − visc·ω − Tc·sign(ω)`. Else breakaway branch: `τ_net = kt·I − visc·ω; τ_motor = sign(τ_net)·max(0, |τ_net| − Tc)` — dissipative-only, no oscillation around zero at 400 Hz, and makes ToDatasheet's no-load approximation physically consistent.

**Rotor inertia:** PhysX wheel spin inertia = `0.5·wc.mass·r²` (0.05 kg @ 33 mm ≈ 2.7e-5; a 540 rotor through 8:1 reflects 5e-6·64 = 3.2e-4 — 12× larger). Build-time only: `CarWheelConfig.extraSpinInertia` (0 = legacy), set by VehicleFactory for powered wheels = `rotorInertia·gear²`; in `MakeWheel` (~line 449): `wc.mass = 0.05 + 2·extraSpinInertia/r²`. VehicleStats keeps reporting base 0.05/wheel (comment: inertia-equivalent mass ≠ translational).

**ESC pipeline in `MotorPart.StepDrive(dt)`** (dt finally used), between latch and DC model: deadband → PWM quantize (`round(v/maxV·steps)/steps·maxV`) → optional slew → first-order lag `_vFilt += (v−_vFilt)·(1−exp(−dt·1000/escTimeConstMs))`. `_vFilt` feeds the model and the published `voltage` channel (controller sees the ESC's effect, like probing a real ESC output). Reset in ResetMotor.

Garage motor inspector adds: Coulomb × 0–2, Rotor J 0–2e-5, ESC lag ms 0–20, PWM steps toggle row {0,256,512,1024,2048}, Deadband V 0–0.5.

Risk: verify in play-test that inflated wc.mass doesn't couple into suspension (PhysX uses body mass for sprung dynamics — expected fine; cap if anomaly appears).

## Step 2 — Battery: placeable garage part + electrical model + SENSOR_BATTERY

Files: `Controllers/hal/controller_api.h` (append `SENSOR_BATTERY = 7` + slice doc `[terminal_V, total_current_A, soc_01]`), `Bridge/ControllerInterop.cs`, NEW `Sensors/BatterySensor.cs`, `Garage/VehicleDesign.cs` (NEW `BatterySpec` + `List<BatterySpec> batteries`), `Garage/VehicleFactory.cs`, `Vehicles/CarVehicle.cs`, `Vehicles/PartVisualFactory.cs` (`BuildBatteryViz`), `Garage/PartIconFactory.cs`, `Garage/PartGhost.cs`, `Garage/PartMarker.cs` (append `PartType.Battery`), `Garage/GarageBootstrap.cs`, `Garage/GarageUI.cs`.

`BatterySpec` (AeroSpec template): `name`, `localPos=(0,−0.02,−0.05)`, `massKg=0.18`, `nominalV=7.4`, `internalR=0.03`, `capacitymAh=0` (reserved — 0 = infinite, SoC deferred), `mirrorGroup=−1`. Old JSON → empty list → stiff infinite supply (today).

**Model — top of `StepPhysics` before the motor loop (~553):** `V_term = V0 − internalR·I_total_prev` (previous-step total current — explicit integration, stable at 400 Hz); each motor's applied voltage clamps to `min(motor.maxVoltage, max(0, V_term))`; after the loop cache `I_total = Σ|motor.Current|`. First battery powers the bus; extra batteries add mass only. Sag appears as the MOTOR sensor's measured `voltage_V` dropping below command — exactly what real firmware sees.

`BatterySensor : SensorComponent`: Type=Battery, DataCount=3, fields `{volt, amps, soc}` (soc fixed 1.0), reads the vehicle's cached values; rig discovers it → `sens/<name>/volt|amps|soc` graphs/CSV free.

Garage: palette `("battery","Battery")`; viz = rounded box 55×30×16 mm + terminal nubs + balance-lead stub; inspector: pos sliders, Mass g 80–350, Nominal V toggle {3.7, 7.4, 11.1}, Int. R 0.005–0.1. Battery branch in drag state machine = minimal copy of the Aero branch (no yaw edit, no mirror twins — centerline part).

## Step 3 — Ackermann steering

Files: `Garage/VehicleDesign.cs` (+`ackermannPct = 0`; `Default()` = 100), `Garage/VehicleFactory.cs`, `Vehicles/CarVehicle.cs`, `Garage/GarageUI.cs` (STEERING section slider 0–100), `Garage/VehicleStats.cs` (optional inner/outer at full lock).

Geometry once in `BuildWheels`: `z_ref` = mean z of non-steering wheels (fallback rearmost; all-steer → center). Per steering wheel: `L_i = z_i − z_ref`, `x_i` = signed lateral.

Per tick (replaces target calc ~592): `δ_v = steerCmd·cfg.steerAngle`; if `|δ_v| < 0.25°` or pct 0 → parallel (legacy). Else `y_c = L_i/tan(δ_v)`, `δ_ack = atan(L_i/(y_c − x_i))`, `δ_i = δ_v + (pct/100)(δ_ack − δ_v)`; guard `|y_c − x_i| > 1e-3`; then `reverseSteering` sign and the existing MoveTowards slew unchanged (each wheel slews independently to its own target). Mirror twins converge on the correct physical pair because `x_i` encodes side. Negative `L_i` (rear-steer) yields correct reverse-Ackermann.

## Step 4 — Tires + surfaces: load-sensitive grip, ballooning, roughness

Files: `Vehicles/CarVehicle.cs`, `Vehicles/CarWheelConfig.cs`, `Garage/VehicleDesign.cs` (WheelSpec), `Garage/VehicleFactory.cs`, `Garage/GarageUI.cs` (wheel inspector), `TrackEd/TrackCatalog.cs`, `Track/SurfaceMap.cs`.

**(a) Load sensitivity** — `WheelSpec.loadSensitivity = 0` (off; slider 0–0.4, typical 0.15). In the per-wheel surface loop (~621-631): `Fz0 = m·g/wheelCount` (cached); `loadFactor = grounded ? clamp((hit.force/Fz0)^(−s), 0.6, 1.4) : 1`, quantized to 2% steps; `combined = surf.frictionMult·loadFactor`; dirty-check `combined` vs `w.lastMult` (semantics widen from surface-only to combined — `SetGrip` already multiplies by lastMult so stays correct). Perf: 2% quantization keeps rewrites to a few per second.

**(b) Ballooning** — `WheelSpec.balloonPct = 0` (off; slider 0–12%). Runtime per wheel: `ω_lp += (|ω|−ω_lp)·dt/0.1`; `r = r0·(1 + pct/100·min(1,(ω_lp/250)²))`; write `wc.radius` only when `|Δr| > 0.5 mm`; sync viz radial localScale. Suspension/anti-roll math reads `col.radius` live → consistent automatically.

**(c) Roughness** — `FloorTypeDef` += `roughAmp` (fraction of per-wheel weight) + `roughLen` m: dirt 0.03/0.12, grass 0.05/0.18, sand 0.04/0.25, mud 0.06/0.20 (asphalt/ice/checker 0). Extend `SurfaceInfo` (Baseline zeros → oval/diff-drive untouched). In the grounded surface block next to rumble (~611): `n = ValueNoise2(hit.point.x/λ, hit.point.z/λ)` (deterministic integer-hash + smoothstep bilinear value noise, ~15-line static helper); `AddForceAtPosition(up·n·roughAmp·m·g/wheelCount, hit.point)`. Position-based → identical forces lap after lap (repeatability).

## Step 5 — Mass, CoM, composite inertia + part masses

Files: NEW `Garage/MassProperties.cs`, `Garage/VehicleDesign.cs`, `Garage/VehicleFactory.cs`, `Vehicles/CarVehicle.cs` (Awake), `Garage/VehicleStats.cs`, `Garage/GarageUI.cs`.

New mass fields, all `0 = auto by kind`: `SensorSpec.massKg` (auto: ToF 5 g, Camera 15 g, Encoder 8 g, Susp 6 g), `AeroSpec.massKg` (auto: wing 10 g·sizeScale², splitter/dam 8 g), `WheelSpec.massKg` (auto: 30 g unpowered / 190 g powered — wheel + 540 motor + pinion). `Mass g` slider per part inspector.

Gate: `VehicleDesign.useCompositeMass = false` (old JSON → false → exactly today: rb.mass = design.mass, CoM (0,−0.03,0), default tensor); `Default()` → true; Body-tab checkbox "Composite mass & CoM" (Mass slider relabels "Chassis mass").

`MassProperties.Compute(design)` (pure; shared by factory + stats): chassis box inertia (Ixx = m/12·(h²+d²) etc. at (0,−0.03,0)) + point masses at part localPos → total M, CoM, diagonal inertia via parallel axis (products of inertia dropped — noted limitation), frontWeightPct, yawInertia. Factory stores on CarVehicle; `Awake` (~189-196) applies `rb.mass/centerOfMass/inertiaTensor` (+identity rotation) only when flag set — legacy path must NOT touch inertiaTensor. Corner mass for ζ-damper derivation (~255) uses composite M. Stats adds `CoM z/y, F/R %, yaw inertia`; totalMass/rideFreq/sag/top-speed inherit.

## Step 6 — Sensor realism: NoiseModel v2, rate/latency, seeding

Files: `Sensors/NoiseModel.cs`, `Sensors/SensorComponent.cs`, `Sensors/SensorRig.cs`, `Garage/VehicleDesign.cs` (SensorSpec), `Garage/VehicleFactory.cs`, `Garage/SymmetryUtil.cs`, `Garage/GarageUI.cs`, `Persistence/GameSettings.cs`, `Menu/MenuUI.cs` (Options), `Core/SimulationRunner.cs` (metadata).

**NoiseModel v2** (new fields default 0 = today): `driftRate` random-walk bias (`_walk += N(0,1)·driftRate·√dt`; `ResetState()` from rig Initialize); deterministic seeding — per-instance `System.Random(Hash(GlobalSeed, instanceOrdinal))` replaces global UnityEngine.Random; `NoiseModel.GlobalSeed` set at session start from `GameSettings.noiseSeed` if > 0 else TickCount — effective seed ALWAYS stamped into CSV sidecar (`noise_seed`). New `Apply(value, dt)` overload; keep old `Apply(value)` (no drift) so untouched call sites compile.

`SensorSpec` += `noiseStd`, `noiseQuant`, `driftRate` (all 0), factory maps onto each sensor's NoiseModel (garage-built sensors finally get configurable noise); REALISM block in sensor inspector; `SymmetryUtil.MirrorInto(SensorSpec)` copies.

**Rate + latency:** `SensorComponent` += `updateRateHz = 0` (0 = every tick) + `latencyMs = 0` + sealed `SampleGated(dt, simTime, dest, offset)`: sample fresh only when due (`dt_effective` = elapsed since last true sample so encoders integrate correctly), push (time, values) into preallocated 64-entry ring, output the newest entry ≤ `simTime − latencyMs/1000`. `SensorRig.Sample` (~120) calls SampleGated. Inspector: Rate Hz 0–100, Latency ms 0–100. CameraSensor keeps its own capture clock. MotorPart: gating applies to feedback Sample only — StepDrive drive path untouched (verified separate).

Options page: `Noise seed (0 = random)` int field.

## Step 7 — IMU vibration, ABI wheel_vel corruption, actuation delay

Files: `Sensors/ImuSensor.cs`, `Vehicles/CarVehicle.cs` (SampleSensors), `Garage/VehicleDesign.cs`, `Garage/VehicleFactory.cs`, `Garage/GarageUI.cs`, `Core/SimulationRunner.cs`, `Persistence/GameSettings.cs`, `Core/TrackBootstrap.cs`.

**(a) IMU vibration** — `VehicleDesign.imuVibration = 0` (off; `Default()` 0.1; slider 0–0.5 in new Body-tab REALISM section). In SampleSensors before noise: per-motor integrated phase `φ_k += ω_motor_k·dt`; amplitude `A = imuVibration·Σ|ω_motor|/2500`; `vib = A·(sin φ + 0.5·sin 2.07φ)` per motor distributed x/y/z with fixed weights (0.5, 1.0, 0.7) added to true accel; gyro gets 10% as rate ripple. Deterministic (phase-integrated, no RNG); spectrum tracks motor speed like a real unbalanced rotor.

**(b) ABI wheel_vel corruption** (closes the audit's CRITICAL clean-channel gap) — `VehicleDesign.wheelVelNoiseStd = 0` + `wheelVelQuantCpr = 0` (0 = clean = today, preserves all existing controllers). CarVehicle owns 4 seeded NoiseModels; in SampleSensors: CPR-consistent quantize `v = round(v·dt/(2π/cpr))·(2π/cpr)/dt` then `noise[i].Apply(v, dt)`. Two sliders in Body REALISM. `Default()` keeps 0 — the Real Twin preset sets the physical encoder's CPR.

**(c) Actuation transport delay** — `SimulationRunner.actuationDelayTicks = 0`; in ControlStep (~349) push `_actuators` into a preallocated ring, `SetCommands(ring[tick − N])` (zero-filled until N elapse). `GameSettings.actuationDelayTicks` + Options row 0–5; `TrackBootstrap` copies onto runners (~156, ~382). Sidecar: `actuation_delay_ticks`. Default 0 = today's same-tick contract.

## Step 8 — Validation tooling + Real Twin preset + docs

Files: NEW `Telemetry/StepMetrics.cs`, NEW `Telemetry/MetricsOverlay.cs`, `Telemetry/CsvLogger.cs` (`SetMetadata`), `Core/TrackBootstrap.cs`, `Garage/VehiclePresets.cs`, `Docs/interface-spec.md`, `README.md`.

`StepMetrics.Compute(hub, spChannel, measChannel)` — pure post-processor over TelemetryHub rings (8192 ≈ 82 s @ 100 Hz): find last setpoint step (|Δ| > 10% span after ≥ 0.5 s flat) → `riseTime` (10→90%), `overshootPct`, `settlingTime` (±5% band), `ssError` (mean of final 20% − target), `peakTime`.

`MetricsOverlay` — IMGUI box on **J** (alongside GraphOverlay's G), preset pairs `sp/linear ↔ veh/speed`, `dbg/target_speed ↔ veh/speed`, `cmd/steer_deg ↔ veh/steer_deg`; recompute 2×/s; graceful "no step found". Wired in TrackBootstrap next to the graph overlay (SP only).

CSV: `SimulationRunner.SaveTelemetry` stamps `rise_time_s / overshoot_pct / settling_time_s / ss_error` + `noise_seed` + `actuation_delay_ticks` into the JSON sidecar — sim-vs-real comparison = diffing two sidecars.

**"Real Twin 1/10" preset** in VehiclePresets: stock geometry with all realism on at hardware-shaped values (Coulomb 1, rotor 5e-6, ESC 1024 steps/0.1 V/5 ms, battery 2S 0.03 Ω 180 g at real tray position, Ackermann 100, loadSensitivity 0.15, balloon 3%, composite mass on, IMU vib 0.1, wheel_vel CPR = real encoder) — the single artifact the user edits toward their physical car.

## Deferred (user-confirmed)

Suspension stiction (external force fights PhysX internal solver → limit-cycle risk; revisit if real logs show hysteresis sim can't match), SoC discharge (`capacitymAh` reserved; drifting supply hurts repeatability), camera image noise (no pixel-consuming controller yet), off-diagonal inertia (<5% on near-symmetric layouts), per-motor transport delay (ESC lag + global delay span the same phase-margin territory).

## Risks

- WheelCollider at RC scale: runtime radius writes (0.5 mm epsilon), stiffness churn (2% quantization + combined dirty-check), inflated wc.mass (verify no suspension coupling in Step 1 play-test BEFORE later steps build on it).
- Old designs bit-compatible everywhere; only intentional change = seeded (vs unseeded) noise sequences.
- Garage drag state machine gains a 4th Battery branch — keep minimal Aero copy, no mid-iteration refactor.
- VehicleStats top speed uses MotorModel.WheelTorque → Coulomb/ESC auto-reflected (verify).

## Verification

Per step: headless batch compile (0 `error CS`, editor closed, wait for exit); `gcc -fsyntax-only controller_api.h` after Step 2; relaunch editor at end. Play-test script:
1. **Back-compat:** pre-i13 vehicle JSON + settings → oval drive matches i12 behavior (all sentinels legacy).
2. **Determinism:** Autonomous, `noiseSeed = 42`, two runs → binary-identical CSVs.
3. **Powertrain:** full-throttle standing start on Real Twin → S-curve spin-up (rotor J + ESC lag), `sens/battery1/volt` sag ≈ I·R, motor at rest with 0.05 V doesn't creep (deadband + Coulomb).
4. **Steering:** full-lock circles at Ackermann 0 vs 100 → different front-wheel angles visible, less inner-tire scrub at 100.
5. **Tires/surfaces:** grass/sand show IMU accel texture, identical across laps; braking reduces peak side grip.
6. **Mass/CoM:** battery nose↔tail moves F/R ~10% in stats; braking pitch differs on track.
7. **Validation loop (the point):** Autonomous car_pid DLL, step the speed setpoint, **J** shows rise/overshoot/settling; Save → sidecar has metrics + seed + delay; add 2 delay ticks → overshoot grows (transfer-gap detection works).

---

# Iteration 14 — Blender 3D asset pipeline (body shells, tires, batteries, antennas)

## Context

The user added reference photos of real 1/10-scale RC cars (`example car models/car_example*.jpg` — a Tamiya touring chassis with a blue cylindrical stick battery + 5-spoke wheels; two F1TENTH/JetRacer-style autonomous builds with knobby/rally tires, PCB stacks, and angled rubber-duck WiFi antennas) and wants stylized-but-recognizable 3D models for **vehicle bodies, tires, battery packs, and antennas**. Today the game is **100% runtime-procedural** — every visual is `GameObject.CreatePrimitive`; there is no `Resources/` folder, no prefab, no mesh-asset loading anywhere. Procedural cubes/cylinders are fine for physics-driven parts but can't express a curved lexan shell, a treaded tire, or a coiled antenna — exactly what the user is asking for. This iteration introduces the project's **first mesh-asset pipeline**: author low-poly stylized meshes in Blender (MCP is connected — Blender 5.2 LTS, metric, FBX exporter available), export FBX into a new Resources folder, and make the shared visual factory mesh-aware with a **primitive fallback** so the game still runs before/without the assets and every existing design is untouched.

**Why this is low-risk:** visuals are fully decoupled from physics — the WheelCollider (per-wheel) and the root BoxCollider do all the physics; part viz is cosmetic geometry on layer 2 (Ignore Raycast) with colliders stripped. Swapping the *look* of wheels/body/battery/antenna changes nothing in the sim, ABI, or telemetry, provided the loader keeps stripping colliders and staying on layer 2.

**Scope confirmed with the user:** Antenna = a **full placeable garage part** (new `PartType`, palette/mirror/save-load, firmware-invisible); bodies = **three hero shells** (Shell, LowRacer, Buggy — Box/Wedge stay primitive); tires = **multiple selectable styles** (slick / knobby / rally + a per-wheel style field).

## Architecture — the mesh seam (one loader, name-keyed, fallback)

Four rendering contexts already funnel through `PartVisualFactory.Build*Viz` — the live car (`CarVehicle`/`VehicleFactory`), the network ghost (`Net/ClientCarView`), drag-placement ghosts (`Garage/PartGhost`), and palette icons (`Garage/PartIconFactory`). Making those builders mesh-aware upgrades **all four for free**.

- **New `Assets/Resources/PartModels/`** — holds exported FBX. `Resources.Load` finds assets by string path (no GUID/`.meta`/prefab-asset coupling in code — consistent with the project's no-prefab ethos). The headless batchmode compile refreshes/imports the folder automatically, so no manual editor step beyond the normal compile checkpoint.
- **New `Vehicles/PartMeshLibrary.cs`** (static, mirrors the lazy-material pattern in `PartVisualFactory`):
  - `GameObject TryInstantiate(string key, Transform parent)` → `Resources.Load<GameObject>("PartModels/"+key)`; on hit, `Object.Instantiate` under `parent` (identity local pose), then **recursively strip every Collider, destroy any Rigidbody, and set layer = `PartVisualFactory.VizLayer` (2)**; return the instance. On miss return null. Cache both hits and misses in a `Dictionary<string,GameObject>` so a missing asset is probed once.
  - `void AssignByName(GameObject root, params (string token, Material mat)[] map)` — walk child renderers; assign a shared material when the object name contains a token (case-insensitive). Lets a multi-part mesh (e.g. `tire`+`rim`) pick up the existing shared `PartVisualFactory` materials so lighting/theme/recolor stay one system; the FBX's own materials are ignored.
  - `static bool Enabled = true` — global off-switch for A/B against primitives and quick revert.
- **Authoring/orientation contract** (baked into export, verified in Unity): meters, **+Y up, −Z forward** to match Unity; author each asset at its real base dimension and let runtime `localScale = target/authoredBase` normalize away any FBX import-scale factor. Wheels authored with **axle along +X**, radius in the Y/Z plane (so `CarVehicle`'s ballooning rescale of holder Y/Z and the WheelCollider spin stay correct).

## The four asset families

### 1. Tires — multiple selectable styles
Blender: three wheel FBX, each a two-object hierarchy (`tire` + `rim`), authored at radius 0.033 m, axle +X:
`wheel_slick` (smooth touring + 5-spoke rim, image 1), `wheel_knobby` (lugged off-road + dish rim, image 2), `wheel_rally` (light rally tread + mesh spoke rim, image 3).
Wiring:
- `WheelSpec.wheelStyle` (int, **field-initializer 0 = slick** → old JSON loads as slick) + mirror in `SymmetryUtil.MirrorInto(WheelSpec)`; mirror field into `Vehicles/CarWheelConfig.cs`.
- `PartVisualFactory.BuildWheelViz(holder, radius, powered, inboardSign, int style = 0)` — new **optional** param (existing 4-arg callers unaffected): try `PartMeshLibrary.TryInstantiate("wheel_"+key)`; on hit scale to `radius`, `AssignByName` (`tire`→Tire, `rim`→Rim), then keep the existing powered motor-can primitive; on miss run today's primitive body. `CarVehicle.MakeWheel` (`:576`) passes `cfg.wheelStyle`; icons/ghost pass the style or 0.
- `VehicleFactory` copies `spec.wheelStyle → CarWheelConfig`; `GarageUI.DrawWheelInspector` gains a **Wheel Style** cycle button. Ballooning (`CarVehicle` holder Y/Z) and `UpdateVisual` spin unchanged.

### 2. Body shells — three hero shapes
Blender: `body_shell`, `body_lowracer`, `body_buggy` FBX — single object, one material slot, authored at the default `bodySize` (0.20×0.10×0.42).
- `CarVehicle.BuildBodyVisual` (`:396`): before the shape switch, try `PartMeshLibrary.TryInstantiate("body_"+key, holder)`; on hit scale per-axis to `bodySize/authoredBase`, register its renderer(s) in `_bodyRenderers`, assign `_bodyMat` (carries the user's `bodyColor`) so `SetBodyMaterial`/recolor keep working; on miss fall through to the existing primitive builders. `BodyShape` enum + its aero mapping (`AeroDynamics.BodyCd/ClA`) untouched; Box/Wedge always primitive.

### 3. Battery packs
Blender: `battery_stick` FBX — blue cylindrical stick pack (image 1), authored ~55×16×30 mm, long axis +Z.
- `PartVisualFactory.BuildBatteryViz` (`:190`): try `PartMeshLibrary.TryInstantiate("battery_stick")`, `AssignByName` (`wrap`→Lipo, `term`→Stud); else current primitive. No data change — battery is already a full part.

### 4. Antennas — new full placeable part (Battery-pattern, cosmetic)
Firmware-invisible (no sensor, no ABI — simpler than Battery: no electrical model). Blender: `antenna_stub` FBX — angled SMA base + tapered flexible whip (images 2/3), authored ~90 mm tall along +Y.
New plumbing (mirrors the Battery/Aero parts):
- `Garage/VehicleDesign.cs`: `AntennaSpec { name, localPos, yawDeg, tiltDeg, sizeScale=1, massKg=0, mirrorGroup=-1, Clone() }` + `List<AntennaSpec> antennas` (old JSON → empty).
- `Vehicles/PartVisualFactory.cs`: `BuildAntennaViz(parent, tiltDeg, sizeScale)` — mesh via `PartMeshLibrary`, primitive fallback (tapered cylinder + base nub).
- `Garage/VehicleFactory.cs`: `CreateAntennaVisual(parent, spec)` (copy of `CreateAeroVisual` `:186`, applies yaw + tilt) + a build loop beside the aero/battery loops.
- `Garage/PartMarker.cs`: append `PartType.Antenna`; `Garage/PartGhost.cs`: `ForAntenna`; `Garage/PartIconFactory.cs`: `"antenna"` key.
- `Garage/GarageUI.cs`: palette entry, `StartPlacing`→pending `AntennaSpec`, drag/commit/select/delete branches (copy the Aero branch — it has yaw + mirror), `DrawAntennaInspector` (pos, yaw, tilt, size, mirror row, delete), and `PartType.Antenna` in the selection/marker/visibility/highlight paths.
- `Garage/SymmetryUtil.cs`: `FindTwin`/`MirrorInto`/`SyncTwin` for `AntennaSpec` (negate x + yaw), include `d.antennas` in `NextGroupId`.
- `Garage/MassProperties.cs`: antenna auto-mass (~8 g) so composite CoM/inertia counts it.

## Blender authoring method

All meshes built deterministically via `execute_blender_code` (bmesh + operators — tori for tires, spoke arrays for rims, lofted/beveled profiles for bodies, tapered cylinders for antennas), low-poly (~a few hundred–1.5k tris each; up to 4 cars render at once in split-screen/LAN). Save an editable **`Blender/parts.blend`** as source; export each asset with `bpy.ops.export_scene.fbx` (selected-object, `apply_unit_scale=True, global_scale=1, axis_forward='-Z', axis_up='Y'`) into `Assets/Resources/PartModels/`. FBX materials are placeholders — runtime overrides them by name. Confirm each asset reads correctly with `render_viewport_to_path` before export.

## Files

New: `Vehicles/PartMeshLibrary.cs`; `Assets/Resources/PartModels/*.fbx` (wheel_slick/knobby/rally, body_shell/lowracer/buggy, battery_stick, antenna_stub); `Blender/parts.blend`.
Modified: `Vehicles/PartVisualFactory.cs` (mesh-aware `BuildWheelViz`/`BuildBatteryViz` + new `BuildAntennaViz`), `Vehicles/CarVehicle.cs` (`BuildBodyVisual` mesh path; `MakeWheel` style pass), `Vehicles/CarWheelConfig.cs` (`wheelStyle`), `Garage/VehicleDesign.cs` (`WheelSpec.wheelStyle`, `AntennaSpec`, `antennas`), `Garage/VehicleFactory.cs`, `Garage/GarageUI.cs`, `Garage/PartGhost.cs`, `Garage/PartIconFactory.cs`, `Garage/PartMarker.cs`, `Garage/SymmetryUtil.cs`, `Garage/MassProperties.cs`, `README.md`.

## Steps (headless compile checkpoint after each)

1. **Mesh infra** — `PartMeshLibrary`; make `PartVisualFactory.BuildWheelViz`/`BuildBatteryViz` mesh-aware with fallback. No assets yet → all fallback → zero visual change. Compile.
2. **Blender authoring** — build + export the 8 FBX into `Assets/Resources/PartModels/`; save `parts.blend`; batch-compile imports them.
3. **Wheel styles** — `WheelSpec`/`CarWheelConfig` field, `BuildWheelViz` style param, `CarVehicle`/`VehicleFactory` pass-through, `GarageUI` picker, `SymmetryUtil`.
4. **Body meshes** — `CarVehicle.BuildBodyVisual` mesh path for Shell/LowRacer/Buggy.
5. **Antenna part** — spec + list, factory creator/loop, ghost, icon, marker, UI (palette/inspector/drag/mirror), `SymmetryUtil`, `MassProperties`.
6. **Validate + docs** — README, headless compile, editor relaunch.

## Risks

- **FBX import scale/orientation** — mitigated by authoring at real meters, exporting with explicit axes, and runtime-scaling to a known target dimension (normalizes any uniform import factor); orientation baked in and verified in the garage preview.
- **Collider leakage from imported models** — `PartMeshLibrary.TryInstantiate` strips all colliders + Rigidbodies and forces layer 2; the on-car `CameraSensor` culls layer 2 so meshes never self-occlude the sensor camera.
- **Old-JSON compat** — `wheelStyle` default 0 and empty `antennas` list reproduce today's look exactly; `Clone()`/JsonUtility round-trip the new fields.
- **Garage drag state machine** gains a 4th cosmetic branch (Antenna) — keep it a minimal copy of the Aero branch, no mid-iteration refactor.

## Verification

- Headless batch compile (0 `error CS`) after each step; editor relaunch at end.
- Blender: `render_viewport_to_path` per asset confirms it reads as its component before export.
- Unity: in the garage preview, confirm wheels spin about the axle, body scales to `bodySize`, battery sits in the tray, antenna stands upright at the authored scale; toggle `PartMeshLibrary.Enabled` off to confirm the primitive fallback and that pre-i14 designs are byte-identical.
- Play-test: assemble a car with knobby tires + touring shell + stick battery + two mirrored antennas; drive it (physics/telemetry unchanged); save/reload (JSON round-trips `wheelStyle` + `antennas`); spawn on the track and as a split-screen/LAN ghost — all four viz contexts show the meshes; the `car_example*.jpg` references are visibly the inspiration.

---

# Iteration 15 — Garage UI overhaul: tooltips, snapping, categorized palette, camera-while-dragging, paint, real aero

## Context

Play-testing the garage after the mesh-asset iteration surfaced seven UX/feature requests: (1) hover should show a part's name/description; (2) grid-snap toggles for part placement; (3) the palette should preview the actual part, organized into sub-categories; (4) the camera must stay rotatable/zoomable while holding a part (drag/ghost mode); (5) undo/redo for everything; (6) custom color painting — pixels painted onto the body in-game; (7) wings/aero should genuinely respond to placement (torque, downforce, lift).

Scope confirmed with the user: **pixel painting** on the body (brush + color picker, saved in the vehicle JSON, LAN-synced); **position + angle-of-attack aero model** (lever-arm torque, signed lift, stall); **snap toggle + step** (5 mm / 15°); **categorized palette with live rotating 3D hover preview**.

Current-state facts (from code audit):
- Palette is one flat 12-entry grid (GarageUI.cs:56-64, 2-per-row at :720); icons are already 64px snapshots of the real `PartVisualFactory` geometry (mesh-aware since i14) — "actual part" previews half-exist; categories and hover preview don't.
- Only tooltip = `GUI.tooltip` label at the tab bottom (GarageUI.cs:727-728); no descriptions, no scene-hover info.
- **No snap of any kind**: placement is the raw continuous surface hit (`Compute*Place`, GarageUI.cs:517-544); only drag-yaw already steps 15° via scroll (:141-143).
- **Camera fully blocked during drags**: `Orbit.blockDrag = overUI || _drag != Idle` (GarageUI.cs:81) gates orbit AND pan AND zoom in OrbitCamera (one flag).
- Undo is nearly complete (every mutation calls PushUndo; sliders coalesce). The one real gap: the motor **Constants⇄Datasheet mode switch** rewrites `w.motorDatasheet`/`motorEntryMode` with no PushUndo (GarageUI.cs:1056-1060). Aim-vectors/mirror toggles are view state (correctly not undoable).
- Aero: forces already `AddForceAtPosition` at each part's `localPos` (CarVehicle.cs:900-909) so **position→torque already works**; but downforce is always along `-transform.up`, clA ≥ 0 always (no lift possible), part `yawDeg` never rotates the force, and airflow direction/AoA is ignored (AeroDynamics.cs:71-93).
- **No texture/livery/paint path exists anywhere**; body appearance = single `_bodyMat` flat color (CarVehicle.cs:396-437). Appearance syncs over LAN/save as the full design JSON (reliable-fragmented, MaxPayloadSize 256 KB) — a base64 field on `VehicleDesign` rides that path for free.
- Body FBX import sets `isReadable = false` (PartModelPostprocessor) — must flip for paintable bodies (MeshCollider + UV raycast need readable meshes at runtime).

## Step 1 — Camera-while-dragging + grid snap + undo gap

**`Garage/OrbitCamera.cs`** — split the single gate: keep `blockDrag` (gates orbit+pan) and add `public bool blockZoom` (gates zoom only). Zoom check becomes `!blockZoom`.

**`Garage/GarageUI.cs` `Update` (:81)** — new gating:
- `Orbit.blockDrag = overUI;` — **RMB orbit and MMB pan now work mid-drag** (drags use LMB only; no conflict).
- `Orbit.blockZoom = overUI || (_drag != Idle && !ctrlHeld);` — during a drag, plain scroll keeps rotating the ghost yaw (existing muscle memory), **Ctrl+scroll zooms the camera**. When idle, scroll zooms as today. `UpdateDragging`'s yaw-scroll (:141-143) ignores scroll when Ctrl is held.
- `InputReader`: add `CtrlHeld()` (dual-backend, same pattern as existing key helpers).

**Grid snap** — `GarageUI` fields `_snapEnabled` (default off) + consts `SnapPos = 0.005f` (5 mm), `SnapYaw = 15f`:
- Toolbar row (next to Aim vectors / Mirror ✕2): **Snap** toggle + hotkey **N** (`InputReader.SnapTogglePressed()`; G is taken by graphs elsewhere, X by mirror).
- Apply in one place: a `Vector3 SnapLocal(Vector3 lp)` helper (`Round(v/SnapPos)*SnapPos` per axis) called at the end of each `Compute*Place` before the ghost pose / commit, only when `_snapEnabled`. Yaw: inspector Heading sliders stay continuous; ghost scroll-yaw already steps 15°.
- Inspector position sliders unaffected (snap is a placement aid, not a data clamp).

**Undo gap** — add `bootstrap.PushUndo("motormode")` before the Datasheet/Constants switch writes (GarageUI.cs:1056-1060). Audit confirms every other mutation is covered; paint strokes get their own undo in Step 5.

## Step 2 — Categorized palette + descriptions + hover tooltips + live 3D preview

**Palette data** (GarageUI.cs:56-64) — restructure to categories:
```
Wheels:  wheel, wheel_powered        Sensors: camera, tof, encoder, suspension
Aero:    wing, splitter, sidedam, canard      Power: battery      Misc: antenna
```
Each entry gains a one-line description (e.g. tof: "Time-of-flight ranger — 4 m cone, firmware-readable"). `DrawPartsTab` renders category headers (GarageSkin.Header) with the existing 2-per-row icon grid under each; replace the bottom `GUI.tooltip` label with the floating tooltip below.

**Floating tooltip** — drawn last in `OnGUI` (topmost): when the pointer rests over a palette icon (track hovered key via `Rect.Contains` during draw) OR over a placed part in the scene (Idle-state raycast in `Update` already hits `PartMarker`s — reuse it to set `_hoverMarker` when not dragging), draw a small skinned box near the cursor after a 0.35 s hover delay: **bold name + description line** (palette) or **part name + type + key stat** (scene: e.g. "tof_front — ToF sensor, range 4 m"; wheel: powered/steered; aero: kind + angle). Clamp to screen.

**Live 3D hover preview** — new `Garage/PartPreviewRig.cs` (PartIconFactory's `Snapshot` pattern, but persistent):
- Lazily builds a hidden rig at `(0,-600,0)`: part viz via the same `PartIconFactory.Icon` build lambdas (refactor the key→build-action map into a public `PartIconFactory.BuildFor(key, parent)` so icons and preview share it), a dedicated camera (cullingMask = VizLayer only, transparent clear) rendering to a **160×160 RenderTexture every frame** while active, part root yaw += ~40°/s.
- `Show(key)` (rebuilds only when key changes), `Hide()` (disables camera — zero cost when not hovering), `Texture` property. GarageUI draws it inside the floating tooltip box when the hovered thing is a palette icon.

## Step 3 — Real aero: angle-of-attack, signed lift, yaw-aware forces

**`Vehicles/AeroDynamics.cs`** — replace the scalar-angle model with a directional one (new method; keep `PartCoefficients` for the stats/straight-line case):
```csharp
// airflowLocal = body-space air velocity (= -vLocal). Part frame from Euler(0, yawDeg, 0).
public static Vector3 PartForce(AeroKind k, float angleDeg, float sizeScale,
                                Vector3 airflowLocal, out Vector3 forceLocal)
```
- **Wing/Canard**: chord pitch = `angleDeg` about the part's local X (after yaw). Effective AoA = geometric angle − airflow pitch **in the part's frame** (`atan2(flow.y, flow.z-component along chord)`), so body pitch over a jump, a backwards wing (yaw 180°), or airflow from below all flip/scale the force naturally. Lift coefficient: linear `clSlope·AoA` up to **±18° stall**, then a smooth ~40% drop (Lerp over 6°); **signed** — negative effective AoA produces upward lift (the requested "lift"). Force applied along the part's local −up rotated by (yaw, pitch), NOT raw `-transform.up`. Induced drag `cd0 + k·cl²` along −airflow.
- **Splitter/SideDam**: constant-cl devices scaled by `max(0, dot(partFwd, −flowDir))` — sideways/backwards mounting neutralizes them; side-slip loads a side dam asymmetrically (yaw moment emerges).
- Magnitudes calibrated so **yaw 0, level, current angles reproduce today's forces** (same clA/cdA at effAoA = angleDeg) — existing designs drive the same in a straight line.

**`Vehicles/CarVehicle.cs` `ApplyAerodynamics` (:884-910)** — per-part loop computes `airflowLocal = transform.InverseTransformDirection(-velocity)`, calls `PartForce`, transforms the returned local force to world, applies at `TransformPoint(localPos)` (unchanged — lever-arm torque already correct). `aeroMult` keeps scaling the downforce component only. `AeroConfig` gains `yawDeg` (copied in VehicleFactory from `AeroSpec.yawDeg` — the field exists, it was just never used physically).

**Garage feedback** — `Garage/VehicleStats.cs` + aero inspector: add an **aero balance readout** — at 10 m/s straight-line, % of total downforce ahead of CoM (lever arms about `MassProperties` CoM, falling back to geometric center) plus total downforce/drag. Shown in the stats panel and echoed in `DrawAeroInspector` so placement consequences are visible while editing. `TotalClA/TotalCdA` (stats top-speed solver) keep the straight-line assumption — still correct.

## Step 4 — Blender: UV-unwrap the three body shells

Painting needs sane UVs; the i14 bodies were exported without deliberate unwraps.
- Reopen `Blender/parts.blend` (via MCP; `bpy.ops.wm.open_mainfile` — session state resets between calls). For `body_shell`, `body_lowracer`, `body_buggy`: Smart UV Project (angle limit 66°, island margin 0.02), verify islands don't overlap (render/inspect), re-export the three FBX with the established settings (selected-object, `axis_forward='-Z'`, `axis_up='Y'`, bake_space_transform). Save parts.blend.
- **`Assets/Editor/PartModelPostprocessor.cs`**: `isReadable = true` for assets whose name starts with `body_` (MeshCollider cooking + `RaycastHit.textureCoord` need readable meshes in builds; wheels/battery/antenna stay non-readable).
- Re-run `PartModelValidator.Report()` headless to confirm bounds unchanged.

## Step 5 — Pixel painting (livery)

**Data** — `Garage/VehicleDesign.cs`: `public string liveryPng = "";` (base64 PNG, "" = none — old JSON loads clean). Rides `Clone()`, save/load, snapshots, and LAN `vehicleJson` automatically (painted 256px PNG ≈ 5–50 KB base64, well under the 256 KB net payload cap).

**Runtime material path** — `Vehicles/CarVehicle.cs` `BuildBodyVisual`: after `_bodyMat` creation, if `livery` texture provided (new `public Texture2D liveryTex`, set by VehicleFactory decoding `design.liveryPng` via `ImageConversion.LoadImage`), set `_bodyMat.mainTexture = liveryTex; _bodyMat.color = Color.white` (livery pixels carry the color; unpainted pixels are pre-filled with `bodyColor`). No livery → today's flat color. All four viz contexts (live car, LAN ghost, garage preview) inherit it because they all build through VehicleFactory/CarVehicle.

**Paint mode** — new `Garage/BodyPainter.cs` + a **PAINT tab** in the left panel (BODY | PARTS | PAINT toolbar):
- Entering: only when the body is a mesh shell (Shell/LowRacer/Buggy — `CarVehicle.BodyMeshKey != null`); Box/Wedge shows "painting needs a shell body". Creates the working `Texture2D` (256×256 RGBA32, bilinear): decoded livery, or filled with `bodyColor`. Adds **temporary MeshColliders** to the body viz children (sharedMesh from their MeshFilters, on the body's own layer) so `RaycastHit.textureCoord` works; removed on tab exit (they'd otherwise pollute placement raycasts — the drag state machine is disabled while the PAINT tab is active).
- Painting: LMB (not over UI) raycasts the body MeshCollider; stamp a filled circle at `textureCoord` (brush radius in UV px), `SetPixels32` + `Apply` per frame while held; interpolate between last/current UV for smooth strokes. RMB orbit / MMB pan / scroll zoom stay live (paint mode never sets blockDrag).
- **Mirror brush** toggle: raycast the X-mirrored world point (`localPos.x` negated, re-projected out along the mirrored ray) and stamp there too — symmetric liveries despite asymmetric UV islands.
- UI: 12-swatch palette + R/G/B sliders, brush size 2–24 px, **Clear** (refill with bodyColor), Eyedropper (Alt+click).
- **Undo**: on stroke start (LMB down), encode current texture into `Design.liveryPng` then `PushUndo("paint")` (stroke-level undo; DesignHistory's JSON snapshot carries the base64). On stroke end, write the new texture into `Design.liveryPng` (no rebuild needed — the preview's material texture is the working texture, live). Undo/redo path: `SetDesign` rebuild decodes `liveryPng` normally.
- On Save / Drive / tab exit: sync `liveryPng` from the working texture (stroke-end already does; belt-and-braces).

**`Garage/VehicleFactory.cs`** — decode `design.liveryPng` once per build → `car.liveryTex`. **Net/session**: nothing to do — design JSON already carries it end-to-end (verified path: HelloMsg/RosterEntry/SessionSnapshot all embed vehicleJson).

## Step 6 — Docs + validation

- README: garage section — snap toggle (N), Ctrl+scroll zoom mid-drag, palette categories, paint mode, aero model note.
- Headless compile after every step (0 `error CS`, editor closed, wait for exit); `PartModelValidator` after Step 4; editor relaunch at end.

## Files

New: `Garage/PartPreviewRig.cs`, `Garage/BodyPainter.cs`.
Modified: `Garage/GarageUI.cs` (gating, snap, categories, tooltips, PAINT tab, undo gap), `Garage/OrbitCamera.cs` (blockZoom), `Core/InputReader.cs` (CtrlHeld, SnapTogglePressed), `Garage/PartIconFactory.cs` (public BuildFor), `Vehicles/AeroDynamics.cs` (PartForce AoA model), `Vehicles/CarVehicle.cs` (aero application, liveryTex path), `Garage/VehicleFactory.cs` (yawDeg→AeroConfig, livery decode), `Garage/VehicleStats.cs` (aero balance), `Garage/VehicleDesign.cs` (liveryPng), `Assets/Editor/PartModelPostprocessor.cs` (body readable), `Blender/parts.blend` + 3 re-exported body FBX, `README.md`.

## Risks

- **Undo snapshot size with liveries**: 50 snapshots × ~50 KB base64 ≈ 2.5 MB strings worst case — acceptable; paint pushes coalesce per stroke key so idle strokes don't multiply.
- **Readable body meshes** cost a RAM copy (a few thousand verts × 3 — negligible).
- **Aero back-compat**: calibrated to match current forces at level/straight/yaw-0; existing designs may differ slightly mid-jump or in slides (intended realism, not regression). Stats solver unchanged.
- **Scroll conflict during drag** resolved by Ctrl modifier; if play-test finds it awkward, swap the default (scroll = zoom, Ctrl+scroll = yaw) — one-line change.
- **textureCoord requires the hit MeshCollider** — paint raycast uses a dedicated mask/distance and runs only in PAINT tab, so the placement path is untouched.

## Verification (user play-test script)

1. Hover a palette icon → after a beat, tooltip with name/description + rotating 3D preview of the real mesh; hover a placed part in the scene → its name/info.
2. Palette shows Wheels / Sensors / Aero / Power / Misc sections.
3. Toggle Snap (N): ghost positions step in 5 mm increments; off = free placement.
4. Hold a part: RMB orbits, MMB pans, Ctrl+scroll zooms, plain scroll still rotates the part 15°.
5. Ctrl+Z: covers datasheet mode switch and paint strokes; everything else as before.
6. PAINT tab on a Shell body: brush strokes land under the cursor, mirror brush paints both sides, undo removes a stroke; save/reload keeps the livery; Drive shows it on track; LAN client sees the painted car; Box body politely refuses.
7. Aero: wing at the rear plants the rear (balance readout shifts rearward when moved back); mount a wing backwards (yaw 180°) → car gets light/lifts at speed; nose-up over a jump generates pitch-dependent forces; side dams produce yaw moment in slides.

---

# Iteration 16 — Visible suspension strut (per-wheel), length/angle reposition the wheel + spring

## Context

Suspension today is five per-wheel numbers on `WheelSpec`/`CarWheelConfig` (`suspStiffness`, `suspDampingRatio`, `suspTravel`, `suspAngleDeg`, `gripMult`). `suspAngleDeg` already tilts the WheelCollider mount (`CarVehicle.StrutTiltZ`, side-relative, clamped ±30°) for a camber-like lean, but nothing is **visible** and there is no notion of strut **length**. The user wants each wheel/motor/tire to carry a **visible suspension strut** — a spring/shock drawn from the wheel base up to the vehicle body — whose **length** and **angle** are tunable and physically move the wheel and update the spring model.

Confirmed scope: **mount-fixed** (the body-side attach point is the anchor; tuning length/angle moves the wheel down/outboard, not the mount); **intrinsic per-wheel**, `suspLength = 0` = rigid mount / no strut (today's exact look, and the old-JSON sentinel); **motion-ratio physics** (a longer arm to the wheel lowers the effective wheel rate and increases wheel travel via a rocker ratio, on top of the existing angle→`cos²θ` installation effect). The separate firmware-readable **Suspension sensor** part is untouched — this is the wheel's own strut geometry, not that sensor.

Back-compat is preserved by the same sentinel pattern used throughout: `suspLength = 0` (new field default) reproduces today's geometry and spring exactly; `Default()`/presets opt in with `length = NominalArm` (motion ratio 1 → identical numbers) and mounts raised so the stock car's hubs don't move — it just gains visible struts.

## Physics — motion ratio (single source of truth)

New **`Vehicles/SuspensionGeometry.cs`** static, so CarVehicle, MassProperties, VehicleStats, and the inspector all agree:
- `NominalArm = 0.03f` (reference; at this length the ratio is 1 = legacy numbers).
- `TiltZ(localPosX, angleDeg)` — the existing `StrutTiltZ` formula (side-relative sign, clamp ±30°) moved here; `CarVehicle.StrutTiltZ` delegates to it.
- `HubOffsetLocal(localPosX, angleDeg, length)` → `Vector3.zero` when `length ≤ 0`, else `Quaternion.Euler(0,0,TiltZ(...)) * Vector3.down * clampLen`, `clampLen ∈ [0.015, 0.06]`. (Tilt is about Z, so the hub shifts only in x/y — **z is unchanged**, keeping Ackermann/wheelbase/anti-roll pairing, which key off `localPos.z`/`x`-sign, valid against the mount.)
- `MotionRatio(length)` = `NominalArm / clampLen` (0 or legacy → 1).
- `EffectiveRate(k, length)` = `clamp(k · MR², 50, 4000)` — longer arm to the wheel → softer wheel rate.
- `EffectiveTravel(travel, length)` = `clamp(travel · (clampLen/NominalArm), 0.005, 0.12)` — longer arm → more wheel travel.

`CarVehicle.MakeWheel` (~598): when `suspLength > 0`, set `spring.spring = EffectiveRate(k, L)` and `wc.suspensionDistance = EffectiveTravel(suspTravel, L)`; when 0, today's values verbatim. **Stability guarantee:** the ζ→damper line already derives `damper = ζ·2·√(spring.spring·cornerMass)` from the *post-scaled* spring, so the damping ratio stays ζ at any length — no destabilization (this is the safeguard against the RC-scale WheelCollider risk). The angle→`cos²θ` vertical-rate effect keeps coming for free from tilting the collider (unchanged).

## Geometry — wheel hangs below the mount

`localPos` becomes the **body mount** (the drag/placement anchor). New helper `CarVehicle.HubLocal(cfg)` = `cfg.localPos + SuspensionGeometry.HubOffsetLocal(cfg.localPos.x, cfg.suspAngleDeg, cfg.suspLength)`:
- `MakeWheel` places the WheelCollider GO and (via `PoseWheelVisualsFromConfig`, ~267) the wheel viz at `HubLocal` instead of `localPos`; collider/viz rotation unchanged (`yaw + TiltZ`).
- Anti-roll pairing, `_wheelbaseEst`, `_ackZRef`, corner weight, drag-placement, and mirror all keep using `localPos` (the mount) — valid because only x/y differ from the hub and the sign/z are preserved.
- `suspLength = 0` → `HubLocal == localPos` → today's build exactly.

## Visible strut

- **`PartVisualFactory.BuildStrutViz(parent, ...)`** — a coil-over (shock body + rod + a few coil rings, reusing the `BuildSuspensionViz` look) authored along **+Z**, unit length 0→1, on `VizLayer`. Built once per wheel, parented to the car body (`transform`), not the wheel (so it spans body→wheel).
- `Wheel` runtime (`:123`) gains `Transform strut;` and `Vector3 mountLocal;`. `MakeWheel` builds the strut when `suspLength > 0` and records `mountLocal = cfg.localPos`.
- New `CarVehicle.UpdateStruts()` orients each strut: `mountWorld = transform.TransformPoint(mountLocal)`, `hubWorld = w.viz.position`, `strut.SetPositionAndRotation(mountWorld, Quaternion.LookRotation(hubWorld - mountWorld))`, `strut.localScale.z = distance`. Called from a **new `LateUpdate()`** (covers dynamic cars, kinematic garage preview, and LAN ghosts uniformly — the wheel viz has already been posed by `StepPhysics`/`PoseWheelVisualsFromConfig`/`ClientCarView`) and once from `PoseWheelVisualsFromConfig`. On track the hub moves with compression, so the strut visibly stretches/tilts — the spring working.

## Data, mirror, factory

- `WheelSpec` (`Garage/VehicleDesign.cs`) + `CarWheelConfig`: add `public float suspLength = 0f;` (field initializer → old JSON legacy).
- `VehicleFactory.Build` wheel-config copy (`:73`): copy `suspLength`.
- `SymmetryUtil.MirrorInto(WheelSpec)`: plain-copy `suspLength` (side-symmetric like the other susp fields).

## Garage inspector, stats, mass, presets

- `GarageUI.DrawWheelInspector` Suspension block (`:1137`): add `w.suspLength = Slider("Strut len mm", w.suspLength*1000, 0, 60)/1000` (0 = rigid); relabel `"Susp angle°"` → `"Strut angle°"`; add an effective-values readout when `L>0`: `→ rate {EffectiveRate:0} N/m · travel {EffectiveTravel*1000:0} mm`. Live rebuild repositions the wheel + strut.
- `VehicleStats.Compute` (`:55-68`): feed `EffectiveRate`/`EffectiveTravel` into the ride-frequency/sag averages so the readout reflects the strut.
- `MassProperties.Compute`: place the wheel point-mass at the **hub** (`localPos + HubOffsetLocal(...)`) so composite CoM accounts for the dropped wheel.
- Presets: `VehicleDesign.Default()` (stock car) + `VehiclePresets` Rally Buggy / Crawler / Real Twin set `suspLength = NominalArm` (Rally Buggy/Crawler a touch longer for soak) with each wheel's `localPos` = desiredHub − `HubOffsetLocal(...)` so hubs stay put (angle 0 → just raise y). Other presets stay 0 (rigid) — still valid, user can add struts.

## Files

New: `Vehicles/SuspensionGeometry.cs`.
Modified: `Vehicles/CarVehicle.cs` (HubLocal, MakeWheel motion-ratio, PoseWheelVisualsFromConfig, StrutTiltZ delegate, Wheel.strut/mountLocal, LateUpdate/UpdateStruts), `Vehicles/CarWheelConfig.cs`, `Vehicles/PartVisualFactory.cs` (BuildStrutViz), `Garage/VehicleDesign.cs` (WheelSpec.suspLength + Default mounts), `Garage/VehicleFactory.cs`, `Garage/SymmetryUtil.cs`, `Garage/GarageUI.cs`, `Garage/VehicleStats.cs`, `Garage/MassProperties.cs`, `Garage/VehiclePresets.cs`, `README.md`, memory `project-overview.md`.

## Steps (headless compile checkpoint after each)

1. **Geometry + physics core** — SuspensionGeometry; `suspLength` on WheelSpec+CarWheelConfig; VehicleFactory copy; SymmetryUtil mirror; CarVehicle HubLocal + MakeWheel motion-ratio + PoseWheelVisualsFromConfig. (Wheels reposition with length/angle; no strut mesh yet.)
2. **Visible strut** — BuildStrutViz; Wheel.strut/mountLocal; build in MakeWheel; LateUpdate→UpdateStruts + call in PoseWheelVisualsFromConfig.
3. **Inspector + stats + mass + presets** — length slider + effective readout + relabel; VehicleStats/MassProperties effective+hub; Default/RallyBuggy/Crawler/RealTwin opt-in with raised mounts.
4. **Docs + validate + relaunch** — README suspension paragraph, memory Iteration 16, headless compile (0 `error CS`), editor relaunch.

## Risks

- **RC-scale WheelCollider stability** (the flagged risk): mitigated by deriving the damper from the *effective* spring (ζ preserved at any length) and clamping `EffectiveRate` [50,4000]/`EffectiveTravel` [0.005,0.12]; play-test the stock car + a long-arm Crawler for shimmy before shipping.
- **Back-compat**: `suspLength = 0` on all old JSON → identical build (hub=localPos, spring/travel unchanged, no strut); `Default`/presets at `NominalArm` → ratio 1 → identical numbers, hubs raised-mount so nothing visibly moves except the added strut.
- **Ghost struts on kinematic/LAN cars** update from `w.viz.position` in `LateUpdate` (rest pose) — struts render correctly at rest without needing StepPhysics.
- **z-invariance of the tilt** keeps Ackermann/wheelbase/anti-roll unaffected by the mount-vs-hub distinction (only x/y move).

## Verification

Headless compile after each step; editor relaunch at end. Play-test: (1) old saved vehicle drives/looks identical (all wheels length 0). (2) Stock car now shows a coil-over strut on each wheel from hub up to the body; wheels sit where they did. (3) Wheel inspector: raise **Strut len** → the wheel drops/extends and the readout shows a softer rate + more travel; change **Strut angle** → the wheel swings out and leans, strut follows, mirror twin mirrors. (4) Drive over the speed bump → struts visibly compress/extend with the wheels; a long-arm Crawler soaks bumps (more travel, softer) vs a short-arm stiff setup. (5) Stats ride-freq/sag reflect the strut length; composite CoM shifts slightly when wheels drop. (6) Spawn on track + as a split-screen/LAN ghost → struts present in all viz contexts; save/reload round-trips `suspLength`.
