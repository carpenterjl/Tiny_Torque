using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Bolts a design's five cosmetic slots onto a freshly built car.
    ///
    /// The pack authors every item around its own mount point — the crown is
    /// modelled at the roof crown, the rim at the hub centre — so attaching one
    /// is a parent with an identity local pose and nothing else. What this class
    /// owns is WHERE that pose is on a given car.
    ///
    /// The manifest's mount frames are absolute coordinates on the 4.55 m
    /// Blender coupe, which is no use to a Baja, a LowRacer or something the
    /// player assembled in the garage. So they are re-expressed as FRACTIONS of
    /// the authoring car's own body box and applied to whatever body box the
    /// design actually has. On the coupe that reproduces the authored mount
    /// exactly (same box, uniformly scaled); on everything else it puts the hat
    /// on the roof instead of through it.
    ///
    /// The bobble is the exception: the manifest mounts it on the antenna tip,
    /// and antenna height varies with style and size, so it reads the built
    /// antenna's own bounds rather than any table.
    ///
    /// Everything here is cosmetic. Nothing touches mass, aero, colliders or the
    /// wheel configs, so MassProperties, the bots, LAN peers and the headless
    /// Opus regression all see the same car they saw before.
    /// </summary>
    public static class CosmeticMounts
    {
        // Mount frames from TinyTorque_cosmetics.json, divided through by the
        // source coupe's body box (x -2.345..2.206, z 0.125..1.354): fz runs
        // rear -> nose, fy runs floor -> roof.
        private const float TopperFZ = 0.4603f, TopperFY = 0.9383f;
        private const float OrnamentFZ = 0.8799f, OrnamentFY = 0.6112f;
        private const float WingFZ = 0.0758f, WingFY = 0.8830f;

        /// <summary>The stock wing's rake, from the pack's MOUNTS table. Blender
        /// authors -8 deg about its +Y; that axis becomes Unity +X and the
        /// handedness flip reverses the sense, so the wing pitches +8 about X —
        /// leading edge down, which is what a rear wing is for.</summary>
        private const float WingRakeDeg = 8f;

        /// <summary>Sink the topper a whisker into the roof so a shallow-based
        /// item can never read as floating (the pack does the same, 2 mm at
        /// authoring scale).</summary>
        private const float TopperSink = 0.0004f;

        /// <summary>
        /// Attach every cosmetic the design asks for. Safe to call on any built
        /// car; missing ids, missing FBX and body-less designs all no-op.
        /// </summary>
        public static void Apply(VehicleFactory.Built built, VehicleDesign design)
        {
            if (design == null || built.root == null) return;

            var car = built.root.transform;
            Bounds body = BodyBounds(car);

            Fit(car, design.cosTopper, "cos_topper",
                MountPoint(body, TopperFZ, TopperFY) + Vector3.down * TopperSink,
                Quaternion.identity);

            Fit(car, design.cosOrnament, "cos_ornament",
                MountPoint(body, OrnamentFZ, OrnamentFY), Quaternion.identity);

            Fit(car, design.cosWing, "cos_wing",
                MountPoint(body, WingFZ, WingFY),
                Quaternion.Euler(WingRakeDeg, 0f, 0f));

            Fit(car, design.cosBobble, "cos_bobble",
                BobblePoint(built, body), Quaternion.identity);

            ApplyRims(built, design);
        }

        /// <summary>A point on the body box from the authored fractions. X stays
        /// on the centreline: every non-rim slot in the pack is a centreline
        /// part.</summary>
        private static Vector3 MountPoint(Bounds b, float fz, float fy)
            => new Vector3(b.center.x,
                           Mathf.Lerp(b.min.y, b.max.y, fy),
                           Mathf.Lerp(b.min.z, b.max.z, fz));

        /// <summary>
        /// The antenna tip, which is what the pack's bobble replaces. Taken from
        /// the built antenna's own bounds so whip/flag/twin/stub and any size
        /// scale all land right. Falls back to the rear deck for a design with
        /// no antenna at all — a bobble should still be visible on a car that
        /// has nothing to hang it from.
        /// </summary>
        private static Vector3 BobblePoint(VehicleFactory.Built built, Bounds body)
        {
            var visuals = built.antennaVisuals;
            if (visuals != null && visuals.Length > 0 && visuals[0] != null)
            {
                var b = LocalBounds(built.root.transform, visuals[0].transform);
                if (b.size.sqrMagnitude > 0f)
                    return new Vector3(b.center.x, b.max.y, b.center.z);
            }
            return new Vector3(body.center.x, body.max.y,
                               Mathf.Lerp(body.min.z, body.max.z, 0.15f));
        }

        /// <summary>
        /// One rim per wheel, parented to the wheel's visual holder so it spins,
        /// steers and travels with the tyre for free. Scaled by
        /// radius/WheelAuthorRadius — the same factor BuildWheelViz applies to
        /// the tyre — and given the same half-turn on the axle side where the
        /// face would otherwise point inboard.
        /// </summary>
        private static void ApplyRims(VehicleFactory.Built built, VehicleDesign design)
        {
            if (string.IsNullOrEmpty(design.cosRim)) return;
            var car = built.car;
            if (car == null || design.wheels == null) return;

            for (int i = 0; i < design.wheels.Count; i++)
            {
                var holder = car.GetWheelVisual(i);
                if (holder == null) continue;
                var w = design.wheels[i];

                PartVisualFactory.HideStockRim(holder);

                var go = CosmeticCatalog.Build(holder, design.cosRim);
                if (go == null) continue;
                go.name = "cos_rim";
                float s = Mathf.Max(0.01f, w.radius) / PartVisualFactory.WheelAuthorRadius;
                go.transform.localScale = Vector3.one * s;
                // BuildWheelViz's rule, mirrored exactly: on the side where +X
                // points inboard the wheel is spun half a turn, and the rim has
                // to come with it or it ends up inside the car.
                float inboardSign = w.localPos.x > 0.001f ? -1f : 1f;
                if (inboardSign >= 0f)
                    go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            }
        }

        private static void Fit(Transform car, string id, string name,
            Vector3 localPos, Quaternion localRot)
        {
            if (string.IsNullOrEmpty(id)) return;
            var mount = new GameObject(name).transform;
            mount.SetParent(car, false);
            mount.localPosition = localPos;
            mount.localRotation = localRot;
            if (CosmeticCatalog.Build(mount, id) == null)
                Object.Destroy(mount.gameObject);   // no asset: leave nothing behind
        }

        /// <summary>
        /// The body shell's extent in car-local space. Reads the renderers under
        /// CarVehicle's "BodyMesh" holder, which covers both the authored-FBX
        /// bodies and the primitive shapes, so a hand-built garage design gets a
        /// real box rather than the nominal one. Falls back to the design's
        /// declared bodySize when the car has no visual at all.
        /// </summary>
        private static Bounds BodyBounds(Transform car)
        {
            var holder = car.Find("BodyMesh");
            if (holder != null)
            {
                var b = LocalBounds(car, holder);
                if (b.size.sqrMagnitude > 1e-8f) return b;
            }
            var cv = car.GetComponent<CarVehicle>();
            Vector3 size = cv != null ? cv.bodySize : new Vector3(0.2f, 0.1f, 0.42f);
            return new Bounds(Vector3.zero, size);
        }

        /// <summary>World renderer bounds of a subtree, expressed in
        /// <paramref name="frame"/>'s local space. The eight-corner walk is
        /// needed because Renderer.bounds is a world AABB, not a local one.</summary>
        private static Bounds LocalBounds(Transform frame, Transform subtree)
        {
            var rends = subtree.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            var result = new Bounds();
            foreach (var r in rends)
            {
                var wb = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? wb.min.x : wb.max.x,
                        (c & 2) == 0 ? wb.min.y : wb.max.y,
                        (c & 4) == 0 ? wb.min.z : wb.max.z);
                    var local = frame.InverseTransformPoint(corner);
                    if (!any) { result = new Bounds(local, Vector3.zero); any = true; }
                    else result.Encapsulate(local);
                }
            }
            return any ? result : new Bounds();
        }
    }
}
