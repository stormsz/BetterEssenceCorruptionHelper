using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Models;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared;
using ImGuiNET;
using SharpDX;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Vector2 = System.Numerics.Vector2;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Handles all rendering and ImGui drawing
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the EssenceRenderer class.
    /// </remarks>
    /// <param name="gameController">The game controller instance</param>
    /// <param name="settings">Plugin settings</param>
    /// <param name="mapStats">Statistics tracker</param>
    /// <param name="entityTracker">Entity tracker</param>
    internal class EssenceRenderer(GameController gameController, Settings settings, MapStatistics mapStats, EssenceEntityTracker entityTracker)
    {
        #region Fields

        private readonly GameController _gameController = gameController;
        private readonly Settings _settings = settings;
        private readonly MapStatistics _mapStats = mapStats;
        private readonly EssenceEntityTracker _entityTracker = entityTracker;

        // Coroutine wait condition
        private readonly WaitRender _uiUpdateWait = new(2); // Every 2 frames

        // Cached string representation of map statistics
        private string _cachedSessionStatsText = "";

        #endregion
        #region Initialization

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets a coroutine for UI updates.
        /// </summary>
        /// <returns>Coroutine that updates UI</returns>
        public IEnumerator GetUIUpdateCoroutine()
        {
            while (true)
            {
                yield return _uiUpdateWait;

                if (ShouldProcess())
                    _entityTracker.UpdateEntityLabels();
            }
        }

        /// <summary>
        /// Updates cached statistics text.
        /// </summary>
        public void UpdateSessionStatsCache()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Map Totals");
            sb.AppendLine($"  Killed: {_mapStats.TotalKilled}");
            sb.AppendLine($"  Corrupted: {_mapStats.TotalCorrupted}");
            sb.AppendLine($"  Missed: {_mapStats.TotalMissed}");
            sb.AppendLine($"  Mistakes: {_mapStats.TotalMistakes}");

            _cachedSessionStatsText = sb.ToString();
        }

        /// <summary>
        /// Renders all visual elements.
        /// </summary>
        public void Render()
        {
            // If not enabled, not ingame, or any big ingame window is open then we dont render
            if (!GameStateGates.ShouldRender(_gameController, _settings))
                return;

            DrawEssenceIndicators();
            DrawMapStatsWindow();
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Main rendering method - draws all essence indicators and debug windows.
        /// </summary>
        private void DrawEssenceIndicators()
        {
            if (!_settings.Indicators.EnableAllIndicators.Value)
                return;

            // ConcurrentDictionary enumeration is already safe against concurrent writes, so we
            // iterate directly instead of copying a snapshot list every single frame.
            var showDebug = _settings.Debug.ShowDebugInfo.Value;

            foreach (var data in _entityTracker.TrackedEntities)
            {
                DrawEssenceIndicator(data);

                // Draw debug window for this specific essence if enabled
                if (showDebug && data.Label?.Label != null)
                    DrawEssenceDebugWindow(data);
            }
        }

        /// <summary>
        /// Returns the rectangle covering a ground label's actual visible content.
        ///
        /// The label root carries invisible horizontal padding - measured live at 40.5px per side
        /// on a 529.9px-wide essence label, with zero vertical padding. That padding is what the
        /// old hardcoded "+40 / -80" was compensating for, but as a constant it broke on narrower
        /// labels (a 129.6px label became a 49.6px sliver). Taking the union of the non-empty
        /// direct children measures it instead, so it stays correct at any label width, resolution
        /// or UI scale.
        /// </summary>
        private static RectangleF GetContentRect(Element root)
        {
            var children = root.Children;
            if (children == null || children.Count == 0)
                return root.GetClientRectCache;

            var found = false;
            float left = 0, top = 0, right = 0, bottom = 0;

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child == null)
                    continue;

                var r = child.GetClientRectCache;
                if (r.Width <= 0 || r.Height <= 0)
                    continue; // Spacer/anchor children report a zero-size rect - ignore them.

                if (!found)
                {
                    left = r.Left; top = r.Top; right = r.Right; bottom = r.Bottom;
                    found = true;
                    continue;
                }

                if (r.Left < left) left = r.Left;
                if (r.Top < top) top = r.Top;
                if (r.Right > right) right = r.Right;
                if (r.Bottom > bottom) bottom = r.Bottom;
            }

            return found
                ? new RectangleF(left, top, right - left, bottom - top)
                : root.GetClientRectCache;
        }

        /// <summary>
        /// Resolves the on-screen rectangle to draw for an essence, or null when nothing should
        /// be drawn this frame.
        ///
        /// Ground labels drop out of ItemsOnGroundLabelsVisible long before the essence is far
        /// enough away for the tracker to clear it, and a label that is no longer visible keeps
        /// returning its last rect - which is how stale boxes end up drifting around the screen.
        /// Requiring IsVisible plus an on-screen test kills that.
        /// </summary>
        private RectangleF? GetDrawRect(EssenceEntityData data)
        {
            var label = data.Label;
            if (label == null || !label.IsVisible || label.Label == null)
                return null;

            var rect = GetContentRect(label.Label);
            if (rect.Width <= 0 || rect.Height <= 0)
                return null;

            var window = _gameController.Window.GetWindowRectangleTimeCache;
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > window.Width || rect.Top > window.Height)
                return null;

            var insetX = _settings.Indicators.BoxInsetX.Value;
            var insetY = _settings.Indicators.BoxInsetY.Value;

            return new RectangleF(
                rect.X + insetX,
                rect.Y + insetY,
                rect.Width - insetX * 2,
                rect.Height - insetY * 2);
        }

        /// <summary>
        /// Draws indicators (border + text) for a single essence based on its state.
        /// </summary>
        private void DrawEssenceIndicator(EssenceEntityData data)
        {
            // Read State once - it is written by the entity-processing coroutine, which may be
            // running on the parallel runner while we render.
            var state = data.State;

            IIndicatorSettings indicator = state switch
            {
                EssenceState.ShouldCorrupt => _settings.Indicators.CorruptMe,
                EssenceState.ShouldKill => _settings.Indicators.KillReady,
                _ => null!
            };

            if (indicator is null || !indicator.ShowIndicator.Value)
                return;

            if (!indicator.DrawBorder.Value && !indicator.DrawText.Value)
                return;

            // Resolve geometry once and share it between the box and the text.
            var rect = GetDrawRect(data);
            if (rect is not { } drawRect)
                return;

            var isCorruptTarget = state == EssenceState.ShouldCorrupt;

            if (indicator.DrawBorder.Value)
                DrawStatusBox(drawRect, indicator, isCorruptTarget);

            if (indicator.DrawText.Value)
                DrawStatusText(drawRect, indicator, isCorruptTarget);
        }

        /// <summary>
        /// Draws colored border box around an essence label using ImGui background draw list.
        /// </summary>
        private void DrawStatusBox(RectangleF rect, IIndicatorSettings indicator, bool isCorruptTarget)
        {
            // Get ImGui background draw list for overlay rendering
            var drawList = ImGui.GetBackgroundDrawList();

            var min = new Vector2(rect.X, rect.Y);
            var max = new Vector2(rect.Right, rect.Bottom);

            // Draw optional background fill, tinted to match the indicator's own border colour
            // rather than a hardcoded pure red/green.
            if (indicator.BackgroundFill.Value)
            {
                var baseColor = indicator.BorderColor.Value;
                var fillColor = ToImguiVec4(new SharpDX.Color(
                    baseColor.R,
                    baseColor.G,
                    baseColor.B,
                    (byte)(indicator.BackgroundOpacity.Value * 255)));

                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor));
            }

            // Draw border (2px thick for visibility)
            var borderColor = ToImguiVec4(indicator.BorderColor.Value);
            drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), 0.0f, ImDrawFlags.None, 2.0f);
        }

        /// <summary>
        /// Draws "CORRUPT" or "KILL" text above essence label.
        /// </summary>
        private void DrawStatusText(RectangleF rect, IIndicatorSettings indicator, bool isCorruptTarget)
        {
            var text = isCorruptTarget ? "CORRUPT" : "KILL";

            // Position text 25 pixels above the essence label
            var textPos = new Vector2(rect.Center.X, rect.Top - 25);

            var drawList = ImGui.GetBackgroundDrawList();

            // Calculate text size for centering
            var textSize = ImGui.CalcTextSize(text);
            var padding = 5f;

            // Draw semi-transparent black background for text readability
            var bgMin = new Vector2(textPos.X - textSize.X / 2 - padding, textPos.Y - padding);
            var bgMax = new Vector2(textPos.X + textSize.X / 2 + padding, textPos.Y + textSize.Y + padding);

            var bgColor = ToImguiVec4(new SharpDX.Color(0, 0, 0, 200));
            drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(bgColor), 0f);

            // Draw centered text
            var textColor = ToImguiVec4(indicator.TextColor.Value);
            var textDrawPos = new Vector2(textPos.X - textSize.X / 2, textPos.Y);
            drawList.AddText(textDrawPos, ImGui.GetColorU32(textColor), text);
        }

        /// <summary>
        /// Draws debug window for a SPECIFIC essence showing detailed analysis.
        /// </summary>
        private void DrawEssenceDebugWindow(EssenceEntityData data)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var debugLines = BuildDebugContent(data);

            // Position flush to right edge of status box
            var debugWindowPos = new Vector2(rect.Right - 40, rect.Top);

            // UNIQUE window ID for THIS SPECIFIC essence
            var windowName = $"###EssenceDebug_{data.EntityId}";

            // Window flags for fixed, non-interactive overlay
            var windowFlags = ImGuiWindowFlags.NoDecoration |
                             ImGuiWindowFlags.NoMove |
                             ImGuiWindowFlags.NoSavedSettings |
                             ImGuiWindowFlags.NoFocusOnAppearing |
                             ImGuiWindowFlags.NoInputs;

            // Apply custom styling
            var bgColor = _settings.Debug.DebugBackgroundColor.Value;
            bgColor.A = (byte)(_settings.Debug.DebugBackgroundOpacity.Value * 255);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, ToImguiVec4(bgColor));
            ImGui.PushStyleColor(ImGuiCol.Border, ToImguiVec4(_settings.Debug.DebugBorderColor.Value));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);

            // Calculate window size based on content width
            var maxLineWidth = 0f;
            foreach (var segments in debugLines)
            {
                var lineWidth = segments.Sum(seg => ImGui.CalcTextSize(seg.text).X);
                if (lineWidth > maxLineWidth)
                    maxLineWidth = lineWidth;
            }

            // Set window dimensions (minimum width 260px, dynamic height)
            var windowSize = new Vector2(Math.Max(260, maxLineWidth + 15), 10 + (debugLines.Count * 16));

            // Set position and size BEFORE Begin()
            ImGui.SetNextWindowPos(debugWindowPos);
            ImGui.SetNextWindowSize(windowSize);

            // Begin unique window for THIS essence.
            // ImGui requires End() for every Begin() regardless of the return value - only the
            // body is conditional. Skipping it corrupts the window stack.
            if (ImGui.Begin(windowName, windowFlags))
            {
                // Draw each line of debug information
                foreach (var segments in debugLines)
                {
                    // Draw all segments on same line
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var (text, color) = segments[i];

                        if (i > 0)
                            ImGui.SameLine(0, 0); // No spacing between segments

                        ImGui.TextColored(ToImguiVec4(color), text);
                    }
                }
            }

            ImGui.End();

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
        }

        /// <summary>
        /// Draws map statistics window
        /// </summary>
        private void DrawMapStatsWindow()
        {
            if (!_settings.MapStats.ShowMapStats.Value ||
                (_gameController.Area.CurrentArea.IsPeaceful && !_settings.MapStats.ShowInTownHideout.Value))
                return;

            // Apply custom window styling
            ImGui.PushStyleColor(ImGuiCol.TitleBg, ToImguiVec4(_settings.MapStats.TitleBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ToImguiVec4(_settings.MapStats.TitleBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ToImguiVec4(_settings.MapStats.ContentBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.Border, ToImguiVec4(_settings.MapStats.BorderColor.Value));
            ImGui.PushStyleColor(ImGuiCol.Text, ToImguiVec4(_settings.MapStats.TitleColor.Value));

            // Begin window. As above, End() is unconditional - only the body is gated on the
            // return value (false means collapsed/clipped, not "no window was pushed").
            if (ImGui.Begin("Essence Map Stats"))
            {
                var statColor = ToImguiVec4(_settings.MapStats.TextColor.Value);

                ImGui.TextColored(ToImguiVec4(SharpDX.Color.White), "Map Totals");
                ImGui.Separator();

                // Display statistics
                ImGui.TextColored(statColor, $"  Killed: {_mapStats.TotalKilled}");
                ImGui.TextColored(statColor, $"  Corrupted: {_mapStats.TotalCorrupted}");
                ImGui.TextColored(statColor, $"  Missed: {_mapStats.TotalMissed}");
                ImGui.TextColored(statColor, $"  Mistakes: {_mapStats.TotalMistakes}");
            }

            ImGui.End();

            ImGui.PopStyleColor(5);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Determines if label refreshing should run this frame.
        /// </summary>
        private bool ShouldProcess() => GameStateGates.ShouldTrackEssences(_gameController, _settings);

        /// <summary>
        /// Builds debug content as list of lines.
        /// </summary>
        private static List<List<(string text, Color color)>> BuildDebugContent(EssenceEntityData data)
        {
            var lines = new List<List<(string, Color)>>();

            AddDebugHeader(lines, data);
            AddDebugPositionInfo(lines, data);
            AddDebugAnalysis(lines, data);

            return lines;
        }

        /// <summary>
        /// Adds debug header showing essence ID and status.
        /// </summary>
        private static void AddDebugHeader(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            var (stateString, stateColor) = data.State switch
            {
                EssenceState.ShouldCorrupt => ("CORRUPT-ME", Color.Red),
                EssenceState.ShouldKill => ("KILL-ME", Color.Green),
                _ => ("UNKNOWN", Color.Gray)
            };

            lines.Add([($"Essence #{data.EntityId}", Color.White)]);
            lines.Add([("Status: ", Color.White), (stateString, stateColor)]);

            if (data.WasCorruptedByPlayer)
                lines.Add([("Player Corrupted: ", Color.White), ("Yes", Color.Yellow)]);

            lines.Add([]); // Empty line for spacing
        }

        /// <summary>
        /// Adds debug info showing entity address and position.
        /// </summary>
        private static void AddDebugPositionInfo(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            lines.Add([("Addr: ", Color.White), ($"0x{data.Address:X12}", Color.LightSkyBlue)]);

            if (data.LastKnownPosition.HasValue)
            {
                var pos = data.LastKnownPosition.Value;
                lines.Add([("Pos: ", Color.White), ($"({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})", Color.LightYellow)]);
            }

            lines.Add([]); // Empty line for spacing
        }

        /// <summary>
        /// Adds essence analysis details.
        /// </summary>
        private static void AddDebugAnalysis(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            if (!data.Analysis.IsValid)
            {
                lines.Add([("ANALYSIS: INVALID", Color.Red)]);
                return;
            }

            var showComparison = ShouldShowComparison(data);

            if (showComparison)
                AddComparisonAnalysis(lines, data);
            else
                AddCurrentAnalysis(lines, data);
        }

        /// <summary>
        /// Checks if we should show before/after comparison.
        /// </summary>
        private static bool ShouldShowComparison(EssenceEntityData data) =>
            data.WasCorruptedByPlayer &&
            data.PreviousAnalysis.HasValue &&
            data.PreviousAnalysis.Value.IsValid;

        /// <summary>
        /// Shows before/after comparison of essence analysis.
        /// </summary>
        private static void AddComparisonAnalysis(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            var prev = data.PreviousAnalysis!.Value;
            lines.Add([("ANALYSIS (Before -> After)", Color.Cyan)]);

            AddComparisonLine(lines, "Total", prev.EssenceCount, data.Analysis.EssenceCount);

            if (prev.DeafeningCount > 0 || data.Analysis.DeafeningCount > 0)
                AddComparisonLine(lines, "Deafening", prev.DeafeningCount, data.Analysis.DeafeningCount);
            if (prev.ShriekingCount > 0 || data.Analysis.ShriekingCount > 0)
                AddComparisonLine(lines, "Shrieking", prev.ShriekingCount, data.Analysis.ShriekingCount);
            if (prev.ScreamingCount > 0 || data.Analysis.ScreamingCount > 0)
                AddComparisonLine(lines, "Screaming", prev.ScreamingCount, data.Analysis.ScreamingCount);

            lines.Add([]);
            AddComparisonLineBool(lines, "MEDS", prev.HasMeds, data.Analysis.HasMeds);
            AddComparisonLineBool(lines, "Valuable", prev.HasValuablePattern, data.Analysis.HasValuablePattern);
        }

        /// <summary>
        /// Shows current essence analysis (no comparison).
        /// </summary>
        private static void AddCurrentAnalysis(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            lines.Add([("ANALYSIS", Color.Cyan)]);
            lines.Add([("Total: ", Color.White), ($"{data.Analysis.EssenceCount}", Color.Green)]);

            if (data.Analysis.DeafeningCount > 0)
                lines.Add([("Deafening: ", Color.White), ($"{data.Analysis.DeafeningCount}", Color.Green)]);
            if (data.Analysis.ShriekingCount > 0)
                lines.Add([("Shrieking: ", Color.White), ($"{data.Analysis.ShriekingCount}", Color.Green)]);
            if (data.Analysis.ScreamingCount > 0)
                lines.Add([("Screaming: ", Color.White), ($"{data.Analysis.ScreamingCount}", Color.White)]);

            lines.Add([]);
            AddBooleanLine(lines, "MEDS", data.Analysis.HasMeds);
            AddBooleanLine(lines, "Valuable", data.Analysis.HasValuablePattern);
        }

        /// <summary>
        /// Adds comparison line showing before/after numeric values.
        /// </summary>
        private static void AddComparisonLine(List<List<(string text, Color color)>> lines, string label, int prev, int curr)
        {
            var currColor = curr > prev ? Color.Green : curr < prev ? Color.Red : Color.White;
            lines.Add([
                ($"{label}: ", Color.White),
                ($"{prev}", Color.White),
                (" -> ", Color.White),
                ($"{curr}", currColor)
            ]);
        }

        /// <summary>
        /// Adds comparison line showing before/after boolean values.
        /// </summary>
        private static void AddComparisonLineBool(List<List<(string text, Color color)>> lines, string label, bool prev, bool curr)
        {
            var currColor = curr ? Color.Green : Color.Red;
            lines.Add([
                ($"{label}: ", Color.White),
                (prev ? "Yes" : "No", Color.White),
                (" -> ", Color.White),
                (curr ? "Yes" : "No", currColor)
            ]);
        }

        /// <summary>
        /// Adds single boolean line with color coding.
        /// </summary>
        private static void AddBooleanLine(List<List<(string text, Color color)>> lines, string label, bool value)
        {
            var color = value ? Color.Green : Color.Red;
            lines.Add([($"{label}: ", Color.White), (value ? "Yes" : "No", color)]);
        }

        /// <summary>
        /// Converts SharpDX Color to ImGui Vector4 format.
        /// </summary>
        private static System.Numerics.Vector4 ToImguiVec4(SharpDX.Color color)
        {
            return new System.Numerics.Vector4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                color.A / 255f
            );
        }

        #endregion
    }
}
