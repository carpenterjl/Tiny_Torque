# Authoring and replacing game assets

How the game actually loads meshes, what it demands of them, and the reference
geometry for the things that have no mesh yet (the aircraft).

Written against the code as of 2026-08-02. The authoritative files are
`Vehicles/PartMeshLibrary.cs`, `Vehicles/PartVisualFactory.cs`,
`Vehicles/CarVehicle.cs` (`BuildBodyVisual`) and
`Core/Flight/DebugPlaneRig.cs`.

---

## 1. What the game loads, and from where

There is exactly one asset-backed visual path: `PartMeshLibrary`. Everything
else in the project is built from Unity primitives at runtime.

```
UnitySim/Assets/Resources/PartModels/<key>.fbx     vehicle parts
UnitySim/Assets/Resources/TrackProps/<key>.fbx     track scenery
```

Loading is `Resources.Load<GameObject>("PartModels/" + key)` — **by path, not by
GUID**. Consequences:

- The file must sit under a folder literally called `Resources`, and its
  **filename is the key**. `body_patrol.fbx` is found because something asks for
  `body_patrol`.
- The `.meta` file matters only for *import settings* (scale factor, normals,
  material import mode). Copying it across from another project is fine and
  usually what you want; its GUID being foreign changes nothing, because nothing
  references these by GUID.
- A missing key is not an error. `PartMeshLibrary.Load` logs one warning and
  caches the miss, then the caller silently builds primitives instead. If your
  new car looks like a grey box, check the Console first.

Every instance is **hard-sanitised** on load (`PartMeshLibrary.Sanitise`): all
`Collider`s and `Rigidbody`s are destroyed and the whole hierarchy is forced onto
one layer. Imported geometry can never participate in physics. Collision shapes
come from code, always.

### Axes and units

Metres, `+Y` up, `+X` right, **`+Z` forward**. (Blender's usual Unity FBX preset —
"-Z forward, Y up" — produces exactly this.) Verified against every placement in
the project: the trainer's prop is at `z = +0.50`, its nose gear at `z = +0.36`;
the police car's front ToF is at `z = +0.22` and its light bar at `z = −0.02`.

---

## 2. The car body contract

`CarVehicle.BuildBodyVisual` → `BodyMeshKey(bodyShape)` → `TryInstantiate`.

| BodyShape | key |
|---|---|
| Shell / LowRacer / Buggy | `body_shell`, `body_lowracer`, `body_buggy` |
| Coupe / Baja / Patrol | `body_coupe`, `body_baja`, `body_patrol` |
| Rattle / Redline / Highwing / Autopia | `body_rattle`, `body_redline`, `body_highwing`, `body_autopia` |
| Tiguan | `body_tiguan` |
| Box / Wedge | *(none — always primitives)* |

### Scale

Every arcade shell is authored to **0.20 × 0.10 × 0.42 m** (`BodyMeshAuthorSize`)
and is rescaled at spawn by `bodySize / BodyMeshAuthorSize`, per axis. Author to a
different size and the car is stretched by the ratio.

The Tiguan is the one exception: authored 1:1 and given `Vector3.one`, because its
renderer bounds (2.099 × 1.472, carrying mirrors and roof rails) are deliberately
*not* its collision box (1.839 × 1.443).

### Materials — the part that trips people up

**The FBX's own materials are ignored.** `AssignByName` walks every renderer and
rebinds `sharedMaterial` from a code-side table, matched by **case-insensitive
substring of the GameObject's name**. This is deliberate: one lighting/theme/
recolour system, and the garage painter needs to know which renderers are paint.

So the shipped shells are exported **split by material, one object per material,
renamed to a token**:

```
patrolpaint_1  chrome_1..6  dark_1..10  decal_1..6  gold_1..4
glass_1  gunmetal_1  white_1  barwhite_1  barwhite_2
```

Binding rules, in `CarVehicle.BindBodyMesh` (which picks the table via
`CarVehicle.BodyAccentTable` and runs `PartVisualFactory.BindByToken`):

- A name starting with `paint` gets the **tintable body material** and is
  registered in `PaintRenderers` (bodyColor, livery, garage paint mode).
- Otherwise the first matching token in `PartVisualFactory.AccentTokens` wins.
- No match → falls back to the body material, i.e. it comes out flat and
  tintable. This is why a wrongly-named export renders as *a plausible wrong
  colour*, never magenta, and never an error.

**Token order matters and nothing can catch a mistake.** First-match substring
means every compound token must precede the token it contains — `barwhite`,
`whitewall` and `hwwhite` before `white`; `redgold` before `gold`; `rustpaint`
before `rust`; `autoglass` before `glass`. Two of those really were wrong the
first time.

---

## 3. The wheel contract

Wheels are **separate FBXs, one per style**, instantiated per corner into a holder
the suspension drives. They must not be part of the body mesh.

- Key: `wheel_<style>` — `slick knobby rally coupe baja patrol rattle redline
  highwing autopia tiguan tiguan_r`.
- **Axle along local +X.** Rim face toward **+X**, brake disc behind it; the
  builder spins the mesh 180° about Y on the side where +X points inboard.
- Authored radius **0.033 m** (66 mm RC tyre); scaled by `radius / 0.033`. The
  Tiguan's two are the exception at **0.349 m** — its *loaded* centre height, not
  its free radius, because 0.349 is also the number the WheelCollider gets.
- Tokens (`PartVisualFactory.WheelTokens`): `tire`/`tyre`, `rim`, `hub`, `stud`,
  `brake`, plus finish tokens. Same ordering hazard: `redtrim` and `hwtrim` both
  contain `rim`, and `hubcap` contains `hub`.
- Anything named tyre-family is kept lit when a cosmetic rim hides the stock one
  (`HideStockRim`), and `CosmeticProbe` hard-fails if that regresses.

---

## 4. Bringing in a textured export (the `TinyTorqueTests/POLICE` case)

That export is geometry + real `.mat` assets + BaseColor/MetallicSmoothness/
Emission/Normal PNGs + an `export.json` manifest, with objects under their Blender
names (`Police_Body`, `Police_Roof`, `_spotlens.001`, …).

Dropped into `Resources/PartModels/` as-is it would load, but:

- **None of those names contain a token.** Every renderer would miss, fall through
  to the body material, and the car would arrive correctly shaped in one flat
  colour.
- The `.mat` assets and textures would never be read, because `AssignByName`
  overwrites `sharedMaterial` on every renderer.

Three ways forward, in increasing order of work:

**A. Speak the existing language.** Split by material and rename the objects to
tokens (`patrolpaint_1`, `chrome_1`, …), exactly as the current exporter does.
Zero code change; drop the FBX in and it works. You lose the textures — the look
comes from the constants in `PartVisualFactory`.

**B. Manifest-driven materials (the Tiguan precedent).** `TiguanMaterials` builds
its token table at runtime from `Resources/PartModels/tiguan_materials.json`,
which is the same shape as your `export.json` but flat-valued. Give the objects a
unique name prefix, add a `BodyShape` entry, and build the table from the manifest
— including `mainTexture` from the PNGs, which the Tiguan path doesn't do yet.
Materials still come from data rather than from the FBX, so the project's rule
holds. This is the honest fit for a textured export.

**C. Keep the FBX's own materials.** A third branch in `BuildBodyVisual` that
returns before `AssignByName`. Smallest diff, but it opts that car out of
bodyColor, livery and garage paint mode, and puts its look outside the one system
the project keeps it in. Fine for a one-off; a bad default.

Whichever route: the police body is **already in the game** as `BodyShape.Patrol`,
from the same `TinyTorque_police.blend`. Overwriting `body_patrol.fbx` replaces it
everywhere (including the Patrol preset and any saved design that selected it);
adding a new `BodyShape` member leaves the old one alone. Enum members are
append-only — saved designs store the int.

And the wheels: your export has none. `wheel_patrol` still ships separately and
still needs its own FBX under the §3 contract.

---

## 5. Aircraft — there are no mesh assets

The RC trainer and the Hydra are built **entirely from primitives** by
`DebugPlaneRig`: a cube fuselage, one flat cube per lifting surface, spheres for
gear, a disc for the prop, cubes and cylinders for the jet's intakes and nozzles.
There is no `plane_*.fbx` to hand you, and no code path that would load one.

That is on purpose, and it is the constraint any replacement model inherits:

> the geometry comes from the same `LiftingSurface` records the aerodynamics
> reads. There is no separate visual description that could disagree with the
> physics: if the wing you can see has 5° of dihedral, it is because the wing the
> model flies has 5° of dihedral.

So a hand-made mesh is **cosmetic only** — it changes nothing about how the
aircraft flies. Model to the numbers below and the two agree; model something
else and the aeroplane flies like the table, not like the picture.

Two further traps if a mesh path is added:

- The **fuselage cube keeps its collider** — it is the crash body. Gear spheres
  keep theirs too. `PartMeshLibrary` strips colliders from everything it loads, so
  a plane mesh has to be added *beside* the primitives with their renderers
  disabled, not in place of them.
- The jet's nozzle cylinders carry `JetNozzleVisual` and turn with the live
  `PlaneVehicle.NozzleDeg`. A replacement needs the same component on the same
  pivots or the nozzles stop agreeing with the thrust vector.

### 5.1 RC Sport Trainer — `DebugPlanes.SportRc()`

All-up mass **2.00 kg**. Local frame, metres.

| Piece | Placement | Size |
|---|---|---|
| Fuselage (collider) | centre `(0, 0, −0.05)` | `0.09 × 0.11 × 1.05` → nose `z +0.475`, tail `z −0.575` |
| Propeller disc | `(0, 0, 0.50)`, normal +Z | Ø `0.254` (10 in), 4 mm thick |
| Wing | root ¼-chord `(0, 0.06, 0)` | span `1.40`, chord `0.24` constant, AR 5.83 |
| Tailplane | root ¼-chord `(0, 0.01, −0.60)` | span `0.50`, chord `0.148` |
| Fin | root ¼-chord `(0, 0.02, −0.60)` | height `0.20`, chord `0.133` |
| Nose gear | `(0, −0.16, 0.36)` | sphere r `0.030` |
| Main gear | `(±0.13, −0.16, −0.06)` | sphere r `0.030` |

Wing angles: dihedral **5°**, sweep **0°**, incidence **2.5°**, washout **−2°**
(tip stalls last). Ailerons 50–95 % span, 25 % chord, ±15°. Elevator 40 % chord
±25°. Rudder 40 % chord ±25°. High wing — `WingY 0.06` sits it above the fuselage.

### 5.2 Hydra VTOL — `DebugJets.HydraVtol()`

All-up mass **600 kg**, max thrust **9 000 N** (T/W ≈ 1.53). Half-scale Harrier
class. Valid below M 0.3 — the panel model is incompressible.

| Piece | Placement | Size |
|---|---|---|
| Fuselage (collider) | centre `(0, 0, −0.35)` | `0.95 × 1.05 × 6.90` → nose `z +3.10`, tail `z −3.80` |
| Wing | root ¼-chord `(0, 0.30, 0)` | span `4.60`, root chord `1.49`, tip `0.68`, S 4.99 m², AR 4.24 |
| Tailplane | root ¼-chord `(0, 0.10, −3.40)` | span `2.10`, root `0.50`, tip `0.30` |
| Fin | root ¼-chord `(0, 0.40, −3.30)` | height `0.95`, root `0.85`, tip `0.45` |
| Intakes | `(±0.655, 0.05, 1.10)` | `0.36 × 0.55 × 1.30` |
| Nozzles, fore pair | `(±0.575, −0.35, +0.28)` | Ø `0.26`, length `0.44`, swivels |
| Nozzles, aft pair | `(±0.575, −0.35, −1.52)` | Ø `0.26`, length `0.44`, swivels |
| Nose gear | `(0, −1.00, 2.20)` | sphere r `0.16` |
| Main gear (tandem) | `(0, −1.00, −1.30)` | sphere r `0.16` |
| Outriggers | `(±2.10, −1.00, −1.48)` | sphere r `0.16` |

Wing sweep **34°**, anhedral **−8°**. Tailplane sweep **30°**, anhedral **−12°**
(the Harrier's drooped tail). Nozzle travel 0–95° — past 90° is viffing, and it
comes free from the geometry.

Note the nozzle stations are **symmetric about the CG on purpose**: balanced hover
is geometry, not a trim constant. Moving them in a model changes nothing; moving
them in `JetSpec` changes the hover.

---

## 6. Track props

`Resources/TrackProps/<key>.fbx`, loaded with `root: PropRoot`. Unlike parts,
props have **no runtime scale contract** — they are placed at their authored size
and validated on extent and triangle budget. They are instantiated onto the
parent's layer rather than the viz layer, so the on-car camera sensor can see
scenery.
