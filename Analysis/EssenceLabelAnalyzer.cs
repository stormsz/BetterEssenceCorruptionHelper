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
    /// - Apply value heuristics (MEDS patterns, count thresholds)
    /// 
    /// Performance considerations:
    /// - Uses string.Contains for fast matching (essence names are short)
    /// - Early exit on corruption detection
    /// - Array lookups for known essence types
    /// </summary>
    internal static class EssenceLabelAnalyzer
    {
        /// <summary>MEDS essences (Misery, Envy, Dread, Scorn) - always valuable for corruption</summary>
        private static readonly string[] MedsEssences = ["Misery", "Envy", "Dread", "Scorn"];

        /// <summary>Valuable result essences from corruption (Horror, Delirium, Hysteria, Insanity)</summary>
        private static readonly string[] ValuableEssences =
        [
            "Horror", "Delirium", "Hysteria", "Insanity"
        ];

        /// <summary>Complete list of known essence names for validation</summary>
        private static readonly string[] EssenceNames =
        [
            "Anger", "Anguish", "Contempt", "Doubt", "Dread", "Envy", "Fear",
            "Greed", "Hatred", "Loathing", "Misery", "Rage", "Scorn", "Sorrow",
            "Spite", "Suffering", "Torment", "Woe", "Wrath", "Zeal",
            "Horror", "Delirium", "Hysteria", "Insanity"
        ];

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

            // Check for valuable result essences (post-corruption)
            if (ValuableEssences.Any(valuable => text.Contains(valuable)))
            {
                result.HasValuableResult = true;
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
            else if (text.Contains("Wailing"))
            {
                result.WailingCount++;
                result.EssenceCount++;
            }
            else if (text.Contains("Weeping"))
            {
                result.WeepingCount++;
                result.EssenceCount++;
            }
            else if (text.Contains("Muttering"))
            {
                result.MutteringCount++;
                result.EssenceCount++;
            }
            else if (text.Contains("Essence of") || IsKnownEssenceName(text))
            {
                // Fallback: count any essence we might have missed
                // Typically indicates parsing issue or new essence type
                result.EssenceCount++;
            }
        }

        /// <summary>
        /// Determines if an essence has a valuable pattern worth corrupting.
        /// 
        /// Value heuristics (in order of priority):
        /// 1. Contains MEDS essences (always corrupt)
        /// 2. Has 6+ total essences (high value cluster)
        /// 3. Contains valuable result essences (already good outcomes)
        /// 
        /// These rules match common PoE essence farming strategies.
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
                return;
            }

            // RULE 3: Already has valuable results (no need to corrupt but still valuable)
            if (result.HasValuableResult)
            {
                result.HasValuablePattern = true;
                return;
            }
        }

        /// <summary>
        /// Validates if text contains a known essence name.
        /// Used as fallback for counting essences when tier parsing fails.
        /// </summary>
        /// <param name="text">Text to check</param>
        /// <returns>True if text contains any known essence name</returns>
        private static bool IsKnownEssenceName(string text)
        {
            return EssenceNames.Any(name => text.Contains(name));
        }
    }
}