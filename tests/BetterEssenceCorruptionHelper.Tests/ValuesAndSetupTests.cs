using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Pricing;
using BetterEssenceCorruptionHelper.Strategy;
using Xunit;
using static BetterEssenceCorruptionHelper.Tests.CatalogueTests;

namespace BetterEssenceCorruptionHelper.Tests;

public class EssenceValuesTests
{
    /// <summary>Real Allflame quirks from Ninja Price (2026-10-23): gaps and single-trade outliers.</summary>
    private static readonly Dictionary<string, double> Market = new()
    {
        ["Deafening Essence of Spite"] = 9.25,
        ["Shrieking Essence of Spite"] = 92,      // one trade, far above Deafening
        ["Screaming Essence of Spite"] = 1.44,
        // Wailing Spite not traded
        ["Deafening Essence of Woe"] = 6.78,
        ["Shrieking Essence of Woe"] = 1.23,
        ["Screaming Essence of Woe"] = 0.5,
        ["Wailing Essence of Woe"] = 1,           // above Screaming, but within 1c: normal
        ["Weeping Essence of Woe"] = 15.25,       // one odd trade
        ["Deafening Essence of Misery"] = 2.06,
        // Shrieking Misery not traded
        ["Screaming Essence of Misery"] = 1,
        ["Essence of Horror"] = 42.77,
    };

    private static readonly Dictionary<EssenceEntry, EssenceValue> Values =
        EssenceValues.Compute(e => Market.TryGetValue(e.Name, out var p) ? p : null, marketReady: true);

    private static EssenceValue V(string name) => Values[E(name)];

    [Fact]
    public void Every_essence_gets_a_value()
    {
        Assert.Equal(EssenceCatalogue.Count, Values.Count);
        Assert.All(Values.Values, v => Assert.NotNull(v.Chaos));
    }

    [Fact]
    public void Traded_essences_keep_their_poe_ninja_price()
    {
        Assert.Equal(new EssenceValue(9.25, ValueSource.Market, 9.25, null), V("Deafening Essence of Spite"));
        Assert.Equal(ValueSource.Market, V("Essence of Horror").Source);
        Assert.Equal(1.44, V("Screaming Essence of Spite").Chaos);
    }

    [Fact]
    public void Untraded_essences_are_cheap()
    {
        Assert.Equal(new EssenceValue(EssenceValues.CheapValue, ValueSource.Cheap, null, null), V("Shrieking Essence of Misery"));
        Assert.Equal(ValueSource.Cheap, V("Wailing Essence of Spite").Source);
        Assert.Equal(ValueSource.Cheap, V("Deafening Essence of Greed").Source); // nothing above to compare with: still cheap
        Assert.Equal(ValueSource.Cheap, V("Essence of Delirium").Source);
    }

    [Fact]
    public void Odd_prices_above_the_tier_above_count_as_cheap()
    {
        var shrieking = V("Shrieking Essence of Spite");
        Assert.Equal(ValueSource.Ignored, shrieking.Source);
        Assert.Equal(EssenceValues.CheapValue, shrieking.Chaos);
        Assert.Equal(92, shrieking.MarketChaos);
        Assert.Same(E("Deafening Essence of Spite"), shrieking.Basis);
        Assert.True(shrieking.IsApproximate);

        Assert.Equal(ValueSource.Ignored, V("Weeping Essence of Woe").Source);
    }

    [Fact]
    public void Small_inversions_at_1c_steps_are_normal()
    {
        Assert.Equal(ValueSource.Market, V("Wailing Essence of Woe").Source);
        Assert.Equal(1, V("Wailing Essence of Woe").Chaos);
    }

    [Fact]
    public void Without_ninja_price_nothing_has_a_value()
    {
        var none = EssenceValues.Compute(_ => null, marketReady: false);
        Assert.All(none.Values, v => Assert.Equal(ValueSource.None, v.Source));
        Assert.All(none.Values, v => Assert.Null(v.Chaos));
    }
}

public class SetupStatusTests
{
    private static readonly CorruptionSetup StabilityOnly = new(StabilityScarab: true, CrystalResonance: false);

    [Fact]
    public void Meds_with_crystal_resonance_tells_you_to_remove_it()
    {
        var inMap = SetupStatus.Describe(Strategies.Meds, false, new CorruptionSetup(true, true));
        Assert.Equal(SetupStatus.Level.Problem, inMap?.Level);
        Assert.Equal("Remove Crystal Resonance from your Atlas tree", inMap?.Title);

        Assert.Equal(SetupStatus.Level.Problem, SetupStatus.Describe(Strategies.Meds, true, null)?.Level);
    }

    [Fact]
    public void Meds_without_stability_in_the_map_is_a_warning()
    {
        Assert.Equal(SetupStatus.Level.Warning, SetupStatus.Describe(Strategies.Meds, false, new CorruptionSetup(false, false))?.Level);
        Assert.Null(SetupStatus.Describe(Strategies.Meds, false, StabilityOnly));
    }

    [Fact]
    public void Profit_warns_that_resonance_makes_corrupting_pointless()
    {
        Assert.Equal(SetupStatus.Level.Warning, SetupStatus.Describe(Strategies.Profit, true, null)?.Level);
        Assert.Null(SetupStatus.Describe(Strategies.Profit, false, null));
        Assert.Null(SetupStatus.Describe(Strategies.Profit, false, new CorruptionSetup(false, false))); // no scarab needed
    }

    [Fact]
    public void Deafening_wants_crystal_resonance()
    {
        Assert.Equal(SetupStatus.Level.Warning, SetupStatus.Describe(Strategies.Deafening, false, null)?.Level);
        Assert.Null(SetupStatus.Describe(Strategies.Deafening, true, null));
    }

    [Fact]
    public void Nothing_known_means_nothing_to_say()
    {
        Assert.Null(SetupStatus.Describe(Strategies.Meds, null, null));
    }

    [Fact]
    public void The_map_wins_over_the_atlas_tree()
    {
        // Atlas reading says allocated, but this map has no Resonance effect: trust the map.
        Assert.Null(SetupStatus.Describe(Strategies.Meds, true, StabilityOnly));
    }
}
