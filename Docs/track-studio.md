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

- **Arena scene tracks are refused.** `ArenaNav.Drop` reads
  `BuiltTrack.floorCollider.bounds` with no raycast fallback, and a hand-authored
  scene has no floor slab. Circuit and FreeRoam only.
- **`BuiltTrack.floorCollider` is null** on a scene track. `ArcadeDirector` and
  `TrackRespawn` already fall back to `Physics.RaycastAll`, so they cope.
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
