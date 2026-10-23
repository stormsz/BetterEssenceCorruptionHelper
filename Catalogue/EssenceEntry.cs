namespace BetterEssenceCorruptionHelper.Catalogue
{
    /// <summary>Which poe.ninja feed Ninja Price prices an item from.</summary>
    internal enum MarketFeed
    {
        Essences,
        Currency,
    }

    /// <summary>
    /// A tradeable item as the game and the market know it.
    ///
    /// <para>
    /// <see cref="Name"/> is the game's BaseItemTypes name. Ninja Price looks essences up by exactly
    /// this string (<c>CollectedData.Essences.LinesByName[BaseName]</c>, Methods.cs) and poe.ninja's
    /// <c>items[].name</c> uses the same text, so one string serves as display name, game BaseName
    /// and market key. <see cref="Metadata"/> is the BaseItemTypes key used to resolve the game's own
    /// definition and verify that assumption at runtime.
    /// </para>
    /// </summary>
    internal sealed record MarketItem(string Name, string Metadata, MarketFeed Feed);

    /// <summary>
    /// One (type, tier) Essence, e.g. "Deafening Essence of Misery".
    /// Catalogue identity only: whether it is tracked, recommended or priced lives elsewhere.
    /// </summary>
    internal sealed class EssenceEntry
    {
        public EssenceEntry(string typeName, EssenceTier tier, EssenceGroup group, string name, string metadata)
        {
            TypeName = typeName;
            Tier = tier;
            Group = group;
            Name = name;
            Item = new MarketItem(name, metadata, MarketFeed.Essences);
        }

        /// <summary>Type suffix, e.g. "Misery".</summary>
        public string TypeName { get; }

        public EssenceTier Tier { get; }

        public EssenceGroup Group { get; }

        /// <summary>Exact item name, e.g. "Deafening Essence of Misery" (see <see cref="MarketItem"/>).</summary>
        public string Name { get; }

        public MarketItem Item { get; }

        public string Metadata => Item.Metadata;

        /// <summary>
        /// Group E - Misery, Envy, Dread, Scorn ("purple MEDS"). Corrupting a monster that holds one of
        /// these is the only way to get the Group F essences; the tier does not matter (poewiki).
        /// </summary>
        public bool IsMeds => Group == EssenceGroup.E;

        /// <summary>Group F - only ever found on an already corrupted monster, so never corruptible itself.</summary>
        public bool IsCorruptionOnly => Group == EssenceGroup.F;

        /// <summary>Tier prefix as shown in the name, or "Corrupted" for Group F.</summary>
        public string TierName => Tier.ToString();

        public override string ToString() => Name;
    }
}
