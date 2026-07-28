"""Export the TinyTorque cosmetics pack into game parts.

Headless (Blender 5.2 -- the source blends are 5.x-era):
    "C:\\Program Files\\Blender Foundation\\Blender 5.2\\blender.exe" ^
        --background --python build_cosmetics.py

Re-runnable; the source blends are opened read-only-in-spirit and NEVER saved.
Writes into UnitySim/Assets/Resources/Cosmetics one FBX per unlockable
(47 items) and per crate (4), each separated by material and renamed so every
object name carries a PartMeshLibrary token, and prints a JSON block between
COSJSON>>> markers carrying the authored PBR for all 39 materials plus the
per-item scale/bounds. Those numbers are pasted into CosmeticCatalog -- never
hand-derived, because the user's requirement is that the colours, materials and
emitters survive the trip exactly.

Frames. An item's authored frame IS its mount frame: the part is modelled around
(0,0,0) = the mounting point with +X car-forward and +Z up, so attaching one in
game is a parent with an identity local pose. Rims are the exception the pack
already documents: their axis is +Y (outboard) with the origin at the hub
centre. Both reach the game convention through the SAME -90 deg rotation about
Z that build_vehicles.py uses (+X -> -Y, +Y -> +X), because the FBX axis
conversion then maps Blender X/Y/Z to Unity X/Z/-Y:

    item  source +X (forward)  -> Unity +Z (forward)
    item  source +Z (up)       -> Unity +Y (up)
    rim   source +Y (outboard) -> Unity +X (the wheel holder's axle, outboard
                                  face, matching BuildWheelViz)

Scale. Two factors, both MEASURED off the source car rather than assumed:

    s_item = 0.420 / <coupe body extent along source X>   (BODY_LEN contract)
    s_rim  = 0.033 / <coupe front-left tyre radius>       (WHEEL_AUTHOR_R)

s_rim differs from s_item on purpose: the runtime scales a wheel mesh by
radius/WheelAuthorRadius, so a rim baked to the author radius rides that same
factor and fits any wheel size. Crates are not attachments -- they are shown on
their own rig -- so they export at authored size (1 Blender metre = 1 unit) and
the rig frames them by bounds.

Mirror caveat, inherited from build_vehicles.py: Blender is right-handed and
Unity is left-handed, so source-left lands on Unity-right. Every car in the game
was imported through the same flip, so cosmetics and bodies agree.
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
    HERE, "..", "UnitySim", "Assets", "Resources", "Cosmetics"))

CAR_BLEND = "TinyTorque_car.blend"
COS_BLEND = "TinyTorque_cosmetics.blend"

BODY_LEN = 0.420        # authored body length contract (Unity Z)
WHEEL_AUTHOR_R = 0.033  # PartVisualFactory.WheelAuthorRadius

# -90 deg about Z: source +X (nose) -> -Y, source +Y (left/outboard) -> +X.
ROT = Matrix.Rotation(math.radians(-90.0), 4, 'Z')

MAT_PREFIX = "M_Cos_"


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def die(msg):
    print("[build_cosmetics] FATAL: " + msg)
    sys.exit(1)


def deselect_all():
    for o in bpy.context.view_layer.objects:
        o.select_set(False)


def meshes_under(root):
    out, stack = [], list(root.children)
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
    return [round(v.x, 5), round(v.z, 5), round(-v.y, 5)]


def unity_bounds(lo, hi):
    """Unity-space min/max from a Blender-space box. The Z axis negates, so
    feeding lo and hi straight through to_unity would report a min above its
    max on that axis -- re-sort per component instead."""
    a, b = to_unity(lo), to_unity(hi)
    return ([min(a[i], b[i]) for i in range(3)],
            [max(a[i], b[i]) for i in range(3)])


def token_of(mat_name):
    """M_Cos_Glow_gold -> glow_gold. The token AssignByName matches on."""
    if not mat_name.startswith(MAT_PREFIX):
        die("material '%s' is outside the %s namespace" % (mat_name, MAT_PREFIX))
    return mat_name[len(MAT_PREFIX):].lower()


# ---------------------------------------------------------------------------
# measurement pass: the source car sets both scale factors
# ---------------------------------------------------------------------------

def measure_car():
    blend = os.path.join(MODELS, CAR_BLEND)
    if not os.path.isfile(blend):
        die("missing " + blend)
    bpy.ops.wm.open_mainfile(filepath=blend)

    body_root = bpy.data.objects.get("CAR_BODY")
    if body_root is None:
        die("no empty 'CAR_BODY' in " + CAR_BLEND)
    # Same body-mesh selection build_vehicles.py uses for the coupe, so the two
    # exporters cannot drift apart on what "the body" means.
    excluded = {"Car_Antenna", "Car_AntennaTip", "Car_AntMount"}

    def under_excluded(o):
        p = o.parent
        while p is not None:
            if p.name in excluded:
                return True
            p = p.parent
        return False

    body = [o for o in meshes_under(body_root)
            if o.name not in excluded and not under_excluded(o)
            and not any(o.parent is not None and o.parent.name.endswith(s)
                        for s in ("_STEER", "_SPIN", "_SIDE"))]
    if not body:
        die("no body meshes found in " + CAR_BLEND)
    lo, hi = world_bbox(body)
    length_x = hi.x - lo.x

    side = bpy.data.objects.get("W_FL_SIDE")
    if side is None:
        die("no wheel empty 'W_FL_SIDE' in " + CAR_BLEND)
    wlo, whi = world_bbox(meshes_under(side))
    tire_r = max(whi.x - wlo.x, whi.z - wlo.z) * 0.5
    if length_x <= 0 or tire_r <= 0:
        die("degenerate car measurements")

    return {
        "body_len_source": round(length_x, 6),
        "tire_r_source": round(tire_r, 6),
        "s_item": BODY_LEN / length_x,
        "s_rim": WHEEL_AUTHOR_R / tire_r,
    }


# ---------------------------------------------------------------------------
# materials -> data
# ---------------------------------------------------------------------------

def _value(sock_in):
    try:
        return [round(float(x), 6) for x in sock_in.default_value]
    except TypeError:
        return round(float(sock_in.default_value), 6)


def _fresnel_pair(sock_in):
    """A Principled input driven by Mix(A=face, B=edge) off a Layer Weight is
    the pack's Fresnel trick (the ghost + fae spectres). Return (face, edge) so
    the engine can pick a flat stand-in honestly instead of guessing."""
    if not sock_in.is_linked:
        return None
    mix = sock_in.links[0].from_node
    if mix.bl_idname != 'ShaderNodeMix':
        return None
    fac = mix.inputs.get('Factor')
    if fac is None or not fac.is_linked:
        return None
    if fac.links[0].from_node.bl_idname != 'ShaderNodeLayerWeight':
        return None
    # Mix exposes one A/B pair per data type; the float pair is the first.
    a = next(i for i in mix.inputs if i.name == 'A')
    b = next(i for i in mix.inputs if i.name == 'B')
    return [round(float(a.default_value), 6), round(float(b.default_value), 6)]


def collect_materials():
    out = {}
    for m in bpy.data.materials:
        if not m.name.startswith(MAT_PREFIX) or not m.use_nodes:
            continue
        b = next((n for n in m.node_tree.nodes
                  if n.bl_idname == 'ShaderNodeBsdfPrincipled'), None)
        if b is None:
            die("material '%s' has no Principled BSDF" % m.name)
        e = {
            "token": token_of(m.name),
            "base": _value(b.inputs['Base Color'])[:3],
            "metallic": _value(b.inputs['Metallic']),
            "roughness": _value(b.inputs['Roughness']),
            "emission": _value(b.inputs['Emission Color'])[:3],
            "coat": _value(b.inputs['Coat Weight']),
            "sheen": _value(b.inputs['Sheen Weight']),
            "aniso": _value(b.inputs['Anisotropic']),
            "blend": m.blend_method,
        }
        alpha_pair = _fresnel_pair(b.inputs['Alpha'])
        glow_pair = _fresnel_pair(b.inputs['Emission Strength'])
        e["alpha"] = 1.0 if alpha_pair else _value(b.inputs['Alpha'])
        e["emission_strength"] = (0.0 if glow_pair
                                  else _value(b.inputs['Emission Strength']))
        if alpha_pair:
            e["alpha_fresnel"] = alpha_pair      # [face, edge]
        if glow_pair:
            e["glow_fresnel"] = glow_pair        # [face, edge]
        out[m.name] = e
    return out


# ---------------------------------------------------------------------------
# per-item export
# ---------------------------------------------------------------------------

def duplicate(o):
    """A real (non-linked) duplicate at the identity pose: the authored mesh
    frame IS the mount frame, so the object's shelf position on the library
    chart is dropped rather than baked."""
    d = o.copy()
    d.data = o.data.copy()
    d.parent = None
    d.matrix_world = Matrix.Identity(4)
    bpy.context.scene.collection.objects.link(d)
    return d


def bake(objs, xform):
    deselect_all()
    for o in objs:
        o.matrix_world = xform @ o.matrix_world
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def separate_and_tokenise(obj):
    """Separate by material, drop unused slots, rename to <token>_<n>.
    Returns (objects, ordered token list); dies on an unnamespaced material."""
    deselect_all()
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.separate(type='MATERIAL')
    bpy.ops.object.mode_set(mode='OBJECT')
    result = [o for o in bpy.context.view_layer.objects
              if o.type == 'MESH' and o.select_get()]

    deselect_all()
    counts, tokens = {}, []
    for o in result:
        if o.material_slots:
            o.select_set(True)
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.material_slot_remove_unused()
            o.select_set(False)
        mats = [s.material for s in o.material_slots if s.material]
        if len(mats) != 1:
            die("%s has %d materials after separate (expected 1)"
                % (o.name, len(mats)))
        token = token_of(mats[0].name)
        counts[token] = counts.get(token, 0) + 1
        o.name = "%s_%d" % (token, counts[token])
        if token not in tokens:
            tokens.append(token)
    return result, tokens


def export_fbx(objs, key):
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    os.makedirs(FBX_DIR, exist_ok=True)
    path = os.path.join(FBX_DIR, key + ".fbx")
    # Identical to build_vehicles.export_fbx: apply_unit_scale=False +
    # global_scale=0.01 cancels the m->cm bake, and PartModelPostprocessor
    # (useFileScale off, globalScale 1) reads the raw numbers.
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True,
        apply_unit_scale=False, global_scale=0.01,
        axis_forward='-Z', axis_up='Y', bake_space_transform=True,
        object_types={'MESH'}, use_mesh_modifiers=True,
        mesh_smooth_type='EDGE', use_tspace=True, path_mode='COPY')
    return path


def purge(objs):
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)


def build_one(src, key, scale):
    dup = duplicate(src)
    bake([dup], Matrix.Scale(scale, 4) @ ROT)
    parts, tokens = separate_and_tokenise(dup)
    lo, hi = world_bbox(parts)
    tris = 0
    for o in parts:
        o.data.calc_loop_triangles()      # not populated until asked for
        tris += len(o.data.loop_triangles)
    path = export_fbx(parts, key)
    size = os.path.getsize(path)
    purge(parts)
    print("[build_cosmetics] wrote %s (%d objects, %d bytes)"
          % (os.path.basename(path), len(parts), size))
    umin, umax = unity_bounds(lo, hi)
    return {
        "key": key,
        "scale": round(scale, 6),
        "objects": len(parts),
        "tokens": tokens,
        "bounds_lo": umin,
        "bounds_hi": umax,
        # Unity XYZ = width, height, depth.
        "extent": [round(hi.x - lo.x, 5), round(hi.z - lo.z, 5),
                   round(hi.y - lo.y, 5)],
        "tris": tris,
        "bytes": size,
    }


def main():
    car = measure_car()
    print("[build_cosmetics] car: body_len=%.5f -> s_item=%.6f | tire_r=%.5f "
          "-> s_rim=%.6f" % (car["body_len_source"], car["s_item"],
                             car["tire_r_source"], car["s_rim"]))

    blend = os.path.join(MODELS, COS_BLEND)
    if not os.path.isfile(blend):
        die("missing " + blend)
    bpy.ops.wm.open_mainfile(filepath=blend)

    mats = collect_materials()
    print("[build_cosmetics] %d materials" % len(mats))

    sources = sorted((o for o in bpy.data.objects
                      if o.type == 'MESH'
                      and (o.name.startswith("C_") or o.name.startswith("B_"))),
                     key=lambda o: o.name)
    if not sources:
        die("no C_*/B_* meshes in " + COS_BLEND)

    items = {}
    for src in sources:
        crate = src.name.startswith("B_")
        key = src.name[2:]
        # Rims ride the wheel's own scale; crates keep authored metres and are
        # framed by their rig; everything else lands on a 1/10 body.
        scale = 1.0 if crate else (car["s_rim"] if key.startswith("rim_")
                                   else car["s_item"])
        rec = build_one(src, key, scale)
        rec["kind"] = "crate" if crate else "item"
        items[key] = rec

    report = {"car": {k: (round(v, 6) if isinstance(v, float) else v)
                      for k, v in car.items()},
              "materials": mats, "items": items}
    print("COSJSON>>> " + json.dumps(report))
    print("[build_cosmetics] %d FBX written to %s" % (len(items), FBX_DIR))


main()
