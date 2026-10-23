using BetterEssenceCorruptionHelper.Catalogue;
using System;
using System.Collections.Generic;

namespace BetterEssenceCorruptionHelper.Pricing
{
    /// <summary>Where an essence's value comes from.</summary>
    internal enum ValueSource
    {
        /// <summary>poe.ninja price, as returned by Ninja Price.</summary>
        Market,

        /// <summary>Not traded on poe.ninja lately, so it is cheap (see <see cref="EssenceValues.CheapValue"/>).</summary>
        Cheap,

        /// <summary>poe.ninja price exceeds the tier above it, capped to prevent thin-market outliers.</summary>
        Capped,

        /// <summary>No value: Ninja Price isn't delivering prices at all.</summary>
        None,
    }

    /// <summary>The value used for one essence, with the poe.ninja price it came from (if any).</summary>
    internal readonly record struct EssenceValue(double? Chaos, ValueSource Source, double? MarketChaos, EssenceEntry? Basis)
    {
        public bool IsApproximate => Source is ValueSource.Cheap;
    }

    /// <summary>
    /// A value for every essence, built from the <see cref="PriceBook"/> snapshot. An essence is worth
    /// its poe.ninja price, capped by the tier above it (a lower tier cannot legitimately hold more
    /// market value than the tier above it in PoE), or if unlisted, a low baseline value.
    /// </summary>
    internal sealed class EssenceValues(PriceBook prices)
    {
        /// <summary>What a cheap ("&lt;1c") essence counts as in calculations, in chaos.</summary>
        public const double CheapValue = 0.5;

        /// <summary>
        /// How far above the next tier's value a price must be to count as an outlier.
        /// Low-tier essences trade in fractions of a chaos where 0.75c next to 0.74c is normal noise,
        /// not an outlier, whereas a 15c Shrieking next to a 5c Deafening is an illiquid artifact.
        /// </summary>
        public const double OddPriceMargin = 1.0;

        private readonly PriceBook _prices = prices;
        private readonly Dictionary<EssenceEntry, EssenceValue> _values = new();
        private int _builtFor = -1;

        /// <summary>Changes whenever the underlying prices do.</summary>
        public int Version => _prices.Version;

        public EssenceValue Get(EssenceEntry entry)
        {
            if (_builtFor != _prices.Version)
            {
                _values.Clear();
                foreach (var (entry2, value) in Compute(e => _prices.ValueOf(e), _prices.Status == ProviderStatus.Ready))
                    _values[entry2] = value;
                _builtFor = _prices.Version;
            }

            return _values.TryGetValue(entry, out var v) ? v : new EssenceValue(null, ValueSource.None, null, null);
        }

        public double? ValueOf(EssenceEntry entry) => Get(entry).Chaos;

        public bool IsApproximate(EssenceEntry entry) => Get(entry).IsApproximate;

        /// <summary>
        /// Pure market valuation with tier ceiling. Evaluates from Deafening down to lowest tier so
        /// higher tiers provide the upper bound. <paramref name="marketReady"/> says whether a missing
        /// price means "not traded" (cheap baseline) or "no data at all" (no value).
        /// </summary>
        public static Dictionary<EssenceEntry, EssenceValue> Compute(Func<EssenceEntry, double?> market, bool marketReady)
        {
            var result = new Dictionary<EssenceEntry, EssenceValue>();
            var missing = marketReady
                ? new EssenceValue(CheapValue, ValueSource.Cheap, null, null)
                : new EssenceValue(null, ValueSource.None, null, null);

            // Corrupted essences (Horror, Delirium, Hysteria, Insanity) have no upgrade tier
            foreach (var entry in EssenceCatalogue.All)
            {
                if (entry.IsCorruptionOnly)
                    result[entry] = market(entry) is { } p ? new EssenceValue(p, ValueSource.Market, p, null) : missing;
            }

            // Natural essence groups: evaluate from Deafening down to lowest tier
            foreach (var group in new[] { EssenceGroup.A, EssenceGroup.B, EssenceGroup.C, EssenceGroup.D, EssenceGroup.E })
            {
                foreach (var type in EssenceCatalogue.TypesIn(group))
                {
                    // The nearest tier above with a real price. An unlisted tier's guessed value
                    // (CheapValue) is no evidence of what the tiers below are worth.
                    EssenceEntry? above = null;
                    for (var tier = EssenceTier.Deafening; tier >= EssenceCatalogue.MinTier(group); tier--)
                    {
                        var entry = EssenceCatalogue.Find(type, tier)!;
                        var upper = above != null ? result[above].Chaos : null;

                        var value = (market(entry), upper) switch
                        {
                            ({ } p, { } u) when p > u + OddPriceMargin => new EssenceValue(u, ValueSource.Capped, p, above),
                            ({ } p, _) => new EssenceValue(p, ValueSource.Market, p, null),
                            _ => missing,
                        };

                        result[entry] = value;
                        if (value.Source is ValueSource.Market or ValueSource.Capped)
                            above = entry;
                    }
                }
            }

            return result;
        }
    }
}
