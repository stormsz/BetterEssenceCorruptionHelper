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

        [Menu("Debug Settings", 400, CollapsedByDefault = true)]
        public DebugSettings Debug { get; set; }
    }

    [Submenu]
    public class VisualSettings
    {
        [Menu("Draw Border", "Draw colored border around essence labels")]
        public ToggleNode DrawBorder { get; set; } = new ToggleNode(true);

        [Menu("Draw Text", "Show 'CORRUPT' or 'KILL' text above essences")]
        public ToggleNode DrawText { get; set; } = new ToggleNode(true);

        [Menu("Border Thickness", "How thick the colored border should be (1-10)")]
        public RangeNode<float> BorderThickness { get; set; } = new RangeNode<float>(3f, 1f, 10f);

        [Menu("Text Size", "Scale of the 'CORRUPT/KILL' text")]
        public RangeNode<float> TextSize { get; set; } = new RangeNode<float>(2f, 0.5f, 5f);

        [Menu("Border Margin", "Extra padding around border")]
        public RangeNode<float> BorderMargin { get; set; } = new RangeNode<float>(45f, 40f, 50f);
    }

    [Submenu]
    public class CorruptMeSettings
    {
        [Menu("Show Corrupt-Me", "Display indicator for essences that should be corrupted")]
        public ToggleNode ShowCorruptMe { get; set; } = new ToggleNode(true);

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

        [Menu("Border Color", "Border color for kill-ready indicator")]
        public ColorNode BorderColor { get; set; } = new ColorNode(Color.Green);

        [Menu("Text Color", "Text color for kill-ready indicator")]
        public ColorNode TextColor { get; set; } = new ColorNode(Color.Green);
    }

    [Submenu]
    public class DebugSettings
    {
        [Menu("Show Debug Info", "Display debug information overlay")]
        public ToggleNode ShowDebugInfo { get; set; } = new ToggleNode(false);

        [Menu("Debug Background", "Enable background for debug window")]
        public ToggleNode DebugBackgroundEnabled { get; set; } = new ToggleNode(true);

        [Menu("Background Color", "Background color for debug window")]
        public ColorNode DebugBackgroundColor { get; set; } = new ColorNode(new Color(0, 0, 0, 180));

        [Menu("Background Opacity", "Opacity of debug window background")]
        public RangeNode<float> DebugBackgroundOpacity { get; set; } = new RangeNode<float>(0.7f, 0f, 1f);

        [Menu("Border Color", "Border color for debug window")]
        public ColorNode DebugBorderColor { get; set; } = new ColorNode(Color.Gray);

        [Menu("Debug Window Width", "Width of debug window in pixels")]
        public RangeNode<int> DebugWindowWidth { get; set; } = new RangeNode<int>(220, 150, 300);
    }
}