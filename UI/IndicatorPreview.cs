using ImGuiNET;
using SharpDX;
using System;
using System.Numerics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace BetterEssenceCorruptionHelper.UI
{
    /// <summary>
    /// Live simulation preview rendered inside the Essence Indicators settings submenu.
    /// Draws a simulated in-game essence monolith ground label with the exact live overlay
    /// applied from current settings, updating in real time as the user adjusts controls.
    /// </summary>
    internal sealed class IndicatorPreview(Settings settings, EssenceIcons icons)
    {
        private readonly Settings _settings = settings;
        private readonly EssenceIcons _icons = icons;
        private int _previewMode = 2; // 0 = Corrupt-Me, 1 = Kill-Ready, 2 = Side-by-Side (default)

        public void Draw()
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("LIVE OVERLAY PREVIEW");
            ImGui.SameLine(0, 16);
            ImGui.RadioButton("Side-by-Side##indicatorPreview", ref _previewMode, 2);
            ImGui.SameLine();
            ImGui.RadioButton("Corrupt-Me##indicatorPreview", ref _previewMode, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Kill-Ready##indicatorPreview", ref _previewMode, 1);

            if (!_settings.Indicators.EnableAllIndicators.Value)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "(All Indicators Disabled)");
            }

            ImGui.Spacing();

            // Preview canvas child window
            var availWidth = ImGui.GetContentRegionAvail().X;
            var isStacked = _previewMode == 2 && availWidth < 620f;
            var canvasHeight = isStacked ? 380f : 240f;

            if (PreviewCanvas.Begin("##indicatorPreviewCanvas", canvasHeight, out var drawList, out var canvasMin, out _))
                DrawScene(drawList, canvasMin, availWidth);
            PreviewCanvas.End();
        }

        private void DrawScene(ImDrawListPtr drawList, Vector2 canvasMin, float availWidth)
        {
            var labelY = canvasMin.Y + 98f; // Leaves comfortable headroom above label for CORRUPT & profit badges

            if (_previewMode == 2 && availWidth >= 620f)
            {
                // Wide Side-by-Side
                var leftCenterX = canvasMin.X + availWidth * 0.28f;
                var rightCenterX = canvasMin.X + availWidth * 0.72f;

                DrawSimulatedMonolith(drawList, leftCenterX, labelY, isCorruptTarget: true);
                DrawSimulatedMonolith(drawList, rightCenterX, labelY, isCorruptTarget: false);
            }
            else if (_previewMode == 2 && availWidth < 620f)
            {
                // Narrow Side-by-Side: Stack them vertically so both remain visible
                var centerX = canvasMin.X + availWidth * 0.5f;
                DrawSimulatedMonolith(drawList, centerX, labelY, isCorruptTarget: true);
                DrawSimulatedMonolith(drawList, centerX, labelY + 145f, isCorruptTarget: false);
            }
            else if (_previewMode == 1)
            {
                // Kill Ready centered
                var centerX = canvasMin.X + availWidth * 0.5f;
                DrawSimulatedMonolith(drawList, centerX, labelY, isCorruptTarget: false);
            }
            else
            {
                // Corrupt Target centered (default)
                var centerX = canvasMin.X + availWidth * 0.5f;
                DrawSimulatedMonolith(drawList, centerX, labelY, isCorruptTarget: true);
            }
        }

        private void DrawSimulatedMonolith(ImDrawListPtr drawList, float centerX, float labelY, bool isCorruptTarget)
        {
            const float labelWidth = PreviewCanvas.LabelWidth;
            const float labelHeight = PreviewCanvas.LabelHeight;

            var labelMin = new Vector2(centerX - labelWidth / 2f, labelY);
            var labelMax = new Vector2(centerX + labelWidth / 2f, labelY + labelHeight);

            // 1. Draw simulated in-game ground label
            PreviewCanvas.DrawGroundLabel(drawList, _icons, labelMin, labelMax, isCorruptTarget ? PreviewCanvas.CorruptSample : PreviewCanvas.KillSample);

            // 2. If master toggle is disabled, do not draw overlay
            if (!_settings.Indicators.EnableAllIndicators.Value)
                return;

            // 3. Draw live indicator overlay exactly matching EssenceRenderer
            IIndicatorSettings indicator = isCorruptTarget
                ? _settings.Indicators.CorruptMe
                : _settings.Indicators.KillReady;

            if (!indicator.ShowIndicator.Value)
                return;

            var showValue = _settings.Indicators.ShowValueRange.Value;
            if (!indicator.DrawBorder.Value && !indicator.DrawText.Value && !showValue)
                return;

            var insetX = _settings.Indicators.BoxInsetX.Value;
            var insetY = _settings.Indicators.BoxInsetY.Value;

            var drawRect = new RectangleF(
                labelMin.X + insetX,
                labelMin.Y + insetY,
                labelWidth - insetX * 2,
                labelHeight - insetY * 2);

            // Box / Fill
            if (indicator.DrawBorder.Value)
                DrawStatusBox(drawList, drawRect, indicator);

            // Status Text (CORRUPT or KILL)
            if (indicator.DrawText.Value)
                DrawStatusText(drawList, drawRect, indicator, isCorruptTarget);

            // Value Text (+15.2c expected)
            if (showValue && isCorruptTarget)
            {
                var valueText = "+15.2c expected";
                DrawValueText(drawList, drawRect, valueText, Color.LimeGreen, indicator.DrawText.Value);
            }
        }

        private static void DrawStatusBox(ImDrawListPtr drawList, RectangleF rect, IIndicatorSettings indicator)
        {
            var min = new Vector2(rect.X, rect.Y);
            var max = new Vector2(rect.Right, rect.Bottom);

            // Background Fill
            if (indicator.BackgroundFill.Value)
            {
                var baseColor = indicator.BorderColor.Value;
                var fillColor = EssenceRenderer.ToImguiVec4(new Color(
                    baseColor.R,
                    baseColor.G,
                    baseColor.B,
                    (byte)(indicator.BackgroundOpacity.Value * 255)));

                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor));
            }

            // Border (2px thick for visibility, matching EssenceRenderer)
            var borderColor = EssenceRenderer.ToImguiVec4(indicator.BorderColor.Value);
            drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), 0.0f, ImDrawFlags.None, 2.0f);
        }

        private static void DrawStatusText(ImDrawListPtr drawList, RectangleF rect, IIndicatorSettings indicator, bool isCorruptTarget)
        {
            var text = isCorruptTarget ? "CORRUPT" : "KILL";
            var textPos = new Vector2(rect.Center.X, rect.Top - 25);
            DrawCenteredTextWithBackground(drawList, textPos, text, EssenceRenderer.ToImguiVec4(indicator.TextColor.Value));
        }

        private static void DrawValueText(ImDrawListPtr drawList, RectangleF rect, string text, Color color, bool belowStatusText)
        {
            var lineHeight = ImGui.GetTextLineHeight() + 12;
            var y = rect.Top - 25 - (belowStatusText ? lineHeight : 0);
            DrawCenteredTextWithBackground(drawList, new Vector2(rect.Center.X, y), text, EssenceRenderer.ToImguiVec4(color));
        }

        private static void DrawCenteredTextWithBackground(ImDrawListPtr drawList, Vector2 textPos, string text, Vector4 textColor)
        {
            var textSize = ImGui.CalcTextSize(text);
            const float padding = 5f;

            var bgMin = new Vector2(textPos.X - textSize.X / 2 - padding, textPos.Y - padding);
            var bgMax = new Vector2(textPos.X + textSize.X / 2 + padding, textPos.Y + textSize.Y + padding);

            var bgColor = new Vector4(0f, 0f, 0f, 200f / 255f);
            drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(bgColor), 0f);
            drawList.AddText(new Vector2(textPos.X - textSize.X / 2, textPos.Y), ImGui.GetColorU32(textColor), text);
        }
    }
}
