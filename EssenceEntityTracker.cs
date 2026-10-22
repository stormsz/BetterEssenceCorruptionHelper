using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Models;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Handles entity tracking and processing for essence monoliths.
    /// Manages the lifecycle of essence entities from detection to opening.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the EssenceEntityTracker class.
    /// </remarks>
    /// <param name="gameController">The game controller instance</param>
    /// <param name="settings">Plugin settings</param>
    /// <param name="mapStats">Statistics tracker</param>
    internal class EssenceEntityTracker(GameController gameController, Settings settings, MapStatistics mapStats)
    {
        #region Constants

        /// <summary>Processing interval for entity scanning and state updates (milliseconds)</summary>
        private const int ENTITY_PROCESS_MS = 50;

        #endregion

        #region Fields

        private readonly GameController _gameController = gameController;
        private readonly Settings _settings = settings;
        private readonly MapStatistics _mapStats = mapStats;

        /// <summary>
        /// Every essence monolith seen in the current area, keyed by game entity ID.
        ///
        /// The ID is what ExileCore itself keys entities by, and it survives an essence leaving and
        /// re-entering the loaded area, so entries are simply kept until the area changes - no
        /// address relinking or "was it killed or unloaded?" guesswork.
        ///
        /// A plain Dictionary is enough: every coroutine touching this runs on the main runner
        /// with SyncModWork, which is the same thread that calls Render().
        /// </summary>
        private readonly Dictionary<uint, EssenceEntityData> _trackedEntities = new();

        /// <summary>Display number counter. Survives hideout trips; restarts with each new map.</summary>
        private int _entityIdCounter;

        // Coroutine wait condition
        private readonly WaitTime _entityProcessWait = new(ENTITY_PROCESS_MS);

        #endregion

        #region Properties

        /// <summary>Enumerates currently tracked entities. Opened ones stay listed with State None.</summary>
        public IEnumerable<EssenceEntityData> TrackedEntities => _trackedEntities.Values;

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets a coroutine for entity processing.
        /// </summary>
        /// <returns>Coroutine that processes entities</returns>
        public IEnumerator GetEntityProcessingCoroutine()
        {
            while (true)
            {
                yield return _entityProcessWait;

                if (ShouldProcess())
                    ProcessEssences();
            }
        }

        /// <summary>
        /// Updates entity label references.
        /// Labels can appear/disappear as they enter/leave screen, so we continuously refresh.
        /// </summary>
        public void UpdateEntityLabels()
        {
            if (_trackedEntities.Count == 0)
                return;

            // Read the visible-label list once for the whole pass. Each access re-reads game
            // memory, so resolving it per tracked essence multiplied that cost by the number of
            // essences on screen.
            var labels = _gameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible;
            if (labels == null)
                return;

            foreach (var data in _trackedEntities.Values)
            {
                var entity = data.Entity;
                if (entity == null)
                    continue;

                // An opened monolith has nothing left to mark.
                if (data.IsOpened)
                {
                    data.Label = null;
                    continue;
                }

                // Re-resolve rather than `??=`. A LabelOnGround that has dropped out of
                // ItemsOnGroundLabelsVisible keeps serving its last known rect, so caching the
                // first one we ever saw leaves stale boxes drifting on screen.
                data.Label = FindLabelIn(labels, entity.Address);
            }
        }

        /// <summary>
        /// Matches a ground label to an entity address within an already-materialised label list.
        /// Hand-rolled loop rather than LINQ to avoid a closure allocation per essence per pass.
        /// </summary>
        private static LabelOnGround? FindLabelIn(IList<LabelOnGround> labels, long address)
        {
            for (var i = 0; i < labels.Count; i++)
            {
                var label = labels[i];
                if (label?.ItemOnGround?.Address == address)
                    return label;
            }

            return null;
        }

        /// <summary>
        /// Clears tracking data when entering a new area. Entity and label references belong to
        /// the area being left, so they are always dropped; the display numbering only restarts on
        /// a new map, so a hideout trip doesn't renumber the essences you come back to.
        /// </summary>
        /// <param name="newMap">True when entering a different map instance than the last one</param>
        public void ResetState(bool newMap)
        {
            _trackedEntities.Clear();

            if (newMap)
                _entityIdCounter = 0;
        }

        #endregion

        #region Entity Processing

        /// <summary>
        /// Main entity processing loop - scans all game entities and tracks essence monoliths.
        /// </summary>
        private void ProcessEssences()
        {
            try
            {
                foreach (var entity in _gameController.Entities)
                {
                    if (!IsValidMonolith(entity, out var monolith))
                        continue;

                    var isOpenedNow = monolith.IsOpened;

                    if (!_trackedEntities.TryGetValue(entity.Id, out var data))
                    {
                        // Only start tracking monoliths that can still be interacted with. One that
                        // is already untargetable was opened before we got here (e.g. coming back
                        // from the hideout) and has nothing to offer.
                        if (!entity.IsTargetable)
                            continue;

                        data = CreateNewEssenceData(entity);
                    }

                    data.Entity = entity;
                    data.Address = entity.Address;
                    data.OpenStage = monolith.OpenStage;

                    if (data.IsOpened)
                        continue;

                    if (!isOpenedNow)
                    {
                        data.WasSeenClosed = true;
                    }
                    else if (data.WasSeenClosed)
                    {
                        HandleEssenceOpened(data);
                        continue;
                    }

                    // Update state if label is available
                    if (data.Label != null)
                        UpdateEntityData(entity, data);
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"ProcessEssences failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates new tracking data for a freshly discovered essence.
        /// </summary>
        private EssenceEntityData CreateNewEssenceData(Entity entity)
        {
            var newData = new EssenceEntityData
            {
                Id = entity.Id,
                EntityId = ++_entityIdCounter,
                Address = entity.Address,
                Entity = entity,
                Label = FindLabelForEntity(entity)
            };

            _trackedEntities[entity.Id] = newData;
            return newData;
        }

        /// <summary>
        /// Updates essence state based on label analysis.
        /// </summary>
        private void UpdateEntityData(Entity entity, EssenceEntityData data)
        {
            if (data.Label == null)
                return;

            // Analyze label to determine essence value
            var newAnalysis = EssenceLabelAnalyzer.Analyze(data.Label);
            var oldState = data.State;
            var wasCorruptedBefore = data.Analysis.IsCorrupted;

            data.Analysis = newAnalysis;
            var newState = DetermineEssenceState(newAnalysis);

            // Detect corruption event (state changed from uncorrupted to corrupted)
            if (!wasCorruptedBefore && newAnalysis.IsCorrupted)
            {
                HandleEssenceCorruption(entity, data, oldState, newState);
            }

            // Track state transitions for debugging
            if (HasStateChanged(wasCorruptedBefore, newAnalysis.IsCorrupted, oldState, newState))
                DebugWindow.LogMsg($"Essence {data.EntityId}: {oldState} -> {newState}");

            data.State = newState;
        }

        /// <summary>
        /// Determines if essence state actually changed.
        /// </summary>
        private static bool HasStateChanged(bool wasCorrupted, bool isCorrupted, EssenceState oldState, EssenceState newState) =>
            (wasCorrupted || !isCorrupted) && newState != oldState;

        /// <summary>
        /// Handles essence corruption event and tracks statistics.
        /// </summary>
        private void HandleEssenceCorruption(Entity entity, EssenceEntityData data, EssenceState oldState, EssenceState newState)
        {
            data.WasCorruptedByPlayer = true;
            data.PreviousAnalysis = data.Analysis;  // Save pre-corruption state for comparison

            if (_settings.Debug.ShowDebugInfo.Value)
            {
                DebugWindow.LogMsg(
                    $"Essence {data.EntityId} (0x{entity.Address:X}) corrupted. Old state: {oldState}, New state: {newState}", 3, SharpDX.Color.Yellow);
            }

            // Track outcome based on what previous state was
            if (oldState == EssenceState.ShouldCorrupt)
            {
                // Good corruption - essence was valuable
                _mapStats.IncrementCorrupted();

                if (_settings.Debug.ShowDebugInfo.Value)
                {
                    DebugWindow.LogMsg($"SUCCESSFUL CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X})", 3, SharpDX.Color.LightGreen);
                }
            }
            else if (oldState == EssenceState.ShouldKill)
            {
                // Bad corruption - essence wasn't valuable enough to corrupt
                _mapStats.IncrementMistakes();

                if (_settings.Debug.ShowDebugInfo.Value)
                {
                    DebugWindow.LogMsg($"MISTAKEN CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X}) should have been killed!", 3, SharpDX.Color.OrangeRed);
                }
            }
        }

        /// <summary>
        /// Processes the monolith being opened (monster released) - the "killed" event. Driven by
        /// the Monolith component's IsOpened flag, so it fires exactly once and doesn't depend on
        /// where the player is standing.
        /// </summary>
        private void HandleEssenceOpened(EssenceEntityData data)
        {
            data.IsOpened = true;
            _mapStats.IncrementKilled();

            // Check if player missed a corruption opportunity (must run before State is cleared)
            if (IsMissedCorruption(data))
            {
                _mapStats.IncrementMissed();
                DebugWindow.LogMsg($"MISSED CORRUPTION: Essence {data.EntityId} (0x{data.Address:X}) should have been corrupted but was killed", 3, SharpDX.Color.Orange);
            }
            else if (_settings.Debug.ShowDebugInfo.Value)
            {
                DebugWindow.LogMsg($"Essence {data.EntityId} opened (stage {data.OpenStage})", 3, SharpDX.Color.Cyan);
            }

            // Nothing left to draw for an opened monolith.
            data.State = EssenceState.None;
            data.Label = null;
        }

        /// <summary>
        /// Checks if player missed a corruption opportunity.
        /// </summary>
        private static bool IsMissedCorruption(EssenceEntityData data) =>
            data.State == EssenceState.ShouldCorrupt &&
            !data.Analysis.IsCorrupted &&
            !data.WasCorruptedByPlayer;

        #endregion

        #region Entity Validation & Helpers

        /// <summary>
        /// Validates if an entity is a loaded essence monolith, and returns its Monolith component.
        /// IsTargetable is deliberately not required here: a monolith stops being targetable once
        /// opened, and a tracked one has to stay visible to us for the IsOpened flip to be seen.
        /// </summary>
        private bool IsValidMonolith(Entity entity, [NotNullWhen(true)] out Monolith? monolith)
        {
            monolith = null;

            // Fast rejection checks first
            if (entity == null || entity.Address == 0 || !entity.IsValid ||
                entity.Address == _gameController.Player?.Address)
                return false;

            // Component checks
            if (!entity.HasComponent<Render>() || !entity.TryGetComponent<Monolith>(out monolith) || monolith == null)
                return false;

            // Position validation (try-catch for safety against memory read errors)
            try
            {
                var pos = entity.PosNum;
                return pos.X != 0 || pos.Y != 0 || pos.Z != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Determines recommended action for an essence based on analysis.
        /// </summary>
        private static EssenceState DetermineEssenceState(EssenceAnalysis analysis) =>
            analysis.IsCorrupted ? EssenceState.ShouldKill :
            analysis.HasValuablePattern ? EssenceState.ShouldCorrupt :
            EssenceState.ShouldKill;

        /// <summary>
        /// Finds the UI label element associated with an essence entity.
        /// </summary>
        private LabelOnGround? FindLabelForEntity(Entity entity) =>
            _gameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                .FirstOrDefault(x => x.ItemOnGround?.Address == entity.Address);

        /// <summary>
        /// Determines if entity processing should run this frame.
        /// </summary>
        private bool ShouldProcess() => GameStateGates.ShouldTrackEssences(_gameController, _settings);

        #endregion
    }
}
