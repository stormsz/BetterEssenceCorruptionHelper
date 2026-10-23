using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Models;
using BetterEssenceCorruptionHelper.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Strategy
{
    /// <summary>A recommendation plus the reason for it (shown in the debug overlay).</summary>
    internal readonly record struct Recommendation(EssenceState State, string Reason, EssenceEntry? Trigger = null);

    /// <summary>
    /// What the Profit strategy needs to value a corruption: essence values, the orb's price, the map
    /// setup. <paramref name="Ready"/> is false while Ninja Price isn't delivering essence prices.
    /// </summary>
    internal sealed record ProfitContext(Func<EssenceEntry, double?> Value, Func<EssenceEntry, bool> IsApproximate, double? VaalOrb, CorruptionSetup Setup, bool Ready = true)
    {
        public CorruptionAssessment? Assess(IReadOnlyList<EssenceEntry> essences, int unrecognized) =>
            CorruptionModel.Assess(essences, unrecognized, Setup, Value, VaalOrb, out _, IsApproximate);
    }

    /// <summary>
    /// The one place that turns a monolith's contents into CORRUPT / KILL, following the active
    /// <see cref="StrategyDefinition"/>. Only the Profit strategy reads the user's per-essence choices.
    /// </summary>
    internal static class StrategyEvaluator
    {
        public static Recommendation Evaluate(EssenceAnalysis analysis, TrackingPreferences preferences, ProfitContext? profit = null)
        {
            // Nothing readable yet: show nothing rather than a confident KILL on a monster we
            // couldn't identify (earlier versions showed KILL here).
            if (!analysis.IsValid)
                return new Recommendation(EssenceState.None, "label not readable");

            if (analysis.IsCorrupted)
                return new Recommendation(EssenceState.ShouldKill, "already corrupted");

            // A readable label without a single essence line is one whose text hasn't populated yet
            // (every monster in a monolith holds at least one essence). Without this it fell through
            // to the Profit strategy's "no prices" KILL.
            if (analysis.EssenceCount == 0)
                return new Recommendation(EssenceState.None, "no essences read yet");

            var strategy = preferences.Strategy;
            return strategy.Mode switch
            {
                StrategyMode.NoCorruption => new Recommendation(EssenceState.ShouldKill, "strategy never corrupts"),
                StrategyMode.FixedSet => ByRule(analysis, strategy, preferences.EffectiveMinPackSize),
                _ => ByProfit(analysis, preferences, profit),
            };
        }

        private static Recommendation ByRule(EssenceAnalysis analysis, StrategyDefinition strategy, int minPack)
        {
            foreach (var essence in analysis.Essences)
            {
                if (strategy.CorruptsByRule(essence))
                    return new Recommendation(EssenceState.ShouldCorrupt, $"{strategy.DisplayName}: {essence.Name}", essence);
            }

            // A known type whose tier couldn't be read (unexpected label text): judge it by its type,
            // rather than silently treating e.g. a MEDS essence as something else.
            foreach (var type in analysis.UnresolvedTypes)
            {
                if (EssenceCatalogue.OfType(type).Any(strategy.CorruptsByRule))
                    return new Recommendation(EssenceState.ShouldCorrupt, $"{strategy.DisplayName}: {type} (tier not readable)");
            }

            if (minPack > 0 && analysis.EssenceCount >= minPack)
                return new Recommendation(EssenceState.ShouldCorrupt, $"{analysis.EssenceCount} essences (pack rule: {minPack}+)");

            return new Recommendation(EssenceState.ShouldKill, "nothing the strategy corrupts");
        }

        private static Recommendation ByProfit(EssenceAnalysis analysis, TrackingPreferences preferences, ProfitContext? profit)
        {
            foreach (var essence in analysis.Essences)
            {
                if (preferences.Choice(essence) == EssenceChoice.Always)
                    return new Recommendation(EssenceState.ShouldCorrupt, $"always corrupt: {essence.Name}", essence);
            }

            foreach (var essence in analysis.Essences)
            {
                if (preferences.Choice(essence) == EssenceChoice.Never)
                    return new Recommendation(EssenceState.ShouldKill, $"never corrupt: {essence.Name}", essence);
            }

            if (profit is { Ready: true } && profit.Assess(analysis.Essences, analysis.UnrecognizedEssenceLines.Count) is { } assessment)
            {
                var gain = assessment.ExpectedNetGain;
                return gain >= preferences.EffectiveMinExpectedProfit
                    ? new Recommendation(EssenceState.ShouldCorrupt, $"expected {Format.SignedChaos(gain)}")
                    : new Recommendation(EssenceState.ShouldKill, $"expected {Format.SignedChaos(gain)}");
            }

            // No prices (Ninja Price missing, loading, or without essence data): fall back to the MEDS
            // rule rather than corrupting nothing - or valuing every essence at 0c.
            return analysis.HasMeds
                ? new Recommendation(EssenceState.ShouldCorrupt, "no prices - MEDS rule")
                : new Recommendation(EssenceState.ShouldKill, "no prices");
        }
    }
}
