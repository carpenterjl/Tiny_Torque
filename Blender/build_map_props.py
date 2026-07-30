"""Export the five TinyTorque_RC map prop packs into track props.

Headless (Blender 5.2 -- the source blends are 5.x-era):
    "C:\\Program Files\\Blender Foundation\\Blender 5.2\\blender.exe" ^
        --background --python build_map_props.py [-- <pack> ...]

Naming one or more packs on the command line builds only those. With no
arguments every pack is rebuilt, which is the honest default but also rewrites
90-odd FBX (and their .meta guids) that did not change -- so a pass that adds
one family should name it.

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
    TinyTorque_city_props.blend   -> city_*    (Torque Falls; the source names
                                    are ALREADY city_-prefixed, so this pack
                                    adds nothing)
    TinyTorque_soc_props.blend    -> soc_*     (the 24-tile arena kit; also
                                    already prefixed. This one goes to the
                                    asset pack instead of Resources, keeps the
                                    authoring frame instead of a base-contact
                                    origin, and reads three palettes off the
                                    theme axis -- see the PACKS entry.)

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

Special case the traffic signals (dt_traffic_light, city_signal): the SigOff
material covers EVERY dark lens in one piece, so it is split by loose parts,
clustered by plan position (one cluster per lamp head) and renamed sigred
(top of a head) / sigamber (below it) so SignalCycle can drive each lamp; the
green lens is already its own material. Clustering rather than ranking is
what makes the city signal work: it has two heads on a mast arm plus a
pedestrian head, so five dark lenses whose heights interleave -- a straight
sort by height would call the second head's red an amber.

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

# Where the modeling project's authoring modules live, so a pack can ask one of
# them for palettes it does not carry in its own blend (see "themes" below).
SCRIPTS = os.path.join(os.path.dirname(MODELS), "scripts")

# The asset pack's arena folder. Pack-native props are exported straight here
# and never enter Resources/ -- they are a Unity-side kit for editing, not
# content the game can place, so putting them in Resources would ship bytes for
# something no TrackCatalog item references.
PACK_DIR = os.path.normpath(os.path.join(
    HERE, "..", "UnitySim", "Assets", "TinyTorqueAssets", "Models", "Props", "Arena"))

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
    # The daylight town. Twice any other pack's palette because it is the only
    # kit with no theme to unify it: five clapboard colourways, two renders,
    # two roof colours and three leaf tints all have to coexist in one street.
    # Numbered names (Wall5, Leaf0, Flower2) are the source's own -- the map
    # module builds extra colourways by rebuilding a prop under a different
    # seed, and only the seeds these 35 showcase meshes used are in the blend.
    "city": {
        "blend": "TinyTorque_city_props.blend",
        "prefix": "M_City_",
        "mats": ["Alu", "Asphalt", "Bark", "BarkPine", "Black", "Blue",
                 "Brick", "BrickPale", "Chrome", "Concrete", "ConcreteDk",
                 "Cream", "Door", "Flower2", "Flower3", "Galv", "Glass",
                 "GlassLit", "GlassShop", "Grass", "Green", "Interior",
                 "Lamp", "Leaf0", "Leaf1", "Leaf2", "LeafHedge", "LeafPine",
                 "NeonRed", "PaintWhite", "Red", "Render2", "Render3",
                 "Roof0", "Roof1", "Rubber", "SigGreen", "SigOff", "SignLit",
                 "Soil", "Steel", "Stucco", "Timber", "Trim", "TrimDk",
                 "Tube", "Wall5", "Wall7", "Yellow"],
    },
    # The soccer/arena tile kit. Three things make it unlike the five above,
    # and each is one optional key rather than a special case in build_pack:
    #
    #   "origin": "row" -- the other packs are showcase props that TrackFactory
    #     drops onto a surface, so their origin is baked to the base contact
    #     point, centred in plan. These are TILES: an arena is assembled by
    #     placing all of them at the same point and letting the authored
    #     offsets stack the shell (floor z -0.6..0, cove 0..16, wall 16..32,
    #     crown 32..40.6, ceiling at 40). Recentring each one would collapse
    #     the shell into a pile at the origin. Measured: the blend stores the
    #     kit lined up along X for the lineup render with `location = (x, 0, 0)`
    #     and Y/Z authored in place, so undoing exactly that X offset restores
    #     the authoring frame and nothing else.
    #
    #   "themes" -- the palette is a THEME AXIS. Every one of the twenty slots
    #     is answered by all three themes (M_Soc_Circuit_wall, M_Soc_Sandlot_wall,
    #     M_Soc_Forge_wall) and retheme() swaps an arena by name lookup without
    #     touching a vertex. The blend was built in the default theme only, so
    #     the other two palettes are read by asking the authoring module to
    #     build them -- geometry still exports ONCE.
    #
    #   "dest" -- pack-native, see PACK_DIR.
    "soc": {
        "blend": "TinyTorque_soc_props.blend",
        "prefix": "M_Soc_Sandlot_",
        "mats": ["floor", "sub", "line", "cove", "wall", "ceiling", "trim",
                 "metal", "glass", "glow_a", "glow_b", "light", "screen",
                 "net", "seat", "crowd", "banner", "ball", "team_blue",
                 "team_orange"],
        "origin": "row",
        "dest": PACK_DIR,
        "themes": {"module": "tt_26_soccer",
                   "names": ["circuit", "sandlot", "forge"]},
    },
}

# Props whose SigOff piece has to be split into per-lamp lenses. Keyed by the
# final export key, so a pack that renames its props cannot silently miss one.
SIGNAL_KEYS = ("dt_traffic_light", "city_signal")


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


def export_fbx(objs, key, dest=None):
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    dest = dest or FBX_DIR
    os.makedirs(dest, exist_ok=True)
    path = os.path.join(dest, key + ".fbx")
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
    """The sigoff piece merges every dark lens; split it into one object per
    lamp so SignalCycle can address them.

    Grouped by plan position first, THEN by height. A signal head stacks its
    lenses on one vertical axis, so a cluster is a head; within a head the
    dark lenses are red on top and amber under it. Ranking the whole prop by
    height instead works only for a single-head signal -- the city's mast arm
    carries two heads at the same heights plus a pedestrian lamp far below, so
    a global sort would hand the second head's red to the amber list and light
    two thirds of the prop wrong.
    """
    src = [o for o in parts if o.name.startswith("sigoff")]
    if len(src) != 1:
        die("expected exactly one sigoff piece, got %d" % len(src))
    parts.remove(src[0])
    pieces = separate_loose(src)
    if len(pieces) < 2:
        die("expected 2+ loose sigoff lenses, got %d" % len(pieces))

    heads = {}
    for o in pieces:
        lo, hi = world_bbox([o])
        key = (round((lo.x + hi.x) * 0.5, 1), round((lo.y + hi.y) * 0.5, 1))
        heads.setdefault(key, []).append((lo.z + hi.z, o))

    counts = {"sigred": 0, "sigamber": 0}
    for key in sorted(heads):
        stack = sorted(heads[key], key=lambda zo: -zo[0])
        for i, (_, o) in enumerate(stack):
            # A head with one dark lens is a pedestrian lamp, which is a red.
            token = "sigamber" if i == 1 else "sigred"
            counts[token] += 1
            o.name = "%s_%d" % (token, counts[token])
    parts.extend(pieces)
    return parts


# ---------------------------------------------------------------------------
# material readout
# ---------------------------------------------------------------------------

def _upstream_constants(sock, want, seen):
    """Every constant of type `want` ('RGBA' or 'VALUE') feeding a socket.

    Procedural materials (brick, siding, shingle, leaf) drive Base Color from a
    noise-and-ramp network, so there is no single authored colour to read --
    the honest answer is the mean of the colours the network mixes between,
    which is what a surface of that material averages to from any distance a
    car ever sees it.

    The socket-type filter is the whole of the correctness here. Without it the
    walk also collects every Vector input it passes, and an unlinked Vector
    defaults to (0, 0, 0) -- so a clapboard whose two authored colours average
    to 0.404 came back as 0.101, a quarter of its real albedo, purely from
    texture-coordinate sockets voting black.
    """
    out = []
    if not sock.is_linked:
        if sock.type != want:
            return out
        v = sock.default_value
        try:
            return [tuple(v)[:3]] if len(v) >= 3 else [(float(v),) * 3]
        except TypeError:
            return [(float(v),) * 3]
    node = sock.links[0].from_node
    if node.name in seen:
        return out
    seen.add(node.name)
    if node.type == 'VALTORGB':                      # ColorRamp
        if want == 'RGBA':
            for e in node.color_ramp.elements:
                out.append(tuple(e.color)[:3])
        return out
    if node.type == 'RGB' and want == 'RGBA':
        return [tuple(node.outputs[0].default_value)[:3]]
    if node.type == 'VALUE' and want == 'VALUE':
        return [(float(node.outputs[0].default_value),) * 3]
    for inp in node.inputs:
        # A mix factor is a blend weight, not a colour -- averaging it in would
        # drag every procedural material toward mid grey.
        if inp.name in ("Fac", "Factor"):
            continue
        out += _upstream_constants(inp, want, seen)
    return out


def _read(sock, want='RGBA'):
    vals = _upstream_constants(sock, want, set())
    if not vals:
        return None
    n = len(vals)
    return [round(sum(v[i] for v in vals) / n, 4) for i in range(3)]


def srgb(c):
    """Blender's linear albedo to the sRGB triple TrackCatalog.T() takes.

    The four earlier packs were converted this way by hand -- M_Ench_Plaster's
    authored 0.430/0.398/0.330 is ench_plaster's 0.68/0.66/0.60 to three
    decimals, and Timber, Snow and Crimson match as exactly. Doing it here
    makes the printed number the number that gets pasted, instead of a linear
    value someone has to remember to encode.
    """
    return round(12.92 * c if c <= 0.0031308
                 else 1.055 * (c ** (1.0 / 2.4)) - 0.055, 4)


def read_principled(m):
    """The measured PBR of one material, or None if it has no Principled node."""
    if m is None or not m.use_nodes:
        return None
    bsdf = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
    if bsdf is None:
        return None
    base = _read(bsdf.inputs["Base Color"]) or [0.5, 0.5, 0.5]
    rough = (_read(bsdf.inputs["Roughness"], 'VALUE') or [0.5])[0]
    metal = (_read(bsdf.inputs["Metallic"], 'VALUE') or [0.0])[0]
    emis = _read(bsdf.inputs["Emission Color"]) or [0.0, 0.0, 0.0]
    strength = (_read(bsdf.inputs["Emission Strength"], 'VALUE') or [0.0])[0]
    alpha = (_read(bsdf.inputs["Alpha"], 'VALUE') or [1.0])[0]
    lit = [c * strength for c in emis]
    return {
        "color": [srgb(c) for c in base],     # paste this into T()
        "linear": base,
        "smooth": round(1.0 - rough, 4),      # Unity Standard _Glossiness
        "metal": round(metal, 4),
        "alpha": round(alpha, 4),
        # T()'s `glow` scales the sRGB colour, so the useful number is how
        # many times its own albedo the surface emits.
        "glow": round(sum(lit) / max(1e-6, sum(base)), 3),
        "emission": [round(c, 4) for c in lit],
    }


def dump_theme_materials(pack, cfg):
    """Palettes for a pack whose materials are a THEME AXIS.

    The blend carries exactly one theme's materials (whichever the kit was
    built in), but the authoring module can construct any of them -- every
    theme answers the same slot names, which is the whole point of the axis.
    So the geometry is exported once and the palette is read three times,
    rather than exporting the kit three times over.
    """
    spec = cfg["themes"]
    if SCRIPTS not in sys.path:
        sys.path.insert(0, SCRIPTS)
    try:
        mod = __import__(spec["module"])
    except Exception as e:                       # noqa: BLE001 - report and go on
        die("could not import %s from %s (%s)" % (spec["module"], SCRIPTS, e))

    for theme in spec["names"]:
        mats = mod.materials(theme)              # slot -> bpy material
        out = {}
        for slot, m in sorted(mats.items()):
            pbr = read_principled(m)
            if pbr is not None:
                out[slot] = pbr
        print("MATJSON>>>" + json.dumps(
            {"pack": pack, "theme": theme, "materials": out}) + "<<<MATJSON")


def dump_materials(pack, cfg, tokens):
    """One JSON block of authored PBR per pack, between MATJSON markers.

    The FBX carries geometry only, so every material is rebuilt in C# from
    numbers -- and those numbers are printed here rather than sampled off a
    render, for the same reason the vehicle exporter prints its own: Blender's
    FBX writer drops emission strength and coat, and a colour picked off a
    screenshot has the view transform baked into it.
    """
    out = {}
    for name, token in sorted(tokens.items()):
        m = bpy.data.materials.get(name)
        if m is None or not m.use_nodes:
            continue
        bsdf = next((n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'),
                    None)
        if bsdf is None:
            continue
        base = _read(bsdf.inputs["Base Color"]) or [0.5, 0.5, 0.5]
        rough = (_read(bsdf.inputs["Roughness"], 'VALUE') or [0.5])[0]
        metal = (_read(bsdf.inputs["Metallic"], 'VALUE') or [0.0])[0]
        emis = _read(bsdf.inputs["Emission Color"]) or [0.0, 0.0, 0.0]
        strength = (_read(bsdf.inputs["Emission Strength"], 'VALUE') or [0.0])[0]
        lit = [c * strength for c in emis]
        out[token] = {
            "color": [srgb(c) for c in base],     # paste this into T()
            "linear": base,
            "smooth": round(1.0 - rough, 4),      # Unity Standard _Glossiness
            "metal": round(metal, 4),
            # T()'s `glow` scales the sRGB colour, so the useful number is how
            # many times its own albedo the surface emits.
            "glow": round(sum(lit) / max(1e-6, sum(base)), 3),
            "emission": [round(c, 4) for c in lit],
        }
    print("MATJSON>>>" + json.dumps({"pack": pack, "materials": out})
          + "<<<MATJSON")


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

    if "themes" in cfg:
        dump_theme_materials(pack, cfg)
    else:
        dump_materials(pack, cfg, tokens)

    origin = cfg.get("origin", "base")
    dest = cfg.get("dest")

    for src in props:
        name = src.name[2:]                       # strip "P_"
        key = ("dt_" + name) if pack == "dt" else name

        dups = duplicate([src])
        if origin == "row":
            # Tiles: keep the authoring frame, undo only the lineup offset.
            # Measured, not assumed -- the kit blend stores every tile with
            # location = (x, 0, 0) and its Y/Z authored in place, so this is
            # exactly the showcase translation and nothing else.
            base = Vector((src.location.x, 0.0, 0.0))
        else:
            # Showcase props: origin at the base contact point, centred in
            # plan, because TrackFactory.ItemPose snaps roots onto the drop
            # surface and a centred origin would bury half the prop.
            lo, hi = world_bbox(dups)
            base = Vector(((lo.x + hi.x) * 0.5, (lo.y + hi.y) * 0.5, lo.z))
        bake(dups, Matrix.Scale(SCALE, 4) @ Matrix.Translation(-base))

        parts = separate_and_tokenise(dups, tokens)
        if key in SIGNAL_KEYS:
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

        export_fbx(parts, key, dest)
        report = {
            "key": key,
            "origin": origin,
            "size": to_unity_size(plo, phi),
            "tris": tris,
            "tokens": sorted({o.name.rsplit("_", 1)[0] for o in parts}),
            "pieces": pieces,
            "profile": profile(parts),
        }
        purge(parts)
        print("PROPJSON>>>" + json.dumps(report) + "<<<PROPJSON")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    want = [a for a in argv if not a.startswith("-")]
    for name in want:
        if name not in PACKS:
            die("unknown pack '%s' (have: %s)" % (name, ", ".join(PACKS)))
    for pack in (want or list(PACKS)):
        build_pack(pack, PACKS[pack])
    print("[build_map_props] done.")


main()
