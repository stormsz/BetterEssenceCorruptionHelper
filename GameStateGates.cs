using ExileCore;

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
        /// <summary>
        /// True when a full-screen game panel is covering the play area, so overlays would either
        /// be hidden behind it or drawn on top of it.
        /// </summary>
        public static bool IsAnyBlockingUiVisible(GameController gameController)
        {
            var ui = gameController.IngameState.IngameUi;
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
