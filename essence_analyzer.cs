using ExileCore.PoEMemory.Elements;

namespace BetterEssenceCorruptionHelper;

internal static class EssenceLabelAnalyzer
{
    private static readonly string[] MedsEssences = ["Misery", "Envy", "Dread", "Scorn"];

    private static readonly string[] ValuableEssences =
    [
        "Horror", "Delirium", "Hysteria", "Insanity"
    ];

    private static readonly string[] EssenceNames =
    [
        "Anger", "Anguish", "Contempt", "Doubt", "Dread", "Envy", "Fear",
        "Greed", "Hatred", "Loathing", "Misery", "Rage", "Scorn", "Sorrow",
        "Spite", "Suffering", "Torment", "Woe", "Wrath", "Zeal",
        "Horror", "Delirium", "Hysteria", "Insanity"
    ];

    public static EssenceAnalysis Analyze(LabelOnGround label)
    {
        var result = new EssenceAnalysis();

        if (label?.Label?.Children == null || label.Label.Children.Count < 2)
            return result;

        var containerChild = label.Label.Children[1];
        if (containerChild?.Children == null || containerChild.ChildCount == 0)
            return result;

        try
        {
            foreach (var child in containerChild.Children)
            {
                if (child == null || string.IsNullOrEmpty(child.Text))
                    continue;

                AnalyzeTextLine(child.Text, ref result);
            }

            DetermineValuablePatterns(ref result);
            result.IsValid = true;
        }
        catch (Exception ex)
        {
            ExileCore.DebugWindow.LogError($"Essence analysis failed: {ex.Message}");
            result.IsValid = false;
        }

        return result;
    }

    private static void AnalyzeTextLine(string text, ref EssenceAnalysis result)
    {
        // Check for corruption
        if (text.Contains("Corrupted") || text.Contains("Vaal Orb"))
        {
            result.IsCorrupted = true;
            return;
        }

        // Check for MEDS essences (always corrupt these)
        if (MedsEssences.Any(meds => text.Contains(meds)))
        {
            result.HasMeds = true;
        }

        // Check for valuable essences that result from corruption
        if (ValuableEssences.Any(valuable => text.Contains(valuable)))
        {
            result.HasValuableResult = true;
        }

        // Count essence tiers (all 6 tiers)
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
            // Count any essence we might have missed
            result.EssenceCount++;
        }
    }

    private static void DetermineValuablePatterns(ref EssenceAnalysis result)
    {
        // RULE 1: Always corrupt MEDS essences (can upgrade to Horror/Delirium/Hysteria/Insanity)
        if (result.HasMeds)
        {
            result.HasValuablePattern = true;
            return;
        }

        // RULE 2: Always corrupt 6+ essences (high value)
        if (result.EssenceCount >= 6)
        {
            result.HasValuablePattern = true;
            return;
        }

        // RULE 3: Enhanced Einhar's Memory pattern - more flexible thresholds
        if ((result.ScreamingCount >= 1 && result.ShriekingCount >= 2) ||
            (result.ScreamingCount >= 2 && result.ShriekingCount >= 1))
        {
            result.HasValuablePattern = true;
            return;
        }

        // RULE 4: High-tier concentration (2+ Deafening or 3+ Shrieking)
        if (result.DeafeningCount >= 2 || result.ShriekingCount >= 3)
        {
            result.HasValuablePattern = true;
            return;
        }

        // RULE 5: Mixed high-tier combination
        if (result.DeafeningCount >= 1 && result.ShriekingCount >= 2)
        {
            result.HasValuablePattern = true;
            return;
        }

        // RULE 6: Already has valuable corruption results
        if (result.HasValuableResult)
        {
            result.HasValuablePattern = true;
            return;
        }
    }

    private static bool IsKnownEssenceName(string text)
    {
        return EssenceNames.Any(name => text.Contains(name));
    }
}

internal struct EssenceAnalysis
{
    public bool IsValid { get; set; }
    public bool IsCorrupted { get; set; }
    public bool HasMeds { get; set; }
    public bool HasValuableResult { get; set; }
    public int EssenceCount { get; set; }
    public int ScreamingCount { get; set; }
    public int ShriekingCount { get; set; }
    public int DeafeningCount { get; set; }
    public int WailingCount { get; set; }
    public int WeepingCount { get; set; }
    public int MutteringCount { get; set; }
    public bool HasValuablePattern { get; set; }
}