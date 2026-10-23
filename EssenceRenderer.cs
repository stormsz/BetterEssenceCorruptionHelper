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
using Vector4 = System.Numerics.Vector4;

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
    /// <param name="entityTracker">Entity tracker (essences and stats of the current map)</param>
    /// <param name="prices">Price snapshot</param>
    /// <param name="values">Complete essence values built from the price snapshot</param>
    /// <param name="setup">Map setup read from the game</param>
    /// <param name="corruptionLog">Observed corruption outcomes</param>
    /// <param name="icons">Essence and currency icons</param>
    internal class EssenceRenderer(GameController gameController, Settings settings, EssenceEntityTracker entityTracker,
        PriceBook prices, EssenceValues values, SetupDetector setup, CorruptionLog corruptionLog, EssenceIcons? icons = null,
        InventoryScanner? inventory = null)
    {
        #region Fields

        private readonly GameController _gameController = gameController;
        private readonly Settings _settings = settings;
        private readonly EssenceEntityTracker _entityTracker = entityTracker;
        private readonly PriceBook _prices = prices;
        private readonly EssenceValues _values = values;
        private readonly SetupDetector _setup = setup;
        private readonly CorruptionLog _corruptionLog = corruptionLog;
        private readonly EssenceIcons? _icons = icons;
        private readonly InventoryScanner? _inventory = inventory;

        /// <summary>Debug overlay content (and its ImGui window name) per essence, rebuilt only when its inputs change.</summary>
        private readonly Dictionary<uint, (int Key, List<DebugLine> Lines, float Width, string WindowName)> _debugCache = new();


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

            DrawVaalOrbBanner();
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
        /// <summary>
        /// Displays a prominent on-screen warning banner when running in a map below the configured Vaal Orb threshold.
        /// </summary>
        private void DrawVaalOrbBanner()
        {
            if (_inventory == null || !_settings.VaalOrbAlert.EnableAlert.Value || !_settings.VaalOrbAlert.ShowWarningBanner.Value)
                return;

            if (_settings.Tracking.Strategy.Mode == StrategyMode.NoCorruption)
                return;

            if (_gameController.Area.CurrentArea?.IsPeaceful != false)
                return;

            var count = _inventory.VaalOrbCount;
            var minThreshold = _settings.VaalOrbAlert.MinVaalOrbs.Value;
            if (count >= minThreshold)
                return;

            var windowRect = _gameController.Window.GetWindowRectangle();
            var centerX = windowRect.Width / 2f;

            ImGui.SetNextWindowPos(new Vector2(centerX, 68f), ImGuiCond.Always, new Vector2(0.5f, 0f));

            var isZero = count == 0;
            var bgCol = new Vector4(0.08f, 0.09f, 0.12f, 0.92f);
            var borderCol = isZero ? new Vector4(0.95f, 0.25f, 0.25f, 0.95f) : new Vector4(0.95f, 0.60f, 0.10f, 0.95f);
            var textCol = isZero ? new Vector4(1.0f, 0.35f, 0.30f, 1.0f) : new Vector4(1.0f, 0.70f, 0.20f, 1.0f);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 5f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.5f);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, bgCol);
            ImGui.PushStyleColor(ImGuiCol.Border, borderCol);

            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                          ImGuiWindowFlags.AlwaysAutoResize |
                                          ImGuiWindowFlags.NoSavedSettings |
                                          ImGuiWindowFlags.NoFocusOnAppearing |
                                          ImGuiWindowFlags.NoNav |
                                          ImGuiWindowFlags.NoInputs;

            if (ImGui.Begin("##bechVaalOrbWarningBanner", flags))
            {
                if (_icons?.For("Vaal") is { } vaalIcon)
                {
                    ImGui.Image(vaalIcon, new Vector2(18f, 18f));
                    ImGui.SameLine(0f, 6f);
                }

                var text = isZero
                    ? "NO VAAL ORBS IN INVENTORY - Monoliths cannot be corrupted!"
                    : $"LOW VAAL ORBS: {count} left (threshold: {minThreshold})";
                ImGui.TextColored(textCol, text);
            }

            ImGui.End();

            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(3);
        }

        private void DrawEssenceIndicators()
        {
            if (!_settings.Indicators.EnableAllIndicators.Value)
                return;

            // The tracker is only written on the main thread (Tick and entity events), the same one
            // that calls Render, so we iterate it directly instead of copying a snapshot every frame.
            var showDebug = _settings.Debug.ShowDebugInfo.Value;

            foreach (var data in _entityTracker.TrackedEntities)
            {
                var rect = GetDrawRect(data);
                if (rect is not { } drawRect)
                    continue;

                DrawEssenceIndicator(data, drawRect);

                // Draw debug window for this specific essence if enabled
                if (showDebug)
                    DrawEssenceDebugWindow(data, drawRect);
            }
        }

        /// <summary>
        /// The corruption value analysis for an essence, recomputed only when its contents, the
        /// prices, the strategy or the detected map setup changed.
        /// </summary>
        private CorruptionAssessment? GetAssessment(EssenceEntityData data)
        {
            var preferences = _settings.Tracking;
            var key = (data.AnalysisVersion, _prices.Version, preferences.Version, _setup.Version);
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
            // Drawn every frame: read the two children by index rather than building the child list.
            // Child 0 is the card background, Child 1 is the text container.
            // Any further children (index >= 2) are interaction buttons (such as the Remnant of
            // Corruption button below the card) which must not expand the card boundary.
            var count = (int)Math.Min(root.ChildCount, 2);
            if (count == 0)
                return root.GetClientRectCache;

            var found = false;
            float left = 0, top = 0, right = 0, bottom = 0;

            for (var i = 0; i < count; i++)
            {
                var child = root.GetChildAtIndex(i);
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
            if (label == null)
                return null;

            // The label can be up to a label scan old and point at memory the game has reused; Render
            // has no other guard, so an unreadable label just isn't drawn this frame.
            RectangleF rect;
            try
            {
                if (!label.IsVisible || label.Label is not { } root)
                    return null;

                rect = GetContentRect(root);
            }
            catch
            {
                return null;
            }

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
        private void DrawEssenceIndicator(EssenceEntityData data, RectangleF drawRect)
        {
            var state = data.State;

            IIndicatorSettings? indicator = state switch
            {
                EssenceState.ShouldCorrupt => _settings.Indicators.CorruptMe,
                EssenceState.ShouldKill => _settings.Indicators.KillReady,
                _ => null,
            };

            if (indicator is null || !indicator.ShowIndicator.Value)
                return;

            var isCorruptTarget = state == EssenceState.ShouldCorrupt;
            var corruptMe = _settings.Indicators.CorruptMe;
            var highlightButton = isCorruptTarget && corruptMe.HighlightVaalButton.Value;
            var showValue = _settings.Indicators.ShowValueRange.Value;

            if (!indicator.DrawBorder.Value && !indicator.DrawText.Value && !highlightButton && !showValue)
                return;

            var hasNoVaalOrbs = isCorruptTarget &&
                                _inventory != null &&
                                _settings.VaalOrbAlert.EnableAlert.Value &&
                                _settings.VaalOrbAlert.WarnOnLabel.Value &&
                                _settings.Tracking.Strategy.Mode != StrategyMode.NoCorruption &&
                                _inventory.VaalOrbCount == 0;

            // 1. Highlight the monolith card if enabled
            if (indicator.DrawBorder.Value)
                DrawStatusBox(drawRect, indicator);

            // 2. Highlight the Vaal button at the bottom of the card if enabled
            // If the user has 0 Vaal Orbs, the in-game button does not appear on the card!
            if (highlightButton && !hasNoVaalOrbs)
            {
                var buttonRect = GetVaalButtonRect(data.Label?.Label, drawRect);
                DrawVaalButtonHighlight(buttonRect, corruptMe);
            }

            // 3. Draw above-label status indicator (Vaal Icon or Text)
            var drewStatus = false;
            if (indicator.DrawText.Value)
            {
                if (hasNoVaalOrbs)
                {
                    DrawStatusText(drawRect, indicator, isCorruptTarget, "NO VAAL ORBS", Color.Red);
                    drewStatus = true;
                }
                else if (isCorruptTarget && corruptMe.ShowVaalIcon.Value && _icons?.For("Vaal") is { } vaalIcon)
                {
                    DrawStatusVaalIcon(drawRect, vaalIcon, corruptMe.BorderColor.Value);
                    drewStatus = true;
                }
                else
                {
                    DrawStatusText(drawRect, indicator, isCorruptTarget);
                    drewStatus = true;
                }
            }

            // 4. Expected value gain line above status indicator
            if (showValue && isCorruptTarget && GetAssessment(data) is { } assessment && data.ValueText != null)
                DrawValueText(drawRect, data.ValueText, assessment.ExpectedNetGain >= 0 ? Color.LimeGreen : Color.Orange, drewStatus, !hasNoVaalOrbs && isCorruptTarget && corruptMe.ShowVaalIcon.Value);
        }

        private static RectangleF GetVaalButtonRect(Element? root, RectangleF cardRect)
        {
            if (root != null)
            {
                try
                {
                    for (var i = 2; i < root.ChildCount; i++)
                    {
                        var child = root.GetChildAtIndex(i);
                        if (child == null || !child.IsVisible)
                            continue;

                        var r = child.GetClientRectCache;
                        if (r.Width >= 12 && r.Height >= 12 && r.Width <= 70 && r.Height <= 70)
                            return r;

                        if (child.ChildCount > 0)
                        {
                            for (var j = 0; j < child.ChildCount; j++)
                            {
                                var subChild = child.GetChildAtIndex(j);
                                if (subChild == null || !subChild.IsVisible)
                                    continue;

                                var sr = subChild.GetClientRectCache;
                                if (sr.Width >= 12 && sr.Height >= 12 && sr.Width <= 70 && sr.Height <= 70)
                                    return sr;
                            }
                        }
                    }
                }
                catch
                {
                    // Fall back to estimated position below
                }
            }

            // Fallback: estimate button directly at bottom center of the monolith card
            const float btnSize = 28f;
            var centerX = cardRect.Center.X;
            var top = cardRect.Bottom;
            return new RectangleF(centerX - btnSize / 2f, top, btnSize, btnSize);
        }

        private static void DrawVaalButtonHighlight(RectangleF buttonRect, CorruptMeSettings corruptMe)
        {
            var drawList = ImGui.GetBackgroundDrawList();
            var min = new Vector2(buttonRect.Left, buttonRect.Top);
            var max = new Vector2(buttonRect.Right, buttonRect.Bottom);
            var borderColor = ToImguiVec4(corruptMe.BorderColor.Value);

            if (corruptMe.BackgroundFill.Value)
            {
                var baseColor = corruptMe.BorderColor.Value;
                var fillColor = ToImguiVec4(new SharpDX.Color(
                    baseColor.R,
                    baseColor.G,
                    baseColor.B,
                    (byte)(corruptMe.BackgroundOpacity.Value * 255)));
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor), 4f);
            }

            drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), 4f, ImDrawFlags.None, 2.5f);
        }

        private static void DrawStatusVaalIcon(RectangleF rect, nint vaalIcon, Color borderColor)
        {
            var drawList = ImGui.GetBackgroundDrawList();
            var iconSize = new Vector2(24f, 24f);
            var center = new Vector2(rect.Center.X, rect.Top - 22f);

            var bgMin = new Vector2(center.X - iconSize.X / 2f - 4f, center.Y - iconSize.Y / 2f - 3f);
            var bgMax = new Vector2(center.X + iconSize.X / 2f + 4f, center.Y + iconSize.Y / 2f + 3f);

            var bgColor = ToImguiVec4(new SharpDX.Color(0, 0, 0, 200));
            var borderVec = ToImguiVec4(borderColor);

            drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(bgColor), 4f);
            drawList.AddRect(bgMin, bgMax, ImGui.GetColorU32(borderVec), 4f, ImDrawFlags.None, 1.2f);

            var iconMin = new Vector2(center.X - iconSize.X / 2f, center.Y - iconSize.Y / 2f);
            var iconMax = new Vector2(center.X + iconSize.X / 2f, center.Y + iconSize.Y / 2f);
            drawList.AddImage(vaalIcon, iconMin, iconMax);
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
        private void DrawStatusText(RectangleF rect, IIndicatorSettings indicator, bool isCorruptTarget, string? overrideText = null, Color? overrideColor = null)
        {
            var text = overrideText ?? (isCorruptTarget ? "CORRUPT" : "KILL");
            var textColor = overrideColor ?? indicator.TextColor.Value;

            // Position text 25 pixels above the essence label
            var textPos = new Vector2(rect.Center.X, rect.Top - 25);
            DrawCenteredTextWithBackground(textPos, text, textColor);
        }

        /// <summary>Draws the value range line above the status indicator (text or icon).</summary>
        private static void DrawValueText(RectangleF rect, string text, Color color, bool belowStatusIndicator, bool isVaalIcon = false)
        {
            var spacing = isVaalIcon ? 34f : ImGui.GetTextLineHeight() + 12f;
            var y = rect.Top - 25f - (belowStatusIndicator ? spacing : 0f);
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
        private void DrawEssenceDebugWindow(EssenceEntityData data, RectangleF cardRect)
        {
            var assessment = GetAssessment(data);

            var key = System.HashCode.Combine(data.AnalysisVersion, data.State, data.MonsterId, data.Corruption != null,
                data.AssessmentKey, data.StateReason, (int)(data.GridPos?.X ?? 0), (int)(data.GridPos?.Y ?? 0));
            if (!_debugCache.TryGetValue(data.Id, out var cached) || cached.Key != key)
            {
                var lines = BuildDebugContent(data, assessment);
                var width = 0f;
                var iconSize = ImGui.GetTextLineHeight();
                if (iconSize <= 0) iconSize = 14f;

                foreach (var line in lines)
                {
                    var lineWidth = 0f;
                    if (line.Prefix is { } p)
                        lineWidth += ImGui.CalcTextSize(p.Text).X;

                    if (line.Icon != null)
                        lineWidth += iconSize + 4f;

                    foreach (var (text, _) in line.Segments)
                        lineWidth += ImGui.CalcTextSize(text).X;

                    if (lineWidth > width)
                        width = lineWidth;
                }

                cached = (key, lines, width, $"###EssenceDebug_{data.Id}");
                _debugCache[data.Id] = cached;
            }

            var debugLines = cached.Lines;

            // Position flush to right edge of monolith card
            var debugWindowPos = new Vector2(cardRect.Right + 5f, cardRect.Top);

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
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 6f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f, 2f));

            // Set window dimensions:
            // Width clamped to avoid screen stretching while fitting content.
            // Height matches monolith card Y space, expanding only if needed to prevent clipping.
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();
            if (lineHeight <= 0) lineHeight = 16f;
            var neededHeight = 12f + (debugLines.Count * lineHeight);
            var windowHeight = Math.Max(cardRect.Height, neededHeight);
            var windowWidth = Math.Max(260f, cached.Width + 24f);
            var windowSize = new Vector2(windowWidth, windowHeight);

            // Set position and size BEFORE Begin()
            ImGui.SetNextWindowPos(debugWindowPos);
            ImGui.SetNextWindowSize(windowSize);

            // Begin unique window for THIS essence.
            // ImGui requires End() for every Begin() regardless of the return value - only the
            // body is conditional. Skipping it corrupts the window stack.
            if (ImGui.Begin(cached.WindowName, windowFlags))
            {
                var iconSize = ImGui.GetTextLineHeight();
                if (iconSize <= 0) iconSize = 14f;

                // Draw each line of debug information
                foreach (var line in debugLines)
                {
                    var hasPrefix = false;

                    if (line.Prefix is { } p)
                    {
                        ImGui.TextColored(ToImguiVec4(p.Color), p.Text);
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

                        ImGui.TextColored(ToImguiVec4(color), text);
                    }

                    if (!hasPrefix && line.Segments.Count == 0)
                        ImGui.Text("");
                }
            }

            ImGui.End();

            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        /// <summary>
        /// Renders the sleek modern map statistics window using a clean 3-column table HUD widget.
        /// </summary>
        private void DrawMapStatsWindow()
        {
            if (!_settings.MapStats.ShowMapStats.Value ||
                (_gameController.Area.CurrentArea?.IsPeaceful != false && !_settings.MapStats.ShowInTownHideout.Value))
                return;

            var mapStats = _entityTracker.MapStats;

            // Apply modern HUD styling: smooth rounded corners, compact padding, and dark palette
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f, 2f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 4f));

            ImGui.PushStyleColor(ImGuiCol.TitleBg, ToImguiVec4(_settings.MapStats.TitleBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ToImguiVec4(_settings.MapStats.TitleBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ToImguiVec4(_settings.MapStats.ContentBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.Border, ToImguiVec4(_settings.MapStats.BorderColor.Value));
            ImGui.PushStyleColor(ImGuiCol.Text, ToImguiVec4(_settings.MapStats.TitleColor.Value));
            ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new Vector4(0.12f, 0.14f, 0.18f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.TableRowBg, new Vector4(0.00f, 0.00f, 0.00f, 0.00f));
            ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, new Vector4(1.0f, 1.0f, 1.0f, 0.035f));
            ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.35f, 0.38f, 0.45f, 0.40f));

            const ImGuiWindowFlags windowFlags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
            if (ImGui.Begin("Essence Tracker###EssenceMapStats", windowFlags))
            {
                // Clamp window position to screen bounds so it cannot be dragged off-screen / hidden
                var windowRect = _gameController.Window.GetWindowRectangleTimeCache;
                if (windowRect.Width > 0 && windowRect.Height > 0)
                {
                    var pos = ImGui.GetWindowPos();
                    var size = ImGui.GetWindowSize();
                    var maxX = Math.Max(0f, windowRect.Width - size.X);
                    var maxY = Math.Max(0f, windowRect.Height - size.Y);
                    var clampedX = Math.Clamp(pos.X, 0f, maxX);
                    var clampedY = Math.Clamp(pos.Y, 0f, maxY);

                    if (Math.Abs(clampedX - pos.X) > 1f || Math.Abs(clampedY - pos.Y) > 1f)
                    {
                        ImGui.SetWindowPos(new Vector2(clampedX, clampedY));
                    }
                }

                var contentColor = ToImguiVec4(_settings.MapStats.TextColor.Value);
                var mutedColor = new Vector4(0.60f, 0.63f, 0.70f, 1.0f);
                var goldColor = new Vector4(0.90f, 0.75f, 0.45f, 1.0f);
                var orangeColor = new Vector4(1.0f, 0.65f, 0.0f, 1.0f);
                var redColor = new Vector4(1.0f, 0.35f, 0.30f, 1.0f);
                var greenColor = new Vector4(0.40f, 0.90f, 0.45f, 1.0f);

                // Strategy Header
                ImGui.TextColored(mutedColor, "Strategy:");
                ImGui.SameLine();
                ImGui.TextColored(goldColor, _settings.Tracking.Strategy.DisplayName);

                if (SetupStatus.Describe(_settings.Tracking.Strategy, _setup) is { } warning)
                {
                    var warnColor = warning.Level == SetupStatus.Level.Problem ? redColor : orangeColor;
                    ImGui.TextColored(warnColor, $"[!] {warning.Title}");
                }

                // Vaal Orb Alert
                if (_inventory != null && _settings.VaalOrbAlert.EnableAlert.Value && _settings.Tracking.Strategy.Mode != StrategyMode.NoCorruption)
                {
                    var vaalCount = _inventory.VaalOrbCount;
                    var minThreshold = _settings.VaalOrbAlert.MinVaalOrbs.Value;
                    if (vaalCount < minThreshold)
                    {
                        var alertColor = vaalCount == 0 ? redColor : orangeColor;
                        var alertMsg = vaalCount == 0
                            ? "No Vaal Orbs in inventory!"
                            : $"Low Vaal Orbs: {vaalCount} left (min {minThreshold})";
                        ImGui.TextColored(alertColor, alertMsg);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(vaalCount == 0
                                ? "Your strategy requires Vaal Orbs to corrupt essences, but you have 0 in inventory.\nThe corrupt button on monoliths will not appear!"
                                : $"You have {vaalCount} Vaal Orbs in your inventory, which is below your alert threshold of {minThreshold}.");
                        }
                    }
                }

                ImGui.Spacing();

                // 3-Column Table: Metric | Map | Session
                const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
                if (ImGui.BeginTable("##bechStatsTable", 3, tableFlags))
                {
                    ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthStretch, 1.3f);
                    ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch, 0.8f);
                    ImGui.TableSetupColumn("Session", ImGuiTableColumnFlags.WidthStretch, 0.9f);
                    ImGui.TableHeadersRow();

                    // Row 1: Killed
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(mutedColor, "Killed");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(contentColor, mapStats.TotalKilled.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextColored(contentColor, _corruptionLog.TotalKilled.ToString());

                    // Row 2: Corrupted
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(mutedColor, "Corrupted");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(contentColor, mapStats.TotalCorrupted.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextColored(contentColor, _corruptionLog.Total.ToString());

                    // Row 3: Missed
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(mutedColor, "Missed");
                    ImGui.TableNextColumn();
                    var mapMissedColor = mapStats.TotalMissed > 0 ? orangeColor : mutedColor;
                    ImGui.TextColored(mapMissedColor, mapStats.TotalMissed.ToString());
                    ImGui.TableNextColumn();
                    var sessMissedColor = _corruptionLog.TotalMissed > 0 ? orangeColor : mutedColor;
                    ImGui.TextColored(sessMissedColor, _corruptionLog.TotalMissed.ToString());

                    // Row 4: Mistakes
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(mutedColor, "Mistakes");
                    ImGui.TableNextColumn();
                    var mapMistakesColor = mapStats.TotalMistakes > 0 ? redColor : mutedColor;
                    ImGui.TextColored(mapMistakesColor, mapStats.TotalMistakes.ToString());
                    ImGui.TableNextColumn();
                    var sessMistakesColor = _corruptionLog.TotalMistakes > 0 ? redColor : mutedColor;
                    ImGui.TextColored(sessMistakesColor, _corruptionLog.TotalMistakes.ToString());
                    if (_corruptionLog.TotalMistakes > 0 && ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"{_corruptionLog.MistakenCorruptions} wrong corruptions, {_corruptionLog.TotalMissed} missed opportunities");
                    }

                    ImGui.EndTable();
                }

                // Session Outcomes & Profit footer
                if (_corruptionLog.Total > 0)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    if (_corruptionLog.RealizedGainCount > 0)
                    {
                        var gain = _corruptionLog.RealizedGainSum;
                        var profitColor = gain >= 0 ? greenColor : redColor;
                        ImGui.TextColored(mutedColor, "Realized Net:");
                        ImGui.SameLine();
                        ImGui.TextColored(profitColor, Format.SignedChaos(gain));
                        ImGui.SameLine();
                        ImGui.TextColored(mutedColor, $"({_corruptionLog.RealizedGainCount} logged)");
                    }

                    var parts = new List<string>();
                    foreach (var outcome in System.Enum.GetValues<CorruptionOutcome>())
                    {
                        var count = _corruptionLog.CountOf(outcome);
                        if (count > 0)
                            parts.Add($"{OutcomeName(outcome)}: {count}");
                    }
                    if (_corruptionLog.Unclassified > 0)
                        parts.Add($"unclassified: {_corruptionLog.Unclassified}");

                    if (parts.Count > 0)
                    {
                        ImGui.TextColored(new Vector4(0.55f, 0.58f, 0.65f, 1.0f), string.Join("  ·  ", parts));
                    }
                }
            }

            ImGui.End();

            ImGui.PopStyleColor(9);
            ImGui.PopStyleVar(4);
        }

        #endregion

        #region Helpers

        /// <summary>One line of text and optional leading icon in the debug overlay.</summary>
        internal sealed record DebugLine(
            (string Text, Color Color)? Prefix,
            nint? Icon,
            List<(string Text, Color Color)> Segments)
        {
            public DebugLine(List<(string Text, Color Color)> segments) : this(null, null, segments) { }
            public DebugLine(nint? icon, List<(string Text, Color Color)> segments) : this(null, icon, segments) { }
        }

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
        private List<DebugLine> BuildDebugContent(EssenceEntityData data, CorruptionAssessment? assessment)
        {
            var lines = new List<DebugLine>();

            AddDebugHeader(lines, data);
            AddDebugEntityInfo(lines, data);
            AddDebugAnalysis(lines, data);
            AddDebugAssessment(lines, data, assessment);

            return lines;
        }

        /// <summary>
        /// Adds debug header showing essence ID and status.
        /// </summary>
        private static void AddDebugHeader(List<DebugLine> lines, EssenceEntityData data)
        {
            var (stateString, stateColor) = data.State switch
            {
                EssenceState.ShouldCorrupt => ("CORRUPT-ME", Color.Red),
                EssenceState.ShouldKill => ("KILL-ME", Color.Green),
                _ => ("UNKNOWN", Color.Gray)
            };

            var header = new List<(string, Color)>
            {
                ($"Essence #{data.EntityId} · ", Color.White),
                (stateString, stateColor),
                ($" ({data.StateReason})", Color.Gray)
            };

            if (data.Corruption != null)
                header.Add((" [Corrupted]", Color.Yellow));

            lines.Add(new DebugLine(header));
        }

        /// <summary>
        /// Adds debug info showing the entity identity and the trapped monster linked to it - the
        /// monster whose death counts as the kill - so both can be checked against the game.
        /// </summary>
        private static void AddDebugEntityInfo(List<DebugLine> lines, EssenceEntityData data)
        {
            var info = new List<(string, Color)>
            {
                ("Id: ", Color.White),
                ($"{data.Id} · ", Color.LightSkyBlue)
            };

            if (data.MonsterId is { } monsterId)
            {
                var monsterName = SafeRenderName(data);
                var rarity = EssenceEntityTracker.RarityName(data.MonsterRarityRank);
                info.Add(($"Monster: {monsterName} ({rarity})", Color.LightGreen));
            }
            else
            {
                info.Add(("Monster: not linked", Color.OrangeRed));
            }

            if (data.GridPos is { } pos)
                info.Add(($" · ({pos.X:F0}, {pos.Y:F0})", Color.LightYellow));

            lines.Add(new DebugLine(info));
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
        private void AddDebugAnalysis(List<DebugLine> lines, EssenceEntityData data)
        {
            if (!data.Analysis.IsValid)
            {
                lines.Add(new DebugLine([("ANALYSIS: INVALID", Color.Red)]));
                return;
            }

            if (data.Corruption?.Before is { IsValid: true } before)
            {
                var outcome = OutcomeClassifier.Classify(before.Essences, data.Analysis.Essences, out var ambiguous,
                    CorruptionModel.ApplicableOutcomes(_setup.Resolve()));
                lines.Add(new DebugLine([("ANALYSIS (Before -> After): ", Color.Cyan),
                    (outcome is { } o ? OutcomeName(o) + (ambiguous ? "?" : "") : "unclassified", Color.Yellow)]));
                AddEssenceLines(lines, before, ("  before ", Color.Gray));
                AddEssenceLines(lines, data.Analysis, ("  after  ", Color.Gray));
                return;
            }

            if (data.Analysis.IsCorrupted)
                lines.Add(new DebugLine([("CORRUPTED: ", Color.Cyan), ($"{data.Analysis.EssenceCount} essences", Color.White)]));

            AddEssenceLines(lines, data.Analysis, null);
        }

        private void AddEssenceLines(List<DebugLine> lines, EssenceAnalysis analysis, (string Text, Color Color)? prefix)
        {
            foreach (var essence in analysis.Essences)
            {
                var value = _values.Get(essence);
                var icon = _icons?.For(essence.TypeName);
                lines.Add(new DebugLine(prefix, icon,
                [
                    (essence.Name, essence.IsMeds ? Color.Violet : Color.White),
                    ("  " + Format.Value(value), value.Source == ValueSource.Market ? Color.LightGreen : Color.Gray)
                ]));
            }

            foreach (var line in analysis.UnrecognizedEssenceLines)
                lines.Add(new DebugLine(prefix, null, [(line, Color.Orange), ("  (not in catalogue)", Color.Gray)]));
        }

        /// <summary>Adds the corruption value analysis: per-outcome ranges, bounds and verdict.</summary>
        private void AddDebugAssessment(List<DebugLine> lines, EssenceEntityData data, CorruptionAssessment? assessment)
        {
            if (data.Analysis.IsCorrupted || !data.Analysis.IsValid)
                return;

            var vaalIcon = _icons?.For("Vaal");

            if (assessment == null)
            {
                lines.Add(new DebugLine(vaalIcon, [
                    ("Corrupt EV: ", Color.Cyan),
                    (data.AssessmentUnavailableReason ?? "unavailable", Color.Gray)
                ]));
                return;
            }

            lines.Add(new DebugLine(vaalIcon, [
                ("Corrupt EV ", Color.Cyan),
                ($"(Monster: {Format.Chaos(assessment.CurrentValue)} · Vaal: {Format.Chaos(assessment.VaalOrbCost)})", Color.Gray)
            ]));

            foreach (var o in assessment.Outcomes)
            {
                var range = Math.Abs(o.MaxGain - o.MinGain) < 1e-9
                    ? Format.SignedChaos(o.MinGain)
                    : $"{Format.SignedChaos(o.MinGain)}..{Format.SignedChaos(o.MaxGain)}";
                lines.Add(new DebugLine([($"  {OutcomeName(o.Outcome)}: ", Color.White), (range, Color.LightGray)]));
            }

            var avgColor = assessment.ExpectedNetGain >= 0 ? Color.LimeGreen : Color.OrangeRed;
            var avgText = $"(avg {Format.SignedChaos(assessment.ExpectedNetGain)})";

            var netSegments = new List<(string, Color)>
            {
                ("  net: ", Color.White),
                ($"{Format.SignedChaos(assessment.WorstNetGain)}..{Format.SignedChaos(assessment.BestNetGain)} ", Color.LightGray),
                (avgText, avgColor)
            };

            if (assessment.Verdict == CorruptionVerdict.AlwaysGains)
                netSegments.Add((" [guaranteed gain]", Color.LimeGreen));
            else if (assessment.Verdict == CorruptionVerdict.AlwaysLoses)
                netSegments.Add((" [guaranteed loss]", Color.OrangeRed));

            lines.Add(new DebugLine(netSegments));

            if (assessment.UnpricedItems.Count > 0)
            {
                var unpText = assessment.UnpricedItems.Count <= 2
                    ? string.Join(", ", assessment.UnpricedItems)
                    : $"{assessment.UnpricedItems.Count} items";
                lines.Add(new DebugLine([("  counted as 0c: ", Color.Gray), (unpText, Color.Gray)]));
            }
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
