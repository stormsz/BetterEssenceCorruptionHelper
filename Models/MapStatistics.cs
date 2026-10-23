namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Statistics tracker for essence corruption decisions, scoped to one map instance (one per
    /// <see cref="MapSession"/>). Only touched from Tick and Render, which share a thread, so no locking.
    ///
    /// Counted when a freed essence monster dies, from its <see cref="EssenceDecision"/>:
    /// - TotalKilled: freed essence monsters killed
    /// - TotalCorrupted: corrupted as advised (CORRUPT)
    /// - TotalMissed: released without a Vaal Orb although the advice was CORRUPT
    /// - TotalMistakes: corrupted although the advice was KILL
    /// </summary>
    internal class MapStatistics
    {
        /// <summary>Essence monsters killed - counted when the released monster dies, not when its monolith is opened</summary>
        public int TotalKilled { get; private set; }

        /// <summary>Essences corrupted as advised (counted at the kill)</summary>
        public int TotalCorrupted { get; private set; }

        /// <summary>Essences released without a Vaal Orb although the advice was CORRUPT (counted at the kill)</summary>
        public int TotalMissed { get; private set; }

        /// <summary>Essences corrupted although the advice was KILL (counted at the kill)</summary>
        public int TotalMistakes { get; private set; }

        /// <summary>Bumped on every change, so the stats window only rebuilds its text when needed</summary>
        public int Version { get; private set; }

        public void IncrementKilled() { TotalKilled++; Version++; }

        public void IncrementCorrupted() { TotalCorrupted++; Version++; }

        public void IncrementMissed() { TotalMissed++; Version++; }

        public void IncrementMistakes() { TotalMistakes++; Version++; }
    }
}
