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

        public void Draw()
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("LIVE OVERLAY PREVIEW");

            if (!_settings.Indicators.EnableAllIndicators.Value)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "(All Indicators Disabled)");
            }

            ImGui.Spacing();

            // Preview canvas child window - rendered side-by-side (or stacked on narrow windows)
            var availWidth = ImGui.GetContentRegionAvail().X;
            var isStacked = availWidth < (PreviewCanvas.LabelWidth * 2f + 40f);
            var canvasHeight = isStacked ? 360f : 230f;

            if (PreviewCanvas.Begin("##indicatorPreviewCanvas", canvasHeight, out var drawList, out var canvasMin, out _))
                DrawScene(drawList, canvasMin, availWidth, isStacked);
            PreviewCanvas.End();
        }

        private void DrawScene(ImDrawListPtr drawList, Vector2 canvasMin, float availWidth, bool isStacked)
        {
            const float labelWidth = PreviewCanvas.LabelWidth;
            const float labelHeight = PreviewCanvas.LabelHeight;
            const float labelYOffset = 76f; // Leaves headroom above label for status & value badges
            var labelY = canvasMin.Y + labelYOffset;

            if (!isStacked)
            {
                // Wide Side-by-Side: distribute evenly with equal margins
                var spacing = (availWidth - 2f * labelWidth) / 3f;
                var leftCenterX = canvasMin.X + spacing + labelWidth / 2f;
                var rightCenterX = canvasMin.X + spacing * 2f + labelWidth * 1.5f;

                DrawSimulatedMonolith(drawList, leftCenterX, labelY, isCorruptTarget: true);
                DrawSimulatedMonolith(drawList, rightCenterX, labelY, isCorruptTarget: false);
            }
            else
            {
                // Narrow Side-by-Side: Stack them vertically so both remain visible
                var centerX = canvasMin.X + availWidth * 0.5f;
                DrawSimulatedMonolith(drawList, centerX, labelY, isCorruptTarget: true);
                DrawSimulatedMonolith(drawList, centerX, labelY + labelHeight + 55f, isCorruptTarget: false);
            }
        }

        private void DrawSimulatedMonolith(ImDrawListPtr drawList, float centerX, float labelY, bool isCorruptTarget)
        {
            const float labelWidth = PreviewCanvas.LabelWidth;
            const float labelHeight = PreviewCanvas.LabelHeight;

            var labelMin = new Vector2(centerX - labelWidth / 2f, labelY);
            var labelMax = new Vector2(centerX + labelWidth / 2f, labelY + labelHeight);

            // 1. Draw simulated in-game ground label (with simulated Vaal button on corrupt target)
            PreviewCanvas.DrawGroundLabel(drawList, _icons, labelMin, labelMax,
                isCorruptTarget ? PreviewCanvas.CorruptSample : PreviewCanvas.KillSample,
                drawVaalButton: isCorruptTarget);

            // 2. If master toggle is disabled, do not draw overlay
            if (!_settings.Indicators.EnableAllIndicators.Value)
                return;

            // 3. Draw live indicator overlay exactly matching EssenceRenderer
            IIndicatorSettings indicator = isCorruptTarget
                ? _settings.Indicators.CorruptMe
                : _settings.Indicators.KillReady;

            if (!indicator.ShowIndicator.Value)
                return;

            var corruptMe = _settings.Indicators.CorruptMe;
            var highlightButton = isCorruptTarget && corruptMe.HighlightVaalButton.Value;
            var showValue = _settings.Indicators.ShowValueRange.Value;

            if (!indicator.DrawBorder.Value && !indicator.DrawText.Value && !highlightButton && !showValue)
                return;

            var insetX = _settings.Indicators.BoxInsetX.Value;
            var insetY = _settings.Indicators.BoxInsetY.Value;

            var drawRect = new RectangleF(
                labelMin.X + insetX,
                labelMin.Y + insetY,
                labelWidth - insetX * 2,
                labelHeight - insetY * 2);

            // Box / Fill on Card
            if (indicator.DrawBorder.Value)
                DrawStatusBox(drawList, drawRect, indicator);

            // Vaal Button Highlight
            if (highlightButton)
            {
                var buttonRect = PreviewCanvas.GetSimulatedVaalButtonRect(labelMin, labelMax);
                DrawVaalButtonHighlight(drawList, buttonRect, corruptMe);
            }

            // Status Indicator (Vaal Icon or Text)
            var drewStatus = false;
            if (indicator.DrawText.Value)
            {
                if (isCorruptTarget && corruptMe.ShowVaalIcon.Value && _icons?.For("Vaal") is { } vaalIcon)
                {
                    DrawStatusVaalIcon(drawList, drawRect, vaalIcon, corruptMe.BorderColor.Value);
                    drewStatus = true;
                }
                else
                {
                    DrawStatusText(drawList, drawRect, indicator, isCorruptTarget);
                    drewStatus = true;
                }
            }

            // Value Text (+15.2c expected)
            if (showValue && isCorruptTarget)
            {
                var valueText = "+15.2c expected";
                DrawValueText(drawList, drawRect, valueText, Color.LimeGreen, drewStatus, corruptMe.ShowVaalIcon.Value);
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

        private static void DrawVaalButtonHighlight(ImDrawListPtr drawList, RectangleF buttonRect, CorruptMeSettings corruptMe)
        {
            var min = new Vector2(buttonRect.Left, buttonRect.Top);
            var max = new Vector2(buttonRect.Right, buttonRect.Bottom);

            if (corruptMe.BackgroundFill.Value)
            {
                var baseColor = corruptMe.BorderColor.Value;
                var fillColor = EssenceRenderer.ToImguiVec4(new Color(
                    baseColor.R,
                    baseColor.G,
                    baseColor.B,
                    (byte)(corruptMe.BackgroundOpacity.Value * 255)));
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor), 4f);
            }

            var borderColor = EssenceRenderer.ToImguiVec4(corruptMe.BorderColor.Value);
            drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), 4f, ImDrawFlags.None, 2.5f);
        }

        private static void DrawStatusVaalIcon(ImDrawListPtr drawList, RectangleF rect, nint vaalIcon, Color borderColor)
        {
            var iconSize = new Vector2(24f, 24f);
            var center = new Vector2(rect.Center.X, rect.Top - 22f);

            var bgMin = new Vector2(center.X - iconSize.X / 2f - 4f, center.Y - iconSize.Y / 2f - 3f);
            var bgMax = new Vector2(center.X + iconSize.X / 2f + 4f, center.Y + iconSize.Y / 2f + 3f);

            var bgColor = new Vector4(0f, 0f, 0f, 200f / 255f);
            var borderVec = EssenceRenderer.ToImguiVec4(borderColor);

            drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(bgColor), 4f);
            drawList.AddRect(bgMin, bgMax, ImGui.GetColorU32(borderVec), 4f, ImDrawFlags.None, 1.2f);

            var iconMin = new Vector2(center.X - iconSize.X / 2f, center.Y - iconSize.Y / 2f);
            var iconMax = new Vector2(center.X + iconSize.X / 2f, center.Y + iconSize.Y / 2f);
            drawList.AddImage(vaalIcon, iconMin, iconMax);
        }

        private static void DrawStatusText(ImDrawListPtr drawList, RectangleF rect, IIndicatorSettings indicator, bool isCorruptTarget)
        {
            var text = isCorruptTarget ? "CORRUPT" : "KILL";
            var textPos = new Vector2(rect.Center.X, rect.Top - 25);
            DrawCenteredTextWithBackground(drawList, textPos, text, EssenceRenderer.ToImguiVec4(indicator.TextColor.Value));
        }

        private static void DrawValueText(ImDrawListPtr drawList, RectangleF rect, string text, Color color, bool belowStatusIndicator, bool isVaalIcon = false)
        {
            var spacing = isVaalIcon ? 34f : ImGui.GetTextLineHeight() + 12f;
            var y = rect.Top - 25f - (belowStatusIndicator ? spacing : 0f);
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
