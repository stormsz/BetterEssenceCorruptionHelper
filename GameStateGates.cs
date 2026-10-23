using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Shared "should we be doing anything right now?" checks, evaluated once per frame by the
    /// plugin so the tracker and the renderer can't drift apart.
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
        private static bool IsAnyBlockingUiVisible(GameController gameController)
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
        /// Both gates, reading the blocking panels a single time. Track: work that only makes sense in
        /// a live, non-town area (entity scanning, label resolution). Render: drawing, deliberately
        /// without the town check - the map-stats window has its own "show in town/hideout" setting.
        /// </summary>
        public static (bool Track, bool Render) Evaluate(GameController gameController, Settings settings)
        {
            if (!settings.Enable.Value || !gameController.InGame)
                return (false, false);

            var render = !IsAnyBlockingUiVisible(gameController);
            return (render && !gameController.Area.CurrentArea.IsPeaceful, render);
        }
    }
}
