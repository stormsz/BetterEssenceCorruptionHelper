using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Models;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using SharpDX;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Vector3 = SharpDX.Vector3;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Handles entity tracking and processing for essence monoliths.
    /// Manages the lifecycle of essence entities from detection to completion.
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

        /// <summary>Distance threshold for determining if an essence has unloaded vs been killed (units)</summary>
        private const int ESSENCE_UNLOAD_DISTANCE = 120;

        /// <summary>Position tolerance for matching reloaded essences to their previous instances (units)</summary>
        private const int POSITION_TOLERANCE = 2;

        /// <summary>Processing interval for entity scanning and state updates (milliseconds)</summary>
        private const int ENTITY_PROCESS_MS = 50;

        #endregion

        #region Fields

        private readonly GameController _gameController = gameController;
        private readonly Settings _settings = settings;
        private readonly MapStatistics _mapStats = mapStats;

        /// <summary>
        /// Thread-safe dictionary tracking all currently visible essence monoliths.
        /// Key: Entity memory address, Value: Tracking data for that essence.
        /// Uses ConcurrentDictionary because entity processing runs on parallel thread.
        /// </summary>
        private readonly ConcurrentDictionary<long, EssenceEntityData> _trackedEntities = new();

        /// <summary>
        /// Thread-safe counter for assigning unique IDs to essences.
        /// Incremented via Interlocked.Increment to avoid race conditions.
        /// </summary>
        private int _entityIdCounter;

        // Coroutine wait condition
        private readonly WaitTime _entityProcessWait = new(ENTITY_PROCESS_MS);

        #endregion

        #region Properties

        /// <summary>Gets a snapshot of currently tracked entities</summary>
        public IEnumerable<EssenceEntityData> TrackedEntities => _trackedEntities.Values;

        #endregion
        #region Initialization

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
            foreach (var kvp in _trackedEntities)
            {
                var data = kvp.Value;
                data.Label ??= FindLabelForEntity(data.Entity!);
            }
        }

        /// <summary>
        /// Clears all tracking data when entering a new area.
        /// Essential because essence addresses/entities are area-specific.
        /// </summary>
        public void ResetState()
        {
            _trackedEntities.Clear();
            _entityIdCounter = 0;
        }

        #endregion

        #region Entity Processing

        /// <summary>
        /// Main entity processing loop - scans all game entities and tracks essence monoliths.
        /// </summary>
        private void ProcessEssences()
        {
            var currentMonoliths = new HashSet<long>();

            try
            {
                // Scan all entities in game world
                foreach (var entity in _gameController.Entities)
                {
                    if (!IsValidMonolith(entity))
                        continue;

                    // Get or create tracking data for this essence
                    var data = GetOrCreateEntityData(entity, currentMonoliths);
                    currentMonoliths.Add(entity.Address);

                    // Update state if label is available
                    if (data.Label != null)
                        UpdateEntityData(entity, data);

                    // Always cache position for relinking after unload cycles
                    data.LastKnownPosition = ToSharpDXVector3(entity.PosNum);
                }

                // Remove essences that were killed or unloaded
                CleanupOldEssences(currentMonoliths);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"ProcessEssences failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets existing tracking data for an entity or creates new data.
        /// </summary>
        private EssenceEntityData GetOrCreateEntityData(Entity entity, HashSet<long> currentMonoliths)
        {
            // Check if we're already tracking this address
            if (_trackedEntities.TryGetValue(entity.Address, out var data))
            {
                data.Entity = entity; // Update entity reference
                return data;
            }

            var entityPos = ToSharpDXVector3(entity.PosNum);

            // Try to find an unloaded essence at this position
            var existingData = FindUnloadedEssenceAtPosition(entityPos, currentMonoliths);

            if (existingData != null)
                return RelinkEssence(existingData, entity);

            // Genuinely new essence - create fresh tracking data
            return CreateNewEssenceData(entity);
        }

        /// <summary>
        /// Searches for an unloaded essence at a specific position.
        /// </summary>
        private EssenceEntityData? FindUnloadedEssenceAtPosition(Vector3 position, HashSet<long> currentMonoliths)
        {
            foreach (var kvp in _trackedEntities)
            {
                var data = kvp.Value;

                // Skip if already processed this frame
                if (currentMonoliths.Contains(data.Address))
                    continue;

                // Check position match (within tolerance)
                if (data.LastKnownPosition.HasValue &&
                    Vector3.Distance(data.LastKnownPosition.Value, position) < POSITION_TOLERANCE)
                {
                    return data;
                }
            }
            return null;
        }

        /// <summary>
        /// Relinks essence tracking data to a new entity instance.
        /// </summary>
        private EssenceEntityData RelinkEssence(EssenceEntityData existingData, Entity entity)
        {
            // Remove old address mapping
            _trackedEntities.TryRemove(existingData.Address, out _);

            // Update to new address and entity reference
            existingData.Address = entity.Address;
            existingData.Entity = entity;

            // Add new address mapping
            _trackedEntities.TryAdd(entity.Address, existingData);

            if (_settings.Debug.ShowDebugInfo.Value)
            {
                DebugWindow.LogMsg($"Essence Relinked: {existingData.EntityId} (Addr changed)", 3, SharpDX.Color.Cyan);
            }

            return existingData;
        }

        /// <summary>
        /// Creates new tracking data for a freshly discovered essence.
        /// </summary>
        private EssenceEntityData CreateNewEssenceData(Entity entity)
        {
            // Thread-safe ID increment
            var newId = Interlocked.Increment(ref _entityIdCounter);

            var newData = new EssenceEntityData
            {
                EntityId = newId,
                Address = entity.Address,
                FirstSeenAddress = entity.Address,
                Entity = entity,
                Label = FindLabelForEntity(entity)
            };

            _trackedEntities.TryAdd(entity.Address, newData);
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
            {
                data.PreviousState = oldState;
                DebugWindow.LogMsg($"Essence {data.EntityId}: {oldState} -> {newState}");
            }

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
            data.PreviousState = oldState;

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
                data.MistakenCorruption = true;
                _mapStats.IncrementMistakes();

                if (_settings.Debug.ShowDebugInfo.Value)
                {
                    DebugWindow.LogMsg($"MISTAKEN CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X}) should have been killed!", 3, SharpDX.Color.OrangeRed);
                }
            }
        }

        /// <summary>
        /// Removes essences that are no longer visible from tracking.
        /// </summary>
        private void CleanupOldEssences(HashSet<long> currentMonoliths)
        {
            var player = _gameController.Player;
            if (player == null)
                return;

            var playerPos = ToSharpDXVector3(player.PosNum);
            var removedEntities = new List<long>();

            // Create snapshot to avoid collection modification during enumeration
            var snapshot = _trackedEntities.ToArray();

            foreach (var kvp in snapshot)
            {
                var address = kvp.Key;
                var data = kvp.Value;

                // Still visible - skip
                if (currentMonoliths.Contains(address))
                    continue;

                // Check if unloaded (player far away) vs killed (player nearby)
                if (IsEssenceUnloaded(data, playerPos))
                {
                    // Player moved away - essence unloaded but might reload later
                    // Clear label so it gets refreshed when player returns
                    data.Label = null;
                    continue;
                }

                // Entity disappeared while player was nearby - must have been killed/opened
                HandleEssenceKilled(data, address);
                removedEntities.Add(address);
            }

            // Remove killed essences from tracking dictionary
            foreach (var address in removedEntities)
            {
                _trackedEntities.TryRemove(address, out _);
            }
        }

        /// <summary>
        /// Determines if an essence was unloaded due to distance vs killed by player.
        /// </summary>
        private static bool IsEssenceUnloaded(EssenceEntityData data, Vector3 playerPos)
        {
            if (!data.LastKnownPosition.HasValue)
                return false;

            var distance = Vector3.Distance(playerPos, data.LastKnownPosition.Value);
            return distance > ESSENCE_UNLOAD_DISTANCE;
        }

        /// <summary>
        /// Processes essence kill/open event.
        /// </summary>
        private void HandleEssenceKilled(EssenceEntityData data, long address)
        {
            data.WasKilled = true;
            _mapStats.IncrementKilled();

            // Check if player missed a corruption opportunity
            if (IsMissedCorruption(data))
            {
                data.MissedCorruption = true;
                _mapStats.IncrementMissed();
                DebugWindow.LogMsg($"MISSED CORRUPTION: Essence {data.EntityId} (0x{address:X}) should have been corrupted but was killed", 3, SharpDX.Color.Orange);
            }
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
        /// Validates if an entity is a valid essence monolith worth tracking.
        /// </summary>
        private bool IsValidMonolith(Entity entity)
        {
            // Fast rejection checks first
            if (entity == null || entity.Address == 0 || !entity.IsValid ||
                !entity.IsTargetable || entity.Address == _gameController.Player?.Address)
                return false;

            // Component checks
            if (!entity.HasComponent<Render>() || !entity.HasComponent<Monolith>())
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
        private bool ShouldProcess() =>
            _settings.Enable.Value &&
            _gameController.InGame &&
            !_gameController.Area.CurrentArea.IsPeaceful &&
            !IsAnyGameUIVisible();

        /// <summary>
        /// Checks if any game UI panel is open.
        /// </summary>
        private bool IsAnyGameUIVisible()
        {
            var ui = _gameController.IngameState.IngameUi;
            return ui.InventoryPanel.IsVisible ||
                    ui.OpenLeftPanel.IsVisible ||
                    ui.TreePanel.IsVisible ||
                    ui.Atlas.IsVisible ||
                    ui.SyndicatePanel.IsVisible ||
                    ui.DelveWindow.IsVisible ||
                    ui.IncursionWindow.IsVisible ||
                    ui.HeistWindow.IsVisible ||
                    ui.ExpeditionWindow.IsVisible ||
                    ui.RitualWindow.IsVisible ||
                    ui.UltimatumPanel.IsVisible;
        }

        /// <summary>
        /// Converts System.Numerics Vector3 to SharpDX Vector3.
        /// </summary>
        private static Vector3 ToSharpDXVector3(System.Numerics.Vector3 vec)
        {
            return new Vector3(vec.X, vec.Y, vec.Z);
        }

        #endregion
    }
}