"""Hard-surface rebuild of battery_stick and antenna_stub.

Run after mcp_helpers.py:
    exec(open(.../mcp_helpers.py).read(), globals())
    exec(open(.../build_small.py).read(), globals())
    build_battery(); build_antenna()

Frames (Blender X/Y/Z -> Unity X/Z/Y):
    battery_stick   long axis +Y (Unity +Z), origin at the pack centre.
                    Contract 0.047 x 0.025 x 0.138 m in Unity =
                    X 47 x Y 138 x Z 25 mm here. NOTHING may exceed it: the
                    runtime applies no scale to this part, so authored size is
                    rendered size. The connector housing is therefore budgeted
                    *inside* the 138 mm rather than hanging off the end - the
                    cell body is 126 mm and the housing takes the last 12 mm,
                    which is also how a real pack with an end connector reads.
    antenna_stub    up is +Z (Unity +Y), origin at the base of the SMA.
                    ~100 mm tall.

Object names carry the material tokens PartMeshLibrary.AssignByName matches:
battery wrap_/cell_/term_/nub_/lead_, antenna base_/sma_/whip_.
"""

import bpy, bmesh, math

MM = 0.001


# ---------------------------------------------------------------------------
# primitives
# ---------------------------------------------------------------------------

def _obj(name, coll_name, bm):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    c = bpy.data.collections.get(coll_name)
    if c is None:
        c = bpy.data.collections.new(coll_name)
        bpy.context.scene.collection.children.link(c)
    c.objects.link(ob)
    return ob


def quad_cap_z(bm, loop, centre):
    """Fill an even vertex loop with quads around one centre vertex."""
    n = len(loop)
    if n < 4 or n % 2:
        return
    c = bm.verts.new(centre)
    for k in range(0, n, 2):
        bm.faces.new((loop[k], loop[(k + 1) % n], loop[(k + 2) % n], c))


def box(bm, cx, cy, cz, sx, sy, sz):
    """Axis-aligned box from centre + full sizes (mm)."""
    hx, hy, hz = sx * 0.5, sy * 0.5, sz * 0.5
    pts = [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, hy, -hz), (-hx, hy, -hz),
           (-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]
    v = [bm.verts.new(((cx + p[0]) * MM, (cy + p[1]) * MM, (cz + p[2]) * MM))
         for p in pts]
    for f in ((0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
              (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
        bm.faces.new([v[i] for i in f])
    return v


def revolve_z(bm, profile, segments, cx=0.0, cy=0.0):
    """Revolve a (z, r) profile about the +Z axis. Open profile: the two ends
    are capped, so use it for posts and whips."""
    rings = []
    for s in range(segments):
        a = 2.0 * math.pi * s / segments
        ca, sa = math.cos(a), math.sin(a)
        rings.append([bm.verts.new(((cx + r * ca) * MM, (cy + r * sa) * MM, z * MM))
                      for (z, r) in profile])
    n = len(profile)
    for s in range(segments):
        t = (s + 1) % segments
        for i in range(n - 1):
            bm.faces.new((rings[s][i], rings[s][i + 1],
                          rings[t][i + 1], rings[t][i]))
    quad_cap_z(bm, [rings[s][0] for s in range(segments)][::-1],
               (cx * MM, cy * MM, profile[0][0] * MM))
    quad_cap_z(bm, [rings[s][n - 1] for s in range(segments)],
               (cx * MM, cy * MM, profile[-1][0] * MM))
    return rings


def cylinder_y(bm, x, z, radius, y0, y1, segments=8):
    """Capped cylinder with its axis along Y (battery power leads)."""
    loops = []
    for y in (y0, y1):
        loops.append([bm.verts.new((
            (x + radius * math.cos(2 * math.pi * s / segments)) * MM,
            y * MM,
            (z + radius * math.sin(2 * math.pi * s / segments)) * MM))
            for s in range(segments)])
    for s in range(segments):
        t = (s + 1) % segments
        bm.faces.new((loops[0][s], loops[0][t], loops[1][t], loops[1][s]))
    quad_cap_z(bm, list(reversed(loops[0])), (x * MM, y0 * MM, z * MM))
    quad_cap_z(bm, loops[1], (x * MM, y1 * MM, z * MM))


def bevel_obj(ob, width, segments=2, angle_deg=35.0):
    bpy.context.view_layer.objects.active = ob
    m = ob.modifiers.new("Bevel", 'BEVEL')
    m.width = width * MM
    m.segments = segments
    m.limit_method = 'ANGLE'
    m.angle_limit = math.radians(angle_deg)
    bpy.ops.object.modifier_apply(modifier=m.name)


def finish(ob, smooth_deg=35.0):
    clean_mesh(ob, merge_dist=1e-6)
    activate(ob)
    bpy.ops.object.shade_auto_smooth(angle=math.radians(smooth_deg))
    wn = ob.modifiers.new("WeightedNormal", 'WEIGHTED_NORMAL')
    wn.keep_sharp = True
    bpy.ops.object.modifier_apply(modifier=wn.name)


# ---------------------------------------------------------------------------
# battery_stick - 2S 1/10 stick pack, 138 x 47 x 25 mm
# ---------------------------------------------------------------------------

CELL_LEN = 126.0            # heat-shrunk cell body
CONN_LEN = 12.0             # connector housing, budgeted inside the 138
PACK_W, PACK_H = 47.0, 25.0
HALF_Y = (CELL_LEN + CONN_LEN) * 0.5     # 69.0


def build_battery():
    key = "battery_stick"
    purge(key)
    cell_c = -HALF_Y + CELL_LEN * 0.5      # cell body centre along Y

    # heat-shrunk cell body: softened edges, never a sharp cube
    bm = bmesh.new()
    box(bm, 0.0, cell_c, 0.0, PACK_W, CELL_LEN, PACK_H)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    wrap = _obj("wrap_pack", key, bm)
    bevel_obj(wrap, 2.6, segments=2, angle_deg=35.0)

    finish(wrap)

    # Heat-shrink overlap seam. Insetting a band on the beveled box does not
    # work - its long side faces are single quads, so the "band" selection is
    # the whole flank. A separate band standing 0.2 mm proud is what actually
    # reads as the wrap seam, and it costs a handful of faces.
    bm = bmesh.new()
    box(bm, 0.0, cell_c, 0.0, PACK_W + 0.4, 9.0, PACK_H + 0.4)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    seam = _obj("cell_seam", key, bm)
    bevel_obj(seam, 2.8, segments=2, angle_deg=35.0)
    finish(seam)

    # connector housing at the tail, filling the last 12 mm of the envelope
    bm = bmesh.new()
    box(bm, 0.0, HALF_Y - CONN_LEN * 0.5, -2.5, 17.0, CONN_LEN, 12.0)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    term = _obj("term_connector", key, bm)
    bevel_obj(term, 0.9, segments=1, angle_deg=40.0)
    finish(term)

    # Main power leads. They run *over* the housing rather than through it -
    # buried at the housing's own height they were completely hidden inside it.
    bm = bmesh.new()
    y0 = HALF_Y - CELL_LEN * 0.0 - CONN_LEN - 7.0
    y1 = HALF_Y - 1.5
    for sx in (-1, 1):
        cylinder_y(bm, sx * 5.5, 5.0, 2.2, y0, y1, segments=8)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    lead = _obj("lead_power", key, bm)
    finish(lead)

    # balance lead plug on the top face
    bm = bmesh.new()
    box(bm, 12.0, HALF_Y - CONN_LEN - 9.0, PACK_H * 0.5 - 1.0, 11.0, 6.0, 4.5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    nub = _obj("nub_balance", key, bm)
    bevel_obj(nub, 0.5, segments=1, angle_deg=40.0)
    finish(nub)

    return check_contract(key)


# ---------------------------------------------------------------------------
# antenna_stub - rubber-duck whip on an SMA base, ~100 mm tall
# ---------------------------------------------------------------------------

def build_antenna():
    key = "antenna_stub"
    purge(key)

    # knurled SMA base: 12 facets read as knurling at this size
    bm = bmesh.new()
    revolve_z(bm, [(0.0, 4.3), (0.8, 4.7), (5.2, 4.7), (6.0, 4.3)], 12)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    base = _obj("base_sma", key, bm)
    finish(base, smooth_deg=25.0)

    # hex coupling nut with a chamfer top and bottom
    bm = bmesh.new()
    revolve_z(bm, [(6.0, 3.6), (6.7, 4.2), (10.3, 4.2), (11.0, 3.6)], 6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    nut = _obj("sma_nut", key, bm)
    finish(nut, smooth_deg=25.0)

    # flexible whip: gentle taper to a rounded tip
    prof = [(11.0, 3.30), (16.0, 3.20), (34.0, 2.95), (56.0, 2.60),
            (78.0, 2.20), (92.0, 1.90), (96.5, 1.70), (98.6, 1.20),
            (99.6, 0.62)]
    bm = bmesh.new()
    revolve_z(bm, prof, 10)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    whip = _obj("whip_ant", key, bm)
    finish(whip, smooth_deg=45.0)

    return check_contract(key)


def build_small_all():
    return {"battery": build_battery(), "antenna": build_antenna()}
