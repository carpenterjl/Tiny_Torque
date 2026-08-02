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

        /// <summary>Picker text. Free to diverge from <see cref="id"/> — the
        /// point of a string key is that renaming what a player reads is not a
        /// save-format change.</summary>
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
        /// silhouette. A design's own <c>dragCdOverride</c> still wins.</summary>
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

        /// <summary>Hidden from every picker. A reference vehicle, not content.</summary>
        public bool debugOnly;
    }

    /// <summary>
    /// The bodies the game can build, as a table instead of six switches.
    ///
    /// <b>Nothing calls this yet.</b> It is transcribed from the live switches
    /// and checked against them by <c>[AKEY]</c> while they are still the live
    /// path — so the transcription is proved before anything depends on it, and
    /// a wrong number here cannot reach a car. K3 moves the consumers over one
    /// at a time; until then this file is a claim under test.
    ///
    /// Modelled on <c>CosmeticCatalog</c>: a hard-coded seed array plus a
    /// dictionary lookup, no scanning. Discovery of committed manifests belongs
    /// with the commit pipeline that writes them, not here.
    ///
    /// <b>Order is the picker order</b> and matches <see cref="BodyShape"/>'s
    /// declaration order, which is also the persisted int order. Do not reorder;
    /// append.
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
        /// The seed table. Every number is transcribed from the switch that
        /// still owns it — <c>BodyMeshKey</c>, <c>HasPaintableBody</c>,
        /// <c>BodyAccentTable</c>, <c>BodyRenderScale</c>,
        /// <c>AeroDynamics.BodyCd</c>/<c>BodyClA</c>,
        /// <c>CosmeticMounts.HasFoldedAppendages</c> — and every one of those is
        /// re-read and compared by <c>[AKEY]</c>.
        /// </summary>
        public static readonly BodyDef[] All =
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
