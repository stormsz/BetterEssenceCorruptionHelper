using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Strategy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Analysis
{
    /// <summary>
    /// What a Vaal Orb on an essence monster can do (poewiki "Corrupting essence monsters", 3.27+):
    /// "one of the following outcomes chosen at random" - no change, all tiers +1, an additional
    /// essence, or every essence transformed into a random type of the next group. Crystal Resonance
    /// replaces that with "replaces all Essences with one of the Essences on the Imprisoned Monster".
    /// </summary>
    internal enum CorruptionOutcome
    {
        NoChange,
        TierUpgrade,
        AddEssence,
        Transform,
        ResonanceReplace,
    }

    /// <summary>
    /// The change in essence value one outcome can cause, relative to not corrupting and before the
    /// Vaal Orb cost. <see cref="UniformMeanGain"/> averages over the outcome's own random choices
    /// (which type a transform picks, which essence Resonance copies) assuming each is equally likely;
    /// null where even that can't be stated (the added essence's type and tier are undocumented).
    /// </summary>
    internal readonly record struct OutcomeRange(CorruptionOutcome Outcome, double MinGain, double MaxGain, double? UniformMeanGain);

    internal enum CorruptionVerdict
    {
        /// <summary>Every possible outcome beats killing the monster uncorrupted, at listing prices.</summary>
        AlwaysGains,

        /// <summary>Some outcomes gain, some lose: worth it or not depends on the unpublished odds.</summary>
        DependsOnOdds,

        /// <summary>No possible outcome makes up for the Vaal Orb, at listing prices.</summary>
        AlwaysLoses,
    }

    /// <summary>
    /// Value analysis for corrupting one monster. All values are chaos at poe.ninja listing prices
    /// (via Ninja Price) - asking prices, not guaranteed sale proceeds.
    /// </summary>
    internal sealed record CorruptionAssessment(
        CorruptionSetup Setup,
        IReadOnlyList<OutcomeRange> Outcomes,
        double CurrentValue,
        double VaalOrbCost,
        double WorstNetGain,
        double BestNetGain,
        CorruptionVerdict Verdict,
        IReadOnlyList<string> UnpricedItems,
        IReadOnlyList<string> EstimatedItems,
        double ExpectedNetGain)
    {
        /// <summary>Some value involved was estimated, capped or missing: the range is approximate.</summary>
        public bool IsApproximate => UnpricedItems.Count > 0 || EstimatedItems.Count > 0;
    }

    /// <summary>
    /// Corruption value analysis.
    ///
    /// <para>
    /// <b>Probabilities.</b> No source gives numbers for essence corruption. poewiki only says "chosen at
    /// random" (its "each outcome has an equal chance" applies to Vaal Orbs on equipment, not essences),
    /// and the 3.29 guides only say the Stability scarab gives a "high chance". The checks covered
    /// poewiki (Essence, Vaal Orb, List of essences), Maxroll's Corrupted and Deafening Essences
    /// guides, poedb and a web search for community sample data, all on 2026-10-23. So no expected
    /// value is computed by default.
    /// </para>
    ///
    /// <para>
    /// What <i>is</i> justified without probabilities: the gain range of every outcome the setup
    /// allows, and hence the worst and best case (<see cref="CorruptionAssessment.WorstNetGain"/>,
    /// <see cref="CorruptionAssessment.BestNetGain"/>). Those bounds hold for any probability
    /// distribution, so "always gains"/"always loses" verdicts are sound.
    /// </para>
    ///
    /// <para>
    /// <see cref="CorruptionAssessment.ExpectedNetGain"/> - what the Profit strategy decides on - counts
    /// every outcome the setup allows as equally likely (the plain reading of "chosen at random"), and
    /// the random picks inside an outcome too. The added essence's type and tier are undocumented, so
    /// that outcome counts at its lower bound (it adds nothing). When real odds are known (e.g. from
    /// enough observed outcomes in the corruption log) they can go through <see cref="ExpectedValue"/>.
    /// </para>
    ///
    /// <para>
    /// Approximations, all reported: an essence without a usable value counts as 0c and is listed in
    /// <see cref="CorruptionAssessment.UnpricedItems"/>; one whose value was estimated or capped (see
    /// EssenceValues) is listed in <see cref="CorruptionAssessment.EstimatedItems"/>. The added essence of <see cref="CorruptionOutcome.AddEssence"/> is assumed to be a
    /// natural (Group A–E) essence, so its gain is bounded by [0, priciest natural essence]. Monster
    /// kill time and any other drops are ignored.
    /// </para>
    /// </summary>
    internal static class CorruptionModel
    {
        private const double Tolerance = 1e-9;

        /// <summary>The outcomes a Vaal Orb can produce under this setup.</summary>
        public static IReadOnlyList<CorruptionOutcome> ApplicableOutcomes(CorruptionSetup setup)
        {
            // Resonance's wording describes the Vaal Orb's whole effect; how it combines with the
            // Stability scarab is undocumented, so Resonance wins (Maxroll pairs it with neither).
            if (setup.CrystalResonance)
                return [CorruptionOutcome.ResonanceReplace];

            if (setup.StabilityScarab)
                return [CorruptionOutcome.TierUpgrade, CorruptionOutcome.Transform];

            return [CorruptionOutcome.NoChange, CorruptionOutcome.TierUpgrade, CorruptionOutcome.AddEssence, CorruptionOutcome.Transform];
        }

        /// <summary>
        /// Assesses corrupting a monster holding <paramref name="essences"/>. Returns null - with the
        /// reason - when there's nothing defensible to say (no Vaal Orb price, no recognised essence).
        /// </summary>
        public static CorruptionAssessment? Assess(
            IReadOnlyList<EssenceEntry> essences,
            int unrecognizedCount,
            CorruptionSetup setup,
            Func<EssenceEntry, double?> price,
            double? vaalOrbCost,
            out string? unavailableReason,
            Func<EssenceEntry, bool>? isEstimate = null)
        {
            unavailableReason = null;
            if (vaalOrbCost is not { } cost)
            {
                unavailableReason = "Vaal Orb price unknown";
                return null;
            }

            if (essences.Count == 0)
            {
                unavailableReason = "no recognised essence";
                return null;
            }

            var unpriced = new SortedSet<string>(StringComparer.Ordinal);
            if (unrecognizedCount > 0)
                unpriced.Add($"{unrecognizedCount} unrecognised essence line(s)");

            var estimated = new SortedSet<string>(StringComparer.Ordinal);
            double P(EssenceEntry e)
            {
                if (price(e) is { } value)
                {
                    if (isEstimate?.Invoke(e) == true)
                        estimated.Add(e.Name);
                    return value;
                }

                unpriced.Add(e.Name);
                return 0;
            }

            var current = essences.Sum(P);
            var outcomes = new List<OutcomeRange>();
            foreach (var outcome in ApplicableOutcomes(setup))
            {
                outcomes.Add(outcome switch
                {
                    CorruptionOutcome.NoChange => new OutcomeRange(outcome, 0, 0, 0),
                    CorruptionOutcome.TierUpgrade => TierUpgrade(essences, P),
                    CorruptionOutcome.AddEssence => AddEssence(price, unpriced),
                    CorruptionOutcome.Transform => Transform(essences, P),
                    CorruptionOutcome.ResonanceReplace => ResonanceReplace(essences, current, P),
                    _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
                });
            }

            var worst = outcomes.Min(o => o.MinGain) - cost;
            var best = outcomes.Max(o => o.MaxGain) - cost;
            var verdict = worst >= -Tolerance ? CorruptionVerdict.AlwaysGains
                : best < -Tolerance ? CorruptionVerdict.AlwaysLoses
                : CorruptionVerdict.DependsOnOdds;

            var equalOdds = outcomes.Select(_ => 1.0 / outcomes.Count).ToArray();
            var expectedGain = ExpectedValue(equalOdds, outcomes.Select(o => o.UniformMeanGain ?? o.MinGain).ToArray()) - cost;

            return new CorruptionAssessment(setup, outcomes, current, cost, worst, best, verdict, unpriced.ToArray(), estimated.ToArray(), expectedGain);
        }

        /// <summary>
        /// EV = Σ pᵢ·Vᵢ. Throws unless <paramref name="probabilities"/> is a valid distribution
        /// (finite, non-negative, summing to 1) of the same length as <paramref name="values"/>.
        /// </summary>
        public static double ExpectedValue(IReadOnlyList<double> probabilities, IReadOnlyList<double> values)
        {
            if (probabilities.Count != values.Count)
                throw new ArgumentException("one probability per value is required");
            if (probabilities.Count == 0)
                throw new ArgumentException("at least one outcome is required");

            double total = 0, ev = 0;
            for (var i = 0; i < probabilities.Count; i++)
            {
                var p = probabilities[i];
                if (!double.IsFinite(p) || p < 0)
                    throw new ArgumentException($"invalid probability {p}");

                total += p;
                ev += p * values[i];
            }

            if (Math.Abs(total - 1) > 1e-6)
                throw new ArgumentException($"probabilities sum to {total}, not 1");

            return ev;
        }

        /// <summary>
        /// Highest known price among natural (A–E) essences - the cap on what an added essence can be
        /// worth. Unpriced essences can't raise the cap and aren't reported: they aren't on the monster.
        /// </summary>
        private static double MaxNaturalPrice(Func<EssenceEntry, double?> price, out int unpricedNaturals)
        {
            var max = 0d;
            unpricedNaturals = 0;
            foreach (var e in EssenceCatalogue.All)
            {
                if (e.Group == EssenceGroup.F)
                    continue;

                if (price(e) is { } p)
                    max = Math.Max(max, p);
                else
                    unpricedNaturals++;
            }

            return max;
        }

        private static OutcomeRange TierUpgrade(IReadOnlyList<EssenceEntry> essences, Func<EssenceEntry, double> price)
        {
            var gain = 0d;
            foreach (var e in essences)
            {
                if (EssenceCatalogue.UpgradeOf(e) is { } upgraded && upgraded != e)
                    gain += price(upgraded) - price(e);
            }

            return new OutcomeRange(CorruptionOutcome.TierUpgrade, gain, gain, gain);
        }

        private static OutcomeRange AddEssence(Func<EssenceEntry, double?> price, ISet<string> unpriced)
        {
            // Prices are never negative, so adding an essence can't lose value; the cap is the priciest
            // natural essence. Which type/tier gets added is undocumented, so there is no mean.
            var cap = MaxNaturalPrice(price, out var unpricedNaturals);
            if (unpricedNaturals > 0)
                unpriced.Add($"added-essence cap ignores {unpricedNaturals} unlisted essence(s)");

            return new OutcomeRange(CorruptionOutcome.AddEssence, 0, cap, null);
        }

        private static OutcomeRange Transform(IReadOnlyList<EssenceEntry> essences, Func<EssenceEntry, double> price)
        {
            // Each essence transforms independently, so the sum of per-essence extremes is attainable.
            double min = 0, max = 0, mean = 0;
            foreach (var e in essences)
            {
                var candidates = EssenceCatalogue.TransformCandidates(e);
                if (candidates.Count == 0)
                    continue;

                var own = price(e);
                double cMin = double.MaxValue, cMax = double.MinValue, cSum = 0;
                foreach (var c in candidates)
                {
                    var p = price(c);
                    cMin = Math.Min(cMin, p);
                    cMax = Math.Max(cMax, p);
                    cSum += p;
                }

                min += cMin - own;
                max += cMax - own;
                mean += cSum / candidates.Count - own;
            }

            return new OutcomeRange(CorruptionOutcome.Transform, min, max, mean);
        }

        private static OutcomeRange ResonanceReplace(IReadOnlyList<EssenceEntry> essences, double current, Func<EssenceEntry, double> price)
        {
            // Every essence becomes a copy of one of them: n copies of the chosen essence.
            var n = essences.Count;
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            foreach (var chosen in essences)
            {
                var value = n * price(chosen) - current;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                sum += value;
            }

            return new OutcomeRange(CorruptionOutcome.ResonanceReplace, min, max, sum / n);
        }
    }
}
