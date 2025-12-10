using BetterEssenceCorruptionHelper.Models;
using ExileCore;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using System.Collections;
using System.Collections.Concurrent;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// ExileCore plugin that analyzes essence monoliths and provides visual indicators
    /// to help players decide whether to corrupt or kill essences based on their value.
    /// 
    /// Performance optimizations:
    /// - Uses separated concerns for better maintainability
    /// - Entity tracking and rendering in separate classes
    /// - Parallel coroutines for background processing
    /// </summary>
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        #region Constants

        /// <summary>Update interval for statistics cache (milliseconds)</summary>
        private const int STATS_UPDATE_MS = 1000;

        #endregion

        #region Fields

        private readonly MapStatistics _mapStats = new();
        private EssenceEntityTracker? _entityTracker;
        private EssenceRenderer? _renderer;

        // Coroutine wait conditions
        private readonly WaitTime _statsUpdateWait = new(STATS_UPDATE_MS);

        // Coroutine instances
        private Coroutine? _statsUpdateCoroutine;
        private Coroutine? _entityProcessingCoroutine;
        private Coroutine? _uiUpdateCoroutine;

        #endregion

        #region Initialization

        /// <summary>
        /// ExileCore initialization hook - called when plugin is enabled.
        /// </summary>
        /// <returns>True if initialization succeeded</returns>
        public override bool Initialise()
        {
            Name = "Better Essence Corruption Helper";

            // Initialize components
            _entityTracker = new EssenceEntityTracker(GameController, Settings, _mapStats);
            _renderer = new EssenceRenderer(GameController, Settings, _mapStats, _entityTracker);

            InitializeCoroutines();

            DebugWindow.LogMsg($"[{Name}] initialized", 2, SharpDX.Color.Green);
            return base.Initialise();
        }

        /// <summary>
        /// Configures and starts coroutines for background processing.
        /// Respects the user's CoroutineMultiThreading setting from ExileCore.
        /// </summary>
        private void InitializeCoroutines()
        {
            // Create coroutine instances
            _statsUpdateCoroutine = new Coroutine(StatsUpdateRoutine(), this, "BetterEssenceStatsUpdate");
            _entityProcessingCoroutine = new Coroutine(_entityTracker!.GetEntityProcessingCoroutine(), this, "BetterEssenceEntityProcessing");
            _uiUpdateCoroutine = new Coroutine(_renderer!.GetUIUpdateCoroutine(), this, "BetterEssenceUIUpdate");

            // Set execution priorities
            _entityProcessingCoroutine!.Priority = CoroutinePriority.Critical;
            _uiUpdateCoroutine!.Priority = CoroutinePriority.High;
            _statsUpdateCoroutine!.Priority = CoroutinePriority.Normal;

            // Get runner references from ExileCore
            var parallelRunner = Core.ParallelRunner;
            var mainRunner = Core.MainRunner;

            // Check the user's CoroutineMultiThreading setting
            bool useParallelMode = GameController.Settings.CoreSettings.PerformanceSettings.CoroutineMultiThreading.Value;

            if (useParallelMode && parallelRunner != null)
            {
                // User enabled multi-threading AND parallel runner is available
                _entityProcessingCoroutine.SyncModWork = false;
                _statsUpdateCoroutine.SyncModWork = false;

                parallelRunner.Run(_entityProcessingCoroutine);
                parallelRunner.Run(_statsUpdateCoroutine);

                DebugWindow.LogMsg($"[{Name}]: Parallel mode initialized");
            }
            else
            {
                // User disabled multi-threading OR parallel runner unavailable
                _entityProcessingCoroutine.SyncModWork = true;
                _statsUpdateCoroutine.SyncModWork = true;

                mainRunner?.Run(_entityProcessingCoroutine);
                mainRunner?.Run(_statsUpdateCoroutine);

                DebugWindow.LogMsg($"[{Name}]: Single-threaded mode initialized");
            }

            // UI updates MUST run on main thread
            _uiUpdateCoroutine.SyncModWork = true;
            mainRunner?.Run(_uiUpdateCoroutine);
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// ExileCore lifecycle hook - called when player enters a new area.
        /// </summary>
        /// <param name="area">The new area instance object</param>
        public override void AreaChange(AreaInstance area)
        {
            ResetState();
        }

        /// <summary>
        /// Clears all tracking data when entering a new area.
        /// </summary>
        private void ResetState()
        {
            _entityTracker?.ResetState();
            _mapStats.Reset();
            _renderer?.UpdateSessionStatsCache();
        }

        /// <summary>
        /// ExileCore tick hook - called every frame.
        /// </summary>
        /// <returns>null - we don't queue any jobs</returns>
        public override Job? Tick()
        {
            return null;
        }

        /// <summary>
        /// ExileCore render hook - called every frame for drawing.
        /// </summary>
        public override void Render()
        {
            _renderer?.Render();
        }

        #endregion

        #region Coroutines

        /// <summary>
        /// Coroutine that periodically updates the cached statistics text.
        /// </summary>
        private IEnumerator StatsUpdateRoutine()
        {
            while (true)
            {
                yield return _statsUpdateWait;

                if (Settings.Enable.Value && GameController.InGame)
                    _renderer?.UpdateSessionStatsCache();
            }
        }

        #endregion
    }
}