"""Hard-surface rebuild of wheel_slick / wheel_knobby / wheel_rally.

Run after loading mcp_helpers.py:
    exec(open(.../mcp_helpers.py).read(), globals())
    exec(open(.../build_wheels.py).read(), globals())
    build_wheel("slick")

Authoring frame: axle along +X, origin at wheel centre, OUTBOARD FACE = +X.
Outer tyre radius is exactly 33.0 mm (66 mm diameter) for all three styles.

All dimensions in this file are millimetres; MM converts to Blender metres.

Construction notes
------------------
* Tyres are surfaces of revolution built at final segment density rather than a
  coarse cage + SubD: a lathe is already an exact revolve, so segments buy
  silhouette quality at half the triangle cost SubD would. Tread detail (grooves)
  lives in the profile; lugs are extruded from tread faces so the knobs are part
  of the same watertight shell rather than intersecting blocks.
* Rims are assembled from individually closed solids (barrel/lip lathe + N
  solidified spoke patches). Each solid is manifold and all-quad; they overlap by
  a few tenths of a millimetre at the joins. This is deliberate - it is how
  production game wheels are built, and it keeps every spoke edge crisp without
  the pole clusters a single bridged surface would need.
* Every part carries real thickness (spokes 2.2 mm, barrel wall 1.2 mm) per the
  "no paper-thin spokes" rule, and gets a small uniform bevel before export.
"""

import bpy, bmesh, math
from mathutils import Vector

MM = 0.001
OUTER_R = 33.0          # contract: 66 mm diameter, never exceeded


# ---------------------------------------------------------------------------
# low-level builders
# ---------------------------------------------------------------------------

def _to_object(name, coll_name, bm):
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


def revolve(profile, segments):
    """Revolve a closed (x_mm, r_mm) cross-section about the +X axis.

    Returns (bm, faces) where faces[(s, i)] is the quad spanning profile points
    i..i+1 and angular steps s..s+1 - the handle used to select tread rows for
    lug extrusion."""
    bm = bmesh.new()
    n = len(profile)
    rings = []
    for s in range(segments):
        a = 2.0 * math.pi * s / segments
        ca, sa = math.cos(a), math.sin(a)
        rings.append([bm.verts.new((x * MM, r * MM * ca, r * MM * sa))
                      for (x, r) in profile])
    bm.verts.ensure_lookup_table()
    faces = {}
    for s in range(segments):
        t = (s + 1) % segments
        for i in range(n):
            j = (i + 1) % n
            faces[(s, i)] = bm.faces.new(
                (rings[s][i], rings[s][j], rings[t][j], rings[t][i]))
    bm.faces.ensure_lookup_table()
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return bm, faces


def extrude_lugs(bm, faces, segments, rows, keep, depth, inset=0.35):
    """Push selected tread faces outward radially to form tread blocks.

    `rows` are profile-row indices inside the tread band; `keep(s, row)` decides
    which angular steps get a block, so bands can be staggered left/right the way
    a real directional off-road tyre is."""
    groups = {}
    for s in range(segments):
        for r in rows:
            if keep(s, r):
                groups.setdefault(s, []).append(faces[(s, r)])
    for s, sel in groups.items():
        sel = [f for f in sel if f.is_valid]
        if not sel:
            continue
        if inset > 0:
            bmesh.ops.inset_region(bm, faces=sel, thickness=inset * MM,
                                   depth=0.0, use_boundary=True,
                                   use_even_offset=True)
        ret = bmesh.ops.extrude_face_region(bm, geom=sel)
        newv = [g for g in ret['geom'] if isinstance(g, bmesh.types.BMVert)]
        bmesh.ops.delete(bm, geom=sel, context='FACES')
        for v in newv:
            d = Vector((0.0, v.co.y, v.co.z))
            if d.length > 1e-9:
                v.co += d.normalized() * (depth * MM)
    bm.faces.ensure_lookup_table()
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)


def spoke_patch(bm, angle_deg, r0, r1, x0, x1, hw0, hw1, nu=4, nv=3, bow=0.0):
    """One tapered spoke as an open quad patch (solidified later).

    hw0/hw1 are angular half-widths in degrees at the hub and rim ends, so the
    spoke tapers like a cast wheel rather than being a constant-width bar. `bow`
    lifts the middle of the spoke axially for a dished face."""
    verts = []
    for iu in range(nu + 1):
        u = iu / nu
        r = r0 + (r1 - r0) * u
        # smoothstep along the dish so the spoke meets hub and rim tangentially
        su = u * u * (3.0 - 2.0 * u)
        x = x0 + (x1 - x0) * su + bow * math.sin(math.pi * u)
        hw = math.radians(hw0 + (hw1 - hw0) * u)
        row = []
        for iv in range(nv + 1):
            v = -1.0 + 2.0 * iv / nv
            a = math.radians(angle_deg) + v * hw
            row.append(bm.verts.new((x * MM, r * MM * math.cos(a),
                                     r * MM * math.sin(a))))
        verts.append(row)
    for iu in range(nu):
        for iv in range(nv):
            bm.faces.new((verts[iu][iv], verts[iu][iv + 1],
                          verts[iu + 1][iv + 1], verts[iu + 1][iv]))
    return verts


def quad_cap(bm, loop, centre):
    """Close an even-sided vertex loop with quads only.

    An n-gon boundary (n even) is filled with n/2 quads around a single centre
    vertex: (v0,v1,v2,c), (v2,v3,v4,c), ... This keeps the mesh n-gon and
    triangle free at the cost of one 4-valence pole, which is exactly what a
    machined end cap looks like anyway. bmesh.ops.grid_fill is unreliable on
    these small loops (it fails silently), so the fill is done by hand."""
    n = len(loop)
    if n < 4 or n % 2:
        return
    c = bm.verts.new(centre)
    for k in range(0, n, 2):
        bm.faces.new((loop[k], loop[(k + 1) % n], loop[(k + 2) % n], c))
    return c


def cylinder_x(bm, y, z, radius, x0, x1, segments=8, cap=True):
    """Small capped cylinder with its axis along X (lug studs, hub nut)."""
    loops = []
    for x in (x0, x1):
        loop = []
        for s in range(segments):
            a = 2.0 * math.pi * s / segments
            loop.append(bm.verts.new((x * MM,
                                      (y + radius * math.cos(a)) * MM,
                                      (z + radius * math.sin(a)) * MM)))
        loops.append(loop)
    for s in range(segments):
        t = (s + 1) % segments
        bm.faces.new((loops[0][s], loops[0][t], loops[1][t], loops[1][s]))
    if cap:
        quad_cap(bm, list(reversed(loops[0])), (x0 * MM, y * MM, z * MM))
        quad_cap(bm, loops[1], (x1 * MM, y * MM, z * MM))


def solidify(obj, thickness, offset=0.0):
    bpy.context.view_layer.objects.active = obj
    m = obj.modifiers.new("Solidify", 'SOLIDIFY')
    m.thickness = thickness * MM
    m.offset = offset
    m.use_even_offset = True
    m.use_rim = True
    m.use_rim_only = False
    bpy.ops.object.modifier_apply(modifier=m.name)


def bevel(obj, width, segments=1, angle_deg=32.0):
    bpy.context.view_layer.objects.active = obj
    m = obj.modifiers.new("Bevel", 'BEVEL')
    m.width = width * MM
    m.segments = segments
    m.limit_method = 'ANGLE'
    m.angle_limit = math.radians(angle_deg)
    m.miter_outer = 'MITER_ARC'
    m.harden_normals = False
    bpy.ops.object.modifier_apply(modifier=m.name)


def join_into(target, others):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    target.select_set(True)
    for o in others:
        o.select_set(True)
    bpy.context.view_layer.objects.active = target
    bpy.ops.object.join()
    return target


# ---------------------------------------------------------------------------
# cross-sections
# ---------------------------------------------------------------------------

def tyre_profile(crown_r, half_w, bulge_x, bulge_r, bead_r, bead_x,
                 grooves=(), shoulder_drop=1.4):
    """Closed tyre cross-section: crown -> shoulder -> sidewall bulge -> bead ->
    bore -> mirrored side. `grooves` are (x_centre, half_width, depth) triples
    cut into the tread as circumferential channels."""
    half = [(0.0, crown_r)]
    # tread with circumferential grooves
    for (gx, gw, gd) in sorted(grooves):
        if gx <= 0:
            continue
        half.append((gx - gw, crown_r))
        half.append((gx, crown_r - gd))
        half.append((gx + gw, crown_r))
    half.append((half_w * 0.62, crown_r))                       # tread band split
    half.append((half_w * 0.92, crown_r))
    half.append((half_w, crown_r - shoulder_drop))              # shoulder round
    half.append((half_w * 1.02, crown_r - shoulder_drop * 2.6))
    half.append((bulge_x, bulge_r))                             # sidewall bulge
    half.append((bead_x + 0.5, bead_r + 1.2))                   # bead transition
    half.append((bead_x, bead_r))                               # bead heel

    loop = list(half)                                    # +x side, crown -> bead
    loop.append((0.0, bead_r))                           # bore (hidden by the rim,
                                                         # so a single row is plenty)
    loop += [(-x, r) for (x, r) in reversed(half)]       # -x side, bead -> crown
    return loop[:-1] if abs(loop[-1][0]) < 1e-9 and abs(loop[0][0]) < 1e-9 else loop


def rim_profile(seat_r, seat_x, flange_r, wall):
    """Closed barrel/flange channel: outer seat surface, both flanges, inner bore."""
    return [
        (-seat_x - 0.5, flange_r), (-seat_x + 0.6, flange_r - 0.6),
        (-seat_x + 0.7, seat_r), (seat_x - 0.7, seat_r),
        (seat_x - 0.6, flange_r - 0.6), (seat_x + 0.5, flange_r),
        (seat_x + 0.5, flange_r - wall), (seat_x - 1.4, seat_r - wall),
        (-seat_x + 1.4, seat_r - wall), (-seat_x - 0.5, flange_r - wall),
    ]


def disc_profile(outer_r, inner_r, x, thick):
    """Flat annular disc (brake rotor) as a closed cross-section."""
    return [(x - thick * 0.5, inner_r), (x - thick * 0.5, outer_r),
            (x + thick * 0.5, outer_r), (x + thick * 0.5, inner_r)]


def hub_profile(x_in, x_out, r_body, r_flare):
    """Centre hub boss: flared base into a chamfered cap."""
    return [
        (x_in, 0.9), (x_in, r_flare), (x_in + 0.8, r_flare + 0.5),
        (x_in + 2.0, r_body), (x_out - 0.9, r_body), (x_out, r_body - 0.9),
        (x_out, 2.2), (x_out - 0.7, 1.6), (x_in + 1.0, 1.2),
    ]


# ---------------------------------------------------------------------------
# style table
# ---------------------------------------------------------------------------

STYLES = {
    # Touring slick per car_example.jpg: one shallow circumferential groove,
    # narrow section, 5-spoke rim. Highest segment count of the three because a
    # slick has no tread detail to hide a polygonal silhouette.
    "slick": dict(
        seg=32, half_w=13.0, bulge_x=14.1, bulge_r=26.0,
        bead_r=20.5, bead_x=13.0, crown_r=OUTER_R, shoulder_drop=1.2,
        grooves=((6.4, 0.9, 1.1),),
        lug=None,
        spokes=5, hw_hub=13.0, hw_rim=17.0, spoke_x=(7.4, 10.2),
        spoke_r=(6.8, 20.2), spoke_thick=2.4, spoke_bow=0.0, spoke_nu=3,
        seat_r=20.4, seat_x=13.0, flange_r=22.1, wall=1.2, rim_seg=24,
        hub=(3.0, 11.2, 7.4, 9.0), studs=(5, 5.4, 1.25, 10.6, 12.0),
        brake=(15.5, 5.0, -5.0, 1.4, 16),
    ),
    # Off-road knobby per car_example2.jpg: wide carcass, staggered lug blocks
    # standing 2.4 mm proud (carcass crown sits at 30.6 so the lugs land exactly
    # on 33.0), dish rim with 6 broad spokes. Lugs break up the silhouette, so a
    # lower segment count reads fine.
    "knobby": dict(
        seg=24, half_w=15.0, bulge_x=17.0, bulge_r=25.0,
        bead_r=20.5, bead_x=14.5, crown_r=OUTER_R - 2.4, shoulder_drop=1.6,
        grooves=(),
        lug=dict(depth=2.5, inset=0.55, gap=1, rows=2),
        spokes=6, hw_hub=15.0, hw_rim=23.0, spoke_x=(8.0, 11.0),
        spoke_r=(7.2, 20.2), spoke_thick=2.6, spoke_bow=0.0, spoke_nu=2,
        seat_r=20.4, seat_x=14.5, flange_r=22.1, wall=1.3, rim_seg=24,
        hub=(4.5, 12.4, 7.8, 9.4), studs=(6, 5.6, 1.3, 11.8, 13.2),
        brake=(14.5, 5.0, -5.5, 1.4, 14),
    ),
    # Rally per car_example3.jpg: two fine circumferential grooves plus shallow
    # lateral notches, 10-spoke mesh rim.
    "rally": dict(
        seg=28, half_w=13.6, bulge_x=14.6, bulge_r=25.6,
        bead_r=20.5, bead_x=13.4, crown_r=OUTER_R - 0.7, shoulder_drop=1.3,
        grooves=((4.6, 0.7, 0.8), (9.4, 0.7, 0.8)),
        lug=dict(depth=0.75, inset=0.45, gap=2, rows=1),
        spokes=8, hw_hub=6.0, hw_rim=11.5, spoke_x=(7.2, 10.2),
        spoke_r=(7.0, 20.2), spoke_thick=1.9, spoke_bow=0.0, spoke_nu=2,
        seat_r=20.4, seat_x=13.4, flange_r=22.1, wall=1.2, rim_seg=24,
        hub=(3.2, 11.2, 7.0, 8.6), studs=(5, 5.2, 1.2, 10.8, 12.2),
        brake=(15.0, 5.0, -5.0, 1.3, 14),
    ),
}


# ---------------------------------------------------------------------------
# assembly
# ---------------------------------------------------------------------------

def build_tyre(style, p):
    prof = tyre_profile(p['crown_r'], p['half_w'], p['bulge_x'], p['bulge_r'],
                        p['bead_r'], p['bead_x'], p['grooves'],
                        p['shoulder_drop'])
    bm, faces = revolve(prof, p['seg'])
    if p['lug']:
        lugp = p['lug']
        # tread rows = profile rows whose two radii both sit at the crown
        rows = [i for i in range(len(prof))
                if prof[i][1] > p['crown_r'] - 0.25
                and prof[(i + 1) % len(prof)][1] > p['crown_r'] - 0.25]
        # A grooved tread splits the crown into several rows; blocks only need
        # the outermost few per side, otherwise every lug spans the whole tread
        # and the triangle count explodes.
        per_side = lugp.get('rows', 0)
        if per_side and len(rows) > per_side * 2:
            rows = rows[:per_side] + rows[-per_side:]
        if rows:
            mid = len(rows) // 2
            left, right = set(rows[:mid]), set(rows[mid:])
            gap = lugp['gap'] + 1

            def keep(s, r):
                if r in left:
                    return s % gap == 0
                if r in right:
                    return s % gap == (gap // 2 if gap > 1 else 0)
                return False
            extrude_lugs(bm, faces, p['seg'], rows, keep,
                         lugp['depth'], lugp['inset'])
    # No bevel modifier on tyres. A lathe has no hard edges to soften - every row
    # is already a smooth revolve - and on lugged treads the bevel rounds the
    # corner vertices where three block edges meet, which produces n-gons. The
    # lug chamfer comes from the inset ring instead, and crisp block edges are
    # what a real tread pattern has.
    return _to_object(f"tire_{style}", f"wheel_{style}", bm)


def build_rim(style, p):
    bm, _ = revolve(rim_profile(p['seat_r'], p['seat_x'], p['flange_r'],
                                p['wall']), p['rim_seg'])
    barrel = _to_object(f"rim_{style}", f"wheel_{style}", bm)

    parts = []
    for k in range(p['spokes']):
        sbm = bmesh.new()
        spoke_patch(sbm, 360.0 * k / p['spokes'], p['spoke_r'][0], p['spoke_r'][1],
                    p['spoke_x'][0], p['spoke_x'][1], p['hw_hub'], p['hw_rim'],
                    nu=p['spoke_nu'], nv=3, bow=p['spoke_bow'])
        bmesh.ops.recalc_face_normals(sbm, faces=sbm.faces)
        so = _to_object(f"_spoke{k}", f"wheel_{style}", sbm)
        solidify(so, p['spoke_thick'])
        # Bevel the spokes before joining. Their edges are the ones that catch
        # light through the wheel face; the barrel is almost entirely hidden by
        # the tyre, and beveling its dozen channel corners costs more triangles
        # than the whole spoke set.
        bevel(so, 0.24, segments=1, angle_deg=45.0)
        parts.append(so)

    rim = join_into(barrel, parts)
    rim.name = f"rim_{style}"
    return rim


def build_hub(style, p):
    x_in, x_out, r_body, r_flare = p['hub']
    bm, _ = revolve(hub_profile(x_in, x_out, r_body, r_flare), 10)
    # The revolved boss is a tube, so its axle bore runs straight through. Real
    # RC wheels close it with a hex/nut cap - add one rather than leaving a hole.
    cylinder_x(bm, 0.0, 0.0, 1.9, x_out - 1.0, x_out + 0.7, segments=6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    ob = _to_object(f"hub_{style}", f"wheel_{style}", bm)
    bevel(ob, 0.2, segments=1, angle_deg=45.0)
    return ob


def build_studs(style, p):
    n, circle_r, stud_r, x0, x1 = p['studs']
    bm = bmesh.new()
    for k in range(n):
        a = 2.0 * math.pi * k / n + math.pi / n
        cylinder_x(bm, circle_r * math.cos(a), circle_r * math.sin(a),
                   stud_r, x0, x1, segments=6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    # No bevel: at 1.25 mm a 0.12 mm chamfer is invisible, and beveling the
    # cap's centre pole would only trade quads for triangles.
    return _to_object(f"stud_{style}", f"wheel_{style}", bm)


def build_brake(style, p):
    outer_r, inner_r, x, thick, seg = p['brake']
    bm, _ = revolve(disc_profile(outer_r, inner_r, x, thick), seg)
    return _to_object(f"brake_{style}", f"wheel_{style}", bm)


def build_wheel(style):
    p = STYLES[style]
    key = f"wheel_{style}"
    purge(key)
    objs = [build_tyre(style, p), build_rim(style, p), build_hub(style, p),
            build_studs(style, p), build_brake(style, p)]
    for o in objs:
        clean_mesh(o)
        activate(o)
        bpy.ops.object.shade_auto_smooth(angle=math.radians(38.0))
        wn = o.modifiers.new("WeightedNormal", 'WEIGHTED_NORMAL')
        wn.keep_sharp = True
        bpy.ops.object.modifier_apply(modifier=wn.name)
    return check_contract(key)


def build_all_wheels():
    return [build_wheel(s) for s in ("slick", "knobby", "rally")]
