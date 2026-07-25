"""Reusable Blender helpers for the AI Hardware Control Sim part-mesh pipeline.

Blender MCP session state resets between calls, so this module is re-loaded at the
top of every call with:

    exec(open(r"E:\\EE Projects\\AI Hardware Control Sim (Unity)\\Blender\\mcp_helpers.py").read(), globals())

Authoring frame (Blender):  X = width, Y = length (nose at -Y), Z = up.
Export maps it to Unity:    X = width, Y = up,     Z = length (nose at +Z).

Contract sizes (metres, Unity axes) enforced by `check_contract`:
    body_*         X 0.200, Z 0.420 exactly   (Y/height free)
    wheel_*        outer tyre radius 0.0330    (axle +X)
    battery_stick  0.047 X * 0.025 Y * 0.138 Z (long axis +Z)
    antenna_stub   0.095 - 0.110 tall (+Y)
"""

import bpy, bmesh, math, os
from mathutils import Vector

PROJECT = r"E:\EE Projects\AI Hardware Control Sim (Unity)"
BLEND_PATH = os.path.join(PROJECT, "Blender", "parts.blend")
FBX_DIR = os.path.join(PROJECT, "UnitySim", "Assets", "Resources", "PartModels")
RENDER_DIR = os.path.join(PROJECT, "Blender", "renders")

# ---------------------------------------------------------------------------
# scene / object utilities
# ---------------------------------------------------------------------------

def ensure_file():
    """Open parts.blend if this session isn't already in it."""
    if os.path.normcase(bpy.data.filepath) != os.path.normcase(BLEND_PATH):
        bpy.ops.wm.open_mainfile(filepath=BLEND_PATH)
    return bpy.data.filepath


def save():
    bpy.ops.wm.save_mainfile(filepath=BLEND_PATH)
    return BLEND_PATH


def coll(name, create=True):
    c = bpy.data.collections.get(name)
    if c is None and create:
        c = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(c)
    return c


def coll_objects(name):
    c = bpy.data.collections.get(name)
    return [o for o in c.objects if o.type == 'MESH'] if c else []


def purge(name):
    """Delete every object in a collection (and its orphaned meshes)."""
    c = bpy.data.collections.get(name)
    if c is None:
        return 0
    n = 0
    for o in list(c.objects):
        me = o.data if o.type == 'MESH' else None
        bpy.data.objects.remove(o, do_unlink=True)
        if me is not None and me.users == 0:
            bpy.data.meshes.remove(me)
        n += 1
    return n


def activate(obj, deselect_others=True):
    if deselect_others:
        for o in bpy.context.view_layer.objects:
            o.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    return obj


def new_mesh_obj(name, collection_name):
    me = bpy.data.meshes.new(name)
    ob = bpy.data.objects.new(name, me)
    coll(collection_name).objects.link(ob)
    return ob


def apply_transforms(obj):
    activate(obj)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def world_bbox(objects):
    lo = Vector(( 1e9,  1e9,  1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objects:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            lo = Vector((min(lo[i], w[i]) for i in range(3)))
            hi = Vector((max(hi[i], w[i]) for i in range(3)))
    return lo, hi


def bbox_size(objects):
    lo, hi = world_bbox(objects)
    return hi - lo


def bbox_center(objects):
    lo, hi = world_bbox(objects)
    return (lo + hi) * 0.5


def normalize(objects, target=None, center=None, uniform=False):
    """Scale/translate a group so its world bbox matches `target` (metres, per-axis;
    None entries are left free) and its centre lands on `center` (default origin).
    Object transforms are applied afterwards so exported data carries the result."""
    size = bbox_size(objects)
    s = Vector((1.0, 1.0, 1.0))
    if target is not None:
        ratios = []
        for i in range(3):
            t = target[i]
            if t is not None and size[i] > 1e-9:
                ratios.append(t / size[i])
                s[i] = t / size[i]
        if uniform and ratios:
            k = sum(ratios) / len(ratios)
            s = Vector((k, k, k))
    for o in objects:
        o.scale = Vector((o.scale[i] * s[i] for i in range(3)))
    bpy.context.view_layer.update()
    c = bbox_center(objects)
    want = Vector(center) if center is not None else Vector((0.0, 0.0, 0.0))
    d = want - c
    for o in objects:
        o.location = o.location + d
    bpy.context.view_layer.update()
    for o in objects:
        apply_transforms(o)
    return [round(v * 1000, 2) for v in bbox_size(objects)]


# ---------------------------------------------------------------------------
# mesh validation
# ---------------------------------------------------------------------------

def mesh_report(obj, evaluated=False, doubles_dist=1e-5):
    """Topology/health report. `evaluated=True` measures the post-modifier result
    (the tri count Unity will actually see)."""
    if isinstance(obj, str):
        obj = bpy.data.objects[obj]
    dg = bpy.context.evaluated_depsgraph_get()
    src = obj.evaluated_get(dg) if evaluated else obj
    me = src.to_mesh() if evaluated else obj.data

    bm = bmesh.new()
    bm.from_mesh(me)

    tris = quads = ngons = 0
    thin = 0
    for f in bm.faces:
        n = len(f.verts)
        if n == 3:
            tris += 1
        elif n == 4:
            quads += 1
        else:
            ngons += 1
        # long-thin face check: longest/shortest edge ratio
        ls = [e.calc_length() for e in f.edges]
        if ls and min(ls) > 1e-9 and max(ls) / min(ls) > 12.0:
            thin += 1

    wire = boundary = nonmanifold = 0
    for e in bm.edges:
        lf = len(e.link_faces)
        if lf == 0:
            wire += 1
        elif lf == 1:
            boundary += 1
        elif lf > 2:
            nonmanifold += 1

    loose = sum(1 for v in bm.verts if not v.link_edges)
    poles = sum(1 for v in bm.verts if len(v.link_edges) >= 6)

    found = bmesh.ops.find_doubles(bm, verts=bm.verts, dist=doubles_dist)
    doubles = len(found.get("targetmap", {}))

    tri_count = sum(len(f.verts) - 2 for f in bm.faces)
    faces = len(bm.faces)
    d = src.dimensions if evaluated else obj.dimensions

    bm.free()
    if evaluated:
        src.to_mesh_clear()

    return {
        "name": obj.name,
        "evaluated": evaluated,
        "dim_mm": [round(v * 1000, 2) for v in d],
        "verts": len(me.vertices),
        "faces": faces,
        "tris": tri_count,
        "quad_pct": round(100.0 * quads / faces, 1) if faces else 0.0,
        "n_tris": tris, "n_quads": quads, "n_ngons": ngons,
        "thin_faces": thin,
        "wire_edges": wire, "boundary_edges": boundary, "nonmanifold_edges": nonmanifold,
        "loose_verts": loose, "poles_6plus": poles, "doubles": doubles,
        "uv_layers": [u.name for u in me.uv_layers],
        "modifiers": [m.type for m in obj.modifiers],
    }


def report_collection(name, evaluated=True):
    return [mesh_report(o, evaluated=evaluated) for o in coll_objects(name)]


def clean_mesh(obj, merge_dist=1e-5):
    """Merge doubles, recalculate normals outside, drop loose verts/edges."""
    if isinstance(obj, str):
        obj = bpy.data.objects[obj]
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=merge_dist)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    loose_v = [v for v in bm.verts if not v.link_edges]
    bmesh.ops.delete(bm, geom=loose_v, context='VERTS')
    loose_e = [e for e in bm.edges if not e.link_faces]
    bmesh.ops.delete(bm, geom=loose_e, context='EDGES')
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return obj.name


def finalize(obj, subd=1, smooth_angle_deg=40.0, weighted_normals=True):
    """Apply SubD at `subd` levels (0 = none), shade auto-smooth, add + apply a
    Weighted Normal modifier. Leaves a clean single-user mesh ready for export."""
    if isinstance(obj, str):
        obj = bpy.data.objects[obj]
    activate(obj)
    if subd and subd > 0:
        m = obj.modifiers.new("SubD", 'SUBSURF')
        m.levels = subd
        m.render_levels = subd
        m.use_limit_surface = False
        bpy.ops.object.modifier_apply(modifier=m.name)
    clean_mesh(obj)
    bpy.ops.object.shade_auto_smooth(angle=math.radians(smooth_angle_deg))
    if weighted_normals:
        wn = obj.modifiers.new("WeightedNormal", 'WEIGHTED_NORMAL')
        wn.keep_sharp = True
        wn.mode = 'FACE_AREA_WITH_ANGLE'
        bpy.ops.object.modifier_apply(modifier=wn.name)
    return mesh_report(obj)


def uv_unwrap(obj, angle_deg=66.0, margin=0.02):
    """Smart UV Project - required on body_* (runtime paint reads textureCoord)."""
    if isinstance(obj, str):
        obj = bpy.data.objects[obj]
    activate(obj)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(angle_deg), island_margin=margin)
    bpy.ops.object.mode_set(mode='OBJECT')
    return [u.name for u in obj.data.uv_layers]


# ---------------------------------------------------------------------------
# contract check
# ---------------------------------------------------------------------------

CONTRACT = {
    # key: (unity_x, unity_y, unity_z) in metres; None = free. Unity axes.
    "body_shell":    (0.200, None, 0.420),
    "body_lowracer": (0.200, None, 0.420),
    "body_buggy":    (None,  None, 0.420),   # flares may exceed 0.200 core width
    "wheel_slick":   (None, 0.066, 0.066),
    "wheel_knobby":  (None, 0.066, 0.066),
    "wheel_rally":   (None, 0.066, 0.066),
    "battery_stick": (0.047, 0.025, 0.138),
    "antenna_stub":  (None, None, None),
}


def check_contract(key, tol_mm=2.0):
    """Measure a collection in UNITY axes (blender X,Y,Z -> unity X,Z,Y) and compare
    against the contract."""
    objs = coll_objects(key)
    if not objs:
        return {"key": key, "error": "no objects"}
    bs = bbox_size(objs)
    bc = bbox_center(objs)
    # blender (x, y, z) -> unity (x, z, y)
    usize = (bs.x, bs.z, bs.y)
    ucenter = (bc.x, bc.z, bc.y)
    want = CONTRACT.get(key)
    fails = []
    if want:
        for i, w in enumerate(want):
            if w is not None and abs(usize[i] - w) * 1000 > tol_mm:
                fails.append("XYZ"[i] + f"={usize[i]*1000:.1f}mm want {w*1000:.1f}mm")
    tris = 0
    dg = bpy.context.evaluated_depsgraph_get()
    for o in objs:
        ev = o.evaluated_get(dg)
        me = ev.to_mesh()
        tris += sum(len(p.vertices) - 2 for p in me.polygons)
        ev.to_mesh_clear()
    return {
        "key": key,
        "objects": [o.name for o in objs],
        "unity_size_mm": [round(v * 1000, 2) for v in usize],
        "unity_center_mm": [round(v * 1000, 2) for v in ucenter],
        "tris": tris,
        "pass": not fails,
        "fails": fails,
    }


# ---------------------------------------------------------------------------
# render
# ---------------------------------------------------------------------------

def _pick_engine(prefer):
    avail = [i.identifier for i in
             bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items]
    for e in prefer:
        if e in avail:
            return e
    return avail[0]


def _studio_rig(objects, direction, res, engine, ortho=False):
    scn = bpy.context.scene
    scn.render.engine = engine
    scn.render.resolution_x = res
    scn.render.resolution_y = res
    scn.render.resolution_percentage = 100
    scn.render.film_transparent = False
    if engine == 'BLENDER_WORKBENCH':
        sh = scn.display.shading
        sh.light = 'STUDIO'
        sh.color_type = 'SINGLE'
        sh.single_color = (0.62, 0.63, 0.66)
        sh.show_cavity = True
        sh.cavity_type = 'BOTH'
        sh.show_object_outline = False
    else:
        w = bpy.data.worlds.get("StudioWorld") or bpy.data.worlds.new("StudioWorld")
        w.use_nodes = True
        w.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.055, 0.06, 1)
        w.node_tree.nodes["Background"].inputs[1].default_value = 1.0
        scn.world = w

    cam = bpy.data.objects.get("STUDIO_CAM")
    if cam is None:
        cd = bpy.data.cameras.new("STUDIO_CAM")
        cam = bpy.data.objects.new("STUDIO_CAM", cd)
        bpy.context.scene.collection.objects.link(cam)
    cam.data.type = 'ORTHO' if ortho else 'PERSP'
    cam.data.lens = 58.0
    cam.data.sensor_fit = 'AUTO'
    scn.camera = cam

    lo, hi = world_bbox(objects)
    ctr = (lo + hi) * 0.5
    radius = max((hi - lo).length * 0.5, 1e-4)
    d = Vector(direction).normalized()
    if ortho:
        cam.data.ortho_scale = radius * 2.35
        dist = radius * 6.0
    else:
        # Fit from the camera's real half-FOV, not a guessed one - assuming 30
        # degrees against a 58 mm lens (~19 deg) cropped every long subject.
        dist = radius / math.sin(cam.data.angle * 0.5) * 1.12
    cam.location = ctr + d * dist
    fwd = (ctr - cam.location).normalized()
    cam.rotation_euler = fwd.to_track_quat('-Z', 'Y').to_euler()

    # three-point lights (EEVEE/Cycles only; workbench ignores them)
    if engine != 'BLENDER_WORKBENCH':
        specs = [("KEY", (1.2, -1.4, 1.6), 60.0), ("FILL", (-1.6, -0.8, 0.6), 18.0),
                 ("RIM", (-0.4, 1.8, 1.0), 35.0)]
        for nm, off, power in specs:
            lo_ = bpy.data.objects.get("STUDIO_" + nm)
            if lo_ is None:
                ld = bpy.data.lights.new("STUDIO_" + nm, 'AREA')
                lo_ = bpy.data.objects.new("STUDIO_" + nm, ld)
                bpy.context.scene.collection.objects.link(lo_)
            lo_.data.type = 'AREA'
            lo_.data.size = radius * 4.0
            lo_.data.energy = power * (radius ** 2) * LIGHT_GAIN
            lo_.location = ctr + Vector(off).normalized() * (radius * 6.0)
            lo_.rotation_euler = (ctr - lo_.location).normalized().to_track_quat('-Z', 'Y').to_euler()
    return cam


# Light energy multiplier. AgX view transform + these area lights; tuned so a
# mid-grey override material renders around 0.5 without clipping.
LIGHT_GAIN = 1.6

VIEWS = {
    # body-centric names (nose at -Y)
    "iso":   (1.0, -1.0, 0.65),
    "front": (0.0, -1.0, 0.08),
    "side":  (1.0,  0.0, 0.08),
    "top":   (0.0, -0.02, 1.0),
    "rear":  (0.0,  1.0, 0.15),
    "low":   (0.9, -1.0, 0.12),
    # wheel-centric aliases (axle along X): 'face' looks down the axle at the rim,
    # 'tread' looks at the contact patch side-on.
    "face":  (1.0,  0.0, 0.0),
    "tread": (0.0, -1.0, 0.0),
    "wiso":  (0.85, -0.9, 0.45),
}

WHEEL_VIEWS = ("wiso", "face", "tread", "top")


def _override_mat(style):
    """Shared material overrides. 'matte' reads form/silhouette; 'shiny' is the
    reflection pass - a chrome-ish sweep that exposes lumps, pinching and waviness
    the matte pass hides."""
    specs = {
        "matte": ((0.55, 0.56, 0.58, 1), 0.0, 0.45),
        "shiny": ((0.35, 0.36, 0.40, 1), 0.85, 0.13),
        "paint": ((0.12, 0.22, 0.55, 1), 0.20, 0.22),
    }
    base, metal, rough = specs.get(style, specs["matte"])
    nm = "STUDIO_" + style.upper()
    m = bpy.data.materials.get(nm)
    if m is None:
        m = bpy.data.materials.new(nm)
        m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = base
    bsdf.inputs["Metallic"].default_value = metal
    bsdf.inputs["Roughness"].default_value = rough
    return m


def render_objects(objects, view="iso", out_name="objs.png", res=900,
                   engine=None, style="matte", ortho=False):
    """Render an explicit object list in isolation - use to inspect one part of a
    multi-object assembly (e.g. just the rim, without the tyre hiding it)."""
    objects = [bpy.data.objects[o] if isinstance(o, str) else o for o in objects]
    if not objects:
        return {"error": "no objects"}
    eng = engine or _pick_engine(['BLENDER_EEVEE', 'BLENDER_EEVEE_NEXT',
                                  'BLENDER_WORKBENCH', 'CYCLES'])
    prev = {}
    for o in bpy.context.scene.objects:
        prev[o.name] = o.hide_render
        o.hide_render = (o.type == 'MESH' and o not in objects)
    _studio_rig(objects, VIEWS.get(view, VIEWS["iso"]), res, eng, ortho=ortho)
    bpy.context.view_layer.material_override = _override_mat(style)
    os.makedirs(RENDER_DIR, exist_ok=True)
    path = os.path.join(RENDER_DIR, out_name)
    bpy.context.scene.render.filepath = path
    bpy.context.scene.render.image_settings.file_format = 'PNG'
    bpy.ops.render.render(write_still=True)
    bpy.context.view_layer.material_override = None
    for o in bpy.context.scene.objects:
        if o.name in prev:
            o.hide_render = prev[o.name]
    return path


def render_part(key, view="iso", out_name=None, res=900, engine=None,
                style="matte", ortho=False):
    """Render one collection in isolation. This Blender build exposes EEVEE only,
    so form vs. surface-quality passes are distinguished by material override
    (`style`: matte / shiny / paint) rather than by render engine."""
    objects = coll_objects(key)
    if not objects:
        return {"error": "no objects in " + key}
    eng = engine or _pick_engine(['BLENDER_EEVEE', 'BLENDER_EEVEE_NEXT',
                                  'BLENDER_WORKBENCH', 'CYCLES'])
    # isolate
    prev = {}
    for o in bpy.context.scene.objects:
        prev[o.name] = o.hide_render
        o.hide_render = (o.type == 'MESH' and o not in objects)
    _studio_rig(objects, VIEWS.get(view, VIEWS["iso"]), res, eng, ortho=ortho)

    bpy.context.view_layer.material_override = _override_mat(style)

    os.makedirs(RENDER_DIR, exist_ok=True)
    fn = out_name or f"{key}_{view}_{style}.png"
    path = os.path.join(RENDER_DIR, fn)
    bpy.context.scene.render.filepath = path
    bpy.context.scene.render.image_settings.file_format = 'PNG'
    bpy.ops.render.render(write_still=True)

    bpy.context.view_layer.material_override = None
    for o in bpy.context.scene.objects:
        if o.name in prev:
            o.hide_render = prev[o.name]
    return path


def render_sheet(key, views=("iso", "front", "side", "top"), res=700, style="matte"):
    return [render_part(key, v, res=res, style=style) for v in views]


def contact_sheet(key, out_name=None, res=520,
                  views=("iso", "front", "side", "top")):
    """Render several views + a reflection pass and stitch them into one PNG so a
    whole asset can be judged in a single image."""
    paths = [render_part(key, v, res=res, style="matte") for v in views]
    paths.append(render_part(key, "iso", res=res, style="shiny"))
    imgs = [bpy.data.images.load(p, check_existing=False) for p in paths]
    n = len(imgs)
    W, H = res * n, res
    out = bpy.data.images.new("_sheet", W, H, alpha=False)
    buf = [0.0] * (W * H * 4)
    for k, im in enumerate(imgs):
        px = list(im.pixels)
        for y in range(res):
            src = y * res * 4
            dst = (y * W + k * res) * 4
            buf[dst:dst + res * 4] = px[src:src + res * 4]
    out.pixels = buf
    os.makedirs(RENDER_DIR, exist_ok=True)
    path = os.path.join(RENDER_DIR, out_name or (key + "_sheet.png"))
    out.filepath_raw = path
    out.file_format = 'PNG'
    out.save()
    for im in imgs:
        bpy.data.images.remove(im)
    bpy.data.images.remove(out)
    return path


# ---------------------------------------------------------------------------
# export
# ---------------------------------------------------------------------------

def export_part(key, filename=None):
    """Export a collection to FBX for Unity.

    apply_unit_scale=False + global_scale=0.01 cancels the m->cm bake that would
    otherwise import 100x oversized (PartModelPostprocessor forces
    useFileScale=false / globalScale=1, so Unity reads the FBX's raw numbers).
    mesh_smooth_type='EDGE' carries the auto-smooth / weighted-normal split normals.
    """
    objects = coll_objects(key)
    if not objects:
        return {"error": "no objects in " + key}
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]

    os.makedirs(FBX_DIR, exist_ok=True)
    path = os.path.join(FBX_DIR, filename or (key + ".fbx"))
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True,
        apply_unit_scale=False, global_scale=0.01,
        axis_forward='-Z', axis_up='Y', bake_space_transform=True,
        object_types={'MESH'}, use_mesh_modifiers=True,
        mesh_smooth_type='EDGE', use_tspace=True, path_mode='COPY')
    return {"path": path, "bytes": os.path.getsize(path),
            "objects": [o.name for o in objects]}
