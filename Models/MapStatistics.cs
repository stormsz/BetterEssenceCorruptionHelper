using System;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Thread-safe statistics tracker for essence corruption decisions.
    /// Uses Interlocked operations for atomic increments (safe across parallel threads).
    /// 
    /// Tracks:
    /// - TotalKilled: All essences player interacted with
    /// - TotalCorrupted: Essences successfully corrupted (were valuable)
    /// - TotalMissed: Valuable essences killed without corrupting (mistakes)
    /// - TotalMistakes: Non-valuable essences corrupted (wasted corruption attempts)
    /// </summary>
    internal class MapStatistics
    {
        private int _totalKilled;
        private int _totalCorrupted;
        private int _totalMissed;
        private int _totalMistakes;

        /// <summary>Total number of essences killed/opened by the player</summary>
        public int TotalKilled => _totalKilled;

        /// <summary>Number of essences that were successfully corrupted (were valuable)</summary>
        public int TotalCorrupted => _totalCorrupted;

        /// <summary>Number of valuable essences that were killed without corrupting (missed opportunities)</summary>
        public int TotalMissed => _totalMissed;

        /// <summary>Number of non-valuable essences that were corrupted (wasted corruption attempts)</summary>
        public int TotalMistakes => _totalMistakes;

        /// <summary>Thread-safe increment using Interlocked</summary>
        public void IncrementKilled() => Interlocked.Increment(ref _totalKilled);

        /// <summary>Thread-safe increment using Interlocked</summary>
        public void IncrementCorrupted() => Interlocked.Increment(ref _totalCorrupted);

        /// <summary>Thread-safe increment using Interlocked</summary>
        public void IncrementMissed() => Interlocked.Increment(ref _totalMissed);

        /// <summary>Thread-safe increment using Interlocked</summary>
        public void IncrementMistakes() => Interlocked.Increment(ref _totalMistakes);

        /// <summary>Thread-safe reset using Interlocked.Exchange</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _totalKilled, 0);
            Interlocked.Exchange(ref _totalCorrupted, 0);
            Interlocked.Exchange(ref _totalMissed, 0);
            Interlocked.Exchange(ref _totalMistakes, 0);
        }
    }
}