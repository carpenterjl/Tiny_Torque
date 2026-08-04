# Track Studio — hand-authored scene tracks

Track Studio is the Unity-Editor side track builder. It makes a **real Unity scene**
— terrain, ProBuilder geometry, spline roads, pack prefabs — into a track the game
can load and race.

It does not replace the in-game Track Builder (`TrackBuilderScene` / `TrackBuilderUI`),
which authors `TrackDesign` tile maps. The two are different track *sources* and both
are live. See "Which builder do I want?" below.

---

## The three track sources

`TrackBootstrap.BuildEnvironment` dispatches on, in order:

| Source | Selected by | Data | Editable in-game | Over LAN |
|---|---|---|---|---|
| Scene track | `GameFlow.ActiveSceneTrack` | the scene itself | no | by **name** |
| Tile map | `GameFlow.ActiveTrack` | `TrackDesign` JSON | yes | as **JSON** |
| Classic oval | both null | procedural | no | n/a |

The two `GameFlow` properties are mutually exclusive by construction — assigning
either clears the other — so a stale value cannot quietly load the wrong track.

---

## Which builder do I want?

**In-game Track Builder** if you want a map that saves to JSON, opens on any machine,
travels the LAN wire as data, round-trips through a resume snapshot, and can be edited
by a player. The cost is that it can only express floor tiles, catalog props and
spline ribbons.

**Track Studio** if you want terrain, custom meshes, authored lighting or baked
lightmaps. The cost is the mirror image: the track ships inside the build, is
identified across the wire by name, and cannot be opened in the in-game builder.
A LAN client that does not have the scene is **refused with a message** rather than
being dropped onto the oval and desynced silently.

---

## Start from a template

`Tools ▸ AIHWSim ▸ Mode Templates` holds one already-built scene per mode — simulation
physics, arcade physics, free roam, lapped race, point-to-point race, soccer, capture
the flag, demolition derby and RC plane. Open one, press Play, and it runs the mode it
is named after with the opponents its `LevelSettings` asks for. Each contains the
minimum that mode actually needs and nothing else: a flattened-cube floor with a
`SurfaceTag`, a sun and a skybox, a kill plane under the world, the descriptors, the
grid, and whatever the mode itself requires — gates and a spline for the races, goal
and ball markers for soccer, base markers for CTF, pickup markers for the derby, an
airspace box for the aircraft.

**Copy one before you build a real level in it.** The templates are *generated*:
`Create All Template Scenes` rebuilds every one of them from
`Editor/ModeTemplates/ModeTemplateBuilder.cs` and replaces anything edited by hand.
That is what stops nine scene files drifting away from the code they demonstrate, and
it is the same bargain the ten physics-test scenes make. `[TPL]`
(`Validate Templates`, or `ModeTemplateValidator.Report` headless) is the gate: it
opens all nine and checks the mode matches the name, the grid is dense, the race gates
form a lap, an arena has its playfield, there is exactly one sun and nothing reloaded
as a Missing Script.

None of the templates is in Build Settings or `SceneTrackCatalog`. They are tools, not
levels; the in-game track pickers are unchanged. A copy you intend to ship needs both,
as any scene track does.

### Each scene owns its settings

A `DrivingSceneDescriptor` points at *assets*, and Save As copies the scene but not what
it points at. Left alone, that means saving a template as `Arcade_Test_Scene` and setting
it to three laps sets the *template* to three laps — and every other scene ever saved from
it — and the next `Create All Template Scenes` writes the template's own values back over
yours. Neither step says anything, which is the part that costs you an evening: the file
you edited is not the file you were looking at.

So an asset's owner is read from where it sits, and a scene saved under a name that does
not own what it points at takes private copies on the way out:

| Where the asset lives | Who owns it |
|---|---|
| `Assets/Settings/Driving/*.asset` | **shared on purpose** — the `_Default` assets. One project-wide answer to how the physics steps. Never cloned behind your back. |
| `Assets/Settings/Driving/Templates/` | the mode templates, which regenerate |
| `Assets/Settings/Driving/Scenes/<SceneName>/` | that scene, alone |

The clone happens in `sceneSaving`, before the file is written, so the save that renames
the scene is the save that gives it its own rules — and it carries the values verbatim, so
nothing about the scene changes except which file it edits. Only the **rules** are taken
automatically; world tuning (physics, assists, mode and arcade numbers) stays on the shared
defaults, because a scene pointing at `PhysicsSettings_Default` is not a mistake. When a
scene does need its own feel, the descriptor's inspector button — or
`Driving Scene ▸ Give This Scene Its Own Settings` — takes copies of all five.

The inspector names the owner of anything this scene does not own, because an object field
renders a shared asset and a private one identically.

---

## Making a track

1. **Open or create a scene** and build geometry however you like.
2. **`Tools/Track Studio/1. Make this scene a track`** — adds a `SceneTrackDescriptor`,
   seeds a spawn marker, and (if the scene has terrain) creates a `TerrainFloorTable`
   seeded from the layers actually present. *Review the guessed floor types.*
3. **Add a road**: `Add spline road` in the Track Studio window drops a
   `SplineContainer` + `TrackSplineAuthoring`. Shape it with Unity's Spline tool.
   Width, banking and surface are `SplineData` channels on the authoring component,
   so they are keyed along the curve.
4. **`2. Bake ribbon + corridor`** — generates the road mesh, its per-surface
   colliders and `SurfaceTag`s, and bakes the centreline bots follow.
   The ribbon itself rebuilds live from then on (`TrackSplineAuthoring.liveRebuild`,
   on by default): move a knot or a channel key and the mesh follows on the frame the
   edit commits, debounced past the drag because re-cooking a `MeshCollider` per
   mouse-move stutters. **The corridor and the racing line are not live** — they are
   solved artifacts, so finish a shaping session with this bake and re-bake the line.
5. **Place gates**: `Add checkpoint` / `Add finish` / `Add player spawn`. Select a marker and
   use **Snap to road** — a gate spans its own local X and cars travel through its
   local **+Z**, so a marker rotated by eye is the classic "my lap never counts" bug.
6. **`3. Renumber checkpoints`** — orders must be a dense `0..n-1` run.
   `LapTimer.NotifyCheckpoint` only advances on an exact index match and refuses the
   line until every checkpoint is hit, so one gap makes the track permanently
   un-lappable with no error anywhere.
6b. **The start grid.** One spawn marker is enough: the field is laid out from it in a
   staggered two-wide row, exactly as a tile map does. Add more to author the grid
   yourself — `gridOrder` 0 is player 1, and slot *N* starts on the marker with
   `gridOrder == N`, so the row can follow the road instead of being projected straight
   back from pole. Author fewer markers than the field and the remainder falls back to
   the procedural row. `3b. Renumber spawns` makes the slots dense and renames each
   object to `Player N Spawn`.

   **Markers must each live in their own .cs file, named after the class.** Unity creates
   one `MonoScript` asset per file, so a marker sharing a file has none to reference; the
   scene then serialises `m_Script` as a fileID pointing at a stub embedded in the scene,
   which reloads as Missing Script and is invisible to `GetComponentsInChildren`. That is
   silent, and it compounds: the setup step sees no spawn and seeds another one every run.
   `Repair markers` cleans up after it and the validator fails on it.

7. **Register it**: add a row to `SceneTrackCatalog.All` and add the scene to
   Build Settings. Both are required — a catalog row with no build entry is a picker
   item that loads a black screen, which is exactly what the validator checks.

---

## Surfaces

`SurfaceMap.At` resolves a wheel contact in this order:

1. **`SurfaceTag`** on the hit collider — spline ribbons and painted meshes.
2. **Terrain** — the alphamap's dominant layer, mapped through the scene's
   `TerrainFloorTable`.
3. **Tile floor slab** — positional lookup, tile maps only.
4. **Scene fallback** — `SceneTrackDescriptor.sceneFallbackFloor`, default asphalt,
   so a road you forgot to tag drives like a road instead of like nothing.

**Physics Material Brush** (`Tools/Track Studio/Surface Brush`) paints all three:
terrain into the alphamap, a spline road into its **surface channel**, anything else as
a `SurfaceTag`. One undo entry per stroke per object, because a terrain alphamap undo
copies the whole map.

**A road is painted into the spline, not onto the ribbon.** The ribbon's colliders are
destroyed and recreated by every `Bake`, which now runs live on every knot drag — so a
`SurfaceTag` stamped on a ribbon collider survives until the next edit and then silently
vanishes. The brush projects the cursor onto the curve instead and writes keys into
`surfaceChannel`, so the brush radius becomes an arc-length half-span: a 1.5 m brush
paints a 3 m run. Each stamp lays a hard-edged run (four keys — a step pair at each end)
and a compaction pass drops keys that carry no information, or a two-second drag would
leave a channel of several hundred. The ribbon rebuilds **once, when the stroke ends**;
during the drag the road's centreline is drawn in its own surface colours so you can see
where the paint is landing.

**Brush controls** mirror what terrain painting gives you:

| | |
|---|---|
| Shape | Circle, or Square — the only way to paint a straight edge without stair-stepping it out of discs |
| Size / Rotation / Hardness / Strength | footprint, turn about the surface normal, solid core fraction, alphamap weight per stamp |
| Spacing | minimum gap between stamps, in brush diameters; 0 stamps every mouse event |
| Scatter | random offset from the cursor in the surface plane, in radii |
| Jitter | per-stamp randomisation of size, rotation and strength |

Strength and hardness blend alphamap weights, so they shape **terrain only** — a floor id
is discrete, and a road run or a `SurfaceTag` is either painted or not. Size does nothing
on a mesh either: a `SurfaceTag` applies to the whole collider, and splitting a mesh into
painted regions is a modelling decision rather than a brush one.

Nothing is unpaintable. A terrain with no `TerrainLayer` for the chosen floor gets one
— `TerrainLayerLibrary` reuses the layer the scene's table already names for that
floor, or generates a `.terrainlayer` from `FloorTypeDef.Tex` so the terrain looks like
the tiles of the same surface — then writes the table row and appends the layer to the
terrain. `terrain.Flush()` after every stamp, or the splat textures lag the stroke and
painting reads as broken. The window's **Paint targets** list is built from the scene's
root objects and is the one thing that can stop a stroke; a target switched off draws
the brush red and names itself under the cursor.

Terrain alphamaps are baked to one floor id per texel **once**, at bind. `At` runs
~12 800 times a second on an eight-car grid at 400 Hz, so a per-call `GetAlphamaps`
would allocate that many managed arrays a second. The bake time is logged.

**A consequence worth knowing:** `frictionMult` doubles as the arcade track-limit
classifier against `ArcadeConfig.OffTrackFrictionThreshold` (0.90). Painting grass
(0.85) makes it count as off-track for free. Painting a whole terrain dirt (1.00)
means track limits **never trigger anywhere**. That is a property of the paint job.

---

## Road lines

`Track Spline Authoring ▸ Lane markings` paints lines on the ribbon: a centre line, a
double line, an optional pair of edge lines, solid or dashed. They rebuild live with
everything else on that component, so a marking is something you look at rather than
something you bake and then check.

| | |
|---|---|
| Centre lines | 0 = none, 1 = a single centre line, 2 = a double line |
| Edge lines | a line just inside each edge of the road, following the width channel |
| Width | one painted line, in metres (0.05 default — 50 cm of real road at 1:10) |
| Spacing | the gap between the two lines of a double, **and** how far an edge line sits in from the edge |
| Dash / Dash gap | metres. Dash 0 paints a solid line |
| Colour | the paint |

The in-game track builder has the same controls under **ROAD LINES** in its spline panel,
with a fixed palette instead of a colour picker, and the style is saved in the track JSON.
Old saves carry no `lines` key at all and deserialize to "none", so every track already on
disk builds the road it always built.

**Lane paint is its own mesh, with no collider and no `SurfaceTag`.** Two reasons, both
worth keeping. The ribbon's surface runs are not touched by this feature at all, so a road
with no markings is inert by construction rather than by a test. And a line down the
middle of the road is exactly where the wheels are: 5 mm of paint in a `MeshCollider` is
suspension input, not decoration. (The kerb stripes DO ride in the collider — tolerable
only because they sit at the outer edge, where a car is already having a bad time.)

Dashes come from an alpha-cutout mask whose v axis is one dash period, not from dropping
quads. Ribbon sampling is 0.4 m, which at this scale is longer than most dashes, so a
geometric dash could only ever be a rumble strip. On a closed loop the period is stretched
to the nearest whole number of dashes so the seam lands on a boundary instead of showing
one short dash wherever the start line happens to be.

---

## Racing line

`4. Bake racing line` solves the ideal line inside the corridor and puts a
friction-limited speed profile on it. The Scene view then draws it coloured by speed,
with apex markers and brake-zone bars.

- **Line**: minimum curvature over lateral offsets, by projected SOR. A closed loop
  makes the system cyclic, which SOR handles by indexing modulo *n*; the corridor
  bound makes it a QP, and clamping after each coordinate update *is* projected
  Gauss-Seidel.
- **Shortest-path blend** (default 0.15): pure minimum curvature runs wide on every
  corner exit, which is wrong for 1:10 cars on tight circuits where the following
  straight is shorter than the arc saved.
- **Profile**: `v = sqrt(g·µ/|κ|)` with banking, then a forward pass bounded by drive
  accel and a backward pass bounded by braking, both on the friction ellipse,
  iterated to a fixed point on a loop.

**Nothing in the shipped game reads the baked line.** `BotDriver` is untouched, so
race feel, lap times and difficulty tiers are exactly what they were. Wiring bots to
follow it is a gameplay change to make deliberately.

### Calibration

`5. Calibrate racing line (play mode)` drives the line for three laps and fits four
scalars — grip scale, standing acceleration, top speed, brake usage — which describe
the **car**, so they transfer to every track built with it.

Lap 1 warms up and is discarded, lap 2 measures, **lap 3 proves lap 2**. Surface
roughness is a deterministic positional noise field, so two laps of the same line must
agree within 20 ms; if they do not, the run was not deterministic and the fit would be
noise, so it **fails** rather than writing it.

```bash
"E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -projectPath "E:\EE Projects\Tiny_Torque\UnitySim" -executeMethod AIHWSim.TrackTools.RacingLineCalibrationRunner.Report -trkScene TTA_Sandbox -logFile cal.log
```

No `-quit` (play mode must keep the process alive; the watcher exits it) and no
`-nographics` (the car carries a camera sensor).

---

## Sectors

`6. Sector configurator` slices the line into sectors at even spacing or before each
apex, with targets integrated from the same profile the lap prediction used — so they
sum to the predicted lap by construction.

`SectorTimer` records splits locally by projecting each car onto the line at 10 Hz.
It deliberately **does not touch `LapTracker`**: that type is `[Serializable]`,
round-trips through session snapshots and rides the LAN protocol, so adding a
`float[]` to it would be a snapshot change and a wire change for what is a
presentation concern.

Exactly three telemetry channels are registered — `race/sector`, `race/sector_time`,
`race/sector_delta` — not one per sector. Sector count is track-dependent and
`CsvLogger` freezes its column layout from the registered set.

---

## Validation

```bash
"E:\Unity Hub\Editor\6000.1.15f1\Editor\Unity.exe" -batchmode -quit -projectPath "E:\EE Projects\Tiny_Torque\UnitySim" -executeMethod AIHWSim.TrackTools.TrackStudioValidator.Report -logFile trk.log
```

Then grep for `[TRK] RESULT`. `-quit` is correct here — unlike the calibration runner
this is edit-mode only.

Checks: catalog rows resolve to scenes that are in Build Settings; descriptor present
and kind matching; Arena refused; spawns present and not overlapping; Circuit has a
finish and dense checkpoint orders; gates at least as wide as the road; every
`SurfaceTag` and table row in floor range; every `TerrainLayer` mapped; racing line
fresh (**the stale-bake case is the most likely real failure**), inside the corridor,
and honest about its calibration; `PathCurvature` still agrees with `BotDriver` on a
circle of known radius; and the solver reproduces bit-for-bit across two identical
solves.

---

## Deliberate limits

- **An Arena scene track needs a `playfield` collider.** `ArenaNav` takes the arena's
  centre, its radius and its "has the ball escaped" test from
  `BuiltTrack.floorCollider.bounds`, so a scene claiming `TrackKind.Arena` has to name
  the collider that IS its floor. Assign it to the descriptor's **Playfield** field;
  the Track Studio window and `[TRK]` both refuse an Arena without one. Circuit and
  FreeRoam leave it empty, which is every scene that existed before this.
- **`BuiltTrack.floorCollider` is null** on a scene track unless `playfield` says
  otherwise. `ArcadeDirector` and `TrackRespawn` already fall back to
  `Physics.RaycastAll`, so they cope either way.
- **Protocol v15.** A scene track crosses the wire as a name. A v14 client would read
  an empty `trackJson`, conclude "classic oval", and exchange perfectly well-formed
  position updates about a track nobody else is on.
- **The Unity-spline → `SplineSpec` conversion resamples**, it does not copy knots.
  Bezier tangents are approximated by dense Catmull-Rom; `spacing` is the fidelity
  knob. This is what lets `RibbonMeshBuilder` stay completely unmodified, so tile-map
  ribbons are byte-identical by construction rather than by test.
- **`PathCurvature` and `RaceLineSolver` duplicate constants and maths from
  `BotDriver`** so `BotDriver` could stay untouched. The validator checks them against
  each other.
