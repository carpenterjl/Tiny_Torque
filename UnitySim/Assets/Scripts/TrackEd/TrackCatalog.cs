using System;
using UnityEngine;
using AIHWSim.Track;
using AIHWSim.Vehicles;

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
        public Color emission = Color.black;  // themed glow (neon grid, lava); black = none
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
                    if (emission.maxColorComponent > 0f)
                    {
                        _mat.EnableKeyword("_EMISSION");
                        _mat.SetColor("_EmissionColor", emission);
                        if (_tex != null) _mat.SetTexture("_EmissionMap", _tex);
                    }
                    _mat.name = $"Floor_{id}";
                }
                return _mat;
            }
        }

        /// <summary>The generated texture (palette icons draw it directly).</summary>
        public Texture2D Tex { get { var _ = Mat; return _tex; } }
    }

    /// <summary>Palette grouping. APPEND-ONLY is not required here (the value is
    /// never persisted — <see cref="PlacedItem.itemId"/> is), but the tab table in
    /// TrackBuilderUI maps one tab to one category, so adding a value means adding
    /// a tab.</summary>
    public enum ItemCategory { Wall, Obstacle, Misc, Arcade, Scenery }

    /// <summary>Runtime behaviour attached by TrackFactory. APPEND-ONLY: the
    /// ordinal is not persisted, but the id string that selects a def is, and an
    /// old build loading a new track skips unknown ids quietly.</summary>
    public enum ItemBehavior { None, Finish, Checkpoint, Light, Spawn, ItemBox }
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
        public string theme = "";        // palette grouping header ("Toy Workshop", ...); "" = ungrouped
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

            // ---- themed surfaces (iteration 24) ----
            // frictionMult doubles as the arcade track-limit classification:
            // ArcadeConfig.OffTrackFrictionThreshold is 0.90, so carpet, wet sand
            // and lava scree read as off-track for free, with no new field and no
            // extra per-tile authoring. Everything at/above 0.90 is racing surface.
            new FloorTypeDef { id = "wood",    label = "Workbench", frictionMult = 1.10f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.55f, 0.38f, 0.22f), new Color(0.46f, 0.31f, 0.17f), 64, 0.28f) },
            new FloorTypeDef { id = "carpet",  label = "Carpet",    frictionMult = 0.80f, rollingResist = 0.012f, roughAmp = 0.035f, roughLen = 0.10f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.48f, 0.20f, 0.22f), new Color(0.38f, 0.15f, 0.18f), 64, 0.55f) },
            new FloorTypeDef { id = "neon",    label = "Neon grid", frictionMult = 1.15f,
                emission = new Color(0.06f, 0.34f, 0.46f),
                makeTexture = () => TrackBuilder.CheckerTexture(8, 8) },
            new FloorTypeDef { id = "plank",   label = "Boardwalk", frictionMult = 1.05f, bumpAmp = 0.02f,
                makeTexture = () => TrackBuilder.StripeTexture(new Color(0.62f, 0.46f, 0.29f), new Color(0.54f, 0.39f, 0.24f), 6, 10) },
            new FloorTypeDef { id = "wetsand", label = "Wet sand",  frictionMult = 0.45f, rollingResist = 0.022f, roughAmp = 0.03f, roughLen = 0.22f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.55f, 0.49f, 0.38f), new Color(0.46f, 0.41f, 0.32f), 64, 0.4f) },
            new FloorTypeDef { id = "lavarock", label = "Lava rock", frictionMult = 0.85f, rollingResist = 0.015f, roughAmp = 0.07f, roughLen = 0.14f,
                emission = new Color(0.22f, 0.05f, 0.01f),
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.16f, 0.13f, 0.13f), new Color(0.34f, 0.11f, 0.04f), 64, 0.7f) },
            new FloorTypeDef { id = "obsidian", label = "Obsidian", frictionMult = 1.20f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.12f, 0.11f, 0.14f), new Color(0.08f, 0.07f, 0.10f), 64, 0.3f) },
            new FloorTypeDef { id = "metalgrate", label = "Grate",  frictionMult = 1.10f, bumpAmp = 0.03f,
                makeTexture = () => TrackBuilder.CheckerTexture(12, 6) },
        };

        // ---- local-space primitive helpers (parent assumed at origin) ----
        private static GameObject LBox(string name, Transform parent, Material mat,
            Vector3 pos, Vector3 euler, Vector3 size)
            => TrackBuilder.Box(name, pos, size, Quaternion.Euler(euler), mat, parent);

        private static GameObject LCyl(string name, Transform parent, Material mat,
            Vector3 pos, Vector3 euler, Vector3 scale)
            => TrackBuilder.Cylinder(name, pos, scale, Quaternion.Euler(euler), mat, parent);

        // ---- mesh-backed props ----------------------------------------------
        // Imported meshes arrive stripped of colliders (PartMeshLibrary.Sanitise),
        // which is right for vehicle parts but leaves a track prop non-solid. So a
        // mesh prop is always a pair: the authored visual shell plus an invisible
        // primitive collision hull authored here. Hulls are deliberately coarse —
        // they are what the car, the ToF sensors and the builder's selection
        // raycast actually hit, and a convex box/capsule beats a 3k-tri mesh
        // collider for all three.

        /// <summary>Invisible collision box (collider only, no renderer).</summary>
        private static GameObject HullBox(Transform parent, Vector3 pos, Vector3 size, Vector3 euler = default)
            => Hull(PrimitiveType.Cube, parent, pos, size, euler);

        /// <summary>Invisible collision cylinder (collider only, no renderer).</summary>
        private static GameObject HullCyl(Transform parent, Vector3 pos, Vector3 scale, Vector3 euler = default)
            => Hull(PrimitiveType.Cylinder, parent, pos, scale, euler);

        private static GameObject Hull(PrimitiveType type, Transform parent,
            Vector3 pos, Vector3 scale, Vector3 euler)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "Hull";
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) UnityEngine.Object.Destroy(mr);
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null) UnityEngine.Object.Destroy(mf);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            return go;
        }

        /// <summary>
        /// Build a prop from an authored FBX under <c>Resources/TrackProps/</c>,
        /// falling back to runtime primitives when the asset is absent — the same
        /// mesh-then-primitive idiom the vehicle parts use, so the game runs
        /// unchanged before any prop ships. The mesh stays on the parent's layer
        /// (NOT the viz layer) so the on-car camera sensor can see the scenery.
        /// </summary>
        private static void MeshProp(Transform p, string key, Material fallback,
            (string token, Material mat)[] tokens,
            Action<Transform> hull, Action<Transform> primitives)
        {
            var mesh = PartMeshLibrary.TryInstantiate(key, p, p.gameObject.layer, PartMeshLibrary.PropRoot);
            if (mesh == null)
            {
                // Primitives carry their own colliders (TrackBuilder default), so
                // the authored hull is skipped on this path.
                primitives(p);
                return;
            }
            if (tokens != null && tokens.Length > 0) PartMeshLibrary.AssignByName(mesh, fallback, tokens);
            hull?.Invoke(p);
        }

        /// <summary>
        /// A mesh prop that gets knocked around. TrackFactory puts a Rigidbody on
        /// every collidered GameObject under a dynamic item, so the visual has to
        /// live UNDER the collider — as a sibling it would sit still while the
        /// body rolled away. The collider therefore goes on a unit-scale child and
        /// is sized through the Collider component, never through the transform:
        /// scaling the hull transform would squash the mesh parented to it.
        /// </summary>
        private static void MeshPropDynamic(Transform p, string key, Material fallback,
            (string token, Material mat)[] tokens,
            Action<GameObject> addCollider, Action<Transform> primitives)
        {
            var body = new GameObject("Body") { layer = p.gameObject.layer };
            body.transform.SetParent(p, false);

            var mesh = PartMeshLibrary.TryInstantiate(key, body.transform,
                p.gameObject.layer, PartMeshLibrary.PropRoot);
            if (mesh == null)
            {
                UnityEngine.Object.Destroy(body);
                primitives(p);
                return;
            }
            if (tokens != null && tokens.Length > 0) PartMeshLibrary.AssignByName(mesh, fallback, tokens);
            addCollider(body);
        }

        // ---- themed prop materials -------------------------------------------
        // One shared material per key so a 60-prop map still batches. Keyed
        // lookup rather than a field per colour: the four theme families need
        // ~30 of these and a wall of `private static Material` would bury the
        // catalog it sits in.
        private static readonly System.Collections.Generic.Dictionary<string, Material> _themeMats
            = new System.Collections.Generic.Dictionary<string, Material>();

        private static Material T(string key, float r, float g, float b,
            float smooth = 0.35f, float glow = 0f)
        {
            if (!_themeMats.TryGetValue(key, out var m) || m == null)
            {
                m = TrackBuilder.StandardMat(new Color(r, g, b));
                m.SetFloat("_Glossiness", smooth);
                if (glow > 0f)
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", new Color(r * glow, g * glow, b * glow));
                }
                m.name = "Prop_" + key;
                _themeMats[key] = m;
            }
            return m;
        }

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

            // Arcade ------------------------------------------------------
            // Authoring boxes on a map suppresses ArcadeDirector's automatic
            // placement entirely — the director only lays its own rows when it
            // finds none, so a hand-placed set is authoritative.
            new ItemDef { id = "item_box", label = "Item box", category = ItemCategory.Arcade,
                behavior = ItemBehavior.ItemBox,
                build = p =>
                {
                    // "Box" hovers; "Viz" spins and bobs inside it. Two levels,
                    // because ArcadeItemBox writes viz.localPosition every frame
                    // and would otherwise cancel the hover height.
                    var box = new GameObject("Box") { layer = p.gameObject.layer };
                    box.transform.SetParent(p, false);
                    box.transform.localPosition = new Vector3(0f, Arcade.ArcadeConfig.AutoBoxHeight, 0f);
                    // The trigger is built here, not by TrackFactory, so it also
                    // exists in the builder — without a collider the box could be
                    // placed and then never selected, moved or deleted again.
                    // Being a trigger, it is still non-solid on track.
                    var trig = box.AddComponent<BoxCollider>();
                    trig.isTrigger = true;
                    trig.size = new Vector3(0.34f, 0.34f, 0.34f);   // forgiving to drive through
                    var viz = new GameObject("Viz") { layer = p.gameObject.layer };
                    viz.transform.SetParent(box.transform, false);
                    Arcade.ArcadeVfx.BuildItemBox(viz.transform);
                } },

            // Scenery — Toy Workshop --------------------------------------
            new ItemDef { id = "tw_book_stack", label = "Book stack", category = ItemCategory.Scenery,
                theme = ToyWorkshop, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "tw_book_stack", TwCover,
                    new[] { ("cover", TwCover), ("pages", TwPages) },
                    h => HullBox(h, new Vector3(0, 0.064f, 0), new Vector3(0.268f, 0.128f, 0.209f)),
                    f => LBox("Books", f, TwCover, new Vector3(0, 0.064f, 0), Vector3.zero,
                             new Vector3(0.268f, 0.128f, 0.209f))) },

            new ItemDef { id = "tw_ruler_ramp", label = "Ruler ramp", category = ItemCategory.Scenery,
                theme = ToyWorkshop,
                build = p => MeshProp(p, "tw_ruler_ramp", TwWood,
                    new[] { ("wood", TwWood), ("ruler", TwSteel), ("tick", TwGraphite), ("rail", TwSteel) },
                    // A thin slab rotated to the slope: the car drives its top
                    // face, which is the only surface that has to be right.
                    h => HullBox(h, new Vector3(0, 0.029f, 0), new Vector3(0.245f, 0.030f, 0.325f),
                                 new Vector3(-14f, 0, 0)),
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 0.312f, 0.245f, 0.03f, 14f, TwWood, f)) },

            new ItemDef { id = "tw_brick_wall", label = "Toy brick", category = ItemCategory.Scenery,
                theme = ToyWorkshop, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "tw_brick_wall", TwBrick,
                    new[] { ("brick", TwBrick), ("stud", TwBrick), ("plate", TwPlate) },
                    h => HullBox(h, new Vector3(0, 0.054f, 0), new Vector3(0.344f, 0.108f, 0.184f)),
                    f => LBox("Brick", f, TwBrick, new Vector3(0, 0.054f, 0), Vector3.zero,
                             new Vector3(0.344f, 0.108f, 0.184f))) },

            new ItemDef { id = "tw_pencil", label = "Pencil", category = ItemCategory.Scenery,
                theme = ToyWorkshop, dynamic = true, dynamicMass = 0.05f,
                build = p => MeshPropDynamic(p, "tw_pencil", TwPencil,
                    new[] { ("barrel", TwPencil), ("wood", TwWood), ("lead", TwGraphite),
                            ("ferrule", TwSteel), ("eraser", TwEraser) },
                    b =>
                    {
                        // Capsule down X: a pencil knocked sideways should roll
                        // across the track, which is the whole point of it.
                        var c = b.AddComponent<CapsuleCollider>();
                        c.direction = 0;
                        c.center = new Vector3(0f, 0.0095f, 0f);
                        c.radius = 0.0095f;
                        c.height = 0.211f;
                    },
                    f => LCyl("Pencil", f, TwPencil, new Vector3(0, 0.0095f, 0),
                              new Vector3(0, 0, 90f), new Vector3(0.019f, 0.105f, 0.019f))) },

            new ItemDef { id = "tw_mug", label = "Mug", category = ItemCategory.Scenery,
                theme = ToyWorkshop,
                build = p => MeshProp(p, "tw_mug", TwCeramic,
                    new[] { ("mug", TwCeramic), ("coffee", TwCoffee) },
                    h => HullCyl(h, new Vector3(0, 0.05f, 0), new Vector3(0.090f, 0.050f, 0.090f)),
                    f => LCyl("Mug", f, TwCeramic, new Vector3(0, 0.05f, 0), Vector3.zero,
                              new Vector3(0.090f, 0.050f, 0.090f))) },

            new ItemDef { id = "tw_tape_arch", label = "Tape arch", category = ItemCategory.Scenery,
                theme = ToyWorkshop,
                build = p => MeshProp(p, "tw_tape_arch", TwTape,
                    new[] { ("tape", TwTape), ("core", TwCore) },
                    h =>
                    {
                        // Three hulls, not one box: the bore is the gate.
                        HullBox(h, new Vector3(-0.225f, 0.170f, 0), new Vector3(0.11f, 0.34f, 0.10f));
                        HullBox(h, new Vector3(0.225f, 0.170f, 0), new Vector3(0.11f, 0.34f, 0.10f));
                        HullBox(h, new Vector3(0, 0.395f, 0), new Vector3(0.34f, 0.11f, 0.10f));
                    },
                    f =>
                    {
                        LBox("SideL", f, TwTape, new Vector3(-0.225f, 0.17f, 0), Vector3.zero, new Vector3(0.11f, 0.34f, 0.09f));
                        LBox("SideR", f, TwTape, new Vector3(0.225f, 0.17f, 0), Vector3.zero, new Vector3(0.11f, 0.34f, 0.09f));
                        LBox("Top", f, TwTape, new Vector3(0, 0.395f, 0), Vector3.zero, new Vector3(0.56f, 0.11f, 0.09f));
                    }) },

            // Scenery — Neon Grid -----------------------------------------
            new ItemDef { id = "ng_pylon", label = "Pylon", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_pylon", NgFrame,
                    new[] { ("pylon", NgFrame), ("glow", NgGlow), ("base", NgPanel) },
                    h => HullCyl(h, new Vector3(0, 0.16f, 0), new Vector3(0.130f, 0.160f, 0.130f)),
                    f => LCyl("Pylon", f, NgFrame, new Vector3(0, 0.16f, 0), Vector3.zero,
                              new Vector3(0.130f, 0.160f, 0.130f))) },

            new ItemDef { id = "ng_arch_gate", label = "Light gate", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_arch_gate", NgFrame,
                    new[] { ("frame", NgFrame), ("glow", NgGlow), ("panel", NgPanel), ("base", NgPanel) },
                    h =>
                    {
                        HullBox(h, new Vector3(-0.390f, 0.250f, 0), new Vector3(0.13f, 0.50f, 0.12f));
                        HullBox(h, new Vector3(0.390f, 0.250f, 0), new Vector3(0.13f, 0.50f, 0.12f));
                        HullBox(h, new Vector3(0, 0.560f, 0), new Vector3(0.91f, 0.15f, 0.12f));
                    },
                    f =>
                    {
                        LBox("LegL", f, NgFrame, new Vector3(-0.39f, 0.25f, 0), Vector3.zero, new Vector3(0.08f, 0.50f, 0.07f));
                        LBox("LegR", f, NgFrame, new Vector3(0.39f, 0.25f, 0), Vector3.zero, new Vector3(0.08f, 0.50f, 0.07f));
                        LBox("Beam", f, NgGlow, new Vector3(0, 0.56f, 0), Vector3.zero, new Vector3(0.91f, 0.075f, 0.07f));
                    }) },

            new ItemDef { id = "ng_ring_float", label = "Light hoop", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_ring_float", NgFrame,
                    new[] { ("ring", NgFrame), ("glow", NgGlow), ("foot", NgPanel) },
                    h =>
                    {
                        HullBox(h, new Vector3(-0.222f, 0.200f, 0), new Vector3(0.05f, 0.40f, 0.10f));
                        HullBox(h, new Vector3(0.222f, 0.200f, 0), new Vector3(0.05f, 0.40f, 0.10f));
                        HullBox(h, new Vector3(0, 0.425f, 0), new Vector3(0.40f, 0.05f, 0.10f));
                    },
                    f =>
                    {
                        LBox("SideL", f, NgGlow, new Vector3(-0.222f, 0.20f, 0), Vector3.zero, new Vector3(0.05f, 0.40f, 0.05f));
                        LBox("SideR", f, NgGlow, new Vector3(0.222f, 0.20f, 0), Vector3.zero, new Vector3(0.05f, 0.40f, 0.05f));
                        LBox("Top", f, NgGlow, new Vector3(0, 0.425f, 0), Vector3.zero, new Vector3(0.44f, 0.05f, 0.05f));
                    }) },

            new ItemDef { id = "ng_barrier_glow", label = "Glow barrier", category = ItemCategory.Scenery,
                theme = NeonGrid, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "ng_barrier_glow", NgFrame,
                    new[] { ("barrier", NgFrame), ("glow", NgGlow), ("cap", NgPanel), ("foot", NgPanel) },
                    h => HullBox(h, new Vector3(0, 0.070f, 0), new Vector3(0.530f, 0.140f, 0.090f)),
                    f => LBox("Barrier", f, NgFrame, new Vector3(0, 0.07f, 0), Vector3.zero,
                             new Vector3(0.530f, 0.140f, 0.090f))) },

            new ItemDef { id = "ng_data_cube", label = "Data stack", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_data_cube", NgPanel,
                    new[] { ("cube", NgPanel), ("glow", NgGlow), ("base", NgFrame) },
                    h => HullBox(h, new Vector3(0, 0.099f, 0), new Vector3(0.185f, 0.198f, 0.185f)),
                    f => LBox("Stack", f, NgPanel, new Vector3(0, 0.099f, 0), Vector3.zero,
                             new Vector3(0.185f, 0.198f, 0.185f))) },

            new ItemDef { id = "ng_spire", label = "Spire", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_spire", NgFrame,
                    new[] { ("spire", NgFrame), ("glow", NgGlow), ("base", NgPanel) },
                    h => HullCyl(h, new Vector3(0, 0.300f, 0), new Vector3(0.160f, 0.300f, 0.160f)),
                    f => LCyl("Spire", f, NgFrame, new Vector3(0, 0.30f, 0), Vector3.zero,
                              new Vector3(0.160f, 0.300f, 0.160f))) },

            // Scenery — Beach Boardwalk -----------------------------------
            new ItemDef { id = "bb_palm", label = "Palm", category = ItemCategory.Scenery,
                theme = BeachBoardwalk,
                build = p => MeshProp(p, "bb_palm", BbTrunk,
                    new[] { ("trunk", BbTrunk), ("crown", BbTrunk), ("frond", BbFrond), ("coconut", BbCoconut) },
                    // Trunk only — fronds are 0.5 m up and nothing can reach them.
                    h => HullCyl(h, new Vector3(0, 0.280f, 0), new Vector3(0.080f, 0.280f, 0.080f)),
                    f =>
                    {
                        LCyl("Trunk", f, BbTrunk, new Vector3(0, 0.28f, 0), Vector3.zero, new Vector3(0.06f, 0.28f, 0.06f));
                        LCyl("Crown", f, BbFrond, new Vector3(0, 0.58f, 0), Vector3.zero, new Vector3(0.42f, 0.02f, 0.42f));
                    }) },

            new ItemDef { id = "bb_surfboard_ramp", label = "Board ramp", category = ItemCategory.Scenery,
                theme = BeachBoardwalk,
                build = p => MeshProp(p, "bb_surfboard_ramp", BbSand,
                    new[] { ("sand", BbSand), ("board", BbBoard), ("stripe", BbStripe), ("fin", BbStripe) },
                    h => HullBox(h, new Vector3(0, 0.045f, 0), new Vector3(0.255f, 0.030f, 0.440f),
                                 new Vector3(-12.75f, 0, 0)),
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 0.42f, 0.255f, 0.03f, 12.75f, BbSand, f)) },

            new ItemDef { id = "bb_plank_wall", label = "Boardwalk rail", category = ItemCategory.Scenery,
                theme = BeachBoardwalk, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "bb_plank_wall", BbPlank,
                    new[] { ("post", BbPlank), ("cap", BbPlank), ("rail", BbBoard), ("plank", BbPlank) },
                    h => HullBox(h, new Vector3(0, 0.089f, 0), new Vector3(0.620f, 0.178f, 0.100f)),
                    f => LBox("Rail", f, BbPlank, new Vector3(0, 0.089f, 0), Vector3.zero,
                             new Vector3(0.620f, 0.178f, 0.060f))) },

            new ItemDef { id = "bb_tiki_torch", label = "Tiki torch", category = ItemCategory.Scenery,
                theme = BeachBoardwalk, behavior = ItemBehavior.Light,
                build = p => MeshProp(p, "bb_tiki_torch", BbTrunk,
                    new[] { ("pole", BbTrunk), ("node", BbTrunk), ("bowl", VfRock),
                            ("flame", BbFlame), ("base", VfRock) },
                    h => HullCyl(h, new Vector3(0, 0.220f, 0), new Vector3(0.050f, 0.220f, 0.050f)),
                    f => LCyl("Pole", f, BbTrunk, new Vector3(0, 0.22f, 0), Vector3.zero,
                              new Vector3(0.05f, 0.22f, 0.05f))) },

            new ItemDef { id = "bb_beach_ball", label = "Beach ball", category = ItemCategory.Scenery,
                theme = BeachBoardwalk, dynamic = true, dynamicMass = 0.04f,
                build = p => MeshPropDynamic(p, "bb_beach_ball", BbBall,
                    new[] { ("ball", BbBall), ("panel", BbPanelA) },
                    b =>
                    {
                        var c = b.AddComponent<SphereCollider>();
                        c.center = new Vector3(0f, 0.081f, 0f);
                        c.radius = 0.081f;
                    },
                    f => LCyl("Ball", f, BbBall, new Vector3(0, 0.08f, 0), Vector3.zero,
                              new Vector3(0.16f, 0.08f, 0.16f))) },

            new ItemDef { id = "bb_sandcastle", label = "Sandcastle", category = ItemCategory.Scenery,
                theme = BeachBoardwalk,
                build = p => MeshProp(p, "bb_sandcastle", BbSand,
                    new[] { ("sand", BbSand), ("tower", BbSand), ("merlon", BbSand),
                            ("wall", BbSand), ("keep", BbSand), ("flag", BbStripe) },
                    h => HullBox(h, new Vector3(0, 0.130f, 0), new Vector3(0.300f, 0.260f, 0.300f)),
                    f => LBox("Castle", f, BbSand, new Vector3(0, 0.13f, 0), Vector3.zero,
                             new Vector3(0.30f, 0.26f, 0.30f))) },

            // Scenery — Volcano Foundry -----------------------------------
            new ItemDef { id = "vf_rock_arch", label = "Rock arch", category = ItemCategory.Scenery,
                theme = VolcanoFoundry,
                build = p => MeshProp(p, "vf_rock_arch", VfRock,
                    new[] { ("rock", VfRock), ("lava", VfLava) },
                    h =>
                    {
                        HullBox(h, new Vector3(-0.300f, 0.200f, 0), new Vector3(0.21f, 0.40f, 0.21f));
                        HullBox(h, new Vector3(0.300f, 0.200f, 0), new Vector3(0.21f, 0.40f, 0.21f));
                        HullBox(h, new Vector3(0, 0.600f, 0), new Vector3(0.70f, 0.20f, 0.21f));
                    },
                    f =>
                    {
                        LBox("LegL", f, VfRock, new Vector3(-0.30f, 0.20f, 0), Vector3.zero, new Vector3(0.20f, 0.40f, 0.20f));
                        LBox("LegR", f, VfRock, new Vector3(0.30f, 0.20f, 0), Vector3.zero, new Vector3(0.20f, 0.40f, 0.20f));
                        LBox("Span", f, VfRock, new Vector3(0, 0.60f, 0), Vector3.zero, new Vector3(0.80f, 0.20f, 0.20f));
                    }) },

            new ItemDef { id = "vf_obsidian_block", label = "Obsidian", category = ItemCategory.Scenery,
                theme = VolcanoFoundry, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "vf_obsidian_block", VfObsidian,
                    new[] { ("obsidian", VfObsidian), ("shard", VfObsidian), ("glow", VfLava) },
                    h => HullBox(h, new Vector3(0, 0.092f, 0), new Vector3(0.360f, 0.185f, 0.170f)),
                    f => LBox("Block", f, VfObsidian, new Vector3(0, 0.092f, 0), Vector3.zero,
                             new Vector3(0.360f, 0.185f, 0.170f))) },

            new ItemDef { id = "vf_steam_vent", label = "Steam vent", category = ItemCategory.Scenery,
                theme = VolcanoFoundry,
                build = p => MeshProp(p, "vf_steam_vent", VfSteel,
                    new[] { ("vent", VfSteel), ("grate", VfSteel), ("lava", VfLava), ("rock", VfRock) },
                    h => HullCyl(h, new Vector3(0, 0.034f, 0), new Vector3(0.260f, 0.034f, 0.260f)),
                    f => LCyl("Vent", f, VfSteel, new Vector3(0, 0.034f, 0), Vector3.zero,
                              new Vector3(0.260f, 0.034f, 0.260f))) },

            new ItemDef { id = "vf_barrel", label = "Barrel", category = ItemCategory.Scenery,
                theme = VolcanoFoundry, dynamic = true, dynamicMass = 0.12f,
                build = p => MeshPropDynamic(p, "vf_barrel", VfBarrel,
                    new[] { ("barrel", VfBarrel), ("band", VfSteel), ("glow", VfLava) },
                    b =>
                    {
                        // Box, not capsule: a capsule stood on end balances on a
                        // point and topples the instant physics starts.
                        var c = b.AddComponent<BoxCollider>();
                        c.center = new Vector3(0f, 0.105f, 0f);
                        c.size = new Vector3(0.155f, 0.209f, 0.155f);
                    },
                    f => LCyl("Barrel", f, VfBarrel, new Vector3(0, 0.105f, 0), Vector3.zero,
                              new Vector3(0.155f, 0.105f, 0.155f))) },

            new ItemDef { id = "vf_grate_ramp", label = "Grate ramp", category = ItemCategory.Scenery,
                theme = VolcanoFoundry,
                build = p => MeshProp(p, "vf_grate_ramp", VfSteel,
                    new[] { ("ramp", VfSteel), ("slat", VfSteel), ("rail", VfBarrel), ("strut", VfSteel) },
                    h => HullBox(h, new Vector3(0, 0.048f, 0), new Vector3(0.260f, 0.030f, 0.455f),
                                 new Vector3(-12.8f, 0, 0)),
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 0.44f, 0.26f, 0.03f, 12.8f, VfSteel, f)) },

            new ItemDef { id = "vf_crag_spire", label = "Crag spire", category = ItemCategory.Scenery,
                theme = VolcanoFoundry,
                build = p => MeshProp(p, "vf_crag_spire", VfRock,
                    new[] { ("crag", VfRock), ("spike", VfObsidian), ("lava", VfLava) },
                    h => HullCyl(h, new Vector3(0, 0.300f, 0), new Vector3(0.200f, 0.300f, 0.200f)),
                    f => LCyl("Crag", f, VfRock, new Vector3(0, 0.30f, 0), Vector3.zero,
                              new Vector3(0.200f, 0.300f, 0.200f))) },
        };

        // ---- theme names (palette group headers; also ItemDef.theme values) ----
        public const string ToyWorkshop    = "Toy Workshop";
        public const string NeonGrid       = "Neon Grid";
        public const string BeachBoardwalk = "Beach Boardwalk";
        public const string VolcanoFoundry = "Volcano Foundry";

        /// <summary>Theme headers in palette order.</summary>
        public static readonly string[] Themes =
            { ToyWorkshop, NeonGrid, BeachBoardwalk, VolcanoFoundry };

        // ---- theme prop materials ----
        private static Material TwCover    => T("tw_cover", 0.58f, 0.16f, 0.14f);
        private static Material TwPages    => T("tw_pages", 0.93f, 0.90f, 0.80f, 0.15f);
        private static Material TwWood     => T("tw_wood", 0.74f, 0.56f, 0.33f);
        private static Material TwSteel    => T("tw_steel", 0.72f, 0.74f, 0.78f, 0.75f);
        private static Material TwBrick    => T("tw_brick", 0.86f, 0.21f, 0.17f, 0.55f);
        private static Material TwPlate    => T("tw_plate", 0.20f, 0.45f, 0.80f, 0.55f);
        private static Material TwCeramic  => T("tw_ceramic", 0.93f, 0.93f, 0.95f, 0.70f);
        private static Material TwCoffee   => T("tw_coffee", 0.20f, 0.11f, 0.06f, 0.60f);
        private static Material TwTape     => T("tw_tape", 0.84f, 0.72f, 0.48f, 0.45f);
        private static Material TwCore     => T("tw_core", 0.68f, 0.54f, 0.36f, 0.10f);
        private static Material TwPencil   => T("tw_pencil", 0.96f, 0.76f, 0.12f, 0.45f);
        private static Material TwGraphite => T("tw_graphite", 0.16f, 0.16f, 0.18f, 0.30f);
        private static Material TwEraser   => T("tw_eraser", 0.96f, 0.60f, 0.60f, 0.15f);

        private static Material NgFrame => T("ng_frame", 0.18f, 0.20f, 0.26f, 0.70f);
        private static Material NgGlow  => T("ng_glow", 0.25f, 0.95f, 1.00f, 0.85f, 1.6f);
        private static Material NgPanel => T("ng_panel", 0.10f, 0.12f, 0.17f, 0.55f);

        private static Material BbTrunk   => T("bb_trunk", 0.46f, 0.33f, 0.20f, 0.20f);
        private static Material BbFrond   => T("bb_frond", 0.20f, 0.56f, 0.24f, 0.25f);
        private static Material BbCoconut => T("bb_coconut", 0.34f, 0.23f, 0.14f, 0.25f);
        private static Material BbBoard   => T("bb_board", 0.94f, 0.92f, 0.86f, 0.70f);
        private static Material BbStripe  => T("bb_stripe", 0.95f, 0.34f, 0.22f, 0.60f);
        private static Material BbPlank   => T("bb_plank", 0.64f, 0.50f, 0.34f, 0.20f);
        private static Material BbSand    => T("bb_sand", 0.86f, 0.77f, 0.56f, 0.15f);
        private static Material BbFlame   => T("bb_flame", 1.00f, 0.56f, 0.14f, 0.60f, 1.8f);
        private static Material BbBall    => T("bb_ball", 0.96f, 0.96f, 0.96f, 0.65f);
        private static Material BbPanelA  => T("bb_panel", 0.92f, 0.22f, 0.24f, 0.65f);

        private static Material VfRock     => T("vf_rock", 0.29f, 0.26f, 0.25f, 0.15f);
        private static Material VfObsidian => T("vf_obsidian", 0.09f, 0.08f, 0.11f, 0.85f);
        private static Material VfLava     => T("vf_lava", 1.00f, 0.34f, 0.06f, 0.50f, 2.0f);
        private static Material VfSteel    => T("vf_steel", 0.44f, 0.45f, 0.48f, 0.70f);
        private static Material VfBarrel   => T("vf_barrel", 0.72f, 0.46f, 0.12f, 0.55f);

        /// <summary>Find an item definition by id (null when unknown/legacy).</summary>
        public static ItemDef Item(string id)
        {
            foreach (var it in Items)
                if (it.id == id) return it;
            return null;
        }
    }
}
