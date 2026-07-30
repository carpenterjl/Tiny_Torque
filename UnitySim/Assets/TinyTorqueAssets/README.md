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

## Not in the pack's remit

The 24 arena tiles are **pack-only**. They are not registered in
`TrackCatalog.Items`, so the Track Builder cannot place them; adding them to the
game was explicitly out of scope for this pass.
