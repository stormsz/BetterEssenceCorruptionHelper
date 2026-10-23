using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Models;
using BetterEssenceCorruptionHelper.Pricing;
using BetterEssenceCorruptionHelper.Strategy;
using BetterEssenceCorruptionHelper.UI;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using System.Diagnostics;
using System.IO;

namespace BetterEssenceCorruptionHelper
{
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        #region Fields

        private readonly MapStatistics _mapStats = new();
        private readonly CorruptionLog _corruptionLog = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private EssenceEntityTracker? _entityTracker;
        private EssenceRenderer? _renderer;
        private NinjaPriceBridge? _priceBridge;
        private PriceBook? _prices;
        private EssenceValues? _values;
        private SetupDetector? _setup;
        private Action? _catalogueDraw;
        private Action? _diagnosticsDraw;
        private Action? _indicatorPreviewDraw;
        private Action? _debugPreviewDraw;

        /// <summary>Hash of the map instance the current stats belong to (0 = none yet)</summary>
        private uint _statsMapHash;

        /// <summary>This frame's gates, evaluated once in Tick and reused by Render.</summary>
        private (bool Track, bool Render) _gates;
        private long _gatesAtMs = long.MinValue;

        #endregion

        #region Initialization

        /// <summary>
        /// ExileCore initialization hook - called when plugin is enabled.
        /// </summary>
        /// <returns>True if initialization succeeded</returns>
        public override bool Initialise()
        {
            Name = "Better Essence Corruption Helper";

            if (Settings.Tracking.Normalize() is { } note)
                DebugWindow.LogMsg($"[{Name}] settings repaired: {note}", 5, SharpDX.Color.Yellow);

            _priceBridge = new NinjaPriceBridge(GameController, () => PluginManager);
            _prices = new PriceBook(EssenceCatalogue.MarketItems, _priceBridge.Resolve, () => _clock.ElapsedMilliseconds, _priceBridge.GetStatus);
            _values = new EssenceValues(_prices);
            _setup = new SetupDetector(GameController);

            var icons = new EssenceIcons(Graphics, Path.Join(ConfigDirectory, "icons"));
            _entityTracker = new EssenceEntityTracker(GameController, Settings, _mapStats, _corruptionLog, _prices, _values, _setup, OutcomeCsvPath);
            _renderer = new EssenceRenderer(GameController, Settings, _mapStats, _entityTracker, _prices, _values, _setup, _corruptionLog, icons);

            var catalogueView = new CatalogueView(Settings, _prices, _values, _setup, _priceBridge, _entityTracker, _corruptionLog, icons,
                () => _clock.ElapsedMilliseconds);
            var indicatorPreview = new IndicatorPreview(Settings, icons);
            var debugPreview = new DebugPreview(Settings, icons);
            _catalogueDraw = catalogueView.Draw;
            _diagnosticsDraw = catalogueView.DrawDiagnostics;
            _indicatorPreviewDraw = indicatorPreview.Draw;
            _debugPreviewDraw = debugPreview.Draw;
            Settings.CatalogueView.DrawDelegate = _catalogueDraw;
            Settings.Debug.Diagnostics.DrawDelegate = _diagnosticsDraw;
            Settings.Indicators.Preview.DrawDelegate = _indicatorPreviewDraw;
            Settings.Debug.Preview.DrawDelegate = _debugPreviewDraw;

            // No coroutines: all work runs in Tick on the main thread (see EssenceEntityTracker for the
            // update path). ExileCore memory objects and ground-label Elements are backed by per-frame
            // caches that are not thread-safe, and Render reads the very same Elements.
            DebugWindow.LogMsg($"[{Name}] initialized", 2, SharpDX.Color.Green);
            return base.Initialise();
        }

        /// <summary>
        /// Hot reload (a new DLL dropped into Plugins\Compiled while the HUD runs) can leave this old
        /// instance's settings page registered next to the new one's, showing a second strategy
        /// selector. Blank our pages - but only if they are still ours, so a newer instance sharing the
        /// same settings object keeps its own.
        /// </summary>
        public override void OnPluginDestroyForHotReload()
        {
            static void Blank(ExileCore.Shared.Nodes.CustomNode node, Action? ours)
            {
                if (ours != null && node.DrawDelegate == ours)
                    node.DrawDelegate = () => { };
            }

            Blank(Settings.CatalogueView, _catalogueDraw);
            Blank(Settings.Debug.Diagnostics, _diagnosticsDraw);
            Blank(Settings.Indicators.Preview, _indicatorPreviewDraw);
            Blank(Settings.Debug.Preview, _debugPreviewDraw);
            base.OnPluginDestroyForHotReload();
        }

        /// <summary>Where observed corruptions are appended, or null while recording is off.</summary>
        private string? OutcomeCsvPath() =>
            Settings.MapStats.RecordOutcomesCsv.Value ? Path.Join(ConfigDirectory, "corruption-outcomes.csv") : null;

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
            _renderer?.Reset();

            // Ninja Price re-syncs its league on area change; sample again and re-resolve its bridge
            // method in case it was reloaded.
            _prices?.Invalidate(reResolveProvider: true);
            _setup?.Invalidate();
        }

        /// <summary>ExileCore entity events drive monolith discovery (see EssenceEntityTracker).</summary>
        public override void EntityAdded(Entity entity) => _entityTracker?.OnEntityAdded(entity);

        public override void EntityRemoved(Entity entity) => _entityTracker?.OnEntityRemoved(entity);

        /// <summary>
        /// ExileCore per-frame update, main thread: gates, price sampling (when due) and tracking.
        /// Returns no Job - nothing here may leave the main thread.
        /// </summary>
        public override Job Tick()
        {
            _gates = GameStateGates.Evaluate(GameController, Settings);
            _gatesAtMs = _clock.ElapsedMilliseconds;

            if (GameController.InGame && !GameController.IsLoading)
            {
                _prices?.Update();
                _setup?.Update(_gatesAtMs);
            }

            _entityTracker?.Tick(_gates.Track);
            return null!;
        }

        /// <summary>
        /// ExileCore render hook - called every frame for drawing.
        /// </summary>
        public override void Render()
        {
            // Reuse this frame's gates; fall back to evaluating them if Tick didn't run recently.
            if (_clock.ElapsedMilliseconds - _gatesAtMs > 100)
                _gates = GameStateGates.Evaluate(GameController, Settings);

            _renderer?.Render(_gates.Render);
        }

        #endregion
    }
}
