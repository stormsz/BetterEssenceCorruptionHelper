using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using SharpDX;
using System.Text;
using Vector2 = System.Numerics.Vector2;

namespace BetterEssenceCorruptionHelper;

public class BetterEssenceCorruptionHelper : BaseSettingsPlugin<Settings>
{
    private IngameState? ingameState;
    private Camera? camera;

    private readonly Dictionary<long, EssenceEntityData> trackedEntities = [];
    private int entityIdCounter = 0;

    private bool AnyDebugEnabled => Settings.ShowDebugInfo.Value;


    private readonly HashSet<string> discoveredEssences = [];
    private readonly HashSet<string> shouldCorruptEssences = [];
    private readonly HashSet<string> successfullyCorrupted = [];

    // Essences that should have been corrupted but were killed without corrupting
    private readonly HashSet<string> missedCorruptions = []; 

    public override bool Initialise()
    {
        ingameState = GameController.Game.IngameState;
        Name = "Better Essence Corruption Helper";
        DebugWindow.LogMsg($"{Name} initialized", 2, Color.Green);

        return base.Initialise();
    }

    public override void AreaChange(AreaInstance area)
    {
        if (AnyDebugEnabled)
        {
            var currentArea = GameController.Game.IngameState.Data.CurrentArea;
            var newArea = area?.DisplayName ?? "Unknown";
            DebugWindow.LogMsg($"Area change detected: {currentArea} -> {newArea}", 2, Color.Cyan);
        }

        trackedEntities.Clear();
        discoveredEssences.Clear();
        shouldCorruptEssences.Clear();
        successfullyCorrupted.Clear();
        missedCorruptions.Clear();

        entityIdCounter = 0;
    }

    public override Job? Tick()
    {
        if (!Settings.Enable || ingameState?.Camera == null)
            return null;

        camera = ingameState.Camera;

        if (GameController.Game.IsEscapeState ||
            GameController.Game.IsLoading ||
            GameController.Game.IsLoadingState)
            return null;

        ProcessMonolithEntities();
        return null;
    }

    private void ProcessMonolithEntities()
    {
        if (camera == null) return;

        var currentMonoliths = new HashSet<long>();
        var currentPositions = new HashSet<string>();

        foreach (var entity in GameController.Entities)
        {
            if (!IsValidMonolith(entity)) continue;

            currentMonoliths.Add(entity.Address);
            var positionKey = GetPositionKey(entity);
            currentPositions.Add(positionKey);

            if (!trackedEntities.TryGetValue(entity.Address, out var data))
            {
                data = new EssenceEntityData
                {
                    EntityId = ++entityIdCounter,
                    Address = entity.Address,
                    FirstSeenAddress = entity.Address
                };
                trackedEntities[entity.Address] = data;
            }

            UpdateEntityData(entity, data);

            // Update global discovery tracking with persistent position-based ID
            UpdateGlobalDiscovery(entity, data);
        }

        // Only check for missed corruptions on entities that are actually gone (not just unloaded)
        // We track by both address and position to handle entity replacement
        var removedEntities = new List<(long address, EssenceEntityData data)>();

        foreach (var (address, data) in trackedEntities)
        {
            // Entity is considered "removed" if:
            // It's not in current entities AND
            // Its position is not in current positions (meaning it's not just unloaded but actually gone)
            if (!currentMonoliths.Contains(address) && !currentPositions.Contains(GetPositionKeyFromData(data)))
            {
                removedEntities.Add((address, data));
            }
        }

        foreach (var (address, data) in removedEntities)
        {
            var positionKey = GetPositionKeyFromData(data);

            // Check if this was an essence that should have been corrupted and wasn't corrupted
            if (shouldCorruptEssences.Contains(positionKey) &&
                !data.Analysis.IsCorrupted &&
                !successfullyCorrupted.Contains(positionKey))
            {
                // This essence was in the "should corrupt" list but was removed without being corrupted
                // AND we haven't already marked it as successfully corrupted
                if (!missedCorruptions.Contains(positionKey))
                {
                    missedCorruptions.Add(positionKey);
                    if (AnyDebugEnabled)
                    {
                        DebugWindow.LogMsg($"MISSED CORRUPTION: Essence {data.EntityId} at {positionKey} was ShouldCorrupt but killed without corrupting", 1, Color.Orange);
                    }
                }
            }

            trackedEntities.Remove(address);
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

        // safety check - ensure entity has a position
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
        // Use rounded position as persistent identifier
        // TODO: Not sure if theres a better way to do this.
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
        data.LastKnownPosition = entity.PosNum;

        // Track all discovered essences in this map
        discoveredEssences.Add(positionKey);

        // Track essences that should be corrupted (for the entire map)
        if (data.State == EssenceState.ShouldCorrupt)
        {
            shouldCorruptEssences.Add(positionKey);

            // Remove from missed if it's now should corrupt
            missedCorruptions.Remove(positionKey);
        }
    }

    private void UpdateEntityData(Entity entity, EssenceEntityData data)
    {
        if (camera == null) return;

        var label = FindLabelForEntity(entity);
        if (label == null)
        {
            data.Label = null;
            return;
        }

        try
        {
            var worldPos = entity.PosNum;
            data.ScreenPosition = camera.WorldToScreen(worldPos.Translate(0, 0, -160));
            data.LastKnownPosition = worldPos;

            data.Label = label;
            var newAnalysis = EssenceLabelAnalyzer.Analyze(label);

            var previousWasCorrupted = data.Analysis.IsCorrupted;

            // Store previous analysis only when corruption occurs
            if (data.Analysis.IsValid && newAnalysis.IsValid && !data.Analysis.IsCorrupted && newAnalysis.IsCorrupted)
            {
                data.PreviousAnalysis = data.Analysis;

                // Mark this position as successfully corrupted
                var positionKey = GetPositionKey(entity);
                successfullyCorrupted.Add(positionKey);

                // Remove from missed corruptions if it was incorrectly marked
                missedCorruptions.Remove(positionKey);

                if (AnyDebugEnabled)
                {
                    DebugWindow.LogMsg($"SUCCESSFUL CORRUPTION: Essence {data.EntityId} at {positionKey} was corrupted", 1, Color.LightGreen);
                }
            }

            data.Analysis = newAnalysis;
            data.State = DetermineEssenceState(data.Analysis);

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

    private LabelOnGround? FindLabelForEntity(Entity entity)
    {
        return GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
            .FirstOrDefault(x => x.ItemOnGround?.Address == entity.Address);
    }

    private static EssenceState DetermineEssenceState(EssenceAnalysis analysis)
    {
        if (analysis.IsCorrupted)
            return EssenceState.ShouldKill;

        if (analysis.HasValuablePattern)
            return EssenceState.ShouldCorrupt;

        return EssenceState.ShouldKill;
    }

    public override void Render()
    {
        if (!Settings.Enable || ingameState == null) return;

        if (AnyDebugEnabled)
        {
            DrawMapStatsWindow();
        }

        foreach (var data in trackedEntities.Values)
        {
            if (data.State == EssenceState.ShouldCorrupt && Settings.ShowCorruptMe.Value)
            {
                DrawEntityVisuals(data, isCorruptTarget: true);
            }
            else if (data.State == EssenceState.ShouldKill && Settings.ShowKillReady.Value)
            {
                DrawEntityVisuals(data, isCorruptTarget: false);
            }

            if (AnyDebugEnabled && data.Label?.Label != null)
            {
                DrawEntityDebugWindow(data);
            }
        }
    }

    private void DrawMapStatsWindow()
    {
        // Count current corrupted essences
        var currentCorruptedCount = trackedEntities.Values.Count(data => data.Analysis.IsValid && data.Analysis.IsCorrupted);

        // Count current should-corrupt essences
        var currentShouldCorruptCount = trackedEntities.Values.Count(data => data.State == EssenceState.ShouldCorrupt);

        var statsLines = new List<(string text, Color color)>
        {
            ($"Total Found: {discoveredEssences.Count}", Color.White),
            ($"Should Corrupt: {shouldCorruptEssences.Count}", Color.Red),
            ("", Color.White),
            ("CURRENT STATE", Color.Yellow),
            ($"Active Entities: {trackedEntities.Count}", Color.White),
            ($"Live Should Corrupt: {currentShouldCorruptCount}", Color.Red),
            ($"Live Corrupted: {currentCorruptedCount}", Color.LightGreen),
            ("", Color.White),
            ($"Successfully Corrupted: {successfullyCorrupted.Count}", Color.LightGreen),
            ($"Missed Corruptions: {missedCorruptions.Count}", Color.Orange),
            ("", Color.White)
        };

        float maxWidth = 0;
        using (Graphics.SetTextScale(1.1f))
        {
            foreach (var (text, color) in statsLines)
            {
                var width = Graphics.MeasureText(text).X;
                if (width > maxWidth) maxWidth = width;
            }
        }

        // Position the stats window
        var windowRect = GameController.Window.GetWindowRectangle();
        var statsRect = new RectangleF(
            windowRect.Width - maxWidth - 30,
            100,
            maxWidth + 20,
            10 + (statsLines.Count * 16)
        );

        if (Settings.DebugBackgroundEnabled.Value)
        {
            var bgColor = Settings.DebugBackgroundColor.Value;
            bgColor.A = (byte)(Settings.DebugBackgroundOpacity.Value * 255);
            Graphics.DrawBox(statsRect, bgColor);
            Graphics.DrawFrame(statsRect, Settings.DebugBorderColor.Value, 1);
        }

        for (int i = 0; i < statsLines.Count; i++)
        {
            var linePos = new Vector2(statsRect.X + 5, statsRect.Y + 5 + (i * 16));
            using (Graphics.SetTextScale(1.1f))
            {
                var (text, color) = statsLines[i];
                Graphics.DrawText(text, linePos, color, FontAlign.Left);
            }
        }
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

        if (Settings.DebugBackgroundEnabled.Value)
        {
            var bgColor = Settings.DebugBackgroundColor.Value;
            bgColor.A = (byte)(Settings.DebugBackgroundOpacity.Value * 255);
            Graphics.DrawBox(debugRect, bgColor);
            Graphics.DrawFrame(debugRect, Settings.DebugBorderColor.Value, 1);
        }

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
        float thickness = Settings.BorderThickness.Value;
        float margin = Settings.BorderMargin.Value;

        var borderRect = new RectangleF(
            rect.X - thickness / 2f + margin,
            rect.Y - thickness / 2f,
            Math.Max(rect.Width + thickness - margin * 2, 10f),
            Math.Max(rect.Height + thickness, 10f)
        );

        return borderRect;
    }

    private List<(string text, Color color)> BuildDebugContent(EssenceEntityData data)
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

    private void DrawEntityVisuals(EssenceEntityData data, bool isCorruptTarget)
    {
        if (data.Label?.Label == null) return;

        var rect = data.Label.Label.GetClientRectCache;

        if (Settings.DrawBorder.Value)
        {
            var color = isCorruptTarget
                ? Settings.CorruptMeBorderColor.Value
                : Settings.KillReadyBorderColor.Value;

            DrawBorder(rect, color);
        }

        if (Settings.DrawText.Value)
        {
            var text = isCorruptTarget ? "CORRUPT" : "KILL";
            var textColor = isCorruptTarget
                ? Settings.CorruptMeTextColor.Value
                : Settings.KillReadyTextColor.Value;

            DrawText(rect, text, textColor);
        }
    }

    private void DrawBorder(RectangleF rect, Color color)
    {
        var borderRect = GetBorderRect(rect);
        Graphics.DrawFrame(borderRect, color, (int)Settings.BorderThickness.Value);
    }

    private void DrawText(RectangleF rect, string text, Color color)
    {
        try
        {
            using (Graphics.SetTextScale(Settings.TextSize.Value))
            {
                var textPos = new Vector2(
                    rect.X + rect.Width / 2,
                    rect.Y - Settings.TextSize.Value * 25 + 15
                );

                var textSize = Graphics.MeasureText(text);
                var bgRect = new RectangleF(
                    textPos.X - textSize.X / 2 - 2,
                    textPos.Y - 2,
                    textSize.X + 4,
                    textSize.Y + 4
                );

                Graphics.DrawBox(bgRect, new Color(0, 0, 0, 150));
                Graphics.DrawText(text, textPos, color, FontAlign.Center);
            }
        }
        catch (Exception ex)
        {
            if (AnyDebugEnabled)
                DebugWindow.LogError($"DrawText failed: {ex.Message}");
        }
    }
}

internal class EssenceEntityData
{
    public long Address { get; set; }
    public long FirstSeenAddress { get; set; }
    public int EntityId { get; set; }
    public System.Numerics.Vector2 ScreenPosition { get; set; }
    public LabelOnGround? Label { get; set; }
    public EssenceAnalysis Analysis { get; set; }
    public EssenceAnalysis PreviousAnalysis { get; set; }
    public EssenceState State { get; set; }
    public EssenceState PreviousState { get; set; }
    public System.Numerics.Vector3? LastKnownPosition { get; set; }
}

internal enum EssenceState
{
    None,
    ShouldCorrupt,
    ShouldKill
}