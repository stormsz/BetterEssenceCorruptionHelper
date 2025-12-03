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

            VisualSettingsHeader = new EmptyNode();
            CorruptMeHeader = new EmptyNode();
            KillReadyHeader = new EmptyNode();
            DebugHeader = new EmptyNode();

            DrawBorder = new ToggleNode(true);
            DrawText = new ToggleNode(true);
            BorderThickness = new RangeNode<float>(3f, 1f, 10f);
            TextSize = new RangeNode<float>(2f, 0.5f, 5f);
            BorderMargin = new RangeNode<float>(45f, 40f, 50f);

            ShowCorruptMe = new ToggleNode(true);
            CorruptMeBorderColor = new ColorNode(Color.Red);
            CorruptMeTextColor = new ColorNode(Color.Red);

            ShowKillReady = new ToggleNode(true);
            KillReadyBorderColor = new ColorNode(Color.Green);
            KillReadyTextColor = new ColorNode(Color.Green);

            ShowDebugInfo = new ToggleNode(false);
            DebugBackgroundEnabled = new ToggleNode(true);
            DebugBackgroundColor = new ColorNode(new Color(0, 0, 0, 180));
            DebugBackgroundOpacity = new RangeNode<float>(0.7f, 0f, 1f);
            DebugBorderColor = new ColorNode(Color.Gray);
            DebugWindowWidth = new RangeNode<int>(220, 150, 300);
        }

        [Menu("Enable")]
        public ToggleNode Enable { get; set; }

        [Menu("Visual Settings", 200, CollapsedByDefault = false)]
        public EmptyNode VisualSettingsHeader { get; set; }

        [Menu("Draw Border", parentIndex = 200)]
        public ToggleNode DrawBorder { get; set; }

        [Menu("Draw Text", parentIndex = 200)]
        public ToggleNode DrawText { get; set; }

        [Menu("Border Thickness", parentIndex = 200)]
        public RangeNode<float> BorderThickness { get; set; }

        [Menu("Border Margin", parentIndex = 200)]
        public RangeNode<float> BorderMargin { get; set; }

        [Menu("Text Size", parentIndex = 200)]
        public RangeNode<float> TextSize { get; set; }

        [Menu("Corrupt-Me Display", 300, CollapsedByDefault = false)]
        public EmptyNode CorruptMeHeader { get; set; }

        [Menu("Show Corrupt-Me", parentIndex = 300)]
        public ToggleNode ShowCorruptMe { get; set; }

        [Menu("Border Color", parentIndex = 300)]
        public ColorNode CorruptMeBorderColor { get; set; }

        [Menu("Text Color", parentIndex = 300)]
        public ColorNode CorruptMeTextColor { get; set; }

        [Menu("Kill-Ready Display", 400, CollapsedByDefault = false)]
        public EmptyNode KillReadyHeader { get; set; }

        [Menu("Show Kill-Ready", parentIndex = 400)]
        public ToggleNode ShowKillReady { get; set; }

        [Menu("Border Color", parentIndex = 400)]
        public ColorNode KillReadyBorderColor { get; set; }

        [Menu("Text Color", parentIndex = 400)]
        public ColorNode KillReadyTextColor { get; set; }

        [Menu("Debug", 500, CollapsedByDefault = true)]
        public EmptyNode DebugHeader { get; set; }

        [Menu("Show Debug Info", parentIndex = 500)]
        public ToggleNode ShowDebugInfo { get; set; }

        [Menu("Debug Background", parentIndex = 500)]
        public ToggleNode DebugBackgroundEnabled { get; set; }

        [Menu("Background Color", parentIndex = 500)]
        public ColorNode DebugBackgroundColor { get; set; }

        [Menu("Background Opacity", parentIndex = 500)]
        public RangeNode<float> DebugBackgroundOpacity { get; set; }

        [Menu("Border Color", parentIndex = 500)]
        public ColorNode DebugBorderColor { get; set; }

        [Menu("Debug Window Width", parentIndex = 500)]
        public RangeNode<int> DebugWindowWidth { get; set; }
    }
}