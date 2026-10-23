using BetterEssenceCorruptionHelper.Catalogue;
using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// What an essence monolith's label says: which essences the monster holds and whether it is
    /// already corrupted. Immutable, so a reference can be kept as the "before" of a corruption.
    /// </summary>
    internal sealed class EssenceAnalysis
    {
        /// <summary>The label couldn't be read (missing or unexpected structure).</summary>
        public static readonly EssenceAnalysis Invalid = new(false, false, [], []);

        public EssenceAnalysis(bool isValid, bool isCorrupted, IReadOnlyList<EssenceEntry> essences, IReadOnlyList<string> unrecognizedEssenceLines,
            IReadOnlyList<string>? unresolvedTypes = null)
        {
            IsValid = isValid;
            IsCorrupted = isCorrupted;
            Essences = essences;
            UnrecognizedEssenceLines = unrecognizedEssenceLines;
            UnresolvedTypes = unresolvedTypes ?? [];
        }

        /// <summary>Indicates whether the analysis was successful (valid label structure)</summary>
        public bool IsValid { get; }

        /// <summary>True if the monster is already corrupted (a monster can only be corrupted once)</summary>
        public bool IsCorrupted { get; }

        /// <summary>Essences matched to the catalogue, in label order</summary>
        public IReadOnlyList<EssenceEntry> Essences { get; }

        /// <summary>Label lines that look like essences ("Essence of ...") but match no catalogue entry</summary>
        public IReadOnlyList<string> UnrecognizedEssenceLines { get; }

        /// <summary>
        /// Known essence types (e.g. "Misery") read from <see cref="UnrecognizedEssenceLines"/> whose tier
        /// couldn't be resolved - so an unexpected tier word doesn't hide a MEDS essence.
        /// </summary>
        public IReadOnlyList<string> UnresolvedTypes { get; }

        /// <summary>Total number of essences on the monster, recognised or not</summary>
        public int EssenceCount => Essences.Count + UnrecognizedEssenceLines.Count;

        public bool HasMeds => Essences.Any(e => e.IsMeds) || UnresolvedTypes.Any(t => EssenceCatalogue.GroupOf(t) == EssenceGroup.E);

        public int CountTier(EssenceTier tier) => Essences.Count(e => e.Tier == tier);

        /// <summary>Same essences (in any order) and corruption state - i.e. nothing worth re-evaluating changed.</summary>
        public bool SameContentAs(EssenceAnalysis other)
        {
            if (IsValid != other.IsValid || IsCorrupted != other.IsCorrupted ||
                Essences.Count != other.Essences.Count ||
                UnrecognizedEssenceLines.Count != other.UnrecognizedEssenceLines.Count)
                return false;

            return Essences.OrderBy(e => e.Name).SequenceEqual(other.Essences.OrderBy(e => e.Name)) &&
                   UnrecognizedEssenceLines.OrderBy(l => l).SequenceEqual(other.UnrecognizedEssenceLines.OrderBy(l => l));
        }
    }
}
