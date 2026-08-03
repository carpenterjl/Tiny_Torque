using System.Collections.Generic;
using AIHWSim.Garage;

namespace AIHWSim.Persistence
{
    public enum UnlockKind
    {
        Car,        // payload unused; the item id maps to a preset display name
        Horn,       // payload = VehicleDesign.hornStyle
        WheelStyle, // payload = WheelSpec.wheelStyle; see UnlockItem.wheelKey
        Topper,     // payload = VehicleLoadout roof-kit slot index
        AeroKit,    // payload = VehicleLoadout aero kit index
        Paint,      // payload = index into the premium paint palette
        Cosmetic,   // payload unused; the item id IS the CosmeticCatalog id
    }

    public sealed class UnlockItem
    {
        public string id;        // stable save-file key — never rename
        public string display;   // what the crate reveal shows
        public UnlockKind kind;
        public int payload;
        public string code;      // the cheat that unlocks it (lowercase, no spaces)

        /// <summary>For <see cref="UnlockKind.WheelStyle"/> only: the
        /// WheelCatalog key this item sells. The showroom used to find the lock
        /// for a style by building "wheel_style_{v}" out of the int and testing
        /// v >= 6 — the same range test that nearly painted four Legendary
        /// wheels neon pink, and one an Asset Studio wheel could not satisfy at
        /// all, having no int. The item names the wheel now, and the showroom
        /// looks it up. <see cref="id"/> stays what it was: it is a save key.</summary>
        public string wheelKey;
        public string blurb;     // one-liner for the reveal / cheat flourish
        public Rarity rarity;    // which crate tier can pay it out
    }

    /// <summary>
    /// Everything a crate (or the right magic word) can hand over: the 20
    /// original unlocks plus all 47 TinyTorque cosmetics, in ONE pool with one
    /// save key space.
    ///
    /// The 20 originals were previously untiered because the old system rolled
    /// uniformly over whatever was still locked. Crates roll a RARITY first, so
    /// every item needs a tier; the ones assigned below follow the same logic
    /// the pack uses — the show cars and the loudest finishes at the top, the
    /// roof kits at the bottom.
    ///
    /// Always free and NOT in here: the stock design, the ★ TT Coupe (the
    /// starter car), every user-saved design, wheel styles 0-5 (generic +
    /// show-car), antenna styles 0-1, and the standard paint palette.
    ///
    /// The cheat codes live only in this file, on purpose — finding them is the
    /// fun. Codes are matched after trim/lowercase/de-space. Cosmetics have no
    /// codes: 47 more would turn a secret into a phone book, and the shop sells
    /// any of them for scrap anyway.
    /// </summary>
    public static class UnlockCatalog
    {
        private static readonly UnlockItem[] Legacy =
        {
            // ---- cars (VehiclePresets display names, sans ★ prefix) ----
            new UnlockItem { id = "car_patrol", display = "TT Patrol", kind = UnlockKind.Car,
                rarity = Rarity.Epic,
                code = "donut", blurb = "To protect and to serve (donuts)." },
            new UnlockItem { id = "car_baja", display = "TT Baja", kind = UnlockKind.Car,
                rarity = Rarity.Epic,
                code = "bajablast", blurb = "Desert-rated. Hydration not included." },
            new UnlockItem { id = "car_realtwin", display = "Real Twin 1/10", kind = UnlockKind.Car,
                rarity = Rarity.Legendary,
                code = "twinning", blurb = "The calibration car. It's twinning time." },
            new UnlockItem { id = "car_opus", display = "Opus Vector", kind = UnlockKind.Car,
                rarity = Rarity.Legendary,
                code = "magnumopus", blurb = "The masterpiece. Drives itself, if you let it." },

            // ---- the Legendary four ----
            // Every one of these is a whole authored car with its own body mesh,
            // its own wheel and its own material set, so they all sit at the top
            // tier. The three character cars keep their faces.
            new UnlockItem { id = "car_rattletrap", display = "TT Rattletrap", kind = UnlockKind.Car,
                rarity = Rarity.Legendary,
                code = "rustnever", blurb = "Rust never sleeps. Neither does the hook." },
            new UnlockItem { id = "car_redline", display = "TT Redline", kind = UnlockKind.Car,
                rarity = Rarity.Legendary,
                code = "luckyseven", blurb = "Number seven, and it knows it." },
            new UnlockItem { id = "car_highwing", display = "TT Highwing", kind = UnlockKind.Car,
                rarity = Rarity.Legendary,
                code = "cleanair", blurb = "Wing up where the air is clean." },
            new UnlockItem { id = "car_autopia", display = "TT Autopia", kind = UnlockKind.Car,
                rarity = Rarity.Legendary,
                code = "nineteenfiftyfive", blurb = "Please keep hands and arms inside the vehicle." },

            // ---- horns ----
            new UnlockItem { id = "horn_siren", display = "Police siren", kind = UnlockKind.Horn, payload = 1,
                rarity = Rarity.Uncommon,
                code = "pullover", blurb = "It's what the horn says." },
            new UnlockItem { id = "horn_truck", display = "Air horn", kind = UnlockKind.Horn, payload = 2,
                rarity = Rarity.Uncommon,
                code = "convoy", blurb = "Breaker one-nine, tiny truck coming through." },
            new UnlockItem { id = "horn_musical", display = "Musical horn", kind = UnlockKind.Horn, payload = 3,
                rarity = Rarity.Rare,
                code = "freebird", blurb = "The eternal encore request." },
            new UnlockItem { id = "horn_clown", display = "Clown horn", kind = UnlockKind.Horn, payload = 4,
                rarity = Rarity.Rare,
                code = "clowncar", blurb = "It's a clown car now. No refunds." },

            // ---- wheel finishes (styles 6-8, appended tint variants) ----
            new UnlockItem { id = "wheel_style_6", display = "Chrome wheels", kind = UnlockKind.WheelStyle, payload = 6, wheelKey = "slick_chrome",
                rarity = Rarity.Rare,
                code = "hubcapital", blurb = "The hub capital of the world." },
            new UnlockItem { id = "wheel_style_7", display = "Gold wheels", kind = UnlockKind.WheelStyle, payload = 7, wheelKey = "slick_gold",
                rarity = Rarity.Epic,
                code = "rollmodel", blurb = "Be a roll model." },
            new UnlockItem { id = "wheel_style_8", display = "Neon wheels", kind = UnlockKind.WheelStyle, payload = 8, wheelKey = "slick_neon",
                rarity = Rarity.Epic,
                code = "glowgetter", blurb = "A real glow-getter." },

            // ---- roof kits (VehicleLoadout slots; see Progression.ApplyLoadout) ----
            new UnlockItem { id = "antenna_style_2", display = "Flag antenna", kind = UnlockKind.Topper, payload = 3,
                rarity = Rarity.Uncommon,
                code = "flagship", blurb = "The flagship topper." },
            new UnlockItem { id = "antenna_style_3", display = "Twin whips", kind = UnlockKind.Topper, payload = 4,
                rarity = Rarity.Uncommon,
                code = "doubletrouble", blurb = "Two of them. Double the trouble." },
            new UnlockItem { id = "light_style_0", display = "Roof light bar", kind = UnlockKind.Topper, payload = 1,
                rarity = Rarity.Common,
                code = "lightsout", blurb = "Lights out and away we go." },
            new UnlockItem { id = "light_style_1", display = "Off-road pods", kind = UnlockKind.Topper, payload = 2,
                rarity = Rarity.Common,
                code = "podrace", blurb = "Now THIS is pod racing." },

            // ---- aero kits ----
            new UnlockItem { id = "aero_kit_street", display = "Street aero kit", kind = UnlockKind.AeroKit, payload = 1,
                rarity = Rarity.Rare,
                code = "groundeffect", blurb = "Splitter + wing. Affects handling — really." },
            new UnlockItem { id = "aero_kit_track", display = "Track aero kit", kind = UnlockKind.AeroKit, payload = 2,
                rarity = Rarity.Epic,
                code = "downforce", blurb = "Maximum attack. Also affects handling." },

            // ---- premium paints (indices into Progression.PaintPalette's tail) ----
            new UnlockItem { id = "paint_gold", display = "Champagne gold paint", kind = UnlockKind.Paint, payload = 0,
                rarity = Rarity.Uncommon,
                code = "midastouch", blurb = "Everything he touched…" },
            new UnlockItem { id = "paint_midnight", display = "Midnight navy paint", kind = UnlockKind.Paint, payload = 1,
                rarity = Rarity.Rare,
                code = "navyseal", blurb = "Navy, sealed coat." },
            new UnlockItem { id = "paint_hotpink", display = "Hot pink paint", kind = UnlockKind.Paint, payload = 2,
                rarity = Rarity.Rare,
                code = "flamingo", blurb = "Pink, leggy, surprisingly fast." },
        };

        private static UnlockItem[] _all;

        /// <summary>Legacy unlocks + every cosmetic, composed once. The cosmetic
        /// entries borrow their id straight from CosmeticCatalog so one string
        /// is the save key, the catalog key AND the FBX stem.</summary>
        public static UnlockItem[] All
        {
            get
            {
                if (_all != null) return _all;
                var list = new List<UnlockItem>(Legacy.Length + CosmeticCatalog.All.Length);
                list.AddRange(Legacy);
                foreach (var c in CosmeticCatalog.All)
                    list.Add(new UnlockItem
                    {
                        id = c.id,
                        display = c.label,
                        kind = UnlockKind.Cosmetic,
                        rarity = c.rarity,
                        blurb = c.description,
                        code = null,
                    });
                _all = list.ToArray();
                return _all;
            }
        }

        /// <summary>What a duplicate pays and what the shop charges. Cosmetics
        /// carry their own (identical) numbers from the manifest; the legacy
        /// items are priced off the same table so one tier costs one price
        /// whatever kind of thing it is.</summary>
        public static int DupeValue(UnlockItem i) =>
            i != null && i.kind == UnlockKind.Cosmetic && CosmeticCatalog.ById(i.id) != null
                ? CosmeticCatalog.ById(i.id).dupeValue
                : CosmeticCatalog.DupeValueFor(i?.rarity ?? Rarity.Common);

        public static int DirectCost(UnlockItem i) =>
            i != null && i.kind == UnlockKind.Cosmetic && CosmeticCatalog.ById(i.id) != null
                ? CosmeticCatalog.ById(i.id).directCost
                : CosmeticCatalog.DirectCostFor(i?.rarity ?? Rarity.Common);

        private static Dictionary<string, UnlockItem> _byId;
        private static Dictionary<string, UnlockItem> _byCode;

        public static UnlockItem ById(string id)
        {
            _byId ??= Build(i => i.id);
            return id != null && _byId.TryGetValue(id, out var item) ? item : null;
        }

        /// <summary>The unlock that sells a wheel, or null when the wheel is
        /// free. Asked by the showroom instead of building an id out of an int,
        /// so a wheel with no unlock row is simply free rather than accidentally
        /// locked or accidentally not.</summary>
        public static UnlockItem ByWheelKey(string wheelKey)
        {
            if (string.IsNullOrEmpty(wheelKey)) return null;
            foreach (var i in All)
                if (i.kind == UnlockKind.WheelStyle && i.wheelKey == wheelKey) return i;
            return null;
        }

        public static UnlockItem ByCode(string code)
        {
            _byCode ??= Build(i => i.code);
            return code != null && _byCode.TryGetValue(code, out var item) ? item : null;
        }

        private static Dictionary<string, UnlockItem> Build(System.Func<UnlockItem, string> key)
        {
            var d = new Dictionary<string, UnlockItem>();
            foreach (var i in All)
            {
                string k = key(i);
                if (k != null) d[k] = i;
            }
            return d;
        }
    }
}
