# Authoring and replacing game assets

How the game actually loads meshes, what it demands of them, and the reference
geometry for the things that have no mesh yet (the aircraft).

Written against the code as of 2026-08-02. The authoritative files are
`Vehicles/PartMeshLibrary.cs`, `Vehicles/PartVisualFactory.cs`,
`Vehicles/CarVehicle.cs` (`BuildBodyVisual`), `Vehicles/BodyCatalog.cs` and
`Vehicles/WheelCatalog.cs` (which rows exist), `Vehicles/AssetManifests.cs`
(§4's runtime half), `Editor/AssetStudio/` (§4's tool) and
`Core/Flight/DebugPlaneRig.cs`.

---

## 1. What the game loads, and from where

There is exactly one asset-backed visual path: `PartMeshLibrary`. Everything
else in the project is built from Unity primitives at runtime.

```
UnitySim/Assets/Resources/PartModels/<key>.fbx     vehicle parts
UnitySim/Assets/Resources/TrackProps/<key>.fbx     track scenery
UnitySim/Assets/Resources/Cosmetics/<key>.fbx      unlockable cosmetics + crates
```

An asset may also ship a **manifest** beside its FBX —
`<key>_asset.json`, same folder — which is what §4 is about. 207 of the 207
assets in the project today ship none, and that is the normal case: a manifest
is how art that arrives from *outside* the repository brings its own materials,
its own scale correction and its own catalogue row with it.

Loading is `Resources.Load<GameObject>("PartModels/" + key)` — **by path, not by
GUID**. Consequences:

- The file must sit under a folder literally called `Resources`, and its
  **filename is the key**. `body_patrol.fbx` is found because something asks for
  `body_patrol`.
- For an FBX the `.meta` matters only for *import settings* (scale factor,
  normals, material import mode). Copying it across from another project is fine
  and usually what you want; its GUID being foreign changes nothing, because
  nothing references a mesh by GUID. **The exception is a `.mat` and its
  textures** — a material references its maps by GUID, so those `.meta` files
  have to travel with them, and two copies of one export under different keys
  duplicate the GUIDs. Unity silently reassigns one side and every texture link
  on the loser comes out unresolved, which looks exactly like an importer bug.
  §4's commit pipeline refuses that by name rather than letting it happen.
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

`CarVehicle.BuildBodyVisual` → `BodyCatalog.Resolve(bodyKey, bodyShape)` →
`TryInstantiate`.

**The key is a string and the table is data.** A design carries `bodyKey`
("body_patrol") alongside the legacy `bodyShape` int, and every consumer —
the mesh, the drag coefficient, the token table, the scale rule, the garage
picker — reads a `BodyCatalog` row rather than a `switch`. The int is still
written to every save for downgrade legibility; the key wins when the two
disagree.

| row | key |
|---|---|
| Shell / LowRacer / Buggy | `body_shell`, `body_lowracer`, `body_buggy` |
| Coupe / Baja / Patrol | `body_coupe`, `body_baja`, `body_patrol` |
| Rattle / Redline / Highwing / Autopia | `body_rattle`, `body_redline`, `body_highwing`, `body_autopia` |
| Tiguan | `body_tiguan` |
| Box / Wedge | *(none — always primitives)* |

That is `BodyCatalog.Seed`, the shipped table. `BodyCatalog.All` is the seed
plus one row per discovered manifest (§4), and a seed row always wins a name
collision — a committed asset can never redefine a shipped car out from under
a save that names it.

### Scale

Every arcade shell is authored to **0.20 × 0.10 × 0.42 m** (`BodyMeshAuthorSize`)
and is rescaled at spawn by `bodySize / BodyMeshAuthorSize`, per axis. Author to a
different size and the car is stretched by the ratio.

**That constant is a nominal divisor, not a measurement of any shell.** The real
shells land between 0.17 and 0.20 wide, because the old exporter scaled them to
length 0.420 with a single *uniform* factor and left width and height as
consequences — which is exactly why `PartModelValidator` pins those bodies'
length and leaves their width free. Do not "correct" the divisor to a measured
size: dividing by real extents stretches a correctly proportioned car to fill a
box no shell actually fills.

The Tiguan is the one exception: authored 1:1 and given `Vector3.one` (its row
sets `unscaled`), because its renderer bounds (2.099 × 1.472, carrying mirrors
and roof rails) are deliberately *not* its collision box (1.839 × 1.443).

### Materials — the part that trips people up

**For an asset with no manifest, the FBX's own materials are ignored.**
`AssignByName` walks every renderer and rebinds `sharedMaterial` from a code-side
table, matched by **case-insensitive substring of the GameObject's name**. This is
deliberate: one lighting/theme/recolour system, and the garage painter needs to
know which renderers are paint. It is also the path all 207 shipped assets take —
§4 is the other one, and an asset never takes both.

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
  highwing autopia tiguan tiguan_r` — resolved through `WheelCatalog`, the same
  seed-plus-manifests shape as `BodyCatalog`. A wheel's save key is **not** always
  its mesh key: `slick_chrome`, `slick_gold` and `slick_neon` are *finishes* over
  the slick's mesh, and a `WheelFinish` on the row is the one thing about a wheel
  that no FBX key can carry.
- **Axle along local +X.** Rim face toward **+X**, brake disc behind it; the
  builder spins the mesh 180° about Y on the side where +X points inboard —
  *composed* with any manifest yaw, never assigned over it.
- Authored radius **0.033 m** (66 mm RC tyre); scaled by `radius / authorRadius`.
  The Tiguan's two are the exception at **0.349 m** — its *loaded* centre height,
  not its free radius, because 0.349 is also the number the WheelCollider gets.
  A committed wheel needs no separate scale correction: recording the mesh's raw
  radius makes that one divide do both jobs.
- Tokens (`PartVisualFactory.WheelTokens`): `tire`/`tyre`, `rim`, `hub`, `stud`,
  `brake`, plus finish tokens. Same ordering hazard: `redtrim` and `hwtrim` both
  contain `rim`, and `hubcap` contains `hub`.
- Anything named tyre-family is kept lit when a cosmetic rim hides the stock one
  (`HideStockRim`), and `CosmeticProbe` hard-fails if that regresses.

---

## 4. Bringing in a textured export — Asset Studio

Blender exports arrive as a folder: `<NAME>.fbx`, an `export.json`, a
`Materials/` of real `.mat` assets and a `Textures/` of BaseColor /
MetallicSmoothness / Emission / Normal PNGs, with objects under their Blender
names (`Police_Body`, `Police_Roof`, `_spotlens.001`, …).

Dropped into `Resources/PartModels/` as-is such an export loads, and is wrong in
two ways that never produce an error: **none of those names contain a token**, so
every renderer misses, falls through to the body material, and the car arrives
correctly shaped in one flat colour; and the `.mat` assets and textures are never
read, because the importer strips them and `AssignByName` overwrites
`sharedMaterial` on every renderer anyway.

**Asset Studio** (`Tools > Asset Studio`) is the answer to that. It browses both
the imported assets and the not-yet-imported exports, previews an export as the
game will actually build it, lets you bind materials per object and per submesh
slot, and commits the result into `Resources/` with a manifest beside it. A
committed asset becomes a car, a wheel or a cosmetic **with no C# edit**.

### 4.1 The two material modes

Per asset, chosen on the draft. There is no third route and no halfway: an asset
with a manifest never falls back to the token tables.

| | **Manifest** | **Verbatim** |
|---|---|---|
| Materials | rebuilt at runtime from the numbers in the manifest | the FBX keeps the exporter's own `.mat` assets |
| Textures | yes — albedo, metallic/smoothness, emission, normal | yes, whatever the `.mat` references |
| Paint mode | **offered** — a baked material can be the paint channel, so `bodyColor` multiplies the livery | refused; `HasPaintableBody` answers no and `SetBodyMaterial` warns once |
| Render pipeline | doesn't care which one wrote the export | the `.mat` must be **Built-in RP Standard** |
| Import setting | `materialImportMode = None` (the default) | `ImportStandard` + `materialLocation = InPrefab` + an explicit remap per material |

**Manifest is the default and should stay it.** The current Blender exporter runs
under URP; this game is Built-in RP and has no URP package, so every `.mat` it
writes references `Universal Render Pipeline/Lit`, resolves here to
`Hidden/InternalErrorShader`, and renders **magenta**. Nothing in this repository
can fix that — verbatim means verbatim — so `[AST]` fails such an asset by name,
with the way out in the message. Manifest mode rebuilds materials from the
exported *numbers* and does not care which pipeline wrote them.

### 4.2 The scale and yaw correction

The old exporter baked its correction into the FBX. The current one does not:
`POLICE` measures `[5.281, 1.521, 2.330]` with `rotation [0,0,0]`, `scale
[1,1,1]`, modelled long-axis along Blender X. It needs exactly what the old
pipeline applied — a **uniform** scale to length 0.420 and a **90° yaw**.

Both are *recorded in the manifest and applied at load*, rather than baked into
`ModelImporter.globalScale`. Recording is strictly more general (`globalScale`
cannot express the yaw at all) and it keeps the imported FBX agreeing byte for
byte with the file the exporter wrote — which is what makes the drift check a
comparison of two files rather than of a file against a remembered import
setting.

- **`authorScale`** — one uniform factor. It **multiplies** the nominal divide of
  §2; it does not replace the divisor. It is `1` for every seed row, which is why
  every shipped car still renders at exactly `1:1:1`.
- **`authorYawDeg`** — the quarter turn that puts the long axis on `+Z`. A
  multiple of 90, and refused otherwise: a bounding box does not survive anything
  else, and the game builds every car facing `+Z` (sensors, lights and gear are
  all placed that way). It is *composed* with the wheel builder's own half-turn,
  never assigned over it.
- **`authorOffset`** — a pivot fix, in **mesh units**, applied after the yaw and
  *inside* the scale. Mesh units rather than metres because a pivot that sits 14 %
  of the car's height too high is 14 % too high at every `bodySize` a design asks
  for, and the garage lets a player move that slider; after the yaw because the
  axes worth thinking in are the car's (`Y` up, `+Z` the way it faces), not the
  ones Blender happened to model along. **Nothing proposes it** — an export can
  measure how big a mesh is and which way its long axis runs, but "the wheels
  should touch the ground" is a judgement about a car. Zero for every asset that
  does not set one, and `TryInstantiate` builds no node at all in that case, so
  all 207 shipped assets cannot tell the field exists.
- **`authorSize`** — what the mesh MEASURES after the scale and the yaw,
  **recorded and not applied**. The validator holds it to within 2 mm of the
  imported prefab. It is not the divisor a design's `bodySize` is a ratio against;
  see §2 for why that has to stay nominal. `authorOffset` deliberately does not
  enter it: a translation does not change a bounding box's extents, which is the
  property that lets an author nudge a car without re-measuring anything.

A wheel needs no `authorScale`. The wheel path already instantiates at
`radius / authorRadius`, so recording the mesh's raw radius makes that one divide
do both jobs.

### 4.3 The manifest

`Resources/<root>/<key>_asset.json`, a **sibling** of the FBX rather than
something under a `Manifests/` folder — the model postprocessor has to find it by
path arithmetic *during* import, where `Resources.Load` does not exist yet.

```jsonc
{
  "schema": 1,
  "key": "body_police",          // the FILE NAME wins if these disagree
  "kind": "CarBody",             // CarBody | Wheel | Cosmetic | Prop | Fitting
  "label": "Police Cruiser",     // what a picker prints; free to change
  "materialMode": "Manifest",    // or "Verbatim"

  "source":  { "assetName": "POLICE", "sourceBlend": "…",
               "exportedAtUtc": "…", "fbxMd5": "…" },

  "authorScale": 0.07953,        // uniform, multiplies the nominal divide
  "authorYawDeg": -90,           // a multiple of 90
  "authorOffset": [0, -0.75, 0], // pivot fix, MESH units, after the yaw,
                                 //   inside the scale. Absent reads as zero.
  "authorSize": [0.1853, 0.1210, 0.4200],   // measured, after scale + yaw

  "spec": { "x": -1, "y": -1, "z": 0.420,   // -1 = unpinned (a hand-written
            "maxTris": 18850,               //   null is accepted and read as -1)
            "specSource": "measured" },

  "materials": [
    { "name": "M_Police_Paint",  // the JOIN KEY — object slots hold these names
      "baked": true,             // the null-discriminator; see below
      "paintChannel": true,      // the design's colour MULTIPLIES this material
      "rgb": [1,1,1], "metallic": 0, "smoothness": 0.5, "alpha": 1,
      "emission": [0,0,0], "emissionStrength": 0,
      "mapAlbedo": "PartModels/body_police/M_Police_Paint_BaseColor",
      "mapMetallicSmoothness": "…", "mapEmission": "", "mapNormal": "…" }
  ],

  "objects": [
    { "name": "Police_Body",     // the Blender OBJECT name, never the mesh name
      "slots": ["M_Police_Dark", "M_Police_Paint"],   // INDEX is the submesh slot
      "role": "Structural", "healthHp": 0, "group": "" }
  ],

  "vehicle":  { "cd": -1, "clA": 0.0, "garageOffered": true },   // -1 = measure it off the mesh
  "cosmetic": { "slot": "", "rarity": "", "theme": "", "description": "" },
  "notes":    { "geometryFixes": 11, "textureWarnings": 0,
                "verificationOverridden": false, "overrideReason": "" },
  "committedHash": "…"
}
```

Things about that file worth knowing before you hand-edit one:

- **Every string that could have been an enum is a string.** `JsonUtility` writes
  an enum as its *ordinal*, so inserting a value in the middle would silently
  reinterpret every asset already authored, and the diff would show a number
  nobody can read.
- **`baked` is the null-discriminator.** A baked material has no flat colour — the
  texture carries it — and `JsonUtility` cannot tell an absent float from a zero
  one. Never read `rgb`, `metallic` or `smoothness` as meaningful without checking
  it first.
- **Object names, never mesh names.** Unity names an imported GameObject after the
  Blender *object*; the mesh datablock inside it is called something else entirely
  (`Police_Body` vs `Police_Body_baked_baked_baked`).
- **An empty slot entry is a statement**, not a gap: "leave this slot as
  imported". A slot naming a material the manifest does not have is a dangling
  reference and `[AST]` fails it.
- **`smoothness`, not roughness.** Blender exports roughness; the conversion
  happens once, in the tool, so nothing at runtime has a convention left to get
  wrong.
- **`vehicle` and `cosmetic` are always present** and `kind` decides which one
  means anything. `JsonUtility` writes a null class field as `{}` and reads `{}`
  back as a defaulted object, so "the block is absent" is not a distinction this
  format can carry.
- **Two hashes, two questions.** `source.fbxMd5` is what the *export* hashed at
  commit time, so a mismatch means Blender moved on; `committedHash` is what the
  *copy* hashed, so a mismatch means somebody edited the file under `Resources/`
  and a re-commit would throw that edit away.
- **Textures live in the asset's own folder**, `Resources/<root>/<key>/`, never a
  shared one. A material Unity replaces gets named after its diffuse *texture*, so
  two assets whose exporter both wrote a `BaseColor` map would collide in a shared
  folder and one would silently win.

### 4.4 Committing

`Tools > Asset Studio >` `1. Sync drafts from exports` → `2. Commit all drafts`,
or the per-asset button in the window. A draft is a `ScriptableObject` under
`TinyTorqueAssets/AssetStudio/Drafts/` — which is what makes Undo work — and it
is **not** what the game reads. Committing is what writes the manifest.

The pipeline copies the FBX and the referenced textures MD5-guarded, writes the
manifest, imports **sidecars first and the model second** (a Verbatim model
imported alongside its materials resolves none of them), then reads the truth back
off the import: the real submesh slot counts, the measured `authorSize`, the
triangle budget. Committing twice moves nothing — not one byte, `.meta` included.

**It refuses rather than guesses**, and gives all the reasons at once so fixing
three problems takes one round trip: an illegal key, the wrong `body_`/`wheel_`
prefix, an unassigned kind, a prop or a fitting (neither has a registry a manifest
can join), overwriting a shipped asset *without having chosen Replace* (§4.5), a
failed exporter verification without an override, an override without a written
reason, a dangling slot, an unverified multi-slot object, a non-positive scale, an
off-quarter yaw, an unparseable cosmetic slot/rarity/theme, and — for a body — a
stated drag coefficient outside anything a car can be (0.15 is a teardrop, 1.2 a
flat plate broadside).

**A body no longer has to state a drag coefficient at all.** It used to be
refused without one, on the grounds that a car whose top speed was chosen by a
fallback constant is a car nobody chose. The game now measures one off the shell's
own silhouette — the mesh the manifest is already shipping — so `"cd": -1` means
*measure it*, and that is the right answer for almost every asset. State a number
only when you are holding a real one, measured or published; it then outranks the
estimate. `clA` is a different question and still has to be authored: a
silhouette cannot see whether a shape makes downforce.

**Slot order is measured, not asked.** `export.json` lists a material's objects in
Blender's material-list order, which has no reason to match an object's submesh
slot order — on the police car it is exactly backwards. The importer the game uses
destroys the real order (`materialImportMode = None`, correctly — the game binds
its own materials), so Asset Studio imports a scratch copy *outside* every
postprocessor scope with materials left on, reads `sharedMaterials` per renderer,
and throws the copy away. The "unverified multi-slot object" refusal above is
therefore the fallback path now: it fires only when that read fails, and then the
draft says so and the ordering goes back to being your problem. Getting this pair
the wrong way round is not cosmetic — a repaint writes **by slot**, so a reversed
`Police_Body` recolours the trim and leaves the livery alone.

A re-sync keeps every authored decision that still has something to attach to:
damage role, health, group, slot mapping and the verified flag by OBJECT NAME,
and hand-edited material values by MATERIAL NAME. Anything the new export no
longer mentions is *reported*, not deleted in silence — "the door is gone" and
"the door was renamed" look identical from here and only you can tell them apart.

### 4.5 Replacing a mesh that already ships

Re-modelled the Patrol car and want the existing `body_patrol` to *be* the new
mesh? That is a **replacement**, not a new asset, and it has its own path:

> Select the asset in Asset Studio → **Replace mesh...** → pick the export folder
> → confirm → look at the preview → **Commit**.

**It asks nothing a new asset has to be asked.** The key, the kind, the label, the
drag coefficient, which slot a hat occupies and how rare it is are all inherited
from the row being replaced — a new mesh has no standing to change them, and a
saved design naming that key must keep meaning the car it has always meant. What
the export supplies is what a mesh actually decides: the geometry, the scale and
yaw correction, the materials, the per-slot bindings.

**The FBX is overwritten in place.** Same path, same `.meta`, same GUID, so every
scene and prefab referencing it keeps resolving. Do *not* delete or rename the old
file first — that breaks those references, makes `[PMV]` and `[AKEY]` fail on a
missing asset, and makes the car fall back to the primitive box its row nominates.
It is a tracked file, so the replacement is one `git checkout` from undone.

**Seed row and manifest split ownership**, which is what makes this need no code
change. `Compose()` keeps the *seed* row when a manifest names a shipped key — the
key, its `BodyShape`, its label, its aero — but the row was never the authority on
geometry, so `authorScale` (bodies) and `authorRadius` (wheels) are read from the
manifest at the point of use, by `BodyCatalog.AuthorScaleOf` and
`WheelCatalog.AuthorRadiusOf`. `HasPaintableBody` asks the manifest *before* the
row's own flag for the same reason: replace a baked livery with a shell that
declares a paint channel and the row is describing a car that is no longer there.
Nothing mutates the static seed table, so deleting the manifest restores the
original answers immediately.

**One thing is genuinely lost, and `[PMV]` says so out loud.** That validator's
literal row for a shipped key was an *independent prediction* about what an FBX
round trip should preserve. After a replacement it describes a file that is not on
disk, so the manifest's measured spec supersedes it and the run logs
`[PMV] SUPERSEDED <key>`. The replacement row carries `specSource: "measured"` and
can only say the mesh has not moved since it was committed. That is what replacing
a shipped asset costs; it is not hidden, and it is cheaper than a gate that fails
until somebody hand-edits a table.

Authorisation is recorded on the draft as `replacesKey` — the key, not a flag — so
editing the draft's key afterwards revokes it rather than carrying a licence to
overwrite `body_patrol` across to `body_coupe`. Props and fittings are refused
here for the same reasons §4.6 gives below — and the refusal arrives before the
folder picker rather than after it.

### 4.6 What a committed asset joins

The manifests **are** the registry. `BodyCatalog`, `WheelCatalog` and
`CosmeticCatalog` each compose their table from their seed rows plus every
manifest discovered under a `Resources` root, so there is no registry file that
could disagree with what is on disk.

- **A body** becomes a `BodyDef`: `clA` authored, `cd` measured off the mesh
  unless the manifest states one, `paintable` derived from whether any material
  claims the paint channel, and the mesh key *is* the key.
- **A wheel** becomes a `WheelDef` whose `authorRadius` is measured off the mesh.
- **A cosmetic** becomes a `CosmeticItem` from five authored fields; scrap value
  and shop price come from the *rarity* through the same table the 47 shipped
  cosmetics use, so a new hat cannot reprice the economy. `UnlockCatalog` composes
  its pool from `CosmeticCatalog`, so it reaches the crates and the shop with no
  further wiring.
- **Props and fittings do not register.** Props are out of v1 scope — the scenery
  call sites skip material binding entirely when their token array is empty — and
  a fitting's key (the battery, the antennas, the light bars) comes from a
  `switch` on an int, so a committed one would import correctly and be asked for
  by nothing. Replacing one of those meshes is a file swap, not a commit.

A committed row has **no enum value at all**, and that is what "a new car without
a code change" means. Its legacy int is `Box` / style 0, which is what an older
build reading the int beside the key will build — unfixable with `JsonUtility`,
and the reason `NetSession.ProtocolVersion` went to 16.

### 4.7 The gates

```
-executeMethod AIHWSim.AssetTools.AssetStudioValidator.Report      -> [AST] RESULT
-executeMethod AIHWSim.EditorTools.PartModelValidator.Report       -> [PMV] RESULT
-executeMethod AIHWSim.EditorTools.AssetKeyValidator.Report        -> [AKEY] RESULT
```

`[AST]` checks every committed manifest against things that can disagree with it:
the prefab Unity imported (objects, submesh slot counts, measured size within
2 mm, triangle count), the map paths against `Resources`, the import settings
against the **slot** a map is bound to, `committedHash` against the FBX on disk,
and the key against the catalogue that should have composed a row for it. Zero
managed assets is a pass and says so — that is the shipped state. Source drift is
reported as *news*, not as a failure: Blender moving on is not a defect in this
repository, and on a machine with no export folder configured that half is skipped
and counted.

`[PMV]`'s table is the 207 literal rows **∪** one row per manifest, and a
duplicate key is a hard failure rather than a merge. The two halves are not the
same kind of claim and the manifest says which is which: a literal row is an
independent prediction about what an FBX round trip should preserve, while a
manifest row carries `specSource: "measured"` and can only say the mesh has not
grown since it was committed.

Neither validator runs the token-table check on a managed asset — it binds by
object name and slot and has never heard of those tables, so the check would
either pass by finding nothing or fail an innocent asset for owning a piece
called `chrome_1`.

### 4.8 The one thing this does not buy you

`PartVisualFactory.AccentTokens` is still a code table. A body wanting *new*
material tokens still needs a code change **unless it ships a manifest**. The
claim is "no code change for a manifest asset", not "no code change ever".

And the police body is **already in the game** as `body_patrol`, from the same
`TinyTorque_police.blend`. Committing over a shipped key is refused; commit under
a new one, which is the whole point of string keys.

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

Props are **out of Asset Studio's scope** (§4) and the commit pipeline refuses
them by name. The reason is not that they are hard: `TrackCatalog` and `ArcadeVfx`
skip material binding entirely when their token array is empty, so a prop with a
manifest would import correctly and never bind. Adding props means giving the
scenery call sites the hand-off the vehicle ones already have, plus an `ItemDef`
row for placement — a real piece of work, not a flag.
