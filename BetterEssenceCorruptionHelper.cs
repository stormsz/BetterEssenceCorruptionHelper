using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
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
                    // Update entity reference
                    data.Entity = entity;
                }

                UpdateEntityData(entity, data);
                UpdateGlobalDiscovery(entity, data);
            }

            // Clean up entities that are gone
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
                    if (_shouldCorruptEssences.Contains(positionKey) &&
                        !data.Analysis.IsCorrupted &&
                        !_successfullyCorrupted.Contains(positionKey))
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

                // Check for corruption
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
            foreach (var data in _trackedEntities.Values)
            {
                if (data.State == EssenceState.ShouldCorrupt && Settings.CorruptMe.ShowCorruptMe.Value)
                {
                    if (Settings.CorruptMe.DrawBorder.Value)
                        DrawBorder(data, isCorruptTarget: true);
                    if (Settings.CorruptMe.DrawText.Value)
                        DrawText(data, isCorruptTarget: true);
                }
                else if (data.State == EssenceState.ShouldKill && Settings.KillReady.ShowKillReady.Value)
                {
                    if (Settings.KillReady.DrawBorder.Value)
                        DrawBorder(data, isCorruptTarget: false);
                    if (Settings.KillReady.DrawText.Value)
                        DrawText(data, isCorruptTarget: false);
                }

                if (Settings.Debug.ShowDebugInfo.Value && data.Label?.Label != null)
                {
                    DrawEntityDebugWindow(data);
                }
            }

            if (Settings.SessionStats.ShowSessionStats.Value)
            {
                DrawSessionStatsWindow();
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

        private void DrawSessionStatsWindow()
        {
            if (string.IsNullOrEmpty(_cachedSessionStatsText))
                return;

            var position = new Vector2(
                Settings.SessionStats.SessionWindowX.Value,
                Settings.SessionStats.SessionWindowY.Value
            );

            DrawTextWindow(position, "Essence Stats", _cachedSessionStatsText,
                Settings.SessionStats.TitleBackground.Value,
                Settings.SessionStats.ContentBackground.Value,
                Settings.SessionStats.TitleColor.Value,
                Settings.SessionStats.TextColor.Value,
                Settings.SessionStats.BorderColor.Value);
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
            var borderRect = GetBorderRect(labelRect);

            var debugLines = BuildDebugContent(data);

            // Calculate required width based on content
            float maxWidth = 0;
            using (Graphics.SetTextScale(1.0f))
            {
                foreach (var (text, color) in debugLines)
                {
                    var width = Graphics.MeasureText(text).X;
                    if (width > maxWidth) maxWidth = width;
                }
            }

            var debugRect = new RectangleF(
                borderRect.Right,
                borderRect.Top,
                Math.Max(180, maxWidth + 15),
                10 + (debugLines.Count * 16)
            );

            // Always draw background for debug window
            var bgColor = Settings.Debug.DebugBackgroundColor.Value;
            bgColor.A = (byte)(Settings.Debug.DebugBackgroundOpacity.Value * 255);
            Graphics.DrawBox(debugRect, bgColor);
            Graphics.DrawFrame(debugRect, Settings.Debug.DebugBorderColor.Value, 1);

            for (int i = 0; i < debugLines.Count; i++)
            {
                var linePos = new Vector2(debugRect.X + 5, debugRect.Y + 5 + (i * 16));
                using (Graphics.SetTextScale(1.0f))
                {
                    var (text, color) = debugLines[i];
                    Graphics.DrawText(text, linePos, color, FontAlign.Left);
                }
            }
        }

        private RectangleF GetBorderRect(RectangleF rect)
        {
            float thickness = Settings.Visual.BorderThickness.Value;
            float margin = Settings.Visual.BorderMargin.Value;

            var borderRect = new RectangleF(
                rect.X - thickness / 2f + margin,
                rect.Y - thickness / 2f,
                Math.Max(rect.Width + thickness - margin * 2, 10f),
                Math.Max(rect.Height + thickness, 10f)
            );

            return borderRect;
        }

        private static List<(string text, Color color)> BuildDebugContent(EssenceEntityData data)
        {
            var lines = new List<(string, Color)>();

            // Header
            string stateString = data.State switch
            {
                EssenceState.ShouldCorrupt => "CORRUPT-ME",
                EssenceState.ShouldKill => "KILL-ME",
                _ => "UNKNOWN"
            };

            Color stateColor = data.State == EssenceState.ShouldCorrupt ? Color.Red : Color.Green;
            lines.Add(($"Essence {data.EntityId} - {stateString}", stateColor));

            if (!data.Analysis.IsValid)
            {
                lines.Add(("Analysis: INVALID", Color.Red));
                return lines;
            }

            // Essence counts
            lines.Add(($"Total: {data.Analysis.EssenceCount}", Color.White));

            if (data.Analysis.DeafeningCount > 0)
                lines.Add(($"  Deafening: {data.Analysis.DeafeningCount}", Color.Green));
            if (data.Analysis.ShriekingCount > 0)
                lines.Add(($"  Shrieking: {data.Analysis.ShriekingCount}", Color.Green));
            if (data.Analysis.ScreamingCount > 0)
                lines.Add(($"  Screaming: {data.Analysis.ScreamingCount}", Color.White));
            if (data.Analysis.WailingCount > 0)
                lines.Add(($"  Wailing: {data.Analysis.WailingCount}", Color.White));
            if (data.Analysis.WeepingCount > 0)
                lines.Add(($"  Weeping: {data.Analysis.WeepingCount}", Color.White));
            if (data.Analysis.MutteringCount > 0)
                lines.Add(($"  Muttering: {data.Analysis.MutteringCount}", Color.White));

            // Status flags
            lines.Add(("Flags:", Color.Yellow));
            lines.Add(($"  MEDS: {data.Analysis.HasMeds}", BoolToColor(data.Analysis.HasMeds)));
            lines.Add(($"  Corrupted: {data.Analysis.IsCorrupted}", BoolToColor(data.Analysis.IsCorrupted)));
            lines.Add(($"  Valuable: {data.Analysis.HasValuablePattern}", BoolToColor(data.Analysis.HasValuablePattern)));

            return lines;
        }

        private static Color BoolToColor(bool value) => value ? Color.Green : Color.Red;

        private void DrawBorder(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null) return;

            var rect = data.Label.Label.GetClientRectCache;

            // Use your original border calculation
            float thickness = Settings.Visual.BorderThickness.Value;
            float margin = Settings.Visual.BorderMargin.Value;

            var borderRect = new RectangleF(
                rect.X - thickness / 2f + margin,
                rect.Y - thickness / 2f,
                Math.Max(rect.Width + thickness - margin * 2, 10f),
                Math.Max(rect.Height + thickness, 10f)
            );

            var color = isCorruptTarget
                ? Settings.CorruptMe.BorderColor.Value
                : Settings.KillReady.BorderColor.Value;

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
                    ? Settings.CorruptMe.TextColor.Value
                    : Settings.KillReady.TextColor.Value;

                using (Graphics.SetTextScale(Settings.Visual.TextSize.Value))
                {
                    var textPos = new Vector2(
                        rect.X + rect.Width / 2,
                        rect.Y - Settings.Visual.TextSize.Value * 25 + 15
                    );

                    var textSize = Graphics.MeasureText(text);
                    var bgRect = new RectangleF(
                        textPos.X - textSize.X / 2 - 2,
                        textPos.Y - 2,
                        textSize.X + 4,
                        textSize.Y + 4
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