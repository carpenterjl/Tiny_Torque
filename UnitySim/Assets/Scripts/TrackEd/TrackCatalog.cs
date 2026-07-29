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
        // Where ItemBehavior.Light hangs its point light. The default is the
        // classic light_post head; taller lamp props override to their head.
        public Vector3 lightPos = new Vector3(0f, 0.8f, 0.25f);
        public bool animated;            // carries a cosmetic mover: never static-batched
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

            // ---- Torque Falls ----------------------------------------------
            // A footway. The town's streets are asphalt with a pale paved verge
            // on each side, and until this existed there was no light grey in
            // the table at all — the nearest surfaces were sand and ice, which
            // are the two lowest-grip entries in the catalog and would have
            // turned every kerb into a patch you slide off.
            new FloorTypeDef { id = "paving",  label = "Paving",  frictionMult = 1.12f, roughAmp = 0.015f, roughLen = 0.09f,
                makeTexture = () => TrackBuilder.NoiseTexture(new Color(0.52f, 0.52f, 0.51f), new Color(0.45f, 0.45f, 0.44f), 64, 0.35f) },
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
        // which is right for vehicle parts but leaves a track prop non-solid. A
        // STATIC mesh prop now collides with its own geometry: MeshProp cooks a
        // non-convex MeshCollider per imported piece (legal on anything without
        // a Rigidbody; cooking is per unique sharedMesh, so a map full of one
        // tree cooks that tree once). The hand-authored primitive hulls this
        // replaced sealed every doorway, walled off ramp feet and left ghost
        // bands — 87 of 113 props were a single box or capsule, and Unity's
        // "cylinder" primitive is a CAPSULE that degenerates to a SPHERE when
        // h < 2r, which is how a 12.6 m volcano shipped with a 5 m floating
        // ball for collision. TrackPresetValidator.CheckColliderCoverage now
        // asserts collider bounds track renderer bounds so that class of defect
        // stays dead. A non-null hull lambda is the OPT-OUT: it suppresses the
        // auto colliders for the two drive-through trigger props (a concave
        // MeshCollider cannot be a trigger). Requires the TrackProps FBX to be
        // CPU-readable (PartModelPostprocessor) — runtime cooking in a player
        // build fails on a stripped mesh while working fine in the editor.

        /// <summary>Invisible collision box (collider only, no renderer).</summary>
        private static GameObject HullBox(Transform parent, Vector3 pos, Vector3 size, Vector3 euler = default)
            => Hull(PrimitiveType.Cube, parent, pos, size, euler);

        // (HullCyl and HullOval died with the primitive-hull era: every static
        // prop now collides with its own mesh, and the sole surviving hulls are
        // the two drive-through trigger boxes below.)

        private static GameObject Hull(PrimitiveType type, Transform parent,
            Vector3 pos, Vector3 scale, Vector3 euler)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "Hull";
            // DestroyImmediate outside play mode: the editor validators build
            // items in edit mode, where deferred Destroy only logs an error.
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(mr);
                else UnityEngine.Object.DestroyImmediate(mr);
            }
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(mf);
                else UnityEngine.Object.DestroyImmediate(mf);
            }
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
        private static GameObject MeshProp(Transform p, string key, Material fallback,
            (string token, Material mat)[] tokens,
            Action<Transform> hull, Action<Transform> primitives)
        {
            var mesh = PartMeshLibrary.TryInstantiate(key, p, p.gameObject.layer, PartMeshLibrary.PropRoot);
            if (mesh == null)
            {
                // Primitives carry their own colliders (TrackBuilder default), so
                // the authored hull is skipped on this path.
                primitives(p);
                return null;
            }
            if (tokens != null && tokens.Length > 0) PartMeshLibrary.AssignByName(mesh, fallback, tokens);
            if (hull != null) hull(p);            // opt-out: authored hull instead
            else AddMeshColliders(mesh);          // default: collide with the geometry
            // Returned so cosmetic scripts (SignalCycle, GhostBob) can attach to
            // the mesh instance; every existing caller ignores it.
            return mesh;
        }

        /// <summary>
        /// Non-convex MeshCollider on every imported piece (BodyPainter's
        /// pattern). sharedMaterial stays null — the same PhysX default
        /// friction (0.6/0.6) the primitive hulls had, so surface feel is
        /// unchanged. The isReadable guard means a mis-imported mesh degrades
        /// to "that piece is a ghost" with a loud log, never to a player-build
        /// cook exception mid-load.
        /// </summary>
        private static void AddMeshColliders(GameObject meshRoot)
        {
            foreach (var mf in meshRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (!mf.sharedMesh.isReadable)
                {
                    Debug.LogWarning($"[TrackCatalog] '{mf.gameObject.name}' mesh is not " +
                                     "CPU-readable; no collider cooked (check " +
                                     "PartModelPostprocessor's TrackProps rule).");
                    continue;
                }
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }
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

        /// <summary>
        /// Generic primitive fallback for the TinyTorque map props: a single
        /// bounds box in the theme's dominant material. 63 bespoke primitive
        /// stand-ins would dwarf the catalog for a path that only runs when an
        /// FBX is missing; the never-black-screen guarantee is what matters.
        /// </summary>
        private static Action<Transform> BoxFallback(Material m, float w, float h, float d)
            => f => LBox("Fallback", f, m, new Vector3(0f, h * 0.5f, 0f),
                         Vector3.zero, new Vector3(w, h, d));

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
                    null,
                    f => LBox("Books", f, TwCover, new Vector3(0, 0.064f, 0), Vector3.zero,
                             new Vector3(0.268f, 0.128f, 0.209f))) },

            new ItemDef { id = "tw_ruler_ramp", label = "Ruler ramp", category = ItemCategory.Scenery,
                theme = ToyWorkshop,
                build = p => MeshProp(p, "tw_ruler_ramp", TwWood,
                    new[] { ("wood", TwWood), ("ruler", TwSteel), ("tick", TwGraphite), ("rail", TwSteel) },
                    // A thin slab rotated to the slope: the car drives its top
                    // face, which is the only surface that has to be right.
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 0.312f, 0.245f, 0.03f, 14f, TwWood, f)) },

            new ItemDef { id = "tw_brick_wall", label = "Toy brick", category = ItemCategory.Scenery,
                theme = ToyWorkshop, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "tw_brick_wall", TwBrick,
                    new[] { ("brick", TwBrick), ("stud", TwBrick), ("plate", TwPlate) },
                    null,
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
                    null,
                    f => LCyl("Mug", f, TwCeramic, new Vector3(0, 0.05f, 0), Vector3.zero,
                              new Vector3(0.090f, 0.050f, 0.090f))) },

            new ItemDef { id = "tw_tape_arch", label = "Tape arch", category = ItemCategory.Scenery,
                theme = ToyWorkshop,
                build = p => MeshProp(p, "tw_tape_arch", TwTape,
                    new[] { ("tape", TwTape), ("core", TwCore) },
                    null,
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
                    null,
                    f => LCyl("Pylon", f, NgFrame, new Vector3(0, 0.16f, 0), Vector3.zero,
                              new Vector3(0.130f, 0.160f, 0.130f))) },

            new ItemDef { id = "ng_arch_gate", label = "Light gate", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_arch_gate", NgFrame,
                    new[] { ("frame", NgFrame), ("glow", NgGlow), ("panel", NgPanel), ("base", NgPanel) },
                    null,
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
                    null,
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
                    null,
                    f => LBox("Barrier", f, NgFrame, new Vector3(0, 0.07f, 0), Vector3.zero,
                             new Vector3(0.530f, 0.140f, 0.090f))) },

            new ItemDef { id = "ng_data_cube", label = "Data stack", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_data_cube", NgPanel,
                    new[] { ("cube", NgPanel), ("glow", NgGlow), ("base", NgFrame) },
                    null,
                    f => LBox("Stack", f, NgPanel, new Vector3(0, 0.099f, 0), Vector3.zero,
                             new Vector3(0.185f, 0.198f, 0.185f))) },

            new ItemDef { id = "ng_spire", label = "Spire", category = ItemCategory.Scenery,
                theme = NeonGrid,
                build = p => MeshProp(p, "ng_spire", NgFrame,
                    new[] { ("spire", NgFrame), ("glow", NgGlow), ("base", NgPanel) },
                    null,
                    f => LCyl("Spire", f, NgFrame, new Vector3(0, 0.30f, 0), Vector3.zero,
                              new Vector3(0.160f, 0.300f, 0.160f))) },

            // Scenery — Beach Boardwalk -----------------------------------
            new ItemDef { id = "bb_palm", label = "Palm", category = ItemCategory.Scenery,
                theme = BeachBoardwalk,
                build = p => MeshProp(p, "bb_palm", BbTrunk,
                    new[] { ("trunk", BbTrunk), ("crown", BbTrunk), ("frond", BbFrond), ("coconut", BbCoconut) },
                    null,
                    f =>
                    {
                        LCyl("Trunk", f, BbTrunk, new Vector3(0, 0.28f, 0), Vector3.zero, new Vector3(0.06f, 0.28f, 0.06f));
                        LCyl("Crown", f, BbFrond, new Vector3(0, 0.58f, 0), Vector3.zero, new Vector3(0.42f, 0.02f, 0.42f));
                    }) },

            new ItemDef { id = "bb_surfboard_ramp", label = "Board ramp", category = ItemCategory.Scenery,
                theme = BeachBoardwalk,
                build = p => MeshProp(p, "bb_surfboard_ramp", BbSand,
                    new[] { ("sand", BbSand), ("board", BbBoard), ("stripe", BbStripe), ("fin", BbStripe) },
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 0.42f, 0.255f, 0.03f, 12.75f, BbSand, f)) },

            new ItemDef { id = "bb_plank_wall", label = "Boardwalk rail", category = ItemCategory.Scenery,
                theme = BeachBoardwalk, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "bb_plank_wall", BbPlank,
                    new[] { ("post", BbPlank), ("cap", BbPlank), ("rail", BbBoard), ("plank", BbPlank) },
                    null,
                    f => LBox("Rail", f, BbPlank, new Vector3(0, 0.089f, 0), Vector3.zero,
                             new Vector3(0.620f, 0.178f, 0.060f))) },

            new ItemDef { id = "bb_tiki_torch", label = "Tiki torch", category = ItemCategory.Scenery,
                theme = BeachBoardwalk, behavior = ItemBehavior.Light,
                build = p => MeshProp(p, "bb_tiki_torch", BbTrunk,
                    new[] { ("pole", BbTrunk), ("node", BbTrunk), ("bowl", VfRock),
                            ("flame", BbFlame), ("base", VfRock) },
                    null,
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
                    null,
                    f => LBox("Castle", f, BbSand, new Vector3(0, 0.13f, 0), Vector3.zero,
                             new Vector3(0.30f, 0.26f, 0.30f))) },

            // Scenery — Volcano Foundry -----------------------------------
            new ItemDef { id = "vf_rock_arch", label = "Rock arch", category = ItemCategory.Scenery,
                theme = VolcanoFoundry,
                build = p => MeshProp(p, "vf_rock_arch", VfRock,
                    new[] { ("rock", VfRock), ("lava", VfLava) },
                    null,
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
                    null,
                    f => LBox("Block", f, VfObsidian, new Vector3(0, 0.092f, 0), Vector3.zero,
                             new Vector3(0.360f, 0.185f, 0.170f))) },

            new ItemDef { id = "vf_steam_vent", label = "Steam vent", category = ItemCategory.Scenery,
                theme = VolcanoFoundry,
                build = p => MeshProp(p, "vf_steam_vent", VfSteel,
                    new[] { ("vent", VfSteel), ("grate", VfSteel), ("lava", VfLava), ("rock", VfRock) },
                    null,
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
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 0.44f, 0.26f, 0.03f, 12.8f, VfSteel, f)) },

            new ItemDef { id = "vf_crag_spire", label = "Crag spire", category = ItemCategory.Scenery,
                theme = VolcanoFoundry,
                build = p => MeshProp(p, "vf_crag_spire", VfRock,
                    new[] { ("crag", VfRock), ("spike", VfObsidian), ("lava", VfLava) },
                    null,
                    f => LCyl("Crag", f, VfRock, new Vector3(0, 0.30f, 0), Vector3.zero,
                              new Vector3(0.200f, 0.300f, 0.200f))) },

            // Scenery — TinyTorque map packs (build_map_props.py). Collision
            // is the mesh itself (MeshProp cooks a MeshCollider per piece), so
            // ramps climb from their true feet, arches clear at their true
            // lintels and landmarks are solid exactly where they look solid.
            // The authored ramps slope along local X (the showcase axis) —
            // place them with yaw 90 to face the racing line.

            // Scenery — Downtown -------------------------------------------
            new ItemDef { id = "dt_arch_gate", label = "City gate", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_arch_gate", DtConcrete, DtTokens,
                    null,
                    BoxFallback(DtConcrete, 3.18f, 1.70f, 0.58f)) },

            new ItemDef { id = "dt_arch_rock", label = "Rock arch", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_arch_rock", DtRock, DtTokens,
                    null,
                    BoxFallback(DtRock, 4.67f, 2.61f, 1.08f)) },

            new ItemDef { id = "dt_barrier", label = "Jersey barrier", category = ItemCategory.Scenery,
                theme = Downtown, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "dt_barrier", DtConcreteLt, DtTokens,
                    null,
                    BoxFallback(DtConcreteLt, 0.302f, 0.08f, 0.068f)) },

            new ItemDef { id = "dt_bld_block", label = "City block", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_bld_block", DtConcrete, DtTokens,
                    null,
                    BoxFallback(DtConcrete, 2.36f, 3.09f, 1.56f)) },

            new ItemDef { id = "dt_bld_hangar", label = "Hangar", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_bld_hangar", DtConcrete, DtTokens,
                    null,
                    BoxFallback(DtConcrete, 1.98f, 1.25f, 2.93f)) },

            new ItemDef { id = "dt_bld_tower", label = "Neon tower", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_bld_tower", DtConcrete, DtTokens,
                    null,
                    BoxFallback(DtConcrete, 1.65f, 8.81f, 1.65f)) },

            new ItemDef { id = "dt_cone", label = "Traffic cone", category = ItemCategory.Scenery,
                theme = Downtown, dynamic = true, dynamicMass = 0.03f, bottomHeavy = true,
                build = p => MeshPropDynamic(p, "dt_cone", DtOrange, DtTokens,
                    b =>
                    {
                        var c = b.AddComponent<BoxCollider>();
                        c.center = new Vector3(0f, 0.037f, 0f);
                        c.size = new Vector3(0.052f, 0.074f, 0.052f);
                    },
                    f => TrackBuilder.Cone("Cone", Vector3.zero, 0.075f, 0.026f, DtOrange, f)) },

            new ItemDef { id = "dt_ramp_jump", label = "Jump ramp", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_ramp_jump", DtConcrete, DtTokens,
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1.0f, 1.06f, 0.03f, 20f, DtConcrete, f)) },

            new ItemDef { id = "dt_ramp_kicker", label = "Kicker", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_ramp_kicker", DtConcrete, DtTokens,
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1.4f, 0.96f, 0.03f, 14f, DtConcrete, f)) },

            new ItemDef { id = "dt_rock_large", label = "Boulder", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_rock_large", DtRock, DtTokens,
                    null,
                    BoxFallback(DtRock, 0.72f, 0.54f, 0.87f)) },

            new ItemDef { id = "dt_rock_small", label = "Rock", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_rock_small", DtRock, DtTokens,
                    null,
                    BoxFallback(DtRock, 0.19f, 0.12f, 0.19f)) },

            new ItemDef { id = "dt_street_lamp", label = "Street lamp", category = ItemCategory.Scenery,
                theme = Downtown, behavior = ItemBehavior.Light,
                lightPos = new Vector3(0f, 0.75f, 0f),
                build = p => MeshProp(p, "dt_street_lamp", DtSteel, DtTokens,
                    null,
                    f => LCyl("Pole", f, DtSteel, new Vector3(0, 0.40f, 0), Vector3.zero,
                              new Vector3(0.06f, 0.40f, 0.06f))) },

            new ItemDef { id = "dt_traffic_light", label = "Traffic light", category = ItemCategory.Scenery,
                theme = Downtown, animated = true,
                build = p =>
                {
                    var m = MeshProp(p, "dt_traffic_light", DtSteel, DtTokens,
                        null,
                        BoxFallback(DtSteel, 0.384f, 0.57f, 0.08f));
                    if (m != null) m.AddComponent<SignalCycle>();
                } },

            new ItemDef { id = "dt_volcano", label = "Volcano", category = ItemCategory.Scenery,
                theme = Downtown,
                build = p => MeshProp(p, "dt_volcano", DtBasalt, DtTokens,
                    null,
                    BoxFallback(DtBasalt, 12.6f, 4.6f, 12.6f)) },

            // Scenery — Toy Room -------------------------------------------
            new ItemDef { id = "toy_ball", label = "Toy ball", category = ItemCategory.Scenery,
                theme = ToyRoom, dynamic = true, dynamicMass = 0.06f,
                build = p => MeshPropDynamic(p, "toy_ball", ToyRed, ToyTokens,
                    b =>
                    {
                        var c = b.AddComponent<SphereCollider>();
                        c.center = new Vector3(0f, 0.42f, 0f);
                        c.radius = 0.42f;
                    },
                    f => LCyl("Ball", f, ToyRed, new Vector3(0, 0.42f, 0), Vector3.zero,
                              new Vector3(0.86f, 0.42f, 0.86f))) },

            new ItemDef { id = "toy_bed", label = "Bed", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_bed", ToyWalnut, ToyTokens,
                    null,
                    BoxFallback(ToyWalnut, 3.62f, 2.67f, 5.03f)) },

            new ItemDef { id = "toy_block_tower", label = "Block tower", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_block_tower", ToyRed, ToyTokens,
                    null,
                    BoxFallback(ToyRed, 0.22f, 1.34f, 0.23f)) },

            new ItemDef { id = "toy_bookcase", label = "Bookcase", category = ItemCategory.Scenery,
                theme = ToyRoom, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "toy_bookcase", ToyWalnut, ToyTokens,
                    null,
                    BoxFallback(ToyWalnut, 1.99f, 4.56f, 0.77f)) },

            new ItemDef { id = "toy_box", label = "Toy box", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_box", ToyCard, ToyTokens,
                    null,
                    BoxFallback(ToyCard, 1.08f, 1.52f, 1.60f)) },

            new ItemDef { id = "toy_brick", label = "Toy brick", category = ItemCategory.Scenery,
                theme = ToyRoom, dynamic = true, dynamicMass = 0.04f,
                build = p => MeshPropDynamic(p, "toy_brick", ToyBlue, ToyTokens,
                    b =>
                    {
                        var c = b.AddComponent<BoxCollider>();
                        c.center = new Vector3(0f, 0.085f, 0f);
                        c.size = new Vector3(0.48f, 0.17f, 0.24f);
                    },
                    f => LBox("Brick", f, ToyBlue, new Vector3(0, 0.085f, 0), Vector3.zero,
                             new Vector3(0.48f, 0.17f, 0.24f))) },

            new ItemDef { id = "toy_chair", label = "Chair", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_chair", ToyWalnut, ToyTokens,
                    null,
                    BoxFallback(ToyWalnut, 1.01f, 2.16f, 1.06f)) },

            new ItemDef { id = "toy_crayon", label = "Crayon", category = ItemCategory.Scenery,
                theme = ToyRoom, dynamic = true, dynamicMass = 0.03f,
                build = p => MeshPropDynamic(p, "toy_crayon", ToyWax, ToyTokens,
                    b =>
                    {
                        // Standing crayon: a box so it topples and stays put.
                        var c = b.AddComponent<BoxCollider>();
                        c.center = new Vector3(0f, 0.12f, 0f);
                        c.size = new Vector3(0.058f, 0.24f, 0.058f);
                    },
                    f => LCyl("Crayon", f, ToyWax, new Vector3(0, 0.12f, 0), Vector3.zero,
                              new Vector3(0.058f, 0.12f, 0.058f))) },

            new ItemDef { id = "toy_domino", label = "Domino", category = ItemCategory.Scenery,
                theme = ToyRoom, dynamic = true, dynamicMass = 0.02f,
                build = p => MeshPropDynamic(p, "toy_domino", ToyCream, ToyTokens,
                    b =>
                    {
                        var c = b.AddComponent<BoxCollider>();
                        c.center = new Vector3(0f, 0.06f, 0f);
                        c.size = new Vector3(0.24f, 0.12f, 0.031f);
                    },
                    f => LBox("Domino", f, ToyCream, new Vector3(0, 0.06f, 0), Vector3.zero,
                             new Vector3(0.24f, 0.12f, 0.031f))) },

            new ItemDef { id = "toy_dresser", label = "Dresser", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_dresser", ToyPine, ToyTokens,
                    null,
                    BoxFallback(ToyPine, 2.76f, 3.07f, 1.56f)) },

            new ItemDef { id = "toy_floor_lamp", label = "Floor lamp", category = ItemCategory.Scenery,
                theme = ToyRoom, behavior = ItemBehavior.Light,
                lightPos = new Vector3(0f, 3.36f, 0f),
                build = p => MeshProp(p, "toy_floor_lamp", ToyBrass, ToyTokens,
                    null,
                    f => LCyl("Pole", f, ToyBrass, new Vector3(0, 1.70f, 0), Vector3.zero,
                              new Vector3(0.09f, 1.70f, 0.09f))) },

            new ItemDef { id = "toy_gate", label = "Toy gate", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_gate", ToyPly, ToyTokens,
                    null,
                    BoxFallback(ToyPly, 2.88f, 1.64f, 0.22f)) },

            new ItemDef { id = "toy_hoop", label = "Hoop", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_hoop", ToyYellow, ToyTokens,
                    null,
                    BoxFallback(ToyYellow, 2.23f, 2.23f, 0.38f)) },

            new ItemDef { id = "toy_lamp", label = "Desk lamp", category = ItemCategory.Scenery,
                theme = ToyRoom, behavior = ItemBehavior.Light,
                lightPos = new Vector3(0.26f, 0.89f, 0f),
                build = p => MeshProp(p, "toy_lamp", ToyRed, ToyTokens,
                    null,
                    BoxFallback(ToyRed, 0.65f, 1.17f, 0.44f)) },

            new ItemDef { id = "toy_ramp_bridge", label = "Card bridge", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_ramp_bridge", ToyPly, ToyTokens,
                    null,
                    BoxFallback(ToyPly, 4.29f, 0.62f, 0.77f)) },

            new ItemDef { id = "toy_ramp_plank", label = "Plank ramp", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_ramp_plank", ToyPly, ToyTokens,
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1.9f, 0.73f, 0.03f, 18f, ToyPly, f)) },

            new ItemDef { id = "toy_table", label = "Table", category = ItemCategory.Scenery,
                theme = ToyRoom,
                build = p => MeshProp(p, "toy_table", ToyPine, ToyTokens,
                    null,
                    BoxFallback(ToyPine, 2.88f, 2.01f, 1.80f)) },

            // Scenery — Enchanted Kingdom ----------------------------------
            new ItemDef { id = "ench_arch_vine", label = "Vine arch", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_arch_vine", EnIron, EnchTokens,
                    null,
                    BoxFallback(EnIron, 1.60f, 1.05f, 0.49f)) },

            new ItemDef { id = "ench_boulder", label = "Mossy boulder", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_boulder", EnRock, EnchTokens,
                    null,
                    BoxFallback(EnRock, 0.52f, 0.35f, 0.45f)) },

            new ItemDef { id = "ench_castle", label = "Castle", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_castle", EnStonePale, EnchTokens,
                    null,
                    BoxFallback(EnStonePale, 6.58f, 8.73f, 7.20f)) },

            new ItemDef { id = "ench_cottage", label = "Cottage", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_cottage", EnPlaster, EnchTokens,
                    null,
                    BoxFallback(EnPlaster, 1.25f, 1.62f, 1.03f)) },

            new ItemDef { id = "ench_crystal", label = "Crystal", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_crystal", EnCrystal, EnchTokens,
                    null,
                    BoxFallback(EnCrystal, 0.56f, 0.92f, 0.61f)) },

            new ItemDef { id = "ench_fountain", label = "Fountain", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_fountain", EnStonePale, EnchTokens,
                    null,
                    f => LCyl("Fountain", f, EnStonePale, new Vector3(0, 0.48f, 0), Vector3.zero,
                              new Vector3(0.60f, 0.48f, 0.60f))) },

            new ItemDef { id = "ench_gate", label = "Castle gate", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_gate", EnStonePale, EnchTokens,
                    null,
                    BoxFallback(EnStonePale, 3.22f, 2.38f, 0.62f)) },

            new ItemDef { id = "ench_gatehouse", label = "Gatehouse", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                // Solid: the authored portcullis reaches the ground (profile
                // zmin is 0 across the span), so there is no drive-through.
                build = p => MeshProp(p, "ench_gatehouse", EnStonePale, EnchTokens,
                    null,
                    BoxFallback(EnStonePale, 2.29f, 3.02f, 0.84f)) },

            new ItemDef { id = "ench_hedge", label = "Hedge", category = ItemCategory.Scenery,
                theme = EnchantedKingdom, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "ench_hedge", EnHedge, EnchTokens,
                    null,
                    BoxFallback(EnHedge, 0.50f, 0.27f, 0.15f)) },

            new ItemDef { id = "ench_lamp", label = "Fairy lamp", category = ItemCategory.Scenery,
                theme = EnchantedKingdom, behavior = ItemBehavior.Light,
                lightPos = new Vector3(0f, 0.60f, 0f),
                build = p => MeshProp(p, "ench_lamp", EnIron, EnchTokens,
                    null,
                    f => LCyl("Pole", f, EnIron, new Vector3(0, 0.40f, 0), Vector3.zero,
                              new Vector3(0.05f, 0.40f, 0.05f))) },

            new ItemDef { id = "ench_peak", label = "Mountain peak", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_peak", EnRock, EnchTokens,
                    null,
                    BoxFallback(EnRock, 15.0f, 11.8f, 15.2f)) },

            new ItemDef { id = "ench_ramp_bridge", label = "Stone bridge", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_ramp_bridge", EnStonePale, EnchTokens,
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1.8f, 1.08f, 0.03f, 12.5f, EnStonePale, f)) },

            new ItemDef { id = "ench_ramp_terrace", label = "Terrace ramp", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                // Unlike its siblings this one climbs along local Z (its X
                // profile is a symmetric plateau) — rising toward the back.
                build = p => MeshProp(p, "ench_ramp_terrace", EnStonePale, EnchTokens,
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1.3f, 2.5f, 0.03f, 16.5f, EnStonePale, f)) },

            new ItemDef { id = "ench_topiary", label = "Topiary", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_topiary", EnHedge, EnchTokens,
                    null,
                    BoxFallback(EnHedge, 0.19f, 0.44f, 0.20f)) },

            new ItemDef { id = "ench_tower", label = "Wizard tower", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_tower", EnStone, EnchTokens,
                    null,
                    BoxFallback(EnStone, 1.46f, 5.57f, 1.46f)) },

            new ItemDef { id = "ench_tree", label = "Blossom tree", category = ItemCategory.Scenery,
                theme = EnchantedKingdom,
                build = p => MeshProp(p, "ench_tree", EnBark, EnchTokens,
                    null,
                    f => LCyl("Trunk", f, EnBark, new Vector3(0, 0.50f, 0), Vector3.zero,
                              new Vector3(0.20f, 0.50f, 0.20f))) },

            // Scenery — Haunted Hollow -------------------------------------
            new ItemDef { id = "haunt_arch_ruin", label = "Ruined arch", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_arch_ruin", HaGrime, HauntTokens,
                    null,
                    BoxFallback(HaGrime, 2.30f, 1.18f, 0.56f)) },

            new ItemDef { id = "haunt_barrow", label = "Barrow", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_barrow", HaEarth, HauntTokens,
                    null,
                    BoxFallback(HaEarth, 11.3f, 3.28f, 11.2f)) },

            new ItemDef { id = "haunt_chapel", label = "Chapel", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_chapel", HaGrime, HauntTokens,
                    null,
                    BoxFallback(HaGrime, 2.21f, 2.48f, 1.95f)) },

            new ItemDef { id = "haunt_crypt", label = "Crypt", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_crypt", HaGrime, HauntTokens,
                    null,
                    BoxFallback(HaGrime, 0.78f, 0.91f, 0.90f)) },

            new ItemDef { id = "haunt_fence", label = "Iron fence", category = ItemCategory.Scenery,
                theme = HauntedHollow, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "haunt_fence", HaIron, HauntTokens,
                    null,
                    BoxFallback(HaIron, 0.57f, 0.27f, 0.10f)) },

            new ItemDef { id = "haunt_gaslamp", label = "Gas lamp", category = ItemCategory.Scenery,
                theme = HauntedHollow, behavior = ItemBehavior.Light,
                lightPos = new Vector3(0f, 0.60f, 0f),
                build = p => MeshProp(p, "haunt_gaslamp", HaIron, HauntTokens,
                    null,
                    f => LCyl("Pole", f, HaIron, new Vector3(0, 0.40f, 0), Vector3.zero,
                              new Vector3(0.05f, 0.40f, 0.05f))) },

            new ItemDef { id = "haunt_gate", label = "Cemetery gate", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_gate", HaGrime, HauntTokens,
                    null,
                    BoxFallback(HaGrime, 3.20f, 1.58f, 0.84f)) },

            new ItemDef { id = "haunt_ghost", label = "Ghost", category = ItemCategory.Scenery,
                theme = HauntedHollow, animated = true,
                build = p =>
                {
                    var m = MeshProp(p, "haunt_ghost", HaGhost, HauntTokens,
                        h => HullBox(h, new Vector3(0, 0.55f, 0), new Vector3(0.90f, 1.00f, 0.55f))
                                 .GetComponent<Collider>().isTrigger = true,
                        BoxFallback(HaGhost, 0.90f, 1.00f, 0.55f));
                    if (m != null) m.AddComponent<GhostBob>();
                } },

            new ItemDef { id = "haunt_gravestone", label = "Gravestone", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_gravestone", HaGrime, HauntTokens,
                    null,
                    BoxFallback(HaGrime, 0.195f, 0.31f, 0.14f)) },

            new ItemDef { id = "haunt_hearse", label = "Hearse", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_hearse", HaTar, HauntTokens,
                    null,
                    BoxFallback(HaTar, 0.47f, 0.53f, 0.94f)) },

            new ItemDef { id = "haunt_mansion", label = "Mansion", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_mansion", HaClapboard, HauntTokens,
                    null,
                    BoxFallback(HaClapboard, 2.74f, 3.65f, 2.72f)) },

            new ItemDef { id = "haunt_pumpkin", label = "Pumpkin", category = ItemCategory.Scenery,
                theme = HauntedHollow, dynamic = true, dynamicMass = 0.06f,
                build = p => MeshPropDynamic(p, "haunt_pumpkin", HaPumpkin, HauntTokens,
                    b =>
                    {
                        var c = b.AddComponent<SphereCollider>();
                        c.center = new Vector3(0f, 0.133f, 0f);
                        c.radius = 0.133f;
                    },
                    f => LCyl("Pumpkin", f, HaPumpkin, new Vector3(0, 0.13f, 0), Vector3.zero,
                              new Vector3(0.28f, 0.13f, 0.28f))) },

            new ItemDef { id = "haunt_ramp_slab", label = "Slab ramp", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_ramp_slab", HaGrime, HauntTokens,
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1.7f, 1.10f, 0.03f, 10f, HaGrime, f)) },

            new ItemDef { id = "haunt_ramp_tomb", label = "Tomb ramp", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_ramp_tomb", HaStoneDark, HauntTokens,
                    null,
                    f => TrackBuilder.Ramp("Ramp", Vector3.zero, 0f, 1.2f, 1.14f, 0.03f, 24f, HaStoneDark, f)) },

            new ItemDef { id = "haunt_tree", label = "Dead tree", category = ItemCategory.Scenery,
                theme = HauntedHollow,
                build = p => MeshProp(p, "haunt_tree", HaBark, HauntTokens,
                    null,
                    f => LCyl("Trunk", f, HaBark, new Vector3(0, 0.50f, 0), Vector3.zero,
                              new Vector3(0.15f, 0.50f, 0.15f))) },

            // The one prop in the project that does not stand on the ground.
            // Its source mesh floats 1.6 authored metres up, but the exporter
            // re-origins every prop at its base contact point (which is what
            // makes ItemPose's drop-to-surface work for the other 62), so the
            // hover has to be put back here or the wisp sits in the mud.
            new ItemDef { id = "haunt_wisp", label = "Wisp", category = ItemCategory.Scenery,
                theme = HauntedHollow, animated = true,
                build = p =>
                {
                    var m = MeshProp(p, "haunt_wisp", HaGhost, HauntTokens,
                        h => HullBox(h, new Vector3(0, 0.38f, 0), new Vector3(0.25f, 0.44f, 0.15f))
                                 .GetComponent<Collider>().isTrigger = true,
                        BoxFallback(HaGhost, 0.25f, 0.44f, 0.15f));
                    if (m == null) return;
                    m.transform.localPosition = new Vector3(0f, 0.16f, 0f);
                    // GhostBob caches its base pose in Start, so the lift is
                    // the centre it bobs about rather than something it fights.
                    m.AddComponent<GhostBob>();
                } },

            // Scenery — Torque Falls (the daylight town) --------------------
            //
            // Collision is the exported mesh itself. That retires a whole class
            // of approximation this section used to carry (measured sub-boxes,
            // doorway rings) — a car can now drive into every aperture the kit
            // asserts a 0.46 x 0.39 clearance on, park inside the buildings
            // that have interiors, and thread the water tower's legs, because
            // the collider IS the geometry. TrackPresetValidator still probes
            // each drive-in corridor (garage, filling station, dealership,
            // fire station, arena) so an FBX re-export that grows a doorsill
            // fails the build instead of quietly bricking up the one prop the
            // map was designed around.

            new ItemDef { id = "city_house_a", label = "Bungalow", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_house_a", CyWall5, CityTokens,
                    null,
                    BoxFallback(CyWall5, 0.980f, 0.662f, 0.940f)) },

            new ItemDef { id = "city_house_b", label = "Two-storey house", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_house_b", CyWall7, CityTokens,
                    null,
                    BoxFallback(CyWall7, 0.940f, 0.967f, 1.080f)) },

            new ItemDef { id = "city_cottage", label = "Cottage", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_cottage", CyRender3, CityTokens,
                    null,
                    BoxFallback(CyRender3, 0.660f, 0.692f, 0.640f)) },

            new ItemDef { id = "city_townhouse", label = "Terrace unit", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_townhouse", CyBrick, CityTokens,
                    null,
                    BoxFallback(CyBrick, 0.540f, 1.020f, 1.120f)) },

            new ItemDef { id = "city_apartment", label = "Walk-up", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_apartment", CyRender2, CityTokens,
                    null,
                    BoxFallback(CyRender2, 1.420f, 1.665f, 1.320f)) },

            new ItemDef { id = "city_store", label = "Corner shop", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_store", CyBrickPale, CityTokens,
                    null,
                    BoxFallback(CyBrickPale, 1.020f, 0.657f, 0.960f)) },

            new ItemDef { id = "city_diner", label = "Diner", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_diner", CyCream, CityTokens,
                    null,
                    BoxFallback(CyCream, 1.500f, 0.637f, 0.820f)) },

            new ItemDef { id = "city_warehouse", label = "Warehouse", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_warehouse", CyConcrete, CityTokens,
                    null,
                    BoxFallback(CyConcrete, 2.020f, 0.755f, 2.020f)) },

            new ItemDef { id = "city_clocktower", label = "Clock tower", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_clocktower", CyBrick, CityTokens,
                    null,
                    BoxFallback(CyBrick, 1.020f, 2.260f, 1.040f)) },

            new ItemDef { id = "city_watertower", label = "Water tower", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                // Four legs, not a box: the tank is 1.2 m up and a car is meant
                // to be able to drive under it between the legs.
                build = p => MeshProp(p, "city_watertower", CyGalv, CityTokens,
                    null,
                    BoxFallback(CyGalv, 0.835f, 2.035f, 0.835f)) },

            // --- the three you drive into --------------------------------

            new ItemDef { id = "city_garage", label = "Garage (drive-through)", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_garage", CyStucco, CityTokens,
                    null,
                    BoxFallback(CyStucco, 1.360f, 0.640f, 1.760f)) },

            new ItemDef { id = "city_gas", label = "Filling station", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_gas", CyPaintWhite, CityTokens,
                    null,
                    BoxFallback(CyPaintWhite, 2.000f, 0.701f, 1.990f)) },

            new ItemDef { id = "city_autoshop", label = "Dealership", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_autoshop", CyRender3, CityTokens,
                    null,
                    BoxFallback(CyRender3, 2.430f, 0.850f, 1.985f)) },

            new ItemDef { id = "city_firehouse", label = "Fire station", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_firehouse", CyBrick, CityTokens,
                    null,
                    BoxFallback(CyBrick, 1.500f, 1.363f, 1.713f)) },

            new ItemDef { id = "city_arena", label = "Arena", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_arena", CyConcrete, CityTokens,
                    null,
                    BoxFallback(CyConcrete, 8.878f, 2.040f, 6.537f)) },

            // --- street furniture ----------------------------------------

            new ItemDef { id = "city_pole", label = "Telephone pole", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                // 2.6 m across because it carries half a span of wire each
                // side, arriving at the end with zero slope: two poles placed
                // at 2.6 m join into one catenary with no wire prop between.
                build = p => MeshProp(p, "city_pole", CyTimber, CityTokens,
                    null,
                    f => LCyl("Pole", f, CyTimber, new Vector3(0f, 0.48f, 0f), Vector3.zero,
                              new Vector3(0.045f, 0.48f, 0.045f))) },

            new ItemDef { id = "city_pole_t", label = "Transformer pole", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                // Same span, but the pole itself stands 0.365 off the mesh
                // centre because the transformer bank hangs to one side.
                build = p => MeshProp(p, "city_pole_t", CyTimber, CityTokens,
                    null,
                    f => LCyl("Pole", f, CyTimber, new Vector3(0f, 0.48f, -0.365f), Vector3.zero,
                              new Vector3(0.045f, 0.48f, 0.045f))) },

            new ItemDef { id = "city_lamp", label = "Street lamp", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                // No ItemBehavior.Light, unlike dt_street_lamp: the town places
                // ~110 of these and it is a midday map, so a point light each
                // would be 110 real-time lights buying nothing. The head keeps
                // its emissive material and reads as lit.
                build = p => MeshProp(p, "city_lamp", CyAlu, CityTokens,
                    null,
                    f => LCyl("Column", f, CyAlu, new Vector3(-0.147f, 0.37f, 0f), Vector3.zero,
                              new Vector3(0.062f, 0.37f, 0.062f))) },

            new ItemDef { id = "city_signal", label = "Traffic signal", category = ItemCategory.Scenery,
                theme = TorqueFalls, animated = true,
                build = p =>
                {
                    var m = MeshProp(p, "city_signal", CyAlu, CityTokens,
                        null,
                        BoxFallback(CyAlu, 0.475f, 0.560f, 0.081f));
                    // Two heads on the mast arm plus a pedestrian lamp, all
                    // driven together — the exporter split the dark lenses per
                    // head so each one has its own red and amber renderer.
                    if (m != null) m.AddComponent<SignalCycle>();
                } },

            new ItemDef { id = "city_sign", label = "Stop sign", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_sign", CyGalv, CityTokens,
                    null,
                    f => LCyl("Post", f, CyGalv, new Vector3(0f, 0.14f, 0f), Vector3.zero,
                              new Vector3(0.030f, 0.14f, 0.030f))) },

            new ItemDef { id = "city_hydrant", label = "Hydrant", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_hydrant", CyRed, CityTokens,
                    null,
                    f => LCyl("Hydrant", f, CyRed, new Vector3(0f, 0.047f, 0f), Vector3.zero,
                              new Vector3(0.055f, 0.047f, 0.055f))) },

            new ItemDef { id = "city_mailbox", label = "Mailbox", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_mailbox", CyBlue, CityTokens,
                    null,
                    BoxFallback(CyBlue, 0.140f, 0.175f, 0.060f)) },

            new ItemDef { id = "city_bench", label = "Bench", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_bench", CyTimber, CityTokens,
                    null,
                    BoxFallback(CyTimber, 0.260f, 0.110f, 0.060f)) },

            new ItemDef { id = "city_busstop", label = "Bus shelter", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_busstop", CyGalv, CityTokens,
                    null,
                    BoxFallback(CyGalv, 0.480f, 0.315f, 0.160f)) },

            new ItemDef { id = "city_billboard", label = "Billboard", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p =>
                {
                    var m = MeshProp(p, "city_billboard", CyGalv, CityTokens,
                        null,
                        BoxFallback(CyGalv, 0.580f, 0.795f, 0.060f));
                    // The face is authored BLANK (a cream slab under three
                    // floodlights); the poster component draws the ad —
                    // deliberate new authoring, chosen over re-authoring the
                    // Blender kit.
                    if (m != null) m.AddComponent<BillboardPoster>();
                } },

            // --- boundaries (tile at their own 0.40 m pitch) --------------

            new ItemDef { id = "city_fence_picket", label = "Picket fence", category = ItemCategory.Scenery,
                theme = TorqueFalls, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "city_fence_picket", CyTrim, CityTokens,
                    null,
                    BoxFallback(CyTrim, 0.420f, 0.126f, 0.020f)) },

            new ItemDef { id = "city_fence_chain", label = "Chain-link fence", category = ItemCategory.Scenery,
                theme = TorqueFalls, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "city_fence_chain", CyGalv, CityTokens,
                    null,
                    BoxFallback(CyGalv, 0.380f, 0.206f, 0.020f)) },

            new ItemDef { id = "city_wall", label = "Garden wall", category = ItemCategory.Scenery,
                theme = TorqueFalls, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "city_wall", CyBrick, CityTokens,
                    null,
                    BoxFallback(CyBrick, 0.420f, 0.154f, 0.060f)) },

            new ItemDef { id = "city_hedge", label = "Hedge", category = ItemCategory.Scenery,
                theme = TorqueFalls, snap = SnapMode.TileEdge,
                build = p => MeshProp(p, "city_hedge", CyLeafHedge, CityTokens,
                    null,
                    BoxFallback(CyLeafHedge, 0.420f, 0.170f, 0.100f)) },

            // --- planting -------------------------------------------------

            new ItemDef { id = "city_tree_oak", label = "Oak", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_tree_oak", CyBark, CityTokens,
                    null,
                    f => LCyl("Trunk", f, CyBark, new Vector3(0f, 0.35f, 0f), Vector3.zero,
                              new Vector3(0.10f, 0.35f, 0.10f))) },

            new ItemDef { id = "city_tree_maple", label = "Maple", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_tree_maple", CyBark, CityTokens,
                    null,
                    f => LCyl("Trunk", f, CyBark, new Vector3(0f, 0.45f, 0f), Vector3.zero,
                              new Vector3(0.06f, 0.45f, 0.06f))) },

            new ItemDef { id = "city_tree_pine", label = "Pine", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_tree_pine", CyBarkPine, CityTokens,
                    null,
                    f => LCyl("Trunk", f, CyBarkPine, new Vector3(0f, 0.20f, 0f), Vector3.zero,
                              new Vector3(0.07f, 0.20f, 0.07f))) },

            new ItemDef { id = "city_tree_young", label = "Street sapling", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_tree_young", CyBark, CityTokens,
                    null,
                    f => LCyl("Trunk", f, CyBark, new Vector3(0f, 0.18f, 0f), Vector3.zero,
                              new Vector3(0.06f, 0.18f, 0.06f))) },

            new ItemDef { id = "city_bush", label = "Shrub", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_bush", CyLeafHedge, CityTokens,
                    null,
                    BoxFallback(CyLeafHedge, 0.180f, 0.138f, 0.180f)) },

            new ItemDef { id = "city_planter", label = "Planter", category = ItemCategory.Scenery,
                theme = TorqueFalls,
                build = p => MeshProp(p, "city_planter", CyConcrete, CityTokens,
                    null,
                    f => LCyl("Planter", f, CyConcrete, new Vector3(0f, 0.043f, 0f), Vector3.zero,
                              new Vector3(0.136f, 0.043f, 0.136f))) },
        };

        // ---- theme names (palette group headers; also ItemDef.theme values) ----
        public const string ToyWorkshop    = "Toy Workshop";
        public const string NeonGrid       = "Neon Grid";
        public const string BeachBoardwalk = "Beach Boardwalk";
        public const string VolcanoFoundry = "Volcano Foundry";
        // TinyTorque map packs (build_map_props.py).
        public const string Downtown         = "Downtown";
        public const string ToyRoom          = "Toy Room";
        public const string EnchantedKingdom = "Enchanted Kingdom";
        public const string HauntedHollow    = "Haunted Hollow";
        public const string TorqueFalls      = "Torque Falls";

        /// <summary>Theme headers in palette order.</summary>
        public static readonly string[] Themes =
            { ToyWorkshop, NeonGrid, BeachBoardwalk, VolcanoFoundry,
              Downtown, ToyRoom, EnchantedKingdom, HauntedHollow, TorqueFalls };

        /// <summary>
        /// The material token table a theme's props are bound with. Public only
        /// so <c>TrackPresetValidator</c> can resolve each FBX child's name
        /// against it: <see cref="Vehicles.PartMeshLibrary.AssignByName"/> is a
        /// first-match SUBSTRING matcher, so a token listed after one it
        /// contains is silently swallowed — the prop still loads, still passes
        /// its extent and budget, and renders a plausible WRONG colour.
        /// </summary>
        public static (string, Material)[] TokensFor(string theme)
        {
            if (theme == Downtown) return DtTokens;
            if (theme == ToyRoom) return ToyTokens;
            if (theme == EnchantedKingdom) return EnchTokens;
            if (theme == HauntedHollow) return HauntTokens;
            if (theme == TorqueFalls) return CityTokens;
            return null;
        }

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
        private static Material NgGlow  => T("ng_glow", 0.25f, 0.95f, 1.00f, 0.85f, 3.0f);
        private static Material NgPanel => T("ng_panel", 0.10f, 0.12f, 0.17f, 0.55f);

        private static Material BbTrunk   => T("bb_trunk", 0.46f, 0.33f, 0.20f, 0.20f);
        private static Material BbFrond   => T("bb_frond", 0.20f, 0.56f, 0.24f, 0.25f);
        private static Material BbCoconut => T("bb_coconut", 0.34f, 0.23f, 0.14f, 0.25f);
        private static Material BbBoard   => T("bb_board", 0.94f, 0.92f, 0.86f, 0.70f);
        private static Material BbStripe  => T("bb_stripe", 0.95f, 0.34f, 0.22f, 0.60f);
        private static Material BbPlank   => T("bb_plank", 0.64f, 0.50f, 0.34f, 0.20f);
        private static Material BbSand    => T("bb_sand", 0.86f, 0.77f, 0.56f, 0.15f);
        private static Material BbFlame   => T("bb_flame", 1.00f, 0.56f, 0.14f, 0.60f, 2.6f);
        private static Material BbBall    => T("bb_ball", 0.96f, 0.96f, 0.96f, 0.65f);
        private static Material BbPanelA  => T("bb_panel", 0.92f, 0.22f, 0.24f, 0.65f);

        private static Material VfRock     => T("vf_rock", 0.29f, 0.26f, 0.25f, 0.15f);
        private static Material VfObsidian => T("vf_obsidian", 0.09f, 0.08f, 0.11f, 0.85f);
        private static Material VfLava     => T("vf_lava", 1.00f, 0.34f, 0.06f, 0.50f, 3.0f);
        private static Material VfSteel    => T("vf_steel", 0.44f, 0.45f, 0.48f, 0.70f);
        private static Material VfBarrel   => T("vf_barrel", 0.72f, 0.46f, 0.12f, 0.55f);

        // ---- TinyTorque map-pack materials (build_map_props.py) --------------
        // Colors are the blends' principled values gamma-lifted to sRGB; the
        // authored 0.8-grey procedural placeholders (walnut, ply, stone family,
        // thatch, grime…) get hand-picked flat colors in-theme. Smoothness is
        // 1 − authored roughness.

        // Downtown
        private static Material DtConcrete   => T("dt_concrete", 0.25f, 0.26f, 0.28f, 0.30f);
        private static Material DtConcreteLt => T("dt_concretelt", 0.39f, 0.39f, 0.41f, 0.38f);
        private static Material DtGold       => T("dt_gold", 1.00f, 0.84f, 0.45f, 0.84f);
        private static Material DtNeonCyan   => T("dt_neoncyan", 0.25f, 0.80f, 1.00f, 0.80f, 3.0f);    // authored 6.0
        private static Material DtNeonGold   => T("dt_neongold", 1.00f, 0.75f, 0.28f, 0.80f, 2.5f);    // authored 5.0
        private static Material DtNeonCrim   => T("dt_neoncrimson", 1.00f, 0.15f, 0.10f, 0.80f, 3.5f); // authored 7.0
        private static Material DtNeonOrange => T("dt_neonorange", 1.00f, 0.42f, 0.08f, 0.80f, 2.8f);
        private static Material DtPanel      => T("dt_panel", 0.15f, 0.16f, 0.18f, 0.67f);
        private static Material DtSteel      => T("dt_steel", 0.51f, 0.52f, 0.55f, 0.69f);
        private static Material DtLampHead   => T("dt_lamp", 0.98f, 0.93f, 0.78f, 0.90f, 3.5f);        // authored 9.0
        private static Material DtRock       => T("dt_rock", 0.30f, 0.29f, 0.30f, 0.20f);
        private static Material DtRockTop    => T("dt_rocktop", 0.42f, 0.40f, 0.38f, 0.14f);
        private static Material DtCrimson    => T("dt_crimson", 0.75f, 0.23f, 0.20f, 0.72f);
        private static Material DtFacade     => T("dt_facade", 0.62f, 0.60f, 0.57f, 0.50f);
        private static Material DtGlass      => T("dt_glass", 0.16f, 0.20f, 0.25f, 0.93f);
        private static Material DtOrange     => T("dt_orange", 0.88f, 0.41f, 0.12f, 0.66f);
        private static Material DtWhite      => T("dt_white", 0.82f, 0.82f, 0.83f, 0.70f);
        private static Material DtRubber     => T("dt_rubber", 0.14f, 0.14f, 0.16f, 0.32f);
        // The three signal lenses carry emission (glow > 0 enables the keyword)
        // so SignalCycle can drive them through MaterialPropertyBlocks.
        private static Material DtSigRed     => T("dt_sigred", 0.90f, 0.12f, 0.06f, 0.85f, 0.3f);
        private static Material DtSigAmber   => T("dt_sigamber", 0.95f, 0.55f, 0.10f, 0.85f, 0.3f);
        private static Material DtSigGreen   => T("dt_siggreen", 0.25f, 0.95f, 0.45f, 0.85f, 2.5f);
        private static Material DtBasalt     => T("dt_basalt", 0.23f, 0.21f, 0.21f, 0.22f);
        private static Material DtLava       => T("dt_lava", 0.97f, 0.45f, 0.12f, 0.50f, 3.0f);

        // Toy Room
        private static Material ToyRed    => T("toy_red", 0.80f, 0.27f, 0.23f, 0.80f);
        private static Material ToyCream  => T("toy_cream", 0.78f, 0.74f, 0.67f, 0.70f);
        private static Material ToyYellow => T("toy_yellow", 0.94f, 0.77f, 0.20f, 0.80f);
        private static Material ToyBlue   => T("toy_blue", 0.24f, 0.42f, 0.74f, 0.80f);
        private static Material ToyGreen  => T("toy_green", 0.28f, 0.60f, 0.37f, 0.80f);
        private static Material ToyOrangeM => T("toy_orange", 0.91f, 0.51f, 0.17f, 0.80f);
        private static Material ToyPurple => T("toy_purple", 0.51f, 0.30f, 0.60f, 0.80f);
        private static Material ToyWalnut => T("toy_walnut", 0.36f, 0.24f, 0.15f, 0.50f);
        private static Material ToyPine   => T("toy_pine", 0.75f, 0.58f, 0.36f, 0.50f);
        private static Material ToyPly    => T("toy_ply", 0.80f, 0.66f, 0.44f, 0.50f);
        private static Material ToyCard   => T("toy_card", 0.76f, 0.63f, 0.45f, 0.45f);
        private static Material ToyPaper  => T("toy_paper", 0.80f, 0.79f, 0.77f, 0.38f);
        private static Material ToyInk    => T("toy_ink", 0.13f, 0.13f, 0.15f, 0.58f);
        private static Material ToyWax    => T("toy_wax", 0.85f, 0.33f, 0.28f, 0.54f);
        private static Material ToyFelt   => T("toy_feltblue", 0.22f, 0.27f, 0.38f, 0.12f);
        private static Material ToyCotton => T("toy_cotton", 0.73f, 0.74f, 0.76f, 0.16f);
        private static Material ToyBrass  => T("toy_brass", 0.90f, 0.77f, 0.50f, 0.78f);
        private static Material ToyShade  => T("toy_shade", 0.76f, 0.69f, 0.58f, 0.30f);
        private static Material ToyBulb   => T("toy_bulb", 0.98f, 0.95f, 0.85f, 0.88f, 2.6f);
        private static Material ToyBook0  => T("toy_book0", 0.57f, 0.23f, 0.20f, 0.42f);
        private static Material ToyBook1  => T("toy_book1", 0.20f, 0.34f, 0.51f, 0.42f);
        private static Material ToyBook2  => T("toy_book2", 0.60f, 0.46f, 0.20f, 0.42f);
        private static Material ToyBook3  => T("toy_book3", 0.20f, 0.44f, 0.32f, 0.42f);
        private static Material ToyBook4  => T("toy_book4", 0.46f, 0.20f, 0.42f, 0.42f);
        private static Material ToyBook5  => T("toy_book5", 0.31f, 0.31f, 0.33f, 0.42f);
        private static Material ToyBook6  => T("toy_book6", 0.63f, 0.57f, 0.41f, 0.42f);

        // Enchanted Kingdom
        private static Material EnIron      => T("ench_iron", 0.21f, 0.21f, 0.23f, 0.58f);
        private static Material EnLeaf      => T("ench_leaf", 0.34f, 0.60f, 0.28f, 0.28f);
        private static Material EnLeafDark  => T("ench_leafdark", 0.22f, 0.42f, 0.20f, 0.28f);
        private static Material EnRose      => T("ench_rose", 0.66f, 0.21f, 0.28f, 0.60f);
        private static Material EnRock      => T("ench_rock", 0.34f, 0.33f, 0.34f, 0.22f);
        private static Material EnRockMoss  => T("ench_rockmoss", 0.25f, 0.34f, 0.22f, 0.16f);
        private static Material EnStone     => T("ench_stone", 0.55f, 0.53f, 0.50f, 0.50f);
        private static Material EnStonePale => T("ench_stonepale", 0.72f, 0.69f, 0.63f, 0.50f);
        private static Material EnStoneMoss => T("ench_stonemoss", 0.48f, 0.55f, 0.42f, 0.50f);
        private static Material EnSlate     => T("ench_slate", 0.36f, 0.38f, 0.44f, 0.50f);
        private static Material EnSlateTeal => T("ench_slateteal", 0.25f, 0.44f, 0.46f, 0.50f);
        private static Material EnSlatePlum => T("ench_slateplum", 0.40f, 0.28f, 0.44f, 0.50f);
        private static Material EnGold      => T("ench_gold", 1.00f, 0.84f, 0.45f, 0.84f);
        private static Material EnWindow    => T("ench_window", 0.71f, 0.55f, 0.29f, 0.80f, 2.2f);
        private static Material EnCrimson   => T("ench_crimson", 0.63f, 0.21f, 0.23f, 0.54f);
        private static Material EnPlaster   => T("ench_plaster", 0.68f, 0.66f, 0.60f, 0.26f);
        private static Material EnTimber    => T("ench_timber", 0.24f, 0.19f, 0.15f, 0.34f);
        private static Material EnThatch    => T("ench_thatch", 0.71f, 0.58f, 0.36f, 0.14f);
        private static Material EnCrystal   => T("ench_crystalrose", 0.46f, 0.34f, 0.55f, 0.91f, 0.8f);
        private static Material EnWater     => T("ench_water", 0.20f, 0.40f, 0.53f, 0.94f, 1.2f);
        private static Material EnSpray     => T("ench_spray", 0.60f, 0.75f, 0.85f, 0.88f, 1.3f);
        private static Material EnFlame     => T("ench_flame", 0.80f, 0.66f, 0.42f, 0.70f, 2.8f);
        private static Material EnHedge     => T("ench_hedge", 0.30f, 0.52f, 0.26f, 0.28f);
        private static Material EnSnow      => T("ench_snow", 0.82f, 0.85f, 0.90f, 0.58f);
        private static Material EnAzure     => T("ench_azure", 0.20f, 0.34f, 0.58f, 0.54f);
        private static Material EnBark      => T("ench_bark", 0.28f, 0.21f, 0.17f, 0.18f);
        private static Material EnBlossom   => T("ench_blossom", 0.81f, 0.68f, 0.78f, 0.56f, 1.2f);

        // Haunted Hollow
        private static Material HaGrime     => T("haunt_grime", 0.52f, 0.51f, 0.48f, 0.26f);
        private static Material HaRubble    => T("haunt_rubble", 0.35f, 0.34f, 0.32f, 0.16f);
        private static Material HaEarth     => T("haunt_earth", 0.25f, 0.22f, 0.18f, 0.12f);
        private static Material HaMoss      => T("haunt_moss", 0.20f, 0.28f, 0.18f, 0.14f);
        private static Material HaFlame     => T("haunt_flame", 0.44f, 0.68f, 0.50f, 0.72f, 2.8f);
        private static Material HaIron      => T("haunt_iron", 0.19f, 0.19f, 0.21f, 0.48f);
        private static Material HaShingle   => T("haunt_shingle", 0.30f, 0.29f, 0.31f, 0.50f);
        private static Material HaVerdigris => T("haunt_verdigris", 0.31f, 0.47f, 0.42f, 0.42f);
        private static Material HaWindow    => T("haunt_window", 0.34f, 0.56f, 0.42f, 0.78f, 2.2f);
        private static Material HaMarble    => T("haunt_marble", 0.72f, 0.71f, 0.68f, 0.48f);
        private static Material HaStoneDark => T("haunt_stonedark", 0.33f, 0.32f, 0.34f, 0.26f);
        private static Material HaGhost     => T("haunt_ghost", 0.45f, 0.63f, 0.56f, 0.66f, 0.7f);
        private static Material HaGhostDim  => T("haunt_ghostdim", 0.49f, 0.61f, 0.55f, 0.66f, 0.4f);
        private static Material HaTar       => T("haunt_tar", 0.15f, 0.15f, 0.16f, 0.56f);
        private static Material HaGlass     => T("haunt_glass", 0.13f, 0.16f, 0.15f, 0.84f);
        private static Material HaCandle    => T("haunt_candle", 0.74f, 0.60f, 0.38f, 0.70f, 2.4f);
        private static Material HaDeadWood  => T("haunt_deadwood", 0.42f, 0.38f, 0.33f, 0.20f);
        private static Material HaClapboard => T("haunt_clapboard", 0.36f, 0.35f, 0.34f, 0.32f);
        private static Material HaPumpkin   => T("haunt_pumpkin", 0.75f, 0.40f, 0.13f, 0.66f);
        private static Material HaJack      => T("haunt_jack", 0.80f, 0.52f, 0.19f, 0.70f, 2.6f);
        private static Material HaStalk     => T("haunt_stalk", 0.31f, 0.32f, 0.19f, 0.28f);
        private static Material HaBark      => T("haunt_bark", 0.24f, 0.20f, 0.17f, 0.14f);

        // Torque Falls — the only pack whose numbers were MEASURED rather than
        // read off the source by eye: build_map_props.py prints a MATJSON block
        // per pack with each material's authored albedo (sRGB-encoded, the same
        // conversion the four packs above were given by hand), smoothness as
        // 1 − roughness, and `glow` as the multiple of its own albedo the
        // surface emits. Colours and smoothness below are that output verbatim.
        //
        // The glow figures sit between the two worlds. Authored for Cycles they
        // run 2.5–19x — sold there by AgX plus a compositor glare. The game now
        // has the glare's stand-in (CameraBloom, HDR camera), so these were
        // raised from the LDR-era "still lit at noon" clamps to roughly
        // authored x 0.5 (capped ~4 — a midday town is not a night render),
        // with the authored ratio noted beside each so the trade stays visible.
        // The two dark signal lenses stay at 0.3 on purpose: SignalCycle drives
        // the lit state, and the resting value is the OFF lamp.
        private static Material CyAlu        => T("city_alu", 0.774f, 0.780f, 0.789f, 0.72f);
        private static Material CyAsphalt    => T("city_asphalt", 0.215f, 0.218f, 0.229f, 0.22f);
        private static Material CyBark       => T("city_bark", 0.322f, 0.283f, 0.246f, 0.12f);
        private static Material CyBarkPine   => T("city_barkpine", 0.293f, 0.235f, 0.202f, 0.12f);
        private static Material CyBlack      => T("city_black", 0.152f, 0.152f, 0.164f, 0.64f);
        private static Material CyBlue       => T("city_blue", 0.206f, 0.373f, 0.665f, 0.70f);
        private static Material CyBrick      => T("city_brick", 0.519f, 0.415f, 0.382f, 0.18f);
        private static Material CyBrickPale  => T("city_brickpale", 0.634f, 0.594f, 0.555f, 0.18f);
        private static Material CyChrome     => T("city_chrome", 0.941f, 0.945f, 0.957f, 0.89f);
        private static Material CyConcrete   => T("city_concrete", 0.522f, 0.525f, 0.527f, 0.20f);
        private static Material CyConcreteDk => T("city_concretedk", 0.373f, 0.378f, 0.384f, 0.18f);
        private static Material CyCream      => T("city_cream", 0.798f, 0.774f, 0.715f, 0.64f);
        private static Material CyDoor       => T("city_door", 0.461f, 0.276f, 0.243f, 0.70f);
        private static Material CyFlower2    => T("city_flower2", 0.748f, 0.396f, 0.680f, 0.45f);
        private static Material CyFlower3    => T("city_flower3", 0.865f, 0.821f, 0.410f, 0.45f);
        private static Material CyGalv       => T("city_galv", 0.680f, 0.687f, 0.698f, 0.52f);
        private static Material CyGlass      => T("city_glass", 0.260f, 0.309f, 0.346f, 0.92f);
        private static Material CyGlassLit   => T("city_glasslit", 0.461f, 0.490f, 0.512f, 0.94f, 1.6f);   // authored 2.47
        private static Material CyGlassShop  => T("city_glassshop", 0.357f, 0.403f, 0.430f, 0.94f);
        private static Material CyGrass      => T("city_grass", 0.283f, 0.397f, 0.212f, 0.10f);
        private static Material CyGreen      => T("city_green", 0.235f, 0.527f, 0.357f, 0.70f);
        private static Material CyInterior   => T("city_interior", 0.584f, 0.584f, 0.588f, 0.28f);
        private static Material CyLamp       => T("city_lamp", 0.955f, 0.936f, 0.865f, 0.90f, 2.4f);       // authored 4.23
        private static Material CyLeaf0      => T("city_leaf0", 0.298f, 0.464f, 0.245f, 0.28f);
        private static Material CyLeaf1      => T("city_leaf1", 0.347f, 0.441f, 0.229f, 0.28f);
        private static Material CyLeaf2      => T("city_leaf2", 0.262f, 0.415f, 0.258f, 0.28f);
        private static Material CyLeafHedge  => T("city_leafhedge", 0.248f, 0.392f, 0.206f, 0.28f);
        private static Material CyLeafPine   => T("city_leafpine", 0.200f, 0.335f, 0.237f, 0.20f);
        private static Material CyNeonRed    => T("city_neonred", 0.665f, 0.221f, 0.190f, 0.80f, 4.0f);    // authored 8.31
        private static Material CyPaintWhite => T("city_paintwhite", 0.774f, 0.777f, 0.774f, 0.38f);
        private static Material CyRed        => T("city_red", 0.722f, 0.215f, 0.183f, 0.70f);
        private static Material CyRender2    => T("city_render2", 0.710f, 0.631f, 0.578f, 0.22f);
        private static Material CyRender3    => T("city_render3", 0.578f, 0.631f, 0.679f, 0.22f);
        private static Material CyRoof0      => T("city_roof0", 0.346f, 0.335f, 0.335f, 0.14f);
        private static Material CyRoof1      => T("city_roof1", 0.447f, 0.299f, 0.244f, 0.14f);
        private static Material CyRubber     => T("city_rubber", 0.133f, 0.138f, 0.147f, 0.32f);
        private static Material CySignLit    => T("city_signlit", 0.748f, 0.748f, 0.735f, 0.70f, 2.2f);    // authored 3.32
        private static Material CySoil       => T("city_soil", 0.243f, 0.200f, 0.160f, 0.10f);
        private static Material CySteel      => T("city_steel", 0.584f, 0.593f, 0.610f, 0.64f);
        private static Material CyStucco     => T("city_stucco", 0.726f, 0.704f, 0.659f, 0.22f);
        private static Material CyTimber     => T("city_timber", 0.501f, 0.410f, 0.303f, 0.38f);
        private static Material CyTrim       => T("city_trim", 0.865f, 0.862f, 0.854f, 0.62f);
        private static Material CyTrimDk     => T("city_trimdk", 0.323f, 0.313f, 0.303f, 0.58f);
        private static Material CyTube       => T("city_tube", 0.896f, 0.901f, 0.906f, 0.80f, 2.0f);       // authored 3.19
        private static Material CyWall5      => T("city_wall5", 0.668f, 0.569f, 0.545f, 0.38f);
        private static Material CyWall7      => T("city_wall7", 0.711f, 0.668f, 0.592f, 0.38f);
        private static Material CyYellow     => T("city_yellow", 0.896f, 0.735f, 0.190f, 0.70f);
        // The signal lenses. Only the green one exists in the blend — the red
        // and amber are the dark M_City_SigOff piece, split per lamp head by
        // the exporter — so those two take the Downtown pack's lens colours
        // rather than a measurement of an unlit lens. All three carry emission
        // (glow > 0 enables the keyword) so SignalCycle can drive them through
        // MaterialPropertyBlocks.
        private static Material CySigGreen   => T("city_siggreen", 0.152f, 0.618f, 0.396f, 0.86f, 3.0f);   // authored 19.35
        private static Material CySigAmber   => T("city_sigamber", 0.95f, 0.55f, 0.10f, 0.85f, 0.3f);
        private static Material CySigRed     => T("city_sigred", 0.90f, 0.12f, 0.06f, 0.85f, 0.3f);

        // ---- per-theme token tables ------------------------------------------
        // One table per pack, passed whole to every MeshProp of that theme — an
        // object only ever contains its own tokens, so extra entries are inert.
        // ORDER IS LOAD-BEARING: AssignByName is first-match substring, so each
        // table is sorted longest-token-first (ghostdim before ghost, neongold
        // before gold, stonepale before stone). Rebuilt per call so a play-mode
        // change that kills the materials cannot leave stale references cached.

        private static (string, Material)[] DtTokens => new (string, Material)[]
        {
            ("neoncrimson", DtNeonCrim), ("concretelt", DtConcreteLt),
            ("neonorange", DtNeonOrange), ("neoncyan", DtNeonCyan),
            ("neongold", DtNeonGold), ("siggreen", DtSigGreen),
            ("sigamber", DtSigAmber), ("concrete", DtConcrete),
            ("crimson", DtCrimson), ("rocktop", DtRockTop),
            ("facade", DtFacade), ("basalt", DtBasalt), ("orange", DtOrange),
            ("rubber", DtRubber), ("sigred", DtSigRed), ("panel", DtPanel),
            ("steel", DtSteel), ("glass", DtGlass), ("white", DtWhite),
            ("gold", DtGold), ("lamp", DtLampHead), ("lava", DtLava),
            ("rock", DtRock),
        };

        private static (string, Material)[] ToyTokens => new (string, Material)[]
        {
            ("shadedesk", ToyShade), ("feltblue", ToyFelt),
            ("walnut", ToyWalnut), ("cotton", ToyCotton), ("purple", ToyPurple),
            ("orange", ToyOrangeM), ("yellow", ToyYellow), ("cream", ToyCream),
            ("green", ToyGreen), ("brass", ToyBrass), ("shade", ToyShade),
            ("paper", ToyPaper), ("book0", ToyBook0), ("book1", ToyBook1),
            ("book2", ToyBook2), ("book3", ToyBook3), ("book4", ToyBook4),
            ("book5", ToyBook5), ("book6", ToyBook6), ("bulb", ToyBulb),
            ("card", ToyCard), ("pine", ToyPine), ("blue", ToyBlue),
            ("red", ToyRed), ("ink", ToyInk), ("wax", ToyWax), ("ply", ToyPly),
        };

        private static (string, Material)[] EnchTokens => new (string, Material)[]
        {
            ("crystalrose", EnCrystal), ("stonemoss", EnStoneMoss),
            ("stonepale", EnStonePale), ("slateplum", EnSlatePlum),
            ("slateteal", EnSlateTeal), ("rockmoss", EnRockMoss),
            ("leafdark", EnLeafDark), ("blossom", EnBlossom),
            ("crimson", EnCrimson), ("plaster", EnPlaster),
            ("thatch", EnThatch), ("timber", EnTimber), ("window", EnWindow),
            ("azure", EnAzure), ("flame", EnFlame), ("hedge", EnHedge),
            ("slate", EnSlate), ("spray", EnSpray), ("stone", EnStone),
            ("water", EnWater), ("snow", EnSnow), ("rock", EnRock),
            ("gold", EnGold), ("iron", EnIron), ("leaf", EnLeaf),
            ("rose", EnRose), ("bark", EnBark),
        };

        private static (string, Material)[] HauntTokens => new (string, Material)[]
        {
            ("clapboard", HaClapboard), ("stonedark", HaStoneDark),
            ("verdigris", HaVerdigris), ("ghostdim", HaGhostDim),
            ("deadwood", HaDeadWood), ("pumpkin", HaPumpkin),
            ("shingle", HaShingle), ("rubble", HaRubble),
            ("candle", HaCandle), ("window", HaWindow), ("marble", HaMarble),
            ("flame", HaFlame), ("ghost", HaGhost), ("grime", HaGrime),
            ("stalk", HaStalk), ("earth", HaEarth), ("glass", HaGlass),
            ("moss", HaMoss), ("iron", HaIron), ("jack", HaJack),
            ("bark", HaBark), ("tar", HaTar),
        };

        private static (string, Material)[] CityTokens => new (string, Material)[]
        {
            // Fifty tokens, which is twice any other pack, and the eight
            // containments below are the whole reason the order is written out
            // rather than sorted alphabetically: brickpale⊃brick,
            // concretedk⊃concrete, glasslit/glassshop⊃glass, barkpine⊃bark,
            // siggreen⊃green, sigred/neonred⊃red, trimdk⊃trim.
            // TrackPresetValidator resolves every real FBX child name through
            // this table and FAILs when the first match is not the child's own
            // token, so a re-sort that breaks one of them cannot ship.
            ("paintwhite", CyPaintWhite), ("concretedk", CyConcreteDk),
            ("brickpale", CyBrickPale), ("leafhedge", CyLeafHedge),
            ("glassshop", CyGlassShop), ("barkpine", CyBarkPine),
            ("leafpine", CyLeafPine), ("siggreen", CySigGreen),
            ("sigamber", CySigAmber), ("interior", CyInterior),
            ("glasslit", CyGlassLit), ("asphalt", CyAsphalt),
            ("neonred", CyNeonRed), ("signlit", CySignLit),
            ("render2", CyRender2), ("render3", CyRender3),
            ("flower2", CyFlower2), ("flower3", CyFlower3),
            ("concrete", CyConcrete), ("chrome", CyChrome),
            ("stucco", CyStucco), ("rubber", CyRubber), ("timber", CyTimber),
            ("yellow", CyYellow), ("sigred", CySigRed), ("trimdk", CyTrimDk),
            ("wall5", CyWall5), ("wall7", CyWall7), ("roof0", CyRoof0),
            ("roof1", CyRoof1), ("leaf0", CyLeaf0), ("leaf1", CyLeaf1),
            ("leaf2", CyLeaf2), ("black", CyBlack), ("brick", CyBrick),
            ("cream", CyCream), ("glass", CyGlass), ("grass", CyGrass),
            ("green", CyGreen), ("steel", CySteel), ("trim", CyTrim),
            ("door", CyDoor), ("galv", CyGalv), ("lamp", CyLamp),
            ("tube", CyTube), ("blue", CyBlue), ("bark", CyBark),
            ("soil", CySoil), ("red", CyRed), ("alu", CyAlu),
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
