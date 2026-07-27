using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// Visuals for the arcade objects. Each builder tries the authored Blender
    /// mesh under <c>Resources/TrackProps/</c> first and falls back to runtime
    /// primitives — the same idiom the vehicle parts use, so arcade mode is fully
    /// playable before a single FBX ships.
    ///
    /// Nothing built here carries a collider: the item box, missile and banana own
    /// their own trigger volumes, sized in code so gameplay never depends on what
    /// an artist did to the mesh.
    /// </summary>
    public static class ArcadeVfx
    {
        private static Material _box, _glow, _banana, _missile, _fin, _shield, _burst;
        private static Material _smoke, _slick;

        private static Material Mat(ref Material slot, Color c, float smooth, Color emission)
        {
            if (slot == null)
            {
                slot = TrackBuilder.StandardMat(c);
                slot.SetFloat("_Glossiness", smooth);
                if (emission.maxColorComponent > 0f)
                {
                    slot.EnableKeyword("_EMISSION");
                    slot.SetColor("_EmissionColor", emission);
                }
            }
            return slot;
        }

        private static Material BoxShell => Mat(ref _box, new Color(0.95f, 0.72f, 0.10f), 0.75f,
            new Color(0.45f, 0.30f, 0.02f));
        private static Material Glow => Mat(ref _glow, new Color(1f, 0.95f, 0.55f), 0.9f,
            new Color(1.0f, 0.80f, 0.25f));
        private static Material BananaSkin => Mat(ref _banana, new Color(0.96f, 0.86f, 0.16f), 0.5f, Color.black);
        private static Material MissileBody => Mat(ref _missile, new Color(0.85f, 0.22f, 0.16f), 0.6f,
            new Color(0.35f, 0.05f, 0.02f));
        private static Material MissileFin => Mat(ref _fin, new Color(0.30f, 0.31f, 0.34f), 0.6f, Color.black);
        private static Material ShieldSkin => Mat(ref _shield, new Color(0.35f, 0.80f, 1.0f), 0.9f,
            new Color(0.15f, 0.55f, 0.85f));

        /// <summary>
        /// Additive, depth-write-off material for anything that fades: the
        /// explosion flash and the shield bubble.
        ///
        /// The Standard shader does not switch to transparent by setting a colour
        /// alpha — its blend mode, ZWrite, keywords and render queue are all
        /// separate state that the material inspector normally writes for you, so
        /// a runtime material has to set them by hand or it renders as opaque.
        /// </summary>
        private static Material BurstSkin
        {
            get
            {
                if (_burst == null)
                {
                    _burst = TrackBuilder.StandardMat(new Color(1f, 0.75f, 0.30f, 1f));
                    _burst.SetFloat("_Mode", 3f);                       // Transparent
                    _burst.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _burst.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);   // additive
                    _burst.SetInt("_ZWrite", 0);
                    _burst.DisableKeyword("_ALPHATEST_ON");
                    _burst.EnableKeyword("_ALPHABLEND_ON");
                    _burst.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    _burst.EnableKeyword("_EMISSION");
                    _burst.renderQueue = 3000;
                }
                return _burst;
            }
        }

        /// <summary>
        /// Alpha-blended, depth-write-off material for the area hazards.
        ///
        /// Same transparency setup as <see cref="BurstSkin"/> with one constant
        /// changed — <c>_DstBlend = OneMinusSrcAlpha</c> instead of <c>One</c> —
        /// and that constant is the whole difference between a cloud and a
        /// fireball. Additive blending can only ADD light, so additive "smoke"
        /// glows and the track stays perfectly visible through it; smoke has to
        /// OCCLUDE, which needs a real over-blend. No emission, for the same
        /// reason.
        /// </summary>
        private static Material AlphaSkin(ref Material slot, Color c, float smooth)
        {
            if (slot == null)
            {
                slot = TrackBuilder.StandardMat(c);
                slot.SetFloat("_Mode", 3f);                         // Transparent
                slot.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                slot.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                slot.SetInt("_ZWrite", 0);
                slot.DisableKeyword("_ALPHATEST_ON");
                slot.EnableKeyword("_ALPHABLEND_ON");
                slot.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                slot.SetFloat("_Glossiness", smooth);
                slot.renderQueue = 3000;
            }
            return slot;
        }

        /// <summary>Murky yellow-green, matte. Alpha lives in the property block
        /// <see cref="AreaHazardViz"/> drives, so the value here is only the
        /// starting point.</summary>
        private static Material SmokeSkin =>
            AlphaSkin(ref _smoke, new Color(0.60f, 0.72f, 0.42f, 0.72f), 0.05f);

        /// <summary>Near-black and glossy — an oil slick reads as a slick almost
        /// entirely through its specular sheen, so this is the one hazard skin
        /// that wants high smoothness.</summary>
        private static Material SlickSkin =>
            AlphaSkin(ref _slick, new Color(0.07f, 0.06f, 0.09f, 0.85f), 0.95f);

        private static Material _driftSmoke;
        /// <summary>Base for drift tire smoke: alpha-blended like the hazards
        /// (smoke must occlude, so no additive), authored white — the per-car
        /// colour and the fade arrive through <see cref="DriftSmoke"/>'s
        /// property block, so one material serves the whole field.</summary>
        public static Material DriftSmokeSkin =>
            AlphaSkin(ref _driftSmoke, new Color(1f, 1f, 1f, 0.5f), 0.05f);

        /// <summary>Collider-free primitive piece on the parent's layer.</summary>
        private static Transform Piece(PrimitiveType type, Transform parent, Material mat,
            Vector3 pos, Vector3 euler, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go.transform;
        }

        private static GameObject TryMesh(string key, Transform parent, Material fallback,
            params (string token, Material mat)[] tokens)
        {
            var go = PartMeshLibrary.TryInstantiate(key, parent, parent.gameObject.layer,
                PartMeshLibrary.PropRoot);
            if (go != null && tokens.Length > 0) PartMeshLibrary.AssignByName(go, fallback, tokens);
            return go;
        }

        /// <summary>The floating power-up box (0.24 m, drivable through).</summary>
        public static void BuildItemBox(Transform parent)
        {
            if (TryMesh("arc_item_box", parent, BoxShell,
                    ("box", BoxShell), ("frame", BoxShell), ("glyph", Glow), ("core", Glow)) != null)
                return;

            Piece(PrimitiveType.Cube, parent, BoxShell, Vector3.zero, new Vector3(0f, 45f, 0f),
                new Vector3(0.22f, 0.22f, 0.22f));
            Piece(PrimitiveType.Sphere, parent, Glow, Vector3.zero, Vector3.zero,
                new Vector3(0.13f, 0.13f, 0.13f));
        }

        /// <summary>A dropped banana peel (~0.12 m across).</summary>
        public static void BuildBanana(Transform parent)
        {
            if (TryMesh("arc_banana", parent, BananaSkin,
                    ("peel", BananaSkin), ("inner", BananaSkin), ("stem", BananaSkin)) != null)
                return;

            Piece(PrimitiveType.Sphere, parent, BananaSkin, Vector3.zero, Vector3.zero,
                new Vector3(0.11f, 0.035f, 0.07f));
            for (int i = 0; i < 3; i++)
                Piece(PrimitiveType.Capsule, parent, BananaSkin,
                    new Vector3(0f, 0.012f, 0f), new Vector3(78f, i * 60f - 60f, 0f),
                    new Vector3(0.022f, 0.05f, 0.022f));
        }

        /// <summary>
        /// Fixed puff layout for <see cref="BuildSmoke"/>: offset, then diameter.
        ///
        /// Fixed rather than randomised on purpose. In a LAN session the host and
        /// every client build their own copy of the same cloud, and a hazard that
        /// is a different shape on two screens is a hazard two players disagree
        /// about — one of them drives through an edge that is only there locally.
        /// The irregularity comes from the table, not from a draw.
        /// </summary>
        private static readonly (Vector3 pos, float size)[] SmokePuffs =
        {
            (new Vector3( 0.00f,  0.02f,  0.00f), 1.00f),
            (new Vector3( 0.36f,  0.10f,  0.14f), 0.82f),
            (new Vector3(-0.31f,  0.06f,  0.26f), 0.90f),
            (new Vector3( 0.08f,  0.14f, -0.38f), 0.86f),
            (new Vector3(-0.22f, -0.06f, -0.28f), 0.74f),
            (new Vector3( 0.28f, -0.08f, -0.10f), 0.68f),
            (new Vector3(-0.05f,  0.26f,  0.09f), 0.72f),
            (new Vector3( 0.15f, -0.02f,  0.35f), 0.64f),
        };

        /// <summary>
        /// A smoke cloud, authored at radius 1 — <see cref="AreaHazardViz"/> owns
        /// the scale, so growth and fade need no geometry changes here.
        ///
        /// The caller is responsible for putting the root on
        /// <see cref="PartVisualFactory.VizLayer"/>. That is the deliberate
        /// asymmetry with <see cref="BuildBanana"/>, which builds at layer 0: a
        /// peel in front of a CameraSensor is a small yellow object, whereas a
        /// 1.5 m opaque blob is a blindfold, and the on-car sensors are supposed
        /// to see the world, not the effects layer.
        /// </summary>
        public static void BuildSmoke(Transform parent)
        {
            if (TryMesh("arc_smoke", parent, SmokeSkin,
                    ("puff", SmokeSkin), ("cloud", SmokeSkin), ("smoke", SmokeSkin)) != null)
                return;

            foreach (var (pos, size) in SmokePuffs)
                Piece(PrimitiveType.Sphere, parent, SmokeSkin, pos, Vector3.zero,
                    new Vector3(size, size * 0.85f, size));
        }

        /// <summary>
        /// An oil slick, authored at radius 1 in XZ and nearly flat — the same
        /// contract as <see cref="BuildSmoke"/>, so one viz can animate both.
        /// </summary>
        public static void BuildSlick(Transform parent)
        {
            if (TryMesh("arc_slick", parent, SlickSkin,
                    ("slick", SlickSkin), ("oil", SlickSkin), ("pool", SlickSkin)) != null)
                return;

            // A cylinder primitive is radius 0.5 and height 2, hence (2, y, 2).
            Piece(PrimitiveType.Cylinder, parent, SlickSkin, Vector3.zero, Vector3.zero,
                new Vector3(1.7f, 0.006f, 1.7f));

            // Three lobes off-centre, so the pool has an outline rather than
            // reading as a decal someone stamped on the tarmac.
            for (int i = 0; i < 3; i++)
                Piece(PrimitiveType.Cylinder, parent, SlickSkin,
                    Quaternion.Euler(0f, i * 118f, 0f) * new Vector3(0f, 0f, 0.42f),
                    Vector3.zero, new Vector3(1.0f - i * 0.14f, 0.005f, 1.0f - i * 0.14f));
        }

        /// <summary>A homing missile (~0.16 m long, nose along +Z).</summary>
        public static void BuildMissile(Transform parent)
        {
            if (TryMesh("arc_missile", parent, MissileBody,
                    ("body", MissileBody), ("nose", MissileBody),
                    ("fin", MissileFin), ("nozzle", MissileFin)) != null)
                return;

            Piece(PrimitiveType.Capsule, parent, MissileBody, Vector3.zero, new Vector3(90f, 0f, 0f),
                new Vector3(0.045f, 0.065f, 0.045f));
            Piece(PrimitiveType.Sphere, parent, Glow, new Vector3(0f, 0f, -0.07f), Vector3.zero,
                new Vector3(0.05f, 0.05f, 0.05f));
            for (int i = 0; i < 3; i++)
                Piece(PrimitiveType.Cube, parent, MissileFin,
                    new Vector3(0f, 0f, -0.045f), new Vector3(0f, 0f, i * 120f),
                    new Vector3(0.006f, 0.075f, 0.03f));
        }

        /// <summary>
        /// The flash left by a missile. Unit-sized: <see cref="ArcadeBurst"/>
        /// scales the whole thing and fades it through a property block, so the
        /// geometry here is authored at radius 1 and never touched again — colour
        /// included, which is why this takes no tint.
        /// </summary>
        public static void BuildExplosion(Transform parent)
        {
            Piece(PrimitiveType.Sphere, parent, BurstSkin, Vector3.zero, Vector3.zero, Vector3.one);

            // Shards, so it reads as a burst rather than a growing ball.
            for (int i = 0; i < 6; i++)
            {
                float a = i * 60f;
                Piece(PrimitiveType.Cube, parent, BurstSkin,
                    Quaternion.Euler(0f, a, 0f) * new Vector3(0f, 0.1f, 0.55f),
                    new Vector3(20f - i * 7f, a, 0f),
                    new Vector3(0.09f, 0.09f, 0.7f));
            }
        }

        /// <summary>
        /// Drift sparks at the rear wheels. Built on the additive
        /// <see cref="BurstSkin"/> — this one really is light being added, which
        /// is exactly what the smoke skin exists to avoid — and tinted per tier
        /// through a property block by the director, so the geometry is authored
        /// once and never rebuilt as the tier climbs.
        ///
        /// VizLayer, like every other cosmetic: a car's own CameraSensor must not
        /// see its own sparks.
        /// </summary>
        public static Transform BuildDriftSparks(Transform car)
        {
            var root = new GameObject("DriftSparks");
            root.layer = PartVisualFactory.VizLayer;
            root.transform.SetParent(car, false);

            for (int i = -1; i <= 1; i += 2)
                Piece(PrimitiveType.Sphere, root.transform, BurstSkin,
                    new Vector3(i * 0.10f, -0.03f, -0.14f), Vector3.zero,
                    new Vector3(0.09f, 0.05f, 0.14f));
            return root.transform;
        }

        /// <summary>Body of the boost flame — a saturated exhaust orange. Alpha
        /// matters even on the additive skin: it is the source-blend factor, so
        /// it sets how hard the flame adds over the track.</summary>
        private static readonly Color BoostFlameOuter = new Color(1.00f, 0.55f, 0.15f, 0.85f);
        /// <summary>Hot near-white core of the boost flame.</summary>
        private static readonly Color BoostFlameCore = new Color(1.00f, 0.92f, 0.62f, 0.95f);

        /// <summary>
        /// Rear thruster flame shown while any boost is burning — item, pad or
        /// mini-turbo. New in protocol 6's pass: boost previously had no visual
        /// at all, so a remote car's mini-turbo had nothing to light up.
        ///
        /// Additive <see cref="BurstSkin"/> (a flame genuinely is added light),
        /// tinted once at build time through property blocks — the geometry is
        /// never repainted, the director only pulses the root's length. VizLayer
        /// like every cosmetic: a car's own CameraSensor must not see its own
        /// exhaust.
        /// </summary>
        public static Transform BuildBoostFlame(Transform car)
        {
            var root = new GameObject("BoostFlame");
            root.layer = PartVisualFactory.VizLayer;
            root.transform.SetParent(car, false);

            // The car body is ~0.42 m long; the rear face sits near z = -0.21,
            // so the plume hangs just behind it.
            TintPiece(Piece(PrimitiveType.Sphere, root.transform, BurstSkin,
                new Vector3(0f, 0.02f, -0.26f), Vector3.zero,
                new Vector3(0.10f, 0.08f, 0.26f)), BoostFlameOuter, 2f);
            TintPiece(Piece(PrimitiveType.Sphere, root.transform, BurstSkin,
                new Vector3(0f, 0.02f, -0.22f), Vector3.zero,
                new Vector3(0.055f, 0.045f, 0.15f)), BoostFlameCore, 3f);
            for (int i = -1; i <= 1; i += 2)
                TintPiece(Piece(PrimitiveType.Sphere, root.transform, BurstSkin,
                    new Vector3(i * 0.06f, 0f, -0.20f), Vector3.zero,
                    new Vector3(0.04f, 0.035f, 0.10f)), BoostFlameOuter, 2f);
            return root.transform;
        }

        /// <summary>Per-piece colour on a shared material, the shield-bubble way.</summary>
        private static void TintPiece(Transform piece, Color c, float emission)
        {
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", c);
            mpb.SetColor("_EmissionColor", c * emission);
            piece.GetComponent<Renderer>().SetPropertyBlock(mpb);
        }

        /// <summary>One of the three orbs that make up an active shield.</summary>
        public static void BuildShieldOrb(Transform parent)
        {
            if (TryMesh("arc_shield_orb", parent, ShieldSkin, ("orb", ShieldSkin), ("gem", ShieldSkin)) != null)
                return;

            Piece(PrimitiveType.Sphere, parent, ShieldSkin, Vector3.zero, Vector3.zero,
                new Vector3(0.05f, 0.05f, 0.05f));
        }

        /// <summary>
        /// The full shield rig: a translucent bubble enclosing the car plus three
        /// orbs circling it. Returns the spinner the caller rotates.
        ///
        /// Parented to the car and built on VizLayer, which is what keeps a
        /// shielded car's own CameraSensor from being blinded by its own effect —
        /// that camera culls layer 2 for exactly this reason.
        /// </summary>
        public static Transform BuildShield(Transform car)
        {
            var root = new GameObject("ShieldViz");
            root.layer = PartVisualFactory.VizLayer;
            root.transform.SetParent(car, false);
            root.transform.localPosition = Vector3.zero;

            // Bubble: wide enough to clear a 0.42 m car with its wheels.
            var bubble = Piece(PrimitiveType.Sphere, root.transform, BurstSkin,
                Vector3.zero, Vector3.zero, new Vector3(0.62f, 0.42f, 0.78f));
            bubble.name = "Bubble";
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_Color", new Color(0.35f, 0.80f, 1.0f, 0.30f));
            mpb.SetColor("_EmissionColor", new Color(0.20f, 0.55f, 0.90f, 1f));
            bubble.GetComponent<Renderer>().SetPropertyBlock(mpb);

            var spin = new GameObject("Orbs");
            spin.layer = PartVisualFactory.VizLayer;
            spin.transform.SetParent(root.transform, false);
            for (int i = 0; i < 3; i++)
            {
                var slot = new GameObject("OrbSlot");
                slot.layer = PartVisualFactory.VizLayer;
                slot.transform.SetParent(spin.transform, false);
                slot.transform.localPosition =
                    Quaternion.Euler(0f, i * 120f, 0f) * new Vector3(0f, 0.06f, 0.34f);
                BuildShieldOrb(slot.transform);
            }
            return root.transform;
        }
    }
}
