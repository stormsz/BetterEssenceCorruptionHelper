using Xunit;

namespace BetterEssenceCorruptionHelper.Tests;

public class EssenceMonsterModTests
{
    /// <summary>Monster mod IDs from the game data (RePoE mods.json, 2026-10-23).</summary>
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
