using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>Which token table a body shell binds its renderers against.</summary>
    public enum BodyTokens
    {
        /// <summary>No table: every renderer flattens onto the tintable body
        /// material. The three legacy shells.</summary>
        None,
        /// <summary>The shared <see cref="PartVisualFactory.AccentTokens"/> set
        /// the build_vehicles.py exports are named for.</summary>
        Accent,
        /// <summary>The Tiguan's own manifest-built table. Its pieces are named
        /// "tig*" and match nothing in the shared set.</summary>
        Tiguan,
    }

    /// <summary>
    /// One body shell as data: the key a save file will name it by, the mesh it
    /// loads, and every per-shape fact that is currently a <c>switch</c> on
    /// <see cref="BodyShape"/>.
    /// </summary>
    public sealed class BodyDef
    {
        /// <summary>The save key. <b>Never rename one</b> — it is what a design
        /// on disk carries.
        ///
        /// It is the FBX key for every body that has one, so an asset committed
        /// by Asset Studio names itself and there is no second name to invent or
        /// keep in step. The two primitive shapes have no asset, so they are the
        /// bare word: a key that said "body_box" would promise a file that has
        /// never existed.</summary>
        public string id;

        /// <summary>Picker text, and since K4 the ONLY source of it. Free to
        /// diverge from <see cref="id"/> — the point of a string key is that
        /// renaming what a player reads is not a save-format change — and free
        /// to diverge from <see cref="legacy"/>'s name too, which it could not
        /// be while the picker printed <c>shape.ToString()</c>.</summary>
        public string label;

        /// <summary>The <see cref="BodyShape"/> this key migrates from, and the
        /// int still written to every save file.
        ///
        /// This field is the migration. It exists so an old design resolves to a
        /// key and a new design still writes an int an old build can read; it is
        /// the one field expected to become dead weight, and deleting it is what
        /// finally retires <see cref="BodyShape"/>.</summary>
        public BodyShape legacy;

        /// <summary>The <c>Resources/PartModels/</c> key, or null for the shapes
        /// built out of primitives.</summary>
        public string meshKey;

        /// <summary>Drag coefficient and built-in downforce area (m²), per
        /// silhouette. A design's own <c>dragCdOverride</c>/<c>frontalAreaM2</c>
        /// still wins, which is how the Tiguan uses its published figures
        /// instead of the 0.80 sitting in its row.
        ///
        /// <b>ClA is what the shell does WITHOUT parts.</b> Wings, splitters and
        /// canards add their own on top, so a car that carries its downforce as
        /// authored geometry has a small number here and a large one after
        /// <c>TotalClA</c> — which is why the two race cars' 0.007/0.008 look
        /// modest beside their actual behaviour.</summary>
        public float cd, clA;

        /// <summary>Whether the garage's paint mode can work on this body — i.e.
        /// whether any renderer ends up on the tintable material. False for the
        /// baked liveries, whose finish IS the artwork.
        ///
        /// A design fact, not a machine fact. <see cref="CarVehicle.HasPaintableBody"/>
        /// also returns false when the FBX did not ship, which is a different
        /// question with the same answer; <c>[AKEY]</c> compares this against it
        /// with the asset check folded back in.</summary>
        public bool paintable;

        /// <summary>Which token table binds this shell's renderers.</summary>
        public BodyTokens tokens;

        /// <summary>Authored 1:1: instantiated at scale 1 rather than
        /// bodySize/authorSize. See <see cref="CarVehicle.BodyRenderScale"/> for
        /// why this is not merely "it happens to already be the right size".</summary>
        public bool unscaled;

        /// <summary>Cosmetic mounts measure the SHELL only, skipping wings,
        /// booms and face rigs by name token — otherwise a hat lands in mid-air
        /// over the wing.</summary>
        public bool foldedAppendages;

        /// <summary>The design bodySize this shell is authored for. Nominal, not
        /// measured: for the arcade shells it is
        /// <see cref="CarVehicle.BodyMeshAuthorSize"/>, the divisor no shell
        /// actually is; for the Tiguan it is the published body box.</summary>
        public Vector3 nominalSize;

        /// <summary>
        /// The single factor between the mesh as it was exported and the game's
        /// scale — 1 for every shell the old exporter already corrected, and
        /// 1/12.573 for something like the police car, which arrives at its
        /// Blender size because the current exporter applies no correction.
        ///
        /// It MULTIPLIES the bodySize divide rather than replacing it, which is
        /// what keeps the two ideas separate: this one says how big the mesh is,
        /// the divide says how big the design wants the car. Uniform, because the
        /// old pipeline was uniform and proportions were never altered.
        /// </summary>
        public float authorScale = 1f;

        /// <summary>Hidden from every picker. A reference vehicle, not content.
        ///
        /// Authored, but no longer arbitrary: <c>[AKEY]</c> holds it to
        /// <see cref="BodyCatalog.Buildable"/>, so the flag has to agree with
        /// whether the garage's own size sliders can reach this shell at all.
        /// That is what K4 replaced the hard-coded <c>s != BodyShape.Tiguan</c>
        /// with — an offered body the player cannot size is a body you can pick
        /// and cannot use.</summary>
        public bool debugOnly;
    }

    /// <summary>
    /// The bodies the game can build, as a table instead of six switches.
    ///
    /// <b>This is the live path.</b> It was transcribed from those switches and
    /// checked against them by <c>[AKEY]</c> while they still ran, so the
    /// transcription was proved before anything depended on it; K3 then moved
    /// every consumer here and deleted them, and K4 moved the garage's picker.
    /// A row is now the whole answer about a body — what it renders as, what it
    /// drags like, what it is called, and whether it is offered at all.
    ///
    /// <b>Order is the picker order</b> and matches <see cref="BodyShape"/>'s
    /// declaration order, which is also the persisted int order. Do not reorder;
    /// append.
    ///
    /// <b><see cref="Seed"/> and <see cref="All"/> are different tables, and the
    /// difference is the point of C1b.</b> Seed is the shipped thirteen and is
    /// what the structural invariants are about — one row per enum value, in
    /// enum order. All is Seed plus every body Asset Studio has committed,
    /// discovered from the manifests themselves so there is no registry file
    /// that could disagree with what is on disk. Those rows have no enum value
    /// at all, which is exactly what "a new car without a code change" means, so
    /// holding them to Seed's invariants would refuse the thing this exists to
    /// allow. Ask Seed about the enum; ask All about a key.
    /// </summary>
    public static class BodyCatalog
    {
        private static BodyDef B(string id, BodyShape legacy, string meshKey,
            float cd, float clA, bool paintable, BodyTokens tokens) => new BodyDef
        {
            id = id, label = legacy.ToString(), legacy = legacy, meshKey = meshKey,
            cd = cd, clA = clA, paintable = paintable, tokens = tokens,
            unscaled = false, foldedAppendages = false,
            nominalSize = CarVehicle.BodyMeshAuthorSize, debugOnly = false,
        };

        /// <summary>
        /// The seed table. Every number in it was transcribed from a switch that
        /// owned it — <c>BodyMeshKey</c>, <c>HasPaintableBody</c>,
        /// <c>BodyAccentTable</c>, <c>BodyRenderScale</c>,
        /// <c>AeroDynamics.BodyCd</c>/<c>BodyClA</c>,
        /// <c>CosmeticMounts.HasFoldedAppendages</c> — and compared against it by
        /// <c>[AKEY]</c> before K3 deleted the six of them. What <c>[AKEY]</c>
        /// checks now is what a table can still be wrong about alone: unique keys
        /// and labels, one row per enum value, plausible aero, and the flags
        /// agreeing with the things they claim to describe.
        ///
        /// The shipped thirteen and only those. Committed bodies join
        /// <see cref="All"/>, not this.
        /// </summary>
        public static readonly BodyDef[] Seed =
        {
            // The two primitive compounds: no asset, no accents, and no sane UVs,
            // which is why paint mode stands down rather than painting a box.
            B("box",   BodyShape.Box,   null, 0.90f, 0f,     false, BodyTokens.None),
            B("wedge", BodyShape.Wedge, null, 0.65f, 0.002f, false, BodyTokens.None),

            // The three original shells. No token table at all: every renderer
            // flattens onto the tintable body material, which is what makes them
            // the only shapes whose whole body takes the design's colour.
            B("body_buggy",    BodyShape.Buggy,    "body_buggy",    0.80f, 0f,     true, BodyTokens.None),
            B("body_shell",    BodyShape.Shell,    "body_shell",    0.45f, 0.004f, true, BodyTokens.None),
            B("body_lowracer", BodyShape.LowRacer, "body_lowracer", 0.55f, 0.006f, true, BodyTokens.None),

            // The three TinyTorque show cars. Baked liveries: the artwork is the
            // finish, so there is no tintable panel and paint mode stands down.
            B("body_coupe",  BodyShape.Coupe,  "body_coupe",  0.48f, 0.004f, false, BodyTokens.Accent),
            B("body_baja",   BodyShape.Baja,   "body_baja",   0.85f, 0f,     false, BodyTokens.Accent),
            B("body_patrol", BodyShape.Patrol, "body_patrol", 0.55f, 0.003f, false, BodyTokens.Accent),

            // The four Legendary cars. Their wings, booms and face rigs are
            // folded out of the cosmetic mount box.
            //
            // Cd read off the silhouettes (this reasoning came over from
            // AeroDynamics.BodyCd at K3b, which is where it used to live): a
            // slab-sided wrecker with a boom in the airstream is the draggiest
            // thing in the game; the two race cars are clean noses spoiled by
            // exposed wings; the Autopia is a 1955 pontoon body with an upright
            // wraparound screen and no roof. Both race cars carry their wing as
            // authored geometry, which is the whole of their downforce; the
            // wrecker and the ride car have none, like every other bluff shape.
            F(B("body_rattle",   BodyShape.Rattle,   "body_rattle",   0.95f, 0f,     false, BodyTokens.Accent)),
            F(B("body_redline",  BodyShape.Redline,  "body_redline",  0.52f, 0.007f, true,  BodyTokens.Accent)),
            F(B("body_highwing", BodyShape.Highwing, "body_highwing", 0.58f, 0.008f, true,  BodyTokens.Accent)),
            F(B("body_autopia",  BodyShape.Autopia,  "body_autopia",  0.72f, 0f,     true,  BodyTokens.Accent)),

            // The 1:1 reference car. Cd and frontal area are PUBLISHED and the
            // design overrides them, so the 0.80 default below is the table value
            // that never gets used — recorded because the table has an entry for
            // every shape, not because anything reads it.
            new BodyDef
            {
                id = "body_tiguan", label = "VW Tiguan", legacy = BodyShape.Tiguan,
                meshKey = "body_tiguan", cd = 0.80f, clA = 0f, paintable = false,
                tokens = BodyTokens.Tiguan, unscaled = true, foldedAppendages = false,
                nominalSize = new Vector3(1.839f, 1.443f, 4.486f), debugOnly = true,
            },
        };

        private static BodyDef F(BodyDef d) { d.foldedAppendages = true; return d; }

        private static BodyDef[] _all;

        /// <summary>
        /// The seed table plus every body Asset Studio has committed —
        /// <b>the point of the whole exercise</b>. A new car is now a folder of
        /// art and a manifest, not a C# edit.
        ///
        /// Seed first and seed wins, so a committed asset can never redefine a
        /// shipped body out from under every save that names it. Discovery is by
        /// manifest, so there is no registry file that could disagree with what
        /// is actually on disk.
        /// </summary>
        public static BodyDef[] All => _all ??= Compose();

        private static BodyDef[] Compose()
        {
            var list = new List<BodyDef>(Seed);
            var taken = new HashSet<string>();
            foreach (BodyDef d in Seed) taken.Add(d.id);

            foreach (AssetManifest m in AssetManifests.Discover())
            {
                if (m == null || m.kind != AssetKinds.CarBody) continue;
                if (!taken.Add(m.key))
                {
                    Debug.LogWarning($"[BodyCatalog] '{m.key}' is already a shipped body; the " +
                                     "committed manifest is ignored. A save that names this key " +
                                     "has always meant the shipped car and still does.");
                    continue;
                }
                list.Add(FromManifest(m));
            }
            return list.ToArray();
        }

        /// <summary>
        /// A committed manifest as a row.
        ///
        /// Three fields are worth reading twice.
        /// <see cref="BodyDef.legacy"/> is <c>Box</c> because there IS no enum
        /// value — this body was added without a code change, which is the whole
        /// idea, and the cost is that an older build reading the int beside the
        /// key gets a box. Stated in <see cref="Resolve"/> and unfixable with
        /// <c>JsonUtility</c>.
        /// <see cref="BodyDef.tokens"/> is <c>None</c> and is never consulted: a
        /// manifest asset is bound by <c>AssetManifestBinder</c> per object and
        /// slot, and <c>BindByToken</c> hands off before it reads a table.
        /// <see cref="BodyDef.paintable"/> is DERIVED from whether any material
        /// claims the paint channel — the same fact, asked once rather than
        /// authored twice — and is false for a Verbatim asset, which has no
        /// material the game built and therefore nothing to tint.
        /// </summary>
        private static BodyDef FromManifest(AssetManifest m) => new BodyDef
        {
            id = m.key,
            label = m.Label,
            legacy = BodyShape.Box,
            meshKey = m.key,
            cd = m.vehicle != null && m.vehicle.cd >= 0f ? m.vehicle.cd : 0.80f,
            clA = m.vehicle?.clA ?? 0f,
            paintable = m.HasPaintChannel && !m.IsVerbatim,
            tokens = BodyTokens.None,
            unscaled = false,
            foldedAppendages = false,
            nominalSize = CarVehicle.BodyMeshAuthorSize,
            debugOnly = false,
            authorScale = m.authorScale > 0f ? m.authorScale : 1f,
        };

        /// <summary>Forget the composed table and every lookup built from it.
        /// For the commit pipeline, which has just written a manifest naming a
        /// key this session has already decided does not exist.</summary>
        public static void ResetCache()
        {
            _all = null;
            _offered = null;
            _byId = null;
            _warned = null;
        }

        /// <summary>
        /// The garage's body-size slider range: the smallest and largest car a
        /// player can build. Width, height, length, in metres.
        ///
        /// It lives here rather than in <c>GarageUI</c> because it is what makes
        /// <see cref="BodyDef.debugOnly"/> a DERIVED fact rather than a name.
        /// The UI still owns how the sliders look; it no longer owns what they
        /// reach, because that is a statement about which bodies are content.
        /// </summary>
        public static readonly Vector3 SizeMin = new Vector3(0.12f, 0.04f, 0.25f);

        /// <summary>The other end of <see cref="SizeMin"/>.</summary>
        public static readonly Vector3 SizeMax = new Vector3(0.35f, 0.18f, 0.60f);

        /// <summary>
        /// Whether the garage's sliders can reach this shell's nominal size —
        /// i.e. whether a player could build the car this shell is authored for.
        ///
        /// The Tiguan is 1.839 x 1.443 x 4.486 m against a 0.35 m ceiling, so it
        /// fails on every axis; every arcade shell is authored at
        /// <see cref="CarVehicle.BodyMeshAuthorSize"/>, which is mid-range on all
        /// three. Nothing is close to the boundary, which is the point: this is a
        /// test for "a different pipeline entirely", not a fussy tolerance.
        /// </summary>
        public static bool Buildable(BodyDef d) =>
            d != null
            && d.nominalSize.x >= SizeMin.x && d.nominalSize.x <= SizeMax.x
            && d.nominalSize.y >= SizeMin.y && d.nominalSize.y <= SizeMax.y
            && d.nominalSize.z >= SizeMin.z && d.nominalSize.z <= SizeMax.z;

        /// <summary>The rows a picker offers, in table order. Everything that is
        /// not a reference vehicle.</summary>
        public static BodyDef[] Offered =>
            _offered ??= System.Array.FindAll(All, d => !d.debugOnly);

        private static BodyDef[] _offered;

        private static Dictionary<string, BodyDef> _byId;

        /// <summary>The entry for a save key, or null.</summary>
        public static BodyDef ById(string id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<string, BodyDef>(All.Length);
                // First wins, so a seed entry always beats a later addition of
                // the same key: a committed asset must not be able to redefine a
                // shipped body out from under every save that names it.
                foreach (var d in All)
                    if (!_byId.ContainsKey(d.id)) _byId.Add(d.id, d);
            }
            return id != null && _byId.TryGetValue(id, out var def) ? def : null;
        }

        /// <summary>The entry for a legacy enum value, or null. This is the
        /// direction K2's migration reads: an old save carries the int.</summary>
        public static BodyDef ByLegacy(BodyShape s)
        {
            foreach (var d in All) if (d.legacy == s) return d;
            return null;
        }

        private static HashSet<string> _warned;

        /// <summary>
        /// What a saved design means, from the pair it carries: the key if it has
        /// one this build knows, else the legacy enum, else the box.
        /// <b>Never null</b> — a design always renders as something.
        ///
        /// The key WINS when both are present and disagree. That is the whole
        /// point of the pair: <see cref="BodyDef.legacy"/> can only name a shape
        /// that was compiled in, so a body added by Asset Studio has to be able
        /// to override it.
        ///
        /// An unknown key is warned about once per key and then ignored, which is
        /// the downgrade case: a newer build saved a body this one has never
        /// heard of. The legacy int it also wrote is the best remaining answer,
        /// and for a genuinely new asset that answer is Box — there was no enum
        /// value to write. Nothing can fix that; the warning is so it is not a
        /// silent box.
        ///
        /// The final fallback matches <see cref="CarVehicle.BodyMeshKey"/>'s
        /// <c>_ => null</c>: an out-of-range enum has always built the primitive.
        /// </summary>
        public static BodyDef Resolve(string key, BodyShape legacy)
        {
            if (!string.IsNullOrEmpty(key))
            {
                BodyDef byKey = ById(key);
                if (byKey != null) return byKey;
                _warned ??= new HashSet<string>();
                if (_warned.Add(key))
                    Debug.LogWarning($"[BodyCatalog] Unknown body key '{key}'; falling back to " +
                                     $"bodyShape {legacy}. A newer build may have saved this design.");
            }
            return ByLegacy(legacy) ?? ById("box");
        }
    }
}
