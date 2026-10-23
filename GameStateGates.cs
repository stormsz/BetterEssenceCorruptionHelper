using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Shared "should we be doing anything right now?" checks.
    ///
    /// These were previously duplicated verbatim in both EssenceEntityTracker and
    /// EssenceRenderer, which meant the two could silently drift apart - the tracker could stop
    /// updating essences while the renderer kept drawing the stale results, or vice versa.
    /// </summary>
    internal static class GameStateGates
    {
        /// <summary>Full-screen game panels that cover the play area, by name (for diagnostics).</summary>
        private static readonly (string Name, Func<IngameUIElements, Element> Get)[] BlockingPanels =
        [
            ("InventoryPanel", ui => ui.InventoryPanel),
            ("OpenLeftPanel", ui => ui.OpenLeftPanel),
            ("TreePanel", ui => ui.TreePanel),
            ("Atlas", ui => ui.Atlas),
            ("SyndicatePanel", ui => ui.SyndicatePanel),
            ("DelveWindow", ui => ui.DelveWindow),
            ("IncursionWindow", ui => ui.IncursionWindow),
            ("HeistWindow", ui => ui.HeistWindow),
            ("ExpeditionWindow", ui => ui.ExpeditionWindow),
            ("RitualWindow", ui => ui.RitualWindow),
            ("UltimatumPanel", ui => ui.UltimatumPanel),
        ];

        /// <summary>
        /// True when a full-screen game panel is covering the play area, so overlays would either
        /// be hidden behind it or drawn on top of it.
        /// </summary>
        public static bool IsAnyBlockingUiVisible(GameController gameController)
        {
            var ui = gameController.IngameState.IngameUi;
            foreach (var (_, get) in BlockingPanels)
            {
                if (get(ui).IsVisible)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Names whatever is currently stopping essence tracking, for the diagnostics log.
        /// </summary>
        public static string DescribeTrackingBlockers(GameController gameController, Settings settings)
        {
            if (!settings.Enable.Value)
                return "plugin disabled";
            if (!gameController.InGame)
                return "not in game";

            var blockers = new List<string>();
            if (gameController.Area.CurrentArea.IsPeaceful)
                blockers.Add("town/hideout");

            var ui = gameController.IngameState.IngameUi;
            foreach (var (name, get) in BlockingPanels)
            {
                if (get(ui).IsVisible)
                    blockers.Add(name);
            }

            return blockers.Count > 0 ? string.Join(", ", blockers) : "nothing";
        }

        /// <summary>
        /// Both gates at once, reading the blocking panels a single time. The plugin evaluates this
        /// once per frame in Tick and Render reuses the result, instead of each caller re-reading
        /// eleven UI elements.
        /// </summary>
        public static (bool Track, bool Render) Evaluate(GameController gameController, Settings settings)
        {
            if (!settings.Enable.Value || !gameController.InGame)
                return (false, false);

            var render = !IsAnyBlockingUiVisible(gameController);
            return (render && !gameController.Area.CurrentArea.IsPeaceful, render);
        }

        /// <summary>
        /// Gate for work that only makes sense in a live, non-town area - entity scanning and
        /// label resolution.
        /// </summary>
        public static bool ShouldTrackEssences(GameController gameController, Settings settings) =>
            settings.Enable.Value &&
            gameController.InGame &&
            !gameController.Area.CurrentArea.IsPeaceful &&
            !IsAnyBlockingUiVisible(gameController);

        /// <summary>
        /// Gate for drawing. Deliberately does not check IsPeaceful: the map-stats window has its
        /// own "show in town/hideout" setting and is allowed to render there.
        /// </summary>
        public static bool ShouldRender(GameController gameController, Settings settings) =>
            settings.Enable.Value &&
            gameController.InGame &&
            !IsAnyBlockingUiVisible(gameController);
    }
}
