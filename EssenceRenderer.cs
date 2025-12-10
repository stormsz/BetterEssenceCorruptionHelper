using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Models;
using ExileCore;
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
            if (!ShouldRender())
                return;

            DrawEssenceIndicators();
            DrawMapStatsWindow();
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Determines if we should render indicators this frame.
        /// </summary>
        private bool ShouldRender() =>
            _settings.Enable.Value &&
            _gameController.InGame &&
            !IsAnyGameUIVisible();

        /// <summary>
        /// Checks if any game UI panel is open.
        /// </summary>
        private bool IsAnyGameUIVisible()
        {
            var ui = _gameController.IngameState.IngameUi;
            return ui.InventoryPanel.IsVisible ||
                    ui.OpenLeftPanel.IsVisible ||
                    ui.TreePanel.IsVisible ||
                    ui.Atlas.IsVisible ||
                    ui.SyndicatePanel.IsVisible ||
                    ui.DelveWindow.IsVisible ||
                    ui.IncursionWindow.IsVisible ||
                    ui.HeistWindow.IsVisible ||
                    ui.ExpeditionWindow.IsVisible ||
                    ui.RitualWindow.IsVisible ||
                    ui.UltimatumPanel.IsVisible;
        }

        /// <summary>
        /// Main rendering method - draws all essence indicators and debug windows.
        /// </summary>
        private void DrawEssenceIndicators()
        {
            if (!_settings.Indicators.EnableAllIndicators.Value)
                return;

            // Snapshot to avoid modification during enumeration
            var snapshot = new List<EssenceEntityData>(_entityTracker.TrackedEntities);

            foreach (var data in snapshot)
            {
                DrawEssenceIndicator(data);

                // Draw debug window for this specific essence if enabled
                if (_settings.Debug.ShowDebugInfo.Value && data.Label?.Label != null)
                    DrawEssenceDebugWindow(data);
            }
        }

        /// <summary>
        /// Draws indicators (border + text) for a single essence based on its state.
        /// </summary>
        private void DrawEssenceIndicator(EssenceEntityData data)
        {
            var isCorruptTarget = data.State == EssenceState.ShouldCorrupt;

            // Draw corrupt-me indicators (red border/text)
            if (isCorruptTarget && _settings.Indicators.CorruptMe.ShowCorruptMe.Value)
            {
                if (_settings.Indicators.CorruptMe.DrawBorder.Value)
                    DrawStatusBox(data, true);
                if (_settings.Indicators.CorruptMe.DrawText.Value)
                    DrawStatusText(data, true);
            }
            // Draw kill-ready indicators (green border/text)
            else if (data.State == EssenceState.ShouldKill && _settings.Indicators.KillReady.ShowKillReady.Value)
            {
                if (_settings.Indicators.KillReady.DrawBorder.Value)
                    DrawStatusBox(data, false);
                if (_settings.Indicators.KillReady.DrawText.Value)
                    DrawStatusText(data, false);
            }
        }

        /// <summary>
        /// Draws colored border box around an essence label using ImGui background draw list.
        /// </summary>
        private void DrawStatusBox(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var indicatorSettings = GetIndicatorSettings(isCorruptTarget);

            // Get ImGui background draw list for overlay rendering
            var drawList = ImGui.GetBackgroundDrawList();

            // Calculate border rectangle with margins
            var borderRect = new RectangleF(
                rect.X + 40,
                rect.Y,
                rect.Width - 80,
                rect.Height
            );

            var borderColor = ToImguiVec4(indicatorSettings.BorderColor);
            var min = new Vector2(borderRect.X, borderRect.Y);
            var max = new Vector2(borderRect.Right, borderRect.Bottom);

            // Draw optional background fill
            if (isCorruptTarget && _settings.Indicators.CorruptMe.BackgroundFill.Value)
            {
                var fillColor = ToImguiVec4(new SharpDX.Color(
                    (byte)255,
                    (byte)0,
                    (byte)0,
                    (byte)(_settings.Indicators.CorruptMe.BackgroundOpacity.Value * 255)));
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor));
            }
            else if (!isCorruptTarget && _settings.Indicators.KillReady.BackgroundFill.Value)
            {
                var fillColor = ToImguiVec4(new SharpDX.Color(
                    (byte)0,
                    (byte)255,
                    (byte)0,
                    (byte)(_settings.Indicators.KillReady.BackgroundOpacity.Value * 255)));
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor));
            }

            // Draw border (2px thick for visibility)
            drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), 0.0f, ImDrawFlags.None, 2.0f);
        }

        /// <summary>
        /// Draws "CORRUPT" or "KILL" text above essence label.
        /// </summary>
        private void DrawStatusText(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var indicatorSettings = GetIndicatorSettings(isCorruptTarget);
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
            var textColor = ToImguiVec4(indicatorSettings.TextColor);
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

            // Begin unique window for THIS essence
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

                ImGui.End();
            }

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

            // Begin window
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

                ImGui.End();
            }

            ImGui.PopStyleColor(5);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Gets indicator settings based on essence state.
        /// </summary>
        private EssenceIndicatorSettings GetIndicatorSettings(bool isCorruptTarget)
        {
            dynamic settings = isCorruptTarget
                ? _settings.Indicators.CorruptMe
                : _settings.Indicators.KillReady;

            return new EssenceIndicatorSettings
            {
                BorderColor = settings.BorderColor.Value,
                TextColor = settings.TextColor.Value
            };
        }

        /// <summary>
        /// Determines if entity processing should run this frame.
        /// </summary>
        private bool ShouldProcess() =>
            _settings.Enable.Value &&
            _gameController.InGame &&
            !_gameController.Area.CurrentArea.IsPeaceful &&
            !IsAnyGameUIVisible();

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