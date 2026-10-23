using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using Vector2 = System.Numerics.Vector2;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// Pending corruption outcome staged when corrupted, committed only when the monster is killed.
    /// </summary>
    internal sealed record PendingCorruptionOutcome(
        EssenceAnalysis Before,
        EssenceAnalysis After,
        bool WasRecommended,
        EssenceState OldState);

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
        public EssenceAnalysis Analysis { get; set; } = EssenceAnalysis.Invalid;

        /// <summary>Analysis before corruption (for comparison)</summary>
        public EssenceAnalysis? PreviousAnalysis { get; set; }

        /// <summary>Pending corruption outcome, committed only when the released monster dies.</summary>
        public PendingCorruptionOutcome? PendingCorruption { get; set; }

        /// <summary>Pending missed corruption, committed only when the released monster dies.</summary>
        public bool PendingMissed { get; set; }

        /// <summary>Bumped whenever <see cref="Analysis"/> changes content, so cached derived values refresh</summary>
        public int AnalysisVersion { get; set; }

        /// <summary>Label line count when last analysed - a change triggers an immediate re-analysis</summary>
        public long LabelSignature { get; set; } = -1;

        /// <summary>Tracker clock (ms) of the last label analysis</summary>
        public long LastAnalysisMs { get; set; } = long.MinValue;

        /// <summary>Current state (should corrupt, should kill)</summary>
        public EssenceState State { get; set; } = EssenceState.None;

        /// <summary>Why <see cref="State"/> was chosen (debug overlay)</summary>
        public string StateReason { get; set; } = "";

        /// <summary>True while the monolith entity is in the loaded entity list (EntityAdded/EntityRemoved)</summary>
        public bool IsLoaded { get; set; } = true;

        /// <summary>Cached corruption value analysis, valid for <see cref="AssessmentKey"/></summary>
        public Analysis.CorruptionAssessment? Assessment { get; set; }

        /// <summary>Why <see cref="Assessment"/> is null, when it is</summary>
        public string? AssessmentUnavailableReason { get; set; }

        /// <summary>(analysis version, price version, preferences version) the cached assessment was built from</summary>
        public (int Analysis, int Prices, int Preferences) AssessmentKey { get; set; } = (-1, -1, -1);

        /// <summary>Indicator text for <see cref="Assessment"/> (built with it, not per frame)</summary>
        public string? ValueText { get; set; }

        /// <summary>
        /// True once this essence has been seen closed (Monolith.IsOpened false and still
        /// targetable). A kill is only counted on a real closed -> opened transition, so a misread
        /// flag can never hide an essence that is still waiting to be opened.
        /// </summary>
        public bool WasSeenClosed { get; set; }

        /// <summary>
        /// True once the monolith has been opened (monster released). A missed corruption is judged
        /// at that point; the kill is only counted later, when the released monster dies.
        /// </summary>
        public bool IsOpened { get; set; }

        /// <summary>Grid position of the monolith - where its trapped monster sits - for linking the two</summary>
        public Vector2? GridPos { get; set; }

        /// <summary>Entity ID of the trapped essence monster, once linked to this monolith</summary>
        public uint? MonsterId { get; set; }

        /// <summary>Last seen entity of the linked monster</summary>
        public Entity? Monster { get; set; }

        /// <summary>Rarity rank of the linked monster when linked (White 0 .. Unique 3), to compare candidates</summary>
        public int MonsterRarityRank { get; set; }

        /// <summary>Grid distance between the linked monster and the monolith when linked, to compare candidates</summary>
        public float MonsterLinkDistance { get; set; }

        /// <summary>True once the released monster has been seen dead - that is when the kill is counted</summary>
        public bool MonsterKilled { get; set; }

        /// <summary>Grid distance from the player when the linked monster was last seen</summary>
        public float MonsterLastDistance { get; set; }

        /// <summary>Tracker clock (ms) when the released monster went missing from the scan, or null while present</summary>
        public long? MonsterMissingSinceMs { get; set; }

        /// <summary>Last monster snapshot written to the diagnostics log, to only log changes</summary>
        public string? LastMonsterDiagnostic { get; set; }

        /// <summary>Last Monolith.OpenStage read from the entity (debug overlay only)</summary>
        public int OpenStage { get; set; }

        /// <summary>Last tracking snapshot written to the diagnostics log, to only log changes</summary>
        public string? LastDiagnostic { get; set; }

        /// <summary>Cheap key of the inputs to <see cref="LastDiagnostic"/>, so the text is only rebuilt on change</summary>
        public int LastDiagnosticKey { get; set; }

        /// <summary>
        /// True if player corrupted this essence. Read by the debug overlay to decide whether to
        /// show the before/after comparison.
        /// </summary>
        public bool WasCorruptedByPlayer { get; set; }
    }
}
