using BetterEssenceCorruptionHelper.Catalogue;
using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Analysis
{
    /// <summary>
    /// Works out which corruption outcome happened from a monster's essences before and after the
    /// Vaal Orb. Types picked at random (transform, Resonance) are matched by group and tier only.
    /// </summary>
    internal static class OutcomeClassifier
    {
        /// <summary>
        /// The observed outcome, or null when the change matches none of them. <paramref name="ambiguous"/>
        /// is set when more than one outcome produces the same result (e.g. a tier upgrade of an
        /// all-Deafening monster changes nothing); the first match is returned.
        /// <paramref name="possible"/> (the outcomes the map setup allows) narrows the candidates when
        /// one of them matches; if none does, the setup was probably different and all are considered.
        /// </summary>
        public static CorruptionOutcome? Classify(IReadOnlyList<EssenceEntry> before, IReadOnlyList<EssenceEntry> after, out bool ambiguous,
            IReadOnlyCollection<CorruptionOutcome>? possible = null)
        {
            var matches = new List<CorruptionOutcome>();

            if (SameMultiset(before.Select(e => e.Name), after.Select(e => e.Name)))
                matches.Add(CorruptionOutcome.NoChange);

            if (after.Count == before.Count + 1 && IsSubMultiset(before.Select(e => e.Name), after.Select(e => e.Name)))
                matches.Add(CorruptionOutcome.AddEssence);

            if (after.Count == before.Count)
            {
                var upgraded = before.Select(e => EssenceCatalogue.UpgradeOf(e)?.Name ?? "");
                if (SameMultiset(upgraded, after.Select(e => e.Name)))
                    matches.Add(CorruptionOutcome.TierUpgrade);

                // Each essence moves to the next group at its (possibly raised) tier; the type is random.
                var expected = before.Select(e => EssenceCatalogue.TransformCandidates(e).FirstOrDefault())
                    .Select(c => c == null ? "" : $"{c.Group}/{c.Tier}");
                if (before.All(e => !e.IsCorruptionOnly) && SameMultiset(expected, after.Select(e => $"{e.Group}/{e.Tier}")))
                    matches.Add(CorruptionOutcome.Transform);

                if (after.Count > 0 && after.All(e => e == after[0]) && before.Contains(after[0]))
                    matches.Add(CorruptionOutcome.ResonanceReplace);
            }

            if (possible != null && matches.Any(possible.Contains))
                matches.RemoveAll(m => !possible.Contains(m));

            ambiguous = matches.Count > 1;
            return matches.Count > 0 ? matches[0] : null;
        }

        private static bool SameMultiset(IEnumerable<string> a, IEnumerable<string> b) =>
            a.OrderBy(x => x, System.StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, System.StringComparer.Ordinal));

        private static bool IsSubMultiset(IEnumerable<string> small, IEnumerable<string> big)
        {
            var remaining = big.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            foreach (var item in small)
            {
                if (!remaining.TryGetValue(item, out var count) || count == 0)
                    return false;
                remaining[item] = count - 1;
            }

            return true;
        }
    }
}
