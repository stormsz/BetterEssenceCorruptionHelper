using BetterEssenceCorruptionHelper.Catalogue;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BetterEssenceCorruptionHelper.Tests;

/// <summary>
/// Checks the catalogue against the poe.ninja data Ninja Price actually holds: the JSON it caches
/// under its NinjaData/&lt;league&gt; folder. Uses the same keying as its CurrencyOverviewData
/// (items joined to lines by id, keyed by item name, ChaosEquivalent = primaryValue x chaos rate).
///
/// Set BECH_NINJA_DATA to that folder; it defaults to the local HUD's Allflame cache. Skipped when
/// the files aren't there (CI), since the data is per machine and per league.
/// </summary>
public class NinjaMarketDataTests
{
    private static readonly string DataDir = Environment.GetEnvironmentVariable("BECH_NINJA_DATA")
        ?? @"D:\bin\POE\ExileApi\ExileApi-Compiled-329.19\Plugins\Temp\Get-Chaos-Value\NinjaData\Allflame";

    /// <summary>Ninja Price's LinesByName: name -> chaos per item.</summary>
    private static Dictionary<string, double> LinesByName(string file)
    {
        var path = Path.Combine(DataDir, file);
        if (!File.Exists(path))
            Assert.Skip($"Ninja Price cache not found at {path}");

        var root = JObject.Parse(File.ReadAllText(path));
        var primary = (string?)root["core"]?["primary"];
        var rate = primary == "divine" ? (double)root["core"]!["rates"]!["chaos"]! : 1.0;
        var lines = root["lines"]!.ToDictionary(l => (string)l["id"]!, l => (double?)l["primaryValue"] ?? 0);

        return root["items"]!
            .Where(i => lines.ContainsKey((string)i["id"]!))
            .ToDictionary(i => (string)i["name"]!, i => lines[(string)i["id"]!] * rate);
    }

    [Fact]
    public void Every_listed_essence_is_in_the_catalogue_under_the_same_name()
    {
        var market = LinesByName("Essences2.json");

        var unknown = market.Keys.Where(name => EssenceCatalogue.FindByName(name) == null).ToList();
        Assert.Empty(unknown);
        Assert.All(market.Values, v => Assert.True(v > 0));

        // Coverage is reported, not asserted: poe.ninja only lists what trades.
        var listed = EssenceCatalogue.All.Count(e => market.ContainsKey(e.Name));
        TestContext.Current.SendDiagnosticMessage($"{listed}/{EssenceCatalogue.Count} catalogue essences listed in {DataDir}");
        Assert.True(listed > 0);
    }

    [Fact]
    public void Corruption_targets_and_deafening_meds_are_priced()
    {
        var market = LinesByName("Essences2.json");
        foreach (var name in new[] { "Essence of Horror", "Essence of Insanity", "Essence of Delirium", "Essence of Hysteria", "Deafening Essence of Scorn" })
            Assert.True(market.ContainsKey(name), $"{name} not listed");
    }

    [Fact]
    public void Vaal_orb_is_in_the_currency_feed_under_its_catalogue_name()
    {
        var currency = LinesByName("Currency2.json");
        Assert.True(currency.TryGetValue(EssenceCatalogue.VaalOrb.Name, out var chaos));
        Assert.InRange(chaos, 0.01, 50); // a single Vaal Orb, not a stack
    }
}
