"""Hard-surface build of the Tiny Torque track props (Resources/TrackProps).

Run after mcp_helpers.py, with the rig pointed at the props file:

    exec(open(r"E:\\EE Projects\\Tiny_Torque\\Blender\\mcp_helpers.py").read(), globals())
    exec(open(r"E:\\EE Projects\\Tiny_Torque\\Blender\\build_props.py").read(), globals())
    use_props(); ensure_file()
    build_arcade(); save()

Frames (Blender X/Y/Z -> Unity X/Z/Y):
    Props stand on the ground plane with the ORIGIN AT THE BASE CONTACT POINT,
    not the centroid: TrackFactory.ItemPose snaps an item's root onto the
    surface it was dropped on, so a centred origin would bury half the prop.
    The two exceptions are arc_item_box and arc_shield_orb, which float and are
    therefore centred.
    Anything with a "forward" (the missile) points at -Y here = Unity +Z.

Object names carry the material tokens PartMeshLibrary.AssignByName matches —
see ArcadeVfx and TrackCatalog for each prop's token list.

Budgets (enforced by PartModelValidator): small prop 1500 tris, medium 3000,
hero landmark 6000. These are placed dozens of times per map, unlike the eight
vehicle parts, so the budget is the point rather than a formality.
"""

import bpy, bmesh, math
from mathutils import Vector


# ---------------------------------------------------------------------------
# primitive helpers (all return the new object, linked into `key`'s collection)
# ---------------------------------------------------------------------------

def _link(ob, key):
    for c in list(ob.users_collection):
        c.objects.unlink(ob)
    coll(key).objects.link(ob)
    return ob


def p_cube(key, name, size=(1, 1, 1), loc=(0, 0, 0), rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=loc)
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = Vector(size)
    ob.rotation_euler = [math.radians(a) for a in rot]
    apply_transforms(ob)
    return _link(ob, key)


def p_cyl(key, name, r=0.5, depth=1.0, loc=(0, 0, 0), rot=(0, 0, 0), verts=24,
          scale=(1, 1, 1)):
    bpy.ops.mesh.primitive_cylinder_add(radius=r, depth=depth, vertices=verts, location=loc)
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = Vector(scale)
    ob.rotation_euler = [math.radians(a) for a in rot]
    apply_transforms(ob)
    return _link(ob, key)


def p_cone(key, name, r1=0.5, r2=0.0, depth=1.0, loc=(0, 0, 0), rot=(0, 0, 0), verts=20,
           scale=(1, 1, 1)):
    """`scale` is applied in the object's OWN frame, before the rotation — that
    is what turns a cone into a flat blade (a palm frond, a surfboard fin)
    pointing wherever `rot` aims it. Squashing after the fact is a trap: the
    helpers apply transforms, which parks every origin at the world origin, so
    a late scale drags geometry toward the scene centre instead of flattening
    it in place."""
    bpy.ops.mesh.primitive_cone_add(radius1=r1, radius2=r2, depth=depth,
                                    vertices=verts, location=loc)
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = Vector(scale)
    ob.rotation_euler = [math.radians(a) for a in rot]
    apply_transforms(ob)
    return _link(ob, key)


def p_ico(key, name, r=0.5, subdiv=2, loc=(0, 0, 0), scale=(1, 1, 1), rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_ico_sphere_add(radius=r, subdivisions=subdiv, location=loc)
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = Vector(scale)
    ob.rotation_euler = [math.radians(a) for a in rot]
    apply_transforms(ob)
    return _link(ob, key)


def p_uv(key, name, r=0.5, segs=20, rings=10, loc=(0, 0, 0), scale=(1, 1, 1), rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=r, segments=segs, ring_count=rings, location=loc)
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = Vector(scale)
    ob.rotation_euler = [math.radians(a) for a in rot]
    apply_transforms(ob)
    return _link(ob, key)


def p_torus(key, name, major=0.2, minor=0.02, mseg=28, nseg=8,
            loc=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1)):
    bpy.ops.mesh.primitive_torus_add(major_radius=major, minor_radius=minor,
                                     major_segments=mseg, minor_segments=nseg,
                                     location=loc,
                                     rotation=[math.radians(a) for a in rot])
    ob = bpy.context.active_object
    ob.name = name
    ob.scale = Vector(scale)
    apply_transforms(ob)
    return _link(ob, key)


def p_wedge(key, name, size=(1, 1, 1), loc=(0, 0, 0), rot=(0, 0, 0)):
    """Right-triangular prism — the ramp primitive. Width X, run Y, rise Z; the
    low edge sits at -Y and the deck climbs to full height at +Y. The origin is
    the centre of the base, so the object already rests on the ground plane."""
    w, l, h = size[0] * 0.5, size[1] * 0.5, size[2]
    me = bpy.data.meshes.new(name)
    ob = bpy.data.objects.new(name, me)
    coll(key).objects.link(ob)
    bm = bmesh.new()
    v = [bm.verts.new(p) for p in [(-w, -l, 0), (w, -l, 0), (w, l, 0), (-w, l, 0),
                                   (w, l, h), (-w, l, h)]]
    for idx in [(0, 1, 2, 3), (3, 2, 4, 5), (0, 1, 4, 5), (1, 2, 4), (0, 5, 3)]:
        bm.faces.new([v[i] for i in idx])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    ob.location = Vector(loc)
    ob.rotation_euler = [math.radians(a) for a in rot]
    apply_transforms(ob)
    return ob


def p_revolve(key, name, profile, segments=24, angle_deg=360.0,
              loc=(0, 0, 0), rot=(0, 0, 0)):
    """Lathe a (radius, z) profile about +Z.

    A profile that starts and ends ON the axis closes into a solid; a profile
    that returns to its first point makes a tube with real wall thickness. This
    is how the mug, tape roll, barrel and torch bowl get an honest hollow
    interior instead of two intersecting cylinders faking one.
    """
    me = bpy.data.meshes.new(name)
    ob = bpy.data.objects.new(name, me)
    coll(key).objects.link(ob)
    bm = bmesh.new()
    verts = [bm.verts.new((r, 0.0, z)) for r, z in profile]
    edges = [bm.edges.new((verts[i], verts[i + 1])) for i in range(len(verts) - 1)]
    bmesh.ops.spin(bm, geom=verts + edges, axis=(0, 0, 1), cent=(0, 0, 0),
                   dvec=(0, 0, 0), angle=math.radians(angle_deg),
                   steps=segments, use_merge=False)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    ob.location = Vector(loc)
    ob.rotation_euler = [math.radians(a) for a in rot]
    apply_transforms(ob)
    return ob


def bevel(ob, width=0.004, segments=2, angle_deg=40.0):
    """Support-loop bevel: the hard-surface way to keep an edge crisp through
    SubD instead of relying on shading alone."""
    activate(ob)
    m = ob.modifiers.new("Bevel", 'BEVEL')
    m.width = width
    m.segments = segments
    m.limit_method = 'ANGLE'
    m.angle_limit = math.radians(angle_deg)
    m.harden_normals = False
    bpy.ops.object.modifier_apply(modifier=m.name)
    return ob


def finish_prop(objects, subd=0, smooth=35.0):
    out = []
    for ob in objects:
        out.append(finalize(ob, subd=subd, smooth_angle_deg=smooth))
    return out


def tri_total(key, evaluated=False):
    """Triangle total for a collection. `finalize` already applies every modifier,
    so the base mesh IS what Unity imports — asking the depsgraph for an evaluated
    copy here just risks reading mesh data that has since been freed."""
    bpy.context.view_layer.update()
    return sum(mesh_report(o, evaluated=evaluated)["tris"] for o in coll_objects(key))


# ---------------------------------------------------------------------------
# arcade family
# ---------------------------------------------------------------------------

def build_item_box():
    """arc_item_box - the floating power-up cube (~0.24 m, drivable through).

    Chamfered shell + a recessed glyph plate on each face + an emissive core.
    Centred on its origin: it hovers, so there is no base contact point.
    """
    key = "arc_item_box"
    purge(key)

    shell = p_cube(key, "box_shell", size=(0.20, 0.20, 0.20))
    bevel(shell, width=0.018, segments=3)

    # A diamond glyph on each of the six faces. The plate is thin along the face
    # NORMAL and rotated 45 deg about that same axis, so it lies flush in the
    # face plane — rotating about any other axis tilts it out and the box grows
    # six spikes instead of six panels.
    plate = 0.050
    thick = 0.012
    off = 0.099
    faces = [
        ("x", ( off, 0, 0)), ("x", (-off, 0, 0)),
        ("y", (0,  off, 0)), ("y", (0, -off, 0)),
        ("z", (0, 0,  off)), ("z", (0, 0, -off)),
    ]
    for i, (axis, loc) in enumerate(faces):
        if axis == "x":
            size, rot = (thick, plate, plate), (45, 0, 0)
        elif axis == "y":
            size, rot = (plate, thick, plate), (0, 45, 0)
        else:
            size, rot = (plate, plate, thick), (0, 0, 45)
        g = p_cube(key, f"glyph_{i}", size=size, loc=loc, rot=rot)
        bevel(g, width=0.003, segments=2)

    p_ico(key, "core_glow", r=0.062, subdiv=2)

    finish_prop(coll_objects(key), subd=0)
    normalize(coll_objects(key), target=(0.24, 0.24, 0.24), center=(0, 0, 0), uniform=True)
    return {"key": key, "tris": tri_total(key), "size": bbox_size(coll_objects(key))[:]}


def build_missile():
    """arc_missile - ~0.16 m long, NOSE AT -Y (Unity +Z, the direction of flight).

    Origin at the body centre: the missile is spawned and posed by code, never
    dropped onto a surface.
    """
    key = "arc_missile"
    purge(key)

    body = p_cyl(key, "body_tube", r=0.020, depth=0.095, rot=(90, 0, 0), verts=20)
    bevel(body, width=0.003, segments=2)

    nose = p_cone(key, "nose_cone", r1=0.020, r2=0.0, depth=0.050,
                  loc=(0, -0.072, 0), rot=(-90, 0, 0), verts=20)

    nozzle = p_cyl(key, "nozzle_bell", r=0.016, depth=0.018,
                   loc=(0, 0.055, 0), rot=(90, 0, 0), verts=16)
    bevel(nozzle, width=0.002, segments=2)

    # Three swept fins at the tail.
    for i in range(3):
        f = p_cube(key, f"fin_{i}", size=(0.0035, 0.040, 0.028),
                   loc=(0, 0.040, 0), rot=(0, i * 120.0, 0))
        # Push each fin out along its own local +Z before the rotation is baked.
        f.location = Vector((math.sin(math.radians(i * 120.0)) * 0.022,
                             0.040,
                             math.cos(math.radians(i * 120.0)) * 0.022))
        apply_transforms(f)
        bevel(f, width=0.0015, segments=2)

    finish_prop(coll_objects(key), subd=0)
    normalize(coll_objects(key), target=(None, 0.16, None), center=(0, 0, 0))
    return {"key": key, "tris": tri_total(key), "size": bbox_size(coll_objects(key))[:]}


def build_banana():
    """arc_banana - a dropped peel, ~0.12 m across, lying flat.

    Origin at the ground contact so the drop raycast seats it on the surface.
    """
    key = "arc_banana"
    purge(key)

    hub = p_uv(key, "peel_hub", r=0.030, segs=14, rings=7, scale=(1.0, 1.0, 0.42))

    # Three splayed lobes, each a tapered box bent up at the tip.
    for i in range(3):
        a = math.radians(i * 120.0)
        lobe = p_cube(key, f"peel_lobe_{i}", size=(0.020, 0.062, 0.011),
                      loc=(0, 0, 0), rot=(0, 0, 0))
        lobe.location = Vector((math.sin(a) * 0.034, math.cos(a) * 0.034, 0.004))
        lobe.rotation_euler = (math.radians(-14.0), 0.0, -a)
        apply_transforms(lobe)
        bevel(lobe, width=0.0045, segments=2)

    stem = p_cyl(key, "stem_tip", r=0.006, depth=0.022, loc=(0, 0, 0.014),
                 rot=(0, 12, 0), verts=10)
    bevel(stem, width=0.002, segments=2)

    # No SubD: bevelled lobes plus a 50 deg smoothing angle already read as soft,
    # and up to sixteen of these can be on track at once (two per player).
    finish_prop(coll_objects(key), subd=0, smooth=50.0)
    normalize(coll_objects(key), target=(0.12, None, None))
    _sit_on_ground(key)
    return {"key": key, "tris": tri_total(key), "size": bbox_size(coll_objects(key))[:]}


def build_shield_orb():
    """arc_shield_orb - one of three orbs that orbit a shielded car (~0.05 m)."""
    key = "arc_shield_orb"
    purge(key)

    orb = p_ico(key, "orb_gem", r=0.025, subdiv=1)
    bevel(orb, width=0.002, segments=1)

    finish_prop(coll_objects(key), subd=0, smooth=25.0)
    normalize(coll_objects(key), target=(0.05, 0.05, 0.05), center=(0, 0, 0), uniform=True)
    return {"key": key, "tris": tri_total(key), "size": bbox_size(coll_objects(key))[:]}


def _sit_on_ground(key):
    """Drop a collection so its lowest point is at z = 0, keeping x/y centred."""
    objs = coll_objects(key)
    lo, hi = world_bbox(objs)
    d = Vector((-(lo.x + hi.x) * 0.5, -(lo.y + hi.y) * 0.5, -lo.z))
    for o in objs:
        o.location = o.location + d
    bpy.context.view_layer.update()
    for o in objs:
        apply_transforms(o)
    return [round(v * 1000, 2) for v in bbox_size(objs)]


ARCADE = [build_item_box, build_missile, build_banana, build_shield_orb]


def build_arcade(export=True):
    """Build (and optionally export) the whole arcade family."""
    return _build_family(ARCADE, export)


# ---------------------------------------------------------------------------
# theme families
#
# Unlike the arcade props these are NOT normalized to a target box: they are
# constructed straight at their real metric size, because a track prop has no
# runtime scale contract — only a max extent and a triangle budget. Building at
# true size keeps every number in the tables below readable as millimetres of
# actual object, which matters when a wall has to stop a 0.42 m car and a gate
# has to let one through.
#
# Scale anchors, for judging every dimension here:
#     car 0.42 long x 0.20 wide x 0.10 tall, wheels 66 mm, tiles 1 m.
# Walls run their long axis along BLENDER X (= Unity X, across the racing line);
# ramps climb toward +Y (= Unity +Z, the direction of travel); gates open along
# Y so the car drives through them.
# ---------------------------------------------------------------------------

def _report(key):
    objs = coll_objects(key)
    return {"key": key, "objects": len(objs), "tris": tri_total(key),
            "size_mm": [round(v * 1000, 1) for v in bbox_size(objs)]}


def _build_family(builders, export=True):
    out = []
    for fn in builders:
        r = fn()
        if export:
            r["export"] = export_part(r["key"])["bytes"]
        out.append(r)
    return out


# --- Toy Workshop ----------------------------------------------------------
# The RC car in a giant human world — the theme that justifies 1/10 scale
# instead of fighting it. Everything here is a real desk object at its real
# size, which is exactly why it reads as enormous next to the car.

def build_tw_book_stack():
    """tw_book_stack — four stacked hardbacks (wall, ~0.26 x 0.19 x 0.14)."""
    key = "tw_book_stack"
    purge(key)

    books = [(0.000, 0.036, 0.260, 0.185,   0.0),
             (0.036, 0.030, 0.242, 0.172,   7.0),
             (0.066, 0.034, 0.254, 0.180,  -5.0),
             (0.100, 0.028, 0.230, 0.166,  12.0)]
    for i, (z, t, ln, wd, yaw) in enumerate(books):
        cz = z + t * 0.5
        cover = p_cube(key, f"cover_b{i}", size=(ln, wd, t), loc=(0, 0, cz), rot=(0, 0, yaw))
        bevel(cover, width=0.0035, segments=2)
        # Pages sit flush with the fore-edge and 10 mm inside the spine, so the
        # block reads as paper bound in a cover rather than a striped slab. The
        # inset is along the book's own X, hence the rotated offset.
        a = math.radians(yaw)
        off = 0.005
        pages = p_cube(key, f"pages_b{i}",
                       size=(ln - 0.010, wd - 0.006, t - 0.006),
                       loc=(math.cos(a) * off, math.sin(a) * off, cz), rot=(0, 0, yaw))
        bevel(pages, width=0.0015, segments=1)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_tw_ruler_ramp():
    """tw_ruler_ramp — a steel ruler over a wooden block (ramp, 0.30 m run)."""
    key = "tw_ruler_ramp"
    purge(key)

    run, rise = 0.28, 0.070
    slope = math.degrees(math.atan2(rise, run))          # 14.0 deg

    block = p_wedge(key, "wood_block", size=(0.24, run, rise))
    bevel(block, width=0.004, segments=2)

    # Deck plate lying on the slope, overhanging both ends so the ramp has a
    # lip to climb rather than a step.
    deck = p_cube(key, "ruler_deck", size=(0.235, 0.320, 0.008),
                  loc=(0, 0, rise * 0.5 + 0.004), rot=(slope, 0, 0))
    bevel(deck, width=0.0025, segments=2)

    # Graduation ticks, spaced along the deck's own axis.
    c, s = math.cos(math.radians(slope)), math.sin(math.radians(slope))
    for i in range(7):
        ly = -0.132 + i * 0.044
        long_tick = (i % 2 == 0)
        p_cube(key, f"tick_{i}",
               size=(0.090 if long_tick else 0.055, 0.0045, 0.0022),
               loc=(0, ly * c, rise * 0.5 + 0.008 + ly * s), rot=(slope, 0, 0))

    for sgn in (-1, 1):
        r = p_cube(key, f"rail_{'r' if sgn > 0 else 'l'}",
                   size=(0.010, 0.320, 0.012),
                   loc=(sgn * 0.1175, 0, rise * 0.5 + 0.006), rot=(slope, 0, 0))
        bevel(r, width=0.002, segments=2)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_tw_brick_wall():
    """tw_brick_wall — a toy building brick (wall, 0.32 x 0.16 x 0.10)."""
    key = "tw_brick_wall"
    purge(key)

    body = p_cube(key, "brick_body", size=(0.320, 0.160, 0.096), loc=(0, 0, 0.048))
    bevel(body, width=0.005, segments=3)

    for i in range(6):
        for j, y in enumerate((-0.040, 0.040)):
            st = p_cyl(key, f"stud_{i}{j}", r=0.0155, depth=0.013,
                       loc=(-0.125 + i * 0.050, y, 0.1015), verts=14)
            bevel(st, width=0.0022, segments=2)

    base = p_cube(key, "plate_base", size=(0.344, 0.184, 0.006), loc=(0, 0, 0.003))
    bevel(base, width=0.002, segments=2)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_tw_pencil():
    """tw_pencil — a hex pencil lying across the track (dynamic roller, 0.21 m)."""
    key = "tw_pencil"
    purge(key)

    # Long axis along X so a knock sends it rolling down the track, not along it.
    barrel = p_cyl(key, "barrel_hex", r=0.0092, depth=0.150, rot=(0, 90, 0), verts=6)
    bevel(barrel, width=0.0012, segments=1)

    p_cone(key, "wood_tip", r1=0.0092, r2=0.0022, depth=0.024,
           loc=(-0.087, 0, 0), rot=(0, -90, 0), verts=12)
    p_cone(key, "lead_point", r1=0.0022, r2=0.0, depth=0.007,
           loc=(-0.1025, 0, 0), rot=(0, -90, 0), verts=10)

    fer = p_cyl(key, "ferrule_band", r=0.0094, depth=0.017,
                loc=(0.0835, 0, 0), rot=(0, 90, 0), verts=14)
    bevel(fer, width=0.0012, segments=1)
    er = p_cyl(key, "eraser_nub", r=0.0082, depth=0.013,
               loc=(0.0985, 0, 0), rot=(0, 90, 0), verts=14)
    bevel(er, width=0.0022, segments=2)

    finish_prop(coll_objects(key), subd=0, smooth=45.0)
    _sit_on_ground(key)
    return _report(key)


def build_tw_mug():
    """tw_mug — a coffee mug (hero, 0.10 m tall, genuinely hollow)."""
    key = "tw_mug"
    purge(key)

    # Lathed profile: outside up, over the rim, inside down, across the base.
    p_revolve(key, "mug_body",
              [(0.0, 0.0), (0.045, 0.0), (0.045, 0.100), (0.041, 0.100),
               (0.041, 0.007), (0.0, 0.007)], segments=28)

    p_torus(key, "mug_handle", major=0.026, minor=0.0065, mseg=20, nseg=8,
            loc=(0.045, 0, 0.056), rot=(90, 0, 0))

    p_cyl(key, "coffee_top", r=0.0405, depth=0.004, loc=(0, 0, 0.078), verts=28)

    finish_prop(coll_objects(key), subd=0, smooth=50.0)
    _sit_on_ground(key)
    return _report(key)


def build_tw_tape_arch():
    """tw_tape_arch — a giant tape roll half-sunk into the ground (gate).

    A roll standing on its rim would hold the hole 110 mm off the deck and the
    car would nose straight into it, so this one is deliberately buried to that
    depth: the core's floor IS ground level and the origin sits there. Geometry
    below z = 0 is intentional.
    """
    key = "tw_tape_arch"
    purge(key)

    r_out, r_in, half_w = 0.280, 0.170, 0.045
    cz = r_in                                     # core floor lands on z = 0

    p_revolve(key, "tape_roll",
              [(r_in, -half_w), (r_out, -half_w), (r_out, half_w),
               (r_in, half_w), (r_in, -half_w)],
              segments=34, loc=(0, 0, cz), rot=(90, 0, 0))

    p_revolve(key, "core_ring",
              [(0.163, -half_w - 0.003), (0.172, -half_w - 0.003),
               (0.172, half_w + 0.003), (0.163, half_w + 0.003),
               (0.163, -half_w - 0.003)],
              segments=30, loc=(0, 0, cz), rot=(90, 0, 0))

    # The loose end, leaving the rim tangentially at 60 deg round the roll and
    # hanging outward — clear of the bore, so it never narrows the gate.
    flap = p_cube(key, "tape_flap", size=(0.005, 0.086, 0.175),
                  loc=(0.286, 0, cz + 0.062), rot=(0, 150, 0))
    bevel(flap, width=0.0015, segments=1)

    finish_prop(coll_objects(key), subd=0, smooth=45.0)
    # No _sit_on_ground: the burial depth IS the pose.
    return _report(key)


TOY_WORKSHOP = [build_tw_book_stack, build_tw_ruler_ramp, build_tw_brick_wall,
                build_tw_pencil, build_tw_mug, build_tw_tape_arch]


# --- Neon Grid -------------------------------------------------------------
# Cheapest family to author: simple forms carry it, and the "glow_" objects
# take an emissive material in TrackCatalog rather than any special geometry.

def build_ng_pylon():
    """ng_pylon — tapered hex column with glow bands (wall, 0.30 m tall)."""
    key = "ng_pylon"
    purge(key)

    col = p_cone(key, "pylon_column", r1=0.056, r2=0.040, depth=0.290,
                 loc=(0, 0, 0.153), verts=6)
    bevel(col, width=0.004, segments=2)

    for i, z in enumerate((0.075, 0.150, 0.225)):
        p_cone(key, f"glow_band_{i}", r1=0.058 - i * 0.005, r2=0.058 - i * 0.005,
               depth=0.014, loc=(0, 0, z), verts=6)

    base = p_cube(key, "base_plate", size=(0.130, 0.130, 0.016), loc=(0, 0, 0.008))
    bevel(base, width=0.004, segments=2)
    p_cone(key, "glow_cap", r1=0.030, r2=0.010, depth=0.022, loc=(0, 0, 0.309), verts=6)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_ng_arch_gate():
    """ng_arch_gate — drivable light gate (hero, 0.86 wide, 0.70 clear opening)."""
    key = "ng_arch_gate"
    purge(key)

    span, leg_h = 0.700, 0.470
    for sgn in (-1, 1):
        nm = 'r' if sgn > 0 else 'l'
        x = sgn * (span * 0.5 + 0.040)
        leg = p_cube(key, f"frame_leg_{nm}", size=(0.080, 0.070, leg_h),
                     loc=(x, 0, leg_h * 0.5 + 0.020))
        bevel(leg, width=0.006, segments=2)
        p_cube(key, f"glow_strip_{nm}", size=(0.020, 0.076, leg_h - 0.060),
               loc=(x, 0, leg_h * 0.5 + 0.020))
        foot = p_cube(key, f"base_{nm}", size=(0.130, 0.120, 0.022), loc=(x, 0, 0.011))
        bevel(foot, width=0.004, segments=2)

    beam = p_cube(key, "frame_beam", size=(span + 0.160, 0.070, 0.075),
                  loc=(0, 0, leg_h + 0.058))
    bevel(beam, width=0.006, segments=2)
    p_cube(key, "glow_strip_top", size=(span + 0.100, 0.076, 0.018),
           loc=(0, 0, leg_h + 0.058))
    sign = p_cube(key, "panel_sign", size=(0.260, 0.016, 0.070),
                  loc=(0, -0.040, leg_h + 0.132))
    bevel(sign, width=0.004, segments=2)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_ng_ring_float():
    """ng_ring_float — a hoop the car drives through (0.40 m clear bore).

    Sunk until the bore's floor IS ground level. A hoop resting on feet would
    hold its opening 70 mm up and a 100 mm car would nose straight into the
    tube — so the lower arc is buried and the feet clamp it where it enters.
    """
    key = "ng_ring_float"
    purge(key)

    major, minor = 0.222, 0.024
    cz = major - minor + 0.005              # bore floor 5 mm above the deck

    # Ring plane is XZ (normal along Y) so the bore faces down the track.
    p_torus(key, "ring_hoop", major=major, minor=minor, mseg=30, nseg=8,
            loc=(0, 0, cz), rot=(90, 0, 0))
    p_torus(key, "glow_inner", major=0.200, minor=0.009, mseg=30, nseg=6,
            loc=(0, 0, cz), rot=(90, 0, 0))

    for sgn in (-1, 1):
        nm = 'r' if sgn > 0 else 'l'
        foot = p_cube(key, f"foot_{nm}", size=(0.055, 0.100, 0.075),
                      loc=(sgn * 0.150, 0, 0.0375))
        bevel(foot, width=0.005, segments=2)

    finish_prop(coll_objects(key), subd=0, smooth=50.0)
    return _report(key)


def build_ng_barrier_glow():
    """ng_barrier_glow — low light barrier (wall, 0.50 x 0.14)."""
    key = "ng_barrier_glow"
    purge(key)

    body = p_cube(key, "barrier_body", size=(0.500, 0.050, 0.120), loc=(0, 0, 0.072))
    bevel(body, width=0.006, segments=2)
    p_cube(key, "glow_strip", size=(0.470, 0.058, 0.026), loc=(0, 0, 0.092))

    for sgn in (-1, 1):
        nm = 'r' if sgn > 0 else 'l'
        cap = p_cube(key, f"cap_{nm}", size=(0.030, 0.070, 0.140),
                     loc=(sgn * 0.250, 0, 0.070))
        bevel(cap, width=0.005, segments=2)

    foot = p_cube(key, "foot_rail", size=(0.520, 0.090, 0.014), loc=(0, 0, 0.007))
    bevel(foot, width=0.004, segments=2)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_ng_data_cube():
    """ng_data_cube — stacked data blocks (decor cluster, 0.24 m tall)."""
    key = "ng_data_cube"
    purge(key)

    stack = [(0.150, 0.000, 0.0), (0.115, 0.084, 22.0), (0.078, 0.150, -16.0)]
    for i, (s, z, yaw) in enumerate(stack):
        c = p_cube(key, f"cube_{i}", size=(s, s, s * 0.62),
                   loc=(0, 0, z + s * 0.31), rot=(0, 0, yaw))
        bevel(c, width=0.008, segments=3)

    for i, (s, z, yaw) in enumerate(stack[:2]):
        p_cube(key, f"glow_seam_{i}", size=(s * 1.04, s * 1.04, 0.010),
               loc=(0, 0, z + s * 0.62 + 0.003), rot=(0, 0, yaw))

    base = p_cube(key, "base_pad", size=(0.185, 0.185, 0.010), loc=(0, 0, 0.005))
    bevel(base, width=0.003, segments=2)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_ng_spire():
    """ng_spire — skyline spire (hero, 0.75 m tall)."""
    key = "ng_spire"
    purge(key)

    shaft = p_cone(key, "spire_shaft", r1=0.070, r2=0.016, depth=0.620,
                   loc=(0, 0, 0.325), verts=6)
    bevel(shaft, width=0.004, segments=2)

    for i, z in enumerate((0.180, 0.330, 0.480)):
        t = (z - 0.015) / 0.620
        r = 0.070 * (1 - t) + 0.016 * t + 0.006
        p_cone(key, f"glow_ring_{i}", r1=r, r2=r, depth=0.012, loc=(0, 0, z), verts=6)

    p_ico(key, "spire_tip", r=0.048, subdiv=1, loc=(0, 0, 0.690), scale=(1, 1, 1.5))
    base = p_cone(key, "base_plinth", r1=0.105, r2=0.086, depth=0.030,
                  loc=(0, 0, 0.015), verts=6)
    bevel(base, width=0.004, segments=2)

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


NEON_GRID = [build_ng_pylon, build_ng_arch_gate, build_ng_ring_float,
             build_ng_barrier_glow, build_ng_data_cube, build_ng_spire]


# --- Beach Boardwalk -------------------------------------------------------

def build_bb_palm():
    """bb_palm — leaning palm (hero, ~0.62 m tall)."""
    key = "bb_palm"
    purge(key)

    # Walk a pen up the trunk, tipping it a little further each segment: a
    # stack of truncated cones following a curve reads as a palm, where one
    # straight cylinder reads as a post.
    pos = Vector((0.0, 0.0, 0.0))
    ang = 0.0
    segs = 6
    for i in range(segs):
        h = 0.090
        a = math.radians(ang)
        d = Vector((0.0, -math.sin(a), math.cos(a)))
        r_lo = 0.030 - 0.0024 * i
        r_hi = 0.030 - 0.0024 * (i + 1)
        p_cone(key, f"trunk_seg_{i}", r1=r_lo, r2=r_hi, depth=h,
               loc=tuple(pos + d * (h * 0.5)), rot=(ang, 0, 0), verts=10)
        pos = pos + d * h
        ang += 2.6

    crown = pos
    p_cone(key, "crown_collar", r1=0.034, r2=0.026, depth=0.030,
           loc=tuple(crown + Vector((0, 0, 0.004))), verts=10)

    # Blades, not spikes: the cone is squashed along its own Y at creation, and
    # after the pitch/yaw that axis points very nearly straight up — so each
    # frond lies flat, face to the sky, the way a palm actually reads.
    for i in range(7):
        yaw = i * (360.0 / 7)
        pitch = 62.0 if i % 2 == 0 else 86.0
        p = math.radians(pitch)
        y = math.radians(yaw)
        d = Vector((math.sin(p) * math.sin(y), -math.sin(p) * math.cos(y), math.cos(p)))
        p_cone(key, f"frond_{i}", r1=0.042, r2=0.006, depth=0.245,
               loc=tuple(crown + d * 0.122 + Vector((0, 0, 0.012))),
               rot=(pitch, 0, yaw), verts=4, scale=(1.0, 0.30, 1.0))

    for i, (dx, dy) in enumerate(((0.028, 0.012), (-0.022, 0.024), (0.006, -0.028))):
        p_ico(key, f"coconut_{i}", r=0.019, subdiv=2,
              loc=(crown.x + dx, crown.y + dy, crown.z - 0.012))

    finish_prop(coll_objects(key), subd=0, smooth=45.0)
    _sit_on_ground(key)
    return _report(key)


def build_bb_surfboard_ramp():
    """bb_surfboard_ramp — a longboard laid over a sand ramp (0.42 run, 0.095 rise).

    The sand is the ramp — a heavily bevelled wedge, so the shape a car climbs
    is the shape the eye reads — and the board is the deck laid on top of it,
    overhanging both ends to give a lip rather than a step.
    """
    key = "bb_surfboard_ramp"
    purge(key)

    run, rise = 0.420, 0.095
    tilt = math.degrees(math.atan2(rise, run))          # 12.75 deg
    c, s = math.cos(math.radians(tilt)), math.sin(math.radians(tilt))

    sand = p_wedge(key, "sand_ramp", size=(0.255, run, rise))
    bevel(sand, width=0.030, segments=4, angle_deg=25.0)

    p_uv(key, "board_deck", r=0.5, segs=18, rings=9,
         loc=(0, 0, rise * 0.5 + 0.013), scale=(0.150, 0.460, 0.024), rot=(tilt, 0, 0))
    p_uv(key, "stripe_line", r=0.5, segs=12, rings=7,
         loc=(0, 0, rise * 0.5 + 0.017), scale=(0.024, 0.430, 0.024), rot=(tilt, 0, 0))

    # Twin fins at the raised tail: the board is laid fin-side up, which is both
    # how you'd prop one against a dune and what makes it read as a surfboard.
    # They sit outboard of the car's 0.20 m track, so it passes between them.
    for sgn in (-1, 1):
        ly = 0.185
        p_cone(key, f"fin_{'r' if sgn > 0 else 'l'}", r1=0.024, r2=0.005, depth=0.044,
               loc=(sgn * 0.052, ly * c, rise * 0.5 + 0.013 + ly * s + 0.020),
               rot=(tilt - 12.0, 0, 0), verts=4, scale=(1.0, 0.28, 1.0))

    p_ico(key, "sand_lip", r=0.5, subdiv=2, loc=(0, -0.232, 0.008),
          scale=(0.250, 0.130, 0.034))

    finish_prop(coll_objects(key), subd=0, smooth=50.0)
    _sit_on_ground(key)
    return _report(key)


def build_bb_plank_wall():
    """bb_plank_wall — boardwalk railing (wall, 0.60 x 0.17)."""
    key = "bb_plank_wall"
    purge(key)

    for i, x in enumerate((-0.270, 0.0, 0.270)):
        po = p_cube(key, f"post_{i}", size=(0.036, 0.036, 0.165), loc=(x, 0, 0.0825))
        bevel(po, width=0.003, segments=2)
        p_cube(key, f"cap_{i}", size=(0.048, 0.048, 0.012), loc=(x, 0, 0.171))

    for i, z in enumerate((0.150, 0.092)):
        r = p_cube(key, f"rail_{i}", size=(0.600, 0.022, 0.032), loc=(0, 0, z))
        bevel(r, width=0.003, segments=2)

    deck = p_cube(key, "plank_deck", size=(0.620, 0.150, 0.018), loc=(0, 0.055, 0.009))
    bevel(deck, width=0.002, segments=1)
    for i in range(3):
        p_cube(key, f"plank_seam_{i}", size=(0.620, 0.004, 0.022),
               loc=(0, 0.005 + i * 0.038, 0.010))

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_bb_tiki_torch():
    """bb_tiki_torch — bamboo torch (light post, 0.52 m tall)."""
    key = "bb_tiki_torch"
    purge(key)

    p_cyl(key, "pole_shaft", r=0.016, depth=0.400, loc=(0, 0, 0.215), verts=12)
    for i, z in enumerate((0.090, 0.190, 0.290, 0.385)):
        p_cyl(key, f"node_{i}", r=0.0195, depth=0.011, loc=(0, 0, z), verts=12)

    p_revolve(key, "bowl_rim",
              [(0.0, 0.0), (0.046, 0.010), (0.052, 0.044), (0.045, 0.044),
               (0.038, 0.014), (0.0, 0.008)], segments=20, loc=(0, 0, 0.415))

    p_cone(key, "flame_core", r1=0.030, r2=0.0, depth=0.078,
           loc=(0, 0, 0.492), verts=8)

    base = p_cone(key, "base_pad", r1=0.060, r2=0.050, depth=0.020,
                  loc=(0, 0, 0.010), verts=14)
    bevel(base, width=0.003, segments=2)

    finish_prop(coll_objects(key), subd=0, smooth=45.0)
    _sit_on_ground(key)
    return _report(key)


def build_bb_beach_ball():
    """bb_beach_ball — 0.16 m ball with real panel geometry (dynamic)."""
    key = "bb_beach_ball"
    purge(key)

    r = 0.080
    p_uv(key, "ball_body", r=r, segs=20, rings=10, loc=(0, 0, r))

    # Three lat-long panels: a thin arc band spun through 60 deg, sitting just
    # proud of the sphere. Separate objects so each takes its own colour.
    steps = 9
    prof = []
    for i in range(steps + 1):
        t = math.pi * i / steps
        prof.append((math.sin(t) * (r + 0.0016), -math.cos(t) * (r + 0.0016)))
    for i in range(steps, -1, -1):
        t = math.pi * i / steps
        prof.append((math.sin(t) * (r + 0.0004), -math.cos(t) * (r + 0.0004)))
    prof.append(prof[0])

    for i in range(3):
        p_revolve(key, f"panel_{i}", prof, segments=7, angle_deg=60.0,
                  loc=(0, 0, r), rot=(0, 0, i * 120.0))

    finish_prop(coll_objects(key), subd=0, smooth=60.0)
    _sit_on_ground(key)
    return _report(key)


def build_bb_sandcastle():
    """bb_sandcastle — crenellated castle (obstacle, 0.30 base, 0.26 tall)."""
    key = "bb_sandcastle"
    purge(key)

    p_cone(key, "sand_base", r1=0.165, r2=0.150, depth=0.030, loc=(0, 0, 0.015), verts=12)

    corners = ((-0.105, -0.105), (0.105, -0.105), (0.105, 0.105), (-0.105, 0.105))
    for i, (x, y) in enumerate(corners):
        p_cone(key, f"tower_{i}", r1=0.048, r2=0.040, depth=0.150,
               loc=(x, y, 0.105), verts=10)
        p_cone(key, f"tower_lip_{i}", r1=0.048, r2=0.048, depth=0.014,
               loc=(x, y, 0.183), verts=10)
        for j in range(4):
            a = math.radians(45 + j * 90)
            p_cube(key, f"merlon_{i}{j}", size=(0.022, 0.022, 0.026),
                   loc=(x + math.cos(a) * 0.033, y + math.sin(a) * 0.033, 0.202),
                   rot=(0, 0, math.degrees(a)))

    for i, (dx, dy, w, d) in enumerate(((0, -0.105, 0.150, 0.055),
                                        (0, 0.105, 0.150, 0.055),
                                        (-0.105, 0, 0.055, 0.150),
                                        (0.105, 0, 0.055, 0.150))):
        wl = p_cube(key, f"wall_{i}", size=(w, d, 0.100), loc=(dx, dy, 0.075))
        bevel(wl, width=0.004, segments=2)

    p_cone(key, "keep_tower", r1=0.060, r2=0.046, depth=0.200, loc=(0, 0, 0.130), verts=12)
    p_cone(key, "keep_roof", r1=0.058, r2=0.0, depth=0.070, loc=(0, 0, 0.263), verts=12)
    p_cyl(key, "flag_pole", r=0.0035, depth=0.070, loc=(0, 0, 0.320), verts=8)
    p_cube(key, "flag_cloth", size=(0.002, 0.052, 0.030), loc=(0, 0.026, 0.340))

    finish_prop(coll_objects(key), subd=0, smooth=45.0)
    _sit_on_ground(key)
    return _report(key)


BEACH_BOARDWALK = [build_bb_palm, build_bb_surfboard_ramp, build_bb_plank_wall,
                   build_bb_tiki_torch, build_bb_beach_ball, build_bb_sandcastle]


# --- Volcano Foundry -------------------------------------------------------

def build_vf_rock_arch():
    """vf_rock_arch — basalt arch (hero, 0.75 wide, 0.46 x 0.53 clear opening).

    Boulders are walked along the arch's own centreline — two vertical legs and
    a semicircle over the top — at a spacing well under one boulder, so the
    chunks fuse into a continuous span instead of hanging in the air as a row
    of separate rocks.
    """
    key = "vf_rock_arch"
    purge(key)

    x_leg, h_leg = 0.300, 0.300
    pts = [(sgn * x_leg, z) for sgn in (-1, 1) for z in (0.058, 0.140, 0.222)]
    for i in range(11):
        th = math.radians(180 - i * 18)
        pts.append((x_leg * math.cos(th), h_leg + x_leg * math.sin(th)))

    # Deterministic irregularity: fixed tables, so the rock looks eroded but the
    # asset rebuilds byte-identically every time.
    # Deep enough in Y, and overlapping enough along the curve, that the
    # silhouette closes into one mass — a chain of separate blobs is the
    # failure mode this table is sized against.
    sizes = [(0.176, 0.205, 0.162), (0.156, 0.188, 0.178),
             (0.170, 0.212, 0.152), (0.152, 0.192, 0.166)]
    for i, (x, z) in enumerate(pts):
        p_ico(key, f"rock_{i}", r=0.5, subdiv=1, loc=(x, (i % 3 - 1) * 0.012, z),
              scale=sizes[i % 4],
              rot=((i * 37) % 90 - 45, (i * 53) % 90 - 45, (i * 29) % 180))

    # Buttressed feet, so the arch looks planted rather than balanced.
    for i, sgn in enumerate((-1, 1)):
        p_ico(key, f"rock_foot_{i}", r=0.5, subdiv=1,
              loc=(sgn * 0.322, 0, 0.044), scale=(0.215, 0.245, 0.110),
              rot=(0, sgn * 8, i * 41))

    for i, (x, z, ry) in enumerate(((-0.322, 0.150, 14), (0.318, 0.235, -10),
                                    (0.030, 0.585, 0))):
        p_cube(key, f"lava_crack_{i}", size=(0.022, 0.150, 0.075),
               loc=(x, 0, z), rot=(0, ry, 0))

    finish_prop(coll_objects(key), subd=0, smooth=28.0)
    _sit_on_ground(key)
    return _report(key)


def build_vf_obsidian_block():
    """vf_obsidian_block — faceted glass block (wall, 0.35 x 0.20 x 0.19)."""
    key = "vf_obsidian_block"
    purge(key)

    p_ico(key, "obsidian_main", r=0.5, subdiv=1, loc=(0, 0, 0.092),
          scale=(0.350, 0.195, 0.185), rot=(0, 0, 8))
    p_ico(key, "shard_0", r=0.5, subdiv=1, loc=(-0.140, 0.030, 0.062),
          scale=(0.110, 0.095, 0.140), rot=(14, -10, 26))
    p_ico(key, "shard_1", r=0.5, subdiv=1, loc=(0.155, -0.028, 0.048),
          scale=(0.095, 0.088, 0.105), rot=(-11, 16, -18))
    p_cube(key, "glow_seam", size=(0.320, 0.026, 0.018), loc=(0, 0, 0.070), rot=(0, 4, 8))

    finish_prop(coll_objects(key), subd=0, smooth=22.0)
    _sit_on_ground(key)
    return _report(key)


def build_vf_steam_vent():
    """vf_steam_vent — ground vent (hazard, 0.26 across, 0.07 proud)."""
    key = "vf_steam_vent"
    purge(key)

    p_revolve(key, "vent_ring",
              [(0.092, 0.0), (0.128, 0.0), (0.128, 0.058), (0.104, 0.066),
               (0.092, 0.048), (0.092, 0.0)], segments=24)

    for i in range(5):
        p_cube(key, f"grate_bar_{i}", size=(0.190, 0.016, 0.012),
               loc=(0, -0.064 + i * 0.032, 0.040))
    p_cyl(key, "lava_glow", r=0.086, depth=0.008, loc=(0, 0, 0.030), verts=22)

    for i, (x, y, sc) in enumerate(((-0.130, 0.055, 0.070), (0.126, -0.048, 0.062),
                                    (0.030, 0.135, 0.055))):
        p_ico(key, f"rock_lip_{i}", r=0.5, subdiv=1, loc=(x, y, 0.016),
              scale=(sc, sc * 0.9, sc * 0.55), rot=(0, 0, i * 37))

    finish_prop(coll_objects(key), subd=0, smooth=30.0)
    _sit_on_ground(key)
    return _report(key)


def build_vf_barrel():
    """vf_barrel — foundry barrel (dynamic, 0.155 dia x 0.21 tall)."""
    key = "vf_barrel"
    purge(key)

    p_revolve(key, "barrel_body",
              [(0.0, 0.0), (0.068, 0.0), (0.070, 0.014), (0.077, 0.100),
               (0.070, 0.186), (0.068, 0.200), (0.0, 0.200)], segments=24)

    for i, z in enumerate((0.048, 0.152)):
        p_torus(key, f"band_{i}", major=0.0765, minor=0.0075, mseg=24, nseg=6,
                loc=(0, 0, z))
    p_cyl(key, "barrel_lid", r=0.058, depth=0.010, loc=(0, 0, 0.204), verts=22)
    p_torus(key, "glow_hazard", major=0.0745, minor=0.005, mseg=24, nseg=6,
            loc=(0, 0, 0.100))

    finish_prop(coll_objects(key), subd=0, smooth=45.0)
    _sit_on_ground(key)
    return _report(key)


def build_vf_grate_ramp():
    """vf_grate_ramp — steel grate ramp (0.45 run, 0.10 rise)."""
    key = "vf_grate_ramp"
    purge(key)

    run, rise = 0.440, 0.100
    slope = math.degrees(math.atan2(rise, run))     # 12.8 deg
    c, s = math.cos(math.radians(slope)), math.sin(math.radians(slope))

    deck = p_wedge(key, "ramp_deck", size=(0.260, run, rise))
    bevel(deck, width=0.004, segments=2)

    for i in range(7):
        ly = -0.180 + i * 0.060
        p_cube(key, f"slat_{i}", size=(0.250, 0.020, 0.010),
               loc=(0, ly * c, rise * 0.5 + ly * s + 0.006), rot=(slope, 0, 0))

    for sgn in (-1, 1):
        nm = 'r' if sgn > 0 else 'l'
        r = p_cube(key, f"rail_{nm}", size=(0.016, run + 0.020, 0.026),
                   loc=(sgn * 0.130, 0, rise * 0.5 + 0.014), rot=(slope, 0, 0))
        bevel(r, width=0.003, segments=2)
        p_cube(key, f"strut_{nm}", size=(0.014, 0.026, rise),
               loc=(sgn * 0.116, run * 0.5 - 0.020, rise * 0.5))

    finish_prop(coll_objects(key), subd=0)
    _sit_on_ground(key)
    return _report(key)


def build_vf_crag_spire():
    """vf_crag_spire — columnar basalt spire (hero, 0.70 m tall)."""
    key = "vf_crag_spire"
    purge(key)

    z = 0.0
    for i in range(5):
        h = 0.155 - i * 0.012
        r_lo = 0.100 - i * 0.016
        r_hi = 0.100 - (i + 1) * 0.016
        p_cone(key, f"crag_seg_{i}", r1=r_lo, r2=r_hi, depth=h,
               loc=(0.006 * i, -0.004 * i, z + h * 0.5), rot=(0, 0, i * 14.0), verts=6)
        z += h - 0.006

    p_cone(key, "crag_tip", r1=0.024, r2=0.0, depth=0.075,
           loc=(0.028, -0.018, z + 0.030), rot=(4, 6, 20), verts=6)

    for i, (x, y, zz, rr) in enumerate(((-0.092, 0.030, 0.120, 12),
                                        (0.086, -0.040, 0.245, -16),
                                        (-0.070, -0.050, 0.395, 22))):
        p_cone(key, f"spike_{i}", r1=0.030, r2=0.0, depth=0.105,
               loc=(x, y, zz), rot=(58, 0, rr), verts=5)

    for i, (x, zz) in enumerate(((-0.070, 0.150), (0.066, 0.330))):
        p_cube(key, f"lava_vein_{i}", size=(0.018, 0.110, 0.055),
               loc=(x, 0, zz), rot=(0, 0, i * 40))

    p_cone(key, "crag_foot", r1=0.140, r2=0.112, depth=0.028, loc=(0, 0, 0.014), verts=8)

    finish_prop(coll_objects(key), subd=0, smooth=26.0)
    _sit_on_ground(key)
    return _report(key)


VOLCANO_FOUNDRY = [build_vf_rock_arch, build_vf_obsidian_block, build_vf_steam_vent,
                   build_vf_barrel, build_vf_grate_ramp, build_vf_crag_spire]


THEMES = {
    "toy_workshop": TOY_WORKSHOP,
    "neon_grid": NEON_GRID,
    "beach_boardwalk": BEACH_BOARDWALK,
    "volcano_foundry": VOLCANO_FOUNDRY,
}


def build_theme(name, export=True):
    """Build one theme family (see THEMES) — one family per call keeps each
    Blender round-trip short enough to inspect before moving on."""
    return _build_family(THEMES[name], export)


def build_all(export=True):
    out = build_arcade(export)
    for name in THEMES:
        out += build_theme(name, export)
    return out
