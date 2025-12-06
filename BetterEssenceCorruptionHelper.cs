using System.Text;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = SharpDX.Vector3;

namespace BetterEssenceCorruptionHelper
{
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        private Camera? _camera;

        private readonly Dictionary<long, EssenceEntityData> _trackedEntities = [];
        private int _entityIdCounter = 0;

        private readonly HashSet<string> _discoveredEssences = [];
        private readonly HashSet<string> _shouldCorruptEssences = [];
        private readonly HashSet<string> _successfullyCorrupted = [];
        private readonly HashSet<string> _missedCorruptions = [];

        private string _cachedSessionStatsText = "";
        private DateTime _lastStatsUpdate = DateTime.MinValue;
        private float _cachedWindowWidth = 0f;
        private int _screenWidth = 0;
        private int _screenHeight = 0;

        private bool AnyDebugEnabled => Settings.Debug.ShowDebugInfo.Value;

        public override bool Initialise()
        {
            _camera = GameController.Game.IngameState.Camera;
            Name = "Better Essence Corruption Helper";

            DebugWindow.LogMsg($"{Name} initialized", 2, Color.Green);

            return base.Initialise();
        }

        public override void AreaChange(AreaInstance area)
        {
            _trackedEntities.Clear();
            _discoveredEssences.Clear();
            _shouldCorruptEssences.Clear();
            _successfullyCorrupted.Clear();
            _missedCorruptions.Clear();
            _entityIdCounter = 0;
            UpdateSessionStatsCache();
        }

        public override Job? Tick()
        {
            if (!Settings.Enable.Value || !GameController.InGame || GameController.Area.CurrentArea.IsPeaceful)
                return null;

            if (IsAnyGameUIVisible())
                return null;

            // Cache screen dimensions if they change
            var rect = GameController.Window.GetWindowRectangle();
            if (_screenWidth != rect.Width || _screenHeight != rect.Height)
            {
                _screenWidth = (int)rect.Width;
                _screenHeight = (int)rect.Height;
            }

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
                    // Update entity reference
                    data.Entity = entity;
                }

                UpdateEntityData(entity, data);
                UpdateGlobalDiscovery(entity, data);
            }

            CleanupOldEssences(currentMonoliths);
        }

        private void CleanupOldEssences(HashSet<long> currentMonoliths)
        {
            var removedEntities = new List<long>();
            foreach (var (address, data) in _trackedEntities)
            {
                if (!currentMonoliths.Contains(address))
                {
                    var positionKey = GetPositionKeyFromData(data);

                    // Check for missed corruptions
                    if (_shouldCorruptEssences.Contains(positionKey) && !data.Analysis.IsCorrupted && !_successfullyCorrupted.Contains(positionKey))
                    {
                        if (!_missedCorruptions.Contains(positionKey))
                        {
                            _missedCorruptions.Add(positionKey);
                            if (AnyDebugEnabled)
                            {
                                DebugWindow.LogMsg($"MISSED CORRUPTION: Essence {data.EntityId} at {positionKey} was ShouldCorrupt but killed without corrupting", 1, Color.Orange);
                            }
                        }
                    }

                    removedEntities.Add(address);
                }
            }

            foreach (var address in removedEntities)
            {
                _trackedEntities.Remove(address);
            }
        }

        private bool IsValidMonolith(Entity entity)
        {
            if (entity == null ||
                entity.Address == 0 ||
                !entity.IsValid ||
                !entity.IsTargetable ||
                entity.Address == GameController.Player?.Address)
                return false;

            if (!entity.HasComponent<Render>() || !entity.HasComponent<Monolith>())
                return false;

            // Safety check
            try
            {
                var pos = entity.PosNum;
                if (pos.X == 0 && pos.Y == 0 && pos.Z == 0)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static string GetPositionKey(Entity entity)
        {
            var pos = entity.PosNum;
            return $"{(int)pos.X}_{(int)pos.Y}_{(int)pos.Z}";
        }

        private static string GetPositionKeyFromData(EssenceEntityData data)
        {
            if (data.LastKnownPosition.HasValue)
            {
                var pos = data.LastKnownPosition.Value;
                return $"{(int)pos.X}_{(int)pos.Y}_{(int)pos.Z}";
            }
            return $"unknown_{data.EntityId}";
        }

        private void UpdateGlobalDiscovery(Entity entity, EssenceEntityData data)
        {
            var positionKey = GetPositionKey(entity);

            var pos = entity.PosNum;
            data.LastKnownPosition = new Vector3(pos.X, pos.Y, pos.Z);

            _discoveredEssences.Add(positionKey);

            if (data.State == EssenceState.ShouldCorrupt)
            {
                _shouldCorruptEssences.Add(positionKey);
                _missedCorruptions.Remove(positionKey);
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

            try
            {
                data.Label = label;
                var newAnalysis = EssenceLabelAnalyzer.Analyze(label);

                if (!data.Analysis.IsCorrupted && newAnalysis.IsCorrupted)
                {
                    data.PreviousAnalysis = data.Analysis;

                    var positionKey = GetPositionKey(entity);
                    _successfullyCorrupted.Add(positionKey);
                    _missedCorruptions.Remove(positionKey);

                    if (AnyDebugEnabled)
                    {
                        DebugWindow.LogMsg($"SUCCESSFUL CORRUPTION: Essence {data.EntityId} at {positionKey} was corrupted", 1, Color.LightGreen);
                    }
                }

                data.Analysis = newAnalysis;
                data.State = DetermineEssenceState(newAnalysis);

                if (AnyDebugEnabled && data.State != data.PreviousState)
                {
                    DebugWindow.LogMsg($"Essence {data.EntityId}: {data.PreviousState} -> {data.State}", 1);
                    data.PreviousState = data.State;
                }
            }
            catch (Exception ex)
            {
                if (AnyDebugEnabled)
                {
                    DebugWindow.LogError($"UpdateEntityData failed: {ex.Message}");
                }
                data.Label = null;
            }
        }

        private static EssenceState DetermineEssenceState(EssenceAnalysis analysis)
        {
            if (analysis.IsCorrupted)
                return EssenceState.ShouldKill;

            if (analysis.HasValuablePattern)
                return EssenceState.ShouldCorrupt;

            return EssenceState.ShouldKill;
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

            UpdateLabelReferences();

            // Draw visuals for essences
            if (Settings.Indicators.EnableAllIndicators.Value)
            {
                foreach (var data in _trackedEntities.Values)
                {
                    if (data.State == EssenceState.ShouldCorrupt && Settings.Indicators.CorruptMe.ShowCorruptMe.Value)
                    {
                        if (Settings.Indicators.CorruptMe.DrawBorder.Value)
                            DrawBorder(data, isCorruptTarget: true);
                        if (Settings.Indicators.CorruptMe.DrawText.Value)
                            DrawText(data, isCorruptTarget: true);
                    }
                    else if (data.State == EssenceState.ShouldKill && Settings.Indicators.KillReady.ShowKillReady.Value)
                    {
                        if (Settings.Indicators.KillReady.DrawBorder.Value)
                            DrawBorder(data, isCorruptTarget: false);
                        if (Settings.Indicators.KillReady.DrawText.Value)
                            DrawText(data, isCorruptTarget: false);
                    }

                    if (Settings.Debug.ShowDebugInfo.Value && data.Label?.Label != null)
                    {
                        DrawEntityDebugWindow(data);
                    }
                }
            }

            if (ShouldShowMapStats())
            {
                DrawMapStatsWindow();
            }
        }

        private void UpdateLabelReferences()
        {
            foreach (var data in _trackedEntities.Values)
            {
                if (data.Label == null && data.Entity != null)
                {
                    data.Label = FindLabelForEntity(data.Entity);
                }
            }
        }

        private bool ShouldShowMapStats()
        {
            if (!Settings.MapStats.ShowMapStats.Value)
                return false;

            // If we're in town/hideout and the option is disabled, don't show
            if (GameController.Area.CurrentArea.IsPeaceful && !Settings.MapStats.ShowInTownHideout.Value)
                return false;

            return true;
        }

        private void DrawMapStatsWindow()
        {
            if (string.IsNullOrEmpty(_cachedSessionStatsText))
                return;

            var screenWidth = GameController.Window.GetWindowRectangle().Width;
            var screenHeight = GameController.Window.GetWindowRectangle().Height;

            var windowWidth = _cachedWindowWidth;
            if (windowWidth == 0f)
            {
                windowWidth = CalculateMapStatsWindowWidth(_cachedSessionStatsText);
                _cachedWindowWidth = windowWidth;
            }

            // Calculate window height for clamping
            const int padding = 8;
            const int titleFontSize = 14;
            const int contentFontSize = 12;
            var titleSize = Graphics.MeasureText("Essence Map Stats", titleFontSize);
            var contentSize = Graphics.MeasureText(_cachedSessionStatsText, contentFontSize);
            var titleHeight = titleSize.Y + padding;
            var contentHeight = contentSize.Y + padding * 2;
            var totalHeight = titleHeight + contentHeight;

            Vector2 position;

            if (Settings.MapStats.WindowAnchor.Value == "Top Left")
            {
                position = new Vector2(
                    Settings.MapStats.OffsetX.Value,
                    Settings.MapStats.OffsetY.Value
                );
            }
            else // Top Right
            {
                position = new Vector2(
                    screenWidth - windowWidth - Settings.MapStats.OffsetX.Value,
                    Settings.MapStats.OffsetY.Value
                );
            }

            // Clamp to screen bounds
            position.X = Math.Max(0, Math.Min(position.X, screenWidth - windowWidth));
            position.Y = Math.Max(0, Math.Min(position.Y, screenHeight - totalHeight));

            DrawTextWindow(position, "Essence Map Stats", _cachedSessionStatsText,
                Settings.MapStats.TitleBackground.Value,
                Settings.MapStats.ContentBackground.Value,
                Settings.MapStats.TitleColor.Value,
                Settings.MapStats.TextColor.Value,
                Settings.MapStats.BorderColor.Value);
        }

        private void UpdateSessionStatsCache()
        {
            var sb = new StringBuilder();

            sb.AppendLine("Current Map:");
            sb.AppendLine($"  Found: {_discoveredEssences.Count}");
            sb.AppendLine($"  Should Corrupt: {_shouldCorruptEssences.Count}");
            sb.AppendLine($"  Successfully Corrupted: {_successfullyCorrupted.Count}");
            sb.AppendLine($"  Missed Corruptions: {_missedCorruptions.Count}");

            _cachedSessionStatsText = sb.ToString();
            _lastStatsUpdate = DateTime.Now;

            // Recalculate window width when stats change
            _cachedWindowWidth = CalculateMapStatsWindowWidth(_cachedSessionStatsText);
        }

        private float CalculateMapStatsWindowWidth(string content)
        {
            const int padding = 8;
            const int titleFontSize = 14;
            const int contentFontSize = 12;

            var titleSize = Graphics.MeasureText("Essence Map Stats", titleFontSize);
            var contentSize = Graphics.MeasureText(content, contentFontSize);

            return Math.Max(titleSize.X, contentSize.X) + padding * 2;
        }

        private void DrawTextWindow(Vector2 position, string title, string content,
            Color titleBg, Color contentBg, Color titleColor, Color textColor, Color borderColor)
        {
            const int padding = 8;
            const int titleFontSize = 14;
            const int contentFontSize = 12;

            var titleSize = Graphics.MeasureText(title, titleFontSize);
            var contentSize = Graphics.MeasureText(content, contentFontSize);

            float width = Math.Max(titleSize.X, contentSize.X) + padding * 2;
            float titleHeight = titleSize.Y + padding;
            float contentHeight = contentSize.Y + padding * 2;
            float totalHeight = titleHeight + contentHeight;

            var titleRect = new RectangleF(position.X, position.Y, width, titleHeight);
            var contentRect = new RectangleF(position.X, position.Y + titleHeight, width, contentHeight);

            // Draw title bar
            Graphics.DrawBox(titleRect, titleBg);
            Graphics.DrawText(title,
                new Vector2(position.X + padding, position.Y + padding / 2),
                titleColor, FontAlign.Left);

            // Draw content background
            Graphics.DrawBox(contentRect, contentBg);

            // Draw content text
            Graphics.DrawText(content,
                new Vector2(position.X + padding, position.Y + titleHeight + padding / 2),
                textColor, FontAlign.Left);

            // Draw border
            Graphics.DrawFrame(new RectangleF(position.X, position.Y, width, totalHeight),
                borderColor, 1);
        }

        private void DrawEntityDebugWindow(EssenceEntityData data)
        {
            if (data.Label?.Label == null) return;

            var labelRect = data.Label.Label.GetClientRectCache;
            var borderRect = GetBorderRect(labelRect, isCorruptTarget: data.State == EssenceState.ShouldCorrupt);

            var debugLines = BuildDebugContent(data);

            // Calculate required width
            float maxWidth = 0;
            using (Graphics.SetTextScale(1.0f))
            {
                foreach (var segments in debugLines)
                {
                    float lineWidth = 0;
                    foreach (var (text, _) in segments)
                    {
                        lineWidth += Graphics.MeasureText(text).X;
                    }
                    if (lineWidth > maxWidth)
                        maxWidth = lineWidth;
                }
            }

            var debugRect = new RectangleF(
                borderRect.Right,
                borderRect.Top,
                Math.Max(180, maxWidth + 15),
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
                    var segments = debugLines[i];
                    float x = debugRect.X + 5;

                    foreach (var (text, color) in segments)
                    {
                        var size = Graphics.MeasureText(text);
                        Graphics.DrawText(text, new Vector2(x, debugRect.Y + 5 + (i * 16)), color);
                        x += size.X;
                    }
                }
            }
        }


        private RectangleF GetBorderRect(RectangleF rect, bool isCorruptTarget)
        {
            float thickness = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderThickness.Value
                : Settings.Indicators.KillReady.BorderThickness.Value;

            float margin = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderMargin.Value
                : Settings.Indicators.KillReady.BorderMargin.Value;

            var borderRect = new RectangleF(
                rect.X - thickness / 2f + margin,
                rect.Y - thickness / 2f,
                Math.Max(rect.Width + thickness - margin * 2, 10f),
                Math.Max(rect.Height + thickness, 10f)
            );

            return borderRect;
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
            Color CountColor(int v) => v == 0 ? Color.White : Color.Green;
            Color BoolColor(bool b) => b ? Color.Green : Color.Red;

            lines.Add([($"Essence #{data.EntityId} ", Color.White)]);
            lines.Add([("Status: ", Color.White), (stateString, stateColor)]);

            lines.Add([]);

            if (!data.Analysis.IsValid)
            {
                lines.Add([("Analysis: INVALID", Color.Red)]);
                return lines;
            }

            lines.Add([("Total: ", Color.White), ($"{data.Analysis.EssenceCount}", CountColor(data.Analysis.EssenceCount))]);
            lines.Add([("Deafening: ", Color.White), ($"{data.Analysis.DeafeningCount}", CountColor(data.Analysis.DeafeningCount))]);
            lines.Add([("Shrieking: ", Color.White), ($"{data.Analysis.ShriekingCount}", CountColor(data.Analysis.ShriekingCount))]);
            lines.Add([("Screaming: ", Color.White), ($"{data.Analysis.ScreamingCount}", CountColor(data.Analysis.ScreamingCount))]);
            lines.Add([("Wailing: ", Color.White), ($"{data.Analysis.WailingCount}", CountColor(data.Analysis.WailingCount))]);
            lines.Add([("Weeping: ", Color.White), ($"{data.Analysis.WeepingCount}", CountColor(data.Analysis.WeepingCount))]);
            lines.Add([("Muttering: ", Color.White), ($"{data.Analysis.MutteringCount}", CountColor(data.Analysis.MutteringCount))]);

            lines.Add([]);
            lines.Add([("Flags:", Color.White)]);
            lines.Add([("  MEDS: ", Color.White), ($"{data.Analysis.HasMeds}", BoolColor(data.Analysis.HasMeds))]);
            lines.Add([("  Corrupted: ", Color.White), ($"{data.Analysis.IsCorrupted}", BoolColor(data.Analysis.IsCorrupted))]);
            lines.Add([("  Valuable: ", Color.White), ($"{data.Analysis.HasValuablePattern}", BoolColor(data.Analysis.HasValuablePattern))]);

            return lines;
        }

        private void DrawBorder(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null) return;

            var rect = data.Label.Label.GetClientRectCache;

            float thickness = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderThickness.Value
                : Settings.Indicators.KillReady.BorderThickness.Value;

            float margin = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderMargin.Value
                : Settings.Indicators.KillReady.BorderMargin.Value;

            var borderRect = new RectangleF(
                rect.X - thickness / 2f + margin,
                rect.Y - thickness / 2f,
                Math.Max(rect.Width + thickness - margin * 2, 10f),
                Math.Max(rect.Height + thickness, 10f)
            );

            var color = isCorruptTarget
                ? Settings.Indicators.CorruptMe.BorderColor.Value
                : Settings.Indicators.KillReady.BorderColor.Value;

            Graphics.DrawFrame(borderRect, color, (int)thickness);
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
                    var textPos = new Vector2(
                        rect.X + rect.Width / 2,
                        rect.Y - textSize * 25 + 15
                    );

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

    internal class EssenceEntityData
    {
        public long Address { get; set; }
        public long FirstSeenAddress { get; set; }
        public int EntityId { get; set; }
        public LabelOnGround? Label { get; set; }
        public Entity? Entity { get; set; }
        public EssenceAnalysis Analysis { get; set; }
        public EssenceAnalysis PreviousAnalysis { get; set; }
        public EssenceState State { get; set; }
        public EssenceState PreviousState { get; set; }
        public SharpDX.Vector3? LastKnownPosition { get; set; }
    }

    internal enum EssenceState
    {
        None,
        ShouldCorrupt,
        ShouldKill
    }
}