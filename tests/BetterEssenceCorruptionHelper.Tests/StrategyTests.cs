using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Models;
using BetterEssenceCorruptionHelper.Strategy;
using Newtonsoft.Json;
using Xunit;
using static BetterEssenceCorruptionHelper.Tests.CatalogueTests;

namespace BetterEssenceCorruptionHelper.Tests;

public class TrackingPreferencesTests
{
    [Fact]
    public void Fresh_install_uses_the_profit_strategy_with_everything_automatic()
    {
        var prefs = new TrackingPreferences();

        Assert.Equal("profit", prefs.StrategyId);
        Assert.Equal(StrategyMode.Profit, prefs.Strategy.Mode);
        Assert.All(EssenceCatalogue.All, e => Assert.Equal(EssenceChoice.Auto, prefs.Choice(e)));
        Assert.Equal(TrackingPreferences.DefaultMinExpectedProfit, prefs.EffectiveMinExpectedProfit);
    }

    [Fact]
    public void Clicking_cycles_auto_always_never_auto()
    {
        var prefs = new TrackingPreferences();
        var zeal = E("Deafening Essence of Zeal");

        prefs.CycleChoice(zeal);
        Assert.Equal(EssenceChoice.Always, prefs.Choice(zeal));
        prefs.CycleChoice(zeal);
        Assert.Equal(EssenceChoice.Never, prefs.Choice(zeal));
        prefs.CycleChoice(zeal);
        Assert.Equal(EssenceChoice.Auto, prefs.Choice(zeal));
        Assert.Empty(prefs.EssenceOverrides);
    }

    [Fact]
    public void Corruption_only_essences_cannot_be_chosen()
    {
        var prefs = new TrackingPreferences();
        prefs.SetChoice(E("Essence of Horror"), EssenceChoice.Always);

        Assert.Equal(EssenceChoice.Auto, prefs.Choice(E("Essence of Horror")));
        Assert.Empty(prefs.EssenceOverrides);
    }

    [Fact]
    public void Choices_survive_strategy_switches()
    {
        var prefs = new TrackingPreferences();
        prefs.SetChoice(E("Deafening Essence of Zeal"), EssenceChoice.Always);

        prefs.SelectStrategy(Strategies.Meds.Id);
        prefs.SelectStrategy(Strategies.Profit.Id);

        Assert.Equal(EssenceChoice.Always, prefs.Choice(E("Deafening Essence of Zeal")));
    }

    [Fact]
    public void Clearing_choices_keeps_rule_values()
    {
        var prefs = new TrackingPreferences();
        prefs.SetChoice(E("Deafening Essence of Scorn"), EssenceChoice.Never);
        prefs.SetChoice(E("Deafening Essence of Zeal"), EssenceChoice.Always);
        prefs.MinExpectedProfit = 5;
        var v = prefs.Version;

        prefs.ClearChoices();

        Assert.Empty(prefs.EssenceOverrides);
        Assert.Equal(EssenceChoice.Auto, prefs.Choice(E("Deafening Essence of Zeal")));
        Assert.Equal(5f, prefs.MinExpectedProfit);
        Assert.True(prefs.Version > v);
    }

    [Fact]
    public void Every_change_bumps_the_version()
    {
        var prefs = new TrackingPreferences();
        var v = prefs.Version;
        prefs.CycleChoice(E("Deafening Essence of Zeal"));
        Assert.True(prefs.Version > v);
        v = prefs.Version;
        prefs.SelectStrategy(Strategies.Deafening.Id);
        Assert.True(prefs.Version > v);
    }

    /// <summary>Stand-in for the plugin's Settings: a root object with an optional Tracking block.</summary>
    private sealed class SettingsFile
    {
        public TrackingPreferences Tracking { get; set; } = new();
    }

    [Theory]
    [InlineData(ObjectCreationHandling.Auto)]
    [InlineData(ObjectCreationHandling.Replace)]
    public void Choices_round_trip_through_json(ObjectCreationHandling handling)
    {
        var json = new JsonSerializerSettings { ObjectCreationHandling = handling };
        var saved = new SettingsFile();
        saved.Tracking.SelectStrategy(Strategies.Meds.Id);
        saved.Tracking.SetChoice(E("Deafening Essence of Zeal"), EssenceChoice.Always);
        saved.Tracking.SetChoice(E("Shrieking Essence of Misery"), EssenceChoice.Never);
        saved.Tracking.MinPackSize = 4;
        saved.Tracking.MinExpectedProfit = 2.5f;

        var loaded = JsonConvert.DeserializeObject<SettingsFile>(JsonConvert.SerializeObject(saved, json), json)!;
        Assert.Null(loaded.Tracking.Normalize());

        Assert.Equal("meds", loaded.Tracking.StrategyId);
        Assert.Equal(EssenceChoice.Always, loaded.Tracking.Choice(E("Deafening Essence of Zeal")));
        Assert.Equal(EssenceChoice.Never, loaded.Tracking.Choice(E("Shrieking Essence of Misery")));
        Assert.Equal(4, loaded.Tracking.EffectiveMinPackSize);
        Assert.Equal(2.5, loaded.Tracking.EffectiveMinExpectedProfit, 6);
    }

    [Fact]
    public void Settings_from_older_versions_get_the_defaults()
    {
        // Real settings file from before this feature: no Tracking block at all.
        var loaded = JsonConvert.DeserializeObject<SettingsFile>("""{ "Enable": { "Value": true } }""")!;
        Assert.Null(loaded.Tracking.Normalize());

        Assert.Equal(TrackingPreferences.CurrentSchemaVersion, loaded.Tracking.SchemaVersion);
        Assert.Equal(Strategies.Default.Id, loaded.Tracking.StrategyId);
        Assert.Empty(loaded.Tracking.EssenceOverrides);
    }

    [Fact]
    public void Normalize_repairs_bad_values_and_ignores_retired_fields()
    {
        var prefs = JsonConvert.DeserializeObject<TrackingPreferences>(
            """{ "SchemaVersion": 0, "StrategyId": "from-the-future", "EssenceOverrides": null, "MinPackSize": -4, "MinExpectedProfit": NaN, "StabilityScarab": true, "AssumedWeights": { "NoChange": 1 } }""")!;

        var note = prefs.Normalize();

        Assert.Contains("from-the-future", note);
        Assert.Equal(Strategies.Default.Id, prefs.StrategyId);
        Assert.NotNull(prefs.EssenceOverrides);
        Assert.Equal(0, prefs.MinPackSize);
        Assert.Null(prefs.MinExpectedProfit);
        Assert.Equal(TrackingPreferences.CurrentSchemaVersion, prefs.SchemaVersion);
    }
}

public class StrategyEvaluatorTests
{
    private static EssenceAnalysis Monolith(params string[] lines) => EssenceLabelAnalyzer.AnalyzeLines(lines);

    private static TrackingPreferences Using(StrategyDefinition strategy)
    {
        var prefs = new TrackingPreferences();
        prefs.SelectStrategy(strategy.Id);
        return prefs;
    }

    /// <summary>Allflame-like prices; Stability scarab in the map unless given.</summary>
    private static ProfitContext Context(CorruptionSetup? setup = null)
    {
        var prices = new Dictionary<string, double>
        {
            ["Deafening Essence of Misery"] = 2.06,
            ["Deafening Essence of Spite"] = 9.25,
            ["Shrieking Essence of Spite"] = 9.25,
            ["Deafening Essence of Scorn"] = 15.79,
            ["Deafening Essence of Envy"] = 16.26,
            ["Deafening Essence of Dread"] = 2.09,
            ["Shrieking Essence of Misery"] = 0.98,
            ["Shrieking Essence of Envy"] = 2.5,
            ["Shrieking Essence of Dread"] = 0.33,
            ["Shrieking Essence of Scorn"] = 6.55,
            ["Essence of Hysteria"] = 33.24,
            ["Essence of Insanity"] = 30.75,
            ["Essence of Horror"] = 42.77,
            ["Essence of Delirium"] = 32.7,
        };
        return new ProfitContext(e => prices.TryGetValue(e.Name, out var p) ? p : null, _ => false, 0.53,
            setup ?? new CorruptionSetup(StabilityScarab: true, CrystalResonance: false));
    }

    [Fact]
    public void Unreadable_label_gives_no_recommendation()
    {
        Assert.Equal(EssenceState.None, StrategyEvaluator.Evaluate(EssenceAnalysis.Invalid, new TrackingPreferences(), Context()).State);
    }

    [Fact]
    public void Already_corrupted_monsters_are_killed()
    {
        var r = StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Scorn", "Corrupted"), new TrackingPreferences(), Context());
        Assert.Equal(EssenceState.ShouldKill, r.State);
    }

    [Fact]
    public void Profit_corrupts_meds_because_the_expected_value_says_so()
    {
        var r = StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Misery"), new TrackingPreferences(), Context());

        // Stability: upgrade (Deafening stays: 0) or transform (avg Group F 34.9c - 2.06c), half each.
        Assert.Equal(EssenceState.ShouldCorrupt, r.State);
        Assert.StartsWith("expected +", r.Reason);
    }

    [Fact]
    public void Profit_kills_what_does_not_pay_for_the_orb()
    {
        // Deafening Spite: upgrade does nothing, transform to a Deafening MEDS averages ~9.05c < 9.25c.
        var r = StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Spite"), new TrackingPreferences(), Context());
        Assert.Equal(EssenceState.ShouldKill, r.State);
    }

    [Fact]
    public void Profit_respects_the_minimum_expected_profit()
    {
        var prefs = new TrackingPreferences { MinExpectedProfit = 50 };
        Assert.Equal(EssenceState.ShouldKill, StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Misery"), prefs, Context()).State);
    }

    [Fact]
    public void Profit_never_corrupts_under_crystal_resonance()
    {
        // Resonance copies an existing essence: zero expected gain, so the orb is a loss.
        var r = StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Misery", "Deafening Essence of Scorn"),
            new TrackingPreferences(), Context(new CorruptionSetup(false, true)));
        Assert.Equal(EssenceState.ShouldKill, r.State);
    }

    [Fact]
    public void Profit_choices_override_the_expected_value()
    {
        var prefs = new TrackingPreferences();
        prefs.SetChoice(E("Deafening Essence of Spite"), EssenceChoice.Always);
        Assert.Equal(EssenceState.ShouldCorrupt, StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Spite"), prefs, Context()).State);

        prefs.SetChoice(E("Deafening Essence of Misery"), EssenceChoice.Never);
        Assert.Equal(EssenceState.ShouldKill, StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Misery"), prefs, Context()).State);
    }

    [Fact]
    public void Profit_without_prices_falls_back_to_the_meds_rule()
    {
        var prefs = new TrackingPreferences();
        Assert.Equal(EssenceState.ShouldCorrupt, StrategyEvaluator.Evaluate(Monolith("Screaming Essence of Dread"), prefs, null).State);
        Assert.Equal(EssenceState.ShouldKill, StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Zeal"), prefs, null).State);
    }

    [Theory]
    [InlineData("Screaming Essence of Misery")]
    [InlineData("Shrieking Essence of Envy")]
    [InlineData("Deafening Essence of Dread")]
    public void Meds_strategy_corrupts_any_meds_tier(string meds)
    {
        var r = StrategyEvaluator.Evaluate(Monolith("Wailing Essence of Zeal", meds), Using(Strategies.Meds), Context());
        Assert.Equal(EssenceState.ShouldCorrupt, r.State);
        Assert.Equal(meds, r.Trigger?.Name);
    }

    [Fact]
    public void Meds_strategy_ignores_profit_picks()
    {
        var prefs = Using(Strategies.Meds);
        prefs.SetChoice(E("Deafening Essence of Zeal"), EssenceChoice.Always);
        prefs.SetChoice(E("Deafening Essence of Scorn"), EssenceChoice.Never);

        Assert.Equal(EssenceState.ShouldKill, StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Zeal"), prefs, Context()).State);
        Assert.Equal(EssenceState.ShouldCorrupt, StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Scorn"), prefs, Context()).State);
    }

    [Fact]
    public void Meds_strategy_keeps_the_pack_size_rule()
    {
        var prefs = Using(Strategies.Meds);
        var six = Monolith("Wailing Essence of Zeal", "Wailing Essence of Spite", "Weeping Essence of Rage", "Weeping Essence of Doubt", "Muttering Essence of Fear", "Whispering Essence of Woe");
        Assert.Equal(EssenceState.ShouldCorrupt, StrategyEvaluator.Evaluate(six, prefs, Context()).State);

        prefs.MinPackSize = 0;
        Assert.Equal(EssenceState.ShouldKill, StrategyEvaluator.Evaluate(six, prefs, Context()).State);
    }

    [Fact]
    public void Deafening_strategy_never_corrupts()
    {
        var prefs = Using(Strategies.Deafening);
        prefs.SetChoice(E("Deafening Essence of Scorn"), EssenceChoice.Always);

        Assert.Equal(EssenceState.ShouldKill, StrategyEvaluator.Evaluate(Monolith("Deafening Essence of Scorn"), prefs, Context()).State);
    }
}
