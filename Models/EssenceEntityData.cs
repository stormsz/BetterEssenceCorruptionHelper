using BetterEssenceCorruptionHelper.Analysis;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using SharpDX;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Tracking data for a single essence entity.
    /// Contains all state needed to track an essence across its lifetime,
    /// including through unload/reload cycles.
    /// </summary>
    internal class EssenceEntityData
    {
        /// <summary>Current entity memory address (changes when essence unloads/reloads)</summary>
        public long Address { get; set; }

        /// <summary>Unique ID assigned by plugin (persists across address changes)</summary>
        public int EntityId { get; set; }

        /// <summary>UI label element showing essence names/tiers</summary>
        public LabelOnGround? Label { get; set; }

        /// <summary>Entity reference (updated when essence reloads)</summary>
        public Entity? Entity { get; set; }

        /// <summary>Current analysis of essence value</summary>
        public EssenceAnalysis Analysis { get; set; }

        /// <summary>Analysis before corruption (for comparison)</summary>
        public EssenceAnalysis? PreviousAnalysis { get; set; }

        /// <summary>Current state (should corrupt, should kill)</summary>
        public EssenceState State { get; set; } = EssenceState.None;

        /// <summary>
        /// Timestamp (tracker clock, ms) of the first pass on which this essence went missing
        /// from the entity list, or null while it is present. Used to ride out transient gaps
        /// instead of instantly declaring the essence killed.
        /// </summary>
        public long? MissingSinceMs { get; set; }

        /// <summary>Last known position (for relinking after unload)</summary>
        public Vector3? LastKnownPosition { get; set; }

        /// <summary>
        /// True if player corrupted this essence. Read by the debug overlay to decide whether to
        /// show the before/after comparison.
        /// </summary>
        public bool WasCorruptedByPlayer { get; set; }
    }
}