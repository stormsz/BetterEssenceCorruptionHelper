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

        /// <summary>Original address when first discovered (for debugging)</summary>
        public long FirstSeenAddress { get; set; }

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

        /// <summary>State before last transition (for debugging)</summary>
        public EssenceState PreviousState { get; set; } = EssenceState.None;

        /// <summary>Last known position (for relinking after unload)</summary>
        public Vector3? LastKnownPosition { get; set; }

        /// <summary>True if player corrupted this essence</summary>
        public bool WasCorruptedByPlayer { get; set; }

        /// <summary>True if essence was killed/opened</summary>
        public bool WasKilled { get; set; }

        /// <summary>True if valuable essence was killed without corrupting</summary>
        public bool MissedCorruption { get; set; }

        /// <summary>True if non-valuable essence was corrupted (mistake)</summary>
        public bool MistakenCorruption { get; set; }
    }
}