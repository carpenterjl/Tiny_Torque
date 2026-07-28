using System;
using System.Collections.Generic;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Persistence
{
    /// <summary>
    /// The scrap shop's stock: six items that rotate daily, plus the four
    /// crates, always available.
    ///
    /// The rotation is seeded from the calendar date, so it needs no server and
    /// no timestamps to police — every machine shows the same six on the same
    /// day, and the list cannot be rerolled by restarting the game. The chosen
    /// ids are written to the profile so buying one does not reshuffle the
    /// other five under the player's cursor.
    ///
    /// Locked items are preferred, because a shop full of things you already
    /// own is not a shop. When the collection is complete the offers fall back
    /// to the whole catalog and simply read as sold out.
    /// </summary>
    public static class ShopStock
    {
        public const int OfferCount = 6;

        /// <summary>
        /// Crate prices. Not in the manifest — crates are earned there, not
        /// sold — so they are derived: the expected duplicate value of a box
        /// (Σ tier-odds × the tier's dupe value, times its pull count) at a 4×
        /// markup, rounded. That makes buying a crate a worse deal than earning
        /// one, which is the point: the shop is a pressure valve, not a
        /// shortcut past racing.
        /// </summary>
        public static int CratePrice(string crateId) => crateId switch
        {
            "crate" => 60,     // EV  13 scrap/pull  x1
            "chrome" => 200,   // EV  25 scrap/pull  x2
            "vault" => 550,    // EV  46 scrap/pull  x3
            "haunt" => 500,    // EV  66 scrap/pull  x2
            _ => 250,
        };

        /// <summary>Days since the epoch, local time — the rotation's clock.</summary>
        private static int Today => (int)(DateTime.Now.Date - new DateTime(2000, 1, 1)).TotalDays;

        /// <summary>Today's six offers, rolled once and then remembered.</summary>
        public static List<UnlockItem> Offers()
        {
            var p = Progression.Current;
            if (p.shopDay != Today || p.shopOffers == null || p.shopOffers.Count == 0)
            {
                Roll(p);
                Progression.Save();
            }

            var list = new List<UnlockItem>(p.shopOffers.Count);
            foreach (var id in p.shopOffers)
            {
                var item = UnlockCatalog.ById(id);
                if (item != null) list.Add(item);
            }
            return list;
        }

        private static void Roll(PlayerProgress p)
        {
            p.shopDay = Today;
            p.shopOffers.Clear();

            var locked = new List<UnlockItem>();
            var all = new List<UnlockItem>();
            foreach (var i in UnlockCatalog.All)
            {
                all.Add(i);
                if (!p.unlocked.Contains(i.id)) locked.Add(i);
            }
            var pool = locked.Count >= OfferCount ? locked : all;

            // Deterministic per day: same six on every launch, and the same six
            // on a friend's machine. Random.InitState is global, so the previous
            // state is put back — the crate rolls happening elsewhere must stay
            // unpredictable.
            var prev = UnityEngine.Random.state;
            UnityEngine.Random.InitState(Today * 8191 + 17);
            var taken = new HashSet<string>();
            int guard = 0;
            while (p.shopOffers.Count < OfferCount && guard++ < 500 && pool.Count > 0)
            {
                var pick = pool[UnityEngine.Random.Range(0, pool.Count)];
                if (taken.Add(pick.id)) p.shopOffers.Add(pick.id);
            }
            UnityEngine.Random.state = prev;
        }

        /// <summary>Buy a crate with scrap. Returns false when the balance will
        /// not cover it.</summary>
        public static bool BuyCrate(string crateId)
        {
            int price = CratePrice(crateId);
            if (CosmeticCatalog.CrateById(crateId) == null) return false;
            if (!Progression.SpendScrap(price)) return false;
            Progression.GrantCrate(crateId);
            Progression.Save();
            return true;
        }

        /// <summary>Seconds until the stock rotates, for the countdown line.</summary>
        public static string TimeToRotation()
        {
            var span = DateTime.Now.Date.AddDays(1) - DateTime.Now;
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }
    }
}
