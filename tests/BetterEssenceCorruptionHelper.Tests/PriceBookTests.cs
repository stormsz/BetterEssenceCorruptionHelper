using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Pricing;
using Xunit;
using static BetterEssenceCorruptionHelper.Tests.CatalogueTests;

namespace BetterEssenceCorruptionHelper.Tests;

public class PriceBookTests
{
    private long _now = 1_000;
    private PriceQuery? _provider;
    private int _resolveCalls;

    private PriceBook NewBook() => new(EssenceCatalogue.MarketItems, () => { _resolveCalls++; return _provider; }, () => _now);

    /// <summary>A provider that knows a few items, like Ninja Price: 0 for everything it doesn't know.</summary>
    private static PriceQuery Market(Dictionary<string, double> prices) => item => prices.GetValueOrDefault(item.Name);

    private static readonly Dictionary<string, double> SomePrices = new()
    {
        ["Vaal Orb"] = 0.3945,
        ["Deafening Essence of Scorn"] = 33.07,
        ["Screaming Essence of Misery"] = 1.0,
        ["Essence of Horror"] = 37.63,
    };

    [Fact]
    public void Ninja_price_missing_gives_unavailable_never_zero()
    {
        var book = NewBook();
        book.Sample();

        Assert.Equal(ProviderStatus.Unavailable, book.Status);
        var p = book.Get(E("Deafening Essence of Scorn"));
        Assert.Equal(PriceState.ProviderUnavailable, p.State);
        Assert.False(p.HasValue);
        Assert.Null(p.Value);
        Assert.Null(book.VaalOrb.Value);
    }

    [Fact]
    public void Ninja_price_initialising_late_is_picked_up_on_retry()
    {
        var book = NewBook();
        Assert.True(book.Update());
        Assert.Equal(ProviderStatus.Unavailable, book.Status);

        _provider = Market(SomePrices); // Ninja Price registers its bridge method later
        _now += PriceBook.RetryIntervalMs - 1;
        Assert.False(book.Update());     // not due yet
        _now += 1;
        Assert.True(book.Update());

        Assert.Equal(ProviderStatus.Ready, book.Status);
        Assert.Equal(33.07, book.Get(E("Deafening Essence of Scorn")).Value);
        Assert.Equal(2, _resolveCalls);
    }

    [Fact]
    public void Provider_answering_zero_for_everything_is_loading_not_unlisted()
    {
        _provider = _ => 0;
        var book = NewBook();
        book.Sample();

        Assert.Equal(ProviderStatus.Loading, book.Status);
        Assert.Equal(PriceState.ProviderLoading, book.Get(E("Deafening Essence of Scorn")).State);
        Assert.Equal(PriceState.ProviderLoading, book.VaalOrb.State);
    }

    [Fact]
    public void Zero_from_a_loaded_provider_is_unlisted_and_has_no_value()
    {
        _provider = Market(SomePrices);
        var book = NewBook();
        book.Sample();

        var shrieking = book.Get(E("Shrieking Essence of Misery")); // not on poe.ninja in Allflame
        Assert.Equal(PriceState.Unlisted, shrieking.State);
        Assert.Null(shrieking.Value);
        Assert.Equal(SomePrices.Count, book.ListedCount);
        Assert.Equal(EssenceCatalogue.MarketItems.Count - SomePrices.Count, book.UnlistedCount);
    }

    [Fact]
    public void Values_are_passed_through_per_item_without_stack_scaling()
    {
        _provider = Market(SomePrices);
        var book = NewBook();
        book.Sample();

        Assert.Equal(0.3945, book.VaalOrb.Value);
        Assert.Equal(1.0, book.ValueOf(E("Screaming Essence of Misery")));
        Assert.Equal(37.63, book.ValueOf(E("Essence of Horror")));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void Invalid_numbers_are_flagged_invalid(double bad)
    {
        _provider = item => item.Name == "Deafening Essence of Scorn" ? bad : SomePrices.GetValueOrDefault(item.Name);
        var book = NewBook();
        book.Sample();

        Assert.Equal(PriceState.Invalid, book.Get(E("Deafening Essence of Scorn")).State);
        Assert.Null(book.ValueOf(E("Deafening Essence of Scorn")));
        Assert.Equal(PriceState.Ok, book.VaalOrb.State);
    }

    [Fact]
    public void An_invalid_reading_after_a_good_one_keeps_the_good_value_as_stale()
    {
        var scorn = 33.07;
        _provider = item => item.Name == "Deafening Essence of Scorn" ? scorn : SomePrices.GetValueOrDefault(item.Name);
        var book = NewBook();
        book.Sample();

        scorn = double.NaN;
        book.Sample();

        var p = book.Get(E("Deafening Essence of Scorn"));
        Assert.Equal(PriceState.Stale, p.State);
        Assert.Equal(33.07, p.Value);
    }

    [Fact]
    public void A_throwing_item_is_an_error_without_affecting_others()
    {
        _provider = item => item.Name == "Essence of Horror" ? throw new InvalidOperationException() : SomePrices.GetValueOrDefault(item.Name);
        var book = NewBook();
        book.Sample();

        Assert.Equal(PriceState.Error, book.Get(E("Essence of Horror")).State);
        Assert.Equal(33.07, book.ValueOf(E("Deafening Essence of Scorn")));
        Assert.Equal(ProviderStatus.Ready, book.Status);
    }

    [Fact]
    public void A_dead_delegate_is_dropped_and_looked_up_again()
    {
        _provider = _ => throw new ObjectDisposedException("Ninja Price");
        var book = NewBook();
        book.Sample();
        Assert.Equal(ProviderStatus.Unavailable, book.Status);

        _provider = Market(SomePrices);
        book.Sample();
        Assert.Equal(2, _resolveCalls);
        Assert.Equal(ProviderStatus.Ready, book.Status);
    }

    [Fact]
    public void Last_good_value_is_kept_as_stale_when_the_provider_goes_away()
    {
        _provider = Market(SomePrices);
        var book = NewBook();
        book.Sample();
        var sampledAt = _now;

        _provider = null;
        book.Invalidate(reResolveProvider: true);
        _now += 10_000;
        book.Sample();

        var scorn = book.Get(E("Deafening Essence of Scorn"));
        Assert.Equal(PriceState.Stale, scorn.State);
        Assert.Equal(33.07, scorn.Value);
        Assert.Equal(sampledAt, scorn.SampledAtMs); // age keeps counting from the real sample
        Assert.Equal(PriceState.ProviderUnavailable, book.Get(E("Shrieking Essence of Misery")).State);
    }

    [Fact]
    public void Currency_without_essences_reports_missing_essence_data()
    {
        _provider = item => item.Name == "Vaal Orb" ? 0.4 : 0;
        var book = NewBook();
        book.Sample();

        Assert.Equal(ProviderStatus.EssenceDataMissing, book.Status);
        Assert.Equal(PriceState.Unlisted, book.Get(E("Deafening Essence of Scorn")).State);
    }

    [Fact]
    public void Ready_provider_is_resampled_on_the_long_interval_and_on_invalidate()
    {
        _provider = Market(SomePrices);
        var book = NewBook();
        Assert.True(book.Update());
        var version = book.Version;

        _now += PriceBook.RetryIntervalMs;
        Assert.False(book.Update()); // ready: no fast retry

        book.Invalidate(reResolveProvider: false);
        Assert.True(book.Update());
        Assert.Equal(1, _resolveCalls); // delegate kept
        Assert.Equal(version + 1, book.Version);

        _now += PriceBook.RefreshIntervalMs;
        Assert.True(book.Update());
    }
}
