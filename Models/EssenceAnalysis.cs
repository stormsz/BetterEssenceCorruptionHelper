namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Represents the analysis results of an essence monolith.
    /// Contains parsed information about essence types, tiers, and value patterns.
    /// </summary>
    internal struct EssenceAnalysis
    {
        /// <summary>Indicates whether the analysis was successful (valid label structure)</summary>
        public bool IsValid { get; set; }

        /// <summary>True if the essence has already been corrupted by the game (cannot be corrupted again)</summary>
        public bool IsCorrupted { get; set; }

        /// <summary>True if the essence contains MEDS essences (Misery, Envy, Dread, Scorn)</summary>
        public bool HasMeds { get; set; }

        /// <summary>True if the essence contains valuable result essences (Horror, Delirium, Hysteria, Insanity)</summary>
        public bool HasValuableResult { get; set; }

        /// <summary>Total number of essences in the monolith (sum of all tiers)</summary>
        public int EssenceCount { get; set; }

        /// <summary>Number of Screaming-tier essences (Tier 4)</summary>
        public int ScreamingCount { get; set; }

        /// <summary>Number of Shrieking-tier essences (Tier 5)</summary>
        public int ShriekingCount { get; set; }

        /// <summary>Number of Deafening-tier essences (Tier 6)</summary>
        public int DeafeningCount { get; set; }

        /// <summary>Number of Wailing-tier essences (Tier 3)</summary>
        public int WailingCount { get; set; }

        /// <summary>Number of Weeping-tier essences (Tier 2)</summary>
        public int WeepingCount { get; set; }

        /// <summary>Number of Muttering-tier essences (Tier 1)</summary>
        public int MutteringCount { get; set; }

        /// <summary>
        /// True if the essence has a valuable pattern worth corrupting.
        /// Determined by: MEDS essences OR 6+ total essences OR valuable result essences.
        /// </summary>
        public bool HasValuablePattern { get; set; }
    }
}