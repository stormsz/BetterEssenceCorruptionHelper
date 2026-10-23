using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using Vector2 = System.Numerics.Vector2;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>Where an essence is in its life: trapped in the monolith, released and fighting, or dead.</summary>
    internal enum EssencePhase
    {
        /// <summary>Monster still trapped; the label can be read and the monster corrupted.</summary>
        Trapped,

        /// <summary>Monolith opened: the monster is free and its kill is awaited, wherever it happens.</summary>
        Released,

        /// <summary>The freed monster died; the kill and its outcome have been counted.</summary>
        Killed,
    }

    /// <summary>
    /// What the player did with the trapped monster, judged against the advice (CORRUPT / KILL) on
    /// screen at the time. Decided when the Vaal Orb lands or when the monolith opens; counted in the
    /// stats only once the freed monster dies.
    /// </summary>
    internal enum EssenceDecision
    {
        /// <summary>Still trapped, nothing done to it yet.</summary>
        Undecided,

        /// <summary>Vaal Orb used, as the CORRUPT advice said.</summary>
        CorruptedAsAdvised,

        /// <summary>Vaal Orb used although the advice was KILL.</summary>
        CorruptedAgainstAdvice,

        /// <summary>Released without a Vaal Orb, as the KILL advice said: not every essence is worth corrupting.</summary>
        KilledAsAdvised,

        /// <summary>Released without a Vaal Orb although the advice was CORRUPT.</summary>
        MissedCorruption,

        /// <summary>Already corrupted when first seen (corrupted before we watched it); it can't be corrupted twice, so nothing to judge.</summary>
        AlreadyCorrupted,

        /// <summary>Released before any advice was shown, or while it wasn't loaded: nothing to judge.</summary>
        NotJudged,
    }

    /// <summary>A Vaal Orb used on the trapped monster: its contents before and after.</summary>
    internal sealed record CorruptionObservation(EssenceAnalysis Before, EssenceAnalysis After);

    /// <summary>
    /// Tracking data for a single essence entity.
    /// Keyed by the game's entity ID, which (unlike the memory address) stays the same when an
    /// essence leaves and re-enters the loaded area, so no relinking is needed. Kept per map in
    /// <see cref="MapSession"/>; the entity, label and monster references are dropped whenever the
    /// game unloads them and picked up again when they come back.
    /// </summary>
    internal class EssenceEntityData
    {
        /// <summary>Game entity ID - the tracking key</summary>
        public uint Id { get; set; }

        /// <summary>Sequential display number assigned by plugin (per map)</summary>
        public int EntityId { get; set; }

        /// <summary>UI label element showing essence names/tiers</summary>
        public LabelOnGround? Label { get; set; }

        /// <summary>Monolith entity while loaded, null while unloaded</summary>
        public Entity? Entity { get; set; }

        /// <summary>Current analysis of essence value</summary>
        public EssenceAnalysis Analysis { get; set; } = EssenceAnalysis.Invalid;

        /// <summary>Bumped whenever <see cref="Analysis"/> changes content, so cached derived values refresh</summary>
        public int AnalysisVersion { get; set; }

        /// <summary>Label structure (child counts, see EssenceLabelAnalyzer.ReadContentSignature) when last read - a change triggers an immediate re-read</summary>
        public long LabelSignature { get; set; } = -1;

        /// <summary>Tracker clock (ms) at which the label is re-read even if its line count didn't change</summary>
        public long NextAnalysisMs { get; set; } = long.MinValue;

        /// <summary>
        /// A label reading that differs from <see cref="Analysis"/> but hasn't been read the same way
        /// twice yet. A label caught while it is being built reads with lines missing, which would look
        /// like a corruption adding an essence; it is only committed once a second read agrees.
        /// </summary>
        public EssenceAnalysis? UnconfirmedAnalysis { get; set; }

        /// <summary>
        /// What the strategy recommends for the trapped monster (drives the CORRUPT / KILL indicator).
        /// Kept after release, as the decision the player was shown.
        /// </summary>
        public EssenceState State { get; set; } = EssenceState.None;

        /// <summary>Why <see cref="State"/> was chosen (debug overlay)</summary>
        public string StateReason { get; set; } = "";

        /// <summary>Lifecycle: trapped, released, killed.</summary>
        public EssencePhase Phase { get; set; } = EssencePhase.Trapped;

        /// <summary>True once the monolith has been opened (monster released or already dead).</summary>
        public bool IsOpened => Phase != EssencePhase.Trapped;

        /// <summary>True once the freed monster has been seen dead - that is when the kill is counted.</summary>
        public bool MonsterKilled => Phase == EssencePhase.Killed;

        /// <summary>What the player did with it; counted when the freed monster dies.</summary>
        public EssenceDecision Decision { get; set; } = EssenceDecision.Undecided;

        /// <summary>The player's Vaal Orb on this monster (contents before/after), or null if they didn't corrupt it.</summary>
        public CorruptionObservation? Corruption { get; set; }

        /// <summary>True while the monolith entity is in the loaded entity list (EntityAdded/EntityRemoved)</summary>
        public bool IsLoaded { get; set; } = true;

        /// <summary>True once the monolith has read targetable since it was last loaded</summary>
        public bool SeenTargetableSinceLoad { get; set; }

        /// <summary>Tracker clock (ms) since the monolith has read untargetable, or null while targetable</summary>
        public long? UntargetableSinceMs { get; set; }

        /// <summary>Cached corruption value analysis, valid for <see cref="AssessmentKey"/></summary>
        public Analysis.CorruptionAssessment? Assessment { get; set; }

        /// <summary>Why <see cref="Assessment"/> is null, when it is</summary>
        public string? AssessmentUnavailableReason { get; set; }

        /// <summary>(analysis, price, preferences, setup) versions the cached assessment was built from</summary>
        public (int Analysis, int Prices, int Preferences, int Setup) AssessmentKey { get; set; } = (-1, -1, -1, -1);

        /// <summary>Indicator text for <see cref="Assessment"/> (built with it, not per frame)</summary>
        public string? ValueText { get; set; }

        /// <summary>Tracker clock (ms) when the monolith was opened</summary>
        public long OpenedAtMs { get; set; }

        /// <summary>Grid position of the monolith - where its trapped monster sits - for linking the two</summary>
        public Vector2? GridPos { get; set; }

        /// <summary>
        /// Entity ID of the essence monster linked to this monolith: the trapped one before release,
        /// the freed one after, followed by ID wherever it goes. They are not always the same entity -
        /// breaking the monolith can replace the trapped entity with a new one - so the freed one is
        /// then recognised by the trapped one's identity (<see cref="MonsterEssenceMods"/> etc.).
        /// </summary>
        public uint? MonsterId { get; set; }

        /// <summary>Linked monster entity while loaded, null while unloaded</summary>
        public Entity? Monster { get; set; }

        /// <summary>Metadata path of the trapped monster (kept after unlinking, to recognise the freed one)</summary>
        public string? MonsterPath { get; set; }

        /// <summary>Name of the trapped monster - a rare's random name (kept after unlinking)</summary>
        public string? MonsterRenderName { get; set; }

        /// <summary>
        /// The trapped monster's essence mod IDs, sorted and comma-joined: its fingerprint, as the mods
        /// follow the essences it holds. "" when it has none, null when never read.
        /// </summary>
        public string? MonsterEssenceMods { get; set; }

        /// <summary>Tracker clock (ms) at which the trapped monster's identity is read again (its mods change on corruption)</summary>
        public long NextIdentityReadMs { get; set; }

        /// <summary>Rarity rank of the linked monster when linked (White 0 .. Unique 3, -1 unknown), to compare candidates</summary>
        public int MonsterRarityRank { get; set; } = -1;

        /// <summary>Grid distance between the linked monster and the monolith when linked, to compare candidates</summary>
        public float MonsterLinkDistance { get; set; }

        /// <summary>True once the trapped monster's identity is known, so the freed monster can be recognised</summary>
        public bool HasMonsterIdentity => MonsterPath != null;

        /// <summary>
        /// True once the linked monster has been seen alive and no longer imprisoned after release.
        /// Only then can its death or disappearance count as the kill: the trapped entity going away
        /// or reading dead as the monolith breaks is not a kill.
        /// </summary>
        public bool MonsterFreed { get; set; }

        /// <summary>Grid distance from the player when the linked monster was last seen (MaxValue until seen in this visit)</summary>
        public float MonsterLastDistance { get; set; } = float.MaxValue;

        /// <summary>Life plus energy shield fraction (0..1) when the linked monster was last seen, 1 if unreadable</summary>
        public float MonsterLastHpFraction { get; set; } = 1f;

        /// <summary>Tracker clock (ms) when the freed monster went missing from the scan, or null while present</summary>
        public long? MonsterMissingSinceMs { get; set; }

        /// <summary>Last monster snapshot written to the diagnostics log, to only log changes</summary>
        public string? LastMonsterDiagnostic { get; set; }

        /// <summary>Last tracking snapshot written to the diagnostics log, to only log changes</summary>
        public string? LastDiagnostic { get; set; }

        /// <summary>Cheap key of the inputs to <see cref="LastDiagnostic"/>, so the text is only rebuilt on change</summary>
        public int LastDiagnosticKey { get; set; }
    }
}
