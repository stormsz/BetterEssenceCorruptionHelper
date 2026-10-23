namespace BetterEssenceCorruptionHelper.Strategy
{
    /// <summary>
    /// Whether the map setup the game reports fits the selected strategy, phrased as what to do about
    /// it - shown in the settings and, when something is off, in the map stats window.
    /// </summary>
    internal static class SetupStatus
    {
        public enum Level
        {
            Ok,
            Warning,
            Problem,
        }

        /// <param name="Title">One direct instruction or fact, e.g. "Remove Crystal Resonance from your Atlas tree".</param>
        /// <param name="Text">Why, in one short sentence.</param>
        public readonly record struct Status(Level Level, string Title, string Text);

        public static Status? Describe(StrategyDefinition strategy, SetupDetector detector) =>
            Describe(strategy, detector.CrystalResonanceAllocated, detector.CurrentMap);

        /// <param name="strategy">The selected strategy</param>
        /// <param name="resonanceAllocated">Crystal Resonance on the atlas tree, null if unknown</param>
        /// <param name="currentMap">What is in effect in the current map, null outside maps</param>
        /// <returns>Null when there is nothing to say.</returns>
        public static Status? Describe(StrategyDefinition strategy, bool? resonanceAllocated, CorruptionSetup? currentMap)
        {
            // Inside a map, its own stats are the truth; outside, the atlas tree.
            var resonance = currentMap?.CrystalResonance ?? resonanceAllocated;

            return strategy.Mode switch
            {
                StrategyMode.FixedSet when resonance == true => new Status(Level.Problem,
                    "Remove Crystal Resonance from your Atlas tree",
                    "With it, Vaal Orbs can't give Hysteria, Insanity, Horror or Delirium."),

                StrategyMode.FixedSet when currentMap is { StabilityScarab: false } && strategy.ExpectedSetup.StabilityScarab => new Status(Level.Warning,
                    "No Essence Scarab of Stability in this map",
                    "Without it, a Vaal Orb can also do nothing."),

                StrategyMode.Profit when resonance == true => new Status(Level.Warning,
                    "Crystal Resonance is allocated",
                    "Nothing is worth a Vaal Orb with it. The table shows what would be, without it."),

                StrategyMode.NoCorruption when resonance == false => new Status(Level.Warning,
                    "Allocate Crystal Resonance on your Atlas tree",
                    "Deafening farming relies on it."),

                _ => null,
            };
        }
    }
}
