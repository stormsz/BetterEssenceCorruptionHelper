using BetterEssenceCorruptionHelper.Analysis;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Tracking data for a single essence entity.
    /// Keyed by the game's entity ID, which (unlike the memory address) stays the same when an
    /// essence leaves and re-enters the loaded area, so no relinking is needed.
    /// </summary>
    internal class EssenceEntityData
    {
        /// <summary>Game entity ID - the tracking key</summary>
        public uint Id { get; set; }

        /// <summary>Current entity memory address (used to match ground labels, shown in debug)</summary>
        public long Address { get; set; }

        /// <summary>Sequential display number assigned by plugin (per map)</summary>
        public int EntityId { get; set; }

        /// <summary>UI label element showing essence names/tiers</summary>
        public LabelOnGround? Label { get; set; }

        /// <summary>Entity reference (updated every pass the entity is present)</summary>
        public Entity? Entity { get; set; }

        /// <summary>Current analysis of essence value</summary>
        public EssenceAnalysis Analysis { get; set; }

        /// <summary>Analysis before corruption (for comparison)</summary>
        public EssenceAnalysis? PreviousAnalysis { get; set; }

        /// <summary>Current state (should corrupt, should kill)</summary>
        public EssenceState State { get; set; } = EssenceState.None;

        /// <summary>
        /// True once Monolith.IsOpened has been read as false for this essence. A kill is only
        /// counted on a real closed -> opened transition, so a misread flag can never hide an
        /// essence that is still waiting to be opened.
        /// </summary>
        public bool WasSeenClosed { get; set; }

        /// <summary>
        /// True once the monolith has been opened (monster released). The kill is counted on that
        /// transition, exactly once.
        /// </summary>
        public bool IsOpened { get; set; }

        /// <summary>Last Monolith.OpenStage read from the entity (debug overlay only)</summary>
        public int OpenStage { get; set; }

        /// <summary>
        /// True if player corrupted this essence. Read by the debug overlay to decide whether to
        /// show the before/after comparison.
        /// </summary>
        public bool WasCorruptedByPlayer { get; set; }
    }
}
