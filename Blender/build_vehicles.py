"""Export the three TinyTorque_RC show cars into game parts.

Headless (Blender 5.2 -- the source blends are 5.x-era):
    "C:\\Program Files\\Blender Foundation\\Blender 5.2\\blender.exe" ^
        --background --python build_vehicles.py

Re-runnable; the source blends are opened read-only-in-spirit and NEVER saved.
For each car it writes into UnitySim/Assets/Resources/PartModels:

    body_<key>.fbx      everything under <NAME>_BODY except the light and
                        antenna groups, separated by material and renamed so
                        every object name carries a PartMeshLibrary token
    wheel_<key>.fbx     the front-left W_xx_SIDE subtree (tire/rim/barrel/
                        disc/nut -- calipers and uprights are dropped: the
                        runtime spins the whole viz holder, a baked caliper
                        would orbit the axle), scaled to author radius 0.033
    light_<...>.fbx     baja: roof pod cluster; patrol: the roof light bar
    antenna_<...>.fbx   coupe: whip + amber tip; baja: whip + flag;
                        patrol: both whips baked as one centred part

and prints one JSON block per car (between VEHJSON>>> markers) carrying the
scale, the Unity-space wheel positions/radius and the light/antenna mount
points. Those numbers are pasted into VehiclePresets -- never hand-derived.

Frames. The source models author the nose at +X, +Z up, wheels thin along Y.
The proven game convention (build_bodies.py / build_wheels.py) is nose at
Blender -Y for bodies and axle along +X with the OUTBOARD face at +X for
wheels; Blender X/Y/Z map to Unity X/Z/Y. Both are reached here with the same
-90 deg rotation about Z (+X -> -Y, +Y -> +X: the front-left wheel's outboard
side is source +Y). Body origin: wheel-set centre in plan, height such that
wheel centres land at Unity y = -0.045 (the stock authoring contract).
Body scale: s = 0.420 / full body extent along source X, so the validator's
pinned Z = 0.420 holds by construction.

Mirror caveat: if asymmetric details (the POLICE lettering) render mirrored
in game, flip the sign of ROT_DEG's companion mirror flag below -- one line.
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
    HERE, "..", "UnitySim", "Assets", "Resources", "PartModels"))

WHEEL_AUTHOR_R = 0.033          # PartVisualFactory.WheelAuthorRadius
BODY_LEN = 0.420                # authored body length contract (Unity Z)
WHEEL_DROP = 0.045              # wheel centres sit at Unity y = -0.045

# -90 deg about Z: source +X (nose) -> -Y, source +Y (left) -> +X.
ROT = Matrix.Rotation(math.radians(-90.0), 4, 'Z')

# ---------------------------------------------------------------------------
# material -> object-name token
# ---------------------------------------------------------------------------
# Body/wheel tokens; AssignByName is first-match substring, so every token is
# chosen to be unambiguous against the others and against legacy tokens.
TOKENS = {
    # tintable paint channel (authored 0.8 grey on all three cars)
    "M_Paint": "paint", "M_Buggy_Paint": "paint", "M_Police_Paint": "paint",
    # fixed accents
    "M_Dark": "dark", "M_Buggy_Dark": "dark", "M_Police_Dark": "dark",
    "M_Buggy_Seat": "dark",
    "M_Gunmetal": "gunmetal", "M_Buggy_Engine": "gunmetal",
    "M_Buggy_Strut": "gunmetal", "M_Buggy_Skid": "gunmetal",
    "M_Police_Steel": "gunmetal",
    "M_Chrome": "chrome", "M_Buggy_Chrome": "chrome",
    "M_Police_Chrome": "chrome",
    "M_Gold_Trim": "gold", "M_Police_Gold": "gold",
    "M_Glass": "glass", "M_Police_Glass": "glass",
    "M_Carbon": "carbon",
    "M_Buggy_Tube": "tube",
    "M_Buggy_Belt": "orange", "M_Buggy_Spring": "orange",
    "M_Buggy_Rim": "orange", "M_Buggy_Flag": "orange",
    "M_Buggy_Roundel": "white", "M_Police_PlateFace": "white",
    "M_Police_Decal": "decal",
    "M_Police_BarBody": "dark",
    # tyres + brake discs (wheel FBXs)
    "M_Tire": "tire", "M_Buggy_Tire": "tire", "M_Police_Tire": "tire",
    # emissives
    "M_HeadLight": "em_head", "M_Police_Head": "em_head",
    "M_Buggy_Lens": "em_head",
    "M_TailLight": "em_tail", "M_Buggy_Tail": "em_tail",
    "M_Police_Tail": "em_tail",
    "M_Amber": "em_amber",
    "M_Police_Red": "em_red", "M_Police_Blue": "em_blue",
    "M_Police_White": "barwhite",
}
NO_MATERIAL_TOKEN = "gunmetal"   # the buggy's bare suspension links

# Wheel-FBX overrides: the brake disc must read "brake" whatever its metal is.
WHEEL_DISC_TOKEN = "brake"

# Antenna-FBX overrides: whip bodies read "whip" whatever their base material.
ANT_TOKENS = {
    "M_Dark": "whip", "M_Buggy_Dark": "whip", "M_Police_Dark": "whip",
    "M_Gunmetal": "base",
    "M_Amber": "em_amber",
    "M_Buggy_Flag": "flag",
}

CARS = {
    "coupe": {
        "blend": "TinyTorque_car.blend",
        "body_empty": "CAR_BODY",
        "wheel_side": "W_FL_SIDE",
        "light_objs": [],
        "light_split_z": None,
        "light_key": None,
        "ant_objs": ["Car_Antenna", "Car_AntennaTip", "Car_AntMount"],
        "ant_key": "antenna_whip",
    },
    "baja": {
        "blend": "TinyTorque_buggy.blend",
        "body_empty": "BUGGY_BODY",
        "wheel_side": "BW_FL_SIDE",
        # LightCans/Lenses each combine the NOSE lights and the ROOF pods in
        # one mesh (x 0.28..2.19, z 0.57..1.75). The exporter splits them by
        # loose parts: pieces whose centre sits above light_split_z become the
        # light part, the nose lights return to the body.
        "light_objs": ["Buggy_LightCans", "Buggy_Lenses"],
        "light_split_z": 1.25,
        "light_key": "light_pods",
        "ant_objs": ["Buggy_Whip", "Buggy_Flag"],
        "ant_key": "antenna_flag",
    },
    "patrol": {
        "blend": "TinyTorque_police.blend",
        "body_empty": "POLICE_BODY",
        "wheel_side": "PW_FL_SIDE",
        # Police_Strobe_red/blue are FRONT GRILLE strobes (x ~2.4, z ~0.84),
        # not part of the roof bar -- they stay in the body as static
        # emissives; only the bar animates in game.
        "light_objs": ["Police_BarBase", "Police_BarCaps", "Police_BarFeet",
                       "Police_Bar_Red", "Police_Bar_Blue", "Police_Bar_White"],
        "light_split_z": None,
        "light_key": "light_bar",
        "ant_objs": ["Police_Antennas"],
        "ant_key": "antenna_twin",
    },
}


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def die(msg):
    print("[build_vehicles] FATAL: " + msg)
    sys.exit(1)


def deselect_all():
    for o in bpy.context.view_layer.objects:
        o.select_set(False)


def meshes_under(root):
    """Every MESH in root's subtree (root excluded)."""
    out = []
    stack = list(root.children)
    while stack:
        o = stack.pop()
        stack.extend(o.children)
        if o.type == 'MESH':
            out.append(o)
    return out


def world_bbox(objs):
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            lo = Vector(map(min, lo, w))
            hi = Vector(map(max, hi, w))
    return lo, hi


def to_unity(v):
    """Export-frame Blender point -> Unity (X, Y, Z) = (x, z, -y)."""
    return [round(v.x, 4), round(v.z, 4), round(-v.y, 4)]


def duplicate(objs):
    """Real (non-linked) duplicates, parent cleared, world transform kept."""
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
    """Bake xform @ matrix_world into each object's vertices (scale included),
    leaving the object transform at identity."""
    deselect_all()
    for o in objs:
        o.matrix_world = xform @ o.matrix_world
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def separate_and_tokenise(objs, tokens, no_mat_token):
    """Separate by material, drop unused slots, rename to <token>_<n>.
    Returns the final object list; dies on an unmapped material."""
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='MATERIAL')
    bpy.ops.object.mode_set(mode='OBJECT')
    # separate leaves both the originals and the new splits selected
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
            token = no_mat_token
        else:
            token = tokens.get(mats[0].name)
            if token is None:
                die("no token mapped for material '%s' (object %s)"
                    % (mats[0].name, o.name))
        counts[token] = counts.get(token, 0) + 1
        o.name = "%s_%d" % (token, counts[token])
    return result


def separate_loose(objs):
    """Split each object into its loose parts; returns the piece list."""
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


def ensure_uvs(objs):
    for o in objs:
        if o.name.startswith("paint") and not o.data.uv_layers:
            print("[build_vehicles] WARN: %s has no UVs -- smart projecting"
                  % o.name)
            deselect_all()
            o.select_set(True)
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.mode_set(mode='EDIT')
            bpy.ops.mesh.select_all(action='SELECT')
            bpy.ops.uv.smart_project(angle_limit=math.radians(66))
            bpy.ops.object.mode_set(mode='OBJECT')


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
    print("[build_vehicles] wrote %s (%d objects, %d bytes)"
          % (path, len(objs), os.path.getsize(path)))
    return path


def purge(objs):
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)


# ---------------------------------------------------------------------------
# per-car build
# ---------------------------------------------------------------------------

def build_car(key, cfg):
    blend = os.path.join(MODELS, cfg["blend"])
    if not os.path.isfile(blend):
        die("missing " + blend)
    bpy.ops.wm.open_mainfile(filepath=blend)

    body_root = bpy.data.objects.get(cfg["body_empty"])
    if body_root is None:
        die("no empty '%s' in %s" % (cfg["body_empty"], cfg["blend"]))

    excluded = set(cfg["light_objs"]) | set(cfg["ant_objs"])
    body_meshes = [o for o in body_root.children if o.type == 'MESH'
                   and o.name not in excluded]
    # antenna meshes may hang under a sub-empty (coupe ANT_BASE): resolve by
    # name anywhere in the file, and also drop any mesh whose ancestor empty
    # is itself excluded.
    def under_excluded(o):
        p = o.parent
        while p is not None:
            if p.name in excluded:
                return True
            p = p.parent
        return False
    body_meshes = [o for o in meshes_under(body_root)
                   if o.name not in excluded and not under_excluded(o)
                   and not any(o.parent is not None and o.parent.name.endswith(s)
                               for s in ("_STEER", "_SPIN", "_SIDE"))]
    if not body_meshes:
        die("no body meshes for " + key)

    # ---- measurements (source frame) --------------------------------------
    lo, hi = world_bbox(body_meshes)
    length_x = hi.x - lo.x
    s = BODY_LEN / length_x

    steer_empties = [o for o in bpy.data.objects
                     if o.type == 'EMPTY' and o.name.endswith("_STEER")]
    if len(steer_empties) != 4:
        die("expected 4 *_STEER empties, found %d" % len(steer_empties))
    wheel_centres = [o.matrix_world.translation.copy() for o in steer_empties]
    cx = sum(w.x for w in wheel_centres) / 4.0
    cy = sum(w.y for w in wheel_centres) / 4.0
    wz = sum(w.z for w in wheel_centres) / 4.0

    side = bpy.data.objects.get(cfg["wheel_side"])
    if side is None:
        die("no wheel empty '%s'" % cfg["wheel_side"])
    wheel_meshes = meshes_under(side)
    wlo, whi = world_bbox(wheel_meshes)
    tire_r = max(whi.x - wlo.x, whi.z - wlo.z) * 0.5
    wheel_centre = side.matrix_world.translation.copy()

    # Body export transform: centre on the wheel set, rotate nose -Y, scale,
    # then drop so wheel centres land at Blender z = -WHEEL_DROP (Unity
    # y = -0.045; wz is at z=0 after centering, so only the constant remains).
    M_body = (Matrix.Translation((0.0, 0.0, -WHEEL_DROP))
              @ Matrix.Scale(s, 4)
              @ ROT
              @ Matrix.Translation((-cx, -cy, -wz)))

    report = {
        "key": key, "scale": round(s, 6),
        "body_len_source": round(length_x, 4),
        "wheel_radius": round(tire_r * s, 6),
        "wheels": {},
    }
    for o in steer_empties:
        p = M_body @ o.matrix_world.translation
        tag = o.name.split("_")[-2]          # FL / FR / RL / RR
        report["wheels"][tag] = to_unity(p)

    # ---- light group split (before the body export, so nose-light pieces
    # can rejoin the body) ---------------------------------------------------
    light_part_objs = []
    extra_body_objs = []
    if cfg["light_key"]:
        ldups = duplicate([bpy.data.objects[n] for n in cfg["light_objs"]])
        if cfg["light_split_z"] is not None:
            for piece in separate_loose(ldups):
                plo, phi = world_bbox([piece])
                if (plo.z + phi.z) * 0.5 >= cfg["light_split_z"]:
                    light_part_objs.append(piece)
                else:
                    extra_body_objs.append(piece)
            if not light_part_objs:
                die("light_split_z classified nothing as the light part")
        else:
            light_part_objs = ldups

    # ---- body -------------------------------------------------------------
    dups = duplicate(body_meshes) + extra_body_objs
    bake(dups, M_body)
    parts = separate_and_tokenise(dups, TOKENS, NO_MATERIAL_TOKEN)
    ensure_uvs(parts)
    export_fbx(parts, "body_" + key)
    purge(parts)

    # ---- wheel (front-left SIDE subtree; outboard = source +Y -> +X) ------
    sw = WHEEL_AUTHOR_R / tire_r
    M_wheel = (Matrix.Scale(sw, 4) @ ROT
               @ Matrix.Translation(-wheel_centre))
    dups = duplicate(wheel_meshes)
    bake(dups, M_wheel)
    wtok = dict(TOKENS)
    # the disc must read "brake" whatever metal it carries
    for m in ("M_Gunmetal", "M_Buggy_Engine", "M_Police_Steel"):
        wtok[m] = WHEEL_DISC_TOKEN
    parts = separate_and_tokenise(dups, wtok, "dark")
    export_fbx(parts, "wheel_" + key)
    purge(parts)

    # ---- light part (origin = group bbox bottom-centre, so localPos sits
    # the part straight onto a roof surface) --------------------------------
    if light_part_objs:
        llo, lhi = world_bbox(light_part_objs)
        base = Vector(((llo.x + lhi.x) * 0.5, (llo.y + lhi.y) * 0.5, llo.z))
        M_light = Matrix.Scale(s, 4) @ ROT @ Matrix.Translation(-base)
        bake(light_part_objs, M_light)
        parts = separate_and_tokenise(light_part_objs, TOKENS, "dark")
        export_fbx(parts, cfg["light_key"])
        purge(parts)
        report["light_key"] = cfg["light_key"]
        report["light_mount"] = to_unity(M_body @ base)
    else:
        report["light_key"] = None
        report["light_mount"] = None

    # ---- antenna part (origin = group bbox bottom-centre) -----------------
    objs = [bpy.data.objects[n] for n in cfg["ant_objs"]]
    alo, ahi = world_bbox(objs)
    base = Vector(((alo.x + ahi.x) * 0.5, (alo.y + ahi.y) * 0.5, alo.z))
    M_ant = Matrix.Scale(s, 4) @ ROT @ Matrix.Translation(-base)
    dups = duplicate(objs)
    bake(dups, M_ant)
    parts = separate_and_tokenise(dups, ANT_TOKENS, "whip")
    export_fbx(parts, cfg["ant_key"])
    purge(parts)
    report["ant_key"] = cfg["ant_key"]
    report["ant_mount"] = to_unity(M_body @ base)

    print("VEHJSON>>>" + json.dumps(report) + "<<<VEHJSON")


def main():
    for key, cfg in CARS.items():
        build_car(key, cfg)
    print("[build_vehicles] done.")


main()
