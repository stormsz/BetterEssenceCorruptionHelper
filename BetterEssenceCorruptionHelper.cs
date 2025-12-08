using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using System.Text;
using Vector2 = System.Numerics.Vector2;
using Vector3 = SharpDX.Vector3;

namespace BetterEssenceCorruptionHelper
{
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        private readonly Dictionary<long, EssenceEntityData> _trackedEntities = [];
        private readonly List<EssenceEntityData> _completedEssences = [];
        private int _entityIdCounter = 0;

        private readonly MapStatistics _mapStats = new();

        private string _cachedSessionStatsText = "";
        private DateTime _lastStatsUpdate = DateTime.MinValue;
        private float _cachedWindowWidth = 0f;
        private int _screenWidth = 0;
        private int _screenHeight = 0;

        private bool AnyDebugEnabled => Settings.Debug.ShowDebugInfo.Value;

        public override bool Initialise()
        {
            Name = "Better Essence Corruption Helper";
            DebugWindow.LogMsg($"{Name} initialized", 2, Color.Green);
            return base.Initialise();
        }

        public override void AreaChange(AreaInstance area)
        {
            _trackedEntities.Clear();
            _completedEssences.Clear();
            _entityIdCounter = 0;
            _cachedWindowWidth = 0f;
            _mapStats.Reset();
            UpdateSessionStatsCache();
        }

        public override Job? Tick()
        {
            if (!Settings.Enable.Value || !GameController.InGame || GameController.Area.CurrentArea.IsPeaceful)
                return null;

            if (IsAnyGameUIVisible())
                return null;

            ProcessEssences();

            // Update cached stats text every second
            if (DateTime.Now - _lastStatsUpdate >= TimeSpan.FromSeconds(1))
            {
                UpdateSessionStatsCache();
            }

            return null;
        }

        private void ProcessEssences()
        {
            var currentMonoliths = new HashSet<long>();

            foreach (var entity in GameController.Entities)
            {
                if (!IsValidMonolith(entity)) continue;

                currentMonoliths.Add(entity.Address);

                if (!_trackedEntities.TryGetValue(entity.Address, out var data))
                {
                    data = new EssenceEntityData
                    {
                        EntityId = ++_entityIdCounter,
                        Address = entity.Address,
                        FirstSeenAddress = entity.Address,
                        Entity = entity
                    };
                    _trackedEntities[entity.Address] = data;
                }
                else
                {
                    data.Entity = entity;
                }

                UpdateEntityData(entity, data);

                // Update position
                var pos = entity.PosNum;
                data.LastKnownPosition = new Vector3(pos.X, pos.Y, pos.Z);
            }

            CleanupOldEssences(currentMonoliths);
        }

        private void CleanupOldEssences(HashSet<long> currentMonoliths)
        {
            var removedEntities = new List<long>();

            foreach (var (address, data) in _trackedEntities)
            {
                if (currentMonoliths.Contains(address))
                    continue;

                // Entity was removed (killed or despawned)
                data.WasKilled = true;
                _mapStats.IncrementKilled();

                // Check for missed corruptions
                if (data.State == EssenceState.ShouldCorrupt &&
                    !data.Analysis.IsCorrupted &&
                    !data.WasCorruptedByPlayer)
                {
                    data.MissedCorruption = true;
                    _mapStats.IncrementMissed();

                    if (AnyDebugEnabled)
                    {
                        DebugWindow.LogMsg(
                            $"MISSED CORRUPTION: Essence {data.EntityId} (0x{address:X}) should have been corrupted but was killed",
                            1, Color.Orange);
                    }
                }

                _completedEssences.Add(data);
                removedEntities.Add(address);
            }

            foreach (var address in removedEntities)
                _trackedEntities.Remove(address);
        }

        private bool IsValidMonolith(Entity entity)
        {
            if (entity == null || entity.Address == 0 || !entity.IsValid ||
                !entity.IsTargetable || entity.Address == GameController.Player?.Address)
                return false;

            if (!entity.HasComponent<Render>() || !entity.HasComponent<Monolith>())
                return false;

            try
            {
                var pos = entity.PosNum;
                return !(pos.X == 0 && pos.Y == 0 && pos.Z == 0);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateEntityData(Entity entity, EssenceEntityData data)
        {
            var label = FindLabelForEntity(entity);
            if (label == null)
            {
                data.Label = null;
                return;
            }

            data.Label = label;
            var newAnalysis = EssenceLabelAnalyzer.Analyze(label);
            var oldState = data.State;
            var wasCorruptedBefore = data.Analysis.IsCorrupted;

            data.Analysis = newAnalysis;
            var newState = DetermineEssenceState(newAnalysis);

            // Check if this essence just got corrupted
            if (!wasCorruptedBefore && newAnalysis.IsCorrupted)
            {
                data.WasCorruptedByPlayer = true;
                data.PreviousAnalysis = data.Analysis;
                data.PreviousState = oldState;

                if (AnyDebugEnabled)
                    DebugWindow.LogMsg($"Essence {data.EntityId} (0x{entity.Address:X}) corrupted. Old state: {oldState}, New state: {newState}", 1, Color.Yellow);

                // Track outcomes and update map statistics
                if (oldState == EssenceState.ShouldCorrupt)
                {
                    _mapStats.IncrementCorrupted();
                    if (AnyDebugEnabled)
                        DebugWindow.LogMsg($"SUCCESSFUL CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X})", 1, Color.LightGreen);
                }
                else if (oldState == EssenceState.ShouldKill)
                {
                    data.MistakenCorruption = true;
                    _mapStats.IncrementMistakes();
                    if (AnyDebugEnabled)
                        DebugWindow.LogMsg($"MISTAKEN CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X}) should have been killed!", 1, Color.OrangeRed);
                }
            }

            data.State = newState;

            // Update PreviousState for non-corruption state changes
            if ((wasCorruptedBefore || !newAnalysis.IsCorrupted) && newState != oldState)
            {
                data.PreviousState = oldState;
                if (AnyDebugEnabled)
                    DebugWindow.LogMsg($"Essence {data.EntityId}: {oldState} -> {newState}", 1);
            }
        }

        private static EssenceState DetermineEssenceState(EssenceAnalysis analysis)
        {
            if (analysis.IsCorrupted)
                return EssenceState.ShouldKill;

            return analysis.HasValuablePattern ? EssenceState.ShouldCorrupt : EssenceState.ShouldKill;
        }

        private LabelOnGround? FindLabelForEntity(Entity entity)
        {
            return GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                .FirstOrDefault(x => x.ItemOnGround?.Address == entity.Address);
        }

        public override void Render()
        {
            if (!Settings.Enable.Value || !GameController.InGame || IsAnyGameUIVisible())
                return;

            foreach (var data in _trackedEntities.Values)
            {
                if (data.Label == null && data.Entity != null)
                    data.Label = FindLabelForEntity(data.Entity);
            }

            // Draw visuals for essences
            if (Settings.Indicators.EnableAllIndicators.Value)
            {
                foreach (var data in _trackedEntities.Values)
                {
                    bool isCorruptTarget = data.State == EssenceState.ShouldCorrupt;

                    if (isCorruptTarget && Settings.Indicators.CorruptMe.ShowCorruptMe.Value)
                    {
                        if (Settings.Indicators.CorruptMe.DrawBorder.Value)
                            DrawBorder(data, true);
                        if (Settings.Indicators.CorruptMe.DrawText.Value)
                            DrawText(data, true);
                    }
                    else if (data.State == EssenceState.ShouldKill && Settings.Indicators.KillReady.ShowKillReady.Value)
                    {
                        if (Settings.Indicators.KillReady.DrawBorder.Value)
                            DrawBorder(data, false);
                        if (Settings.Indicators.KillReady.DrawText.Value)
                            DrawText(data, false);
                    }

                    if (Settings.Debug.ShowDebugInfo.Value && data.Label?.Label != null)
                        DrawEntityDebugWindow(data);
                }
            }

            if (Settings.MapStats.ShowMapStats.Value && (!GameController.Area.CurrentArea.IsPeaceful || Settings.MapStats.ShowInTownHideout.Value))
                DrawMapStatsWindow();
        }

        private void DrawMapStatsWindow()
        {
            if (string.IsNullOrEmpty(_cachedSessionStatsText))
                return;

            // Cache screen dimensions on first draw or if not set
            if (_screenWidth == 0 || _screenHeight == 0)
            {
                var rect = GameController.Window.GetWindowRectangle();
                _screenWidth = (int)rect.Width;
                _screenHeight = (int)rect.Height;
            }

            // Use cached window width
            if (_cachedWindowWidth == 0f)
                _cachedWindowWidth = CalculateMapStatsWindowWidth(_cachedSessionStatsText);

            // Calculate window height for clamping
            const int padding = 8;
            const int titleFontSize = 14;
            const int contentFontSize = 12;
            var titleSize = Graphics.MeasureText("Essence Map Stats", titleFontSize);
            var contentSize = Graphics.MeasureText(_cachedSessionStatsText, contentFontSize);
            var titleHeight = titleSize.Y + padding;
            var contentHeight = contentSize.Y + padding * 2;
            var totalHeight = titleHeight + contentHeight;

            // Calculate position based on anchor
            Vector2 position = Settings.MapStats.WindowAnchor.Value == "Top Left"
                ? new Vector2(Settings.MapStats.OffsetX.Value, Settings.MapStats.OffsetY.Value)
                : new Vector2(_screenWidth - _cachedWindowWidth - Settings.MapStats.OffsetX.Value, Settings.MapStats.OffsetY.Value);

            // Clamp to screen bounds
            position.X = Math.Max(0, Math.Min(position.X, _screenWidth - _cachedWindowWidth));
            position.Y = Math.Max(0, Math.Min(position.Y, _screenHeight - totalHeight));

            DrawTextWindow(position, "Essence Map Stats", _cachedSessionStatsText, _cachedWindowWidth,
                Settings.MapStats.TitleBackground.Value,
                Settings.MapStats.ContentBackground.Value,
                Settings.MapStats.TitleColor.Value,
                Settings.MapStats.TextColor.Value,
                Settings.MapStats.BorderColor.Value);
        }

        private void UpdateSessionStatsCache()
        {
            var sb = new StringBuilder();

            sb.AppendLine("Map Totals");
            sb.AppendLine($"  Killed: {_mapStats.TotalKilled}");
            sb.AppendLine($"  Corrupted: {_mapStats.TotalCorrupted}");
            sb.AppendLine($"  Missed: {_mapStats.TotalMissed}");
            sb.AppendLine($"  Mistakes: {_mapStats.TotalMistakes}");

            _cachedSessionStatsText = sb.ToString();
            _lastStatsUpdate = DateTime.Now;
            _cachedWindowWidth = CalculateMapStatsWindowWidth(_cachedSessionStatsText);
        }

        private float CalculateMapStatsWindowWidth(string content)
        {
            const int padding = 8;
            const int titleFontSize = 14;
            const int contentFontSize = 12;

            var titleSize = Graphics.MeasureText("Essence Map Stats", titleFontSize);
            var contentSize = Graphics.MeasureText(content, contentFontSize);

            return Math.Max(titleSize.X + 10, contentSize.X) + padding * 2;
        }

        private void DrawTextWindow(Vector2 position, string title, string content, float width,
            Color titleBg, Color contentBg, Color titleColor, Color textColor, Color borderColor)
        {
            const int padding = 8;
            const int titleFontSize = 14;
            const int contentFontSize = 12;

            var titleSize = Graphics.MeasureText(title, titleFontSize);
            var contentSize = Graphics.MeasureText(content, contentFontSize);

            float titleHeight = titleSize.Y + padding;
            float contentHeight = contentSize.Y + padding * 2;
            float totalHeight = titleHeight + contentHeight;

            var titleRect = new RectangleF(position.X, position.Y, width, titleHeight);
            var contentRect = new RectangleF(position.X, position.Y + titleHeight, width, contentHeight);

            // Draw backgrounds
            Graphics.DrawBox(titleRect, titleBg);
            Graphics.DrawBox(contentRect, contentBg);

            // Draw title
            float availableWidth = width - 5;
            float titleX;

            if (titleSize.X <= availableWidth)
            {
                titleX = position.X + (width - titleSize.X) / 2;
            }
            else
            {
                titleX = position.X + 5;
            }

            Graphics.DrawText(title, new Vector2(titleX, position.Y + padding / 2), titleColor, FontAlign.Left);

            // Draw content
            Graphics.DrawText(content, new Vector2(position.X + padding, position.Y + titleHeight + padding / 2), textColor, FontAlign.Left);

            // Draw border
            Graphics.DrawFrame(new RectangleF(position.X, position.Y, width, totalHeight), borderColor, 1);
        }

        private RectangleF GetBorderRect(RectangleF rect, bool isCorruptTarget)
        {
            float thickness = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderThickness.Value
                : Settings.Indicators.KillReady.BorderThickness.Value;

            float margin = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderMargin.Value
                : Settings.Indicators.KillReady.BorderMargin.Value;

            return new RectangleF(
                rect.X - thickness / 2f + margin,
                rect.Y - thickness / 2f,
                Math.Max(rect.Width + thickness - margin * 2, 10f),
                Math.Max(rect.Height + thickness, 10f)
            );
        }

        private void DrawBorder(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null) return;

            var rect = data.Label.Label.GetClientRectCache;
            var borderRect = GetBorderRect(rect, isCorruptTarget);

            var color = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderColor.Value
                : Settings.Indicators.KillReady.BorderColor.Value;

            var thickness = (int)(isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderThickness.Value
                : Settings.Indicators.KillReady.BorderThickness.Value);

            Graphics.DrawFrame(borderRect, color, thickness);
        }

        private void DrawText(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null) return;

            var rect = data.Label.Label.GetClientRectCache;

            try
            {
                var text = isCorruptTarget ? "CORRUPT" : "KILL";
                var textColor = isCorruptTarget
                    ? Settings.Indicators.CorruptMe.TextColor.Value
                    : Settings.Indicators.KillReady.TextColor.Value;

                var textSize = isCorruptTarget
                    ? Settings.Indicators.CorruptMe.TextSize.Value
                    : Settings.Indicators.KillReady.TextSize.Value;

                using (Graphics.SetTextScale(textSize))
                {
                    var textPos = new Vector2(rect.X + rect.Width / 2, rect.Y - textSize * 25 + 15);
                    var measuredSize = Graphics.MeasureText(text);
                    var bgRect = new RectangleF(
                        textPos.X - measuredSize.X / 2 - 2,
                        textPos.Y - 2,
                        measuredSize.X + 4,
                        measuredSize.Y + 4
                    );

                    Graphics.DrawBox(bgRect, new Color(0, 0, 0, 150));
                    Graphics.DrawText(text, textPos, textColor, FontAlign.Center);
                }
            }
            catch (Exception ex)
            {
                if (AnyDebugEnabled)
                    DebugWindow.LogError($"DrawText failed: {ex.Message}");
            }
        }

        private void DrawEntityDebugWindow(EssenceEntityData data)
        {
            if (data.Label?.Label == null) return;

            var labelRect = data.Label.Label.GetClientRectCache;
            var borderRect = GetBorderRect(labelRect, data.State == EssenceState.ShouldCorrupt);
            var debugLines = BuildDebugContent(data);

            // Calculate required width
            float maxWidth = 0;
            using (Graphics.SetTextScale(1.0f))
            {
                foreach (var segments in debugLines)
                {
                    float lineWidth = segments.Sum(seg => Graphics.MeasureText(seg.text).X);
                    if (lineWidth > maxWidth)
                        maxWidth = lineWidth;
                }
            }

            var debugRect = new RectangleF(
                borderRect.Right,
                borderRect.Top,
                Math.Max(260, maxWidth + 15),
                10 + (debugLines.Count * 16)
            );

            var bgColor = Settings.Debug.DebugBackgroundColor.Value;
            bgColor.A = (byte)(Settings.Debug.DebugBackgroundOpacity.Value * 255);
            Graphics.DrawBox(debugRect, bgColor);
            Graphics.DrawFrame(debugRect, Settings.Debug.DebugBorderColor.Value, 1);

            using (Graphics.SetTextScale(1.0f))
            {
                for (int i = 0; i < debugLines.Count; i++)
                {
                    float x = debugRect.X + 5;
                    foreach (var (text, color) in debugLines[i])
                    {
                        Graphics.DrawText(text, new Vector2(x, debugRect.Y + 5 + (i * 16)), color);
                        x += Graphics.MeasureText(text).X;
                    }
                }
            }
        }

        private static List<List<(string text, Color color)>> BuildDebugContent(EssenceEntityData data)
        {
            var lines = new List<List<(string, Color)>>();

            string stateString = data.State switch
            {
                EssenceState.ShouldCorrupt => "CORRUPT-ME",
                EssenceState.ShouldKill => "KILL-ME",
                _ => "UNKNOWN"
            };

            Color stateColor = data.State == EssenceState.ShouldCorrupt ? Color.Red : Color.Green;

            lines.Add([($"Essence #{data.EntityId}", Color.White)]);
            lines.Add([("Status: ", Color.White), (stateString, stateColor)]);

            if (data.WasCorruptedByPlayer)
                lines.Add([("Player Corrupted: ", Color.White), ("Yes", Color.Yellow)]);

            lines.Add([]);
            lines.Add([("Addr: ", Color.White), ($"0x{data.Address:X12}", Color.LightSkyBlue)]);

            if (data.LastKnownPosition.HasValue)
            {
                var pos = data.LastKnownPosition.Value;
                lines.Add([("Pos: ", Color.White), ($"({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})", Color.LightYellow)]);
            }

            lines.Add([]);

            if (!data.Analysis.IsValid)
            {
                lines.Add([("ANALYSIS: INVALID", Color.Red)]);
                return lines;
            }

            bool showComparison = data.WasCorruptedByPlayer && data.PreviousAnalysis.HasValue && data.PreviousAnalysis.Value.IsValid;

            if (showComparison)
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
            else
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
                lines.Add([("MEDS: ", Color.White), (data.Analysis.HasMeds ? "Yes" : "No", data.Analysis.HasMeds ? Color.Green : Color.Red)]);
                lines.Add([("Valuable: ", Color.White), (data.Analysis.HasValuablePattern ? "Yes" : "No", data.Analysis.HasValuablePattern ? Color.Green : Color.Red)]);
            }

            return lines;
        }

        private static void AddComparisonLine(List<List<(string text, Color color)>> lines, string label, int prev, int curr)
        {
            Color currColor = curr > prev ? Color.Green : curr < prev ? Color.Red : Color.White;
            lines.Add([($"{label}: ", Color.White), ($"{prev}", Color.White), (" -> ", Color.White), ($"{curr}", currColor)]);
        }

        private static void AddComparisonLineBool(List<List<(string text, Color color)>> lines, string label, bool prev, bool curr)
        {
            Color currColor = curr ? Color.Green : Color.Red;
            lines.Add([($"{label}: ", Color.White), (prev ? "Yes" : "No", Color.White), (" -> ", Color.White), (curr ? "Yes" : "No", currColor)]);
        }

        private bool IsAnyGameUIVisible()
        {
            var ui = GameController.IngameState.IngameUi;
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
    }

    /// <summary>
    /// Tracks map-wide statistics for essence encounters
    /// </summary>
    internal class MapStatistics
    {
        public int TotalKilled { get; private set; }
        public int TotalCorrupted { get; private set; }
        public int TotalMissed { get; private set; }
        public int TotalMistakes { get; private set; }

        public void IncrementKilled() => TotalKilled++;
        public void IncrementCorrupted() => TotalCorrupted++;
        public void IncrementMissed() => TotalMissed++;
        public void IncrementMistakes() => TotalMistakes++;

        public void Reset()
        {
            TotalKilled = 0;
            TotalCorrupted = 0;
            TotalMissed = 0;
            TotalMistakes = 0;
        }

        public double SuccessRate => TotalKilled > 0 ? (double)TotalCorrupted / TotalKilled * 100 : 0;
        public double MissedRate => TotalKilled > 0 ? (double)TotalMissed / TotalKilled * 100 : 0;
    }

    /// <summary>
    /// Represents data for a single essence monolith entity
    /// </summary>
    internal class EssenceEntityData
    {
        public long Address { get; set; }
        public long FirstSeenAddress { get; set; }
        public int EntityId { get; set; }
        public LabelOnGround? Label { get; set; }
        public Entity? Entity { get; set; }
        public EssenceAnalysis Analysis { get; set; }
        public EssenceAnalysis? PreviousAnalysis { get; set; }
        public EssenceState State { get; set; } = EssenceState.None;
        public EssenceState PreviousState { get; set; } = EssenceState.None;
        public Vector3? LastKnownPosition { get; set; }

        public bool WasCorruptedByPlayer { get; set; } = false;
        public bool WasKilled { get; set; } = false;
        public bool MissedCorruption { get; set; } = false;
        public bool MistakenCorruption { get; set; } = false;

        public bool IsSuccessfulCorruption => WasCorruptedByPlayer && PreviousState == EssenceState.ShouldCorrupt;
        public bool ShouldHaveBeenCorrupted => PreviousState == EssenceState.ShouldCorrupt && !WasCorruptedByPlayer;
    }

    internal enum EssenceState
    {
        None,
        ShouldCorrupt,
        ShouldKill
    }
}