using System;
using UnityEngine;
using AIHWSim.Track;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// A drivable floor surface type. The array index in
    /// <see cref="TrackCatalog.Floors"/> is the id persisted in track JSON —
    /// APPEND-ONLY: never reorder or remove entries.
    /// </summary>
    public sealed class FloorTypeDef
    {
        public string id;              // stable key (debug/UI only; index is persisted)
        public string label;
        public float frictionMult = 1f;   // scales wheel friction stiffness (1 = dirt baseline)
        public float rollingResist;       // extra brake torque per wheel (N·m) while on the tile
        public float boostAccel;          // forward acceleration (m/s^2) while on the tile
        public float bumpAmp;             // vertical rumble force amplitude (fraction of weight)
        public float roughAmp;            // surface roughness force amplitude (fraction of per-wheel weight)
        public float roughLen = 0.15f;    // roughness feature wavelength (m)
        public Func<Texture2D> makeTexture;

        private Material _mat;
        private Texture2D _tex;

        /// <summary>Lazily built shared material — exactly ONE per floor type so tiles batch.</summary>
        public Material Mat
        {
            get
            {
                if (_mat == null)
                {
                    _tex = makeTexture != null ? makeTexture() : null;
                    _mat = TrackBuilder.StandardMat(Color.white);
                    if (_tex != null) _mat.mainTexture = _tex;
                    _mat.name = $"Floor_{id}";
                }
                return _mat;
            }
        }

        /// <summary>The generated texture (palette icons draw it directly).</summary>
        public Texture2D Tex { get { var _ = Mat; return _tex; } }
    }

    public enum ItemCategory { Wall, Obstacle, Misc }
    public enum ItemBehavior { None, Finish, Checkpoint, Light, Spawn }
    public enum SnapMode { TileCenter, TileEdge }

    /// <summary>
    /// A placeable track item. <see cref="build"/> creates visuals + colliders
    /// under a parent assumed to be at the world origin with identity rotation
    /// (the factory moves the root afterwards). Behavior components (triggers,
    /// lights, rigidbodies) are attached by TrackFactory so ghosts/icons stay inert.
    /// </summary>
    public sealed class ItemDef
    {
        public string id;
        public string label;
        public ItemCategory category;
        public ItemBehavior behavior = ItemBehavior.None;
        public SnapMode snap = SnapMode.TileCenter;
        public bool dynamic;             // pieces get Rigidbodies when built interactive
        public float dynamicMass = 0.08f; // per collidered piece (kg)
        public bool bottomHeavy;         // weighted base (cones): low center of mass
        public Action<Transform> build;
    }

    /// <summary>
    /// Static part catalog for the track builder: floor surface types and
    /// placeable items, consumed by TrackFactory, the palette icons, and ghosts.
    /// </summary>
    public static class TrackCatalog
    {
        // ---- shared item materials (lazy, rebuilt if lost on play-mode change) ----
        private static Material _concrete, _wood, _tire, _railA, _railB, _coneMat,
            _barrier, _metal, _lamp, _spawnPad, _spawnArrow, _marker;

        private static Material Mat(ref Material slot, Color c, float smooth = 0.35f)
        {
            if (slot == null)
            {
                slot = TrackBuilder.StandardMat(c);
                slot.SetFloat("_Glossiness", smooth);
            }
            return slot;
        }

        private static Material Concrete => Mat(ref _concrete, new Color(0.62f, 0.62f, 0.60f));
        private static Material Wood => Mat(ref _wood, new Color(0.45f, 0.32f, 0.20f));
        private static Material TireBlack => Mat(ref _tire, new Color(0.10f, 0.10f, 0.11f), 0.5f);
        private static Material RailWhite => Mat(ref _railA, new Color(0.92f, 0.92f, 0.92f));
        private static Material RailRed => Mat(ref _railB, new Color(0.85f, 0.20f, 0.15f));
        private static Material ConeOrange => Mat(ref _coneMat, new Color(1.00f, 0.45f, 0.05f));
        private static Material BarrierYellow => Mat(ref _barrier, new Color(0.95f, 0.80f, 0.10f));
        private static Material Metal => Mat(ref _metal, new Color(0.45f, 0.47f, 0.50f), 0.7f);
        private static Material LampHead => Mat(ref _lamp, new Color(1.0f, 0.95f, 0.75f), 0.8f);
        private static Material SpawnPad => Mat(ref _spawnPad, new Color(0.15f, 0.35f, 0.60f));
        private static Material SpawnArrow => Mat(ref _spawnArrow, new Color(0.55f, 0.85f, 1.0f), 0.6f);
        private static Material MarkerBlue => Mat(ref _marker, new Color(0.25f, 0.55f, 1.0f), 0.6f);

        private static Material _checkerMat;
        private static Material CheckerMat
        {
            get
            {
                if (_checkerMat == null)
                {
                    _checkerMat = TrackBuilder.StandardMat(Color.white);
                    _checkerMat.mainTexture = TrackBuilder.CheckerTexture(6, 12);
                }
                return _checkerMat;
            }
        }

        // ---- floor types (index = persisted id; APPEND-ONLY) ----
        public static readonly FloorTypeDef[] Floors =
        {
            new FloorTypeDef { id = "dirt",    label = "Dirt",    frictionMult = 1.00f, roughAmp = 0.03f, roughLen = 0.12f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.42f, 0.30f, 0.18f), new Color(0.34f, 0.24f, 0.14f)) },
            new FloorTypeDef { id = "asphalt", label = "Asphalt", frictionMult = 1.15f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.22f, 0.22f, 0.24f), new Color(0.17f, 0.17f, 0.19f)) },
            // rollingResist is extra brake torque per wheel (N·m), sized for a
            // ~1.8 kg car on 33 mm wheels: decel ≈ resist·wheels/(r·mass).
            new FloorTypeDef { id = "grass",   label = "Grass",   frictionMult = 0.85f, rollingResist = 0.005f, roughAmp = 0.05f, roughLen = 0.18f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.22f, 0.42f, 0.16f), new Color(0.16f, 0.33f, 0.12f)) },
            new FloorTypeDef { id = "sand",    label = "Sand",    frictionMult = 0.60f, rollingResist = 0.018f, roughAmp = 0.04f, roughLen = 0.25f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.82f, 0.72f, 0.48f), new Color(0.74f, 0.64f, 0.42f)) },
            new FloorTypeDef { id = "ice",     label = "Ice",     frictionMult = 0.30f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.72f, 0.84f, 0.94f), new Color(0.80f, 0.90f, 0.98f), 64, 0.5f) },
            new FloorTypeDef { id = "mud",     label = "Mud",     frictionMult = 0.55f, rollingResist = 0.045f, roughAmp = 0.06f, roughLen = 0.20f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.26f, 0.19f, 0.12f), new Color(0.20f, 0.14f, 0.09f)) },
            new FloorTypeDef { id = "rumble",  label = "Rumble strip", frictionMult = 1.05f, bumpAmp = 0.06f,
                makeTexture = () => TrackBuilder.StripeTexture(new Color(0.85f, 0.15f, 0.12f), new Color(0.92f, 0.92f, 0.92f)) },
            new FloorTypeDef { id = "boost",   label = "Boost pad", frictionMult = 1.00f, boostAccel = 9f,
                makeTexture = () => TrackBuilder.ChevronTexture(new Color(0.10f, 0.12f, 0.18f), new Color(0.20f, 0.85f, 1.0f)) },
            new FloorTypeDef { id = "checker", label = "Checker", frictionMult = 1.15f,
                makeTexture = () => TrackBuilder.CheckerTexture(4, 16) },
        };

        // ---- local-space primitive helpers (parent assumed at origin) ----
        private static GameObject LBox(string name, Transform parent, Material mat,
            Vector3 pos, Vector3 euler, Vector3 size)
            => TrackBuilder.Box(name, pos, size, Quaternion.Euler(euler), mat, parent);

        private static GameObject LCyl(string name, Transform parent, Material mat,
            Vector3 pos, Vector3 euler, Vector3 scale)
            => TrackBuilder.Cylinder(name, pos, scale, Quaternion.Euler(euler), mat, parent);

        // ---- placeable items (dimensions sized for the 1/10 RC world; tiles are 1 m) ----
        public static readonly ItemDef[] Items =
        {
            // Walls -------------------------------------------------------
            new ItemDef { id = "wall_small", label = "Block", category = ItemCategory.Wall,
                build = p => LBox("Block", p, Concrete, new Vector3(0, 0.1f, 0), Vector3.zero, new Vector3(0.2f, 0.2f, 0.2f)) },

            new ItemDef { id = "tire_stack", label = "Tire stack", category = ItemCategory.Wall, dynamic = true,
                build = p =>
                {
                    // Real tori: 0.105 m outer, 0.07 m tall, visible center hole.
                    // Each tire is its own collidered piece so stacks knock apart.
                    for (int i = 0; i < 3; i++)
                        TrackBuilder.Tire($"Tire{i}",
                            new Vector3(0, 0.035f + i * 0.07f, 0), 0.105f, 0.035f, TireBlack, p);
                } },

            new ItemDef { id = "wall_tall", label = "Tall wall", category = ItemCategory.Wall, snap = SnapMode.TileEdge,
                build = p => LBox("TallWall", p, Concrete, new Vector3(0, 0.25f, 0), Vector3.zero, new Vector3(1f, 0.5f, 0.1f)) },

            new ItemDef { id = "fence", label = "Fence", category = ItemCategory.Wall, snap = SnapMode.TileEdge,
                build = p =>
                {
                    LBox("PostL", p, Wood, new Vector3(-0.475f, 0.14f, 0), Vector3.zero, new Vector3(0.03f, 0.28f, 0.03f));
                    LBox("PostR", p, Wood, new Vector3(0.475f, 0.14f, 0), Vector3.zero, new Vector3(0.03f, 0.28f, 0.03f));
                    LBox("RailTop", p, RailWhite, new Vector3(0, 0.24f, 0), Vector3.zero, new Vector3(1f, 0.025f, 0.0125f));
                    LBox("RailMid", p, RailRed, new Vector3(0, 0.14f, 0), Vector3.zero, new Vector3(1f, 0.025f, 0.0125f));
                } },

            // Obstacles ---------------------------------------------------
            new ItemDef { id = "ramp", label = "Ramp", category = ItemCategory.Obstacle,
                build = p => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1f, 0.75f, 0.04f, 16f, Concrete, p) },

            new ItemDef { id = "speed_bump", label = "Speed bump", category = ItemCategory.Obstacle,
                build = p => LCyl("Bump", p, BarrierYellow,
                    new Vector3(0, 0.025f, 0), new Vector3(0, 0, 90f),
                    new Vector3(0.14f, 0.5f, 0.14f)) }, // axis along X, spans 1 m, ~0.045 m proud

            new ItemDef { id = "platform", label = "Platform", category = ItemCategory.Obstacle,
                build = p => LBox("Platform", p, Concrete, new Vector3(0, 0.0625f, 0), Vector3.zero, new Vector3(1f, 0.125f, 1f)) },

            new ItemDef { id = "cone", label = "Cone", category = ItemCategory.Obstacle, dynamic = true,
                dynamicMass = 0.03f, bottomHeavy = true, // light, weighted base: flies when hit, settles fast
                build = p => TrackBuilder.Cone("Cone", Vector3.zero, 0.18f, 0.07f, ConeOrange, p) },

            new ItemDef { id = "barrier", label = "Barrier", category = ItemCategory.Obstacle,
                build = p => LBox("Barrier", p, BarrierYellow, new Vector3(0, 0.125f, 0), Vector3.zero, new Vector3(0.6f, 0.25f, 0.125f)) },

            // Misc --------------------------------------------------------
            new ItemDef { id = "finish", label = "Start/Finish", category = ItemCategory.Misc,
                behavior = ItemBehavior.Finish,
                build = p =>
                {
                    LBox("FinishStrip", p, CheckerMat, new Vector3(0, 0.008f, 0), Vector3.zero, new Vector3(1.5f, 0.016f, 0.3f));
                    LCyl("PostL", p, Metal, new Vector3(-0.8f, 0.375f, 0), Vector3.zero, new Vector3(0.04f, 0.375f, 0.04f));
                    LCyl("PostR", p, Metal, new Vector3(0.8f, 0.375f, 0), Vector3.zero, new Vector3(0.04f, 0.375f, 0.04f));
                    LBox("Banner", p, CheckerMat, new Vector3(0, 0.76f, 0), Vector3.zero, new Vector3(1.7f, 0.125f, 0.02f));
                } },

            new ItemDef { id = "checkpoint", label = "Checkpoint", category = ItemCategory.Misc,
                behavior = ItemBehavior.Checkpoint,
                build = p =>
                {
                    LCyl("PostL", p, MarkerBlue, new Vector3(-0.65f, 0.28f, 0), Vector3.zero, new Vector3(0.03f, 0.28f, 0.03f));
                    LCyl("PostR", p, MarkerBlue, new Vector3(0.65f, 0.28f, 0), Vector3.zero, new Vector3(0.03f, 0.28f, 0.03f));
                    LBox("TopBar", p, MarkerBlue, new Vector3(0, 0.6f, 0), Vector3.zero, new Vector3(1.35f, 0.035f, 0.035f));
                } },

            new ItemDef { id = "light_post", label = "Light post", category = ItemCategory.Misc,
                behavior = ItemBehavior.Light,
                build = p =>
                {
                    LCyl("Pole", p, Metal, new Vector3(0, 0.45f, 0), Vector3.zero, new Vector3(0.03f, 0.45f, 0.03f));
                    LBox("Arm", p, Metal, new Vector3(0, 0.86f, 0.125f), Vector3.zero, new Vector3(0.025f, 0.025f, 0.3f));
                    LBox("Head", p, LampHead, new Vector3(0, 0.845f, 0.25f), Vector3.zero, new Vector3(0.09f, 0.03f, 0.14f));
                } },

            new ItemDef { id = "spawn", label = "Spawn point", category = ItemCategory.Misc,
                behavior = ItemBehavior.Spawn,
                build = p =>
                {
                    LBox("Pad", p, SpawnPad, new Vector3(0, 0.005f, 0), Vector3.zero, new Vector3(0.6f, 0.01f, 0.6f));
                    // Arrow pointing +Z (the spawn heading).
                    LBox("ArrowShaft", p, SpawnArrow, new Vector3(0, 0.0125f, -0.0375f), Vector3.zero, new Vector3(0.0875f, 0.0075f, 0.275f));
                    LBox("ArrowHeadL", p, SpawnArrow, new Vector3(-0.07f, 0.0125f, 0.13f), new Vector3(0, 45f, 0), new Vector3(0.075f, 0.0075f, 0.2125f));
                    LBox("ArrowHeadR", p, SpawnArrow, new Vector3(0.07f, 0.0125f, 0.13f), new Vector3(0, -45f, 0), new Vector3(0.075f, 0.0075f, 0.2125f));
                } },
        };

        /// <summary>Find an item definition by id (null when unknown/legacy).</summary>
        public static ItemDef Item(string id)
        {
            foreach (var it in Items)
                if (it.id == id) return it;
            return null;
        }
    }
}
