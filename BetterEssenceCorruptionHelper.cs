using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using System.Collections;
using SharpDX;
using System.Text;
using Vector2 = System.Numerics.Vector2;
using Vector3 = SharpDX.Vector3;

namespace BetterEssenceCorruptionHelper
{
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        #region Constants

        private const int ESSENCE_UNLOAD_DISTANCE = 120;
        private const int POSITION_TOLERANCE = 2;
        private const int STATS_UPDATE_MS = 1000;
        private const int ENTITY_PROCESS_MS = 16; // ~60 FPS
        private const int UI_UPDATE_FRAMES = 2;
        private const int COMPLETED_BUFFER_SIZE = 100;

        #endregion

        #region Fields

        private readonly Dictionary<long, EssenceEntityData> _trackedEntities = [];
        private readonly CircularBuffer<EssenceEntityData> _completedEssences;
        private readonly MapStatistics _mapStats = new();
        private readonly Runner _pluginRunner = new("BetterEssenceCorruptionHelper");

        // Coroutines
        private readonly WaitTime _statsUpdateWait;
        private readonly WaitTime _entityProcessWait;
        private readonly WaitRender _uiUpdateWait;

        private Coroutine? _statsUpdateCoroutine;
        private Coroutine? _entityProcessingCoroutine;
        private Coroutine? _uiUpdateCoroutine;

        // State
        private int _entityIdCounter;
        private string _cachedSessionStatsText = "";
        private float _cachedWindowWidth;
        private int _screenWidth;
        private int _screenHeight;

        private bool AnyDebugEnabled => Settings.Debug.ShowDebugInfo.Value;

        #endregion

        #region Initialization

        public BetterEssenceCorruptionHelper()
        {
            _completedEssences = new CircularBuffer<EssenceEntityData>(COMPLETED_BUFFER_SIZE);
            _statsUpdateWait = new WaitTime(STATS_UPDATE_MS);
            _entityProcessWait = new WaitTime(ENTITY_PROCESS_MS);
            _uiUpdateWait = new WaitRender(UI_UPDATE_FRAMES);
        }

        public override bool Initialise()
        {
            Name = "Better Essence Corruption Helper";

            InitializeCoroutines();

            DebugWindow.LogMsg($"{Name} initialized", 2, Color.Green);
            return base.Initialise();
        }

        private void InitializeCoroutines()
        {
            // Create coroutines
            _statsUpdateCoroutine = new Coroutine(StatsUpdateRoutine(), this, "StatsUpdate");
            _entityProcessingCoroutine = new Coroutine(EntityProcessingRoutine(), this, "EntityProcessing");
            _uiUpdateCoroutine = new Coroutine(UIUpdateRoutine(), this, "UIUpdate");

            // Set priorities
            _entityProcessingCoroutine.Priority = CoroutinePriority.Critical;
            _uiUpdateCoroutine.Priority = CoroutinePriority.High;
            _statsUpdateCoroutine.Priority = CoroutinePriority.Normal;

            // Add coroutines to runner
            _pluginRunner.Run(_statsUpdateCoroutine);
            _pluginRunner.Run(_entityProcessingCoroutine);
            _pluginRunner.Run(_uiUpdateCoroutine);
        }

        #endregion

        #region Lifecycle

        public override void AreaChange(AreaInstance area)
        {
            ResetState();
        }

        private void ResetState()
        {
            _trackedEntities.Clear();
            _completedEssences.Clear();
            _entityIdCounter = 0;
            _cachedWindowWidth = 0f;
            _screenWidth = 0;
            _screenHeight = 0;
            _mapStats.Reset();
            UpdateSessionStatsCache();
        }

        public override Job? Tick()
        {
            _pluginRunner.Update();
            return null;
        }

        public override void Render()
        {
            if (!ShouldRender())
                return;

            RenderEssenceIndicators();
            RenderMapStatsWindow();
        }

        private bool ShouldRender() =>
            Settings.Enable.Value &&
            GameController.InGame &&
            !IsAnyGameUIVisible();

        #endregion

        #region Coroutines

        private IEnumerator StatsUpdateRoutine()
        {
            while (true)
            {
                yield return _statsUpdateWait;

                if (ShouldProcess())
                    UpdateSessionStatsCache();
            }
        }

        private IEnumerator EntityProcessingRoutine()
        {
            while (true)
            {
                yield return _entityProcessWait;

                if (ShouldProcess())
                    ProcessEssences();
            }
        }

        private IEnumerator UIUpdateRoutine()
        {
            while (true)
            {
                yield return _uiUpdateWait;

                if (ShouldProcess())
                    UpdateEntityLabels();
            }
        }

        #endregion

        #region Entity Processing

        private void ProcessEssences()
        {
            var currentMonoliths = new HashSet<long>();

            try
            {
                foreach (var entity in GameController.Entities)
                {
                    if (!IsValidMonolith(entity))
                        continue;

                    var data = GetOrCreateEntityData(entity, currentMonoliths);
                    currentMonoliths.Add(entity.Address);

                    if (data.Label != null)
                        UpdateEntityData(entity, data);

                    // Always cache position for relinking after unload
                    data.LastKnownPosition = entity.PosNum.ToVector3();
                }

                CleanupOldEssences(currentMonoliths);
            }
            catch (Exception ex)
            {
                LogDebugError($"ProcessEssences failed: {ex.Message}");
            }
        }

        private EssenceEntityData GetOrCreateEntityData(Entity entity, HashSet<long> currentMonoliths)
        {
            // Already tracking this address
            if (_trackedEntities.TryGetValue(entity.Address, out var data))
            {
                data.Entity = entity;
                return data;
            }

            var entityPos = entity.PosNum.ToVector3();

            // Try to find an essence that unloaded and reloaded (same position, different address)
            var existingData = FindUnloadedEssenceAtPosition(entityPos, currentMonoliths);

            if (existingData != null)
                return RelinkEssence(existingData, entity);

            // It's a new essence
            return CreateNewEssenceData(entity);
        }

        private EssenceEntityData? FindUnloadedEssenceAtPosition(Vector3 position, HashSet<long> currentMonoliths)
        {
            return _trackedEntities.Values
                .FirstOrDefault(x =>
                    !currentMonoliths.Contains(x.Address) && // Not already processed this frame
                    x.LastKnownPosition.HasValue &&
                    Vector3.Distance(x.LastKnownPosition.Value, position) < POSITION_TOLERANCE);
        }

        private EssenceEntityData RelinkEssence(EssenceEntityData existingData, Entity entity)
        {
            // Remove old address key
            _trackedEntities.Remove(existingData.Address);

            // Update to new address
            existingData.Address = entity.Address;
            existingData.Entity = entity;

            _trackedEntities[entity.Address] = existingData;

            LogDebug($"Essence Relinked: {existingData.EntityId} (Addr changed)", Color.Cyan);

            return existingData;
        }

        private EssenceEntityData CreateNewEssenceData(Entity entity)
        {
            var newData = new EssenceEntityData
            {
                EntityId = ++_entityIdCounter,
                Address = entity.Address,
                FirstSeenAddress = entity.Address,
                Entity = entity,
                Label = FindLabelForEntity(entity)
            };

            _trackedEntities[entity.Address] = newData;
            return newData;
        }

        private void UpdateEntityData(Entity entity, EssenceEntityData data)
        {
            if (data.Label == null)
                return;

            var newAnalysis = EssenceLabelAnalyzer.Analyze(data.Label);
            var oldState = data.State;
            var wasCorruptedBefore = data.Analysis.IsCorrupted;

            data.Analysis = newAnalysis;
            var newState = DetermineEssenceState(newAnalysis);

            // Detect corruption event
            if (!wasCorruptedBefore && newAnalysis.IsCorrupted)
            {
                HandleEssenceCorruption(entity, data, oldState, newState);
            }

            // Track state transitions
            if (HasStateChanged(wasCorruptedBefore, newAnalysis.IsCorrupted, oldState, newState))
            {
                data.PreviousState = oldState;
                LogDebug($"Essence {data.EntityId}: {oldState} -> {newState}");
            }

            data.State = newState;
        }

        private static bool HasStateChanged(bool wasCorrupted, bool isCorrupted, EssenceState oldState, EssenceState newState) =>
            (wasCorrupted || !isCorrupted) && newState != oldState;

        private void HandleEssenceCorruption(Entity entity, EssenceEntityData data, EssenceState oldState, EssenceState newState)
        {
            data.WasCorruptedByPlayer = true;
            data.PreviousAnalysis = data.Analysis;
            data.PreviousState = oldState;

            LogDebug($"Essence {data.EntityId} (0x{entity.Address:X}) corrupted. Old state: {oldState}, New state: {newState}", Color.Yellow);

            // Track outcomes
            if (oldState == EssenceState.ShouldCorrupt)
            {
                _mapStats.IncrementCorrupted();
                LogDebug($"SUCCESSFUL CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X})", Color.LightGreen);
            }
            else if (oldState == EssenceState.ShouldKill)
            {
                data.MistakenCorruption = true;
                _mapStats.IncrementMistakes();
                LogDebug($"MISTAKEN CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X}) should have been killed!", Color.OrangeRed);
            }
        }

        private void CleanupOldEssences(HashSet<long> currentMonoliths)
        {
            var player = GameController.Player;
            if (player == null)
                return;

            var playerPos = player.PosNum.ToVector3();
            var removedEntities = new List<long>();

            foreach (var (address, data) in _trackedEntities)
            {
                // Still visible in game
                if (currentMonoliths.Contains(address))
                    continue;

                // Check if unloaded vs killed/opened
                if (IsEssenceUnloaded(data, playerPos))
                {
                    // Player too far - essence just unloaded, clear label for re-discovery
                    data.Label = null;
                    continue;
                }

                // Entity disappeared while player was close - it was killed/opened
                HandleEssenceKilled(data, address);
                removedEntities.Add(address);
            }

            // Remove killed essences
            foreach (var address in removedEntities)
                _trackedEntities.Remove(address);
        }

        private static bool IsEssenceUnloaded(EssenceEntityData data, Vector3 playerPos)
        {
            if (!data.LastKnownPosition.HasValue)
                return false;

            // PoE unloads entities ~150 units away. If player is >120 units away, assume unload.
            var distance = Vector3.Distance(playerPos, data.LastKnownPosition.Value);
            return distance > ESSENCE_UNLOAD_DISTANCE;
        }

        private void HandleEssenceKilled(EssenceEntityData data, long address)
        {
            data.WasKilled = true;
            _mapStats.IncrementKilled();

            // Check for missed corruptions
            if (IsMissedCorruption(data))
            {
                data.MissedCorruption = true;
                _mapStats.IncrementMissed();
                LogDebug($"MISSED CORRUPTION: Essence {data.EntityId} (0x{address:X}) should have been corrupted but was killed", Color.Orange);
            }

            _completedEssences.PushBack(data);
        }

        private static bool IsMissedCorruption(EssenceEntityData data) =>
            data.State == EssenceState.ShouldCorrupt &&
            !data.Analysis.IsCorrupted &&
            !data.WasCorruptedByPlayer;

        private void UpdateEntityLabels()
        {
            foreach (var data in _trackedEntities.Values)
            {
                if (data.Label == null && data.Entity != null)
                    data.Label = FindLabelForEntity(data.Entity);
            }
        }

        #endregion

        #region Entity Validation & Helpers

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
                return pos.X != 0 || pos.Y != 0 || pos.Z != 0;
            }
            catch
            {
                return false;
            }
        }

        private static EssenceState DetermineEssenceState(EssenceAnalysis analysis) =>
            analysis.IsCorrupted ? EssenceState.ShouldKill :
            analysis.HasValuablePattern ? EssenceState.ShouldCorrupt :
            EssenceState.ShouldKill;

        private LabelOnGround? FindLabelForEntity(Entity entity) =>
            GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                .FirstOrDefault(x => x.ItemOnGround?.Address == entity.Address);

        private bool ShouldProcess() =>
            Settings.Enable.Value &&
            GameController.InGame &&
            !GameController.Area.CurrentArea.IsPeaceful &&
            !IsAnyGameUIVisible();

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

        #endregion

        #region Rendering

        private void RenderEssenceIndicators()
        {
            if (!Settings.Indicators.EnableAllIndicators.Value)
                return;

            foreach (var data in _trackedEntities.Values)
            {
                RenderEssenceIndicator(data);

                if (Settings.Debug.ShowDebugInfo.Value && data.Label?.Label != null)
                    DrawEntityDebugWindow(data);
            }
        }

        private void RenderEssenceIndicator(EssenceEntityData data)
        {
            var isCorruptTarget = data.State == EssenceState.ShouldCorrupt;

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
        }

        private void DrawBorder(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var borderRect = CalculateBorderRect(rect, isCorruptTarget);
            var indicatorSettings = GetIndicatorSettings(isCorruptTarget);

            Graphics.DrawFrame(borderRect, indicatorSettings.BorderColor, (int)indicatorSettings.BorderThickness);
        }

        private void DrawText(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null)
                return;

            try
            {
                var rect = data.Label.Label.GetClientRectCache;
                var indicatorSettings = GetIndicatorSettings(isCorruptTarget);
                var text = isCorruptTarget ? "CORRUPT" : "KILL";

                using (Graphics.SetTextScale(indicatorSettings.TextSize))
                {
                    var textPos = new Vector2(rect.X + rect.Width / 2, rect.Y - indicatorSettings.TextSize * 25 + 15);
                    var measuredSize = Graphics.MeasureText(text);

                    // Draw background
                    var bgRect = new RectangleF(
                        textPos.X - measuredSize.X / 2 - 2,
                        textPos.Y - 2,
                        measuredSize.X + 4,
                        measuredSize.Y + 4
                    );
                    Graphics.DrawBox(bgRect, new Color(0, 0, 0, 150));

                    // Draw text
                    Graphics.DrawText(text, textPos, indicatorSettings.TextColor, FontAlign.Center);
                }
            }
            catch (Exception ex)
            {
                LogDebugError($"DrawText failed: {ex.Message}");
            }
        }

        private RectangleF CalculateBorderRect(RectangleF rect, bool isCorruptTarget)
        {
            var indicatorSettings = GetIndicatorSettings(isCorruptTarget);

            return new RectangleF(
                rect.X - indicatorSettings.BorderThickness / 2f + indicatorSettings.BorderMargin,
                rect.Y - indicatorSettings.BorderThickness / 2f,
                Math.Max(rect.Width + indicatorSettings.BorderThickness - indicatorSettings.BorderMargin * 2, 10f),
                Math.Max(rect.Height + indicatorSettings.BorderThickness, 10f)
            );
        }

        private EssenceIndicatorSettings GetIndicatorSettings(bool isCorruptTarget)
        {
            if (isCorruptTarget)
            {
                var settings = Settings.Indicators.CorruptMe;
                return new EssenceIndicatorSettings
                {
                    BorderColor = settings.BorderColor.Value,
                    BorderThickness = settings.BorderThickness.Value,
                    BorderMargin = settings.BorderMargin.Value,
                    TextColor = settings.TextColor.Value,
                    TextSize = settings.TextSize.Value
                };
            }
            else
            {
                var settings = Settings.Indicators.KillReady;
                return new EssenceIndicatorSettings
                {
                    BorderColor = settings.BorderColor.Value,
                    BorderThickness = settings.BorderThickness.Value,
                    BorderMargin = settings.BorderMargin.Value,
                    TextColor = settings.TextColor.Value,
                    TextSize = settings.TextSize.Value
                };
            }
        }

        #endregion

        #region Debug Rendering

        private void DrawEntityDebugWindow(EssenceEntityData data)
        {
            if (data.Label?.Label == null)
                return;

            var labelRect = data.Label.Label.GetClientRectCache;
            var borderRect = CalculateBorderRect(labelRect, data.State == EssenceState.ShouldCorrupt);
            var debugLines = BuildDebugContent(data);

            var debugRect = CalculateDebugWindowRect(borderRect, debugLines);
            DrawDebugWindowBackground(debugRect);
            DrawDebugWindowContent(debugRect, debugLines);
        }

        private RectangleF CalculateDebugWindowRect(RectangleF borderRect, List<List<(string text, Color color)>> debugLines)
        {
            float maxWidth = 0;
            using (Graphics.SetTextScale(1.0f))
            {
                foreach (var segments in debugLines)
                {
                    var lineWidth = segments.Sum(seg => Graphics.MeasureText(seg.text).X);
                    if (lineWidth > maxWidth)
                        maxWidth = lineWidth;
                }
            }

            return new RectangleF(
                borderRect.Right,
                borderRect.Top,
                Math.Max(260, maxWidth + 15),
                10 + (debugLines.Count * 16)
            );
        }

        private void DrawDebugWindowBackground(RectangleF rect)
        {
            var bgColor = Settings.Debug.DebugBackgroundColor.Value;
            bgColor.A = (byte)(Settings.Debug.DebugBackgroundOpacity.Value * 255);

            Graphics.DrawBox(rect, bgColor);
            Graphics.DrawFrame(rect, Settings.Debug.DebugBorderColor.Value, 1);
        }

        private void DrawDebugWindowContent(RectangleF rect, List<List<(string text, Color color)>> debugLines)
        {
            using (Graphics.SetTextScale(1.0f))
            {
                for (var i = 0; i < debugLines.Count; i++)
                {
                    var x = rect.X + 5;
                    foreach (var (text, color) in debugLines[i])
                    {
                        Graphics.DrawText(text, new Vector2(x, rect.Y + 5 + (i * 16)), color);
                        x += Graphics.MeasureText(text).X;
                    }
                }
            }
        }

        private static List<List<(string text, Color color)>> BuildDebugContent(EssenceEntityData data)
        {
            var lines = new List<List<(string, Color)>>();

            AddDebugHeader(lines, data);
            AddDebugPositionInfo(lines, data);
            AddDebugAnalysis(lines, data);

            return lines;
        }

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

            lines.Add([]);
        }

        private static void AddDebugPositionInfo(List<List<(string, Color)>> lines, EssenceEntityData data)
        {
            lines.Add([("Addr: ", Color.White), ($"0x{data.Address:X12}", Color.LightSkyBlue)]);

            if (data.LastKnownPosition.HasValue)
            {
                var pos = data.LastKnownPosition.Value;
                lines.Add([("Pos: ", Color.White), ($"({pos.X:F0}, {pos.Y:F0}, {pos.Z:F0})", Color.LightYellow)]);
            }

            lines.Add([]);
        }

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

        private static bool ShouldShowComparison(EssenceEntityData data) =>
            data.WasCorruptedByPlayer &&
            data.PreviousAnalysis.HasValue &&
            data.PreviousAnalysis.Value.IsValid;

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

        private static void AddBooleanLine(List<List<(string text, Color color)>> lines, string label, bool value)
        {
            var color = value ? Color.Green : Color.Red;
            lines.Add([($"{label}: ", Color.White), (value ? "Yes" : "No", color)]);
        }

        #endregion

        #region Map Stats Window

        private void RenderMapStatsWindow()
        {
            if (!ShouldShowMapStats())
                return;

            if (string.IsNullOrEmpty(_cachedSessionStatsText))
                return;

            EnsureScreenDimensionsCached();
            EnsureWindowWidthCached();

            var position = CalculateMapStatsPosition();

            DrawTextWindow(
                position,
                "Essence Map Stats",
                _cachedSessionStatsText,
                _cachedWindowWidth,
                Settings.MapStats.TitleBackground.Value,
                Settings.MapStats.ContentBackground.Value,
                Settings.MapStats.TitleColor.Value,
                Settings.MapStats.TextColor.Value,
                Settings.MapStats.BorderColor.Value);
        }

        private bool ShouldShowMapStats() =>
            Settings.MapStats.ShowMapStats.Value &&
            (!GameController.Area.CurrentArea.IsPeaceful || Settings.MapStats.ShowInTownHideout.Value);

        private void EnsureScreenDimensionsCached()
        {
            if (_screenWidth == 0 || _screenHeight == 0)
            {
                var rect = GameController.Window.GetWindowRectangle();
                _screenWidth = (int)rect.Width;
                _screenHeight = (int)rect.Height;
            }
        }

        private void EnsureWindowWidthCached()
        {
            if (_cachedWindowWidth == 0f)
                _cachedWindowWidth = CalculateMapStatsWindowWidth(_cachedSessionStatsText);
        }

        private Vector2 CalculateMapStatsPosition()
        {
            const int padding = 8;
            const int titleFontSize = 14;
            const int contentFontSize = 12;

            var titleSize = Graphics.MeasureText("Essence Map Stats", titleFontSize);
            var contentSize = Graphics.MeasureText(_cachedSessionStatsText, contentFontSize);
            var totalHeight = titleSize.Y + padding + contentSize.Y + padding * 2;

            // Calculate base position
            var position = Settings.MapStats.WindowAnchor.Value == "Top Left"
                ? new Vector2(Settings.MapStats.OffsetX.Value, Settings.MapStats.OffsetY.Value)
                : new Vector2(_screenWidth - _cachedWindowWidth - Settings.MapStats.OffsetX.Value, Settings.MapStats.OffsetY.Value);

            // Clamp to screen bounds
            return new Vector2(
                Math.Max(0, Math.Min(position.X, _screenWidth - _cachedWindowWidth)),
                Math.Max(0, Math.Min(position.Y, _screenHeight - totalHeight))
            );
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

            var titleHeight = titleSize.Y + padding;
            var contentHeight = contentSize.Y + padding * 2;
            var totalHeight = titleHeight + contentHeight;

            var titleRect = new RectangleF(position.X, position.Y, width, titleHeight);
            var contentRect = new RectangleF(position.X, position.Y + titleHeight, width, contentHeight);

            // Draw backgrounds
            Graphics.DrawBox(titleRect, titleBg);
            Graphics.DrawBox(contentRect, contentBg);

            // Draw title (centered if it fits, left-aligned otherwise)
            var availableWidth = width - 5;
            var titleX = titleSize.X <= availableWidth
                ? position.X + (width - titleSize.X) / 2
                : position.X + 5;

            Graphics.DrawText(title, new Vector2(titleX, position.Y + padding / 2), titleColor, FontAlign.Left);

            // Draw content
            Graphics.DrawText(content, new Vector2(position.X + padding, position.Y + titleHeight + padding / 2), textColor, FontAlign.Left);

            // Draw border
            Graphics.DrawFrame(new RectangleF(position.X, position.Y, width, totalHeight), borderColor, 1);
        }

        #endregion

        #region Logging Helpers

        private void LogDebug(string message, Color? color = null)
        {
            if (AnyDebugEnabled)
                DebugWindow.LogMsg(message, 1, color ?? Color.White);
        }

        private void LogDebugError(string message)
        {
            if (AnyDebugEnabled)
                DebugWindow.LogError(message);
        }

        #endregion
    }

    #region Supporting Classes

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

        public bool WasCorruptedByPlayer { get; set; }
        public bool WasKilled { get; set; }
        public bool MissedCorruption { get; set; }
        public bool MistakenCorruption { get; set; }

        public bool IsSuccessfulCorruption => WasCorruptedByPlayer && PreviousState == EssenceState.ShouldCorrupt;
        public bool ShouldHaveBeenCorrupted => PreviousState == EssenceState.ShouldCorrupt && !WasCorruptedByPlayer;
    }

    internal enum EssenceState
    {
        None,
        ShouldCorrupt,
        ShouldKill
    }

    internal readonly struct EssenceIndicatorSettings
    {
        public Color BorderColor { get; init; }
        public float BorderThickness { get; init; }
        public float BorderMargin { get; init; }
        public Color TextColor { get; init; }
        public float TextSize { get; init; }
    }

    #endregion

    #region Extensions

    internal static class PosNumExtensions
    {
        public static Vector3 ToVector3(this System.Numerics.Vector3 vec) =>
            new(vec.X, vec.Y, vec.Z);
    }

    #endregion
}