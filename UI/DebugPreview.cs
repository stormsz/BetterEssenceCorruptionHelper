using BetterEssenceCorruptionHelper.Models;
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

        public void Draw()
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("LIVE DEBUG OVERLAY PREVIEW");
            ImGui.SameLine(0, 16);
            ImGui.SetNextItemWidth(140);
            var scenarioNames = new[] { "Corrupt Target", "Kill Ready" };
            ImGui.Combo("##debugPreviewScenario", ref _scenario, scenarioNames, scenarioNames.Length);

            if (!_settings.Debug.ShowDebugInfo.Value)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "(Show Debug Info is disabled - hidden in game)");
            }

            ImGui.Spacing();

            // Preview canvas child window
            const float canvasHeight = 220f;
            if (!ImGui.BeginChild("##debugPreviewCanvas", new Vector2(0, canvasHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.NoScrollbar))
                return;

            var drawList = ImGui.GetWindowDrawList();
            var canvasMin = ImGui.GetWindowPos();
            var canvasSize = ImGui.GetWindowSize();
            var canvasMax = canvasMin + canvasSize;

            // Draw dark background + floor grid
            drawList.AddRectFilled(canvasMin, canvasMax, ImGui.GetColorU32(new Vector4(0.06f, 0.07f, 0.08f, 1.0f)));
            DrawFloorGrid(drawList, canvasMin, canvasMax);

            var availWidth = canvasSize.X;
            var isCorrupt = _scenario == 0;

            // Draw ground label (280px wide to fit long names like 'Deafening Essence of Misery')
            const float labelWidth = 280f;
            const float labelHeight = 88f;
            var labelMin = new Vector2(canvasMin.X + 16f, canvasMin.Y + 16f);
            var labelMax = new Vector2(labelMin.X + labelWidth, labelMin.Y + labelHeight);

            DrawGroundLabel(drawList, labelMin, labelMax, isCorrupt);

            // Compute dynamic debug window width based on content so text NEVER clips
            var lines = isCorrupt ? GetCorruptDebugLines() : GetKillDebugLines();
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

            ImGui.EndChild();
        }

        private void DrawSimulatedDebugWindow(Vector2 pos, float width, List<List<(string text, Color color)>> lines)
        {
            var bgColor = _settings.Debug.DebugBackgroundColor.Value;
            bgColor.A = (byte)(_settings.Debug.DebugBackgroundOpacity.Value * 255);

            var bgVec4 = ToImguiVec4(bgColor);
            var borderVec4 = ToImguiVec4(_settings.Debug.DebugBorderColor.Value);

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
                        ImGui.TextColored(ToImguiVec4(color), text);
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

        private void DrawGroundLabel(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool isCorrupt)
        {
            // PoE stone background
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.11f, 0.95f)), 2.0f);
            drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.38f, 0.32f, 0.22f, 0.85f)), 2.0f, ImDrawFlags.None, 1.5f);

            var titleColor = new Vector4(0.85f, 0.78f, 0.60f, 0.90f);
            drawList.AddText(new Vector2(min.X + 8, min.Y + 5), ImGui.GetColorU32(titleColor), "Imprisoned Monster");
            drawList.AddLine(new Vector2(min.X + 6, min.Y + 22), new Vector2(max.X - 6, min.Y + 22), ImGui.GetColorU32(new Vector4(0.38f, 0.32f, 0.22f, 0.40f)));

            var entries = isCorrupt
                ? new (string Type, string Tier, Vector4 Color)[]
                {
                    ("Misery", "Deafening Essence of Misery", new Vector4(0.80f, 0.52f, 1.00f, 1f)),
                    ("Scorn", "Shrieking Essence of Scorn", new Vector4(0.80f, 0.52f, 1.00f, 1f)),
                    ("Zeal", "Wailing Essence of Zeal", new Vector4(0.96f, 0.74f, 0.30f, 1f)),
                }
                : new (string Type, string Tier, Vector4 Color)[]
                {
                    ("Greed", "Deafening Essence of Greed", new Vector4(0.50f, 0.62f, 1.00f, 1f)),
                    ("Anger", "Screaming Essence of Anger", new Vector4(1.00f, 0.45f, 0.42f, 1f)),
                    ("Torment", "Wailing Essence of Torment", new Vector4(0.38f, 0.86f, 0.55f, 1f)),
                };

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var rowY = min.Y + 26 + i * 20;

                if (_icons.For(entry.Type) is { } icon)
                    drawList.AddImage(icon, new Vector2(min.X + 8, rowY), new Vector2(min.X + 24, rowY + 16));
                else
                    drawList.AddRectFilled(new Vector2(min.X + 11, rowY + 3), new Vector2(min.X + 21, rowY + 13), ImGui.GetColorU32(entry.Color));

                drawList.AddText(new Vector2(min.X + 28, rowY + 1), ImGui.GetColorU32(entry.Color), entry.Tier);
            }
        }

        private static void DrawFloorGrid(ImDrawListPtr drawList, Vector2 min, Vector2 max)
        {
            var gridColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.025f));
            for (var x = min.X + 20; x < max.X; x += 25)
                drawList.AddLine(new Vector2(x, min.Y), new Vector2(x, max.Y), gridColor);
            for (var y = min.Y + 20; y < max.Y; y += 25)
                drawList.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), gridColor);
        }

        private static Vector4 ToImguiVec4(Color color) =>
            new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }
}
