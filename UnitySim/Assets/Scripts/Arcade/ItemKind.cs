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
    }
}
