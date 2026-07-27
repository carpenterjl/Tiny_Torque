namespace AIHWSim.Arcade
{
    /// <summary>
    /// A power-up. Serialized as a byte on the LAN wire and stored in replay
    /// events, so this enum is APPEND-ONLY: never reorder or remove a value.
    /// </summary>
    public enum ItemKind : byte
    {
        None = 0,
        Boost = 1,        // one shot of forward acceleration
        Missile = 2,      // homes on the car ahead
        Banana = 3,       // dropped behind; spins out whoever touches it
        Shield = 4,       // absorbs one hit
        TripleBoost = 5,  // three boosts on one pickup
        SmokeCloud = 6,   // dropped behind; blinds whoever drives through it
        OilSlick = 7,     // dropped behind; kills grip inside it
    }

    /// <summary>
    /// The two <see cref="ItemKind"/>s that deploy a persistent area rather than a
    /// projectile or a self-buff. Both are handled by one <c>AreaHazard</c> and one
    /// containment poll; only the effect they apply differs.
    /// </summary>
    public static class ItemKindExt
    {
        public static bool IsAreaHazard(this ItemKind k) =>
            k == ItemKind.SmokeCloud || k == ItemKind.OilSlick;
    }
}
