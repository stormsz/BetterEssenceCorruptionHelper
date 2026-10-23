namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Everything tracked for one map instance, keyed by the area hash.
    ///
    /// Entity IDs are per instance, so the essences seen in a map stay valid for as long as that
    /// instance exists: through hideout trips, portals back in, and the game unloading a monolith or
    /// its monster when you walk far away. Keeping them here means an essence coming back into range
    /// is the same essence (same display number, same pending outcome), never a new one.
    /// </summary>
    internal sealed class MapSession(uint areaHash)
    {
        /// <summary>Area hash of the map instance (0 = not bound to a map yet)</summary>
        public uint AreaHash { get; } = areaHash;

        /// <summary>Every essence monolith seen in this map, by game entity ID.</summary>
        public Dictionary<uint, EssenceEntityData> Essences { get; } = new();

        /// <summary>Tracked essences by the entity ID of their linked monster.</summary>
        public Dictionary<uint, EssenceEntityData> ByMonsterId { get; } = new();

        /// <summary>Kill and corruption totals for this map.</summary>
        public MapStatistics Stats { get; } = new();

        /// <summary>Last display number handed out (essences are numbered 1, 2, ... per map).</summary>
        public int LastDisplayNumber { get; set; }

        /// <summary>Tracker clock (ms) when this map was last entered, to evict the oldest session.</summary>
        public long LastEnteredMs { get; set; }
    }
}
