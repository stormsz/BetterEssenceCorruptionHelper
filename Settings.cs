using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;
using BetterEssenceCorruptionHelper.Strategy;
using Newtonsoft.Json;

namespace BetterEssenceCorruptionHelper
{
    public class Settings : ISettings
    {
        public Settings()
        {
            Enable = new ToggleNode(true);
            CatalogueView = new CustomNode();
            Indicators = new IndicatorSettings();
            VaalOrbAlert = new VaalOrbAlertSettings();
            MapStats = new MapStatsSettings();
            Debug = new DebugSettings();
            Tracking = new TrackingPreferences();
        }

        [Menu("Enable", "Turns the entire plugin on/off")]
        public ToggleNode Enable { get; set; }

        [Menu(null, 25)]
        [JsonIgnore]
        public CustomNode CatalogueView { get; set; }

        [Menu("Essence Indicators", 50, CollapsedByDefault = true)]
        public IndicatorSettings Indicators { get; set; }

        [Menu("Vaal Orb Alert", 75, CollapsedByDefault = true)]
        public VaalOrbAlertSettings VaalOrbAlert { get; set; }

        [Menu("Essence Map Stats", 100, CollapsedByDefault = true)]
        public MapStatsSettings MapStats { get; set; }

        /// <summary>
        /// Strategy and per-essence corruption choices. Plain data, kept out of the generated menu: it is
        /// edited through the catalogue view and persisted with the rest of the settings.
        /// </summary>
        [IgnoreMenu]
        public TrackingPreferences Tracking { get; set; }

        [Menu("Debug & Diagnostics", 200, CollapsedByDefault = true)]
        public DebugSettings Debug { get; set; }
    }

    [Submenu(CollapsedByDefault = true)]
    public class IndicatorSettings
    {
        [Menu("Enable All Indicators", "Master toggle for all essence indicators")]
        public ToggleNode EnableAllIndicators { get; set; } = new ToggleNode(true);

        [Menu("Box Inset X", "Pixels to inset the indicator box horizontally from each side of the ground label. 0 = match the label exactly.")]
        public RangeNode<int> BoxInsetX { get; set; } = new RangeNode<int>(0, -60, 60);

        [Menu("Box Inset Y", "Pixels to inset the indicator box vertically from the top and bottom of the ground label. 0 = match the label exactly.")]
        public RangeNode<int> BoxInsetY { get; set; } = new RangeNode<int>(0, -60, 60);

        [Menu("Corrupt-Me Indicator", 100)]
        public CorruptMeSettings CorruptMe { get; set; } = new CorruptMeSettings();

        [Menu("Kill-Ready Indicator", 200)]
        public KillReadySettings KillReady { get; set; } = new KillReadySettings();

        [Menu("Show Expected Profit", "Above CORRUPT, show how much a Vaal Orb is expected to add, after its own cost, at current prices", 250)]
        public ToggleNode ShowValueRange { get; set; } = new ToggleNode(true);

        /// <summary>Live simulation window showing the essence ground label with current overlay settings.</summary>
        [Menu(null, 300)]
        [JsonIgnore]
        public CustomNode Preview { get; set; } = new CustomNode();
    }

    /// <summary>
    /// Common shape of the two indicator setting blocks, so the renderer can read them without
    /// `dynamic` dispatch on a per-essence, per-frame hot path.
    /// Implemented explicitly so ExileCore's [Menu] reflection/serialization is unaffected.
    /// </summary>
    public interface IIndicatorSettings
    {
        ToggleNode ShowIndicator { get; }
        ToggleNode DrawBorder { get; }
        ToggleNode DrawText { get; }
        ToggleNode BackgroundFill { get; }
        RangeNode<float> BackgroundOpacity { get; }
        ColorNode BorderColor { get; }
        ColorNode TextColor { get; }
    }

    [Submenu]
    public class CorruptMeSettings : IIndicatorSettings
    {
        ToggleNode IIndicatorSettings.ShowIndicator => ShowCorruptMe;
        ToggleNode IIndicatorSettings.DrawBorder => DrawBorder;
        ToggleNode IIndicatorSettings.DrawText => DrawText;
        ToggleNode IIndicatorSettings.BackgroundFill => BackgroundFill;
        RangeNode<float> IIndicatorSettings.BackgroundOpacity => BackgroundOpacity;
        ColorNode IIndicatorSettings.BorderColor => BorderColor;
        ColorNode IIndicatorSettings.TextColor => TextColor;

        [Menu("Enable Corrupt-Me Indicator", "Display indicator for essences that should be corrupted")]
        public ToggleNode ShowCorruptMe { get; set; } = new ToggleNode(true);

        [Menu("Highlight Card Border", "Draw border around corrupt-me monolith card")]
        public ToggleNode DrawBorder { get; set; } = new ToggleNode(true);

        [Menu("Highlight Vaal Button", "Highlight the corrupt interaction button at the bottom center of the monolith label")]
        public ToggleNode HighlightVaalButton { get; set; } = new ToggleNode(true);

        [Menu("Show Status Indicator", "Show indicator above corrupt-me essences")]
        public ToggleNode DrawText { get; set; } = new ToggleNode(true);

        [Menu("Use Vaal Icon", "Show Vaal Orb icon instead of 'CORRUPT' text above the label")]
        public ToggleNode ShowVaalIcon { get; set; } = new ToggleNode(false);

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
    public class KillReadySettings : IIndicatorSettings
    {
        ToggleNode IIndicatorSettings.ShowIndicator => ShowKillReady;
        ToggleNode IIndicatorSettings.DrawBorder => DrawBorder;
        ToggleNode IIndicatorSettings.DrawText => DrawText;
        ToggleNode IIndicatorSettings.BackgroundFill => BackgroundFill;
        RangeNode<float> IIndicatorSettings.BackgroundOpacity => BackgroundOpacity;
        ColorNode IIndicatorSettings.BorderColor => BorderColor;
        ColorNode IIndicatorSettings.TextColor => TextColor;

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

    [Submenu(CollapsedByDefault = true)]
    public class VaalOrbAlertSettings
    {
        [Menu("Enable Vaal Orb Alert", "Scans player inventory for Vaal Orbs and alerts when below threshold")]
        public ToggleNode EnableAlert { get; set; } = new ToggleNode(true);

        [Menu("Alert Threshold (Min Vaal Orbs)", "Alert when inventory has fewer than this amount of Vaal Orbs")]
        public RangeNode<int> MinVaalOrbs { get; set; } = new RangeNode<int>(5, 1, 50);

        [Menu("Show HUD Warning Banner", "Display an on-screen warning banner at the top of the screen in maps when below threshold")]
        public ToggleNode ShowWarningBanner { get; set; } = new ToggleNode(true);

        [Menu("Warn on Essence Label", "Show 'NO VAAL ORBS' indicator on corrupt-me monoliths when inventory is empty")]
        public ToggleNode WarnOnLabel { get; set; } = new ToggleNode(true);
    }


    [Submenu(CollapsedByDefault = true)]
    public class MapStatsSettings
    {
        [Menu("Show Map Stats Window", "Display essence map statistics window")]
        public ToggleNode ShowMapStats { get; set; } = new ToggleNode(true);

        [Menu("Show in Town/Hideout", "Display stats window even in town or hideout")]
        public ToggleNode ShowInTownHideout { get; set; } = new ToggleNode(false);

        [Menu("Record Corruption Outcomes (CSV)", "Append every observed corruption (before/after essences, outcome, prices) to corruption-outcomes.csv in this plugin's config folder")]
        public ToggleNode RecordOutcomesCsv { get; set; } = new ToggleNode(false);

        [Menu("Title Background", "Background color for window title")]
        public ColorNode TitleBackground { get; set; } = new ColorNode(new Color(24, 26, 33, 240));

        [Menu("Content Background", "Background color for window content")]
        public ColorNode ContentBackground { get; set; } = new ColorNode(new Color(14, 16, 20, 230));

        [Menu("Title Color", "Text color for window title")]
        public ColorNode TitleColor { get; set; } = new ColorNode(new Color(230, 215, 175));

        [Menu("Text Color", "Text color for window content")]
        public ColorNode TextColor { get; set; } = new ColorNode(new Color(225, 228, 235));

        [Menu("Border Color", "Border color for window")]
        public ColorNode BorderColor { get; set; } = new ColorNode(new Color(60, 64, 75, 200));
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

        [JsonIgnore]
        public CustomNode Preview { get; set; } = new CustomNode();

        /// <summary>Copy buttons, the item mapping table and the game-files check, drawn by CatalogueView.</summary>
        [JsonIgnore]
        public CustomNode Diagnostics { get; set; } = new CustomNode();
    }
}
