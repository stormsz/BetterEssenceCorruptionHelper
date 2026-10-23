namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Statistics tracker for essence corruption decisions, scoped to one map instance.
    /// Only touched from the main runner and Render, which share a thread, so no locking.
    ///
    /// Tracks:
    /// - TotalKilled: All essences player interacted with
    /// - TotalCorrupted: Essences successfully corrupted (were valuable)
    /// - TotalMissed: Valuable essences killed without corrupting (mistakes)
    /// - TotalMistakes: Non-valuable essences corrupted (wasted corruption attempts)
    /// </summary>
    internal class MapStatistics
    {
        /// <summary>Essence monsters killed - counted when the released monster dies, not when its monolith is opened</summary>
        public int TotalKilled { get; private set; }

        /// <summary>Number of essences that were successfully corrupted (were valuable)</summary>
        public int TotalCorrupted { get; private set; }

        /// <summary>Number of valuable essences that were killed without corrupting (missed opportunities)</summary>
        public int TotalMissed { get; private set; }

        /// <summary>Number of non-valuable essences that were corrupted (wasted corruption attempts)</summary>
        public int TotalMistakes { get; private set; }

        /// <summary>Bumped on every change, so the stats window only rebuilds its text when needed</summary>
        public int Version { get; private set; }

        public void IncrementKilled() { TotalKilled++; Version++; }

        public void IncrementCorrupted() { TotalCorrupted++; Version++; }

        public void IncrementMissed() { TotalMissed++; Version++; }

        public void IncrementMistakes() { TotalMistakes++; Version++; }

        public void Reset()
        {
            TotalKilled = 0;
            TotalCorrupted = 0;
            TotalMissed = 0;
            TotalMistakes = 0;
            Version++;
        }
    }
}
