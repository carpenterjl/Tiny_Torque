using System.Collections.Generic;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// One wheel style as data: the key a save file will name it by, the mesh it
    /// loads, the radius that mesh renders unscaled at, and the finish — if any —
    /// applied over it.
    /// </summary>
    public sealed class WheelDef
    {
        /// <summary>The save key. <b>Never rename one.</b>
        ///
        /// It is the FBX key wherever a style IS a mesh. Three styles are not:
        /// chrome, gold and neon are re-tints of the slick, and their keys say so
        /// rather than pretending to name a file. That is the whole reason a
        /// wheel needs a key of its own instead of just an FBX name.</summary>
        public string id;

        /// <summary>Picker text. Currently duplicated by hand in
        /// <c>GarageUI.DrawWheelInspector</c> and <c>ShowroomUI.WheelNames</c>;
        /// K4 deletes both and reads this.</summary>
        public string label;

        /// <summary>The <c>wheelStyle</c> int this key migrates from, still
        /// written to every save file and to <c>VehicleLoadout</c>. Append-only:
        /// an old save's 7 must stay gold forever.</summary>
        public int legacy;

        /// <summary>The <c>Resources/PartModels/</c> key. Never null — a missing
        /// asset falls back to the primitive wheel at runtime, which is a fact
        /// about the machine, not about the style.</summary>
        public string meshKey;

        /// <summary>The radius at which <see cref="meshKey"/> renders unscaled.
        /// 33 mm for every arcade tyre (the exporter rescales them all); the
        /// Tiguan's is its LOADED centre height, which is also the number the
        /// design gives the WheelCollider.</summary>
        public float authorRadius;

        /// <summary>The rim re-tint applied over the mesh, or None.</summary>
        public WheelFinish finish;

        /// <summary>Authored 1:1, and therefore bound against the Tiguan's own
        /// token table rather than the shared wheel set.</summary>
        public bool fullScale;

        /// <summary>Whether the garage's style button cycles to it. The three
        /// finishes are showroom unlockables, so the garage skips them; a design
        /// already carrying one still displays it.</summary>
        public bool garageOffered;

        /// <summary>Hidden from every picker.</summary>
        public bool debugOnly;
    }

    /// <summary>
    /// The wheel styles the game can build, as a table instead of three switches
    /// and two hand-copied name arrays.
    ///
    /// <b>Nothing calls this yet</b> — see <see cref="BodyCatalog"/> for why, and
    /// for the seed-plus-lookup shape it copies.
    ///
    /// <b>Order is the persisted int order.</b> Do not reorder; append.
    /// </summary>
    public static class WheelCatalog
    {
        private static WheelDef W(string id, string label, int legacy, string meshKey,
            bool garageOffered) => new WheelDef
        {
            id = id, label = label, legacy = legacy, meshKey = meshKey,
            authorRadius = PartVisualFactory.WheelAuthorRadius,
            finish = WheelFinish.None, fullScale = false,
            garageOffered = garageOffered, debugOnly = false,
        };

        /// <summary>
        /// The seed table, transcribed from <c>PartVisualFactory</c>'s
        /// <c>WheelStyleKey</c>, <c>AuthorRadiusFor</c>, <c>FinishFor</c> and
        /// <c>IsFullScale</c>, and from the garage's <c>offered</c> list. Every
        /// one of those is re-read and compared by <c>[AKEY]</c>.
        /// </summary>
        public static readonly WheelDef[] All =
        {
            W("wheel_slick",    "Slick",   0, "wheel_slick",    true),
            W("wheel_knobby",   "Knobby",  1, "wheel_knobby",   true),
            W("wheel_rally",    "Rally",   2, "wheel_rally",    true),
            W("wheel_coupe",    "Coupe",   3, "wheel_coupe",    true),
            W("wheel_baja",     "Baja",    4, "wheel_baja",     true),
            W("wheel_patrol",   "Steelie", 5, "wheel_patrol",   true),

            // The three showroom finishes: the slick's mesh, re-tinted. Not
            // offered in the garage — they are unlocked, not designed.
            Fin(W("slick_chrome", "Chrome", 6, "wheel_slick", false), WheelFinish.Chrome),
            Fin(W("slick_gold",   "Gold",   7, "wheel_slick", false), WheelFinish.Gold),
            Fin(W("slick_neon",   "Neon",   8, "wheel_slick", false), WheelFinish.Neon),

            // The four Legendary wheels: their own meshes with their own authored
            // materials, which is why no finish may touch them.
            W("wheel_rattle",   "Rusted",      9, "wheel_rattle",   true),
            W("wheel_redline",  "Race gold",  10, "wheel_redline",  true),
            W("wheel_highwing", "Five-spoke", 11, "wheel_highwing", true),
            W("wheel_autopia",  "Whitewall",  12, "wheel_autopia",  true),

            // The Tiguan's two. They differ ONLY in the brake disc — vented
            // 340x30 front against solid 300x12 rear — which, once the calipers
            // are dropped, is the only brake hardware still visible through the
            // spokes. One style for both would put a front disc on both rear
            // corners.
            Full("wheel_tiguan",   "Tiguan front", 13, "wheel_tiguan"),
            Full("wheel_tiguan_r", "Tiguan rear",  14, "wheel_tiguan_r"),
        };

        private static WheelDef Fin(WheelDef d, WheelFinish f) { d.finish = f; return d; }

        private static WheelDef Full(string id, string label, int legacy, string meshKey)
        {
            var d = W(id, label, legacy, meshKey, false);
            d.authorRadius = PartVisualFactory.TiguanWheelAuthorRadius;
            d.fullScale = true;
            d.debugOnly = true;
            return d;
        }

        private static Dictionary<string, WheelDef> _byId;

        /// <summary>The entry for a save key, or null.</summary>
        public static WheelDef ById(string id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<string, WheelDef>(All.Length);
                foreach (var d in All)
                    if (!_byId.ContainsKey(d.id)) _byId.Add(d.id, d);
            }
            return id != null && _byId.TryGetValue(id, out var def) ? def : null;
        }

        /// <summary>
        /// The entry for a legacy style int, or null.
        ///
        /// <b>Null is a real answer</b>, and the live path disagrees with it on
        /// purpose: <c>WheelStyleKey</c> maps every unknown int to the slick, so
        /// a corrupt save renders rather than throwing. K2's migration keeps that
        /// by treating null as "fall back to the slick", but the catalogue itself
        /// declines to claim it knows what style 47 is.
        /// </summary>
        public static WheelDef ByLegacy(int style)
        {
            foreach (var d in All) if (d.legacy == style) return d;
            return null;
        }
    }
}
