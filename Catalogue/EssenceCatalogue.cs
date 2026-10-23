using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Catalogue
{
    /// <summary>
    /// Every Essence that can matter for essence monsters on patch 3.29, plus the Vaal Orb used to
    /// corrupt them.
    ///
    /// <para>
    /// Source: poewiki "Essence" (groups, tier ranges, corruption rules; version history through
    /// 3.29.3) and its item table (names + metadata ids), retrieved 2026-10-23. The generated
    /// names and metadata are checked against that list by the test suite, and against the game's
    /// own BaseItemTypes at runtime (see NinjaPriceBridge / the catalogue diagnostics).
    /// </para>
    ///
    /// <para>
    /// 104 entries: groups A–E (100 natural essences) and group F (4 corruption-only essences).
    /// Deliberately excluded: Essence of Desolation (CurrencyEssenceFaridun1) - a 3.28 Mirage league
    /// item that poewiki lists as drop-disabled and that never appears on, or results from
    /// corrupting, an essence monster. Remnant of Corruption is gone too: since 3.27 Vaal Orbs do
    /// what Remnants did, and existing Remnants were converted to Vaal Orbs.
    /// </para>
    /// </summary>
    internal static class EssenceCatalogue
    {
        /// <summary>Vaal Orb - corrupts an essence monster since 3.27 (previously Remnant of Corruption).</summary>
        public static readonly MarketItem VaalOrb = new("Vaal Orb", "Metadata/Items/Currency/CurrencyCorrupt", MarketFeed.Currency);

        /// <summary>Metadata prefix shared by every essence item (used to discover uncatalogued ones).</summary>
        public const string EssenceMetadataPrefix = "Metadata/Items/Currency/CurrencyEssence";

        /// <summary>Known essence items that are intentionally not in the catalogue, by metadata.</summary>
        public static readonly IReadOnlyDictionary<string, string> KnownExclusions = new Dictionary<string, string>
        {
            ["Metadata/Items/Currency/CurrencyEssenceFaridun1"] = "Essence of Desolation - Mirage (3.28) item, not found on essence monsters",
        };

        /// <summary>The group table: lowest natural tier and the four types of each group.</summary>
        private static readonly (EssenceGroup Group, EssenceTier MinTier, string[] Types)[] Groups =
        [
            (EssenceGroup.A, EssenceTier.Whispering, ["Greed", "Contempt", "Hatred", "Woe"]),
            (EssenceGroup.B, EssenceTier.Muttering, ["Fear", "Anger", "Torment", "Sorrow"]),
            (EssenceGroup.C, EssenceTier.Weeping, ["Rage", "Suffering", "Wrath", "Doubt"]),
            (EssenceGroup.D, EssenceTier.Wailing, ["Loathing", "Zeal", "Anguish", "Spite"]),
            (EssenceGroup.E, EssenceTier.Screaming, ["Misery", "Envy", "Dread", "Scorn"]),
            (EssenceGroup.F, EssenceTier.Corrupted, ["Hysteria", "Insanity", "Horror", "Delirium"]),
        ];

        private static readonly EssenceEntry[] _all = Build();
        private static readonly Dictionary<string, EssenceEntry> _byName = _all.ToDictionary(e => e.Name);
        private static readonly Dictionary<(string Type, EssenceTier Tier), EssenceEntry> _byTypeAndTier =
            _all.ToDictionary(e => (e.TypeName, e.Tier));
        private static readonly Dictionary<string, EssenceGroup> _groupByType =
            _all.GroupBy(e => e.TypeName).ToDictionary(g => g.Key, g => g.First().Group);
        private static readonly Dictionary<EssenceEntry, EssenceEntry[]> _transformCandidates = BuildTransformCandidates();

        /// <summary>All entries, group A..F, lowest tier first within each type.</summary>
        public static IReadOnlyList<EssenceEntry> All => _all;

        /// <summary>All natural essences (Groups A–E), excluding the corruption-only Group F.</summary>
        public static IReadOnlyList<EssenceEntry> NaturalEssences { get; } = _all.Where(e => e.Group != EssenceGroup.F).ToArray();

        /// <summary>All entries plus the Vaal Orb - everything the price book samples.</summary>
        public static IReadOnlyList<MarketItem> MarketItems { get; } = _all.Select(e => e.Item).Append(VaalOrb).ToArray();

        public static int Count => _all.Length;

        public static IEnumerable<EssenceEntry> InGroup(EssenceGroup group) => _all.Where(e => e.Group == group);

        /// <summary>Exact (ordinal) lookup by item name.</summary>
        public static EssenceEntry? FindByName(string name) => _byName.GetValueOrDefault(name);

        public static EssenceEntry? Find(string typeName, EssenceTier tier) => _byTypeAndTier.GetValueOrDefault((typeName, tier));

        public static bool IsKnownType(string typeName) => _groupByType.ContainsKey(typeName);

        public static EssenceGroup? GroupOf(string typeName) => _groupByType.TryGetValue(typeName, out var group) ? group : null;

        public static IEnumerable<EssenceEntry> OfType(string typeName) => _all.Where(e => e.TypeName == typeName);

        public static EssenceTier MinTier(EssenceGroup group) => Groups[(int)group].MinTier;

        public static IReadOnlyList<string> TypesIn(EssenceGroup group) => Groups[(int)group].Types;

        /// <summary>
        /// The essence a "tier upgrade" corruption turns this one into: same type, one tier higher.
        /// Deafening is the highest natural tier and stays Deafening (poewiki: Deafening is reached
        /// "by corrupting ... tier 6 (Shrieking)"; nothing names a tier above it). Group F has no
        /// upgrade - it only exists on monsters that are already corrupted.
        /// </summary>
        public static EssenceEntry? UpgradeOf(EssenceEntry entry) => entry.Tier switch
        {
            EssenceTier.Corrupted => null,
            EssenceTier.Deafening => entry,
            _ => Find(entry.TypeName, entry.Tier + 1),
        };

        /// <summary>
        /// The candidates a "transform" corruption picks from for this essence: a random type of the
        /// next group at the same tier, raised to the next group's lowest tier if needed (poewiki:
        /// "Muttering Essence of Fear will upgrade into a Weeping Tier C Essence"). Group E becomes one
        /// of the four Group F essences whatever its tier. Empty for Group F.
        /// Precomputed and cached to avoid allocations during evaluation and rendering.
        /// </summary>
        public static IReadOnlyList<EssenceEntry> TransformCandidates(EssenceEntry entry) =>
            _transformCandidates.TryGetValue(entry, out var candidates) ? candidates : [];

        private static Dictionary<EssenceEntry, EssenceEntry[]> BuildTransformCandidates()
        {
            var dict = new Dictionary<EssenceEntry, EssenceEntry[]>(_all.Length);
            foreach (var entry in _all)
            {
                if (entry.Group == EssenceGroup.F)
                {
                    dict[entry] = [];
                    continue;
                }

                var next = entry.Group + 1;
                if (next == EssenceGroup.F)
                {
                    dict[entry] = _all.Where(e => e.Group == EssenceGroup.F).ToArray();
                    continue;
                }

                var tier = entry.Tier < MinTier(next) ? MinTier(next) : entry.Tier;
                dict[entry] = TypesIn(next).Select(type => Find(type, tier)!).ToArray();
            }
            return dict;
        }

        private static EssenceEntry[] Build()
        {
            var entries = new List<EssenceEntry>();
            foreach (var (group, minTier, types) in Groups)
            {
                foreach (var type in types)
                {
                    if (group == EssenceGroup.F)
                    {
                        entries.Add(new EssenceEntry(type, EssenceTier.Corrupted, group,
                            $"Essence of {type}", $"{EssenceMetadataPrefix}{type}1"));
                        continue;
                    }

                    // Metadata numbers count from the type's own lowest tier, not from Whispering:
                    // CurrencyEssenceMisery1 is Screaming, CurrencyEssenceGreed1 is Whispering.
                    for (var tier = minTier; tier <= EssenceTier.Deafening; tier++)
                    {
                        entries.Add(new EssenceEntry(type, tier, group,
                            $"{tier} Essence of {type}", $"{EssenceMetadataPrefix}{type}{tier - minTier + 1}"));
                    }
                }
            }

            return entries.ToArray();
        }
    }
}
