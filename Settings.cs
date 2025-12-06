using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace BetterEssenceCorruptionHelper
{
    public class Settings : ISettings
    {
        public Settings()
        {
            Enable = new ToggleNode(true);
            Visual = new VisualSettings();
            CorruptMe = new CorruptMeSettings();
            KillReady = new KillReadySettings();
            SessionStats = new SessionStatsSettings();
            Debug = new DebugSettings();
        }

        [Menu("Enable", "Turns the entire plugin on/off")]
        public ToggleNode Enable { get; set; }

        [Menu("Visual Settings", 100, CollapsedByDefault = false)]
        public VisualSettings Visual { get; set; }

        [Menu("Corrupt-Me Settings", 200, CollapsedByDefault = false)]
        public CorruptMeSettings CorruptMe { get; set; }

        [Menu("Kill-Ready Settings", 300, CollapsedByDefault = false)]
        public KillReadySettings KillReady { get; set; }

        [Menu("Session Stats Settings", 350, CollapsedByDefault = false)]
        public SessionStatsSettings SessionStats { get; set; }

        [Menu("Debug Settings", 400, CollapsedByDefault = true)]
        public DebugSettings Debug { get; set; }
    }

    [Submenu]
    public class VisualSettings
    {
        [Menu("Border Thickness", "How thick the colored border should be (1-10)")]
        public RangeNode<float> BorderThickness { get; set; } = new RangeNode<float>(3f, 1f, 10f);

        [Menu("Border Margin", "Extra padding around border")]
        public RangeNode<float> BorderMargin { get; set; } = new RangeNode<float>(45f, 40f, 50f);

        [Menu("Text Size", "Scale of the 'CORRUPT/KILL' text")]
        public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(2f, 0.5f, 5f);
    }

    [Submenu]
    public class CorruptMeSettings
    {
        [Menu("Show Corrupt-Me", "Display indicator for essences that should be corrupted")]
        public ToggleNode ShowCorruptMe { get; set; } = new ToggleNode(true);

        [Menu("Draw Border", "Draw border around corrupt-me essences")]
        public ToggleNode DrawBorder { get; set; } = new ToggleNode(true);

        [Menu("Draw Text", "Show 'CORRUPT' text above corrupt-me essences")]
        public ToggleNode DrawText { get; set; } = new ToggleNode(true);

        [Menu("Border Color", "Border color for corrupt-me indicator")]
        public ColorNode BorderColor { get; set; } = new ColorNode(Color.Red);

        [Menu("Text Color", "Text color for corrupt-me indicator")]
        public ColorNode TextColor { get; set; } = new ColorNode(Color.Red);
    }

    [Submenu]
    public class KillReadySettings
    {
        [Menu("Show Kill-Ready", "Display indicator for essences ready to kill")]
        public ToggleNode ShowKillReady { get; set; } = new ToggleNode(true);

        [Menu("Draw Border", "Draw border around kill-ready essences")]
        public ToggleNode DrawBorder { get; set; } = new ToggleNode(true);

        [Menu("Draw Text", "Show 'KILL' text above kill-ready essences")]
        public ToggleNode DrawText { get; set; } = new ToggleNode(true);

        [Menu("Border Color", "Border color for kill-ready indicator")]
        public ColorNode BorderColor { get; set; } = new ColorNode(Color.Green);

        [Menu("Text Color", "Text color for kill-ready indicator")]
        public ColorNode TextColor { get; set; } = new ColorNode(Color.Green);
    }

    [Submenu]
    public class SessionStatsSettings
    {
        [Menu("Show Session Stats", "Display session stats window")]
        public ToggleNode ShowSessionStats { get; set; } = new ToggleNode(true);

        [Menu("Session Window X", "Horizontal position of session stats window")]
        public RangeNode<int> SessionWindowX { get; set; } = new RangeNode<int>(10, 0, 5000);

        [Menu("Session Window Y", "Vertical position of session stats window")]
        public RangeNode<int> SessionWindowY { get; set; } = new RangeNode<int>(100, 0, 5000);

        [Menu("Title Background", "Background color for session stats title")]
        public ColorNode TitleBackground { get; set; } = new ColorNode(new Color(0, 157, 255, 200));

        [Menu("Content Background", "Background color for session stats content")]
        public ColorNode ContentBackground { get; set; } = new ColorNode(new Color(0, 0, 0, 150));

        [Menu("Title Color", "Text color for session stats title")]
        public ColorNode TitleColor { get; set; } = new ColorNode(Color.White);

        [Menu("Text Color", "Text color for session stats content")]
        public ColorNode TextColor { get; set; } = new ColorNode(Color.White);

        [Menu("Border Color", "Border color for session stats window")]
        public ColorNode BorderColor { get; set; } = new ColorNode(new Color(100, 100, 100, 200));
    }

    [Submenu]
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