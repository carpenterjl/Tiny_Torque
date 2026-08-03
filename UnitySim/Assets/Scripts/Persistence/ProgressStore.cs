using System;
using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Persistence
{
    /// <summary>
    /// One vehicle's cosmetic choices from the Showroom, keyed by the picker
    /// display name ("★ TT Coupe", a library save name…). Sentinels mean
    /// "don't touch what the design authored": −1 for the cycle values, 0 for
    /// the slot values, "" for the cosmetic ids — so a loadout that was never
    /// edited changes nothing.
    /// </summary>
    [Serializable]
    public sealed class VehicleLoadout
    {
        public string vehicleName = "";
        public int hornStyle = -1;     // -1 = as designed
        public int wheelStyle = -1;    // -1 = as designed (applies to all wheels)

        /// <summary>The wheel the showroom actually chose, as a
        /// <see cref="Vehicles.WheelCatalog"/> key; "" = as designed. Outranks
        /// <see cref="wheelStyle"/> for the same reason a design's key does:
        /// a wheel added by Asset Studio has no int to be named by.
        ///
        /// <b>Both are written.</b> A progress.json from before K5 carries only
        /// the int, and resolves through it — that is the case the [AKEY]
        /// loadout section exercises with a hand-built `wheelStyle: 7`, because
        /// the design dump cannot: no design in its enumeration uses styles 6-8.
        /// A file written by this build and read by an older one loses the key
        /// and keeps the int, which is the same unclosable downgrade path
        /// VehicleDesign.bodyKey documents.</summary>
        public string wheelKey = "";
        public int paintIdx = -1;      // -1 = as designed; else Progression.PaintPalette
        public int topper = 0;         // 0 = as designed; see Progression.ApplyRoofKit
        public int aeroKit = 0;        // 0 = as designed; 1 street / 2 track

        // TinyTorque cosmetics — CosmeticCatalog ids, "" = none.
        public string cosTopper = "";
        public string cosRim = "";
        public string cosOrnament = "";
        public string cosBobble = "";
        public string cosWing = "";
    }

    /// <summary>Pulls since this box last paid epic-or-better. Two parallel
    /// fields rather than a dictionary because JsonUtility cannot serialize
    /// one.</summary>
    [Serializable]
    public sealed class PityEntry
    {
        public string box = "";
        public int sinceEpic;
    }

    /// <summary>
    /// A championship in progress: which series, which round is next, and the
    /// points table. The roster is pinned at the first round — bot names and
    /// difficulty included — because standings across a series only mean
    /// something if the same drivers contest every round.
    /// </summary>
    [Serializable]
    public sealed class ChampionshipState
    {
        public bool active;
        public bool paid;                                   // championship crates already awarded
        public string seriesId = "";
        public int round;                                   // 0-based; == rounds means finished
        public int botDifficulty;
        public List<string> driverNames = new List<string>();
        public List<int> driverPoints = new List<int>();
        public List<bool> driverIsBot = new List<bool>();

        public int PointsOf(string name)
        {
            int i = driverNames.IndexOf(name);
            return i >= 0 && i < driverPoints.Count ? driverPoints[i] : 0;
        }
    }

    /// <summary>
    /// The ONE local progression profile — deliberately global rather than
    /// keyed by player name: split-screen is a couch, and the couch shares the
    /// toy box. LAN races feed the same pool (win on any screen, unlock here).
    /// </summary>
    [Serializable]
    public sealed class PlayerProgress
    {
        // 1 = the original mystery-unlock profile; 2 = crates, scrap and
        // championships. A v1 file loads as v2 with the new fields at their
        // defaults, which is exactly right: you keep everything you unlocked,
        // you start with no scrap and no crates.
        public int version = 2;
        public List<string> unlocked = new List<string>();
        public List<string> redeemedCodes = new List<string>();
        public int xp;
        public int level = 1;
        public int wins;
        public int racesFinished;
        public List<VehicleLoadout> loadouts = new List<VehicleLoadout>();

        public int scrap;
        public List<string> crates = new List<string>();     // unopened, crate ids
        public List<PityEntry> pity = new List<PityEntry>();
        public ChampionshipState championship = new ChampionshipState();
        public int shopDay = -1;                             // day index the offers were rolled for
        public List<string> shopOffers = new List<string>();
    }

    /// <summary>What finishing a race paid out (the results overlay reads this).</summary>
    public sealed class AwardResult
    {
        public List<string> crates = new List<string>();  // crate ids earned
        public int xpGained;
        public bool leveledUp;
        public int newLevel;
    }

    /// <summary>
    /// The progression façade. THE GATING RULE, load-bearing: only the UI layer
    /// may consult this class — MenuUI's pickers, the Showroom, the results
    /// overlays and the cheats page. VehiclePresets.Resolve, VehicleLibrary,
    /// VehicleFactory, SessionConfig and NetSession stay progression-blind, so
    /// bots keep driving locked cars, LAN peers' designs always build, and the
    /// headless regression can never notice this system exists.
    /// </summary>
    public static class Progression
    {
        private const string FileName = "progress.json";

        private static PlayerProgress _current;

        public static PlayerProgress Current
        {
            get
            {
                if (_current != null) return _current;
                _current = SaveSystem.LoadJson<PlayerProgress>(FileName) ?? new PlayerProgress();
                Migrate(_current);
                return _current;
            }
        }

        /// <summary>Fill in anything a v1 file could not have had. Additive
        /// only — a profile is a player's collection and this never takes
        /// something away from it.</summary>
        private static void Migrate(PlayerProgress p)
        {
            p.unlocked ??= new List<string>();
            p.redeemedCodes ??= new List<string>();
            p.loadouts ??= new List<VehicleLoadout>();
            p.crates ??= new List<string>();
            p.pity ??= new List<PityEntry>();
            p.shopOffers ??= new List<string>();
            p.championship ??= new ChampionshipState();
            if (p.version < 2) p.version = 2;
        }

        public static void Save() => SaveSystem.SaveJson(FileName, Current);

        /// <summary>Set after a race; consumed by the results overlay.</summary>
        public static AwardResult LastAward { get; private set; }
        public static void ConsumeAward() => LastAward = null;

        /// <summary>The paint palette the Showroom offers: the standard board
        /// (always free) followed by the three premium finishes (unlockable).
        /// Loadout paintIdx indexes THIS array.</summary>
        public static readonly (Color color, string name, string lockId)[] PaintPalette =
        {
            (new Color(0.85f, 0.20f, 0.16f), "Rally red", null),
            (new Color(0.95f, 0.55f, 0.10f), "Sunset orange", null),
            (new Color(0.93f, 0.85f, 0.25f), "Race yellow", null),
            (new Color(0.20f, 0.65f, 0.30f), "Circuit green", null),
            (new Color(0.20f, 0.45f, 0.90f), "Blue streak", null),
            (new Color(0.90f, 0.90f, 0.92f), "Arctic white", null),
            (new Color(0.16f, 0.16f, 0.18f), "Carbon black", null),
            (new Color(0.94f, 0.78f, 0.36f), "Champagne gold", "paint_gold"),
            (new Color(0.055f, 0.075f, 0.16f), "Midnight navy", "paint_midnight"),
            (new Color(1.00f, 0.25f, 0.65f), "Hot pink", "paint_hotpink"),
        };

        // ══════════════ TEMPORARY DEV SWITCH — delete before shipping ═════════════
        /// <summary>
        /// Treat every unlockable as owned, so everything can be tested without
        /// grinding for it.
        ///
        /// Deliberately an override of the two GATES rather than a bulk grant:
        /// nothing is written into the profile's <c>unlocked</c> list, so turning
        /// it off puts the collection back exactly as it was, and
        /// <see cref="Grant"/> is untouched — a crate pull still knows what you
        /// really own and still pays scrap for a real duplicate.
        ///
        /// TO REMOVE: delete this property, the two <c>if (DevUnlockAll)</c> lines
        /// below, <c>MenuUI.DrawDevRow</c> and its call in
        /// <c>MenuUI.DrawSetupFooter</c>, and <c>GameSettings.devUnlockAll</c>.
        /// Nothing else reads it.
        /// </summary>
        public static bool DevUnlockAll => SettingsStore.Current.devUnlockAll;
        // ═════════════════════════ end temporary dev switch ═══════════════════════

        /// <summary>Unknown ids are unlocked by definition — anything not in
        /// the catalog was never gated (user saves, base styles, the future).</summary>
        public static bool IsUnlocked(string id)
        {
            if (DevUnlockAll) return true;   // TEMPORARY dev switch
            if (UnlockCatalog.ById(id) == null) return true;
            return Current.unlocked.Contains(id);
        }

        /// <summary>Is this ★-preset display name locked? (UI pickers only.)</summary>
        public static bool IsCarLocked(string displayName)
        {
            if (DevUnlockAll) return false;  // TEMPORARY dev switch
            string bare = displayName != null && displayName.StartsWith(VehiclePresets.Prefix)
                ? displayName.Substring(VehiclePresets.Prefix.Length)
                : displayName;
            foreach (var i in UnlockCatalog.All)
                if (i.kind == UnlockKind.Car && i.display == bare)
                    return !Current.unlocked.Contains(i.id);
            return false;
        }

        // ---- scrap ----------------------------------------------------------

        public static int Scrap => Current.scrap;

        public static void AddScrap(int amount)
        {
            if (amount <= 0) return;
            Current.scrap += amount;
        }

        /// <summary>Spend scrap. Returns false (and changes nothing) when the
        /// balance will not cover it.</summary>
        public static bool SpendScrap(int amount)
        {
            if (amount <= 0 || Current.scrap < amount) return false;
            Current.scrap -= amount;
            Save();
            return true;
        }

        /// <summary>Buy an item outright at its tier price. The manifest's own
        /// escape hatch: a player who wants ONE thing should not be hostage to
        /// the box.</summary>
        public static bool Buy(string id)
        {
            var item = UnlockCatalog.ById(id);
            if (item == null || Current.unlocked.Contains(id)) return false;
            if (Current.scrap < UnlockCatalog.DirectCost(item)) return false;
            Current.scrap -= UnlockCatalog.DirectCost(item);
            Current.unlocked.Add(id);
            Save();
            return true;
        }

        // ---- crates ---------------------------------------------------------

        /// <summary>Hand over a crate, unopened. The player opens it when they
        /// feel like it, in the Showroom.</summary>
        public static void GrantCrate(string crateId)
        {
            if (string.IsNullOrEmpty(crateId)) return;
            Current.crates.Add(crateId);
        }

        public static int CrateCount(string crateId)
        {
            int n = 0;
            foreach (var c in Current.crates) if (c == crateId) n++;
            return n;
        }

        /// <summary>Remove one crate of this id from the inventory.</summary>
        public static bool TakeCrate(string crateId)
        {
            int i = Current.crates.IndexOf(crateId);
            if (i < 0) return false;
            Current.crates.RemoveAt(i);
            return true;
        }

        public static int PityOf(string box)
        {
            foreach (var e in Current.pity) if (e.box == box) return e.sinceEpic;
            return 0;
        }

        public static void SetPity(string box, int value)
        {
            foreach (var e in Current.pity)
                if (e.box == box) { e.sinceEpic = value; return; }
            Current.pity.Add(new PityEntry { box = box, sinceEpic = value });
        }

        /// <summary>Mark an item owned. Returns false when it already was —
        /// which is what makes it a duplicate.</summary>
        public static bool Grant(string id)
        {
            if (string.IsNullOrEmpty(id) || Current.unlocked.Contains(id)) return false;
            Current.unlocked.Add(id);
            return true;
        }

        // ---- race results ---------------------------------------------------

        /// <summary>
        /// A race finished. Crates are the whole reward now: any finish pays a
        /// Scrap Crate, a podium adds a Chrome Case. XP still accumulates for
        /// the level badge. Saves.
        ///
        /// <paramref name="opponents"/> gates the podium: coming third out of
        /// three is a podium, coming third out of one is a typo.
        /// </summary>
        public static AwardResult OnRaceFinished(int place, int opponents)
        {
            var p = Current;
            var result = new AwardResult();

            p.racesFinished++;
            if (place == 1) p.wins++;

            result.crates.Add("crate");
            GrantCrate("crate");
            if (place >= 1 && place <= 3 && opponents >= 2)
            {
                result.crates.Add("chrome");
                GrantCrate("chrome");
            }

            result.xpGained = place == 1 ? 100 : place == 2 ? 50 : place == 3 ? 30 : 15;
            p.xp += result.xpGained;
            result.leveledUp = ApplyLevelUps(p);
            result.newLevel = p.level;

            LastAward = result;
            Save();
            return result;
        }

        /// <summary>A championship was won: the Gold Vault, plus the Cursed
        /// Casket when the series was the haunted one. Adds to the pending
        /// award so the final standings screen shows both.</summary>
        public static void OnChampionshipWon(bool haunted)
        {
            var result = LastAward ?? new AwardResult();
            result.crates.Add("vault");
            GrantCrate("vault");
            if (haunted)
            {
                result.crates.Add("haunt");
                GrantCrate("haunt");
            }
            LastAward = result;
            Save();
        }

        /// <summary>Level n → n+1 costs 100·n XP (level 5 ≈ 10 winless races).</summary>
        private static bool ApplyLevelUps(PlayerProgress p)
        {
            bool leveled = false;
            while (p.xp >= 100 * p.level)
            {
                p.xp -= 100 * p.level;
                p.level++;
                leveled = true;
            }
            return leveled;
        }

        /// <summary>Cheat redemption. Returns the item on a fresh hit, null on
        /// a miss; <paramref name="alreadyHad"/> distinguishes "wrong word"
        /// from "right word, twice".</summary>
        public static UnlockItem Redeem(string rawCode, out bool alreadyHad)
        {
            alreadyHad = false;
            string code = (rawCode ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            var item = UnlockCatalog.ByCode(code);
            if (item == null) return null;

            var p = Current;
            if (p.unlocked.Contains(item.id))
            {
                alreadyHad = true;
                return item;
            }
            p.unlocked.Add(item.id);
            if (!p.redeemedCodes.Contains(code)) p.redeemedCodes.Add(code);
            Save();
            return item;
        }

        // ---- loadouts -------------------------------------------------------

        public static VehicleLoadout LoadoutFor(string vehicleName)
        {
            if (string.IsNullOrEmpty(vehicleName)) vehicleName = "";
            foreach (var l in Current.loadouts)
                if (l.vehicleName == vehicleName) return l;
            var fresh = new VehicleLoadout { vehicleName = vehicleName };
            Current.loadouts.Add(fresh);
            return fresh;
        }

        /// <summary>
        /// Overlay a vehicle's Showroom loadout onto a resolved design. Called
        /// ONLY from the UI flow's call sites (StartSinglePlayer, MakeSlot, the
        /// LAN connect paths, the Showroom preview) — never inside
        /// VehiclePresets.Resolve or VehicleLibrary.Load, so everything below
        /// the menu keeps its bytes.
        /// </summary>
        public static void ApplyLoadout(VehicleDesign d, string vehicleName)
        {
            if (d == null) return;
            ApplyLoadout(d, LoadoutFor(vehicleName));
        }

        /// <summary>
        /// The same overlay against a loadout already in hand. Split out at K5
        /// so <c>[AKEY]</c> can apply a hand-written progress.json fragment
        /// without going through <see cref="LoadoutFor"/>, which ADDS an entry
        /// for a name it has never seen — reading the player's progress to test
        /// a parser is one thing, growing it is another.
        /// </summary>
        public static void ApplyLoadout(VehicleDesign d, VehicleLoadout l)
        {
            if (d == null || l == null) return;

            if (l.hornStyle >= 0) d.hornStyle = l.hornStyle;
            // The loadout carries both halves since K5 — resolved through the
            // same rules a design uses, so an old progress.json with only
            // `wheelStyle: 7` still means gold. K4 had to CLEAR the design's key
            // here, because writing a stale int under a live key would have been
            // silently ignored; now the override writes a key of its own and the
            // pair simply agrees.
            // Either half being set means "the player chose a wheel". Testing
            // only the int would make a key-only loadout — which is what a wheel
            // committed by Asset Studio will produce, having no int — read as
            // "as designed" and do nothing at all.
            bool chose = l.wheelStyle >= 0 || !string.IsNullOrEmpty(l.wheelKey);
            if (chose && d.wheels != null)
            {
                var def = Vehicles.WheelCatalog.Resolve(l.wheelKey, l.wheelStyle);
                foreach (var w in d.wheels)
                {
                    w.wheelKey = def.id;
                    w.wheelStyle = def.legacy;
                }
            }
            if (l.paintIdx >= 0 && l.paintIdx < PaintPalette.Length)
            {
                d.bodyColor = PaintPalette[l.paintIdx].color;
                // Explicit choice only: picking a paint replaces a livery, a
                // never-touched loadout (paintIdx -1) leaves it alone.
                d.liveryPng = "";
            }
            ApplyRoofKit(d, l.topper);
            ApplyAeroKit(d, l.aeroKit);
            ApplyCosmetics(d, l);
        }

        /// <summary>
        /// The five TinyTorque cosmetic slots, written onto the design so they
        /// ride it into the race and over the wire. Ownership is checked HERE
        /// rather than at the UI: a loadout can outlive a profile reset, and a
        /// car should never quietly wear something the player does not own.
        /// </summary>
        private static void ApplyCosmetics(VehicleDesign d, VehicleLoadout l)
        {
            d.cosTopper = Owned(l.cosTopper);
            d.cosRim = Owned(l.cosRim);
            d.cosOrnament = Owned(l.cosOrnament);
            d.cosBobble = Owned(l.cosBobble);
            d.cosWing = Owned(l.cosWing);
        }

        private static string Owned(string id) =>
            !string.IsNullOrEmpty(id) && IsUnlocked(id) ? id : "";

        /// <summary>Roof-kit slots: 0 as-designed · 1 light bar · 2 pods ·
        /// 3 flag antenna · 4 twin whips · 5 whip · 6 clean deck. Distinct from
        /// the cosmetic Topper slot, which mounts a crown on the roof rather
        /// than replacing the light/antenna parts.</summary>
        private static void ApplyRoofKit(VehicleDesign d, int topper)
        {
            if (topper <= 0) return;
            d.lights?.Clear();
            d.antennas?.Clear();
            switch (topper)
            {
                case 1:
                    d.lights.Add(new LightSpec { name = "bar", style = 0,
                        localPos = new Vector3(0f, d.bodySize.y * 0.35f, -0.02f) });
                    break;
                case 2:
                    d.lights.Add(new LightSpec { name = "pods", style = 1,
                        localPos = new Vector3(0f, d.bodySize.y * 0.4f, 0.03f) });
                    break;
                case 3:
                    d.antennas.Add(new AntennaSpec { name = "flag", antennaStyle = 2, tiltDeg = 0f,
                        localPos = new Vector3(0.06f, 0f, -0.15f) });
                    break;
                case 4:
                    d.antennas.Add(new AntennaSpec { name = "twins", antennaStyle = 3, tiltDeg = 0f,
                        localPos = new Vector3(0f, 0f, -0.16f) });
                    break;
                case 5:
                    d.antennas.Add(new AntennaSpec { name = "whip", antennaStyle = 1, tiltDeg = 10f,
                        localPos = new Vector3(0.06f, 0f, -0.15f) });
                    break;
                // case 6: clean deck — cleared above, add nothing.
            }
        }

        /// <summary>Aero kits genuinely change mass and downforce — the stats
        /// bars move, and the Showroom says so. 1 = street, 2 = track.</summary>
        private static void ApplyAeroKit(VehicleDesign d, int kit)
        {
            if (kit <= 0 || d.aero == null) return;
            d.aero.Clear();
            if (kit == 1)
            {
                d.aero.Add(new AeroSpec { name = "splitter", kind = AeroKind.Splitter,
                    localPos = new Vector3(0f, -0.02f, 0.20f) });
                d.aero.Add(new AeroSpec { name = "wing", kind = AeroKind.Wing, angleDeg = 4f,
                    sizeScale = 0.85f, localPos = new Vector3(0f, 0.07f, -0.20f) });
            }
            else
            {
                d.aero.Add(new AeroSpec { name = "splitter", kind = AeroKind.Splitter,
                    localPos = new Vector3(0f, -0.02f, 0.20f), sizeScale = 1.1f });
                d.aero.Add(new AeroSpec { name = "canard_l", kind = AeroKind.Canard, angleDeg = 8f,
                    localPos = new Vector3(-0.08f, 0.02f, 0.17f), mirrorGroup = 9 });
                d.aero.Add(new AeroSpec { name = "canard_r", kind = AeroKind.Canard, angleDeg = 8f,
                    localPos = new Vector3(0.08f, 0.02f, 0.17f), mirrorGroup = 9 });
                d.aero.Add(new AeroSpec { name = "wing", kind = AeroKind.Wing, angleDeg = 10f,
                    localPos = new Vector3(0f, 0.08f, -0.21f) });
            }
        }
    }
}
