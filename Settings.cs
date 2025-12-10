using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;
using System.Collections.Generic;

namespace BetterEssenceCorruptionHelper
{
    public class Settings : ISettings
    {
        public Settings()
        {
            Enable = new ToggleNode(true);
            Indicators = new IndicatorSettings();
            MapStats = new MapStatsSettings();
            Debug = new DebugSettings();
        }

        [Menu("Enable", "Turns the entire plugin on/off")]
        public ToggleNode Enable { get; set; }

        [Menu("Essence Indicators", 50, CollapsedByDefault = false)]
        public IndicatorSettings Indicators { get; set; }

        [Menu("Essence Map Stats", 100, CollapsedByDefault = false)]
        public MapStatsSettings MapStats { get; set; }

        [Menu("Debug Settings", 200, CollapsedByDefault = true)]
        public DebugSettings Debug { get; set; }
    }

    [Submenu(CollapsedByDefault = false)]
    public class IndicatorSettings
    {
        [Menu("Enable All Indicators", "Master toggle for all essence indicators")]
        public ToggleNode EnableAllIndicators { get; set; } = new ToggleNode(true);

        [Menu("Corrupt-Me Indicator", 100)]
        public CorruptMeSettings CorruptMe { get; set; } = new CorruptMeSettings();

        [Menu("Kill-Ready Indicator", 200)]
        public KillReadySettings KillReady { get; set; } = new KillReadySettings();
    }

    [Submenu]
    public class CorruptMeSettings
    {
        [Menu("Enable Corrupt-Me Indicator", "Display indicator for essences that should be corrupted")]
        public ToggleNode ShowCorruptMe { get; set; } = new ToggleNode(true);

        [Menu("Draw Border", "Draw border around corrupt-me essences")]
        public ToggleNode DrawBorder { get; set; } = new ToggleNode(true);

        [Menu("Draw Text", "Show 'CORRUPT' text above corrupt-me essences")]
        public ToggleNode DrawText { get; set; } = new ToggleNode(true);

        [Menu("Background Fill", "Fill the status box with background color")]
        public ToggleNode BackgroundFill { get; set; } = new ToggleNode(false);

        [Menu("Background Opacity", "Opacity of the background fill")]
        public RangeNode<float> BackgroundOpacity { get; set; } = new RangeNode<float>(0.3f, 0f, 1f);

        [Menu("Border Color", "Border color for corrupt-me indicator")]
        public ColorNode BorderColor { get; set; } = new ColorNode(Color.Red);

        [Menu("Text Color", "Text color for corrupt-me indicator")]
        public ColorNode TextColor { get; set; } = new ColorNode(Color.Red);
    }

    [Submenu]
    public class KillReadySettings
    {
        [Menu("Enable Kill-Ready Indicator", "Display indicator for essences ready to kill")]
        public ToggleNode ShowKillReady { get; set; } = new ToggleNode(true);

        [Menu("Draw Border", "Draw border around kill-ready essences")]
        public ToggleNode DrawBorder { get; set; } = new ToggleNode(true);

        [Menu("Draw Text", "Show 'KILL' text above kill-ready essences")]
        public ToggleNode DrawText { get; set; } = new ToggleNode(true);

        [Menu("Background Fill", "Fill the status box with background color")]
        public ToggleNode BackgroundFill { get; set; } = new ToggleNode(false);

        [Menu("Background Opacity", "Opacity of the background fill")]
        public RangeNode<float> BackgroundOpacity { get; set; } = new RangeNode<float>(0.3f, 0f, 1f);

        [Menu("Border Color", "Border color for kill-ready indicator")]
        public ColorNode BorderColor { get; set; } = new ColorNode(Color.Green);

        [Menu("Text Color", "Text color for kill-ready indicator")]
        public ColorNode TextColor { get; set; } = new ColorNode(Color.Green);
    }

    [Submenu(CollapsedByDefault = false)]
    public class MapStatsSettings
    {
        [Menu("Show Map Stats Window", "Display essence map statistics window")]
        public ToggleNode ShowMapStats { get; set; } = new ToggleNode(true);

        [Menu("Show in Town/Hideout", "Display stats window even in town or hideout")]
        public ToggleNode ShowInTownHideout { get; set; } = new ToggleNode(false);

        [Menu("Title Background", "Background color for window title")]
        public ColorNode TitleBackground { get; set; } = new ColorNode(new Color(0, 157, 255, 200));

        [Menu("Content Background", "Background color for window content")]
        public ColorNode ContentBackground { get; set; } = new ColorNode(new Color(0, 0, 0, 150));

        [Menu("Title Color", "Text color for window title")]
        public ColorNode TitleColor { get; set; } = new ColorNode(Color.White);

        [Menu("Text Color", "Text color for window content")]
        public ColorNode TextColor { get; set; } = new ColorNode(Color.White);

        [Menu("Border Color", "Border color for window")]
        public ColorNode BorderColor { get; set; } = new ColorNode(new Color(100, 100, 100, 200));
    }

    [Submenu(CollapsedByDefault = true)]
    public class DebugSettings
    {
        [Menu("Show Debug Info", "Display debug information overlay for each essence")]
        public ToggleNode ShowDebugInfo { get; set; } = new ToggleNode(false);

        [Menu("Background Color", "Background color for debug window")]
        public ColorNode DebugBackgroundColor { get; set; } = new ColorNode(new Color(0, 0, 0, 180));

        [Menu("Background Opacity", "Opacity of debug window background")]
        public RangeNode<float> DebugBackgroundOpacity { get; set; } = new RangeNode<float>(0.7f, 0f, 1f);

        [Menu("Border Color", "Border color for debug window")]
        public ColorNode DebugBorderColor { get; set; } = new ColorNode(Color.Gray);
    }
}