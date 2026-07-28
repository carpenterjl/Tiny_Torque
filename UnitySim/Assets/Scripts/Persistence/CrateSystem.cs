using System.Collections.Generic;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Persistence
{
    /// <summary>One pull's outcome: the item, and whether it was already owned
    /// (in which case it paid scrap instead).</summary>
    public sealed class CratePull
    {
        public UnlockItem item;
        public bool duplicate;
        public int scrap;
        public bool pityForced;   // this pull was the pity counter cashing in
    }

    /// <summary>
    /// Opens the four TinyTorque boxes, implementing the manifest's rules
    /// verbatim:
    ///
    ///   weights  relative per-pull weight by rarity, normalised over the tiers
    ///            the box can actually fill
    ///   floor    the lowest rarity at least one item must be; reroll the last
    ///            pull if nothing met it
    ///   pity     pulls since the last epic-or-better; on reaching it, force
    ///            the next pull to epic or better and reset the counter
    ///   theme    a themed box draws only from items of that theme
    ///
    /// The manifest also ships a per-item <c>odds</c> table. It is deliberately
    /// NOT transcribed: every entry in it is exactly
    /// <c>weight[rarity] / |pool[rarity]|</c>, so rolling a rarity by weight and
    /// then picking uniformly inside it reproduces those numbers and keeps
    /// reproducing them now that the 20 legacy unlocks have joined the pools.
    ///
    /// The pool is every item of the tier, owned or not — which is what makes a
    /// duplicate possible, and duplicates are how the manifest turns bad luck
    /// into scrap you can spend on the exact thing you wanted.
    /// </summary>
    public static class CrateSystem
    {
        private const Rarity PityTier = Rarity.Epic;

        /// <summary>
        /// Open one crate of this id from the inventory. Returns null when the
        /// player has none (or the id is unknown); otherwise the pulls, with
        /// the profile already updated and saved.
        /// </summary>
        public static List<CratePull> Open(string crateId)
        {
            var def = CosmeticCatalog.CrateById(crateId);
            if (def == null || !Progression.TakeCrate(crateId)) return null;

            var pools = BuildPools(def);
            var pulls = new List<CratePull>(Mathf.Max(1, def.pulls));
            int pity = Progression.PityOf(crateId);

            for (int i = 0; i < def.pulls; i++)
            {
                bool forced = def.pity > 0 && pity >= def.pity;
                var pull = Draw(def, pools, forced ? PityTier : (Rarity?)null);
                if (pull == null) continue;
                pull.pityForced = forced;
                pulls.Add(pull);

                if (pull.item.rarity >= PityTier) pity = 0;
                else pity++;
            }

            // Floor: at least one pull has to reach it. Redoing the LAST pull is
            // the manifest's own wording, and it keeps the earlier reveals
            // honest — a player watching the roulette never sees a result
            // retracted.
            if (def.floor.HasValue && pulls.Count > 0)
            {
                bool met = false;
                foreach (var p in pulls) if (p.item.rarity >= def.floor.Value) met = true;
                if (!met)
                {
                    Undo(pulls[pulls.Count - 1]);
                    var redo = Draw(def, pools, def.floor.Value);
                    if (redo != null)
                    {
                        pulls[pulls.Count - 1] = redo;
                        if (redo.item.rarity >= PityTier) pity = 0;
                    }
                }
            }

            Progression.SetPity(crateId, pity);
            Progression.Save();
            return pulls;
        }

        /// <summary>
        /// Draw one item. <paramref name="minRarity"/> restricts the roll to
        /// that tier and above (pity and floor both use it), keeping the
        /// relative weights of the tiers that survive.
        /// </summary>
        private static CratePull Draw(CrateDef def, List<UnlockItem>[] pools, Rarity? minRarity)
        {
            int tier = RollRarity(def, pools, minRarity);
            if (tier < 0) return null;
            var pool = pools[tier];
            var item = pool[Random.Range(0, pool.Count)];

            var pull = new CratePull { item = item };
            if (Progression.Grant(item.id))
            {
                pull.duplicate = false;
            }
            else
            {
                pull.duplicate = true;
                pull.scrap = UnlockCatalog.DupeValue(item);
                Progression.AddScrap(pull.scrap);
            }
            return pull;
        }

        /// <summary>Undo a pull that the floor rule is about to replace.</summary>
        private static void Undo(CratePull pull)
        {
            if (pull == null || pull.item == null) return;
            if (pull.duplicate) Progression.AddScrap(-pull.scrap);
            else Progression.Current.unlocked.Remove(pull.item.id);
        }

        /// <summary>Weighted rarity roll over the tiers that have both a
        /// non-zero weight and at least one item in this box.</summary>
        private static int RollRarity(CrateDef def, List<UnlockItem>[] pools, Rarity? minRarity)
        {
            int lo = minRarity.HasValue ? (int)minRarity.Value : 0;
            float total = 0f;
            for (int t = lo; t < pools.Length; t++)
                if (pools[t].Count > 0) total += Weight(def, t);

            // A forced tier the box cannot fill (a themed box with no legendary,
            // say) falls back to the whole range rather than paying nothing.
            if (total <= 0f)
            {
                if (lo > 0) return RollRarity(def, pools, null);
                return -1;
            }

            float roll = Random.value * total;
            for (int t = lo; t < pools.Length; t++)
            {
                if (pools[t].Count == 0) continue;
                roll -= Weight(def, t);
                if (roll <= 0f) return t;
            }
            for (int t = pools.Length - 1; t >= lo; t--)
                if (pools[t].Count > 0) return t;   // float slop
            return -1;
        }

        private static float Weight(CrateDef def, int tier) =>
            def.weights != null && tier < def.weights.Length
                ? Mathf.Max(0f, def.weights[tier]) : 0f;

        /// <summary>
        /// This box's draw pool, per rarity. A themed box keeps only items of
        /// its theme — which also means the legacy unlocks, having no theme,
        /// stay out of the Cursed Casket and leave its authored eleven intact.
        /// </summary>
        private static List<UnlockItem>[] BuildPools(CrateDef def)
        {
            var pools = new List<UnlockItem>[5];
            for (int i = 0; i < pools.Length; i++) pools[i] = new List<UnlockItem>();
            foreach (var item in UnlockCatalog.All)
            {
                if (def.theme.HasValue)
                {
                    var c = CosmeticCatalog.ById(item.id);
                    if (c == null || c.theme != def.theme.Value) continue;
                }
                int t = (int)item.rarity;
                if (t >= 0 && t < pools.Length) pools[t].Add(item);
            }
            return pools;
        }

        /// <summary>How many items this box can pay out per tier — the
        /// manifest's <c>pool</c> block, computed rather than stored, for the
        /// odds readout in the UI.</summary>
        public static int[] PoolSizes(CrateDef def)
        {
            var pools = BuildPools(def);
            var sizes = new int[pools.Length];
            for (int i = 0; i < pools.Length; i++) sizes[i] = pools[i].Count;
            return sizes;
        }

        /// <summary>Per-tier drop chance, as the crate screen shows it.</summary>
        public static float[] TierOdds(CrateDef def)
        {
            var pools = BuildPools(def);
            var odds = new float[pools.Length];
            float total = 0f;
            for (int t = 0; t < pools.Length; t++)
                if (pools[t].Count > 0) total += Weight(def, t);
            if (total <= 0f) return odds;
            for (int t = 0; t < pools.Length; t++)
                odds[t] = pools[t].Count > 0 ? Weight(def, t) / total : 0f;
            return odds;
        }
    }
}
