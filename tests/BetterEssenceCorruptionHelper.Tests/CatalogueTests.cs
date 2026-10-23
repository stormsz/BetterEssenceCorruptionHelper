using BetterEssenceCorruptionHelper.Catalogue;
using Xunit;

namespace BetterEssenceCorruptionHelper.Tests;

public class CatalogueTests
{
    private const string MetadataPrefix = "Metadata/Items/Currency/";

    internal static EssenceEntry E(string name) => EssenceCatalogue.FindByName(name) ?? throw new KeyNotFoundException(name);

    private static List<(string Name, string Metadata, bool DropEnabled)> WikiRows() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "poewiki-essences.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('|'))
            .Select(p => (p[0], MetadataPrefix + p[1], p[2] == "1"))
            .ToList();

    [Fact]
    public void Catalogue_has_every_group_with_the_wiki_tier_ranges()
    {
        Assert.Equal(104, EssenceCatalogue.Count);
        Assert.Equal(28, EssenceCatalogue.InGroup(EssenceGroup.A).Count()); // 4 types x 7 tiers
        Assert.Equal(24, EssenceCatalogue.InGroup(EssenceGroup.B).Count()); // 4 x 6 (Muttering+)
        Assert.Equal(20, EssenceCatalogue.InGroup(EssenceGroup.C).Count()); // 4 x 5 (Weeping+)
        Assert.Equal(16, EssenceCatalogue.InGroup(EssenceGroup.D).Count()); // 4 x 4 (Wailing+)
        Assert.Equal(12, EssenceCatalogue.InGroup(EssenceGroup.E).Count()); // 4 x 3 (Screaming+)
        Assert.Equal(4, EssenceCatalogue.InGroup(EssenceGroup.F).Count());

        // Types that earlier drafts put in the wrong group or left out entirely.
        Assert.Equal(EssenceGroup.C, E("Weeping Essence of Wrath").Group);
        Assert.Equal(EssenceGroup.C, E("Weeping Essence of Doubt").Group);
        Assert.Equal(EssenceGroup.C, E("Weeping Essence of Rage").Group);
        Assert.Equal(EssenceGroup.D, E("Wailing Essence of Anguish").Group);
        Assert.Null(EssenceCatalogue.FindByName("Weeping Essence of Anguish"));
    }

    [Fact]
    public void Every_wiki_essence_is_catalogued_with_identical_name_and_metadata()
    {
        var wiki = WikiRows();
        Assert.Equal(105, wiki.Count);

        foreach (var (name, metadata, dropEnabled) in wiki)
        {
            var entry = EssenceCatalogue.FindByName(name);
            if (EssenceCatalogue.KnownExclusions.ContainsKey(metadata))
            {
                Assert.Null(entry);
                Assert.False(dropEnabled); // Desolation: excluded because it can't drop
                continue;
            }

            Assert.NotNull(entry);
            Assert.Equal(metadata, entry.Metadata);
        }

        var wikiNames = wiki.Select(w => w.Name).ToHashSet();
        Assert.All(EssenceCatalogue.All, e => Assert.Contains(e.Name, wikiNames));
    }

    [Fact]
    public void Names_and_metadata_are_unique()
    {
        Assert.Equal(EssenceCatalogue.Count, EssenceCatalogue.All.Select(e => e.Name).Distinct().Count());
        Assert.Equal(EssenceCatalogue.Count, EssenceCatalogue.All.Select(e => e.Metadata).Distinct().Count());
        Assert.Equal(EssenceCatalogue.Count + 1, EssenceCatalogue.MarketItems.Distinct().Count());
    }

    [Fact]
    public void Meds_are_exactly_misery_envy_dread_scorn_at_screaming_to_deafening()
    {
        var meds = EssenceCatalogue.All.Where(e => e.IsMeds).ToList();
        Assert.Equal(12, meds.Count);
        Assert.Equal(new[] { "Dread", "Envy", "Misery", "Scorn" }, meds.Select(e => e.TypeName).Distinct().Order());
        Assert.Equal(new[] { EssenceTier.Screaming, EssenceTier.Shrieking, EssenceTier.Deafening }, meds.Select(e => e.Tier).Distinct().Order());
        Assert.All(meds, e => Assert.Equal(EssenceGroup.E, e.Group));
    }

    [Fact]
    public void Corruption_only_essences_have_no_tier_prefix()
    {
        var f = EssenceCatalogue.InGroup(EssenceGroup.F).ToList();
        Assert.Equal(new[] { "Essence of Delirium", "Essence of Horror", "Essence of Hysteria", "Essence of Insanity" }, f.Select(e => e.Name).Order());
        Assert.All(f, e => Assert.True(e.IsCorruptionOnly));
        Assert.All(f, e => Assert.Equal(EssenceTier.Corrupted, e.Tier));
    }

    [Fact]
    public void Vaal_orb_is_the_currency_item_that_replaced_remnants()
    {
        Assert.Equal("Vaal Orb", EssenceCatalogue.VaalOrb.Name);
        Assert.Equal("Metadata/Items/Currency/CurrencyCorrupt", EssenceCatalogue.VaalOrb.Metadata);
        Assert.Equal(MarketFeed.Currency, EssenceCatalogue.VaalOrb.Feed);
        Assert.DoesNotContain(EssenceCatalogue.MarketItems, i => i.Name.Contains("Remnant"));
    }

    [Fact]
    public void Tier_upgrade_raises_one_tier_and_caps_at_deafening()
    {
        Assert.Same(E("Shrieking Essence of Misery"), EssenceCatalogue.UpgradeOf(E("Screaming Essence of Misery")));
        Assert.Same(E("Deafening Essence of Scorn"), EssenceCatalogue.UpgradeOf(E("Deafening Essence of Scorn")));
        Assert.Null(EssenceCatalogue.UpgradeOf(E("Essence of Horror")));
    }

    [Fact]
    public void Transform_moves_to_the_next_group_raising_the_tier_when_needed()
    {
        // poewiki's own example: Muttering Fear (B) becomes a Weeping Group C essence.
        var fear = EssenceCatalogue.TransformCandidates(E("Muttering Essence of Fear"));
        Assert.Equal(4, fear.Count);
        Assert.All(fear, c => Assert.Equal((EssenceGroup.C, EssenceTier.Weeping), (c.Group, c.Tier)));

        var greed = EssenceCatalogue.TransformCandidates(E("Whispering Essence of Greed"));
        Assert.All(greed, c => Assert.Equal((EssenceGroup.B, EssenceTier.Muttering), (c.Group, c.Tier)));

        var spite = EssenceCatalogue.TransformCandidates(E("Deafening Essence of Spite"));
        Assert.All(spite, c => Assert.Equal((EssenceGroup.E, EssenceTier.Deafening), (c.Group, c.Tier)));

        // MEDS of any tier become one of the four corruption-only essences.
        foreach (var tier in new[] { "Screaming", "Shrieking", "Deafening" })
        {
            var meds = EssenceCatalogue.TransformCandidates(E($"{tier} Essence of Envy"));
            Assert.Equal(EssenceCatalogue.InGroup(EssenceGroup.F).Select(e => e.Name).Order(), meds.Select(e => e.Name).Order());
        }

        Assert.Empty(EssenceCatalogue.TransformCandidates(E("Essence of Delirium")));
    }
}
