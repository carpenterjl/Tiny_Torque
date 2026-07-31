"""Export the 1:1 Volkswagen Tiguan into game parts, at actual size.

Headless (Blender 5.2):
    "C:\\Program Files\\Blender Foundation\\Blender 5.2\\blender.exe" ^
        --background --factory-startup --python build_tiguan_part.py
    ... --python build_tiguan_part.py -- --wheel-only     # frame check first

The source is TinyTorque_RC/models/TinyTorque_tiguan.blend, built by that
repo's scripts/build_tiguan.py (+ tt_38_tiguan.py). Re-save it there before
running this (`build_tiguan.py -- --save`); this script opens it read-only and
never writes it back. Writes into UnitySim/Assets/Resources/PartModels:

    body_tiguan.fbx        everything under TIGUAN_BODY except the wheel rig,
                           separated by material and renamed so every object
                           name carries a PartMeshLibrary token
    wheel_tiguan.fbx       the front-left W_FL_SIDE subtree (tyre/rim/cap/
                           bolts/disc)
    wheel_tiguan_r.fbx     the rear-left W_RL_SIDE subtree - identical but for
                           the disc (solid 300x12 rear vs vented 340x30 front),
                           which is the ONLY brake hardware still visible once
                           the calipers are dropped, so getting it wrong is the
                           whole rear-brake story on both rear corners
    tiguan_materials.json  the 30 authored materials as numbers

plus one VEHJSON block carrying the measured Unity-space wheel positions and
body bounds. Those are PASTED into DebugVehicles/PartModelValidator, never
hand-derived - same discipline as build_vehicles.py, and it matters more here
because the frame mapping below is not the one a first-principles reading of
UNITY_EXPORT.md would give you.

WHY THIS IS A SEPARATE SCRIPT FROM build_vehicles.py
----------------------------------------------------
Three constants differ and nothing else does:

    BODY_LEN     build_vehicles scales every arcade shell to length 0.420 so
                 PartModelValidator's pinned Z holds by construction. This car
                 is 1:1 by definition - the entire reason it exists is to be
                 measured against a real Tiguan - so there is no scale at all.
    WHEEL_SCALE  likewise: no rescale to the 33 mm author radius.
    WHEEL_DROP   0.5615 here against 0.045 there (derived below).

Sharing one script behind a flag would put "am I the real car?" inside every
transform in a file that seven shipped assets depend on. The helpers below are
COPIED from build_vehicles.py rather than imported because that module calls
main() at import time - importing it would silently re-export all seven cars.

WHICH FBX CONTRACT, AND WHY IT IS NOT THE CIRCUITS ONE
------------------------------------------------------
TinyTorque_RC/scripts/export_unity.py uses identity FBX axes and swaps the
vertices itself, and UNITY_EXPORT.md section 9 argues that at length. That
reasoning is about the CIRCUITS' consumer, which pulls the Mesh out of the
asset and builds its own GameObject - so a conversion parked on the model root
never happens and has to be done to the data.

PartMeshLibrary.TryInstantiate does the opposite: it instantiates the imported
model, so the root conversion DOES happen and is exactly what you want. The
contract for a Resources/PartModels asset is build_vehicles.py's, copied
verbatim below. Copying section 9's settings here instead produces a car lying
on its side, and the marker test that would catch it is circuits-only.

FRAME
-----
The model authors the nose at +X, +Z up, wheels thin along Y - the same as
every other car in this pipeline - so ROT is the same -90 deg about Z, and the
front-left wheel's outboard face is source +Y. Body origin: the wheel-set
centre in plan, dropped so the wheel centres land WHEEL_DROP below it.

WHEEL_DROP is the one number this file shares with DebugVehicles.VwTiguan()
by convention rather than by paste, so it is derived here in full:

    chassis box centre above ground = (clearance + roof) / 2
                                    = (0.189 + 1.632) / 2 = 0.9105
    wheel centre above ground       = loaded radius       = 0.349  (MEASURED:
                                      the W_*_STEER empties sit at z 0.3490)
    WHEEL_DROP                      = 0.9105 - 0.349      = 0.5615

If the preset's localPos.y stops being -0.5615 the car floats or sinks and
nothing anywhere reports it.

The collision box is deliberately NOT the mesh bounds. Measured bounds are
2.0990 x 1.4716 x 4.4864, which include the door mirrors (width 2.099 against
the body's 1.839) and the roof rails (top 1.673 against the roof's 1.632).
Neither is solid. The box is the published body; the rails and mirrors poke
out of it, which is correct.

WHAT IS DROPPED, AND WHAT THAT COSTS
------------------------------------
Calipers and dust shields parent to W_*_STEER, not W_*_SIDE, so meshes_under()
never sees them - the same filter build_vehicles.py uses, for the same reason:
the runtime spins the whole wheel viz holder, and a baked caliper would orbit
the axle. The cost is that the car has no brake calipers at all. Mounting them
would need a second FBX on the steering knuckle and the runtime has no mount
point for one.

M_TigPaint SURVIVES AS A COLOUR, NOT AS A PAINT
------------------------------------------------
It is a procedural metallic flake: a Voronoi mask at scale 500 driving base
colour, metallic, roughness and a normal together, under a clearcoat with
orange peel at 320. probe_materials() resolves it to the flake/binder mean and
the Map Range bands - a dark navy metallic. The flakes, the sparkle, the tilt
normal and the peel are gone. That is the honest answer for an untextured
Built-in-RP kit: the Unity car is the same COLOUR as the Blender car, it is
not the same PAINT. Recovering it would mean baking to a texture, and the
shell has no UVs to bake to.
"""

import bpy
import json
import math
import os
import sys
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
BLEND = os.path.join(r"E:\EE Projects\AI_3D_Modeling\TinyTorque_RC\models",
                     "TinyTorque_tiguan.blend")
FBX_DIR = os.path.normpath(os.path.join(
    HERE, "..", "UnitySim", "Assets", "Resources", "PartModels"))

BODY_EMPTY = "TIGUAN_BODY"

# No scale-to-length and no scale-to-author-radius: this car is 1:1 by
# definition. See the module docstring.
WHEEL_DROP = 0.5615             # chassis origin -> wheel-centre plane, metres

# -90 deg about Z: source +X (nose) -> -Y, source +Y (left) -> +X.
ROT = Matrix.Rotation(math.radians(-90.0), 4, 'Z')

# Published figures the box is built from, kept here so the derivation above is
# checkable and so the export can report measured-against-published.
SPEC = dict(length=4.486, width=1.839, width_mirrors=2.099, height=1.632,
            clearance=0.189, rail_rise=0.041, wheelbase=2.681,
            track_f=1.585, track_r=1.576, loaded_radius=0.349)

# ---------------------------------------------------------------------------
# material -> object-name token
# ---------------------------------------------------------------------------
# Every token is prefixed "tig" so it can never collide with the 40 tokens in
# PartVisualFactory.AccentTokens or the 15 in WheelTokens - the Tiguan binds
# against its OWN table (PartVisualFactory.TiguanTokens, built from the JSON
# this script writes), and keeping the namespaces disjoint means neither table
# can ever be a trap for the other.
#
# AssignByName is a first-match SUBSTRING test, so "tiglens" would swallow
# "tiglensred". That ordering is not maintained here: TiguanMaterials.cs sorts
# the table by descending token length, which makes compound-before-contained
# a property of the loader rather than a rule someone has to remember. The
# comment at PartVisualFactory.cs:187 records two shipped bugs from getting the
# hand-ordered version wrong.
TIG_TOKENS = {
    # paint and body panels
    "M_TigPaint": "tigpaint",
    "M_TigClad": "tigclad",
    "M_TigGloss": "tiggloss",
    "M_TigDark": "tigdark",
    "M_TigChrome": "tigchrome",
    "M_TigAlu": "tigalu",
    "M_TigRail": "tigrail",
    "M_TigMesh": "tigmesh",
    "M_TigTrim": "tigtrim",
    # glazing
    "M_TigGlass": "tigglass",
    "M_TigPrivacy": "tigprivacy",
    # lamps: lenses, reflectors, and the three emitters
    "M_TigLens": "tiglens",
    "M_TigLensRed": "tiglensred",
    "M_TigLensAmber": "tiglensamber",
    "M_TigReflector": "tigreflector",
    "M_TigEmitW": "tigemitw",
    "M_TigEmitR": "tigemitr",
    "M_TigEmitA": "tigemita",
    # underbody and exhaust
    "M_TigExhaust": "tigexhaust",
    "M_TigShield": "tigshield",
    "M_TigRubber": "tigrubber",
    # interior
    "M_TigInterior": "tiginterior",
    "M_TigSeat": "tigseat",
    # number plates - the EU strip is its own two materials
    "M_TigPlate": "tigplate",
    "M_TIG_EU_BLUE": "tigeublue",
    "M_TIG_EU_STAR": "tigeustar",
    # wheels
    "M_TigRim": "tigrim",
    "M_TigTread": "tigtread",
    "M_TigDisc": "tigdisc",
    # Present in the blend but structurally unreachable by this export: the
    # caliper parents to W_*_STEER and the floor is PREVIEW scenery outside
    # TIGUAN_BODY. Mapped anyway - a token costs nothing, and die() firing on
    # a material the model legitimately contains would be a confusing failure.
    "M_TigCaliper": "tigcaliper",
    "M_TigFloor": "tigfloor",
}


# ---------------------------------------------------------------------------
# helpers (copied from build_vehicles.py - see the docstring for why copied)
# ---------------------------------------------------------------------------

def die(msg):
    print("[build_tiguan_part] FATAL: " + msg)
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
    """Real (non-linked) duplicates, parent cleared, world transform kept.

    The four corners are LINKED duplicates sharing mesh data, so the .copy()
    of the data here is what keeps baking one corner's transform from writing
    through into the other three.
    """
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
    """Bake xform @ matrix_world into each object's vertices, leaving the
    object transform at identity."""
    deselect_all()
    for o in objs:
        o.matrix_world = xform @ o.matrix_world
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def separate_and_tokenise(objs, tokens):
    """Separate by material, drop unused slots, rename to <token>_<n>.
    Returns the final object list; dies on an unmapped material.

    Dying is the point. A silent fallthrough would put a Tiguan panel on some
    other material and read as a lighting bug, not as a naming bug.
    """
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
            die("%s has no material - every Tiguan surface is authored with "
                "one, so this is a model change, not a default to paper over"
                % o.name)
        token = tokens.get(base_mat_name(mats[0].name))
        if token is None:
            die("no token mapped for material '%s' (object %s)"
                % (mats[0].name, o.name))
        counts[token] = counts.get(token, 0) + 1
        o.name = "%s_%d" % (token, counts[token])
    return result


def export_fbx(objs, key):
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    os.makedirs(FBX_DIR, exist_ok=True)
    path = os.path.join(FBX_DIR, key + ".fbx")
    # VERBATIM from build_vehicles.py:407. apply_unit_scale=False +
    # global_scale=0.01 cancels the m->cm bake, so with the importer's
    # useFileScale=false / globalScale=1 one Blender metre is one Unity unit.
    # EDGE smoothing carries the split normals. Do not substitute the circuits
    # exporter's arguments here - see the module docstring.
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True,
        apply_unit_scale=False, global_scale=0.01,
        axis_forward='-Z', axis_up='Y', bake_space_transform=True,
        object_types={'MESH'}, use_mesh_modifiers=True,
        mesh_smooth_type='EDGE', use_tspace=True, path_mode='COPY')
    tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objs)
    print("[build_tiguan_part] wrote %s (%d objects, %d tris, %d bytes)"
          % (path, len(objs), tris, os.path.getsize(path)))
    return path, tris


def purge(objs):
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)


# ---------------------------------------------------------------------------
# materials, as numbers
# ---------------------------------------------------------------------------
# Copied from TinyTorque_RC/scripts/export_unity.py:300-438 and extended. The
# copy is deliberate for the same reason as the helpers above, and because the
# two extensions (coat, emission) are needed by a car and not by a circuit.
#
# The standing rule this implements: no PBR number is ever retyped on the Unity
# side. TiguanMaterials.cs has no colour table in it.

_MIX_NODES = ('ShaderNodeMix', 'ShaderNodeMixRGB')


def base_mat_name(n):
    """`M_TigPaint.001` -> `M_TigPaint`.

    Blender uniquifies a duplicate datablock with a numeric suffix. The
    manifest binds by name, so a leaked suffix binds nothing and the object
    comes out default grey - which looks like a missing material rather than
    like a naming bug.
    """
    if len(n) > 4 and n[-4] == "." and n[-3:].isdigit():
        return n[:-4]
    return n


def _socket_rgb(sock, depth=0):
    """Resolve a colour socket to one representative linear RGB.

    Follows links a few levels: a two-tone mix averages its ends, a colour ramp
    averages its stops. "Representative" is the honest word - a procedural
    two-tone noise has no single colour, and its mean is the closest thing.
    """
    if not sock.is_linked:
        v = sock.default_value
        return (float(v[0]), float(v[1]), float(v[2]))
    if depth > 4:
        return (0.5, 0.5, 0.5)
    n = sock.links[0].from_node
    if n.bl_idname == 'ShaderNodeValToRGB':
        els = n.color_ramp.elements
        if len(els):
            return tuple(sum(e.color[i] for e in els) / len(els)
                         for i in range(3))
    if n.bl_idname in _MIX_NODES:
        cols = [_socket_rgb(s, depth + 1) for s in n.inputs if s.type == 'RGBA']
        if cols:
            return tuple(sum(c[i] for c in cols) / len(cols) for i in range(3))
    if n.bl_idname == 'ShaderNodeRGB':
        v = n.outputs[0].default_value
        return (float(v[0]), float(v[1]), float(v[2]))
    for s in n.inputs:
        if s.type == 'RGBA':
            return _socket_rgb(s, depth + 1)
    return (0.5, 0.5, 0.5)


def _socket_num(sock, fallback=0.0):
    if sock is None:
        return fallback
    if not sock.is_linked:
        try:
            return float(sock.default_value)
        except TypeError:
            return fallback
    n = sock.links[0].from_node
    # A Map Range driving roughness or metallic from noise: its declared output
    # band is a better answer than the default on an unread socket.
    if n.bl_idname == 'ShaderNodeMapRange':
        lo = n.inputs.get('To Min')
        hi = n.inputs.get('To Max')
        if lo is not None and hi is not None and not lo.is_linked \
                and not hi.is_linked:
            return 0.5 * (float(lo.default_value) + float(hi.default_value))
    return fallback


def probe_materials(names):
    """The palette, as numbers, for every material name the export uses."""
    out = []
    for nm in sorted(set(names)):
        if not nm:
            continue
        token = TIG_TOKENS.get(nm)
        if token is None:
            die("probe: no token for material '%s'" % nm)
        m = bpy.data.materials.get(nm)
        b = None
        if m is not None and m.node_tree is not None:
            for n in m.node_tree.nodes:
                if n.bl_idname == 'ShaderNodeBsdfPrincipled':
                    b = n
                    break
        if b is None:
            out.append(dict(token=token, name=nm, rgb=[0.5, 0.5, 0.5],
                            metallic=0.0, transmission=0.0, smoothness=0.3,
                            coat=0.0, coatSmoothness=0.0,
                            emission=[0.0, 0.0, 0.0], emissionStrength=0.0,
                            probed=False))
            continue
        rgb = _socket_rgb(b.inputs['Base Color'])
        rough = _socket_num(b.inputs.get('Roughness'), 0.5)

        # Unity's Standard shader has no clearcoat. Probing base roughness
        # alone gives a matte car: M_TigPaint is 0.20-0.48 rough UNDER a
        # 0.04-rough coat, and what you see on a real car is the coat. Both
        # numbers ship and TiguanMaterials.cs states the one rule that
        # combines them - which is a rule, not a hand-tuned number.
        coat = _socket_num(b.inputs.get('Coat Weight'), 0.0)
        coat_rough = _socket_num(b.inputs.get('Coat Roughness'), 0.03)

        # M_TigEmitW/R/A are pure emitters over a black base. Drop emission and
        # every lamp on the car becomes a hole.
        estr = _socket_num(b.inputs.get('Emission Strength'), 0.0)
        ergb = (0.0, 0.0, 0.0)
        if estr > 0.0:
            ergb = _socket_rgb(b.inputs['Emission Color'])

        # The glazing is transmissive and the cabin is modelled, so opaque glass
        # would hide an interior that is actually there. Probed rather than
        # keyed off the token name in C#: "is this surface see-through" is a
        # property of the material, and the one place that knows it is here.
        trans = _socket_num(b.inputs.get('Transmission Weight'), 0.0)

        out.append(dict(
            token=token, name=nm,
            rgb=[round(v, 6) for v in rgb],
            metallic=round(_socket_num(b.inputs.get('Metallic'), 0.0), 4),
            transmission=round(trans, 4),
            # Unity Standard wants smoothness, which is what every other
            # generated material in this project uses.
            smoothness=round(1.0 - rough, 4),
            coat=round(coat, 4),
            coatSmoothness=round(1.0 - coat_rough, 4),
            emission=[round(v, 6) for v in ergb],
            emissionStrength=round(estr, 4),
            probed=True))
    return out


# ---------------------------------------------------------------------------
# the export
# ---------------------------------------------------------------------------

def under_wheel_rig(o):
    """True for anything hanging off a steering/spinning wheel empty.

    Same test build_vehicles.py applies, and it is what keeps the calipers and
    dust shields (parented to _STEER) out of a mesh the runtime will spin.
    """
    p = o.parent
    while p is not None:
        if p.name.endswith(("_STEER", "_SPIN", "_SIDE")):
            return True
        p = p.parent
    return False


def build(wheel_only=False):
    if not os.path.isfile(BLEND):
        die("missing " + BLEND + " - run TinyTorque_RC/scripts/build_tiguan.py"
            " -- --save first")
    bpy.ops.wm.open_mainfile(filepath=BLEND)

    body_root = bpy.data.objects.get(BODY_EMPTY)
    if body_root is None:
        die("no empty '%s' in the blend" % BODY_EMPTY)

    # The PREVIEW collection's lights, cameras and floor are not under
    # TIGUAN_BODY, so they drop out structurally rather than by name - a
    # renamed light cannot leak into the export.
    body_meshes = [o for o in meshes_under(body_root) if not under_wheel_rig(o)]
    if not body_meshes:
        die("no body meshes under " + BODY_EMPTY)

    # ---- measurements (source frame) --------------------------------------
    steer = [o for o in bpy.data.objects
             if o.type == 'EMPTY' and o.name.endswith("_STEER")]
    if len(steer) != 4:
        die("expected 4 *_STEER empties, found %d" % len(steer))
    centres = [o.matrix_world.translation.copy() for o in steer]
    cx = sum(w.x for w in centres) / 4.0
    cy = sum(w.y for w in centres) / 4.0
    wz = sum(w.z for w in centres) / 4.0

    # The derivation in the docstring assumes the wheel centres sit at the
    # loaded radius. Check it rather than trusting it: if the model's hub
    # height ever moves, WHEEL_DROP is wrong and nothing downstream would say
    # so - the car would just sit low.
    if abs(wz - SPEC["loaded_radius"]) > 1e-4:
        die("wheel centres at z=%.4f but WHEEL_DROP was derived from the "
            "loaded radius %.4f - re-derive it (see the module docstring)"
            % (wz, SPEC["loaded_radius"]))

    lo, hi = world_bbox(body_meshes)
    report = {
        "key": "tiguan",
        "scale": 1.0,
        "wheel_drop": WHEEL_DROP,
        "measured": {
            "length": round(hi.x - lo.x, 4),
            "width_mirrors": round(hi.y - lo.y, 4),
            "floor": round(lo.z, 4),
            "roof_rails": round(hi.z, 4),
            "hub_height": round(wz, 4),
        },
        "published": dict(SPEC),
        "wheels": {},
    }

    # Body export transform: centre on the wheel set in plan, rotate the nose
    # to -Y, then drop so the wheel centres land at Blender z = -WHEEL_DROP.
    # No Matrix.Scale - that is the whole point of this file.
    M_body = (Matrix.Translation((0.0, 0.0, -WHEEL_DROP))
              @ ROT
              @ Matrix.Translation((-cx, -cy, -wz)))

    for o in steer:
        p = M_body @ o.matrix_world.translation
        tag = o.name.split("_")[-2]          # FL / FR / RL / RR
        report["wheels"][tag] = to_unity(p)

    # ---- wheels ------------------------------------------------------------
    # W_FL_SIDE and W_RL_SIDE are already the prototypes: the tyre/rim/disc
    # builders author in hub-local space and the SIDE empty is the only thing
    # placing them, so the left-hand corners are the identity ones (the right
    # side's pi yaw lives on its own _SIDE empty). No un-instancing needed
    # beyond duplicate()'s data copy.
    wheel_names = []
    for side_name, key in (("W_FL_SIDE", "wheel_tiguan"),
                           ("W_RL_SIDE", "wheel_tiguan_r")):
        side = bpy.data.objects.get(side_name)
        if side is None:
            die("no wheel empty '%s'" % side_name)
        wheel_meshes = meshes_under(side)
        if not wheel_meshes:
            die("no meshes under " + side_name)
        wheel_names += [base_mat_name(s.material.name)
                        for o in wheel_meshes for s in o.material_slots
                        if s.material]
        centre = side.matrix_world.translation.copy()
        M_wheel = ROT @ Matrix.Translation(-centre)
        dups = duplicate(wheel_meshes)
        bake(dups, M_wheel)
        parts = separate_and_tokenise(dups, TIG_TOKENS)
        wlo, whi = world_bbox(parts)
        report[key] = {
            "bounds": [round(whi.x - wlo.x, 4), round(whi.z - wlo.z, 4),
                       round(-(wlo.y - whi.y), 4)],
            "objects": len(parts),
        }
        _, tris = export_fbx(parts, key)
        report[key]["tris"] = tris
        purge(parts)

    if wheel_only:
        report["body_skipped"] = True
        print("VEHJSON>>>" + json.dumps(report) + "<<<VEHJSON")
        return

    # ---- body --------------------------------------------------------------
    body_names = [base_mat_name(s.material.name)
                  for o in body_meshes for s in o.material_slots if s.material]
    dups = duplicate(body_meshes)
    bake(dups, M_body)
    parts = separate_and_tokenise(dups, TIG_TOKENS)
    blo, bhi = world_bbox(parts)
    report["body_tiguan"] = {
        "bounds": [round(bhi.x - blo.x, 4), round(bhi.z - blo.z, 4),
                   round(-(blo.y - bhi.y), 4)],
        "objects": len(parts),
    }
    _, tris = export_fbx(parts, "body_tiguan")
    report["body_tiguan"]["tris"] = tris
    purge(parts)

    # ---- material manifest -------------------------------------------------
    mats = probe_materials(body_names + wheel_names)
    path = os.path.join(FBX_DIR, "tiguan_materials.json")
    doc = {
        "schema": 1,
        "units": "metres",
        "generator": "Blender/build_tiguan_part.py",
        "materials": mats,
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    unprobed = [m["name"] for m in mats if not m["probed"]]
    print("[build_tiguan_part] wrote %s (%d materials, %d unprobed%s)"
          % (path, len(mats), len(unprobed),
             (": " + ", ".join(unprobed)) if unprobed else ""))
    report["materials"] = len(mats)

    print("VEHJSON>>>" + json.dumps(report) + "<<<VEHJSON")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    build(wheel_only="--wheel-only" in argv)
    print("[build_tiguan_part] done.")


main()
