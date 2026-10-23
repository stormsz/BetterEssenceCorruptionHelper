using ImGuiNET;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Numerics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace BetterEssenceCorruptionHelper.UI
{
    /// <summary>
    /// Live simulation preview rendered inside the Debug & Diagnostics settings submenu.
    /// Draws a simulated essence monolith with the exact live debug window overlay applied
    /// from current settings (Background Color, Background Opacity, Border Color), updating
    /// in real time as the user adjusts controls.
    /// </summary>
    internal sealed class DebugPreview(Settings settings, EssenceIcons icons)
    {
        private readonly Settings _settings = settings;
        private readonly EssenceIcons _icons = icons;
        private int _scenario; // 0 = Corrupt Target, 1 = Kill Ready

        private static readonly List<List<(string text, Color color)>> CorruptDebugLines = GetCorruptDebugLines();
        private static readonly List<List<(string text, Color color)>> KillDebugLines = GetKillDebugLines();

        public void Draw()
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("LIVE DEBUG OVERLAY PREVIEW");
            ImGui.SameLine(0, 16);
            ImGui.RadioButton("Corrupt Target##debugPreview", ref _scenario, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Kill Ready##debugPreview", ref _scenario, 1);

            if (!_settings.Debug.ShowDebugInfo.Value)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "(Show Debug Info is disabled - hidden in game)");
            }

            ImGui.Spacing();

            if (PreviewCanvas.Begin("##debugPreviewCanvas", 220f, out var drawList, out var canvasMin, out _))
                DrawScenario(drawList, canvasMin);
            PreviewCanvas.End();
        }

        private void DrawScenario(ImDrawListPtr drawList, Vector2 canvasMin)
        {
            var isCorrupt = _scenario == 0;
            var labelMin = new Vector2(canvasMin.X + 16f, canvasMin.Y + 16f);
            var labelMax = new Vector2(labelMin.X + PreviewCanvas.LabelWidth, labelMin.Y + PreviewCanvas.LabelHeight);

            PreviewCanvas.DrawGroundLabel(drawList, _icons, labelMin, labelMax, isCorrupt ? PreviewCanvas.CorruptSample : PreviewCanvas.KillSample);

            // Compute dynamic debug window width based on content so text NEVER clips
            var lines = isCorrupt ? CorruptDebugLines : KillDebugLines;
            float maxLineWidth = 0f;
            foreach (var line in lines)
            {
                float lw = 0f;
                foreach (var (text, _) in line)
                    lw += ImGui.CalcTextSize(text).X;
                if (lw > maxLineWidth)
                    maxLineWidth = lw;
            }

            var debugWidth = maxLineWidth + 24f;
            var debugPos = new Vector2(labelMax.X + 18f, canvasMin.Y + 12f);

            DrawSimulatedDebugWindow(debugPos, debugWidth, lines);
        }

        private void DrawSimulatedDebugWindow(Vector2 pos, float width, List<List<(string text, Color color)>> lines)
        {
            var bgColor = _settings.Debug.DebugBackgroundColor.Value;
            bgColor.A = (byte)(_settings.Debug.DebugBackgroundOpacity.Value * 255);

            var bgVec4 = EssenceRenderer.ToImguiVec4(bgColor);
            var borderVec4 = EssenceRenderer.ToImguiVec4(_settings.Debug.DebugBorderColor.Value);

            ImGui.SetCursorScreenPos(pos);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, bgVec4);
            ImGui.PushStyleColor(ImGuiCol.Border, borderVec4);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));

            if (ImGui.BeginChild("##simulatedDebugBox", new Vector2(width, 196f), ImGuiChildFlags.Border))
            {
                foreach (var line in lines)
                {
                    for (var i = 0; i < line.Count; i++)
                    {
                        var (text, color) = line[i];
                        if (i > 0)
                            ImGui.SameLine(0, 0);
                        ImGui.TextColored(EssenceRenderer.ToImguiVec4(color), text);
                    }
                    if (line.Count == 0)
                        ImGui.Spacing();
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        private static List<List<(string text, Color color)>> GetCorruptDebugLines() =>
        [
            [("Essence #1482", Color.White)],
            [("Status: ", Color.White), ("CORRUPT-ME", Color.Red), (" (Worth it (+4.5c EV))", Color.Gray)],
            [("Monster: ", Color.Gray), ("Misery Behemoth", Color.White), (" (ID: 9840)", Color.Gray)],
            [("Trapped at: ", Color.Gray), ("(145, 92) · 28m away", Color.White)],
            [("Setup: ", Color.Gray), ("Standard (No scarabs)", Color.White)],
            [("Expected Net Gain: ", Color.White), ("+4.5c", Color.LimeGreen), (" (Worst: -0.6c, Best: +23.4c)", Color.Gray)],
            [("Items: ", Color.Gray), ("Deafening Misery, Shrieking Scorn, Wailing Zeal", Color.White)],
        ];

        private static List<List<(string text, Color color)>> GetKillDebugLines() =>
        [
            [("Essence #1483", Color.White)],
            [("Status: ", Color.White), ("KILL-ME", Color.Green), (" (Expected profit -0.4c)", Color.Gray)],
            [("Monster: ", Color.Gray), ("Greed Goliath", Color.White), (" (ID: 9841)", Color.Gray)],
            [("Trapped at: ", Color.Gray), ("(160, 105) · 42m away", Color.White)],
            [("Setup: ", Color.Gray), ("Standard (No scarabs)", Color.White)],
            [("Expected Net Gain: ", Color.White), ("-0.4c", Color.OrangeRed), (" (Worst: -0.6c, Best: +1.2c)", Color.Gray)],
            [("Items: ", Color.Gray), ("Deafening Greed, Screaming Anger, Wailing Torment", Color.White)],
        ];

    }
}
