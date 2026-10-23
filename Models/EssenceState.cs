namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// The recommended action for a trapped essence monster: what the CORRUPT / KILL indicator shows,
    /// and what a Vaal Orb (or releasing it untouched) is judged against. One enum rather than two
    /// flags, as the three cases are mutually exclusive. Where the essence is in its life is tracked
    /// separately by <see cref="EssencePhase"/>.
    /// </summary>
    internal enum EssenceState
    {
        /// <summary>No recommendation: the label hasn't been read (or couldn't be)</summary>
        None,

        /// <summary>The active strategy says corrupt it with a Vaal Orb.</summary>
        ShouldCorrupt,

        /// <summary>The active strategy says release and kill it without corrupting.</summary>
        ShouldKill
    }
}