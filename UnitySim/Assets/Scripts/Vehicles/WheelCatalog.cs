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

        /// <summary>Picker text, and since K4 the only copy of it. It was
        /// written out by hand in three places — here,
        /// <c>GarageUI.DrawWheelInspector</c> and <c>ShowroomUI.WheelNames</c> —
        /// which is how the garage and the showroom could have disagreed about
        /// what style 10 is called.</summary>
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
    /// The wheel styles the game can build, as a table instead of four switches
    /// and two hand-copied name arrays.
    ///
    /// <b>This is the live path</b> — see <see cref="BodyCatalog"/> for the order
    /// it was proved in, and for the seed-plus-lookup shape it copies. K3c took
    /// the four switches, K4 the two name arrays and the garage's offered list.
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
        /// <c>IsFullScale</c>, and from the garage's <c>offered</c> list — all
        /// five compared against it by <c>[AKEY]</c> before K3c and K4 deleted
        /// them. What is checked now is the table's own consistency: unique keys
        /// and labels, <c>legacy == index</c>, the two author radii, and the two
        /// picker flags against the facts they stand for.
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

        /// <summary>The slick — what a wheel is when nothing said otherwise.
        /// Named rather than written as <c>All[0]</c> at each site, because
        /// "index zero" is not a reason and this is a choice three callers make
        /// (the part ghost, the palette icon, and any corrupt save).</summary>
        public static WheelDef Default => All[0];

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

        private static HashSet<string> _warned;

        /// <summary>
        /// What a saved wheel means, from the pair it carries: the key if it has
        /// one this build knows, else the legacy int, else the slick.
        /// <b>Never null.</b> See <see cref="BodyCatalog.Resolve"/> — same rules,
        /// same reasons, and the key wins for the same reason.
        ///
        /// The last fallback is where this and <see cref="ByLegacy"/> part
        /// company on purpose: the table declines to say what style 47 is, and
        /// this says "the slick", because that is what
        /// <c>WheelStyleKey</c> has always rendered for it.
        /// </summary>
        public static WheelDef Resolve(string key, int legacy)
        {
            if (!string.IsNullOrEmpty(key))
            {
                WheelDef byKey = ById(key);
                if (byKey != null) return byKey;
                _warned ??= new HashSet<string>();
                if (_warned.Add(key))
                    UnityEngine.Debug.LogWarning(
                        $"[WheelCatalog] Unknown wheel key '{key}'; falling back to wheelStyle " +
                        $"{legacy}. A newer build may have saved this design.");
            }
            return ByLegacy(legacy) ?? All[0];
        }
    }
}
