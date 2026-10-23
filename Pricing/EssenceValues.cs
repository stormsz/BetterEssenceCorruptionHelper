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

        /// <summary>poe.ninja's price is a thin-market outlier (above the tier above it), so it counts as cheap too.</summary>
        Ignored,

        /// <summary>No value: Ninja Price isn't delivering prices at all.</summary>
        None,
    }

    /// <summary>The value used for one essence, with the poe.ninja price it came from (if any).</summary>
    internal readonly record struct EssenceValue(double? Chaos, ValueSource Source, double? MarketChaos, EssenceEntry? Basis)
    {
        public bool IsApproximate => Source is ValueSource.Cheap or ValueSource.Ignored;
    }

    /// <summary>
    /// A value for every essence, built from the <see cref="PriceBook"/> snapshot. No pricing of our
    /// own: an essence is worth its poe.ninja price, or it is cheap.
    ///
    /// <para>
    /// Cheap because poe.ninja only lists an essence after it traded on the currency exchange lately,
    /// and what drops off is the low end (checked 2026-10-23, Allflame): poe.ninja keeps no data for
    /// unlisted essences (its details endpoint returns 404); the ones that dropped off during the day
    /// were all 1c when last listed; the traded Screaming and Shrieking essences have a median of about
    /// 1c; every Deafening essence was listed; and the in-game exchange offered Whispering Hatred at 1c.
    /// So a missing price is valued at <see cref="CheapValue"/> and shown as "&lt;1c".
    /// </para>
    ///
    /// <para>
    /// The same goes for a listed price more than <see cref="OddPriceMargin"/> above the tier above it
    /// (e.g. a 92c Shrieking Spite next to a 9c Deafening one, a 24.5c Wailing Sorrow next to a 0.25c
    /// Screaming one): these come from one odd trade, and taken at face value they made corrupting
    /// low-tier monsters look profitable.
    /// </para>
    /// </summary>
    internal sealed class EssenceValues(PriceBook prices)
    {
        /// <summary>What a cheap ("&lt;1c") essence counts as in calculations, in chaos.</summary>
        public const double CheapValue = 0.5;

        /// <summary>
        /// How far above the next tier's value a price must be to count as odd. Cheap essences trade at
        /// 1c steps, so a 1c Wailing next to a 0.5c Screaming is normal noise, not an outlier.
        /// </summary>
        public const double OddPriceMargin = 1;

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
        /// The valuation rules, over any market (pure, for tests). <paramref name="marketReady"/> says
        /// whether a missing price means "not traded" (cheap) or "no data at all" (no value).
        /// </summary>
        public static Dictionary<EssenceEntry, EssenceValue> Compute(Func<EssenceEntry, double?> market, bool marketReady)
        {
            var result = new Dictionary<EssenceEntry, EssenceValue>();
            var missing = marketReady
                ? new EssenceValue(CheapValue, ValueSource.Cheap, null, null)
                : new EssenceValue(null, ValueSource.None, null, null);

            foreach (var entry in EssenceCatalogue.All)
            {
                if (entry.IsCorruptionOnly)
                    result[entry] = market(entry) is { } p ? new EssenceValue(p, ValueSource.Market, p, null) : missing;
            }

            foreach (var group in new[] { EssenceGroup.A, EssenceGroup.B, EssenceGroup.C, EssenceGroup.D, EssenceGroup.E })
            {
                foreach (var type in EssenceCatalogue.TypesIn(group))
                {
                    EssenceEntry? above = null;
                    for (var tier = EssenceTier.Deafening; tier >= EssenceCatalogue.MinTier(group); tier--)
                    {
                        var entry = EssenceCatalogue.Find(type, tier)!;
                        var upper = above != null ? result[above].Chaos : null;

                        result[entry] = (market(entry), upper) switch
                        {
                            ({ } p, { } u) when p > u + OddPriceMargin => new EssenceValue(CheapValue, ValueSource.Ignored, p, above),
                            ({ } p, _) => new EssenceValue(p, ValueSource.Market, p, null),
                            _ => missing,
                        };

                        above = entry;
                    }
                }
            }

            return result;
        }
    }
}
