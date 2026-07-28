"""Export the four TinyTorque_RC map prop packs into track props.

Headless (Blender 5.2 -- the source blends are 5.x-era):
    "C:\\Program Files\\Blender Foundation\\Blender 5.2\\blender.exe" ^
        --background --python build_map_props.py

Re-runnable; the source blends are opened and NEVER saved. Each blend is a
showcase -- every prop is ONE multi-material mesh named P_* inside a PROPS
collection, lined up along X at full-scale metres. For each prop this writes
UnitySim/Assets/Resources/TrackProps/<key>.fbx:

    TinyTorque_props.blend        -> dt_*      (Downtown; the pack's own file
                                    names are unprefixed and "cone" would
                                    collide with the primitive cone item)
    TinyTorque_toy_props.blend    -> toy_*     (Toy Room)
    TinyTorque_ench_props.blend   -> ench_*    (Enchanted Kingdom)
    TinyTorque_haunt_props.blend  -> haunt_*   (Haunted Hollow)

Per prop: duplicate, bake S(0.1) @ T(-cx, -cy, -minz) (origin at the base
contact point, centre in plan -- TrackFactory.ItemPose snaps roots onto the
drop surface, so a centred origin would bury half the prop), separate by
material, rename pieces <token>_<n> where token = the material name minus its
theme prefix, lowercased (M_Prop_NeonCyan -> neoncyan). Unknown materials are
fatal. AssignByName in TrackCatalog is first-match substring, so the C# token
arrays must be ordered longest-first (ghostdim before ghost) -- the printed
token lists are pasted there, never remembered.

Frames: the showcase fronts face -Y (toward the preview camera), which the
shared FBX args map straight onto Unity +Z -- no rotation. Blender X/Y/Z ->
Unity X/Z/Y. Scale is a uniform 0.1: 1 authored metre = 0.1 game metre, the
exact 1/10-world fiction (hero landmarks stay enormous on purpose).

Special case dt_traffic_light: M_Prop_SigOff covers BOTH dark lenses in one
piece; it is split by loose parts and renamed sigred (top) / sigamber
(middle) so SignalCycle can drive each lamp; the green lens is already its
own material.

Per prop one JSON block is printed between PROPJSON>>> markers: key, scaled
Unity-space size, token piece bounds, tri count, and a 12-station zmin/zmax
profile along the longer plan axis (ramps: deck slope; gates: leg extents and
clearance under the lintel). Hull sizes, PMV rows and tri budgets downstream
are pasted from this output, never hand-derived.
"""

import bpy
import json
import math
import os
import sys
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
MODELS = r"E:\EE Projects\AI_3D_Modeling\TinyTorque_RC\models"
FBX_DIR = os.path.normpath(os.path.join(
    HERE, "..", "UnitySim", "Assets", "Resources", "TrackProps"))

SCALE = 0.1
PROFILE_STATIONS = 12

# Known materials per pack -- the token is the name minus this prefix,
# lowercased. Anything not listed here is fatal: a silently auto-derived
# token would just as silently miss its C# material and render as fallback.
PACKS = {
    "dt": {
        "blend": "TinyTorque_props.blend",
        "prefix": "M_Prop_",
        "mats": ["Concrete", "ConcreteLt", "Gold", "NeonCyan", "NeonGold",
                 "NeonCrimson", "NeonOrange", "Panel", "Steel", "Lamp",
                 "Rock", "RockTop", "Crimson", "Facade", "Glass", "Orange",
                 "White", "Rubber", "SigOff", "SigGreen", "Basalt", "Lava"],
    },
    "toy": {
        "blend": "TinyTorque_toy_props.blend",
        "prefix": "M_Toy_",
        "mats": ["Red", "Cream", "Yellow", "Blue", "Green", "Orange",
                 "Purple", "Walnut", "Pine", "Ply", "Card", "Paper", "Ink",
                 "Wax", "FeltBlue", "Cotton", "Brass", "Shade", "ShadeDesk",
                 "Bulb", "Book0", "Book1", "Book2", "Book3", "Book4",
                 "Book5", "Book6"],
    },
    "ench": {
        "blend": "TinyTorque_ench_props.blend",
        "prefix": "M_Ench_",
        "mats": ["Iron", "Leaf", "LeafDark", "Rose", "Rock", "RockMoss",
                 "Stone", "StonePale", "StoneMoss", "Slate", "SlateTeal",
                 "SlatePlum", "Gold", "Window", "Crimson", "Plaster",
                 "Timber", "Thatch", "CrystalRose", "Water", "Spray",
                 "Flame", "Hedge", "Snow", "Azure", "Bark", "Blossom"],
    },
    "haunt": {
        "blend": "TinyTorque_haunt_props.blend",
        "prefix": "M_Haunt_",
        "mats": ["Grime", "Rubble", "Earth", "Moss", "Flame", "Iron",
                 "Shingle", "Verdigris", "Window", "Marble", "StoneDark",
                 "Ghost", "GhostDim", "Tar", "Glass", "Candle", "DeadWood",
                 "Clapboard", "Pumpkin", "Jack", "Stalk", "Bark"],
    },
}


# ---------------------------------------------------------------------------
# helpers (build_vehicles.py idiom)
# ---------------------------------------------------------------------------

def die(msg):
    print("[build_map_props] FATAL: " + msg)
    sys.exit(1)


def deselect_all():
    for o in bpy.context.view_layer.objects:
        o.select_set(False)


def world_bbox(objs):
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            lo = Vector(map(min, lo, w))
            hi = Vector(map(max, hi, w))
    return lo, hi


def duplicate(objs):
    deselect_all()
    dups = []
    for o in objs:
        d = o.copy()
        d.data = o.data.copy()
        d.parent = None
        d.matrix_world = o.matrix_world.copy()
        bpy.context.scene.collection.objects.link(d)
        dups.append(d)
    return dups


def bake(objs, xform):
    deselect_all()
    for o in objs:
        o.matrix_world = xform @ o.matrix_world
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def separate_and_tokenise(objs, tokens):
    """Separate by material, drop unused slots, rename to <token>_<n>."""
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='MATERIAL')
    bpy.ops.object.mode_set(mode='OBJECT')
    result = [o for o in bpy.context.view_layer.objects
              if o.type == 'MESH' and o.select_get()]

    deselect_all()
    counts = {}
    for o in result:
        if o.material_slots:
            o.select_set(True)
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.material_slot_remove_unused()
            o.select_set(False)
        mats = [s.material for s in o.material_slots if s.material]
        if len(mats) > 1:
            die("%s still has %d materials after separate" % (o.name, len(mats)))
        if not mats:
            die("object %s has no material" % o.name)
        token = tokens.get(mats[0].name)
        if token is None:
            die("no token mapped for material '%s' (object %s)"
                % (mats[0].name, o.name))
        counts[token] = counts.get(token, 0) + 1
        o.name = "%s_%d" % (token, counts[token])
    return result


def separate_loose(objs):
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='LOOSE')
    bpy.ops.object.mode_set(mode='OBJECT')
    return [o for o in bpy.context.view_layer.objects
            if o.type == 'MESH' and o.select_get()]


def export_fbx(objs, key):
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    os.makedirs(FBX_DIR, exist_ok=True)
    path = os.path.join(FBX_DIR, key + ".fbx")
    # Identical to mcp_helpers.export_part: apply_unit_scale=False +
    # global_scale=0.01 cancels the m->cm bake; EDGE smoothing carries split
    # normals; PartModelPostprocessor reads the raw numbers.
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True,
        apply_unit_scale=False, global_scale=0.01,
        axis_forward='-Z', axis_up='Y', bake_space_transform=True,
        object_types={'MESH'}, use_mesh_modifiers=True,
        mesh_smooth_type='EDGE', use_tspace=True, path_mode='COPY')
    print("[build_map_props] wrote %s (%d objects, %d bytes)"
          % (path, len(objs), os.path.getsize(path)))


def purge(objs):
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)


def to_unity_size(lo, hi):
    return [round(hi.x - lo.x, 4), round(hi.z - lo.z, 4), round(hi.y - lo.y, 4)]


def to_unity_point(v):
    return [round(v.x, 4), round(v.z, 4), round(-v.y, 4)]


def split_traffic_lenses(parts):
    """The sigoff piece merges both dark lenses; split by loose parts and
    rename by height so SignalCycle can address each lamp."""
    src = [o for o in parts if o.name.startswith("sigoff")]
    if len(src) != 1:
        die("expected exactly one sigoff piece, got %d" % len(src))
    parts.remove(src[0])
    pieces = separate_loose(src)
    if len(pieces) != 2:
        die("expected 2 loose sigoff lenses, got %d" % len(pieces))
    pieces.sort(key=lambda o: -(world_bbox([o])[0].z + world_bbox([o])[1].z))
    pieces[0].name = "sigred_1"
    pieces[1].name = "sigamber_1"
    parts.extend(pieces)
    return parts


def profile(parts):
    """12-station (zmin, zmax) along the longer plan axis. Ramps read their
    deck slope from zmax; gates read leg extents from where zmin stays at the
    floor and lintel clearance from where it lifts off."""
    lo, hi = world_bbox(parts)
    axis = 0 if (hi.x - lo.x) >= (hi.y - lo.y) else 1
    a0, a1 = lo[axis], hi[axis]
    if a1 - a0 < 1e-6:
        return None
    zmin = [None] * PROFILE_STATIONS
    zmax = [None] * PROFILE_STATIONS
    for o in parts:
        for v in o.data.vertices:
            w = v.co  # transforms are baked; local == world
            i = min(PROFILE_STATIONS - 1,
                    int((w[axis] - a0) / (a1 - a0) * PROFILE_STATIONS))
            z = w.z
            if zmin[i] is None or z < zmin[i]:
                zmin[i] = z
            if zmax[i] is None or z > zmax[i]:
                zmax[i] = z
    r = lambda z: None if z is None else round(z, 3)
    return {"axis": "x" if axis == 0 else "y",
            "zmin": [r(z) for z in zmin], "zmax": [r(z) for z in zmax]}


# ---------------------------------------------------------------------------
# per-pack build
# ---------------------------------------------------------------------------

def build_pack(pack, cfg):
    blend = os.path.join(MODELS, cfg["blend"])
    if not os.path.isfile(blend):
        die("missing " + blend)
    bpy.ops.wm.open_mainfile(filepath=blend)

    tokens = {cfg["prefix"] + m: m.lower() for m in cfg["mats"]}

    coll = bpy.data.collections.get("PROPS")
    if coll is None:
        die("no PROPS collection in " + cfg["blend"])
    props = sorted([o for o in coll.objects if o.type == 'MESH'
                    and o.name.startswith("P_")], key=lambda o: o.name)
    if not props:
        die("no P_* meshes in " + cfg["blend"])

    for src in props:
        name = src.name[2:]                       # strip "P_"
        key = ("dt_" + name) if pack == "dt" else name

        dups = duplicate([src])
        lo, hi = world_bbox(dups)
        base = Vector(((lo.x + hi.x) * 0.5, (lo.y + hi.y) * 0.5, lo.z))
        bake(dups, Matrix.Scale(SCALE, 4) @ Matrix.Translation(-base))

        parts = separate_and_tokenise(dups, tokens)
        if key == "dt_traffic_light":
            parts = split_traffic_lenses(parts)

        plo, phi = world_bbox(parts)
        tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons)
                   for o in parts)
        pieces = {}
        for o in parts:
            olo, ohi = world_bbox([o])
            pieces[o.name] = {
                "center": to_unity_point((olo + ohi) * 0.5),
                "size": to_unity_size(olo, ohi),
            }

        export_fbx(parts, key)
        report = {
            "key": key,
            "size": to_unity_size(plo, phi),
            "tris": tris,
            "tokens": sorted({o.name.rsplit("_", 1)[0] for o in parts}),
            "pieces": pieces,
            "profile": profile(parts),
        }
        purge(parts)
        print("PROPJSON>>>" + json.dumps(report) + "<<<PROPJSON")


def main():
    for pack, cfg in PACKS.items():
        build_pack(pack, cfg)
    print("[build_map_props] done.")


main()
