using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Models;
using BetterEssenceCorruptionHelper.Pricing;
using BetterEssenceCorruptionHelper.Strategy;
using Xunit;
using static BetterEssenceCorruptionHelper.Tests.CatalogueTests;

namespace BetterEssenceCorruptionHelper.Tests;

public class LabelAnalyzerTests
{
    [Fact]
    public void Essence_lines_map_to_exact_catalogue_entries()
    {
        var a = EssenceLabelAnalyzer.AnalyzeLines(["Deafening Essence of Misery", "Shrieking Essence of Zeal", "Weeping Essence of Wrath"]);

        Assert.True(a.IsValid);
        Assert.False(a.IsCorrupted);
        Assert.Equal(new[] { "Deafening Essence of Misery", "Shrieking Essence of Zeal", "Weeping Essence of Wrath" }, a.Essences.Select(e => e.Name));
        Assert.Equal(3, a.EssenceCount);
        Assert.True(a.HasMeds);
        Assert.Equal(1, a.CountTier(EssenceTier.Deafening));
    }

    [Fact]
    public void Corrupted_marker_and_group_f_results_are_read()
    {
        var a = EssenceLabelAnalyzer.AnalyzeLines(["Essence of Hysteria", "Deafening Essence of Woe", "Corrupted"]);

        Assert.True(a.IsCorrupted);
        Assert.Equal(new[] { "Essence of Hysteria", "Deafening Essence of Woe" }, a.Essences.Select(e => e.Name));
    }

    [Fact]
    public void Monster_modifier_lines_are_ignored()
    {
        var a = EssenceLabelAnalyzer.AnalyzeLines(["Screaming Essence of Scorn", "Extra Fire Damage", "Possessed by Tormented Smuggler"]);

        Assert.Equal(1, a.EssenceCount);
        Assert.Empty(a.UnrecognizedEssenceLines);
        Assert.False(a.IsCorrupted);
    }

    [Theory]
    [InlineData("  Deafening Essence of Misery  ", "Deafening Essence of Misery")]
    [InlineData("<rare>{Shrieking Essence of Envy}", "Shrieking Essence of Envy")]
    [InlineData("2x Wailing Essence of Anguish", "Wailing Essence of Anguish")]
    [InlineData("<corrupted>{Essence of Delirium}", "Essence of Delirium")]
    public void Decorated_lines_are_still_matched(string line, string expected)
    {
        Assert.True(EssenceLabelAnalyzer.TryParseEssenceLine(line, out var entry));
        Assert.Equal(expected, entry?.Name);
    }

    [Fact]
    public void Unknown_essences_are_counted_but_flagged()
    {
        var a = EssenceLabelAnalyzer.AnalyzeLines(["Essence of Desolation", "Weeping Essence of Anguish", "Deafening Essence of Rage"]);

        Assert.Equal(3, a.EssenceCount);
        Assert.Single(a.Essences);
        Assert.Equal(new[] { "Essence of Desolation", "Weeping Essence of Anguish" }, a.UnrecognizedEssenceLines);
        Assert.Equal(new[] { "Anguish" }, a.UnresolvedTypes); // Desolation isn't a catalogue type
    }

    [Fact]
    public void A_meds_essence_with_an_unreadable_tier_still_counts_as_meds()
    {
        var a = EssenceLabelAnalyzer.AnalyzeLines(["Thundering Essence of Misery", "Wailing Essence of Zeal"]);

        Assert.True(a.HasMeds);
        Assert.Equal(new[] { "Misery" }, a.UnresolvedTypes);

        var meds = new TrackingPreferences();
        meds.SelectStrategy(Strategies.Meds.Id);
        var r = StrategyEvaluator.Evaluate(a, meds);
        Assert.Equal(EssenceState.ShouldCorrupt, r.State);
    }

    [Fact]
    public void Same_content_ignores_order()
    {
        var a = EssenceLabelAnalyzer.AnalyzeLines(["Deafening Essence of Misery", "Shrieking Essence of Zeal"]);
        var b = EssenceLabelAnalyzer.AnalyzeLines(["Shrieking Essence of Zeal", "Deafening Essence of Misery"]);
        var c = EssenceLabelAnalyzer.AnalyzeLines(["Shrieking Essence of Zeal", "Deafening Essence of Misery", "Corrupted"]);

        Assert.True(a.SameContentAs(b));
        Assert.False(a.SameContentAs(c));
    }
}

public class CorruptionModelTests
{
    /// <summary>Prices from Ninja Price's Allflame snapshot (2026-10-22), chaos per item.</summary>
    private static readonly Dictionary<string, double> Prices = new()
    {
        ["Screaming Essence of Misery"] = 1.0,
        ["Deafening Essence of Misery"] = 2.03,
        ["Deafening Essence of Envy"] = 12.06,
        ["Deafening Essence of Dread"] = 1.0,
        ["Deafening Essence of Scorn"] = 33.07,
        ["Essence of Horror"] = 37.63,
        ["Essence of Insanity"] = 36.3,
        ["Essence of Delirium"] = 45.47,
        ["Essence of Hysteria"] = 33.14,
        ["Shrieking Essence of Zeal"] = 1.5,
        ["Deafening Essence of Zeal"] = 4.0,
        ["Deafening Essence of Loathing"] = 3.0,
        ["Deafening Essence of Anguish"] = 2.0,
        ["Deafening Essence of Spite"] = 5.0,
    };

    private const double Vaal = 0.3945;
    private static readonly CorruptionSetup Stability = new(StabilityScarab: true, CrystalResonance: false);
    private static readonly CorruptionSetup Plain = new(false, false);
    private static readonly CorruptionSetup Resonance = new(false, true);

    private static double? Price(EssenceEntry e) => Prices.TryGetValue(e.Name, out var p) ? p : null;

    private static CorruptionAssessment Assess(CorruptionSetup setup, params string[] names) =>
        CorruptionModel.Assess(names.Select(E).ToArray(), 0, setup, Price, Vaal, out _)!;

    [Fact]
    public void Applicable_outcomes_follow_the_setup()
    {
        Assert.Equal(new[] { CorruptionOutcome.NoChange, CorruptionOutcome.TierUpgrade, CorruptionOutcome.AddEssence, CorruptionOutcome.Transform }, CorruptionModel.ApplicableOutcomes(Plain));
        Assert.Equal(new[] { CorruptionOutcome.TierUpgrade, CorruptionOutcome.Transform }, CorruptionModel.ApplicableOutcomes(Stability));
        Assert.Equal(new[] { CorruptionOutcome.ResonanceReplace }, CorruptionModel.ApplicableOutcomes(Resonance));
        Assert.Equal(new[] { CorruptionOutcome.ResonanceReplace }, CorruptionModel.ApplicableOutcomes(new CorruptionSetup(true, true)));
    }

    [Fact]
    public void Deafening_meds_with_stability_always_gains_at_these_prices()
    {
        var a = Assess(Stability, "Deafening Essence of Misery");

        Assert.Equal(2.03, a.CurrentValue, 6);
        var upgrade = a.Outcomes.Single(o => o.Outcome == CorruptionOutcome.TierUpgrade);
        Assert.Equal(0, upgrade.MinGain, 6); // Deafening can't go higher

        var transform = a.Outcomes.Single(o => o.Outcome == CorruptionOutcome.Transform);
        Assert.Equal(33.14 - 2.03, transform.MinGain, 6);
        Assert.Equal(45.47 - 2.03, transform.MaxGain, 6);
        Assert.Equal((37.63 + 36.3 + 45.47 + 33.14) / 4 - 2.03, transform.UniformMeanGain!.Value, 6);

        // Worst case is the upgrade "doing nothing" minus the orb: a small loss.
        Assert.Equal(-Vaal, a.WorstNetGain, 6);
        Assert.Equal(45.47 - 2.03 - Vaal, a.BestNetGain, 6);
        Assert.Equal(CorruptionVerdict.DependsOnOdds, a.Verdict);
        Assert.False(a.IsApproximate);
    }

    [Fact]
    public void Screaming_meds_upgrade_uses_the_next_tier_price_and_reports_unpriced_items()
    {
        // Shrieking Misery is not listed on poe.ninja: counted as 0c and reported.
        var a = Assess(Stability, "Screaming Essence of Misery");

        var upgrade = a.Outcomes.Single(o => o.Outcome == CorruptionOutcome.TierUpgrade);
        Assert.Equal(0 - 1.0, upgrade.MinGain, 6);
        Assert.Contains("Shrieking Essence of Misery", a.UnpricedItems);
    }

    [Fact]
    public void Transform_gains_add_up_per_essence()
    {
        var a = Assess(Stability, "Deafening Essence of Misery", "Deafening Essence of Scorn");
        var t = a.Outcomes.Single(o => o.Outcome == CorruptionOutcome.Transform);

        Assert.Equal(2 * 33.14 - 2.03 - 33.07, t.MinGain, 6);
        Assert.Equal(2 * 45.47 - 2.03 - 33.07, t.MaxGain, 6);
    }

    [Fact]
    public void Added_essence_is_bounded_by_zero_and_the_priciest_natural_essence()
    {
        var a = Assess(Plain, "Deafening Essence of Zeal");
        var add = a.Outcomes.Single(o => o.Outcome == CorruptionOutcome.AddEssence);

        Assert.Equal(0, add.MinGain);
        Assert.Equal(33.07, add.MaxGain, 6); // Deafening Scorn - Group F can't be added
        Assert.NotNull(add.UniformMeanGain);
        Assert.True(add.UniformMeanGain > 0);
        Assert.DoesNotContain("Whispering Essence of Greed", a.UnpricedItems); // not on the monster, not reported
    }

    [Fact]
    public void Added_essence_cap_says_when_unlisted_essences_were_ignored()
    {
        var a = Assess(Plain, "Deafening Essence of Zeal");
        Assert.Contains(a.UnpricedItems, i => i.StartsWith("added-essence cap ignores"));

        // Not applicable under Stability, so no note.
        Assert.DoesNotContain(Assess(Stability, "Deafening Essence of Zeal").UnpricedItems, i => i.StartsWith("added-essence"));
    }

    [Fact]
    public void Resonance_copies_one_existing_essence_onto_every_slot()
    {
        var a = Assess(Resonance, "Deafening Essence of Scorn", "Deafening Essence of Zeal", "Deafening Essence of Dread");
        var r = a.Outcomes.Single();
        var current = 33.07 + 4.0 + 1.0;

        Assert.Equal(CorruptionOutcome.ResonanceReplace, r.Outcome);
        Assert.Equal(3 * 1.0 - current, r.MinGain, 6);
        Assert.Equal(3 * 33.07 - current, r.MaxGain, 6);
        Assert.Equal((3 * 33.07 + 3 * 4.0 + 3 * 1.0) / 3 - current, r.UniformMeanGain!.Value, 6);
    }

    [Fact]
    public void Verdicts_are_sound_bounds()
    {
        // Cheap essence, Resonance with only itself: nothing can change, the orb is always lost.
        var loses = Assess(Resonance, "Deafening Essence of Dread");
        Assert.Equal(CorruptionVerdict.AlwaysLoses, loses.Verdict);

        // Two cheap MEDS, Stability: worst outcome (upgrade of Deafening = no change) still costs the orb,
        // so even a great transform can't make this "always gains".
        var depends = Assess(Stability, "Deafening Essence of Dread", "Deafening Essence of Misery");
        Assert.Equal(CorruptionVerdict.DependsOnOdds, depends.Verdict);

        // Shrieking Zeal + Stability: upgrade gains 2.5c, transform to Deafening Group E gains at least
        // 1.0 - 1.5; with these prices the worst case is a loss.
        var zeal = Assess(Stability, "Shrieking Essence of Zeal");
        Assert.True(zeal.WorstNetGain <= zeal.BestNetGain);
    }

    [Fact]
    public void Missing_vaal_orb_price_or_no_essences_means_no_assessment()
    {
        Assert.Null(CorruptionModel.Assess([E("Deafening Essence of Scorn")], 0, Stability, Price, null, out var reason));
        Assert.Equal("Vaal Orb price unknown", reason);

        Assert.Null(CorruptionModel.Assess([], 2, Stability, Price, Vaal, out reason));
        Assert.Equal("no recognised essence", reason);
    }

    [Fact]
    public void Expected_gain_counts_each_possible_outcome_as_equally_likely()
    {
        var transformMean = (37.63 + 36.3 + 45.47 + 33.14) / 4 - 2.03;

        // Stability: tier +1 (nothing for Deafening) or transform, half each.
        var stability = Assess(Stability, "Deafening Essence of Misery");
        Assert.Equal(0.5 * 0 + 0.5 * transformMean - Vaal, stability.ExpectedNetGain, 6);

        // No scarab: no change, tier +1, added essence (estimated mean), transform.
        var plain = Assess(Plain, "Deafening Essence of Misery");
        var addMean = plain.Outcomes.Single(o => o.Outcome == CorruptionOutcome.AddEssence).UniformMeanGain!.Value;
        Assert.Equal(0.25 * 0 + 0.25 * addMean + 0.25 * transformMean - Vaal, plain.ExpectedNetGain, 6);

        // Crystal Resonance: copying an existing essence is worth nothing on average.
        var resonance = Assess(Resonance, "Deafening Essence of Scorn", "Deafening Essence of Dread");
        Assert.Equal(-Vaal, resonance.ExpectedNetGain, 6);
    }

    [Fact]
    public void Outlier_price_in_candidate_group_does_not_distort_transform_or_add_expected_values()
    {
        // Setup where Shrieking Spite has an extreme troll listing of 95c, while Deafening Spite is 9.61c.
        // Group C essence (Suffering) transforms into Group D (Loathing 1c, Zeal 1.25c, Anguish 1c, Spite 95c).
        // Prices go through EssenceValues as in the plugin: its tier cap brings the 95c listing down to
        // the Deafening price, and the (mean) expected value must stay below the default CORRUPT bar.
        var customPrices = new Dictionary<string, double>(Prices)
        {
            ["Shrieking Essence of Suffering"] = 1.0,
            ["Deafening Essence of Suffering"] = 1.09,
            ["Shrieking Essence of Loathing"] = 1.0,
            ["Shrieking Essence of Zeal"] = 1.25,
            ["Shrieking Essence of Anguish"] = 1.0,
            ["Shrieking Essence of Spite"] = 95.0,
            ["Deafening Essence of Spite"] = 9.61,
        };

        var values = EssenceValues.Compute(e => customPrices.TryGetValue(e.Name, out var p) ? p : 1.0, marketReady: true);
        Assert.Equal(9.61, values[E("Shrieking Essence of Spite")].Chaos!.Value, 6);

        var a = CorruptionModel.Assess(
            [E("Shrieking Essence of Suffering")],
            0,
            Plain,
            e => values[e].Chaos,
            0.61,
            out _,
            e => values[e].IsApproximate)!;

        // Uncapped, the troll listing made this +7.38c.
        Assert.True(a.ExpectedNetGain < TrackingPreferences.DefaultMinExpectedProfit, $"Expected EV below the CORRUPT bar, but got {a.ExpectedNetGain}");
    }

    [Fact]
    public void Expected_value_requires_a_valid_distribution()
    {
        Assert.Equal(0.25 * 10 + 0.75 * 2, CorruptionModel.ExpectedValue([0.25, 0.75], [10, 2]), 9);

        Assert.Throws<ArgumentException>(() => CorruptionModel.ExpectedValue([0.5, 0.4], [1, 1]));        // sums to 0.9
        Assert.Throws<ArgumentException>(() => CorruptionModel.ExpectedValue([1.2, -0.2], [1, 1]));       // negative
        Assert.Throws<ArgumentException>(() => CorruptionModel.ExpectedValue([double.NaN, 1], [1, 1]));   // NaN
        Assert.Throws<ArgumentException>(() => CorruptionModel.ExpectedValue([1], [1, 2]));               // lengths
        Assert.Throws<ArgumentException>(() => CorruptionModel.ExpectedValue([], []));
    }

    [Fact]
    public void Estimated_values_are_reported_and_make_the_range_approximate()
    {
        var a = CorruptionModel.Assess([E("Deafening Essence of Misery")], 0, Stability, Price, Vaal, out _,
            e => e.Name == "Deafening Essence of Misery")!;

        Assert.Equal(new[] { "Deafening Essence of Misery" }, a.EstimatedItems);
        Assert.True(a.IsApproximate);
    }
}

public class OutcomeClassifierTests
{
    private static CorruptionOutcome? Classify(string[] before, string[] after, out bool ambiguous, CorruptionSetup? setup = null) =>
        OutcomeClassifier.Classify(before.Select(E).ToArray(), after.Select(E).ToArray(), out ambiguous,
            setup is { } s ? CorruptionModel.ApplicableOutcomes(s) : null);

    [Fact]
    public void Recognises_each_outcome()
    {
        Assert.Equal(CorruptionOutcome.TierUpgrade, Classify(["Screaming Essence of Misery", "Shrieking Essence of Zeal"], ["Shrieking Essence of Misery", "Deafening Essence of Zeal"], out _));
        Assert.Equal(CorruptionOutcome.AddEssence, Classify(["Screaming Essence of Misery"], ["Screaming Essence of Misery", "Wailing Essence of Fear"], out _));
        Assert.Equal(CorruptionOutcome.Transform, Classify(["Shrieking Essence of Envy", "Muttering Essence of Fear"], ["Essence of Horror", "Weeping Essence of Rage"], out _));
        Assert.Equal(CorruptionOutcome.ResonanceReplace, Classify(["Deafening Essence of Scorn", "Deafening Essence of Zeal"], ["Deafening Essence of Scorn", "Deafening Essence of Scorn"], out _));
        Assert.Equal(CorruptionOutcome.NoChange, Classify(["Screaming Essence of Misery", "Deafening Essence of Zeal"], ["Screaming Essence of Misery", "Deafening Essence of Zeal"], out var ambiguous));
        Assert.False(ambiguous);
    }

    [Fact]
    public void A_single_unchanged_essence_could_be_no_change_or_a_resonance_copy()
    {
        Assert.Equal(CorruptionOutcome.NoChange, Classify(["Screaming Essence of Misery"], ["Screaming Essence of Misery"], out var ambiguous));
        Assert.True(ambiguous);

        // Knowing the setup removes the ambiguity.
        Assert.Equal(CorruptionOutcome.NoChange, Classify(["Screaming Essence of Misery"], ["Screaming Essence of Misery"], out ambiguous, new CorruptionSetup(false, false)));
        Assert.False(ambiguous);
        Assert.Equal(CorruptionOutcome.ResonanceReplace, Classify(["Screaming Essence of Misery"], ["Screaming Essence of Misery"], out ambiguous, new CorruptionSetup(false, true)));
        Assert.False(ambiguous);
    }

    [Fact]
    public void With_stability_an_unchanged_deafening_monster_was_upgraded()
    {
        Assert.Equal(CorruptionOutcome.TierUpgrade, Classify(["Deafening Essence of Scorn"], ["Deafening Essence of Scorn"], out var ambiguous, new CorruptionSetup(true, false)));
        Assert.False(ambiguous);
    }

    [Fact]
    public void An_outcome_the_setup_rules_out_is_still_reported_when_nothing_else_fits()
    {
        // Setup says Stability, but an essence was added: report it rather than "unclassified".
        Assert.Equal(CorruptionOutcome.AddEssence, Classify(["Screaming Essence of Misery"], ["Screaming Essence of Misery", "Wailing Essence of Fear"], out _, new CorruptionSetup(true, false)));
    }

    [Fact]
    public void All_deafening_upgrade_is_indistinguishable_from_no_change()
    {
        Assert.Equal(CorruptionOutcome.NoChange, Classify(["Deafening Essence of Scorn"], ["Deafening Essence of Scorn"], out var ambiguous));
        Assert.True(ambiguous);
    }

    [Fact]
    public void Unexplained_changes_are_unclassified()
    {
        Assert.Null(Classify(["Deafening Essence of Scorn"], ["Weeping Essence of Wrath"], out _));
    }
}

public class CorruptionLogTests
{
    private static CorruptionRecord Record(CorruptionOutcome? outcome, double? before, double? after) => new(
        new DateTime(2026, 10, 23, 1, 2, 3, DateTimeKind.Utc), "Strand, the \"best\" map", ["Deafening Essence of Misery"], ["Essence of Horror"],
        outcome, false, "meds", new CorruptionSetup(true, false), true, before, after, 0.4, 0);

    [Fact]
    public void Tallies_outcomes_and_realised_gain()
    {
        var log = new CorruptionLog();
        log.Record(Record(CorruptionOutcome.Transform, 2.0, 37.6), null);
        log.Record(Record(CorruptionOutcome.TierUpgrade, 2.0, 2.0), null);
        log.Record(Record(null, null, null), null);

        Assert.Equal(3, log.Total);
        Assert.Equal(1, log.CountOf(CorruptionOutcome.Transform));
        Assert.Equal(1, log.Unclassified);
        Assert.Equal(2, log.RealizedGainCount);
        Assert.Equal((37.6 - 2.0 - 0.4) + (-0.4), log.RealizedGainSum, 9);
    }

    [Fact]
    public void Tracks_session_kills_mistakes_and_missed()
    {
        var log = new CorruptionLog();
        log.RecordKill();
        log.RecordKill();
        log.RecordMissed();

        // 1 recommended corruption, 1 mistaken corruption
        var good = new CorruptionRecord(
            DateTime.UtcNow, "Map", ["Misery"], ["Horror"],
            CorruptionOutcome.Transform, false, "meds", new CorruptionSetup(false, false),
            true, 10.0, 30.0, 0.3, 0);

        var bad = new CorruptionRecord(
            DateTime.UtcNow, "Map", ["Greed"], ["Greed"],
            CorruptionOutcome.NoChange, false, "meds", new CorruptionSetup(false, false),
            false, 1.0, 1.0, 0.3, 0);

        log.Record(good, null);
        log.Record(bad, null);

        Assert.Equal(2, log.TotalKilled);
        Assert.Equal(2, log.Total);
        Assert.Equal(1, log.RecommendedCorruptions);
        Assert.Equal(1, log.MistakenCorruptions);
        Assert.Equal(1, log.TotalMissed);
        Assert.Equal(2, log.TotalMistakes); // 1 mistaken corruption + 1 missed corruption

        log.ResetSession();
        Assert.Equal(0, log.TotalKilled);
        Assert.Equal(0, log.Total);
        Assert.Equal(0, log.RecommendedCorruptions);
        Assert.Equal(0, log.MistakenCorruptions);
        Assert.Equal(0, log.TotalMissed);
        Assert.Equal(0, log.TotalMistakes);
    }

    [Fact]
    public void Csv_gets_one_header_and_escaped_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bech-test-{Guid.NewGuid():N}.csv");
        try
        {
            var log = new CorruptionLog();
            Assert.Null(log.Record(Record(CorruptionOutcome.Transform, 2.0, 37.6), path));
            Assert.Null(log.Record(Record(CorruptionOutcome.NoChange, 2.0, 2.0), path));

            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(CorruptionLog.CsvHeader, lines[0]);
            Assert.StartsWith("2026-10-23T01:02:03Z,\"Strand, the \"\"best\"\" map\",meds,1,0,1,Transform,0,", lines[1]);
            Assert.EndsWith(",2,37.6,0.4,35.2,0", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
