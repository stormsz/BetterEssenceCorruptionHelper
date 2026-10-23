using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Models;
using BetterEssenceCorruptionHelper.Pricing;
using BetterEssenceCorruptionHelper.Strategy;
using BetterEssenceCorruptionHelper.UI;
using ExileCore;
using ExileCore.PoEMemory;
using ImGuiNET;
using SharpDX;
using System.Collections.Generic;
using Vector2 = System.Numerics.Vector2;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Handles all rendering and ImGui drawing. Only draws: everything it shows is computed by the
    /// tracker or cached here against version counters, so a frame does no analysis and no string
    /// building unless something changed.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the EssenceRenderer class.
    /// </remarks>
    /// <param name="gameController">The game controller instance</param>
    /// <param name="settings">Plugin settings</param>
    /// <param name="mapStats">Statistics tracker</param>
    /// <param name="entityTracker">Entity tracker</param>
    /// <param name="prices">Price snapshot</param>
    /// <param name="values">Complete essence values built from the price snapshot</param>
    /// <param name="setup">Map setup read from the game</param>
    /// <param name="corruptionLog">Observed corruption outcomes</param>
    internal class EssenceRenderer(GameController gameController, Settings settings, MapStatistics mapStats, EssenceEntityTracker entityTracker,
        PriceBook prices, EssenceValues values, SetupDetector setup, CorruptionLog corruptionLog)
    {
        #region Fields

        private readonly GameController _gameController = gameController;
        private readonly Settings _settings = settings;
        private readonly MapStatistics _mapStats = mapStats;
        private readonly EssenceEntityTracker _entityTracker = entityTracker;
        private readonly PriceBook _prices = prices;
        private readonly EssenceValues _values = values;
        private readonly SetupDetector _setup = setup;
        private readonly CorruptionLog _corruptionLog = corruptionLog;

        /// <summary>Debug overlay content per essence, rebuilt only when its inputs change.</summary>
        private readonly Dictionary<uint, (int Key, List<List<(string text, Color color)>> Lines, float Width)> _debugCache = new();

        private (int, int, int, int) _statsKey = (-1, -1, -1, -1);
        private readonly List<(string Text, Color Color)> _statsLines = new();

        #endregion

        #region Public Methods

        /// <summary>
        /// Renders all visual elements. <paramref name="canRender"/> is the shared gate the plugin
        /// evaluated this frame (enabled, in game, no full-screen panel open).
        /// </summary>
        public void Render(bool canRender)
        {
            if (!canRender)
                return;

            DrawEssenceIndicators();
            DrawMapStatsWindow();
        }

        /// <summary>Drops per-essence caches (area change).</summary>
        public void Reset() => _debugCache.Clear();

        #endregion

        #region Rendering

        /// <summary>
        /// Main rendering method - draws all essence indicators and debug windows.
        /// </summary>
        private void DrawEssenceIndicators()
        {
            if (!_settings.Indicators.EnableAllIndicators.Value)
                return;

            // The tracker is only written on the main thread (Tick and entity events), the same one
            // that calls Render, so we iterate it directly instead of copying a snapshot every frame.
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
        /// The corruption value analysis for an essence, recomputed only when its contents, the
        /// prices, the strategy or the detected map setup changed.
        /// </summary>
        private CorruptionAssessment? GetAssessment(EssenceEntityData data)
        {
            var preferences = _settings.Tracking;
            var key = (data.AnalysisVersion, _prices.Version, preferences.Version * 7919 + _setup.Version);
            if (data.AssessmentKey == key)
                return data.Assessment;

            data.AssessmentKey = key;
            data.Assessment = null;
            data.ValueText = null;
            data.AssessmentUnavailableReason = null;

            if (!data.Analysis.IsValid || data.Analysis.IsCorrupted)
                return null;

            data.Assessment = CorruptionModel.Assess(
                data.Analysis.Essences,
                data.Analysis.UnrecognizedEssenceLines.Count,
                _setup.Resolve(),
                _values.ValueOf,
                _prices.VaalOrb.Value,
                out var reason,
                _values.IsApproximate);
            data.AssessmentUnavailableReason = reason;

            if (data.Assessment is { } a)
                data.ValueText = $"Vaal: {Format.SignedChaos(a.ExpectedNetGain)} expected";

            return data.Assessment;
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
            var state = data.State;

            IIndicatorSettings indicator = state switch
            {
                EssenceState.ShouldCorrupt => _settings.Indicators.CorruptMe,
                EssenceState.ShouldKill => _settings.Indicators.KillReady,
                _ => null!
            };

            if (indicator is null || !indicator.ShowIndicator.Value)
                return;

            var showValue = _settings.Indicators.ShowValueRange.Value;
            if (!indicator.DrawBorder.Value && !indicator.DrawText.Value && !showValue)
                return;

            // Resolve geometry once and share it between the box and the text.
            var rect = GetDrawRect(data);
            if (rect is not { } drawRect)
                return;

            var isCorruptTarget = state == EssenceState.ShouldCorrupt;

            if (indicator.DrawBorder.Value)
                DrawStatusBox(drawRect, indicator);

            if (indicator.DrawText.Value)
                DrawStatusText(drawRect, indicator, isCorruptTarget);

            // The expected gain matters where a Vaal Orb is being considered: on CORRUPT monsters only.
            if (showValue && isCorruptTarget && GetAssessment(data) is { } assessment && data.ValueText != null)
                DrawValueText(drawRect, data.ValueText, assessment.ExpectedNetGain >= 0 ? Color.LimeGreen : Color.Orange, indicator.DrawText.Value);
        }

        /// <summary>
        /// Draws colored border box around an essence label using ImGui background draw list.
        /// </summary>
        private void DrawStatusBox(RectangleF rect, IIndicatorSettings indicator)
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
            DrawCenteredTextWithBackground(textPos, text, indicator.TextColor.Value);
        }

        /// <summary>Draws the value range line above the status text (or where it would be).</summary>
        private static void DrawValueText(RectangleF rect, string text, Color color, bool belowStatusText)
        {
            var lineHeight = ImGui.GetTextLineHeight() + 12;
            var y = rect.Top - 25 - (belowStatusText ? lineHeight : 0);
            DrawCenteredTextWithBackground(new Vector2(rect.Center.X, y), text, color);
        }

        private static void DrawCenteredTextWithBackground(Vector2 textPos, string text, Color color)
        {
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
            var textDrawPos = new Vector2(textPos.X - textSize.X / 2, textPos.Y);
            drawList.AddText(textDrawPos, ImGui.GetColorU32(ToImguiVec4(color)), text);
        }

        /// <summary>
        /// Draws debug window for a SPECIFIC essence showing detailed analysis.
        /// </summary>
        private void DrawEssenceDebugWindow(EssenceEntityData data)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var assessment = GetAssessment(data);

            var key = System.HashCode.Combine(data.AnalysisVersion, data.State, data.MonsterId, data.WasCorruptedByPlayer,
                data.AssessmentKey, data.StateReason, (int)(data.GridPos?.X ?? 0), (int)(data.GridPos?.Y ?? 0));
            if (!_debugCache.TryGetValue(data.Id, out var cached) || cached.Key != key)
            {
                var lines = BuildDebugContent(data, assessment);
                var width = 0f;
                foreach (var segments in lines)
                {
                    var lineWidth = 0f;
                    foreach (var (text, _) in segments)
                        lineWidth += ImGui.CalcTextSize(text).X;
                    if (lineWidth > width)
                        width = lineWidth;
                }

                cached = (key, lines, width);
                _debugCache[data.Id] = cached;
            }

            var debugLines = cached.Lines;

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

            // Set window dimensions (minimum width 260px, dynamic height)
            var windowSize = new Vector2(Math.Max(260, cached.Width + 15), 10 + (debugLines.Count * 16));

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

                    if (segments.Count == 0)
                        ImGui.Text("");
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

            var key = (_mapStats.Version, _corruptionLog.Version, _settings.Tracking.Version, _setup.Version);
            if (key != _statsKey)
            {
                _statsKey = key;
                BuildStatsLines();
            }

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
                ImGui.TextColored(ToImguiVec4(SharpDX.Color.White), "Map Totals");
                ImGui.Separator();

                foreach (var (text, color) in _statsLines)
                {
                    if (text.Length == 0)
                        ImGui.Separator();
                    else
                        ImGui.TextColored(ToImguiVec4(color), text);
                }
            }

            ImGui.End();

            ImGui.PopStyleColor(5);
        }

        /// <summary>Stats window text - rebuilt only when the stats, the outcome log or the strategy change.</summary>
        private void BuildStatsLines()
        {
            _statsLines.Clear();
            var statColor = _settings.MapStats.TextColor.Value;

            // Display statistics
            _statsLines.Add(($"  Killed: {_mapStats.TotalKilled}", statColor));
            _statsLines.Add(($"  Corrupted: {_mapStats.TotalCorrupted}", statColor));
            _statsLines.Add(($"  Missed: {_mapStats.TotalMissed}", statColor));
            _statsLines.Add(($"  Mistakes: {_mapStats.TotalMistakes}", statColor));

            _statsLines.Add(("", statColor));
            _statsLines.Add(($"Strategy: {_settings.Tracking.Strategy.DisplayName}", Color.LightSkyBlue));
            if (SetupStatus.Describe(_settings.Tracking.Strategy, _setup) is { } warning)
                _statsLines.Add((warning.Title, warning.Level == SetupStatus.Level.Problem ? Color.OrangeRed : Color.Yellow));

            if (_corruptionLog.Total == 0)
                return;

            _statsLines.Add(($"Session corruptions: {_corruptionLog.Total}", Color.White));
            var parts = new List<string>();
            foreach (var outcome in System.Enum.GetValues<CorruptionOutcome>())
            {
                var count = _corruptionLog.CountOf(outcome);
                if (count > 0)
                    parts.Add($"{OutcomeName(outcome)} {count}");
            }
            if (_corruptionLog.Unclassified > 0)
                parts.Add($"unclassified {_corruptionLog.Unclassified}");
            _statsLines.Add(($"  {string.Join(" · ", parts)}", statColor));

            if (_corruptionLog.RealizedGainCount > 0)
            {
                var gain = _corruptionLog.RealizedGainSum;
                _statsLines.Add(($"  Realised {Format.SignedChaos(gain)} over {_corruptionLog.RealizedGainCount} (listing prices)",
                    gain >= 0 ? Color.LimeGreen : Color.OrangeRed));
            }
        }

        #endregion

        #region Helpers

        internal static string OutcomeName(CorruptionOutcome outcome) => outcome switch
        {
            CorruptionOutcome.NoChange => "no change",
            CorruptionOutcome.TierUpgrade => "tier +1",
            CorruptionOutcome.AddEssence => "added essence",
            CorruptionOutcome.Transform => "transformed",
            CorruptionOutcome.ResonanceReplace => "resonance copy",
            _ => outcome.ToString(),
        };

        /// <summary>
        /// Builds debug content as list of lines.
        /// </summary>
        private List<List<(string text, Color color)>> BuildDebugContent(EssenceEntityData data, CorruptionAssessment? assessment)
        {
            var lines = new List<List<(string, Color)>>();

            AddDebugHeader(lines, data);
            AddDebugEntityInfo(lines, data);
            AddDebugAnalysis(lines, data);
            AddDebugAssessment(lines, data, assessment);

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
            lines.Add([("Status: ", Color.White), (stateString, stateColor), ($" ({data.StateReason})", Color.Gray)]);

            if (data.WasCorruptedByPlayer)
                lines.Add([("Player Corrupted: ", Color.White), ("Yes", Color.Yellow)]);

            lines.Add([]); // Empty line for spacing
        }

        /// <summary>
        /// Adds debug info showing the entity identity and the trapped monster linked to it - the
        /// monster whose death counts as the kill - so both can be checked against the game.
        /// </summary>
        private static void AddDebugEntityInfo(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            lines.Add([("Id: ", Color.White), ($"{data.Id}", Color.LightSkyBlue)]);
            lines.Add([("Addr: ", Color.White), ($"0x{data.Address:X12}", Color.LightSkyBlue)]);
            lines.Add(data.MonsterId is { } monsterId
                ? [("Monster: ", Color.White), ($"id={monsterId}", Color.LightGreen), ($" {SafeRenderName(data)} ({EssenceEntityTracker.RarityName(data.MonsterRarityRank)})", Color.Gray)]
                : [("Monster: ", Color.White), ("not linked", Color.OrangeRed)]);

            if (data.GridPos is { } pos)
                lines.Add([("Grid: ", Color.White), ($"({pos.X:F0}, {pos.Y:F0})", Color.LightYellow)]);

            lines.Add([]); // Empty line for spacing
        }

        /// <summary>The linked monster's name; its entity may be stale (unloaded), so a failed read is tolerated.</summary>
        private static string SafeRenderName(EssenceEntityData data)
        {
            try
            {
                return data.Monster?.RenderName ?? "";
            }
            catch
            {
                return "(unreadable)";
            }
        }

        /// <summary>
        /// Adds essence analysis details: each essence with its price, before/after when corrupted.
        /// </summary>
        private void AddDebugAnalysis(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            if (!data.Analysis.IsValid)
            {
                lines.Add([("ANALYSIS: INVALID", Color.Red)]);
                return;
            }

            if (data.WasCorruptedByPlayer && data.PreviousAnalysis is { IsValid: true } before)
            {
                var outcome = OutcomeClassifier.Classify(before.Essences, data.Analysis.Essences, out var ambiguous,
                    CorruptionModel.ApplicableOutcomes(_setup.Resolve()));
                lines.Add([("ANALYSIS (Before -> After): ", Color.Cyan),
                    (outcome is { } o ? OutcomeName(o) + (ambiguous ? "?" : "") : "unclassified", Color.Yellow)]);
                AddEssenceLines(lines, before, "  before ");
                AddEssenceLines(lines, data.Analysis, "  after  ");
                return;
            }

            lines.Add([("ANALYSIS: ", Color.Cyan), ($"{data.Analysis.EssenceCount} essences{(data.Analysis.IsCorrupted ? ", corrupted" : "")}", Color.White)]);
            AddEssenceLines(lines, data.Analysis, "  ");
        }

        private void AddEssenceLines(List<List<(string, Color)>> lines, EssenceAnalysis analysis, string indent)
        {
            foreach (var essence in analysis.Essences)
            {
                var value = _values.Get(essence);
                lines.Add([(indent + essence.Name, essence.IsMeds ? Color.Violet : Color.White),
                    ("  " + Format.Value(value), value.Source == ValueSource.Market ? Color.LightGreen : Color.Gray)]);
            }

            foreach (var line in analysis.UnrecognizedEssenceLines)
                lines.Add([(indent + line, Color.Orange), ("  (not in catalogue)", Color.Gray)]);
        }

        /// <summary>Adds the corruption value analysis: per-outcome ranges, bounds and verdict.</summary>
        private static void AddDebugAssessment(List<List<(string, Color)>> lines, EssenceEntityData data, CorruptionAssessment? assessment)
        {
            if (data.Analysis.IsCorrupted || !data.Analysis.IsValid)
                return;

            lines.Add([]);
            if (assessment == null)
            {
                lines.Add([("VAAL ORB VALUE: ", Color.Cyan), (data.AssessmentUnavailableReason ?? "unavailable", Color.Gray)]);
                return;
            }

            lines.Add([("VAAL ORB VALUE ", Color.Cyan), ($"(now {Format.Chaos(assessment.CurrentValue)}, orb {Format.Chaos(assessment.VaalOrbCost)})", Color.Gray)]);
            foreach (var o in assessment.Outcomes)
            {
                var range = Math.Abs(o.MaxGain - o.MinGain) < 1e-9
                    ? Format.SignedChaos(o.MinGain)
                    : $"{Format.SignedChaos(o.MinGain)}..{Format.SignedChaos(o.MaxGain)}";
                lines.Add([($"  {OutcomeName(o.Outcome)}: ", Color.White), (range, Color.LightGray)]);
            }

            var (verdictText, verdictColor) = assessment.Verdict switch
            {
                CorruptionVerdict.AlwaysGains => ("gains in every outcome", Color.LimeGreen),
                CorruptionVerdict.AlwaysLoses => ("loses in every outcome", Color.OrangeRed),
                _ => ("depends on unpublished odds", Color.Yellow),
            };
            var approx = assessment.IsApproximate ? " (approx.)" : "";
            lines.Add([("  net: ", Color.White), ($"{Format.SignedChaos(assessment.WorstNetGain)}..{Format.SignedChaos(assessment.BestNetGain)}, ", Color.LightGray), (verdictText + approx, verdictColor)]);

            if (assessment.EstimatedItems.Count > 0)
                lines.Add([("  estimated: ", Color.Gray), (string.Join(", ", assessment.EstimatedItems), Color.Gray)]);
            if (assessment.UnpricedItems.Count > 0)
                lines.Add([("  counted as 0c: ", Color.Gray), (string.Join(", ", assessment.UnpricedItems), Color.Gray)]);
        }

        /// <summary>
        /// Converts SharpDX Color to ImGui Vector4 format.
        /// </summary>
        internal static System.Numerics.Vector4 ToImguiVec4(SharpDX.Color color)
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
