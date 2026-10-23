using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Models;
using Xunit;

namespace BetterEssenceCorruptionHelper.Tests;

public class EssenceMonsterModTests
{
    /// <summary>PoE1 monster mods of generation type "essence" (RePoE mods.json, 2026-10-23).</summary>
    [Theory]
    [InlineData("EssenceDaemonModLifeAndDamageLevel3")]
    [InlineData("EssenceDaemonModLifeAndDamageCorrupt")]
    [InlineData("EssenceDaemonHysteria1")]
    [InlineData("DecayOnHitEssenceMonster1")]
    [InlineData("RemnantOfCorruptionMonsterStats")]
    public void Essence_monster_mods_identify_the_essence_monster(string mod) =>
        Assert.True(EssenceEntityTracker.IsEssenceMonsterMod(mod));

    /// <summary>The pack trapped around the monolith carries supporter mods; ordinary rare mods aren't essence mods.</summary>
    [Theory]
    [InlineData("MonsterSupporterEssenceGreed")]
    [InlineData("MonsterSupporterEssenceWoe")]
    [InlineData("MonsterRareLifeIncrease")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_mods_do_not(string? mod) =>
        Assert.False(EssenceEntityTracker.IsEssenceMonsterMod(mod));
}

public class EssenceDecisionTests
{
    private static readonly EssenceAnalysis Plain = Read("Screaming Essence of Greed", "Shrieking Essence of Woe");
    private static readonly EssenceAnalysis Upgraded = Read("Shrieking Essence of Greed", "Deafening Essence of Woe");
    private static readonly EssenceAnalysis CorruptedLabel = Read("Shrieking Essence of Greed", "Deafening Essence of Woe", "Corrupted");

    private static EssenceAnalysis Read(params string[] lines) => EssenceLabelAnalyzer.AnalyzeLines(lines);

    [Fact]
    public void The_corrupted_line_marks_a_corruption() =>
        Assert.True(EssenceEntityTracker.IsCorruption(Plain, CorruptedLabel));

    [Fact]
    public void Essences_changing_without_the_marker_is_a_corruption() =>
        Assert.True(EssenceEntityTracker.IsCorruption(Plain, Upgraded));

    [Fact]
    public void An_already_corrupted_monster_cannot_be_corrupted_again()
    {
        var reread = Read("Deafening Essence of Greed", "Deafening Essence of Woe", "Corrupted");
        Assert.False(EssenceEntityTracker.IsCorruption(CorruptedLabel, reread));
    }

    [Fact]
    public void A_first_reading_is_not_a_corruption() =>
        Assert.False(EssenceEntityTracker.IsCorruption(EssenceAnalysis.Invalid, CorruptedLabel));

    [Fact]
    public void An_unchanged_reading_is_not_a_corruption() =>
        Assert.False(EssenceEntityTracker.IsCorruption(Plain, Read("Shrieking Essence of Woe", "Screaming Essence of Greed")));

    /// <summary>Enums are internal and xUnit theories must be public, so the cases pass them as ints.</summary>
    [Theory]
    [InlineData((int)EssenceState.ShouldKill, true, (int)EssenceDecision.KilledAsAdvised)]
    [InlineData((int)EssenceState.ShouldCorrupt, true, (int)EssenceDecision.MissedCorruption)]
    [InlineData((int)EssenceState.None, true, (int)EssenceDecision.NotJudged)]
    [InlineData((int)EssenceState.ShouldCorrupt, false, (int)EssenceDecision.NotJudged)]
    public void A_release_without_a_vaal_orb_is_judged_by_the_advice_shown(int shown, bool observed, int expected) =>
        Assert.Equal((EssenceDecision)expected, EssenceEntityTracker.JudgeRelease((EssenceState)shown, observed));
}
