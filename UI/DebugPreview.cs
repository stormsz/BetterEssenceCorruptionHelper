using BetterEssenceCorruptionHelper.Catalogue;
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
            ImGui.RadioButton("Corrupt Target##debugPreview", ref _scenario, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Kill Ready##debugPreview", ref _scenario, 1);

            if (!_settings.Debug.ShowDebugInfo.Value)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "(Show Debug Info is disabled - hidden in game)");
            }

            ImGui.Spacing();

            var isCorrupt = _scenario == 0;
            var lines = isCorrupt ? GetCorruptDebugLines() : GetKillDebugLines();

            var iconSize = ImGui.GetTextLineHeight();
            if (iconSize <= 0) iconSize = 14f;

            float maxLineWidth = 0f;
            foreach (var line in lines)
            {
                float lw = 0f;
                if (line.Prefix is { } p)
                    lw += ImGui.CalcTextSize(p.Text).X;
                if (line.Icon != null)
                    lw += iconSize + 4f;
                foreach (var (text, _) in line.Segments)
                    lw += ImGui.CalcTextSize(text).X;
                if (lw > maxLineWidth)
                    maxLineWidth = lw;
            }

            var debugWidth = Math.Max(280f, maxLineWidth + 24f);
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();
            if (lineHeight <= 0) lineHeight = 16f;
            var debugHeight = 16f + lines.Count * lineHeight;

            var availWidth = ImGui.GetContentRegionAvail().X;
            var isStacked = availWidth < (PreviewCanvas.LabelWidth + debugWidth + 50f);
            var canvasHeight = isStacked
                ? (72f + PreviewCanvas.LabelHeight + (isCorrupt ? 38f : 14f) + debugHeight + 20f)
                : Math.Max(270f, 72f + debugHeight + 16f);

            if (PreviewCanvas.Begin("##debugPreviewCanvas", canvasHeight, out var drawList, out var canvasMin, out _))
                DrawScenario(drawList, canvasMin, isStacked, isCorrupt, lines, debugWidth, debugHeight);
            PreviewCanvas.End();
        }

        private void DrawScenario(
            ImDrawListPtr drawList,
            Vector2 canvasMin,
            bool isStacked,
            bool isCorrupt,
            List<EssenceRenderer.DebugLine> lines,
            float debugWidth,
            float debugHeight)
        {
            var labelMin = new Vector2(canvasMin.X + 16f, canvasMin.Y + 70f);
            var labelMax = new Vector2(labelMin.X + PreviewCanvas.LabelWidth, labelMin.Y + PreviewCanvas.LabelHeight);

            // 1. Draw simulated in-game ground label
            PreviewCanvas.DrawGroundLabel(
                drawList,
                _icons,
                labelMin,
                labelMax,
                isCorrupt ? PreviewCanvas.CorruptSample : PreviewCanvas.KillSample,
                drawVaalButton: isCorrupt);

            // 2. Draw live overlay indicators on the monolith label matching game behavior
            if (isCorrupt)
            {
                // Value badge above
                DrawCenteredBadge(drawList, new Vector2((labelMin.X + labelMax.X) / 2f, labelMin.Y - 50f), "+15.2c expected", Color.LimeGreen);
                // Status badge above
                DrawCenteredBadge(drawList, new Vector2((labelMin.X + labelMax.X) / 2f, labelMin.Y - 26f), "CORRUPT", Color.Red);

                // Card border
                drawList.AddRect(labelMin, labelMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0f, 0f, 1f)), 0f, ImDrawFlags.None, 2f);

                // Vaal button border
                var btnRect = PreviewCanvas.GetSimulatedVaalButtonRect(labelMin, labelMax);
                drawList.AddRect(new Vector2(btnRect.Left, btnRect.Top), new Vector2(btnRect.Right, btnRect.Bottom), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0f, 0f, 1f)), 4f, ImDrawFlags.None, 2.5f);
            }
            else
            {
                // Kill badge above
                DrawCenteredBadge(drawList, new Vector2((labelMin.X + labelMax.X) / 2f, labelMin.Y - 26f), "KILL", Color.LimeGreen);

                // Card border
                drawList.AddRect(labelMin, labelMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.85f, 0.3f, 1f)), 0f, ImDrawFlags.None, 2f);
            }

            // 3. Position and draw the simulated debug window
            var debugPos = isStacked
                ? new Vector2(canvasMin.X + 16f, labelMax.Y + (isCorrupt ? 38f : 14f))
                : new Vector2(labelMax.X + 16f, labelMin.Y);

            DrawSimulatedDebugWindow(debugPos, debugWidth, debugHeight, lines);
        }

        private static void DrawCenteredBadge(ImDrawListPtr drawList, Vector2 pos, string text, Color textColor)
        {
            var textSize = ImGui.CalcTextSize(text);
            const float padding = 5f;
            var bgMin = new Vector2(pos.X - textSize.X / 2f - padding, pos.Y - padding);
            var bgMax = new Vector2(pos.X + textSize.X / 2f + padding, pos.Y + textSize.Y + padding);
            var bgColor = new Vector4(0f, 0f, 0f, 200f / 255f);

            drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(bgColor), 0f);
            drawList.AddText(new Vector2(pos.X - textSize.X / 2f, pos.Y), ImGui.GetColorU32(EssenceRenderer.ToImguiVec4(textColor)), text);
        }

        private void DrawSimulatedDebugWindow(Vector2 pos, float width, float height, List<EssenceRenderer.DebugLine> lines)
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
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2));

            if (ImGui.BeginChild("##simulatedDebugBox", new Vector2(width, height), ImGuiChildFlags.Border))
            {
                var iconSize = ImGui.GetTextLineHeight();
                if (iconSize <= 0) iconSize = 14f;

                foreach (var line in lines)
                {
                    var hasPrefix = false;

                    if (line.Prefix is { } p)
                    {
                        ImGui.TextColored(EssenceRenderer.ToImguiVec4(p.Color), p.Text);
                        hasPrefix = true;
                    }

                    if (line.Icon is { } icon)
                    {
                        if (hasPrefix)
                            ImGui.SameLine(0, 0);

                        ImGui.Image(icon, new Vector2(iconSize, iconSize));
                        ImGui.SameLine(0, 4);
                        hasPrefix = true;
                    }

                    for (var i = 0; i < line.Segments.Count; i++)
                    {
                        var (text, color) = line.Segments[i];

                        if (i > 0 || hasPrefix)
                            ImGui.SameLine(0, 0);

                        ImGui.TextColored(EssenceRenderer.ToImguiVec4(color), text);
                    }

                    if (!hasPrefix && line.Segments.Count == 0)
                        ImGui.Text("");
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        private List<EssenceRenderer.DebugLine> GetCorruptDebugLines()
        {
            var vaalIcon = _icons?.For("Vaal");
            var misery = EssenceCatalogue.FindByName("Deafening Essence of Misery");
            var scorn = EssenceCatalogue.FindByName("Shrieking Essence of Scorn");
            var zeal = EssenceCatalogue.FindByName("Wailing Essence of Zeal");

            var miseryIcon = misery != null ? _icons?.For(misery) ?? _icons?.For(misery.TypeName) : null;
            var scornIcon = scorn != null ? _icons?.For(scorn) ?? _icons?.For(scorn.TypeName) : null;
            var zealIcon = zeal != null ? _icons?.For(zeal) ?? _icons?.For(zeal.TypeName) : null;

            return
            [
                new([("Essence #1 · ", Color.White), ("CORRUPT-ME", Color.Red), (" (MEDS Corruption: Deafening Essence of Misery)", Color.Gray)]),
                new([("Id: ", Color.White), ("998 · ", Color.LightSkyBlue), ("Monster: Kitava Zealot (Rare)", Color.LightGreen), (" · (629, 928)", Color.LightYellow)]),
                new(miseryIcon, [("Deafening Essence of Misery", Color.Violet), ("  12.5c", Color.LightGreen)]),
                new(scornIcon, [("Shrieking Essence of Scorn", Color.Violet), ("  2.9c", Color.LightGreen)]),
                new(zealIcon, [("Wailing Essence of Zeal", Color.White), ("  0.8c", Color.LightGreen)]),
                new(vaalIcon, [("Corrupt EV ", Color.Cyan), ("(Monster: 16.2c · Vaal: 0.29c)", Color.Gray)]),
                new([("  no change: ", Color.White), ("+0c", Color.LightGray)]),
                new([("  tier +1: ", Color.White), ("+18.4c", Color.LightGray)]),
                new([("  added essence: ", Color.White), ("+0c..+32.5c", Color.LightGray)]),
                new([("  transformed: ", Color.White), ("+8.5c..+24.0c", Color.LightGray)]),
                new([("  net: ", Color.White), ("-0.29c..+32.5c ", Color.LightGray), ("(avg +15.2c)", Color.LimeGreen)]),
            ];
        }

        private List<EssenceRenderer.DebugLine> GetKillDebugLines()
        {
            var vaalIcon = _icons?.For("Vaal");
            var greed = EssenceCatalogue.FindByName("Deafening Essence of Greed");
            var anger = EssenceCatalogue.FindByName("Screaming Essence of Anger");
            var torment = EssenceCatalogue.FindByName("Wailing Essence of Torment");

            var greedIcon = greed != null ? _icons?.For(greed) ?? _icons?.For(greed.TypeName) : null;
            var angerIcon = anger != null ? _icons?.For(anger) ?? _icons?.For(anger.TypeName) : null;
            var tormentIcon = torment != null ? _icons?.For(torment) ?? _icons?.For(torment.TypeName) : null;

            return
            [
                new([("Essence #2 · ", Color.White), ("KILL-ME", Color.Green), (" (No valuable corruption targets)", Color.Gray)]),
                new([("Id: ", Color.White), ("1042 · ", Color.LightSkyBlue), ("Monster: Misery Behemoth (Rare)", Color.LightGreen), (" · (640, 950)", Color.LightYellow)]),
                new(greedIcon, [("Deafening Essence of Greed", Color.White), ("  4.4c", Color.LightGreen)]),
                new(angerIcon, [("Screaming Essence of Anger", Color.White), ("  1.2c", Color.LightGreen)]),
                new(tormentIcon, [("Wailing Essence of Torment", Color.White), ("  -", Color.Gray)]),
                new(vaalIcon, [("Corrupt EV ", Color.Cyan), ("(Monster: 5.6c · Vaal: 0.29c)", Color.Gray)]),
                new([("  no change: ", Color.White), ("+0c", Color.LightGray)]),
                new([("  tier +1: ", Color.White), ("+1.1c", Color.LightGray)]),
                new([("  added essence: ", Color.White), ("-0.29c..+1.5c", Color.LightGray)]),
                new([("  transformed: ", Color.White), ("-3.8c..-0.5c", Color.LightGray)]),
                new([("  net: ", Color.White), ("-3.8c..+1.5c ", Color.LightGray), ("(avg -0.8c)", Color.OrangeRed)]),
            ];
        }
    }
}
