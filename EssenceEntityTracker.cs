using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Models;
using BetterEssenceCorruptionHelper.Pricing;
using BetterEssenceCorruptionHelper.Strategy;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Vector2 = System.Numerics.Vector2;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Handles entity tracking and processing for essence monoliths.
    /// Manages the lifecycle of essence entities from detection to opening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Update path, all on the main thread:
    /// <list type="number">
    /// <item>Discovery is event driven. <see cref="OnEntityAdded"/>/<see cref="OnEntityRemoved"/> keep the
    /// set of loaded monolith entities, so the tracker never walks the whole entity list per pass
    /// (earlier versions iterated every entity 20 times a second, even in maps without essences). A
    /// one-off seed scan covers entities that existed before the plugin was enabled or before the area
    /// change was seen, and a reseed once a second (1/20 of the old scan rate) guards against a missed event.</item>
    /// <item><see cref="Tick"/> runs every frame: monolith state (opened?) for the handful of
    /// monoliths, label resolution only while a tracked essence needs one, label analysis only when
    /// its line count changed (a Vaal Orb adds the "Corrupted" line) or at the old 50ms cadence, and
    /// the recommendation right after. So a corruption shows up on the next frame instead of the next
    /// 50ms scan.</item>
    /// <item>The monster pass (linking the trapped monster, counting its kill) walks monsters only, at the
    /// old 50ms cadence, and only while a monolith still needs a monster linked or watched.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Initializes a new instance of the EssenceEntityTracker class.
    /// </para>
    /// </remarks>
    /// <param name="gameController">The game controller instance</param>
    /// <param name="settings">Plugin settings</param>
    /// <param name="mapStats">Statistics tracker</param>
    /// <param name="corruptionLog">Observed corruption outcomes</param>
    /// <param name="prices">Price snapshot (Vaal Orb cost of observed corruptions)</param>
    /// <param name="values">Essence values used to value observed corruptions</param>
    /// <param name="setup">Map setup read from the game, recorded with each corruption</param>
    /// <param name="outcomeCsvPath">Where to append observed corruptions, or null when recording is off</param>
    internal class EssenceEntityTracker(
        GameController gameController,
        Settings settings,
        MapStatistics mapStats,
        CorruptionLog corruptionLog,
        PriceBook prices,
        EssenceValues values,
        SetupDetector setup,
        Func<string?> outcomeCsvPath)
    {
        #region Constants

        /// <summary>Interval of the monster pass (trapped-monster linking and kill detection), ms - the old scan rate</summary>
        private const int MONSTER_PASS_MS = 50;

        /// <summary>Re-read a label's text at least this often even if its line count didn't change, ms - the old scan rate</summary>
        private const int ANALYSIS_REFRESH_MS = 50;

        /// <summary>Minimum gap between visible-label list scans while a tracked essence is missing its label, ms (~2 frames, as before)</summary>
        private const int LABEL_SCAN_MIN_MS = 33;

        /// <summary>Safety re-scan of the label list while every tracked essence has a visible label, ms</summary>
        private const int LABEL_SCAN_SAFETY_MS = 250;

        /// <summary>Safety re-seed of monolith candidates from the full entity list, ms (1/20 of the old scan rate)</summary>
        private const int RESEED_MS = 1000;

        /// <summary>Screen margin (px) within which a monolith's ground label may be visible</summary>
        private const float LABEL_SCREEN_MARGIN = 300f;

        /// <summary>The same tracking error is logged at most this often, ms</summary>
        private const int ERROR_LOG_INTERVAL_MS = 5000;

        /// <summary>Max grid distance between a monolith and the trapped monster linked to it</summary>
        private const float MONSTER_LINK_RADIUS = 15f;

        /// <summary>
        /// How long a released monster must be missing from the entity list before it counts as
        /// gone - the list can drop an entity for a pass or two while it is rebuilt.
        /// </summary>
        private const int MONSTER_VANISH_GRACE_MS = 1000;

        /// <summary>
        /// A released monster that vanishes this close to the player (grid units) died and left no
        /// corpse; one that vanishes further away most likely just left the loaded area.
        /// </summary>
        private const float MONSTER_VANISH_KILL_DISTANCE = 60f;

        /// <summary>Diagnostics summaries are written at most this often, ms</summary>
        private const int DIAGNOSTIC_SUMMARY_MS = 1000;

        #endregion

        #region Fields

        private readonly GameController _gameController = gameController;
        private readonly Settings _settings = settings;
        private readonly MapStatistics _mapStats = mapStats;
        private readonly CorruptionLog _corruptionLog = corruptionLog;
        private readonly PriceBook _prices = prices;
        private readonly EssenceValues _values = values;
        private readonly SetupDetector _setup = setup;
        private readonly Func<string?> _outcomeCsvPath = outcomeCsvPath;

        /// <summary>
        /// Every essence monolith seen in the current area, keyed by game entity ID.
        ///
        /// The ID is what ExileCore itself keys entities by, and it survives an essence leaving and
        /// re-entering the loaded area, so entries are simply kept until the area changes - no
        /// address relinking or "was it killed or unloaded?" guesswork.
        ///
        /// A plain Dictionary is enough: Tick, Render and the entity events all run on the main thread.
        /// </summary>
        private readonly Dictionary<uint, EssenceEntityData> _trackedEntities = new();

        /// <summary>Loaded entities carrying a Monolith component, by ID (EntityAdded/EntityRemoved + seeding).</summary>
        private readonly Dictionary<uint, Entity> _monoliths = new();

        /// <summary>Display number counter. Survives hideout trips; restarts with each new map.</summary>
        private int _entityIdCounter;

        /// <summary>Visible monolith labels by entity ID, rebuilt on every label scan (reused to avoid allocating).</summary>
        private readonly Dictionary<uint, LabelOnGround> _labelsById = new();

        /// <summary>Tracked essences by the entity ID of their linked trapped monster.</summary>
        private readonly Dictionary<uint, EssenceEntityData> _byMonsterId = new();

        /// <summary>Unreleased essences whose trapped monster can still be (re)linked (rebuilt each pass).</summary>
        private readonly List<EssenceEntityData> _unreleased = new();

        /// <summary>Best imprisoned monster seen this pass for each unreleased essence, by essence entity ID.</summary>
        private readonly Dictionary<uint, MonsterCandidate> _bestCandidates = new();

        /// <summary>Cached MonsterInsideMonolith reads by monster ID, so stats aren't re-read every pass.</summary>
        private readonly Dictionary<uint, (bool Imprisoned, long CheckedAtMs)> _imprisonedChecks = new();

        /// <summary>How long a MonsterInsideMonolith read is trusted before it is read again (ms).</summary>
        private const int IMPRISONED_CHECK_TTL_MS = 1000;

        /// <summary>
        /// An imprisoned monster standing on an unreleased monolith. A monolith can hold a whole pack -
        /// the essence monster plus its adds - so the candidates are ranked: highest rarity first (the
        /// essence monster is rare, its adds are white), then closest to the monolith (it stands right
        /// on it, the adds around it).
        /// </summary>
        private readonly record struct MonsterCandidate(Entity Monster, int Rarity, float Distance)
        {
            public bool IsBetterThan(int rarity, float distance) =>
                Rarity > rarity || (Rarity == rarity && Distance < distance);
        }

        /// <summary>Linked monsters that came through the current monster pass.</summary>
        private readonly HashSet<uint> _monstersSeenThisPass = new();

        /// <summary>Released essences whose monster hasn't been seen dead yet (rebuilt each pass, diagnostics only).</summary>
        private readonly List<EssenceEntityData> _releasedAwaitingKill = new();

        /// <summary>Other monsters already reported near a released monolith, so each is logged once.</summary>
        private readonly HashSet<uint> _reportedNearbyMonsters = new();

        /// <summary>Monotonic clock for every interval here.</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _nextSeedMs;
        private long _nextMonsterPassMs;
        private long _nextLabelScanMs;
        private long _nextSafetyLabelScanMs;
        private bool _seededThisArea;

        /// <summary>(preferences, values, setup) versions the current recommendations were computed for.</summary>
        private (int, int, int) _evaluatedFor = (-1, -1, -1);

        private readonly TrackerPerformance _performance = new();

        private string? _lastError;
        private long _lastErrorLoggedMs = long.MinValue;

        // Diagnostics state (only used while Debug > Show Debug Info is on), so the log only gets
        // a line when something actually changes.
        private readonly HashSet<uint> _seenThisPass = new();
        private bool? _lastShouldProcess;
        private string? _lastScanSummary;
        private string? _lastLabelSummary;
        private long _nextDiagnosticSummaryMs;
        private int _validMonoliths, _rejectedMonoliths, _skippedUntargetable;

        #endregion

        #region Properties

        /// <summary>Enumerates currently tracked entities. Opened ones stay listed with State None.</summary>
        public IEnumerable<EssenceEntityData> TrackedEntities => _trackedEntities.Values;

        /// <summary>Work counters from the last full second (diagnostics).</summary>
        public TrackerPerformance Performance => _performance;

        public int CandidateMonolithCount => _monoliths.Count;

        #endregion

        #region Public Methods

        /// <summary>ExileCore EntityAdded: remember monoliths. Cheap - one component lookup per added entity.</summary>
        public void OnEntityAdded(Entity entity)
        {
            if (!IsMonolithEntity(entity))
                return;

            _monoliths[entity.Id] = entity;
            if (_trackedEntities.TryGetValue(entity.Id, out var data))
            {
                data.Entity = entity;
                data.IsLoaded = true;
            }
        }

        /// <summary>ExileCore EntityRemoved: the monolith left the loaded area. Its tracking data stays until the area changes.</summary>
        public void OnEntityRemoved(Entity entity)
        {
            if (entity == null || !_monoliths.Remove(entity.Id))
                return;

            if (_trackedEntities.TryGetValue(entity.Id, out var data))
            {
                data.IsLoaded = false;
                data.Label = null;
            }
        }

        /// <summary>Per-frame update. <paramref name="canTrack"/> is the shared tracking gate.</summary>
        public void Tick(bool canTrack)
        {
            var started = Stopwatch.GetTimestamp();
            var now = _clock.ElapsedMilliseconds;
            var diagnostics = DiagnosticsOn;

            if (diagnostics && canTrack != _lastShouldProcess)
            {
                LogDiagnostic(canTrack
                    ? "tracking active"
                    : $"tracking paused by: {GameStateGates.DescribeTrackingBlockers(_gameController, _settings)}");
            }
            _lastShouldProcess = canTrack;

            if (canTrack)
            {
                try
                {
                    if (now >= _nextSeedMs)
                        SeedMonoliths(now, diagnostics);

                    ProcessMonoliths(diagnostics);
                    ReevaluateIfInputsChanged();
                    RefreshLabels(now, diagnostics);
                    AnalyzeLabels(now);

                    if (now >= _nextMonsterPassMs)
                    {
                        _nextMonsterPassMs = now + MONSTER_PASS_MS;
                        ProcessMonsters(diagnostics);
                    }

                    if (diagnostics && now >= _nextDiagnosticSummaryMs)
                    {
                        _nextDiagnosticSummaryMs = now + DIAGNOSTIC_SUMMARY_MS;
                        LogScanDiagnostics();
                    }
                }
                catch (Exception ex)
                {
                    // Tick runs every frame: report a persistent failure every few seconds, not 60 times a second.
                    if (ex.Message != _lastError || now - _lastErrorLoggedMs >= ERROR_LOG_INTERVAL_MS)
                    {
                        DebugWindow.LogError($"Essence tracking failed: {ex.Message}");
                        _lastError = ex.Message;
                        _lastErrorLoggedMs = now;
                    }
                }
            }

            _performance.EndTick(Stopwatch.GetTimestamp() - started, now, _monoliths.Count, _trackedEntities.Count);
        }

        /// <summary>
        /// Clears tracking data when entering a new area. Entity and label references belong to
        /// the area being left, so they are always dropped; the display numbering only restarts on
        /// a new map, so a hideout trip doesn't renumber the essences you come back to.
        /// </summary>
        /// <param name="newMap">True when entering a different map instance than the last one</param>
        public void ResetState(bool newMap)
        {
            _trackedEntities.Clear();
            _monoliths.Clear();
            _byMonsterId.Clear();
            _reportedNearbyMonsters.Clear();
            _imprisonedChecks.Clear();

            // Seed again as soon as the new area is trackable: events that arrived before this call
            // belong to the new area too and were just cleared.
            _seededThisArea = false;
            _nextSeedMs = 0;
            _nextLabelScanMs = 0;
            _nextSafetyLabelScanMs = 0;

            if (newMap)
                _entityIdCounter = 0;
        }

        #endregion

        #region Entity Processing

        /// <summary>
        /// Adds any monolith in the entity list that the events didn't report. A full walk of the
        /// entity list, so it runs once per area and then only every <see cref="RESEED_MS"/>.
        /// </summary>
        private void SeedMonoliths(long now, bool diagnostics)
        {
            _nextSeedMs = now + RESEED_MS;
            var visited = 0;
            foreach (var entity in _gameController.Entities)
            {
                visited++;
                if (entity == null)
                    continue;

                // Known and still live: nothing to do. A different, dead Entity under the same ID (a
                // missed EntityRemoved) is replaced, or the monolith would stay invisible all area.
                if (_monoliths.TryGetValue(entity.Id, out var known) && (ReferenceEquals(known, entity) || IsEntityValid(known)))
                    continue;

                if (!IsMonolithEntity(entity))
                    continue;

                if (diagnostics && _seededThisArea)
                    LogDiagnostic($"reseed found monolith id={entity.Id} that EntityAdded did not report");

                OnEntityAdded(entity);
            }

            _seededThisArea = true;
            _performance.RecordSeed(visited);
        }

        /// <summary>
        /// Per-frame monolith pass over the known monoliths only: start tracking new ones and detect
        /// release (the monolith turning untargetable).
        /// </summary>
        private void ProcessMonoliths(bool diagnostics)
        {
            _validMonoliths = _rejectedMonoliths = _skippedUntargetable = 0;
            if (diagnostics)
                _seenThisPass.Clear();

            var playerAddress = _gameController.Player?.Address ?? 0;
            foreach (var (id, entity) in _monoliths)
            {
                _performance.MonolithChecks++;
                if (!IsValidMonolith(entity, playerAddress, out var monolith))
                {
                    _rejectedMonoliths++;
                    continue;
                }

                _validMonoliths++;
                var targetable = entity.IsTargetable;

                // "Opened" means the monster was released. On 3.29 the Monolith component's
                // IsOpened/OpenStage never change (confirmed with diagnostics), so the monolith
                // turning untargetable is the signal that actually fires.
                var isOpenedNow = !targetable;

                if (!_trackedEntities.TryGetValue(id, out var data))
                {
                    // Only start tracking monoliths that can still be interacted with. One that
                    // is already untargetable was opened before we got here (e.g. coming back
                    // from the hideout) and has nothing to offer.
                    if (!targetable)
                    {
                        _skippedUntargetable++;
                        continue;
                    }

                    data = CreateNewEssenceData(entity);
                }

                if (diagnostics)
                    _seenThisPass.Add(id);

                data.Entity = entity;
                data.IsLoaded = true;
                data.Address = entity.Address;
                data.OpenStage = monolith.OpenStage;
                data.GridPos = entity.GridPosNum;

                if (!data.IsOpened)
                {
                    if (!isOpenedNow)
                        data.WasSeenClosed = true;
                    else if (data.WasSeenClosed)
                        HandleEssenceOpened(data);
                }

                if (diagnostics)
                    LogSnapshotIfChanged(data, targetable);
            }
        }

        /// <summary>
        /// Creates new tracking data for a freshly discovered essence. Its label is picked up by the
        /// label scan this same frame.
        /// </summary>
        private EssenceEntityData CreateNewEssenceData(Entity entity)
        {
            var newData = new EssenceEntityData
            {
                Id = entity.Id,
                EntityId = ++_entityIdCounter,
                Address = entity.Address,
                Entity = entity,
                WasSeenClosed = true,
            };

            _trackedEntities[entity.Id] = newData;
            _nextLabelScanMs = 0;
            return newData;
        }

        /// <summary>
        /// Resolves ground labels for tracked, unreleased, loaded essences.
        ///
        /// A LabelOnGround that has dropped out of ItemsOnGroundLabelsVisible keeps serving its last
        /// known rect, so labels are re-resolved from the visible list rather than cached forever. The
        /// list is only walked while it can change the outcome: when a tracked essence that is on
        /// screen has no visible label (at most every <see cref="LABEL_SCAN_MIN_MS"/>), plus a safety
        /// re-check every <see cref="LABEL_SCAN_SAFETY_MS"/>. Earlier versions walked it every second
        /// frame for the rest of the map once any essence had been seen, on screen or not, released or not.
        /// </summary>
        private void RefreshLabels(long now, bool diagnostics)
        {
            var anyCandidate = false;
            var needsScan = false;
            var screen = new ScreenTest(_gameController);
            foreach (var data in _trackedEntities.Values)
            {
                if (data.IsOpened || !data.IsLoaded)
                {
                    data.Label = null;
                    continue;
                }

                anyCandidate = true;

                // A monolith whose position is off screen can't have a visible label, so there is
                // nothing to find for it - otherwise every unreleased essence out of view would keep
                // the label list being walked.
                if ((data.Label == null || !IsLabelVisible(data.Label)) && screen.MayShowLabel(data.Entity))
                    needsScan = true;
            }

            if (!anyCandidate || now < (needsScan ? _nextLabelScanMs : _nextSafetyLabelScanMs))
                return;

            _nextLabelScanMs = now + LABEL_SCAN_MIN_MS;
            _nextSafetyLabelScanMs = now + LABEL_SCAN_SAFETY_MS;

            // Read the visible-label list once for the whole scan; each access re-reads game memory.
            var labels = _gameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible;
            if (labels == null)
                return;

            _performance.LabelScans++;
            var countMonolithLabels = diagnostics && now >= _nextDiagnosticSummaryMs;
            var monolithLabels = 0;

            // One pass over the labels, indexing the ones that belong to tracked essences by entity
            // ID - the same key the tracking uses, and what AutoExile matches monolith labels on.
            _labelsById.Clear();
            for (var i = 0; i < labels.Count; i++)
            {
                var item = labels[i]?.ItemOnGround;
                if (item == null)
                    continue;

                _performance.LabelsVisited++;
                if (countMonolithLabels && item.HasComponent<Monolith>())
                    monolithLabels++;

                if (_trackedEntities.ContainsKey(item.Id))
                    _labelsById[item.Id] = labels[i];
            }

            foreach (var data in _trackedEntities.Values)
            {
                if (data.IsOpened || !data.IsLoaded)
                    continue;

                var label = _labelsById.GetValueOrDefault(data.Id);
                if (label?.Address != data.Label?.Address)
                    data.LabelSignature = -1; // a different label object: analyse it right away

                data.Label = label;
            }

            if (countMonolithLabels)
            {
                var summary = $"labels: {labels.Count} visible ground labels, {monolithLabels} on monoliths, {_labelsById.Count} matched to tracked essences";
                if (summary != _lastLabelSummary)
                {
                    LogDiagnostic(summary);
                    _lastLabelSummary = summary;
                }
            }
        }

        /// <summary>
        /// Re-reads labels whose content may have changed: immediately when the line count changed
        /// (or the label is new), otherwise every <see cref="ANALYSIS_REFRESH_MS"/>.
        /// </summary>
        private void AnalyzeLabels(long now)
        {
            foreach (var data in _trackedEntities.Values)
            {
                if (data.IsOpened || data.Label == null)
                    continue;

                var signature = EssenceLabelAnalyzer.ReadContentSignature(data.Label);
                if (signature == data.LabelSignature && now - data.LastAnalysisMs < ANALYSIS_REFRESH_MS)
                    continue;

                data.LabelSignature = signature;
                data.LastAnalysisMs = now;
                _performance.Analyses++;
                UpdateEntityData(data);
            }
        }

        /// <summary>
        /// Strategy, choices, prices (Ninja Price refresh) or the detected map setup changed: re-evaluate
        /// every live recommendation now.
        /// </summary>
        private void ReevaluateIfInputsChanged()
        {
            var preferences = _settings.Tracking;
            var inputs = (preferences.Version, _values.Version, _setup.Version);
            if (inputs == _evaluatedFor)
                return;

            _evaluatedFor = inputs;
            var profit = CurrentProfitContext();
            foreach (var data in _trackedEntities.Values)
            {
                if (!data.IsOpened && data.Analysis.IsValid)
                    ApplyRecommendation(data, StrategyEvaluator.Evaluate(data.Analysis, preferences, profit));
            }
        }

        private ProfitContext CurrentProfitContext() =>
            new(_values.ValueOf, _values.IsApproximate, _prices.VaalOrb.Value, _setup.Resolve(), _prices.Status == ProviderStatus.Ready);

        /// <summary>
        /// Updates essence state based on label analysis.
        /// </summary>
        private void UpdateEntityData(EssenceEntityData data)
        {
            if (data.Label == null)
                return;

            var newAnalysis = EssenceLabelAnalyzer.Analyze(data.Label);
            var previous = data.Analysis;

            // A label caught mid-update reads as invalid for a frame; keep the last good reading rather
            // than flickering the indicator.
            if (!newAnalysis.IsValid && previous.IsValid)
                return;

            var oldState = data.State;

            // Commit the new reading first, so a failure while handling a corruption below can't make
            // the next read see "uncorrupted -> corrupted" again and count it twice.
            if (!newAnalysis.SameContentAs(previous))
            {
                data.Analysis = newAnalysis;
                data.AnalysisVersion++;
            }

            // Detect corruption event:
            // 1. Label says corrupted (case-insensitive)
            // 2. OR the essences on the closed monolith changed (tiers increased, essence added, or transformed)
            var essencesChanged = previous.IsValid && previous.EssenceCount > 0 && newAnalysis.IsValid && newAnalysis.EssenceCount > 0 &&
                !newAnalysis.SameContentAs(previous);

            var isCorrupted = newAnalysis.IsCorrupted || essencesChanged;

            if (previous.IsValid && !previous.IsCorrupted && isCorrupted)
            {
                if (!newAnalysis.IsCorrupted)
                {
                    newAnalysis = new EssenceAnalysis(newAnalysis.IsValid, true, newAnalysis.Essences,
                        newAnalysis.UnrecognizedEssenceLines, newAnalysis.UnresolvedTypes);
                    data.Analysis = newAnalysis;
                }

                HandleEssenceCorruption(data, previous, newAnalysis, oldState);
            }

            ApplyRecommendation(data, StrategyEvaluator.Evaluate(data.Analysis, _settings.Tracking, CurrentProfitContext()));
        }

        private static void ApplyRecommendation(EssenceEntityData data, Recommendation recommendation)
        {
            var oldState = data.State;
            data.State = recommendation.State;
            data.StateReason = recommendation.Reason;

            if (recommendation.State != oldState)
                DebugWindow.LogMsg($"Essence {data.EntityId}: {oldState} -> {recommendation.State} ({recommendation.Reason})");
        }

        /// <summary>
        /// Handles essence corruption event. Staged as pending on the entity, committed only when the monster is killed.
        /// </summary>
        private void HandleEssenceCorruption(EssenceEntityData data, EssenceAnalysis before, EssenceAnalysis after, EssenceState oldState)
        {
            data.WasCorruptedByPlayer = true;
            data.PreviousAnalysis = before; // pre-corruption state for the debug comparison

            if (oldState == EssenceState.None && before.IsValid)
            {
                var rec = StrategyEvaluator.Evaluate(before, _settings.Tracking, CurrentProfitContext());
                oldState = rec.State;
            }

            var wasRecommended = oldState == EssenceState.ShouldCorrupt;
            data.PendingCorruption = new PendingCorruptionOutcome(before, after, wasRecommended, oldState);
            data.PendingMissed = false;

            if (_settings.Debug.ShowDebugInfo.Value)
            {
                DebugWindow.LogMsg(
                    $"Essence {data.EntityId} (0x{data.Address:X}) corrupted (pending kill). Recommended: {wasRecommended}", 3,
                    wasRecommended ? SharpDX.Color.LightGreen : SharpDX.Color.OrangeRed);
            }
        }

        /// <summary>Classifies what the Vaal Orb did and records it with the prices of the moment.</summary>
        private void RecordOutcome(EssenceEntityData data, EssenceAnalysis before, EssenceAnalysis after, bool wasRecommended)
        {
            var preferences = _settings.Tracking;
            var setup = _setup.Resolve();
            var outcome = OutcomeClassifier.Classify(before.Essences, after.Essences, out var ambiguous,
                CorruptionModel.ApplicableOutcomes(setup));

            var unpriced = before.UnrecognizedEssenceLines.Count + after.UnrecognizedEssenceLines.Count;
            double Value(IReadOnlyList<EssenceEntry> essences)
            {
                var sum = 0d;
                foreach (var e in essences)
                {
                    if (_values.ValueOf(e) is { } p)
                        sum += p;
                    else
                        unpriced++;
                }

                return sum;
            }

            var hasPrices = _prices.Status is ProviderStatus.Ready;
            var record = new CorruptionRecord(
                At: DateTime.UtcNow,
                Area: _gameController.Area.CurrentArea?.DisplayName ?? "",
                Before: before.Essences.Select(e => e.Name).Concat(before.UnrecognizedEssenceLines).ToArray(),
                After: after.Essences.Select(e => e.Name).Concat(after.UnrecognizedEssenceLines).ToArray(),
                Outcome: outcome,
                Ambiguous: ambiguous,
                StrategyId: preferences.StrategyId,
                Setup: setup,
                WasRecommended: wasRecommended,
                ValueBefore: hasPrices ? Value(before.Essences) : null,
                ValueAfter: hasPrices ? Value(after.Essences) : null,
                VaalOrbCost: _prices.VaalOrb.Value,
                UnpricedCount: unpriced);

            var error = _corruptionLog.Record(record, _outcomeCsvPath());
            if (error != null)
                DebugWindow.LogError($"[BECH] Could not append corruption outcome to CSV: {error}");

            if (_settings.Debug.ShowDebugInfo.Value)
            {
                var gain = record.RealizedGain is { } g ? $", {g:+0.##;-0.##}c at listing prices" : "";
                DebugWindow.LogMsg($"Essence {data.EntityId} corruption outcome: {outcome?.ToString() ?? "unclassified"}{(ambiguous ? " (ambiguous)" : "")} " +
                                   $"[{string.Join(", ", record.Before)}] -> [{string.Join(", ", record.After)}]{gain}", 5, SharpDX.Color.Cyan);
            }
        }

        /// <summary>
        /// Processes the monolith being opened: the monster is released and the chance to corrupt
        /// is gone, so a missed corruption is judged here. The kill itself is only counted once the
        /// released monster dies (see TrackMonster).
        /// </summary>
        private void HandleEssenceOpened(EssenceEntityData data)
        {
            if (data.IsOpened)
                return;

            data.IsOpened = true;

            // Check if player missed a corruption opportunity (must run before State is cleared)
            if (IsMissedCorruption(data))
            {
                data.PendingMissed = true;
                if (_settings.Debug.ShowDebugInfo.Value)
                    DebugWindow.LogMsg($"MISSED CORRUPTION: Essence {data.EntityId} (0x{data.Address:X}) opened uncorrupted (pending kill)", 3, SharpDX.Color.Orange);
            }
            else if (_settings.Debug.ShowDebugInfo.Value)
            {
                DebugWindow.LogMsg($"Essence {data.EntityId} released", 3, SharpDX.Color.Cyan);
            }

            if (data.MonsterId == null && DiagnosticsOn)
                LogDiagnostic($"#{data.EntityId} id={data.Id}: released with no linked monster - its kill can't be counted");

            // Nothing left to draw for an opened monolith.
            data.State = EssenceState.None;
            data.Label = null;
        }

        /// <summary>
        /// Monster pass: links trapped monsters to their monoliths and watches released ones for
        /// their death. Skipped entirely while no monolith needs a monster linked or watched.
        /// </summary>
        private void ProcessMonsters(bool diagnostics)
        {
            _monstersSeenThisPass.Clear();
            _unreleased.Clear();
            _bestCandidates.Clear();
            _releasedAwaitingKill.Clear();

            var watchingLinkedMonster = false;
            foreach (var tracked in _trackedEntities.Values)
            {
                if (!tracked.IsOpened && tracked.GridPos.HasValue)
                    _unreleased.Add(tracked);
                else if (diagnostics && tracked.IsOpened && !tracked.MonsterKilled && tracked.GridPos.HasValue)
                    _releasedAwaitingKill.Add(tracked);

                if (tracked.MonsterId != null && !tracked.MonsterKilled)
                    watchingLinkedMonster = true;
            }

            if (_unreleased.Count == 0 && _releasedAwaitingKill.Count == 0 && !watchingLinkedMonster)
                return;

            foreach (var entity in MonsterEntities())
            {
                if (entity?.Type != EntityType.Monster)
                    continue;

                _performance.MonsterVisits++;
                TrackMonster(entity);
            }

            ApplyBestMonsterLinks();
            HandleMissingMonsters();
        }

        /// <summary>Monsters only, from ExileCore's per-type index; the full entity list if that is unavailable.</summary>
        private IEnumerable<Entity> MonsterEntities()
        {
            var byType = _gameController.EntityListWrapper?.ValidEntitiesByType;
            if (byType != null && byType.TryGetValue(EntityType.Monster, out var monsters) && monsters != null)
                return monsters;

            return _gameController.Entities;
        }

        /// <summary>
        /// Follows the trapped essence monsters: links an imprisoned one to its monolith, and counts
        /// the kill when a released one dies.
        /// </summary>
        private void TrackMonster(Entity monster)
        {
            // A linked monster that isn't loaded right now just counts as "not seen" this pass.
            if (!monster.IsValid)
                return;

            if (_byMonsterId.TryGetValue(monster.Id, out var data))
            {
                _monstersSeenThisPass.Add(monster.Id);
                data.Monster = monster;
                data.MonsterMissingSinceMs = null;
                data.MonsterLastDistance = monster.DistancePlayer;

                // Only a released monster can die: an imprisoned one can't be damaged at all, so a
                // "dead" reading before release would be a misread, not a kill.
                if (data.IsOpened && !data.MonsterKilled)
                {
                    if (DiagnosticsOn)
                        LogMonsterSnapshotIfChanged(data, monster);

                    if (GetDeathSignal(monster) is { } signal)
                        HandleMonsterKilled(data, signal);
                }
                return;
            }

            if (_unreleased.Count > 0)
                ConsiderTrappedMonster(monster);

            if (_releasedAwaitingKill.Count > 0)
                ReportMonsterNearReleasedMonolith(monster);
        }

        /// <summary>
        /// Which reading says the monster is dead, or null while it is alive. Several are checked
        /// because ExileCore doesn't document which one is reliable for a given patch; the
        /// diagnostics log records which one fired.
        /// </summary>
        private static string? GetDeathSignal(Entity monster)
        {
            if (monster.IsDead)
                return "IsDead";
            if (!monster.IsAlive)
                return "IsAlive=false";
            if (monster.TryGetComponent<Life>(out var life) && life != null && life.MaxHP > 0 && life.CurHP <= 0)
                return "CurHP<=0";
            return null;
        }

        /// <summary>
        /// Records a monster as a candidate for the nearest unreleased monolith if it is one of that
        /// monolith's prisoners - it stands on the monolith and carries the MonsterInsideMonolith
        /// stat. The best candidate per monolith is linked at the end of the pass.
        /// </summary>
        private void ConsiderTrappedMonster(Entity monster)
        {
            var position = monster.GridPosNum;
            EssenceEntityData? nearest = null;
            var nearestDistance = MONSTER_LINK_RADIUS;

            foreach (var data in _unreleased)
            {
                var distance = Vector2.Distance(position, data.GridPos!.Value);
                if (distance < nearestDistance)
                {
                    nearest = data;
                    nearestDistance = distance;
                }
            }

            // Distance first because it is cheap; the stats are only read for a monster standing
            // on a monolith.
            if (nearest == null || !IsImprisoned(monster))
                return;

            var candidate = new MonsterCandidate(monster, RarityRank(monster), nearestDistance);
            if (!_bestCandidates.TryGetValue(nearest.Id, out var best) || candidate.IsBetterThan(best.Rarity, best.Distance))
                _bestCandidates[nearest.Id] = candidate;
        }

        /// <summary>
        /// Links each unreleased monolith to the best imprisoned monster seen this pass, replacing
        /// the current link only when the new one ranks higher. So an add that happened to be
        /// linked first is swapped out as soon as the rare essence monster is seen.
        /// </summary>
        private void ApplyBestMonsterLinks()
        {
            foreach (var (essenceId, candidate) in _bestCandidates)
            {
                if (!_trackedEntities.TryGetValue(essenceId, out var data) || data.MonsterId == candidate.Monster.Id)
                    continue;

                var previous = data.MonsterId;
                if (previous != null)
                {
                    if (!candidate.IsBetterThan(data.MonsterRarityRank, data.MonsterLinkDistance))
                        continue;

                    _byMonsterId.Remove(previous.Value);
                }

                var monster = candidate.Monster;
                data.MonsterId = monster.Id;
                data.Monster = monster;
                data.MonsterRarityRank = candidate.Rarity;
                data.MonsterLinkDistance = candidate.Distance;
                _byMonsterId[monster.Id] = data;
                _monstersSeenThisPass.Add(monster.Id);

                if (DiagnosticsOn)
                {
                    LogDiagnostic($"#{data.EntityId} id={data.Id}: linked trapped monster id={monster.Id} " +
                                  $"({monster.RenderName}, {RarityName(candidate.Rarity)}, {candidate.Distance:F1} grid away)" +
                                  (previous != null ? $", replacing id={previous}" : ""));
                }
            }
        }

        /// <summary>
        /// Whether the monster is still imprisoned in a monolith (MonsterInsideMonolith stat). Reads
        /// are cached briefly per monster, as the pack around a monolith is re-checked every pass.
        /// </summary>
        private bool IsImprisoned(Entity monster)
        {
            var now = _clock.ElapsedMilliseconds;
            if (_imprisonedChecks.TryGetValue(monster.Id, out var check) && now - check.CheckedAtMs < IMPRISONED_CHECK_TTL_MS)
                return check.Imprisoned;

            bool imprisoned;
            try
            {
                imprisoned = monster.Stats?.GetValueOrDefault(GameStat.MonsterInsideMonolith) > 0;
            }
            catch
            {
                return false; // unreadable stats - don't cache, just try again next pass
            }

            _imprisonedChecks[monster.Id] = (imprisoned, now);
            return imprisoned;
        }

        /// <summary>Monster rarity as a rank (White 0 .. Unique 3), or -1 if unreadable.</summary>
        private static int RarityRank(Entity monster)
        {
            try
            {
                var rarity = monster.Rarity;
                return rarity is >= MonsterRarity.White and <= MonsterRarity.Unique ? (int)rarity : -1;
            }
            catch
            {
                return -1;
            }
        }

        internal static string RarityName(int rank) =>
            rank is >= 0 and <= (int)MonsterRarity.Unique ? ((MonsterRarity)rank).ToString() : "unknown rarity";

        /// <summary>
        /// Handles linked monsters that didn't come through this pass.
        ///
        /// Unreleased: drop the link so it gets made again - e.g. if corrupting replaced the
        /// monster entity, or both just left the loaded area.
        ///
        /// Released: if it stays missing past the grace period and was last seen close to the
        /// player, it died without leaving a corpse in the entity list. If it was far away it most
        /// likely just left the loaded area, so the link is kept and it can still die later.
        /// </summary>
        private void HandleMissingMonsters()
        {
            foreach (var data in _trackedEntities.Values)
            {
                if (data.MonsterKilled || data.MonsterId is not { } monsterId || _monstersSeenThisPass.Contains(monsterId))
                    continue;

                if (!data.IsOpened)
                {
                    _byMonsterId.Remove(monsterId);
                    data.MonsterId = null;
                    data.Monster = null;

                    if (DiagnosticsOn)
                        LogDiagnostic($"#{data.EntityId} id={data.Id}: trapped monster id={monsterId} not in scan, unlinked");
                    continue;
                }

                var now = _clock.ElapsedMilliseconds;
                if (data.MonsterMissingSinceMs == null)
                {
                    data.MonsterMissingSinceMs = now;

                    if (DiagnosticsOn)
                        LogDiagnostic($"#{data.EntityId} id={data.Id}: released monster id={monsterId} not in scan, last seen {data.MonsterLastDistance:F0} grid away ({DescribeMissingMonster(data.Monster)})");
                    continue;
                }

                if (now - data.MonsterMissingSinceMs.Value < MONSTER_VANISH_GRACE_MS)
                    continue;

                if (data.MonsterLastDistance <= MONSTER_VANISH_KILL_DISTANCE)
                    HandleMonsterKilled(data, $"vanished {data.MonsterLastDistance:F0} grid away");
            }
        }

        /// <summary>The released essence monster died - this is the "killed" event.</summary>
        private void HandleMonsterKilled(EssenceEntityData data, string signal)
        {
            if (!data.IsOpened)
                HandleEssenceOpened(data);

            data.MonsterKilled = true;

            // Commit pending corruption or missed outcome now that the monster is confirmed dead
            if (data.PendingCorruption is { } pending)
            {
                if (pending.WasRecommended)
                {
                    _mapStats.IncrementCorrupted();
                    if (_settings.Debug.ShowDebugInfo.Value)
                        DebugWindow.LogMsg($"SUCCESSFUL CORRUPTION CONFIRMED: Essence {data.EntityId} killed", 3, SharpDX.Color.LightGreen);
                }
                else
                {
                    _mapStats.IncrementMistakes();
                    if (_settings.Debug.ShowDebugInfo.Value)
                        DebugWindow.LogMsg($"MISTAKEN CORRUPTION CONFIRMED: Essence {data.EntityId} killed", 3, SharpDX.Color.OrangeRed);
                }

                RecordOutcome(data, pending.Before, pending.After, pending.WasRecommended);
                data.PendingCorruption = null;
            }
            else if (data.PendingMissed)
            {
                _mapStats.IncrementMissed();
                _corruptionLog.RecordMissed();
                if (_settings.Debug.ShowDebugInfo.Value)
                    DebugWindow.LogMsg($"MISSED CORRUPTION CONFIRMED: Essence {data.EntityId} killed without corrupting", 3, SharpDX.Color.Orange);
                data.PendingMissed = false;
            }

            _mapStats.IncrementKilled();
            _corruptionLog.RecordKill();

            if (_settings.Debug.ShowDebugInfo.Value)
                DebugWindow.LogMsg($"Essence {data.EntityId}: monster killed ({signal})", 3, SharpDX.Color.Cyan);
        }

        /// <summary>
        /// Checks if player missed a corruption opportunity.
        /// </summary>
        private bool IsMissedCorruption(EssenceEntityData data)
        {
            if (data.WasCorruptedByPlayer || data.Analysis.IsCorrupted)
                return false;

            var state = data.State;
            if (state == EssenceState.None && data.Analysis.IsValid)
            {
                var rec = StrategyEvaluator.Evaluate(data.Analysis, _settings.Tracking, CurrentProfitContext());
                state = rec.State;
            }

            return state == EssenceState.ShouldCorrupt;
        }

        #endregion

        #region Diagnostics

        /// <summary>Diagnostics ride on the existing debug toggle, so there is one switch for both.</summary>
        private bool DiagnosticsOn => _settings.Debug.ShowDebugInfo.Value;

        private static void LogDiagnostic(string message) => DebugWindow.LogMsg($"[BECH diag] {message}");

        /// <summary>
        /// Logs an essence's tracking snapshot, but only when it differs from the last one. Compared on a
        /// cheap key first, so the text is only built on an actual change (this runs every frame).
        /// </summary>
        private static void LogSnapshotIfChanged(EssenceEntityData data, bool targetable)
        {
            var key = HashCode.Combine(targetable, data.Label != null, data.State, data.MonsterId, data.IsOpened);
            if (key == data.LastDiagnosticKey && data.LastDiagnostic != null)
                return;

            data.LastDiagnosticKey = key;
            var snapshot = $"targetable={targetable} label={(data.Label != null ? "yes" : "no")} state={data.State} " +
                           $"monster={(data.MonsterId?.ToString() ?? "not linked")}{(data.IsOpened ? " [released]" : "")}";
            if (snapshot == data.LastDiagnostic)
                return;

            data.LastDiagnostic = snapshot;
            LogDiagnostic($"#{data.EntityId} id={data.Id}: {snapshot}");
        }

        /// <summary>
        /// Periodic diagnostics: what happened to tracked essences that aren't among the loaded
        /// monoliths, plus a one-line summary whenever its numbers change.
        /// </summary>
        private void LogScanDiagnostics()
        {
            var opened = 0;
            var withLabel = 0;
            var linked = 0;
            var killed = 0;

            foreach (var data in _trackedEntities.Values)
            {
                if (data.IsOpened) opened++;
                if (data.Label != null) withLabel++;
                if (data.MonsterId != null) linked++;
                if (data.MonsterKilled) killed++;

                if (!_seenThisPass.Contains(data.Id))
                {
                    var missing = DescribeMissing(data);
                    if (missing != data.LastDiagnostic)
                    {
                        data.LastDiagnostic = missing;
                        data.LastDiagnosticKey = 0;
                        LogDiagnostic($"#{data.EntityId} id={data.Id}: {missing}");
                    }
                }
            }

            var summary =
                $"scan: {_validMonoliths} valid monoliths ({_rejectedMonoliths} more rejected as invalid, {_skippedUntargetable} untargetable and untracked), " +
                $"{_trackedEntities.Count} tracked ({opened} released, {linked} with a linked monster, {killed} monsters killed), {withLabel} with a visible label";

            if (summary != _lastScanSummary)
            {
                LogDiagnostic(summary);
                _lastScanSummary = summary;
            }
        }

        /// <summary>Why a tracked essence is missing from this pass, as far as its entity can tell us.</summary>
        private static string DescribeMissing(EssenceEntityData data)
        {
            if (!data.IsLoaded)
                return "not in scan: removed from the entity list (out of range)";

            var entity = data.Entity;
            if (entity == null)
                return "not in scan (no entity)";

            try
            {
                return $"not in scan: valid={entity.IsValid} targetable={entity.IsTargetable} " +
                       $"hasMonolith={entity.HasComponent<Monolith>()} hasRender={entity.HasComponent<Render>()}";
            }
            catch (Exception ex)
            {
                return $"not in scan (entity unreadable: {ex.GetType().Name})";
            }
        }

        /// <summary>Logs a released monster's state, but only when it differs from the last one.</summary>
        private static void LogMonsterSnapshotIfChanged(EssenceEntityData data, Entity monster)
        {
            var snapshot = DescribeMonster(monster);
            if (snapshot == data.LastMonsterDiagnostic)
                return;

            data.LastMonsterDiagnostic = snapshot;
            LogDiagnostic($"#{data.EntityId} monster id={data.MonsterId}: {snapshot}");
        }

        /// <summary>
        /// Everything that could tell us a monster is dead. HP is bucketed to 10% steps so a fight
        /// logs a handful of lines rather than one per hit.
        /// </summary>
        private static string DescribeMonster(Entity monster)
        {
            var hp = "no Life component";
            if (monster.TryGetComponent<Life>(out var life) && life != null)
            {
                hp = life.CurHP <= 0
                    ? $"{life.CurHP}/{life.MaxHP}"
                    : $"~{Math.Ceiling(life.HPPercentage * 10) * 10:F0}% of {life.MaxHP}";
            }

            return $"alive={monster.IsAlive} dead={monster.IsDead} hp={hp} targetable={monster.IsTargetable} hidden={monster.IsHidden}";
        }

        /// <summary>What a released monster's last entity reference says now that it is missing from the scan.</summary>
        private static string DescribeMissingMonster(Entity? monster)
        {
            if (monster == null)
                return "no entity";

            try
            {
                return $"valid={monster.IsValid} {DescribeMonster(monster)}";
            }
            catch (Exception ex)
            {
                return $"entity unreadable: {ex.GetType().Name}";
            }
        }

        /// <summary>
        /// Reports, once each, other monsters standing on a released monolith. If the game spawns a
        /// fresh entity for the released monster instead of reusing the trapped one, this is where
        /// it shows up.
        /// </summary>
        private void ReportMonsterNearReleasedMonolith(Entity monster)
        {
            if (_reportedNearbyMonsters.Contains(monster.Id))
                return;

            var position = monster.GridPosNum;
            foreach (var data in _releasedAwaitingKill)
            {
                var distance = Vector2.Distance(position, data.GridPos!.Value);
                if (distance > MONSTER_LINK_RADIUS)
                    continue;

                if (data.MonsterId == null && monster.IsAlive)
                {
                    data.MonsterId = monster.Id;
                    data.Monster = monster;
                    _byMonsterId[monster.Id] = data;
                    if (DiagnosticsOn)
                        LogDiagnostic($"#{data.EntityId}: linked released monster id={monster.Id} ({monster.RenderName}) {distance:F1} grid away");
                    return;
                }

                _reportedNearbyMonsters.Add(monster.Id);
                LogDiagnostic($"#{data.EntityId}: other monster near released monolith: id={monster.Id} ({monster.RenderName}, {monster.Rarity}, {distance:F1} grid away) {DescribeMonster(monster)}");
                return;
            }
        }

        #endregion

        #region Entity Validation & Helpers

        /// <summary>Whether an entity carries the Monolith component (unreadable entities: no).</summary>
        private static bool IsMonolithEntity([NotNullWhen(true)] Entity? entity)
        {
            if (entity == null)
                return false;

            try
            {
                return entity.HasComponent<Monolith>();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates if an entity is a loaded essence monolith, and returns its Monolith component.
        /// IsTargetable is deliberately not required here: a monolith stops being targetable once
        /// opened, and a tracked one has to stay visible to us for that flip to be seen.
        /// </summary>
        private static bool IsValidMonolith([NotNullWhen(true)] Entity? entity, long playerAddress, [NotNullWhen(true)] out Monolith? monolith)
        {
            monolith = null;

            // Fast rejection checks first
            if (entity == null || entity.Address == 0 || !entity.IsValid || entity.Address == playerAddress)
                return false;

            // Component checks
            if (!entity.HasComponent<Render>() || !entity.TryGetComponent<Monolith>(out monolith) || monolith == null)
                return false;

            // Position validation (try-catch for safety against memory read errors)
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

        /// <summary>IsValid of a possibly stale entity; unreadable counts as invalid.</summary>
        private static bool IsEntityValid(Entity entity)
        {
            try
            {
                return entity.IsValid;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a world position can have an on-screen ground label this frame. Camera and window are
        /// read once per frame; any failure answers "yes" so a label is never missed because of it.
        /// </summary>
        private readonly struct ScreenTest
        {
            private readonly Camera? _camera;
            private readonly SharpDX.RectangleF _window;

            public ScreenTest(GameController gameController)
            {
                _camera = null;
                _window = default;
                try
                {
                    _camera = gameController.IngameState.Camera;
                    _window = gameController.Window.GetWindowRectangleTimeCache;
                }
                catch
                {
                    _camera = null;
                }
            }

            public bool MayShowLabel(Entity? entity)
            {
                if (_camera == null || entity == null || _window.Width <= 0)
                    return true;

                try
                {
                    var p = _camera.WorldToScreen(entity.PosNum);
                    return p.X >= -LABEL_SCREEN_MARGIN && p.Y >= -LABEL_SCREEN_MARGIN &&
                           p.X <= _window.Width + LABEL_SCREEN_MARGIN && p.Y <= _window.Height + LABEL_SCREEN_MARGIN;
                }
                catch
                {
                    return true;
                }
            }
        }

        /// <summary>IsVisible of a held label; an unreadable label counts as not visible.</summary>
        private static bool IsLabelVisible(LabelOnGround label)
        {
            try
            {
                return label.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }

    /// <summary>
    /// Work the tracker did over the last full second, for the diagnostics view. Counts, not
    /// guesses: "entity visits" is what the tracker actually touched, and <see cref="EntityListSize"/>
    /// is what the previous design walked on every 50ms pass (20 times a second).
    /// </summary>
    internal sealed class TrackerPerformance
    {
        private long _windowStartMs = -1;
        private int _ticks;
        private long _tickTicksTotal, _tickTicksMax;

        public int MonolithChecks;
        public int MonsterVisits;
        public int LabelScans;
        public int LabelsVisited;
        public int Analyses;
        private int _seeds, _seedVisits;

        /// <summary>Snapshot of the last completed one-second window.</summary>
        public Snapshot Last { get; private set; } = new();

        /// <summary>Entities in the list at the last seed scan.</summary>
        public int EntityListSize { get; private set; }

        public sealed record Snapshot(
            int Ticks = 0,
            double AvgTickMicros = 0,
            double MaxTickMicros = 0,
            int MonolithChecks = 0,
            int MonsterVisits = 0,
            int LabelScans = 0,
            int LabelsVisited = 0,
            int Analyses = 0,
            int SeedVisits = 0,
            int CandidateMonoliths = 0,
            int Tracked = 0)
        {
            /// <summary>Entity objects visited this second (monoliths, monsters, seed walks).</summary>
            public int EntityVisits => MonolithChecks + MonsterVisits + SeedVisits;
        }

        public void RecordSeed(int visited)
        {
            _seeds++;
            _seedVisits += visited;
            EntityListSize = visited;
        }

        public void EndTick(long elapsedTicks, long nowMs, int candidates, int tracked)
        {
            _ticks++;
            _tickTicksTotal += elapsedTicks;
            _tickTicksMax = Math.Max(_tickTicksMax, elapsedTicks);

            if (_windowStartMs < 0)
                _windowStartMs = nowMs;
            if (nowMs - _windowStartMs < 1000)
                return;

            var toMicros = 1_000_000.0 / Stopwatch.Frequency;
            Last = new Snapshot(
                _ticks,
                _tickTicksTotal * toMicros / _ticks,
                _tickTicksMax * toMicros,
                MonolithChecks,
                MonsterVisits,
                LabelScans,
                LabelsVisited,
                Analyses,
                _seedVisits,
                candidates,
                tracked);

            _windowStartMs = nowMs;
            _ticks = 0;
            _tickTicksTotal = _tickTicksMax = 0;
            MonolithChecks = MonsterVisits = LabelScans = LabelsVisited = Analyses = _seeds = _seedVisits = 0;
        }
    }
}
