# Tiny Torque Assets

A browsable, draggable kit of every model in the project — prefabs, materials, a
scatter brush, two debug scenes and two starter maps.

**This pack is editor-only and ships nothing.** It lives outside every
`Resources/` folder, neither of its scenes is registered in Build Settings, and
no game script references any asset in it. `PackValidator` asserts all of that
on every run.

It exists because the game itself has **no prefabs and no material assets** —
everything is built procedurally at `Awake` and materials are rebuilt in C# and
bound by name token. That is the right design for the game and a terrible one
for looking at your own models. This pack is the other half.

---

## Layout

```
Models/       the meshes, copied from Resources/ and categorised
  Vehicles/<Car>/            body, wheel, and per-car extras for the 7 cars
  Vehicles/Common/           legacy shells, shared wheels, battery, antennas, light bars
  Props/<Theme>/             Downtown · Toy · Enchanted · Haunted · City ·
                             Arcade · Beach · NeonGrid · Tabletop · Volcano · Arena
  Cosmetics/<Slot>/          Topper · Rim · Ornament · Bobble · Wing · Crates
Materials/    generated .mat assets, mirroring the game's runtime materials
Prefabs/      mirrors Models/ — see "Variants" below
Brushes/      six ScatterPreset assets for the scatter brush
Maps/         two TrackDesign JSON files
Scenes/       TTA_Kit (the lineup) · TTA_Sandbox (scratch space)
Editor/       the generators, the brush, and the validator
```

## Variants

| Kind | Prefab | Collider |
|---|---|---|
| Props | `<key>_Mesh` | one non-convex MeshCollider per piece — what the game does |
| Props | `<key>_Box` | one BoxCollider fitted to renderer bounds — the cheap stand-in |
| Vehicle parts, cosmetics | `<key>_Viz` | none (they mount to a moving car) |

Arena tiles carry a theme in the name: `soc_wall_Circuit_Mesh`. One geometry,
three palettes — the kit's twenty material slots are all answered by all three
themes, so retexturing an arena never touches a vertex.

## Regenerating

`Tools > TinyTorque Assets >` — the numbered items run in order and are all
safe to re-run. `Rebuild everything` does the lot.

1. **Copy source meshes** — from `Resources/` into `Models/`
2. **Generate materials** — clones of the game's runtime materials
3. **Generate prefabs**
4. **Generate brush presets**
5. **Create debug scenes**
6. **Generate maps**
7. **Install maps to Track Builder** — copies the JSON to your save area

Headless:

```bash
Unity.exe -batchmode -quit -projectPath UnitySim -executeMethod AIHWSim.Pack.PackBuildAll.RunHeadless -logFile pack.log
```

## What is generated vs authored

**Everything here is generated.** Do not hand-edit assets in `Models/`,
`Materials/`, `Prefabs/` or `Scenes/` and expect the edit to survive — the next
rebuild overwrites them. The things that ARE authored, and worth editing:

- `Brushes/*.asset` — tweak radius, density, palette to taste (a rebuild resets
  the tuning but keeps your own new presets)
- `Editor/PackSocData.cs` — the arena palette, the one material table with no
  runtime source. Regenerate it from the exporter rather than typing numbers:
  `scratchpad/gen_socdata.py` reads `build_map_props.py`'s MATJSON blocks.

The game's C# tables remain the single source of truth for every other
material. The generator clones them; it never retypes a number.

## The maps

`Maps/TinyTorque_FreeRoam.json` and `Maps/TinyTorque_BaseRace.json` are ordinary
`TrackDesign` documents built only from ids the game's `TrackCatalog` already
knows. Run **Install maps to Track Builder**, then open the Track Builder scene
and Load them — they edit and Drive like any user map.

They are deliberately **not** `TrackPresets` entries, so they appear in no
in-game race or map picker. Promoting one later is a single row in
`TrackPresets.All`.

## Known fallbacks

A one-material FBX imports with the ROOT carrying the mesh, named after the file
rather than after its material token — so ornaments, the legacy body shells and
a couple of arena tiles can never match a token. They take their kind's fallback
material, which is the same answer the game reaches. The prefab generator logs
every one as `FALLBACK <key> -> <material>`.

The four `arc_*` props (item box, banana, missile, shield orb) have no
`ItemDef` at all — the arcade director spawns them — so there is nothing to
harvest and they take the neutral grey. That is the honest answer, not a bug.

## The circuits

`Circuits/` holds three real Formula 1 tracks — Interlagos, Monza and
Spa-Francorchamps — with a scene each in `Scenes/Circuits/`. They are not
authored in Unity and not authored by eye: the centreline is the surveyed
OpenStreetMap raceway trace, the elevation is a DEM sampled along it, and every
piece of furniture is placed by a deterministic, hash-seeded layout pass in the
Blender project next door (`AI_3D_Modeling/TinyTorque_RC`). Read
`UNITY_EXPORT.md` there for the whole contract; the short version:

```bash
# in AI_3D_Modeling/TinyTorque_RC
blender -b --factory-startup -P scripts/export_unity.py -- --all
```

then **Tools > TinyTorque Assets > Circuits > Rebuild everything**. That runs the
axis test, copies the FBX and manifests in, and rebuilds all three scenes. The
export folder is probed automatically; *Set export folder…* is there for when the
two repositories are not side by side.

| Circuit | Lap | Corners | Trees | Stands | Buildings |
|---|---|---|---|---|---|
| Interlagos | 4 305 m | 16 | 847 | 6 | 3 061 |
| Monza | 5 799 m | 12 | 16 588 | 26 | 251 |
| Spa | 7 015 m | 22 | 16 626 | 9 | 84 |

**Geometry arrives in three roles**, and the hierarchy in each scene follows
them. *World* meshes — road, kerbs, run-off, terrain, barriers, the pit complex,
the batched buildings — have their vertices already in the right place and sit at
the identity. *Split* bodies are one mesh per grandstand, so a landmark can be
grabbed on its own. *Instances* are a rigid prototype plus a transform: sausage
kerbs, marshal posts and braking boards are prefab instances you can select and
move; trees are combined into one mesh per 250 m cell, because Spa has 16 626 of
them. The per-tree transforms stay in the manifest either way, so an LOD group or
a GPU-instanced draw is still reachable from the same data — and *Explode trees to
GameObjects* gives you one GameObject each when you actually want to move them.

**Nothing here is hand-placed, and the build says so.** Every run reports placed
against expected for all four roles, counts every drop by reason, and
`3. Validate circuit scenes` re-measures the built scene against the manifest's
spine — the one description of the circuit that never went through the FBX
pipeline. A mirrored or transposed import places exactly the right number of
everything, so counting is not enough; the road-on-spine check fails it by
hundreds of metres.

`0. Verify axis convention` runs an L-shaped marker with a different extent on
each axis through the real pipeline and asserts it lands on a copy Blender baked
into place itself. It gates the rest, and it has already earned that three times.

**Kerbs are striped in geometry, not in a texture.** The Blender kerb is a
red/white block pattern driven off the road ribbon's `trk` UV, and a flat
Standard material can only average it — one dull red, on the most recognisable
feature of a circuit. So the exporter rebuilds the kerb band at 900 mm station
spacing and alternates the *material slot*, which needs no UV and no texture and
matches how the rest of this kit is shaded. It costs about 26 000 triangles a
circuit.

Both bands are in the scene and **Circuits > Striped kerbs** switches which one
draws — instantly, no rebuild. The collider stays on the plain band either way,
so the toggle can never change what a car hits.

**The tree chunk meshes are committed.** `Circuits/*/generated/` holds one
combined mesh per 250 m cell, baked from the manifest's per-tree transforms.
They are kept under the 16-bit index ceiling and vertex-compressed, because this
project serialises assets as text and 32-bit indices are eight hex characters
each — that one choice is tens of megabytes.

**These scenes are pack scenes.** They are not in Build Settings and the game
cannot load them yet — see `PackValidator.PromotedScenes` for what promoting one
involves.

## Not in the pack's remit

The 24 arena tiles are **pack-only**. They are not registered in
`TrackCatalog.Items`, so the Track Builder cannot place them; adding them to the
game was explicitly out of scope for this pass.
