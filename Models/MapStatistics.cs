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
        /// <summary>Total number of essences killed/opened by the player</summary>
        public int TotalKilled { get; private set; }

        /// <summary>Number of essences that were successfully corrupted (were valuable)</summary>
        public int TotalCorrupted { get; private set; }

        /// <summary>Number of valuable essences that were killed without corrupting (missed opportunities)</summary>
        public int TotalMissed { get; private set; }

        /// <summary>Number of non-valuable essences that were corrupted (wasted corruption attempts)</summary>
        public int TotalMistakes { get; private set; }

        public void IncrementKilled() => TotalKilled++;

        public void IncrementCorrupted() => TotalCorrupted++;

        public void IncrementMissed() => TotalMissed++;

        public void IncrementMistakes() => TotalMistakes++;

        public void Reset()
        {
            TotalKilled = 0;
            TotalCorrupted = 0;
            TotalMissed = 0;
            TotalMistakes = 0;
        }
    }
}
