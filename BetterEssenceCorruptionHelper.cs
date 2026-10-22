using BetterEssenceCorruptionHelper.Models;
using ExileCore;
using ExileCore.Shared;
using ExileCore.Shared.Enums;

namespace BetterEssenceCorruptionHelper
{
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        #region Fields

        private readonly MapStatistics _mapStats = new();
        private EssenceEntityTracker? _entityTracker;
        private EssenceRenderer? _renderer;

        /// <summary>Hash of the map instance the current stats belong to (0 = none yet)</summary>
        private uint _statsMapHash;

        // Coroutine instances
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
        /// </summary>
        private void InitializeCoroutines()
        {
            // Create coroutine instances. Passing `this` as owner lets ExileCore pause them when
            // the plugin is disabled and finish them on hot reload.
            _entityProcessingCoroutine = new Coroutine(_entityTracker!.GetEntityProcessingCoroutine(), this, "BetterEssenceEntityProcessing");
            _uiUpdateCoroutine = new Coroutine(_renderer!.GetUIUpdateCoroutine(), this, "BetterEssenceUIUpdate");

            // Set execution priorities
            _entityProcessingCoroutine.Priority = CoroutinePriority.Critical;
            _uiUpdateCoroutine.Priority = CoroutinePriority.High;

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
            // SyncModWork keeps them on this thread even when ExileCore's CoroutineMultiThreading
            // option is on, which is also why the tracker can use plain collections.
            _entityProcessingCoroutine.SyncModWork = true;
            _uiUpdateCoroutine.SyncModWork = true;

            mainRunner?.Run(_entityProcessingCoroutine);
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
            // Map stats belong to one map instance. Town/hideout trips keep them (so they can still
            // be shown there), and coming back through a portal lands in the same instance hash.
            // Only entering a different non-peaceful instance starts a fresh tally.
            var newMap = !area.IsPeaceful && area.Hash != _statsMapHash;
            if (newMap)
            {
                _statsMapHash = area.Hash;
                _mapStats.Reset();
            }

            _entityTracker?.ResetState(newMap);
        }

        /// <summary>
        /// ExileCore render hook - called every frame for drawing.
        /// </summary>
        public override void Render()
        {
            _renderer?.Render();
        }

        #endregion
    }
}
