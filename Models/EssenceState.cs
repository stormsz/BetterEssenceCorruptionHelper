namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Represents the recommended action for an essence monolith.
    /// Used to determine visual indicators and track player decisions.
    /// </summary>
    internal enum EssenceState
    {
        /// <summary>Unknown or uninitialized state (essence not yet analyzed)</summary>
        None,

        /// <summary>
        /// Essence is valuable and should be corrupted.
        /// Applies to essences with MEDS patterns, 6+ total essences, or valuable results.
        /// </summary>
        ShouldCorrupt,

        /// <summary>
        /// Essence is not valuable enough to justify corruption cost.
        /// Player should kill/open without corrupting.
        /// </summary>
        ShouldKill
    }
}