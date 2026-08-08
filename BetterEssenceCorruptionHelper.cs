using BetterEssenceCorruptionHelper.Models;
using ExileCore;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using System.Collections;

namespace BetterEssenceCorruptionHelper
{
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        #region Constants

        /// Update interval for statistics cache (milliseconds)
        private const int STATS_UPDATE_MS = 1000;

        #endregion

        #region Fields

        private readonly MapStatistics _mapStats = new();
        private EssenceEntityTracker? _entityTracker;
        private EssenceRenderer? _renderer;

        // Coroutine wait condition
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

            var mainRunner = Core.MainRunner;

            // Everything runs on the main runner, deliberately.
            //
            // Entity processing reads ExileCore memory objects (GameController.Entities, and the
            // ground-label Element tree via EssenceLabelAnalyzer). Those are backed by per-frame
            // caches that are not thread-safe, and Render() reads the very same Elements. Running
            // the scan on Core.ParallelRunner raced the render thread over that shared cache state
            // for no real benefit - the whole pass is a handful of entities at 20Hz, which is far
            // too little work to be worth a thread.
            //
            // Stats only touch Interlocked counters, but there is nothing to gain by moving them
            // off the main runner either.
            _entityProcessingCoroutine.SyncModWork = true;
            _statsUpdateCoroutine.SyncModWork = true;
            _uiUpdateCoroutine.SyncModWork = true;

            mainRunner?.Run(_entityProcessingCoroutine);
            mainRunner?.Run(_statsUpdateCoroutine);
            mainRunner?.Run(_uiUpdateCoroutine);

            DebugWindow.LogMsg($"[{Name}]: initialized on main runner");
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// ExileCore lifecycle hook - called when player enters a new area.
        /// </summary>
        /// <param name="area">The new area instance object</param>
        public override void AreaChange(AreaInstance area)
        {
            _entityTracker?.ResetState();
            _mapStats.Reset();
            _renderer?.UpdateSessionStatsCache();
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
