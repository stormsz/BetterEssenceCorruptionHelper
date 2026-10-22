using ExileCore.PoEMemory.Elements;
using BetterEssenceCorruptionHelper.Models;

namespace BetterEssenceCorruptionHelper.Analysis
{
    /// <summary>
    /// Analyzes essence monolith UI labels to extract essence information and determine value.
    /// 
    /// Parses the label hierarchy to:
    /// - Identify essence types and tiers
    /// - Detect corruption status
    /// - Apply value heuristics (MEDS patterns, count thresholds
    /// </summary>
    internal static class EssenceLabelAnalyzer
    {
        /// <summary>MEDS essences (Misery, Envy, Dread, Scorn) - always valuable for corruption</summary>
        private static readonly string[] MedsEssences = ["Misery", "Envy", "Dread", "Scorn"];

        /// <summary>
        /// Analyzes an essence monolith label to extract essence information.
        /// 
        /// Label structure expected:
        /// - Parent label with children
        /// - Second child contains essence text lines
        /// - Each line contains essence name and tier
        /// 
        /// Throws on invalid structure but returns invalid analysis instead of exception.
        /// </summary>
        /// <param name="label">The UI label element for the essence monolith</param>
        /// <returns>Parsed essence analysis with counts and value patterns</returns>
        public static EssenceAnalysis Analyze(LabelOnGround label)
        {
            var result = new EssenceAnalysis();

            // Validate label structure
            if (label?.Label?.Children == null || label.Label.Children.Count < 2)
                return result;

            var containerChild = label.Label.Children[1];
            if (containerChild?.Children == null || containerChild.ChildCount == 0)
                return result;

            try
            {
                // Parse each text line in the label
                foreach (var child in containerChild.Children)
                {
                    if (child == null || string.IsNullOrEmpty(child.Text))
                        continue;

                    AnalyzeTextLine(child.Text, ref result);
                }

                // Apply value heuristics based on parsed data
                DetermineValuablePatterns(ref result);
                result.IsValid = true;
            }
            catch (Exception ex)
            {
                // Log error but don't crash - return invalid analysis
                ExileCore.DebugWindow.LogError($"Essence analysis failed: {ex.Message}");
                result.IsValid = false;
            }

            return result;
        }

        /// <summary>
        /// Parses a single text line from an essence label.
        /// 
        /// Line formats:
        /// - "Corrupted" - indicates already corrupted
        /// - "Essence of {Name}" with tier prefix
        /// - Tier prefixes: Deafening, Shrieking, Screaming, Wailing, Weeping, Muttering
        /// 
        /// Updates analysis in-place for performance.
        /// </summary>
        /// <param name="text">The text line to analyze</param>
        /// <param name="result">Analysis struct to update (ref for performance)</param>
        private static void AnalyzeTextLine(string text, ref EssenceAnalysis result)
        {
            // Early exit for corrupted essences (no further value in parsing)
            if (text.Contains("Corrupted"))
            {
                result.IsCorrupted = true;
                return;
            }

            // Check for MEDS essences (always valuable)
            if (MedsEssences.Any(meds => text.Contains(meds)))
            {
                result.HasMeds = true;
            }

            // Count by tier (highest to lowest for performance)
            if (text.Contains("Deafening"))
            {
                result.DeafeningCount++;
                result.EssenceCount++;
            }
            else if (text.Contains("Shrieking"))
            {
                result.ShriekingCount++;
                result.EssenceCount++;
            }
            else if (text.Contains("Screaming"))
            {
                result.ScreamingCount++;
                result.EssenceCount++;
            }
            // Low tiers are counted toward the total but not tracked individually - nothing reads
            // a per-tier breakdown below Screaming.
            else if (text.Contains("Wailing") || text.Contains("Weeping") || text.Contains("Muttering"))
            {
                result.EssenceCount++;
            }
            else if (text.Contains("Essence of"))
            {
                // Fallback: an essence line with an unlisted tier prefix (e.g. Whispering) or none
                // at all (the corruption-only essences). Matching on bare essence names here only
                // ever added false positives from non-essence label lines.
                result.EssenceCount++;
            }
        }

        /// <summary>
        /// Determines if an essence has a valuable pattern worth corrupting.
        /// 
        /// Value heuristics (in order of priority):
        /// 1. Contains MEDS essences (always corrupt)
        /// 2. Has 6+ total essences (high value cluster)
        ///
        /// There is deliberately no rule for Horror/Delirium/Hysteria/Insanity: they only exist on
        /// already-corrupted monoliths, which are marked to kill before these rules are consulted.
        /// </summary>
        /// <param name="result">Analysis to evaluate and update</param>
        private static void DetermineValuablePatterns(ref EssenceAnalysis result)
        {
            // RULE 1: Always corrupt MEDS essences (can upgrade to Horror/Delirium/Hysteria/Insanity)
            if (result.HasMeds)
            {
                result.HasValuablePattern = true;
                return;
            }

            // RULE 2: Always corrupt 6+ essences (high value - corruption can upgrade multiple)
            if (result.EssenceCount >= 6)
            {
                result.HasValuablePattern = true;
            }
        }
    }
}