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
    /// Manages the lifecycle of essence entities from detection to the death of their monster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Update path, all on the main thread:
    /// <list type="number">
    /// <item>Discovery is event driven. <see cref="OnEntityAdded"/>/<see cref="OnEntityRemoved"/> keep the
    /// set of loaded monolith entities and the references to linked monsters, so the tracker never
    /// walks the whole entity list per pass. A seed scan covers entities that existed before the plugin
    /// was enabled or the area change was seen, and a reseed once a second guards against a missed event.</item>
    /// <item><see cref="Tick"/> runs every frame: monolith state (opened?) for unopened monoliths, label
    /// resolution only while a tracked essence needs one, label analysis when its line count changed
    /// (a Vaal Orb adds the "Corrupted" line) plus a periodic re-read, and the recommendation right after.</item>
    /// <item>The monster pass (every <see cref="MONSTER_PASS_MS"/>) reads linked monsters directly and only
    /// walks the monster list while an essence still needs its monster found.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Essences are kept per map instance (<see cref="MapSession"/>): entity IDs are stable within an
    /// instance, so an essence that the game unloads when you walk away, or that you come back to through
    /// a portal, is recognised as the same one.
    /// </para>
    /// <para>
    /// A kill is only counted for the essence monster itself, once it has been seen free and then dies,
    /// wherever in the map that happens: it is followed by entity ID, not position. The only positional
    /// step is pairing a monolith with the frozen monster inside it. Breaking the monolith can replace
    /// the trapped entity with a new one, so the trapped entity going away (or reading dead) on release
    /// is not a kill: the freed monster is recognised anywhere in the loaded area by the trapped one's
    /// essence mods and rarity, and that one is followed instead.
    /// </para>
    /// </remarks>
    /// <param name="gameController">The game controller instance</param>
    /// <param name="settings">Plugin settings</param>
    /// <param name="corruptionLog">Observed corruption outcomes</param>
    /// <param name="prices">Price snapshot (Vaal Orb cost of observed corruptions)</param>
    /// <param name="values">Essence values used to value observed corruptions</param>
    /// <param name="setup">Map setup read from the game, recorded with each corruption</param>
    /// <param name="outcomeCsvPath">Where to append observed corruptions, or null when recording is off</param>
    internal class EssenceEntityTracker(
        GameController gameController,
        Settings settings,
        CorruptionLog corruptionLog,
        PriceBook prices,
        EssenceValues values,
        SetupDetector setup,
        Func<string?> outcomeCsvPath)
    {
        #region Constants

        /// <summary>Interval of the monster pass (monster linking and kill detection), ms</summary>
        private const int MONSTER_PASS_MS = 50;

        /// <summary>Re-read a label's text at least this often even if its line count didn't change, ms</summary>
        private const int ANALYSIS_REFRESH_MS = 200;

        /// <summary>Minimum gap between visible-label list scans while a tracked essence is missing its label, ms (~2 frames)</summary>
        private const int LABEL_SCAN_MIN_MS = 33;

        /// <summary>Safety re-scan of the label list while every tracked essence has a visible label, ms</summary>
        private const int LABEL_SCAN_SAFETY_MS = 250;

        /// <summary>Safety re-seed of monoliths and linked monsters from the full entity list, ms</summary>
        private const int RESEED_MS = 1000;

        /// <summary>Screen margin (px) within which a monolith's ground label may be visible</summary>
        private const float LABEL_SCREEN_MARGIN = 300f;

        /// <summary>The same tracking error is logged at most this often, ms</summary>
        private const int ERROR_LOG_INTERVAL_MS = 5000;

        /// <summary>A monolith must read untargetable this long before it counts as opened, ms (filters a misread on load)</summary>
        private const int OPEN_CONFIRM_MS = 150;

        /// <summary>Max grid distance between a monolith and the trapped monster linked to it</summary>
        private const float MONSTER_LINK_RADIUS = 15f;

        /// <summary>
        /// If the trapped entity is replaced on release, the freed monster is looked for on every monster
        /// pass for this long, then every <see cref="FREED_SLOW_SEARCH_MS"/> until found. ms
        /// </summary>
        private const int FREED_FAST_SEARCH_MS = 10000;

        /// <summary>Interval of the freed-monster search after <see cref="FREED_FAST_SEARCH_MS"/>, ms</summary>
        private const int FREED_SLOW_SEARCH_MS = 1000;

        /// <summary>How often the trapped monster's identity (mods) is re-read - a Vaal Orb changes them. ms</summary>
        private const int IDENTITY_REFRESH_MS = 500;

        /// <summary>
        /// How long a freed monster must be missing from the entity list before its disappearance is
        /// judged - the list can drop an entity for a pass or two while it is rebuilt.
        /// </summary>
        private const int MONSTER_VANISH_GRACE_MS = 1000;

        /// <summary>
        /// A damaged freed monster that vanishes this close to the player (grid units) died and left no
        /// corpse (e.g. shattered); one that vanishes further away most likely just left the loaded area.
        /// </summary>
        private const float MONSTER_VANISH_KILL_DISTANCE = 60f;

        /// <summary>How long a MonsterInsideMonolith read is trusted before it is read again (ms).</summary>
        private const int IMPRISONED_CHECK_TTL_MS = 1000;

        /// <summary>Map instances whose essences are remembered (a map device opens up to 6 portals)</summary>
        private const int MAX_MAP_SESSIONS = 8;

        /// <summary>Diagnostics summaries are written at most this often, ms</summary>
        private const int DIAGNOSTIC_SUMMARY_MS = 1000;

        #endregion

        #region Fields

        private readonly GameController _gameController = gameController;
        private readonly Settings _settings = settings;
        private readonly CorruptionLog _corruptionLog = corruptionLog;
        private readonly PriceBook _prices = prices;
        private readonly EssenceValues _values = values;
        private readonly SetupDetector _setup = setup;
        private readonly Func<string?> _outcomeCsvPath = outcomeCsvPath;

        /// <summary>Recently visited map instances by area hash.</summary>
        private readonly Dictionary<uint, MapSession> _sessions = new();

        /// <summary>
        /// The map being tracked. Town and hideout keep the last map's session, so its stats can still
        /// be shown there; AreaHash 0 until the first map is bound.
        ///
        /// Plain Dictionaries are enough everywhere: Tick, Render and the entity events all run on the main thread.
        /// </summary>
        private MapSession _session = new(0);

        /// <summary>False in town/hideout: entity events there must not touch the last map's session.</summary>
        private bool _inMap = true;

        /// <summary>Loaded entities carrying a Monolith component, by ID (EntityAdded/EntityRemoved + seeding).</summary>
        private readonly Dictionary<uint, Entity> _monoliths = new();

        /// <summary>Visible monolith labels by entity ID, rebuilt on every label scan (reused to avoid allocating).</summary>
        private readonly Dictionary<uint, LabelOnGround> _labelsById = new();

        /// <summary>Unreleased essences still looking for their trapped monster (rebuilt each pass).</summary>
        private readonly List<EssenceEntityData> _seekingTrapped = new();

        /// <summary>Released essences still looking for their freed monster (rebuilt each pass).</summary>
        private readonly List<EssenceEntityData> _seekingFreed = new();

        /// <summary>Best monster seen this pass for each essence that is looking for one, by essence entity ID.</summary>
        private readonly Dictionary<uint, MonsterCandidate> _bestCandidates = new();

        /// <summary>Cached MonsterInsideMonolith reads by monster ID, so stats aren't re-read every pass.</summary>
        private readonly Dictionary<uint, (bool Imprisoned, long CheckedAtMs)> _imprisonedChecks = new();

        /// <summary>Trapped entities left behind when their monolith opened; never linked as the freed monster.</summary>
        private readonly HashSet<uint> _leftoverMonsters = new();

        /// <summary>
        /// A monster that may be an essence's monster. Ranked: <see cref="MonsterCandidate.Match"/> first
        /// (trapped: carries essence mods; freed: also has the trapped monster's name), then highest
        /// rarity (the essence monster is rare, the pack around it white or magic), then closest to the
        /// monolith (trapped only).
        /// </summary>
        private readonly record struct MonsterCandidate(Entity? Monster, int Match, int Rarity, float Distance, string? Mods)
        {
            public bool IsBetterThan(MonsterCandidate other) =>
                Match != other.Match ? Match > other.Match :
                Rarity != other.Rarity ? Rarity > other.Rarity :
                Distance < other.Distance;
        }

        /// <summary>Monotonic clock for every interval here.</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _nextSeedMs;
        private long _nextMonsterPassMs;
        private long _nextSlowFreedSearchMs;
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

        /// <summary>Essences tracked in the current map. Opened ones stay listed with State None.</summary>
        public IEnumerable<EssenceEntityData> TrackedEntities => _session.Essences.Values;

        /// <summary>Kill and corruption totals of the current (or, in town/hideout, the last) map.</summary>
        public MapStatistics MapStats => _session.Stats;

        /// <summary>Work counters from the last full second (diagnostics).</summary>
        public TrackerPerformance Performance => _performance;

        #endregion

        #region Public Methods

        /// <summary>
        /// ExileCore EntityAdded: remember monoliths, and pick up a linked monster coming back into
        /// range. Cheap - a dictionary lookup plus one component lookup per added entity.
        /// </summary>
        public void OnEntityAdded(Entity entity)
        {
            if (entity == null || !_inMap)
                return;

            if (_session.ByMonsterId.TryGetValue(entity.Id, out var owner))
            {
                owner.Monster = entity;
                return;
            }

            if (!IsMonolithEntity(entity))
                return;

            _monoliths[entity.Id] = entity;
            if (_session.Essences.TryGetValue(entity.Id, out var data) && !ReferenceEquals(data.Entity, entity))
                MarkLoaded(data, entity);
        }

        /// <summary>ExileCore EntityRemoved: a monolith or linked monster left the loaded area. Tracking data stays with the map.</summary>
        public void OnEntityRemoved(Entity entity)
        {
            if (entity == null || !_inMap)
                return;

            if (_session.ByMonsterId.TryGetValue(entity.Id, out var owner))
            {
                if (ReferenceEquals(owner.Monster, entity))
                    owner.Monster = null;
                return;
            }

            if (_monoliths.Remove(entity.Id) && _session.Essences.TryGetValue(entity.Id, out var data))
                MarkUnloaded(data);
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
                    // Enabled mid-map: no area change has bound a session yet.
                    if (_session.AreaHash == 0)
                        EnterMap(_gameController.Area.CurrentArea.Hash, now);

                    if (now >= _nextSeedMs)
                        SeedEntities(now, diagnostics);

                    ProcessMonoliths(now, diagnostics);
                    ReevaluateIfInputsChanged();
                    RefreshLabels(now, diagnostics);
                    AnalyzeLabels(now);

                    if (now >= _nextMonsterPassMs)
                    {
                        _nextMonsterPassMs = now + MONSTER_PASS_MS;
                        ProcessMonsters(now, diagnostics);
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

            _performance.EndTick(Stopwatch.GetTimestamp() - started, now, _monoliths.Count, _session.Essences.Count);
        }

        /// <summary>
        /// Area change. Entity and label references belong to the area being left, so they are always
        /// dropped; the essences themselves stay with their map. Entering a map resumes its session
        /// (same essences, numbers, pending outcomes and stats) or starts a new one; town and hideout
        /// keep the last map's.
        /// </summary>
        public void OnAreaChanged(AreaInstance area)
        {
            foreach (var data in _session.Essences.Values)
            {
                MarkUnloaded(data);

                // Same for the linked monster. Its link (ID) stays; it is picked up again when it loads,
                // and it can't count as vanished before it has been seen again in this visit.
                data.Monster = null;
                data.MonsterMissingSinceMs = null;
                data.MonsterLastDistance = float.MaxValue;
            }

            _monoliths.Clear();
            _imprisonedChecks.Clear();
            _leftoverMonsters.Clear();

            // Seed again as soon as the new area is trackable: events that arrived before this call
            // belong to the new area too and were just cleared.
            _seededThisArea = false;
            _nextSeedMs = 0;
            _nextLabelScanMs = 0;
            _nextSafetyLabelScanMs = 0;

            _inMap = area == null || !area.IsPeaceful;
            if (area != null && _inMap)
                EnterMap(area.Hash, _clock.ElapsedMilliseconds);
        }

        #endregion

        #region Map Sessions

        /// <summary>Switches to the session of the given map instance, creating it (and evicting the oldest) if new.</summary>
        private void EnterMap(uint areaHash, long now)
        {
            if (!_sessions.TryGetValue(areaHash, out var session))
            {
                session = new MapSession(areaHash);
                _sessions[areaHash] = session;

                if (_sessions.Count > MAX_MAP_SESSIONS)
                {
                    var oldest = _sessions.Values.Where(s => s != session).MinBy(s => s.LastEnteredMs)!;
                    _sessions.Remove(oldest.AreaHash);
                }

                if (DiagnosticsOn)
                    LogDiagnostic($"new map instance {areaHash:X8}");
            }
            else if (session != _session && DiagnosticsOn)
            {
                LogDiagnostic($"back in map instance {areaHash:X8}: {session.Essences.Count} essences remembered");
            }

            session.LastEnteredMs = now;
            _session = session;
        }

        private static void MarkLoaded(EssenceEntityData data, Entity entity)
        {
            data.Entity = entity;
            data.IsLoaded = true;
            data.SeenTargetableSinceLoad = false;
            data.UntargetableSinceMs = null;
        }

        /// <summary>The monolith left the loaded area (or the area was left): drop what referenced live game memory.</summary>
        private static void MarkUnloaded(EssenceEntityData data)
        {
            data.IsLoaded = false;
            data.Entity = null;
            data.Label = null;
            data.LabelSignature = -1;
            data.SeenTargetableSinceLoad = false;
            data.UntargetableSinceMs = null;
        }

        #endregion

        #region Entity Processing

        /// <summary>
        /// Adds any monolith the events didn't report and re-attaches linked monsters whose
        /// EntityAdded was missed. A full walk of the entity list, so it runs once per area and then
        /// only every <see cref="RESEED_MS"/>.
        /// </summary>
        private void SeedEntities(long now, bool diagnostics)
        {
            _nextSeedMs = now + RESEED_MS;
            var visited = 0;
            foreach (var entity in _gameController.Entities)
            {
                visited++;
                if (entity == null)
                    continue;

                if (_session.ByMonsterId.TryGetValue(entity.Id, out var owner))
                {
                    if (owner.Monster == null && IsEntityValid(entity))
                        owner.Monster = entity;
                    continue;
                }

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
        /// Per-frame monolith pass over the loaded monoliths: start tracking new ones and detect
        /// release (the monolith turning untargetable). Opened monoliths are skipped - nothing about
        /// them changes any more.
        /// </summary>
        private void ProcessMonoliths(long now, bool diagnostics)
        {
            _validMonoliths = _rejectedMonoliths = _skippedUntargetable = 0;
            if (diagnostics)
                _seenThisPass.Clear();

            foreach (var (id, entity) in _monoliths)
            {
                _performance.MonolithChecks++;

                if (_session.Essences.TryGetValue(id, out var data))
                {
                    if (diagnostics)
                        _seenThisPass.Add(id);
                    if (data.IsOpened)
                        continue;

                    if (!IsEntityValid(entity))
                    {
                        _rejectedMonoliths++;
                        continue;
                    }

                    _validMonoliths++;
                    if (!ReferenceEquals(data.Entity, entity))
                        MarkLoaded(data, entity);

                    UpdateOpenedState(data, entity.IsTargetable, now, diagnostics);
                    continue;
                }

                if (!IsValidMonolith(entity))
                {
                    _rejectedMonoliths++;
                    continue;
                }

                _validMonoliths++;

                // Only start tracking monoliths that can still be interacted with. One that is already
                // untargetable was opened before we got here and has nothing to offer.
                if (!entity.IsTargetable)
                {
                    _skippedUntargetable++;
                    continue;
                }

                data = CreateNewEssenceData(entity);
                if (diagnostics)
                {
                    _seenThisPass.Add(id);
                    LogSnapshotIfChanged(data, true);
                }
            }
        }

        /// <summary>
        /// "Opened" means the monster was released. On 3.29 the Monolith component's IsOpened/OpenStage
        /// never change (confirmed with diagnostics), so the monolith turning untargetable is the signal
        /// that actually fires. It has to hold for <see cref="OPEN_CONFIRM_MS"/>, so a misread while the
        /// entity loads back in doesn't release an essence that is still waiting.
        /// </summary>
        private void UpdateOpenedState(EssenceEntityData data, bool targetable, long now, bool diagnostics)
        {
            if (targetable)
            {
                data.SeenTargetableSinceLoad = true;
                data.UntargetableSinceMs = null;
            }
            else
            {
                data.UntargetableSinceMs ??= now;
                if (now - data.UntargetableSinceMs.Value >= OPEN_CONFIRM_MS)
                    HandleEssenceOpened(data, now, observed: data.SeenTargetableSinceLoad);
            }

            if (diagnostics)
                LogSnapshotIfChanged(data, targetable);
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
                EntityId = ++_session.LastDisplayNumber,
                Entity = entity,
                GridPos = entity.GridPosNum,
                SeenTargetableSinceLoad = true,
            };

            _session.Essences[entity.Id] = newData;
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
        /// re-check every <see cref="LABEL_SCAN_SAFETY_MS"/>.
        /// </summary>
        private void RefreshLabels(long now, bool diagnostics)
        {
            var anyCandidate = false;
            var needsScan = false;
            var screen = new ScreenTest(_gameController);
            foreach (var data in _session.Essences.Values)
            {
                if (data.IsOpened || !data.IsLoaded)
                    continue;

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
            // ID - the same key the tracking uses.
            _labelsById.Clear();
            for (var i = 0; i < labels.Count; i++)
            {
                var label = labels[i];
                var item = label?.ItemOnGround;
                if (item == null)
                    continue;

                _performance.LabelsVisited++;
                if (countMonolithLabels && item.HasComponent<Monolith>())
                    monolithLabels++;

                if (_session.Essences.ContainsKey(item.Id))
                    _labelsById[item.Id] = label!;
            }

            foreach (var data in _session.Essences.Values)
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
            foreach (var data in _session.Essences.Values)
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
            foreach (var data in _session.Essences.Values)
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

            var shownState = data.State;

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

                HandleEssenceCorruption(data, previous, newAnalysis, shownState);
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
        /// <param name="data">The corrupted essence</param>
        /// <param name="before">Its contents before the Vaal Orb</param>
        /// <param name="after">Its contents after</param>
        /// <param name="shownState">The recommendation on screen when it was corrupted</param>
        private void HandleEssenceCorruption(EssenceEntityData data, EssenceAnalysis before, EssenceAnalysis after, EssenceState shownState)
        {
            var wasRecommended = shownState == EssenceState.ShouldCorrupt;
            data.Corruption = new CorruptionObservation(before, after);
            data.Decision = wasRecommended ? EssenceDecision.Corrupted : EssenceDecision.WronglyCorrupted;

            // The essences changed, and with them the trapped monster's mods: re-read its identity.
            data.MonsterIdentityReadMs = long.MinValue;

            if (DiagnosticsOn)
            {
                DebugWindow.LogMsg(
                    $"Essence {data.EntityId} (id={data.Id}) corrupted (pending kill). Recommended: {wasRecommended}", 3,
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

            if (DiagnosticsOn)
            {
                var gain = record.RealizedGain is { } g ? $", {g:+0.##;-0.##}c at listing prices" : "";
                DebugWindow.LogMsg($"Essence {data.EntityId} corruption outcome: {outcome?.ToString() ?? "unclassified"}{(ambiguous ? " (ambiguous)" : "")} " +
                                   $"[{string.Join(", ", record.Before)}] -> [{string.Join(", ", record.After)}]{gain}", 5, SharpDX.Color.Cyan);
            }
        }

        /// <summary>
        /// Processes the monolith being opened: the monster is released and the chance to corrupt
        /// is gone, so a missed corruption is judged here. The kill itself is only counted once the
        /// freed monster dies, wherever that happens (see <see cref="UpdateLinkedMonster"/>).
        /// </summary>
        /// <param name="data">The essence whose monolith opened</param>
        /// <param name="now">Tracker clock (ms)</param>
        /// <param name="observed">
        /// False when the monolith was already open the first time it was read after loading back in
        /// (opened by someone else, or while it was unloaded): it isn't judged as a missed corruption.
        /// </param>
        private void HandleEssenceOpened(EssenceEntityData data, long now, bool observed)
        {
            if (data.IsOpened)
                return;

            data.Phase = EssencePhase.Released;
            data.OpenedAtMs = now;

            if (!observed)
            {
                if (DiagnosticsOn)
                    LogDiagnostic($"#{data.EntityId} id={data.Id}: found opened after loading back in - not judged");
            }
            else if (data.Decision == EssenceDecision.Undecided)
            {
                // Opened without a Vaal Orb. A label that was already corrupted had nothing left to decide.
                data.Decision = data.State == EssenceState.ShouldCorrupt && !data.Analysis.IsCorrupted
                    ? EssenceDecision.MissedCorruption
                    : EssenceDecision.LeftUncorrupted;
            }

            if (DiagnosticsOn)
            {
                if (data.Decision == EssenceDecision.MissedCorruption)
                    DebugWindow.LogMsg($"MISSED CORRUPTION: Essence {data.EntityId} (id={data.Id}) opened uncorrupted (counted at the kill)", 3, SharpDX.Color.Orange);
                else
                    DebugWindow.LogMsg($"Essence {data.EntityId} released ({data.Decision})", 3, SharpDX.Color.Cyan);
            }

            if (!data.HasMonsterIdentity && DiagnosticsOn)
                LogDiagnostic($"#{data.EntityId} id={data.Id}: released before its trapped monster was linked - the freed monster can only be followed if it is the same entity");

            // Nothing left to draw for an opened monolith. State keeps the last recommendation.
            data.Label = null;
        }

        /// <summary>
        /// Monster pass: checks linked monsters through their held entity, then walks the monster list
        /// only if an essence still needs its trapped or freed monster found.
        /// </summary>
        private void ProcessMonsters(long now, bool diagnostics)
        {
            _seekingTrapped.Clear();
            _seekingFreed.Clear();
            _bestCandidates.Clear();

            var slowSearchDue = now >= _nextSlowFreedSearchMs;
            if (slowSearchDue)
                _nextSlowFreedSearchMs = now + FREED_SLOW_SEARCH_MS;

            foreach (var data in _session.Essences.Values)
            {
                if (data.MonsterKilled)
                    continue;

                if (data.MonsterId != null)
                {
                    UpdateLinkedMonster(data, now, diagnostics);
                    if (data.MonsterKilled)
                        continue;
                }

                if (!data.IsOpened)
                {
                    // Keep looking until the essence monster itself is linked: the adds trapped with it
                    // may be linked first.
                    if (data.IsLoaded && data.GridPos != null && !IsEssenceMonsterLinked(data))
                        _seekingTrapped.Add(data);
                }
                else if (!data.MonsterFreed && data.HasMonsterIdentity &&
                         (now - data.OpenedAtMs <= FREED_FAST_SEARCH_MS || slowSearchDue))
                {
                    _seekingFreed.Add(data);
                }
            }

            if (_seekingTrapped.Count == 0 && _seekingFreed.Count == 0)
                return;

            foreach (var monster in MonsterEntities())
            {
                if (monster?.Type != EntityType.Monster || _session.ByMonsterId.ContainsKey(monster.Id) || !monster.IsValid)
                    continue;

                _performance.MonsterVisits++;
                ConsiderCandidate(monster);
            }

            ApplyBestMonsterLinks(now, diagnostics);
        }

        /// <summary>The linked trapped monster is the essence monster: it carries essence mods, or is at least rare.</summary>
        private static bool IsEssenceMonsterLinked(EssenceEntityData data) =>
            data.MonsterId != null && (!string.IsNullOrEmpty(data.MonsterEssenceMods) || data.MonsterRarityRank >= (int)MonsterRarity.Rare);

        /// <summary>Monsters only, from ExileCore's per-type index; the full entity list if that is unavailable.</summary>
        private IEnumerable<Entity> MonsterEntities()
        {
            var byType = _gameController.EntityListWrapper?.ValidEntitiesByType;
            if (byType != null && byType.TryGetValue(EntityType.Monster, out var monsters) && monsters != null)
                return monsters;

            return _gameController.Entities;
        }

        /// <summary>
        /// Follows an essence's linked monster by ID, wherever it is in the map. Before release there
        /// is nothing to watch (a trapped monster can't be damaged) beyond keeping its identity current,
        /// as a Vaal Orb changes its mods. After release the linked monster has to be seen alive and
        /// free before its death counts - the trapped entity may be replaced by a new one when the
        /// monolith breaks, and that leftover reading dead or disappearing is not a kill.
        /// </summary>
        private void UpdateLinkedMonster(EssenceEntityData data, long now, bool diagnostics)
        {
            var id = data.MonsterId!.Value;
            var monster = data.Monster;
            if (monster == null || !IsEntityValid(monster))
            {
                HandleMissingMonster(data, id, now, diagnostics);
                return;
            }

            data.MonsterMissingSinceMs = null;
            data.MonsterLastDistance = monster.DistancePlayer;

            if (!data.IsOpened)
            {
                if (now - data.MonsterIdentityReadMs >= IDENTITY_REFRESH_MS)
                    RecordIdentity(data, monster, now);
                return;
            }

            var deathSignal = GetDeathSignal(monster);
            if (!data.MonsterFreed)
            {
                if (deathSignal == null && IsImprisoned(monster, useCache: false) == false)
                {
                    data.MonsterFreed = true;
                    data.MonsterLastHpFraction = HpFraction(monster);
                    if (diagnostics)
                        LogDiagnostic($"#{data.EntityId} id={data.Id}: trapped monster id={id} is the freed monster");
                }
                else if (deathSignal != null || now - data.OpenedAtMs > FREED_FAST_SEARCH_MS)
                {
                    // Dead the moment it was freed, or still imprisoned long after release: the trapped
                    // entity was left behind and the freed monster is another one.
                    _leftoverMonsters.Add(id);
                    Unlink(data, id, diagnostics, $"trapped entity left behind on release ({deathSignal ?? "still imprisoned"}), not a kill");
                }
                return;
            }

            if (diagnostics)
                LogMonsterSnapshotIfChanged(data, monster);

            data.MonsterLastHpFraction = HpFraction(monster);
            if (deathSignal != null)
                HandleMonsterKilled(data, deathSignal);
        }

        /// <summary>
        /// A linked monster that isn't loaded. Not yet freed (trapped, or the trapped entity on
        /// release): drop the link so it gets made again - this is how an ID change on corruption or
        /// release is followed. Freed: if it stays missing past the grace period, was damaged and was
        /// last seen close to the player, it died without leaving a corpse; otherwise it just left the
        /// loaded area (you ran off, or it chased you elsewhere) and keeps its link - same ID when it
        /// comes back.
        /// </summary>
        private void HandleMissingMonster(EssenceEntityData data, uint id, long now, bool diagnostics)
        {
            if (!data.MonsterFreed)
            {
                Unlink(data, id, diagnostics, data.IsOpened
                    ? "trapped entity gone on release, not a kill - looking for the freed monster"
                    : "trapped monster unloaded or replaced");
                return;
            }

            if (data.MonsterMissingSinceMs == null)
            {
                data.MonsterMissingSinceMs = now;
                if (diagnostics)
                    LogDiagnostic($"#{data.EntityId} id={data.Id}: freed monster id={id} missing, last seen {data.MonsterLastDistance:F0} grid away at {data.MonsterLastHpFraction:P0} life");
                return;
            }

            if (now - data.MonsterMissingSinceMs.Value < MONSTER_VANISH_GRACE_MS)
                return;

            if (data.MonsterLastDistance <= MONSTER_VANISH_KILL_DISTANCE && data.MonsterLastHpFraction < 1f)
                HandleMonsterKilled(data, $"vanished {data.MonsterLastDistance:F0} grid away at {data.MonsterLastHpFraction:P0} life");
        }

        private void Unlink(EssenceEntityData data, uint id, bool diagnostics, string reason)
        {
            _session.ByMonsterId.Remove(id);
            data.MonsterId = null;
            data.Monster = null;
            data.MonsterMissingSinceMs = null;

            if (diagnostics)
                LogDiagnostic($"#{data.EntityId} id={data.Id}: unlinked monster id={id}: {reason}");
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
        /// Current life plus energy shield as a fraction of max (0..1); 1 when unreadable, so an unknown
        /// never reads as damaged.
        /// </summary>
        private static float HpFraction(Entity monster)
        {
            try
            {
                if (!monster.TryGetComponent<Life>(out var life) || life == null)
                    return 1f;

                var max = life.MaxHP + life.MaxES;
                return max > 0 ? Math.Clamp((life.CurHP + life.CurES) / (float)max, 0f, 1f) : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>
        /// Offers a monster to the essences looking for one.
        ///
        /// Trapped: the frozen monster is the monolith, so it stands on it - the nearest monolith within
        /// <see cref="MONSTER_LINK_RADIUS"/>, carrying the MonsterInsideMonolith stat. Position only
        /// pairs it with its monolith here; from then on it is followed by ID.
        ///
        /// Freed (only when the trapped entity was replaced on release): recognised anywhere in the
        /// loaded area by the trapped monster's identity - same essence mods (or, if those couldn't be
        /// read, the same metadata path and name) and rarity - never by where it stands.
        /// </summary>
        private void ConsiderCandidate(Entity monster)
        {
            // Distance first because it is cheap; the stats are only read for a monster standing
            // on a monolith.
            if (_seekingTrapped.Count > 0 &&
                FindNearest(_seekingTrapped, monster.GridPosNum, MONSTER_LINK_RADIUS, out var trapped, out var trappedDistance) &&
                IsImprisoned(monster, useCache: true) == true)
            {
                var mods = ReadEssenceMods(monster);
                Offer(trapped, new MonsterCandidate(monster, string.IsNullOrEmpty(mods) ? 0 : 1, RarityRank(monster), trappedDistance, mods));
            }

            if (_seekingFreed.Count > 0 && !_leftoverMonsters.Contains(monster.Id))
            {
                foreach (var data in _seekingFreed)
                {
                    if (MatchFreedMonster(data, monster) is { } candidate)
                        Offer(data, candidate);
                }
            }
        }

        /// <summary>
        /// The monster as a candidate for this essence's freed monster, or null if it isn't one. Cheap
        /// cached reads first (rarity, path); mods and stats only for a monster that passed them.
        /// </summary>
        private MonsterCandidate? MatchFreedMonster(EssenceEntityData data, Entity monster)
        {
            var rank = RarityRank(monster);
            if (rank != data.MonsterRarityRank)
                return null;

            string? mods = null;
            if (!string.IsNullOrEmpty(data.MonsterEssenceMods))
            {
                mods = ReadEssenceMods(monster);
                if (mods != data.MonsterEssenceMods)
                    return null;
            }
            else if (monster.Path != data.MonsterPath)
            {
                return null;
            }

            var sameName = monster.RenderName == data.MonsterRenderName;
            if (mods == null && !sameName)
                return null; // without a mod fingerprint, path alone is not enough

            if (!monster.IsAlive || !monster.IsHostile || IsImprisoned(monster, useCache: false) != false)
                return null;

            // Same mods and same rare name is the strongest match; the name breaks a tie between two
            // essences holding the same essences.
            return new MonsterCandidate(monster, sameName ? 1 : 0, rank, 0f, mods);
        }

        private static bool FindNearest(List<EssenceEntityData> essences, Vector2 position, float radius,
            [NotNullWhen(true)] out EssenceEntityData? nearest, out float nearestDistance)
        {
            nearest = null;
            nearestDistance = radius;
            foreach (var data in essences)
            {
                var distance = Vector2.Distance(position, data.GridPos!.Value);
                if (distance < nearestDistance)
                {
                    nearest = data;
                    nearestDistance = distance;
                }
            }

            return nearest != null;
        }

        private void Offer(EssenceEntityData data, MonsterCandidate candidate)
        {
            if (!_bestCandidates.TryGetValue(data.Id, out var best) || candidate.IsBetterThan(best))
                _bestCandidates[data.Id] = candidate;
        }

        /// <summary>
        /// Links each essence to the best monster seen for it this pass. A trapped link is replaced
        /// only by a higher-ranked prisoner, so an add that happened to be linked first is swapped out
        /// as soon as the essence monster is seen. After release a recognised freed monster replaces
        /// an unconfirmed link - that link is the trapped entity, which hasn't shown itself free.
        /// </summary>
        private void ApplyBestMonsterLinks(long now, bool diagnostics)
        {
            foreach (var (essenceId, candidate) in _bestCandidates)
            {
                var monster = candidate.Monster!;
                if (!_session.Essences.TryGetValue(essenceId, out var data) || data.MonsterId == monster.Id ||
                    _session.ByMonsterId.ContainsKey(monster.Id))
                    continue;

                var previous = data.MonsterId;
                if (previous != null)
                {
                    var current = new MonsterCandidate(null, string.IsNullOrEmpty(data.MonsterEssenceMods) ? 0 : 1, data.MonsterRarityRank, data.MonsterLinkDistance, null);
                    if (!data.IsOpened && !candidate.IsBetterThan(current))
                        continue;

                    _session.ByMonsterId.Remove(previous.Value);
                    if (data.IsOpened)
                        _leftoverMonsters.Add(previous.Value);
                }

                data.MonsterId = monster.Id;
                data.Monster = monster;
                data.MonsterLinkDistance = candidate.Distance;
                data.MonsterMissingSinceMs = null;
                data.MonsterLastDistance = monster.DistancePlayer;
                _session.ByMonsterId[monster.Id] = data;

                if (data.IsOpened)
                {
                    // Freed candidates are alive and free by construction; the trapped identity stays.
                    data.MonsterFreed = true;
                    data.MonsterLastHpFraction = HpFraction(monster);
                }
                else
                {
                    RecordIdentity(data, monster, now, candidate.Mods);
                }

                if (diagnostics)
                {
                    LogDiagnostic($"#{data.EntityId} id={data.Id}: linked {(data.IsOpened ? "freed" : "trapped")} monster id={monster.Id} " +
                                  $"({monster.RenderName}, {RarityName(candidate.Rarity)}, {DescribeMods(candidate.Mods)}" +
                                  (data.IsOpened ? ")" : $", {candidate.Distance:F1} grid from the monolith)") +
                                  (previous != null ? $", replacing id={previous}" : ""));
                }
            }
        }

        /// <summary>
        /// Remembers who the trapped monster is, so the freed one can be recognised if the game
        /// replaces the entity on release. A failed mod read keeps the last good fingerprint.
        /// </summary>
        private static void RecordIdentity(EssenceEntityData data, Entity monster, long now, string? mods = null)
        {
            data.MonsterPath = monster.Path;
            data.MonsterRenderName = monster.RenderName;
            data.MonsterRarityRank = RarityRank(monster);
            data.MonsterEssenceMods = mods ?? ReadEssenceMods(monster) ?? data.MonsterEssenceMods;
            data.MonsterIdentityReadMs = now;
        }

        /// <summary>
        /// The monster's essence mods (see <see cref="IsEssenceMonsterMod"/>), sorted and comma-joined:
        /// "" when it has none, null when they can't be read.
        /// </summary>
        private static string? ReadEssenceMods(Entity monster)
        {
            try
            {
                if (!monster.TryGetComponent<ObjectMagicProperties>(out var magic) || magic?.Mods is not { } mods)
                    return null;

                List<string>? essenceMods = null;
                foreach (var mod in mods)
                {
                    if (IsEssenceMonsterMod(mod))
                        (essenceMods ??= []).Add(mod);
                }

                if (essenceMods == null)
                    return "";

                essenceMods.Sort(StringComparer.Ordinal);
                return string.Join(",", essenceMods);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Whether a monster mod ID is one the essence monster gets from the essences it holds. In the
        /// PoE1 game data (RePoE mods.json) those are the monster mods of generation type "essence":
        /// the "Essence..." ones (e.g. EssenceDaemonModLifeAndDamageLevel3, DecayOnHitEssenceMonster1)
        /// and the RemnantOfCorruption ones. The life/damage mod only comes in eight variants (Level1-7
        /// and Corrupt), so the fingerprint alone isn't unique - rarity and the rare's name narrow it
        /// down. The pack around the monolith carries "MonsterSupporterEssence..." mods (generation
        /// type "unique"), which don't identify the essence monster.
        /// </summary>
        internal static bool IsEssenceMonsterMod(string? mod) =>
            !string.IsNullOrEmpty(mod) &&
            (mod.StartsWith("RemnantOfCorruption", StringComparison.Ordinal) ||
             (mod.Contains("Essence", StringComparison.Ordinal) && !mod.StartsWith("MonsterSupporter", StringComparison.Ordinal)));

        private static string DescribeMods(string? mods) => mods switch
        {
            null => "mods unreadable",
            "" => "no essence mods",
            _ => $"mods {mods}",
        };

        /// <summary>
        /// Whether the monster is still imprisoned in a monolith (MonsterInsideMonolith stat). Reads
        /// are cached briefly per monster for the candidate search, as the pack around a monolith is
        /// re-checked every pass; the linked monster's release is read fresh.
        /// </summary>
        /// <returns>Null when the stats can't be read - neither imprisoned nor free is assumed then.</returns>
        private bool? IsImprisoned(Entity monster, bool useCache)
        {
            var now = _clock.ElapsedMilliseconds;
            if (useCache && _imprisonedChecks.TryGetValue(monster.Id, out var check) && now - check.CheckedAtMs < IMPRISONED_CHECK_TTL_MS)
                return check.Imprisoned;

            bool imprisoned;
            try
            {
                var stats = monster.Stats;
                if (stats == null)
                    return null;

                imprisoned = stats.GetValueOrDefault(GameStat.MonsterInsideMonolith) > 0;
            }
            catch
            {
                return null; // unreadable stats - don't cache, just try again next pass
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

        /// <summary>The freed essence monster died - this is the "killed" event, and when its outcome is counted.</summary>
        private void HandleMonsterKilled(EssenceEntityData data, string signal)
        {
            data.Phase = EssencePhase.Killed;
            var stats = _session.Stats;

            switch (data.Decision)
            {
                case EssenceDecision.Corrupted:
                    stats.IncrementCorrupted();
                    if (DiagnosticsOn)
                        DebugWindow.LogMsg($"SUCCESSFUL CORRUPTION CONFIRMED: Essence {data.EntityId} killed", 3, SharpDX.Color.LightGreen);
                    break;

                case EssenceDecision.WronglyCorrupted:
                    stats.IncrementMistakes();
                    if (DiagnosticsOn)
                        DebugWindow.LogMsg($"MISTAKEN CORRUPTION CONFIRMED: Essence {data.EntityId} killed", 3, SharpDX.Color.OrangeRed);
                    break;

                case EssenceDecision.MissedCorruption:
                    stats.IncrementMissed();
                    _corruptionLog.RecordMissed();
                    if (DiagnosticsOn)
                        DebugWindow.LogMsg($"MISSED CORRUPTION CONFIRMED: Essence {data.EntityId} killed without corrupting", 3, SharpDX.Color.Orange);
                    break;
            }

            if (data.Corruption is { } corruption)
                RecordOutcome(data, corruption.Before, corruption.After, data.Decision == EssenceDecision.Corrupted);

            stats.IncrementKilled();
            _corruptionLog.RecordKill();

            if (DiagnosticsOn)
                DebugWindow.LogMsg($"Essence {data.EntityId}: monster id={data.MonsterId} killed ({signal})", 3, SharpDX.Color.Cyan);
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

            foreach (var data in _session.Essences.Values)
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
                $"{_session.Essences.Count} tracked ({opened} released, {linked} with a linked monster, {killed} monsters killed), {withLabel} with a visible label";

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

        /// <summary>Logs a freed monster's state, but only when it differs from the last one.</summary>
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
        /// Validates a not-yet-tracked monolith entity before tracking starts. IsTargetable is
        /// checked separately: a monolith stops being targetable once opened.
        /// </summary>
        private static bool IsValidMonolith(Entity entity)
        {
            try
            {
                if (entity.Address == 0 || !entity.IsValid || !entity.HasComponent<Render>() || !entity.HasComponent<Monolith>())
                    return false;

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
    /// guesses: "entity visits" is what the tracker actually touched, next to the size of the entity
    /// list it avoids walking.
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
