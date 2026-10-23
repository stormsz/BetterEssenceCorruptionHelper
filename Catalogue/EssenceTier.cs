namespace BetterEssenceCorruptionHelper.Catalogue
{
    /// <summary>
    /// Essence tiers. Natural essences run Whispering (1) .. Deafening (7); the four corruption-only
    /// essences (Group F) have a single tier and no name prefix, modelled here as tier 8.
    /// </summary>
    internal enum EssenceTier
    {
        Whispering = 1,
        Muttering = 2,
        Weeping = 3,
        Wailing = 4,
        Screaming = 5,
        Shrieking = 6,
        Deafening = 7,
        Corrupted = 8,
    }

    /// <summary>
    /// Essence groups as poewiki defines them. Groups A–E appear naturally on imprisoned monsters and
    /// differ in their lowest tier; Group F only comes from corrupting a Group E (MEDS) essence.
    /// Corruption's "transform" outcome moves an essence one group along (A→B→…→E→F).
    /// </summary>
    internal enum EssenceGroup
    {
        A,
        B,
        C,
        D,
        E,
        F,
    }
}
