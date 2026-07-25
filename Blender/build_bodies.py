"""Hard-surface rebuild of body_shell / body_lowracer / body_buggy.

Run after loading mcp_helpers.py:
    exec(open(.../mcp_helpers.py).read(), globals())
    exec(open(.../build_bodies.py).read(), globals())
    build_body("shell")

Authoring frame: X = width, Y = length with the NOSE AT -Y, Z = up.
Origin is the car root, so the geometry must be placed to match the runtime:
`CarVehicle.BuildBodyVisual` parents the mesh at localPosition zero with the
BoxCollider also centred, and the stock design puts the wheels at Unity
(+-0.083, -0.045, +-0.152). Mapped into this frame that is:

    wheel centre height  Z = -0.045
    front axle           Y = -0.152     (Unity +Z is the nose, Blender -Y)
    rear axle            Y = +0.152
    tyre radius          0.033

so the wheel arches are cut about those points. Contract: X = 0.200 and
Y = 0.420 exactly (buggy may exceed X by <=8% on the flares); Z is free.

Construction
------------
The body is a closed, watertight solid lofted from a closed cross-section that
runs roof centre -> side -> skirt -> underside -> centre, swept along a set of
stations and capped at nose and tail. Wheel arches are *pockets*, not through
holes: the faces inside the arch circle are inset (forming a crisp lip of
constant width) and then pushed inboard past the tyre's inner face, so you look
into a real wheel well. That keeps the mesh a single closed solid - which the
garage paint mode needs, since it cooks a runtime MeshCollider and reads
RaycastHit.textureCoord - and costs roughly half the triangles a solidified
single-sided shell with true cut-outs would.

The arch lip is projected onto an exact circle after the faces are selected, so
the opening stays perfectly round no matter how coarse the station grid is.
Hard edges (body line, arch lip, skirt) carry edge creases so SubD smooths the
panels without softening the character lines.
"""

import bpy, bmesh, math
from mathutils import Vector

# --- runtime-derived constants (see module docstring) -----------------------
AXLE_Y = 0.152
AXLE_Z = -0.045
TYRE_R = 0.033
TYRE_INNER_X = 0.069        # inboard face of the tyre; pockets must clear it

LEN = 0.420
HALF_LEN = LEN * 0.5

# How far an arch-boundary vertex may be pulled toward the exact circle, as a
# fraction of its shortest edge. 0 disables the snap and lets the opening follow
# grid lines; anything much above ~0.3 folds the skin into shards where the grid
# is coarse relative to the arch.
ARCH_SNAP = 0.30


# ---------------------------------------------------------------------------
# interpolation
# ---------------------------------------------------------------------------

def _catmull(p0, p1, p2, p3, t):
    t2 = t * t
    t3 = t2 * t
    return 0.5 * ((2 * p1) + (-p0 + p2) * t +
                  (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 +
                  (-p0 + 3 * p1 - 3 * p2 + p3) * t3)


def sample_keys(keys, y):
    """Catmull-Rom sample of a keyframe table [(y, a, b, c, ...), ...] at `y`.

    Keys are the designer-facing description of the car - a dozen rows per body
    rather than a hand-typed value for every station - and the spline keeps the
    hood/roof/deck transitions smooth instead of faceting at each keyframe."""
    ys = [k[0] for k in keys]
    n = len(keys)
    if y <= ys[0]:
        return list(keys[0][1:])
    if y >= ys[-1]:
        return list(keys[-1][1:])
    i = max(j for j in range(n - 1) if ys[j] <= y)
    t = (y - ys[i]) / (ys[i + 1] - ys[i])
    i0, i1, i2, i3 = max(i - 1, 0), i, i + 1, min(i + 2, n - 1)
    return [_catmull(keys[i0][c], keys[i1][c], keys[i2][c], keys[i3][c], t)
            for c in range(1, len(keys[0]))]


# ---------------------------------------------------------------------------
# cross-section
# ---------------------------------------------------------------------------
# (x_fraction_of_halfwidth, w_top, w_shoulder, w_bottom, dz)
#   z = w_top*ztop + w_shoulder*zshoulder + w_bottom*zbot + dz
# Listed from the roof centreline down the right-hand side and in along the
# underside; the loop is mirrored and closed automatically.

SECTION = [
    # x_frac, w_top, w_shoulder, w_bottom, dz, topness
    (0.000, 1.00, 0.00, 0.00,  0.0000, 1.00),   # roof/hood centre
    (0.450, 1.00, 0.00, 0.00, -0.0012, 1.00),   # roof/hood
    (0.760, 1.00, 0.00, 0.00, -0.0055, 1.00),   # roof edge / hood shut line
    (0.880, 0.62, 0.38, 0.00,  0.0040, 0.50),   # upper shoulder
    (0.945, 0.34, 0.66, 0.00,  0.0028, 0.25),   # fender crown, upper
    (0.985, 0.12, 0.88, 0.00,  0.0014, 0.10),   # fender crown, lower
    (1.000, 0.00, 1.00, 0.00,  0.0000, 0.00),   # body line crease - widest
    (0.986, 0.00, 0.78, 0.22, -0.0035, 0.00),   # upper flank
    (0.968, 0.00, 0.52, 0.48, -0.0010, 0.00),
    (0.950, 0.00, 0.28, 0.72,  0.0055, 0.00),   # lower flank
    (0.930, 0.00, 0.10, 0.90,  0.0015, 0.00),   # sill
    (0.900, 0.00, 0.00, 1.00,  0.0000, 0.00),   # skirt edge
    (0.550, 0.00, 0.00, 1.00, -0.0012, 0.00),   # underside
    (0.000, 0.00, 0.00, 1.00, -0.0018, 0.00),   # underside centre
]

# The arch reaches from the skirt up past the body line (a 66 mm wheel under a
# 78 mm body leaves no choice), so rows 3-11 all carry arch boundary vertices -
# hence the extra rows through that band.
# roof edge, body line, skirt: the three lines that must survive subdivision
CREASE_ANCHORS = {2, 6, 11}
TOP_ANCHOR_X = 0.760          # x_frac of the anchor that `roofw` pins


def section_points(halfw, ztop, zsh, zbot, roofw):
    """One closed cross-section loop: right half from the roof down and in, then
    the mirrored left half. Shared centre points are not duplicated.

    `roofw` is the half-width of the upper surface as a fraction of the body's
    full half-width. It is what separates a hood from its fenders: with a single
    width per station every section is one smooth arc and the body reads as a
    bar of soap. Pulling the top anchors in while the body line stays out at
    `halfw` gives the fenders real volume to crown over the wheels."""
    k = roofw / TOP_ANCHOR_X
    right = []
    for a in SECTION:
        xf = a[0] * (1.0 + a[5] * (k - 1.0))
        right.append((xf * halfw,
                      a[1] * ztop + a[2] * zsh + a[3] * zbot + a[4]))
    pts = [(0.0, right[0][1])]
    pts += [(x, z) for (x, z) in right[1:]]           # down the right side
    left = [(-x, z) for (x, z) in right[1:-1]]        # back up the left side
    pts += list(reversed(left))
    return pts


def crease_rows():
    """Section-loop row indices that should stay sharp through SubD."""
    n = len(SECTION)
    rows = set()
    for a in CREASE_ANCHORS:
        rows.add(a)                       # right-hand side
        rows.add(2 * n - 2 - a)           # mirrored index on the left
    return rows


# ---------------------------------------------------------------------------
# stations
# ---------------------------------------------------------------------------

def station_list(extra_arch=9):
    """Longitudinal stations, densified across both wheel arches so the pocket
    boundary has enough vertices to be projected onto a clean circle.

    The mid-body is smooth and needs few stations; the triangle budget is spent
    where the arches are, because that is where a coarse grid shows."""
    base = [-HALF_LEN, -0.200, -0.188,
            -0.075, -0.020, 0.030, 0.075,
            0.188, 0.200, HALF_LEN]
    ys = set(round(v, 5) for v in base)
    span = TYRE_R + 0.007
    for cy in (-AXLE_Y, AXLE_Y):
        for k in range(extra_arch):
            t = -1.0 + 2.0 * k / (extra_arch - 1)
            ys.add(round(cy + t * span * 1.05, 5))
    return sorted(ys)


# ---------------------------------------------------------------------------
# body specs
# ---------------------------------------------------------------------------
# keys: (y, halfwidth, ztop, zshoulder, zbot, roofw)

BODIES = {
    # Touring GT lexan shell (car_example.jpg): low sloped hood, curved
    # windscreen into a smooth roofline, tapered deck with a ducktail lip,
    # flared arches, side skirts.
    "shell": dict(
        target_x=0.200, arch_r=0.040, pocket_x=0.062,
        panels=[(-0.055, 0.011), (0.095, 0.011)],   # hood shut line, deck seam
        keys=[
            (-0.2100, 0.042, -0.0215, -0.0300, -0.0400, 0.70),
            (-0.1980, 0.066, -0.0150, -0.0300, -0.0440, 0.72),   # splitter lip
            (-0.1800, 0.088, -0.0080, -0.0285, -0.0425, 0.70),
            (-0.1640, 0.098, -0.0045, -0.0245, -0.0420, 0.62),
            (-0.1520, 0.100, -0.0035, -0.0225, -0.0420, 0.58),   # front fender
            (-0.1380, 0.099, -0.0030, -0.0230, -0.0420, 0.57),
            (-0.1150, 0.096, -0.0030, -0.0245, -0.0420, 0.56),   # hood, low+flat
            (-0.0850, 0.095, -0.0025, -0.0230, -0.0420, 0.58),
            (-0.0620, 0.096,  0.0000, -0.0215, -0.0420, 0.62),   # cowl
            (-0.0480, 0.096,  0.0075, -0.0205, -0.0420, 0.66),   # screen base
            (-0.0220, 0.094,  0.0255, -0.0180, -0.0420, 0.74),   # windscreen
            (-0.0020, 0.092,  0.0325, -0.0170, -0.0420, 0.80),
            ( 0.0300, 0.091,  0.0330, -0.0170, -0.0420, 0.82),   # roof
            ( 0.0580, 0.092,  0.0300, -0.0175, -0.0420, 0.80),
            ( 0.0820, 0.094,  0.0180, -0.0195, -0.0420, 0.72),   # rear screen
            ( 0.1080, 0.097,  0.0090, -0.0215, -0.0420, 0.64),   # deck
            ( 0.1380, 0.099,  0.0070, -0.0225, -0.0420, 0.60),
            ( 0.1520, 0.100,  0.0065, -0.0230, -0.0420, 0.58),   # rear fender
            ( 0.1760, 0.097,  0.0075, -0.0260, -0.0420, 0.62),
            ( 0.1980, 0.088,  0.0125, -0.0295, -0.0405, 0.70),   # ducktail
            ( 0.2100, 0.070,  0.0080, -0.0325, -0.0378, 0.74),   # diffuser
        ]),

    # F1TENTH-style aero wedge (car_example3.jpg): splitter-led nose ramp, flat
    # deck for the compute stack, small central canopy, rear diffuser.
    "lowracer": dict(
        target_x=0.200, arch_r=0.039, pocket_x=0.062,
        panels=[(-0.088, 0.010), (0.086, 0.010)],
        keys=[
            (-0.2100, 0.046, -0.0330, -0.0380, -0.0440, 0.62),
            (-0.1980, 0.070, -0.0300, -0.0385, -0.0455, 0.66),   # splitter
            (-0.1800, 0.090, -0.0250, -0.0375, -0.0440, 0.64),
            (-0.1640, 0.099, -0.0180, -0.0350, -0.0435, 0.58),
            (-0.1520, 0.100, -0.0150, -0.0340, -0.0435, 0.56),   # front fender
            (-0.1380, 0.099, -0.0110, -0.0335, -0.0435, 0.58),
            (-0.0900, 0.098, -0.0045, -0.0330, -0.0435, 0.70),   # nose ramp top
            (-0.0500, 0.098, -0.0015, -0.0325, -0.0435, 0.82),   # deck starts
            (-0.0150, 0.097,  0.0045, -0.0320, -0.0435, 0.60),   # canopy bump
            ( 0.0200, 0.097,  0.0040, -0.0320, -0.0435, 0.60),
            ( 0.0600, 0.098, -0.0015, -0.0325, -0.0435, 0.84),   # flat deck
            ( 0.1000, 0.099, -0.0025, -0.0330, -0.0435, 0.80),
            ( 0.1380, 0.100, -0.0030, -0.0340, -0.0435, 0.60),
            ( 0.1520, 0.100, -0.0035, -0.0345, -0.0435, 0.56),   # rear fender
            ( 0.1800, 0.097, -0.0040, -0.0370, -0.0430, 0.64),
            ( 0.2000, 0.088, -0.0045, -0.0395, -0.0405, 0.70),
            ( 0.2100, 0.070, -0.0080, -0.0410, -0.0375, 0.74),   # diffuser
        ]),

    # Off-road buggy (car_example2.jpg): taller rounded cab, hard-edged flared
    # arches, hood scoop line, chunky rocker, rear wing shelf. The flares are
    # allowed to exceed the 0.200 core width by up to 8%.
    "buggy": dict(
        target_x=0.216, arch_r=0.043, pocket_x=0.064,
        panels=[(-0.098, 0.011), (0.100, 0.011)],
        keys=[
            (-0.2100, 0.048, -0.0100, -0.0260, -0.0390, 0.68),
            (-0.1980, 0.070, -0.0030, -0.0265, -0.0420, 0.70),
            (-0.1800, 0.092,  0.0050, -0.0250, -0.0415, 0.64),
            (-0.1620, 0.104,  0.0110, -0.0215, -0.0410, 0.54),
            (-0.1520, 0.108,  0.0130, -0.0195, -0.0410, 0.50),   # front flare
            (-0.1420, 0.104,  0.0140, -0.0215, -0.0410, 0.52),
            (-0.1150, 0.094,  0.0165, -0.0245, -0.0410, 0.60),   # hood + scoop
            (-0.0800, 0.092,  0.0200, -0.0240, -0.0410, 0.62),
            (-0.0500, 0.093,  0.0330, -0.0230, -0.0410, 0.68),   # windscreen
            (-0.0150, 0.094,  0.0440, -0.0225, -0.0410, 0.76),   # cab roof
            ( 0.0250, 0.094,  0.0450, -0.0225, -0.0410, 0.78),
            ( 0.0550, 0.093,  0.0390, -0.0230, -0.0410, 0.74),
            ( 0.0850, 0.094,  0.0270, -0.0240, -0.0410, 0.64),   # rear cab
            ( 0.1150, 0.100,  0.0195, -0.0245, -0.0410, 0.56),
            ( 0.1420, 0.106,  0.0175, -0.0225, -0.0410, 0.50),
            ( 0.1520, 0.108,  0.0170, -0.0205, -0.0410, 0.50),   # rear flare
            ( 0.1700, 0.104,  0.0175, -0.0230, -0.0410, 0.56),
            ( 0.1950, 0.092,  0.0190, -0.0280, -0.0400, 0.68),   # wing shelf
            ( 0.2100, 0.074,  0.0140, -0.0320, -0.0375, 0.74),
        ]),
}


# ---------------------------------------------------------------------------
# mesh assembly
# ---------------------------------------------------------------------------

def _crease_layer(bm):
    lay = bm.edges.layers.float.get('crease_edge')
    if lay is None:
        lay = bm.edges.layers.float.new('crease_edge')
    return lay


def loft(spec):
    """Sweep the cross-section along the stations into a closed solid."""
    ys = station_list()
    bm = bmesh.new()
    rings = []
    for y in ys:
        halfw, ztop, zsh, zbot, roofw = sample_keys(spec['keys'], y)
        rings.append([bm.verts.new((x, y, z))
                      for (x, z) in section_points(halfw, ztop, zsh, zbot, roofw)])
    bm.verts.ensure_lookup_table()

    nt = len(rings[0])
    for i in range(len(rings) - 1):
        for j in range(nt):
            k = (j + 1) % nt
            bm.faces.new((rings[i][j], rings[i][k],
                          rings[i + 1][k], rings[i + 1][j]))
    # nose and tail caps: even-sided closed loops, filled with quads
    _cap(bm, list(reversed(rings[0])))
    _cap(bm, rings[-1])

    bm.faces.ensure_lookup_table()
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)

    # crease the longitudinal character lines
    lay = _crease_layer(bm)
    rows = crease_rows()
    for i in range(len(rings) - 1):
        for j in rows:
            e = bm.edges.get((rings[i][j], rings[i + 1][j]))
            if e is not None:
                e[lay] = 1.0
    return bm, rings, ys


def _cap(bm, loop):
    n = len(loop)
    if n % 2:
        bm.faces.new(loop)
        return
    c = bm.verts.new(tuple(sum(v.co[a] for v in loop) / n for a in range(3)))
    for k in range(0, n, 2):
        bm.faces.new((loop[k], loop[(k + 1) % n], loop[(k + 2) % n], c))


def carve_arch(bm, cy, sign, radius, pocket_x, lip=0.006):
    """Turn the faces inside one wheel-arch circle into a recessed well.

    Order matters: the region boundary is snapped onto the exact circle *before*
    insetting, so the visible arch lip is perfectly round; the inset then walks
    that circle inward by a constant `lip`, giving the uniform arch thickness the
    brief calls for; finally the interior is pushed inboard past the tyre."""
    lay = _crease_layer(bm)

    def inside(co):
        return math.hypot(co.y - cy, co.z - AXLE_Z) < radius

    sel = []
    for f in bm.faces:
        c = f.calc_center_median()
        if c.x * sign <= 0:
            continue
        if abs(c.x) < 0.030:                 # skip the deep underside centre
            continue
        # Flank faces only. Including faces that straddle the skirt edge or the
        # fender crown - where the surface turns through 90 degrees - meant
        # pushing them inboard folded the skin into tabs at every arch corner.
        # Bounding the opening by the crown above and the skirt below is also
        # what a real arch looks like.
        if abs(f.normal.x) < 0.30:
            continue
        if inside(c):
            sel.append(f)
    if not sel:
        return 0

    selset = set(sel)
    # boundary verts: shared by a selected and an unselected face
    bverts = []
    for v in set(vv for f in sel for vv in f.verts):
        linked = set(v.link_faces)
        if linked - selset and linked & selset:
            bverts.append(v)
    # Pull the boundary onto the circle, but never further than half the local
    # edge length. An unclamped snap yanks vertices clear across a face wherever
    # the grid is coarse, folding the skin into shards around every arch.
    for v in bverts:
        dy, dz = v.co.y - cy, v.co.z - AXLE_Z
        d = math.hypot(dy, dz)
        if d <= 1e-6:
            continue
        elen = min((e.calc_length() for e in v.link_edges), default=0.004)
        step = radius - d
        step = max(-elen * ARCH_SNAP, min(elen * ARCH_SNAP, step))
        v.co.y += dy / d * step
        v.co.z += dz / d * step

    # Push the well inboard past the inner face of the tyre. Only *interior*
    # vertices move: anything still shared with the outer skin stays where it is,
    # so the ring of faces straddling the boundary stretches into the pocket wall
    # by itself. An earlier version inset a lip ring first and moved everything,
    # which left sliver tabs poking through the skin at every arch corner - the
    # boundary ring is a better wall than any generated one.
    for v in set(vv for f in sel for vv in f.verts):
        if set(v.link_faces) - selset:
            continue
        if abs(v.co.x) > pocket_x:
            v.co.x = sign * pocket_x

    # crease the arch lip so subdivision keeps the opening crisp and round
    for f in sel:
        for e in f.edges:
            if any(lf not in selset for lf in e.link_faces):
                e[lay] = 1.0
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return len(sel)


def panel_line(bm, y, width, depth=0.0009):
    """A shallow inset seam across the upper surfaces - a panel *gap*, not a
    carved groove (guideline: shallow, uniform, parallel). The seam edges are
    creased so subdivision keeps the gap legible instead of buffing it out."""
    lay = _crease_layer(bm)
    sel = [f for f in bm.faces
           if abs(f.calc_center_median().y - y) < width * 0.5
           and f.normal.z > 0.25]
    if not sel:
        return 0
    res = bmesh.ops.inset_region(bm, faces=sel, thickness=0.0016, depth=0.0,
                                 use_boundary=True, use_even_offset=False)
    for f in res.get('faces', []):
        for e in f.edges:
            e[lay] = 1.0
    for v in set(vv for f in sel for vv in f.verts):
        v.co.z -= depth
    return len(sel)


def build_body(name, subd=1, do_uv=True):
    spec = BODIES[name]
    key = f"body_{name}"
    purge(key)

    bm, rings, ys = loft(spec)
    for cy in (-AXLE_Y, AXLE_Y):
        for sign in (-1, 1):
            carve_arch(bm, cy, sign, spec['arch_r'], spec['pocket_x'])
    for (py, pw) in spec.get('panels', []):
        panel_line(bm, py, pw)

    me = bpy.data.meshes.new(key)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(key, me)
    coll(key).objects.link(ob)

    # Tight merge threshold. The loft has no doubles to begin with, and on the
    # buggy the deep arch pocket passes within microns of the underside - a
    # default-ish threshold welds those two sheets into a 4-face edge, i.e. a
    # non-manifold mesh. Leaving them a hair apart is invisible and correct.
    clean_mesh(ob, merge_dist=1e-6)
    activate(ob)
    if subd:
        m = ob.modifiers.new("SubD", 'SUBSURF')
        m.levels = m.render_levels = subd
        m.use_limit_surface = False
        bpy.ops.object.modifier_apply(modifier=m.name)
    clean_mesh(ob, merge_dist=1e-6)

    # SubD pulls the surface inside its cage, so bring the silhouette back onto
    # the exact contract dimensions. X/Y only - height is deliberately free, and
    # the vertical placement must stay put or the arches leave the wheels.
    normalize_xy(ob, spec['target_x'], LEN)

    bpy.ops.object.shade_auto_smooth(angle=math.radians(38.0))
    wn = ob.modifiers.new("WeightedNormal", 'WEIGHTED_NORMAL')
    wn.keep_sharp = True
    bpy.ops.object.modifier_apply(modifier=wn.name)

    if do_uv:
        uv_unwrap(ob, angle_deg=66.0, margin=0.02)
    return check_contract(key)


def normalize_xy(ob, target_x, target_y):
    """Scale X and Y about the origin to exactly hit the contract, leaving Z."""
    bpy.context.view_layer.update()
    d = ob.dimensions
    sx = target_x / d.x if d.x > 1e-9 else 1.0
    sy = target_y / d.y if d.y > 1e-9 else 1.0
    for v in ob.data.vertices:
        v.co.x *= sx
        v.co.y *= sy
    ob.data.update()


def build_all_bodies():
    return {n: build_body(n) for n in ("shell", "lowracer", "buggy")}
