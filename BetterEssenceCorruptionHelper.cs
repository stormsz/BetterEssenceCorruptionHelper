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
using ImGuiNET;

namespace BetterEssenceCorruptionHelper
{
    public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
    {
        #region Constants

        // Essence tracking constants
        private const int ESSENCE_UNLOAD_DISTANCE = 120; // Distance at which PoE unloads entities
        private const int POSITION_TOLERANCE = 2; // Position tolerance for essence relinking
        private const int STATS_UPDATE_MS = 1000; // How often to update statistics
        private const int ENTITY_PROCESS_MS = 50; // How often to process entities
        private const int UI_UPDATE_FRAMES = 2; // How often to update UI elements
        private const int COMPLETED_BUFFER_SIZE = 100; // Size of completed essences buffer

        #endregion

        #region Fields

        // Entity tracking
        private readonly Dictionary<long, EssenceEntityData> _trackedEntities = [];
        private readonly CircularBuffer<EssenceEntityData> _completedEssences;
        private readonly MapStatistics _mapStats = new();
        private readonly Runner _pluginRunner = new("BetterEssenceCorruptionHelper");

        // Coroutines for background processing
        private readonly WaitTime _statsUpdateWait;
        private readonly WaitTime _entityProcessWait;
        private readonly WaitRender _uiUpdateWait;

        private Coroutine? _statsUpdateCoroutine;
        private Coroutine? _entityProcessingCoroutine;
        private Coroutine? _uiUpdateCoroutine;

        // State management
        private int _entityIdCounter; // Unique ID counter for each essence
        private string _cachedSessionStatsText = "";

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
            // Create coroutines for different processing tasks
            _statsUpdateCoroutine = new Coroutine(StatsUpdateRoutine(), this, "StatsUpdate");
            _entityProcessingCoroutine = new Coroutine(EntityProcessingRoutine(), this, "EntityProcessing");
            _uiUpdateCoroutine = new Coroutine(UIUpdateRoutine(), this, "UIUpdate");

            // Set priorities: entity processing is critical, UI updates are high priority
            _entityProcessingCoroutine.Priority = CoroutinePriority.Critical;
            _uiUpdateCoroutine.Priority = CoroutinePriority.High;
            _statsUpdateCoroutine.Priority = CoroutinePriority.Normal;

            // Add coroutines to runner for execution
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
            // Clear all tracking data when area changes
            _trackedEntities.Clear();
            _completedEssences.Clear();
            _entityIdCounter = 0;
            _mapStats.Reset();
            UpdateSessionStatsCache();
        }

        public override Job? Tick()
        {
            // Update coroutine runner each frame
            _pluginRunner.Update();
            return null;
        }

        public override void Render()
        {
            if (!ShouldRender())
                return;

            // Draw all UI elements
            DrawEssenceIndicators(); // Red/green status boxes and text
            DrawMapStatsWindow(); // Map statistics window
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
                // Iterate through all entities to find essence monoliths
                foreach (var entity in GameController.Entities)
                {
                    if (!IsValidMonolith(entity))
                        continue;

                    var data = GetOrCreateEntityData(entity, currentMonoliths);
                    currentMonoliths.Add(entity.Address);

                    if (data.Label != null)
                        UpdateEntityData(entity, data);

                    // Cache position for relinking after unload
                    data.LastKnownPosition = ToSharpDXVector3(entity.PosNum);
                }

                CleanupOldEssences(currentMonoliths);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"ProcessEssences failed: {ex.Message}");
            }
        }

        private EssenceEntityData GetOrCreateEntityData(Entity entity, HashSet<long> currentMonoliths)
        {
            // Check if we're already tracking this entity
            if (_trackedEntities.TryGetValue(entity.Address, out var data))
            {
                data.Entity = entity;
                return data;
            }

            var entityPos = ToSharpDXVector3(entity.PosNum);

            // Try to find an essence that unloaded and reloaded (same position, different address)
            var existingData = FindUnloadedEssenceAtPosition(entityPos, currentMonoliths);

            if (existingData != null)
                return RelinkEssence(existingData, entity);

            // It's a new essence - create fresh tracking data
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

            if (Settings.Debug.ShowDebugInfo.Value)
            {
                DebugWindow.LogMsg($"Essence Relinked: {existingData.EntityId} (Addr changed)", 3, SharpDX.Color.Cyan);
            }

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
                DebugWindow.LogMsg($"Essence {data.EntityId}: {oldState} -> {newState}");
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

            if (Settings.Debug.ShowDebugInfo.Value)
            {
                DebugWindow.LogMsg(
                    $"Essence {data.EntityId} (0x{entity.Address:X}) corrupted. Old state: {oldState}, New state: {newState}", 3, SharpDX.Color.Yellow);
            }

            // Track outcomes based on previous state
            if (oldState == EssenceState.ShouldCorrupt)
            {
                _mapStats.IncrementCorrupted();

                if (Settings.Debug.ShowDebugInfo.Value)
                {
                    DebugWindow.LogMsg($"SUCCESSFUL CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X})", 3, SharpDX.Color.LightGreen);
                }
            }
            else if (oldState == EssenceState.ShouldKill)
            {
                data.MistakenCorruption = true;
                _mapStats.IncrementMistakes();

                if (Settings.Debug.ShowDebugInfo.Value)
                {
                    DebugWindow.LogMsg($"MISTAKEN CORRUPTION: Essence {data.EntityId} (0x{entity.Address:X}) should have been killed!", 3, SharpDX.Color.OrangeRed);
                }
            }
        }

        private void CleanupOldEssences(HashSet<long> currentMonoliths)
        {
            var player = GameController.Player;
            if (player == null)
                return;

            var playerPos = ToSharpDXVector3(player.PosNum);
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

            // Remove killed essences from tracking
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
                DebugWindow.LogMsg($"MISSED CORRUPTION: Essence {data.EntityId} (0x{address:X}) should have been corrupted but was killed", 3, SharpDX.Color.Orange);
            }

            _completedEssences.PushBack(data);
        }

        private static bool IsMissedCorruption(EssenceEntityData data) =>
            data.State == EssenceState.ShouldCorrupt &&
            !data.Analysis.IsCorrupted &&
            !data.WasCorruptedByPlayer;

        private void UpdateEntityLabels()
        {
            // Update labels for all tracked entities
            foreach (var data in _trackedEntities.Values)
            {
                data.Label ??= FindLabelForEntity(data.Entity!);
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

        private void DrawEssenceIndicators()
        {
            if (!Settings.Indicators.EnableAllIndicators.Value)
                return;

            // Draw indicators for each tracked essence
            foreach (var data in _trackedEntities.Values)
            {
                DrawEssenceIndicator(data);

                // Draw debug window if enabled
                if (Settings.Debug.ShowDebugInfo.Value && data.Label?.Label != null)
                    DrawEssenceDebugWindow(data);
            }
        }

        private void DrawEssenceIndicator(EssenceEntityData data)
        {
            var isCorruptTarget = data.State == EssenceState.ShouldCorrupt;

            // Draw corrupt-me indicators (red border/text)
            if (isCorruptTarget && Settings.Indicators.CorruptMe.ShowCorruptMe.Value)
            {
                if (Settings.Indicators.CorruptMe.DrawBorder.Value)
                    DrawStatusBox(data, true);
                if (Settings.Indicators.CorruptMe.DrawText.Value)
                    DrawStatusText(data, true);
            }
            // Draw kill-ready indicators (green border/text)
            else if (data.State == EssenceState.ShouldKill && Settings.Indicators.KillReady.ShowKillReady.Value)
            {
                if (Settings.Indicators.KillReady.DrawBorder.Value)
                    DrawStatusBox(data, false);
                if (Settings.Indicators.KillReady.DrawText.Value)
                    DrawStatusText(data, false);
            }
        }

        private void DrawStatusBox(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var indicatorSettings = GetIndicatorSettings(isCorruptTarget);

            // Draw directly to the ImGui background draw list for better performance
            var drawList = ImGui.GetBackgroundDrawList();

            // Create border rectangle with hardcoded offsets
            // X+40 and width-80 creates a 40px margin on each side of the essence label
            var borderRect = new RectangleF(
                rect.X + 40,
                rect.Y,
                rect.Width - 80,
                rect.Height
            );

            // Convert colors for ImGui
            var borderColor = ToImguiVec4(indicatorSettings.BorderColor);

            // Define rectangle corners for drawing
            var min = new Vector2(borderRect.X, borderRect.Y);
            var max = new Vector2(borderRect.Right, borderRect.Bottom);

            // Draw background fill if enabled in settings
            if (isCorruptTarget && Settings.Indicators.CorruptMe.BackgroundFill.Value)
            {
                var fillColor = ToImguiVec4(new SharpDX.Color(
                    (byte)255,
                    (byte)0,
                    (byte)0,
                    (byte)(Settings.Indicators.CorruptMe.BackgroundOpacity.Value * 255)));
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor));
            }
            else if (!isCorruptTarget && Settings.Indicators.KillReady.BackgroundFill.Value)
            {
                var fillColor = ToImguiVec4(new SharpDX.Color(
                    (byte)0,
                    (byte)255,
                    (byte)0,
                    (byte)(Settings.Indicators.KillReady.BackgroundOpacity.Value * 255)));
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(fillColor));
            }

            // Draw border (2 pixels thick)
            drawList.AddRect(min, max, ImGui.GetColorU32(borderColor), 0.0f, ImDrawFlags.None, 2.0f);
        }

        private void DrawStatusText(EssenceEntityData data, bool isCorruptTarget)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var indicatorSettings = GetIndicatorSettings(isCorruptTarget);
            var text = isCorruptTarget ? "CORRUPT" : "KILL";

            // Position text 25 pixels above the essence label
            var textPos = new Vector2(rect.Center.X, rect.Top - 25);

            // Draw directly to ImGui background draw list
            var drawList = ImGui.GetBackgroundDrawList();

            // Calculate text size for positioning
            var textSize = ImGui.CalcTextSize(text);
            var padding = 5f;

            // Draw background rectangle for text (black with transparency)
            var bgMin = new Vector2(textPos.X - textSize.X / 2 - padding, textPos.Y - padding);
            var bgMax = new Vector2(textPos.X + textSize.X / 2 + padding, textPos.Y + textSize.Y + padding);

            var bgColor = ToImguiVec4(new SharpDX.Color(0, 0, 0, 200));
            drawList.AddRectFilled(bgMin, bgMax, ImGui.GetColorU32(bgColor), 5.0f); // 5px rounded corners

            // Draw the text itself
            var textColor = ToImguiVec4(indicatorSettings.TextColor);
            var textDrawPos = new Vector2(textPos.X - textSize.X / 2, textPos.Y);
            drawList.AddText(textDrawPos, ImGui.GetColorU32(textColor), text);
        }

        private void DrawEssenceDebugWindow(EssenceEntityData data)
        {
            if (data.Label?.Label == null)
                return;

            var rect = data.Label.Label.GetClientRectCache;
            var debugLines = BuildDebugContent(data);

            // Position debug window flush to the right side of the status box
            // Status box is at rect.X + 40 with width rect.Width - 80
            // So right edge is at: rect.X + 40 + (rect.Width - 80) = rect.Right - 40
            var debugWindowPos = new Vector2(rect.Right - 40, rect.Top);

            // Create unique window name for each essence
            var windowName = $"###EssenceDebug_{data.EntityId}";
            
            // Window flags to make it non-interactive and fixed position
            var windowFlags = ImGuiWindowFlags.NoDecoration |
                             ImGuiWindowFlags.NoMove |
                             ImGuiWindowFlags.NoSavedSettings |
                             ImGuiWindowFlags.NoFocusOnAppearing |
                             ImGuiWindowFlags.NoInputs;

            var bgColor = Settings.Debug.DebugBackgroundColor.Value;
            bgColor.A = (byte)(Settings.Debug.DebugBackgroundOpacity.Value * 255);

            // Set window styles
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ToImguiVec4(bgColor));
            ImGui.PushStyleColor(ImGuiCol.Border, ToImguiVec4(Settings.Debug.DebugBorderColor.Value));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);

            // Calculate window size based on content
            var maxLineWidth = 0f;
            for (var i = 0; i < debugLines.Count; i++)
            {
                var lineWidth = 0f;
                foreach (var (text, _) in debugLines[i])
                {
                    lineWidth += ImGui.CalcTextSize(text).X;
                }
                if (lineWidth > maxLineWidth)
                    maxLineWidth = lineWidth;
            }

            // Set window dimensions (minimum width 260px)
            var windowSize = new Vector2(Math.Max(260, maxLineWidth + 15), 10 + (debugLines.Count * 16));

            ImGui.SetNextWindowPos(debugWindowPos);
            ImGui.SetNextWindowSize(windowSize);

            // Draw the debug window
            if (ImGui.Begin(windowName, windowFlags))
            {
                // Draw each line of debug information
                for (var i = 0; i < debugLines.Count; i++)
                {
                    var xPos = 5f;
                    foreach (var (text, color) in debugLines[i])
                    {
                        ImGui.SetCursorPos(new Vector2(xPos, 5 + (i * 16)));
                        ImGui.PushStyleColor(ImGuiCol.Text, ToImguiVec4(color));
                        ImGui.Text(text);
                        ImGui.PopStyleColor();
                        xPos += ImGui.CalcTextSize(text).X;
                    }
                }

                ImGui.End();
            }

            // Restore ImGui styles
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
        }

        #endregion

        #region Helpers

        private EssenceIndicatorSettings GetIndicatorSettings(bool isCorruptTarget)
        {
            // Get settings based on whether this is a corrupt-me or kill-ready essence
            dynamic settings = isCorruptTarget
                ? Settings.Indicators.CorruptMe
                : Settings.Indicators.KillReady;

            return new EssenceIndicatorSettings
            {
                BorderColor = settings.BorderColor.Value,
                TextColor = settings.TextColor.Value
            };
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

            lines.Add([]); // Empty line for spacing
        }

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

        private void DrawMapStatsWindow()
        {
            if (!Settings.MapStats.ShowMapStats.Value ||
                (GameController.Area.CurrentArea.IsPeaceful && !Settings.MapStats.ShowInTownHideout.Value))
                return;

            // Set window colors
            ImGui.PushStyleColor(ImGuiCol.TitleBg, ToImguiVec4(Settings.MapStats.TitleBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ToImguiVec4(Settings.MapStats.TitleBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ToImguiVec4(Settings.MapStats.ContentBackground.Value));
            ImGui.PushStyleColor(ImGuiCol.Border, ToImguiVec4(Settings.MapStats.BorderColor.Value));
            ImGui.PushStyleColor(ImGuiCol.Text, ToImguiVec4(Settings.MapStats.TitleColor.Value));

            if (ImGui.Begin("Essence Map Stats"))
            {
                // Draw the Content
                var statColor = ToImguiVec4(Settings.MapStats.TextColor.Value);

                // Draw header
                ImGui.TextColored(ToImguiVec4(SharpDX.Color.White), "Map Totals");
                ImGui.Separator();

                // Draw statistics
                ImGui.TextColored(statColor, $"  Killed: {_mapStats.TotalKilled}");
                ImGui.TextColored(statColor, $"  Corrupted: {_mapStats.TotalCorrupted}");
                ImGui.TextColored(statColor, $"  Missed: {_mapStats.TotalMissed}");
                ImGui.TextColored(statColor, $"  Mistakes: {_mapStats.TotalMistakes}");

                ImGui.End();
            }

            // Restore colors
            ImGui.PopStyleColor(5);
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
        }

        #endregion

        #region Static Helpers

        private static System.Numerics.Vector4 ToImguiVec4(SharpDX.Color color)
        {
            return new System.Numerics.Vector4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                color.A / 255f
            );
        }

        private static SharpDX.Vector3 ToSharpDXVector3(System.Numerics.Vector3 vec)
        {
            return new SharpDX.Vector3(vec.X, vec.Y, vec.Z);
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
        public Color TextColor { get; init; }
    }
    #endregion
}