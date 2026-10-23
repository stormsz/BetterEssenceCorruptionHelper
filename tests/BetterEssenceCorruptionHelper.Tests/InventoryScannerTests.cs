using BetterEssenceCorruptionHelper.Strategy;
using Xunit;

namespace BetterEssenceCorruptionHelper.Tests
{
    public class InventoryScannerTests
    {
        [Theory]
        [InlineData("Metadata/Items/Currency/CurrencyVaal", true)]
        [InlineData("Metadata/Items/Currency/CurrencyVaalOrb", true)]
        [InlineData("metadata/items/currency/currencyvaal", true)]
        [InlineData("Some/Path/CurrencyVaal", true)]
        [InlineData("Metadata/Items/Currency/CurrencyChaos", false)]
        [InlineData("Metadata/Items/Currency/CurrencyExalted", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsVaalOrbPath_IdentifiesCorrectPaths(string? path, bool expected)
        {
            Assert.Equal(expected, InventoryScanner.IsVaalOrbPath(path!));
        }

        [Fact]
        public void ShouldAlert_WhenDisabled_ReturnsFalse()
        {
            var alert = InventoryScanner.ShouldAlert(
                alertEnabled: false,
                vaalCount: 0,
                minThreshold: 5,
                strategyMode: StrategyMode.Profit);

            Assert.False(alert);
        }

        [Fact]
        public void ShouldAlert_WhenStrategyDoesNotUseCorruption_ReturnsFalse()
        {
            // Deafening Boss Farming has Mode = NoCorruption (does not use Vaal Orbs)
            var alert = InventoryScanner.ShouldAlert(
                alertEnabled: true,
                vaalCount: 0,
                minThreshold: 5,
                strategyMode: StrategyMode.NoCorruption);

            Assert.False(alert);
        }

        [Theory]
        [InlineData(0, 5, true)]
        [InlineData(1, 5, true)]
        [InlineData(4, 5, true)]
        [InlineData(5, 5, false)]
        [InlineData(6, 5, false)]
        [InlineData(20, 5, false)]
        [InlineData(0, 1, true)]
        [InlineData(1, 1, false)]
        public void ShouldAlert_WhenStrategyRequiresCorruption_AlertsBelowThreshold(int count, int threshold, bool expectedAlert)
        {
            var alertProfit = InventoryScanner.ShouldAlert(
                alertEnabled: true,
                vaalCount: count,
                minThreshold: threshold,
                strategyMode: StrategyMode.Profit);

            var alertMeds = InventoryScanner.ShouldAlert(
                alertEnabled: true,
                vaalCount: count,
                minThreshold: threshold,
                strategyMode: StrategyMode.FixedSet);

            Assert.Equal(expectedAlert, alertProfit);
            Assert.Equal(expectedAlert, alertMeds);
        }
    }
}
