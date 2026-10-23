using BetterEssenceCorruptionHelper.Pricing;
using System.Globalization;

namespace BetterEssenceCorruptionHelper.UI
{
    /// <summary>
    /// One consistent way to print chaos values and price states. Plain ASCII/Latin-1 only: the HUD's
    /// ImGui font has no glyphs for symbols like "≈" or "…".
    /// </summary>
    internal static class Format
    {
        /// <summary>"0.39c", "2.03c", "33.1c", "450c".</summary>
        public static string Chaos(double value) => Number(value) + "c";

        /// <summary>"+12.5c" / "-0.4c".</summary>
        public static string SignedChaos(double value)
        {
            // Sign of what is printed: -0.001 rounds to "0" and shows as "+0c", not "-0c".
            var number = Number(System.Math.Abs(value));
            return (value < 0 && number != "0" ? "-" : "+") + number + "c";
        }

        private static string Number(double value)
        {
            var abs = System.Math.Abs(value);
            var format = abs < 10 ? "0.##" : abs < 100 ? "0.#" : "0";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>Short text for a raw price: the value, or a state marker - never a bare "0c" for missing data.</summary>
        public static string Price(PriceInfo price) => price.State switch
        {
            PriceState.Ok => Chaos(price.Chaos),
            PriceState.Stale => Chaos(price.Chaos) + " (old)",
            PriceState.Unlisted => "no price",
            PriceState.ProviderLoading => "loading",
            PriceState.ProviderUnavailable => "no Ninja Price",
            PriceState.Invalid => "invalid",
            PriceState.Error => "error",
            _ => "-",
        };

        /// <summary>
        /// Short text for the value used. Unlisted or unknown prices show as "-" (calculations still use the cheap floor).
        /// </summary>
        public static string Value(EssenceValue value) => value switch
        {
            { Chaos: not { } } => "-",
            { IsApproximate: true } => "-",
            _ => Chaos(value.Chaos.Value),
        };

        public static string Age(long ms) =>
            ms < 1000 ? "<1s" : ms < 60_000 ? $"{ms / 1000}s" : ms < 3_600_000 ? $"{ms / 60_000}m" : $"{ms / 3_600_000}h";
    }
}
