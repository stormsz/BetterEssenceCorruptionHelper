using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Models;
using BetterEssenceCorruptionHelper.Pricing;
using BetterEssenceCorruptionHelper.Strategy;
using ImGuiNET;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace BetterEssenceCorruptionHelper.UI
{
    /// <summary>
    /// The plugin's Strategy page (picker, setup alert, the strategy's one knob, the essence grid with
    /// its legend) and the diagnostics block under Debug &amp; Diagnostics (copy buttons, item mapping,
    /// game-files check). Drawn through CustomNodes, like the Beasts plugin's beast picker. Drawing
    /// only reads cached data (price snapshot, values, detector state).
    /// </summary>
    internal sealed class CatalogueView(
        Settings settings,
        PriceBook prices,
        EssenceValues values,
        SetupDetector setup,
        NinjaPriceBridge bridge,
        EssenceEntityTracker tracker,
        CorruptionLog corruptionLog,
        EssenceIcons icons,
        Func<long> clockMs)
    {
        private static readonly Vector4 WarnColor = new(1f, 0.78f, 0.3f, 1f);
        private static readonly Vector4 GoodColor = new(0.45f, 0.9f, 0.45f, 1f);
        private static readonly Vector4 VaalCell = new(0.20f, 0.50f, 0.25f, 0.55f);
        private static readonly Vector4 AlwaysCell = new(0.22f, 0.68f, 0.30f, 0.95f);
        private static readonly Vector4 NeverCell = new(0.62f, 0.18f, 0.16f, 0.75f);
        private static readonly Vector4 MissingCell = new(0f, 0f, 0f, 0.4f);
        private static readonly Vector4 BandRow = new(1f, 1f, 1f, 0.035f);
        private static readonly Vector4 SelectedOutline = new(1f, 1f, 1f, 0.85f);
        private static readonly Vector4 ProblemBox = new(0.45f, 0.08f, 0.06f, 0.85f);
        private static readonly Vector4 WarningBox = new(0.40f, 0.30f, 0.05f, 0.75f);

        /// <summary>Type name colours after poewiki's essence icons: A blue, B green, C amber, D red, E purple, F white.</summary>
        private static Vector4 GroupColor(EssenceGroup group) => group switch
        {
            EssenceGroup.A => new Vector4(0.50f, 0.62f, 1.00f, 1f),
            EssenceGroup.B => new Vector4(0.38f, 0.86f, 0.55f, 1f),
            EssenceGroup.C => new Vector4(0.96f, 0.74f, 0.30f, 1f),
            EssenceGroup.D => new Vector4(1.00f, 0.45f, 0.42f, 1f),
            EssenceGroup.E => new Vector4(0.80f, 0.52f, 1.00f, 1f),
            _ => new Vector4(0.95f, 0.95f, 0.95f, 1f),
        };

        /// <summary>
        /// The small top table. Under MEDS corruption it holds MEDS and what they corrupt into, with only
        /// the tiers they come in; under the other strategies MEDS are ordinary group E rows of the main
        /// table, and the top table is just the corrupted essences.
        /// </summary>
        private static readonly EssenceGroup[] MedsGroups = [EssenceGroup.F, EssenceGroup.E];

        private static readonly EssenceTier[] MedsTiers =
            [EssenceTier.Corrupted, EssenceTier.Deafening, EssenceTier.Shrieking, EssenceTier.Screaming];

        private static readonly EssenceGroup[] CorruptedGroups = [EssenceGroup.F];

        private static readonly EssenceTier[] CorruptedTiers = [EssenceTier.Corrupted];

        /// <summary>The main table: the other groups, highest first, most valuable tier on the left.</summary>
        private static readonly EssenceGroup[] OtherGroups = [EssenceGroup.D, EssenceGroup.C, EssenceGroup.B, EssenceGroup.A];

        private static readonly EssenceGroup[] OtherGroupsWithMeds = [EssenceGroup.E, .. OtherGroups];

        private static readonly EssenceTier[] OtherTiers =
        [
            EssenceTier.Deafening, EssenceTier.Shrieking, EssenceTier.Screaming, EssenceTier.Wailing,
            EssenceTier.Weeping, EssenceTier.Muttering, EssenceTier.Whispering,
        ];

        private readonly Settings _settings = settings;
        private readonly PriceBook _prices = prices;
        private readonly EssenceValues _values = values;
        private readonly SetupDetector _setup = setup;
        private readonly NinjaPriceBridge _bridge = bridge;
        private readonly EssenceEntityTracker _tracker = tracker;
        private readonly CorruptionLog _corruptionLog = corruptionLog;
        private readonly EssenceIcons _icons = icons;
        private readonly Func<long> _clockMs = clockMs;

        /// <summary>Corrupting a monster holding only this essence, valid for <see cref="_profitKey"/>.</summary>
        private readonly Dictionary<EssenceEntry, CorruptionAssessment?> _singleProfit = new();
        private (int, int, int) _profitKey = (-1, -1, -1);

        private EssenceEntry? _selected;
        private float _legendHeight;
        private List<(string Metadata, string BaseName)>? _uncatalogued;
        private string? _copyNote;

        private TrackingPreferences Preferences => _settings.Tracking;

        /// <summary>The Strategy page, straight under the HUD's own "Strategy" section - no inner tabs.</summary>
        public void Draw()
        {
            ImGui.Spacing();
            DrawStrategy();
            ImGui.Spacing();
            ImGui.Spacing();
        }

        #region Strategy

        private void DrawStrategy()
        {
            var preferences = Preferences;
            var strategy = preferences.Strategy;

            ImGui.SetNextItemWidth(300);
            if (ImGui.BeginCombo("##strategy", strategy.DisplayName))
            {
                foreach (var candidate in Strategies.All)
                {
                    if (ImGui.Selectable(candidate.DisplayName, candidate.Id == strategy.Id))
                        preferences.SelectStrategy(candidate.Id);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(candidate.Summary);
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(460);
                ImGui.Text(strategy.Summary);
                ImGui.Spacing();
                foreach (var note in strategy.Notes)
                    ImGui.BulletText(note);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }

            if (SetupStatus.Describe(strategy, _setup) is { } status)
            {
                ImGui.Spacing();
                DrawAlert(status);
            }

            // Profit is nothing but prices: without them, say so instead of a table of dashes.
            if (strategy.Mode == StrategyMode.Profit && NinjaPriceProblem() is { } missing)
            {
                ImGui.Spacing();
                DrawAlert(missing);
                ImGui.Spacing();
                if (ImGui.SmallButton("Check again"))
                    _prices.Invalidate(reResolveProvider: true);
                return;
            }

            switch (strategy.Mode)
            {
                case StrategyMode.Profit:
                    ImGui.Spacing();
                    var profit = (float)preferences.EffectiveMinExpectedProfit;
                    ImGui.SetNextItemWidth(160);
                    if (ImGui.SliderFloat("Vaal when the expected profit is at least", ref profit, 0f, 20f, "%.1f chaos"))
                    {
                        preferences.MinExpectedProfit = profit;
                        preferences.Changed();
                    }
                    break;

                case StrategyMode.FixedSet:
                    ImGui.Spacing();
                    var pack = preferences.EffectiveMinPackSize;
                    ImGui.SetNextItemWidth(160);
                    if (ImGui.SliderInt("Also Vaal monsters with this many essences (0 = off)", ref pack, 0, 12))
                    {
                        preferences.MinPackSize = pack;
                        preferences.Changed();
                    }
                    break;
            }

            ImGui.Spacing();
            ImGui.Spacing();
            DrawPriceRow();
            ImGui.Spacing();
            var medsStrategy = strategy.Mode == StrategyMode.FixedSet;
            if (medsStrategy)
                DrawGrid("essenceMedsGrid", MedsGroups, MedsTiers);
            else
                DrawGrid("essenceCorruptedGrid", CorruptedGroups, CorruptedTiers);
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Spacing();
            DrawGrid(medsStrategy ? "essenceOtherGrid" : "essenceAllGrid", medsStrategy ? OtherGroups : OtherGroupsWithMeds, OtherTiers);
            var gridHeight = ImGui.GetItemRectSize().Y;

            // Legend to the right of the main table, bottom-aligned with it: a spacer inside the group
            // pushes it down (its height is last frame's - it only changes when the strategy does).
            ImGui.SameLine(0, 14);
            ImGui.BeginGroup();
            ImGui.Dummy(new Vector2(1, Math.Max(0, gridHeight - _legendHeight)));
            ImGui.BeginGroup();
            DrawLegend();
            ImGui.EndGroup();
            _legendHeight = ImGui.GetItemRectSize().Y;
            ImGui.EndGroup();
        }

        /// <summary>Why the Profit strategy can't price anything right now, or null when Ninja Price is ready.</summary>
        private SetupStatus.Status? NinjaPriceProblem() => _prices.Status switch
        {
            ProviderStatus.Ready => null,
            ProviderStatus.Unavailable => new SetupStatus.Status(SetupStatus.Level.Problem,
                "Profit needs the Ninja Price plugin",
                "Install and enable Ninja Price. Until then, only monsters holding a MEDS essence get a Vaal Orb."),
            ProviderStatus.EssenceDataMissing => new SetupStatus.Status(SetupStatus.Level.Problem,
                "Ninja Price has no essence prices",
                "Turn on Essence valuation in Ninja Price's settings. Until then, only MEDS get a Vaal Orb."),
            _ => new SetupStatus.Status(SetupStatus.Level.Warning,
                "Waiting for Ninja Price",
                "It's still loading prices. Until then, only monsters holding a MEDS essence get a Vaal Orb."),
        };

        /// <summary>A coloured box: a direct instruction and one short sentence of why.</summary>
        private static void DrawAlert(SetupStatus.Status status)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(10, 6));
            if (ImGui.BeginTable($"##bechAlert{status.Title}", 1, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableNextRow();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(status.Level == SetupStatus.Level.Problem ? ProblemBox : WarningBox));
                ImGui.TableNextColumn();
                ImGui.Text(status.Title);
                ImGui.Text(status.Text);
                ImGui.EndTable();
            }
            ImGui.PopStyleVar();
        }

        private void DrawPriceRow()
        {
            var vaal = _prices.VaalOrb;
            ImGui.AlignTextToFramePadding();
            var problem = _prices.Status switch
            {
                ProviderStatus.Loading => "Ninja Price is still loading prices.",
                ProviderStatus.EssenceDataMissing => "Ninja Price has no essence prices - is Essence valuation off in its settings?",
                ProviderStatus.Unavailable => "Ninja Price isn't running - enable it to see prices.",
                _ => null,
            };
            if (problem != null)
                ImGui.TextColored(WarnColor, problem);
            else
                ImGui.Text($"Vaal Orb {Format.Chaos(vaal.Chaos)}");

            ImGui.SameLine(0, 16);
            if (ImGui.SmallButton("Refresh prices"))
                _prices.Invalidate(reResolveProvider: true);
            if (ImGui.IsItemHovered() && _prices.LastSampleMs is { } sampled)
                ImGui.SetTooltip($"Prices from Ninja Price, updated {Format.Age(_clockMs() - sampled)} ago. They refresh on their own.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy prices"))
                Copy(BuildPricesTsv(), "prices");
            if (_copyNote != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(_copyNote);
            }
        }

        #endregion

        #region Grid

        /// <summary>
        /// One of the two essence tables - the same for every strategy; only the cell colours change.
        /// poewiki's essence table turned sideways: one row per type (with its icon), one column per tier,
        /// each cell the essence's value. Columns fit their content and can be resized (double-click a
        /// border to fit again); the table is exactly as wide and tall as its content.
        /// </summary>
        private void DrawGrid(string id, EssenceGroup[] groups, EssenceTier[] tiers)
        {
            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit |
                                          ImGuiTableFlags.Resizable | ImGuiTableFlags.NoHostExtendX;
            if (!ImGui.BeginTable(id, 1 + tiers.Length, flags))
                return;

            ImGui.TableSetupColumn("Essence");
            foreach (var tier in tiers)
                ImGui.TableSetupColumn(tier.ToString());
            ImGui.TableHeadersRow();

            var now = _clockMs();
            var band = false;
            foreach (var group in groups)
            {
                foreach (var type in EssenceCatalogue.TypesIn(group))
                    DrawGridRow(type, group, tiers, band, now);
                band = !band;
            }

            ImGui.EndTable();
        }

        private void DrawGridRow(string type, EssenceGroup group, EssenceTier[] tiers, bool band, long now)
        {
            var preferences = Preferences;
            var mode = preferences.Strategy.Mode;
            var entries = EssenceCatalogue.OfType(type).ToList();

            ImGui.TableNextRow();
            if (band)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(BandRow));

            ImGui.TableNextColumn();
            var size = ImGui.GetTextLineHeight();
            if (_icons.For(type) is { } icon)
            {
                ImGui.Image(icon, new Vector2(size, size));
                ImGui.SameLine(0, 4);
            }

            ImGui.PushStyleColor(ImGuiCol.Text, GroupColor(group));
            var clicked = ImGui.Selectable($"{type}##type{type}");
            ImGui.PopStyleColor();

            if (mode == StrategyMode.Profit && group != EssenceGroup.F)
            {
                if (clicked)
                    CycleType(entries);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    entries.ForEach(e => preferences.SetChoice(e, EssenceChoice.Auto));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Click: every {type} tier to always, then never, then automatic. Right-click: automatic.");
            }

            foreach (var tier in tiers)
            {
                ImGui.TableNextColumn();
                if (EssenceCatalogue.Find(type, tier) is { } entry)
                    DrawGridCell(entry, mode, now);
                else
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(MissingCell));
            }
        }

        private void CycleType(List<EssenceEntry> entries)
        {
            var choices = entries.Select(Preferences.Choice).Distinct().ToList();
            var next = choices.Count == 1
                ? choices[0] switch { EssenceChoice.Auto => EssenceChoice.Always, EssenceChoice.Always => EssenceChoice.Never, _ => EssenceChoice.Auto }
                : EssenceChoice.Auto;
            entries.ForEach(e => Preferences.SetChoice(e, next));
        }

        /// <summary>A cell's background and what it means, in plain words, under the current strategy.</summary>
        private (Vector4? Background, string Meaning) CellState(EssenceEntry entry, StrategyMode mode)
        {
            if (entry.IsCorruptionOnly)
                return (null, "Only from a Vaal Orb on a monster holding a MEDS essence.");

            var preferences = Preferences;
            switch (mode)
            {
                case StrategyMode.FixedSet:
                    return preferences.Strategy.CorruptsByRule(entry)
                        ? (VaalCell, "Monsters holding it get a Vaal Orb.")
                        : (null, "Monsters holding it are killed as they are.");

                case StrategyMode.NoCorruption:
                    return (null, "Deafening farming never uses Vaal Orbs.");
            }

            var assessment = SingleProfit(entry);
            var profit = assessment?.ExpectedNetGain;
            var outcomes = assessment != null ? OutcomeText(entry, assessment) : "No price to judge it by.";
            return preferences.Choice(entry) switch
            {
                EssenceChoice.Always => (AlwaysCell, $"{outcomes}\n\nAlways Vaal (your pick). Click for never."),
                EssenceChoice.Never => (NeverCell, $"{outcomes}\n\nNever Vaal (your pick). Click for automatic."),
                _ when profit >= preferences.EffectiveMinExpectedProfit => (VaalCell, $"{outcomes}\n\nWorth it. Click to always Vaal."),
                _ => (null, $"{outcomes}\n\nNot worth it on its own. Click to always Vaal."),
            };
        }

        /// <summary>
        /// What a Vaal Orb can do to a monster holding only this essence (poewiki, "Corrupting essence
        /// monsters"), what each outcome is worth, and the expected result after the orb.
        /// </summary>
        private string OutcomeText(EssenceEntry entry, CorruptionAssessment assessment)
        {
            var sb = new StringBuilder("A Vaal Orb on a monster holding only this can:\n");
            foreach (var outcome in assessment.Outcomes)
            {
                sb.Append("  ").Append(outcome.Outcome switch
                {
                    CorruptionOutcome.NoChange => "do nothing: +0c",
                    CorruptionOutcome.TierUpgrade when EssenceCatalogue.UpgradeOf(entry) is { } up && up != entry =>
                        $"upgrade it to {up.Name}: {Format.SignedChaos(outcome.MinGain)}",
                    CorruptionOutcome.TierUpgrade => "upgrade it: already Deafening, +0c",
                    CorruptionOutcome.AddEssence => "add another essence: counted as +0c",
                    CorruptionOutcome.Transform => $"turn it into a random {TransformTarget(entry)}: {Format.SignedChaos(outcome.UniformMeanGain ?? 0)} on average " +
                                                   $"({Format.SignedChaos(outcome.MinGain)} to {Format.SignedChaos(outcome.MaxGain)})",
                    _ => outcome.Outcome.ToString(),
                }).Append('\n');
            }

            sb.Append($"Each counted as equally likely: {Format.SignedChaos(assessment.ExpectedNetGain)} expected after the {Format.Chaos(assessment.VaalOrbCost)} orb.");
            return sb.ToString();
        }

        private static string TransformTarget(EssenceEntry entry)
        {
            var candidates = EssenceCatalogue.TransformCandidates(entry);
            return entry.IsMeds
                ? "Hysteria, Insanity, Horror or Delirium"
                : $"{candidates[0].TierName} {string.Join("/", candidates.Select(c => c.TypeName))}";
        }

        private void DrawGridCell(EssenceEntry entry, StrategyMode mode, long now)
        {
            var value = _values.Get(entry);
            var (background, meaning) = CellState(entry, mode);
            if (background is { } bg)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(bg));

            var textColor = value.Source switch
            {
                ValueSource.Market => ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
                ValueSource.Ignored => WarnColor,
                _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
            };

            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            if (ImGui.Selectable($"{Format.Value(value)}##cell{entry.Name}", false))
            {
                _selected = entry;
                if (mode == StrategyMode.Profit)
                    Preferences.CycleChoice(entry);
            }
            ImGui.PopStyleColor();

            if (mode == StrategyMode.Profit && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                Preferences.SetChoice(entry, EssenceChoice.Auto);

            if (_selected == entry)
                ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(SelectedOutline));

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{entry.Name}: {(value.Chaos != null ? Format.Value(value) : "no price")}\n{Format.ValueExplanation(value)}\n\n{meaning}");
        }

        /// <summary>
        /// Expected gain for a monster holding only this essence, cached until prices, setup or choices
        /// change. With Crystal Resonance everything would be a loss, so the table previews the setup
        /// without it (the alert says so); live decisions still use the real setup.
        /// </summary>
        private CorruptionAssessment? SingleProfit(EssenceEntry entry)
        {
            var key = (_values.Version, _setup.Version, Preferences.Version);
            if (key != _profitKey)
            {
                _singleProfit.Clear();
                _profitKey = key;
            }

            if (_singleProfit.TryGetValue(entry, out var cached))
                return cached;

            var preview = _setup.Resolve() with { CrystalResonance = false };
            var context = new ProfitContext(_values.ValueOf, _values.IsApproximate, _prices.VaalOrb.Value, preview);
            var assessment = context.Assess([entry], 0);
            _singleProfit[entry] = assessment;
            return assessment;
        }

        /// <summary>Only what the current strategy's table actually uses, in a few short words each.</summary>
        private void DrawLegend()
        {
            switch (Preferences.Strategy.Mode)
            {
                case StrategyMode.Profit:
                    Swatch(VaalCell, "Worth a Vaal");
                    Swatch(AlwaysCell, "Always (you)");
                    Swatch(NeverCell, "Never (you)");
                    DrawResetPicks();
                    break;
                case StrategyMode.FixedSet:
                    Swatch(VaalCell, "Gets a Vaal");
                    break;
            }

            Sample(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], "no price");
            Sample(WarnColor, "odd price");
        }

        /// <summary>
        /// Puts every always/never pick back to automatic. Always there (greyed out with no picks), so
        /// the legend keeps its size and the way back is easy to find.
        /// </summary>
        private void DrawResetPicks()
        {
            var picks = Preferences.EssenceOverrides.Count;
            ImGui.BeginDisabled(picks == 0);
            if (ImGui.SmallButton(picks > 0 ? $"Reset picks ({picks})" : "Reset picks"))
                Preferences.ClearChoices();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(picks > 0
                    ? "Back to automatic for every essence you set to always or never.\nRight-click a cell or a row name to reset just that one."
                    : "No picks. Click a cell to always Vaal it, again for never.");
            ImGui.Spacing();
        }

        private static void Swatch(Vector4 color, string label)
        {
            ImGui.ColorButton($"##swatch{label}", color with { W = Math.Max(color.W, 0.6f) },
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker | ImGuiColorEditFlags.NoDragDrop,
                new Vector2(ImGui.CalcTextSize("<1c").X, ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            ImGui.Text(label);
        }

        /// <summary>A "&lt;1c" in the cell's colour, then what it means in normal text.</summary>
        private static void Sample(Vector4 color, string label)
        {
            ImGui.TextColored(color, "<1c");
            ImGui.SameLine();
            ImGui.Text(label);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(label == "no price"
                    ? "Ninja Price has no price for it, so it counts as cheap."
                    : "Ninja Price's price is above the tier above it - an odd trade - so it counts as cheap.");
        }

        #endregion

        #region Diagnostics

        /// <summary>The diagnostics block, drawn at the end of the Debug &amp; Diagnostics section.</summary>
        public void DrawDiagnostics()
        {
            ImGui.Spacing();
            if (ImGui.Button("Copy mapping"))
                Copy(BuildMappingTsv(), "mapping");
            ImGui.SameLine();
            if (ImGui.Button("Copy map setup & stats"))
                Copy(BuildSetupText(), "map setup");
            ImGui.SameLine();
            if (ImGui.Button("Copy corruption log"))
                Copy(BuildCorruptionCsv(), "corruption log");
            ImGui.SameLine();
            if (ImGui.Button("Copy performance"))
                Copy(BuildPerformanceText(), "performance");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How much work the essence tracker does per second, for troubleshooting.");

            ImGui.Spacing();
            ImGui.Spacing();
            DrawMappingTable();
            ImGui.Spacing();
            ImGui.Spacing();

            if (ImGui.Button("Check game files for new essences"))
            {
                _uncatalogued = _bridge.FindUncataloguedEssences()
                    .Where(u => !EssenceCatalogue.KnownExclusions.ContainsKey(u.Metadata))
                    .ToList();
            }

            if (_uncatalogued == null)
                return;

            ImGui.SameLine();
            if (_uncatalogued.Count == 0)
                ImGui.TextColored(GoodColor, "Nothing new.");
            else
                ImGui.TextColored(WarnColor, $"Not in the catalogue: {string.Join(", ", _uncatalogued.Select(u => $"{u.BaseName} ({u.Metadata})"))}");
        }

        private void Copy(string text, string what)
        {
            ImGui.SetClipboardText(text);
            _copyNote = $"copied {what}";
        }

        private IEnumerable<(EssenceEntry? Entry, MarketItem Item)> MappingItems() =>
            new[] { ((EssenceEntry?)null, EssenceCatalogue.VaalOrb) }.Concat(EssenceCatalogue.All.Select(e => ((EssenceEntry?)e, e.Item)));

        private void DrawMappingTable()
        {
            const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit |
                                          ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY;
            if (!ImGui.BeginTable("bechMapping", 5, flags, new Vector2(0, 240)))
                return;

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Game files");
            ImGui.TableSetupColumn("Ninja Price");
            ImGui.TableSetupColumn("Value used");
            ImGui.TableSetupColumn("Metadata");
            ImGui.TableHeadersRow();

            foreach (var (entry, item) in MappingItems())
            {
                var mapping = _bridge.GetMapping(item);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(item.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(mapping.Status == ItemMappingStatus.Verified ? GoodColor : WarnColor,
                    mapping.Status == ItemMappingStatus.NameMismatch ? $"game says '{mapping.GameBaseName}'" : mapping.Status.ToString());
                ImGui.TableNextColumn();
                ImGui.Text(Format.Price(_prices.Get(item)));
                ImGui.TableNextColumn();
                ImGui.Text(entry == null ? Format.Price(_prices.Get(item)) : Format.Value(_values.Get(entry)));
                ImGui.TableNextColumn();
                ImGui.TextDisabled(item.Metadata);
            }

            ImGui.EndTable();
        }

        private string BuildPricesTsv()
        {
            var sb = new StringBuilder("name\tgroup\ttier\tpoe_ninja_chaos\tvalue_chaos\tvalue_source\tchoice\n");
            foreach (var entry in EssenceCatalogue.All)
            {
                var value = _values.Get(entry);
                sb.Append(entry.Name).Append('\t').Append(entry.Group).Append('\t').Append(entry.TierName).Append('\t')
                    .Append(Num(value.MarketChaos)).Append('\t').Append(Num(value.Chaos)).Append('\t')
                    .Append(value.Source).Append('\t').Append(Preferences.Choice(entry)).Append('\n');
            }

            sb.Append("Vaal Orb\t\t\t").Append(Num(_prices.VaalOrb.Value)).Append('\t').Append(Num(_prices.VaalOrb.Value)).Append("\tMarket\t\n");
            return sb.ToString();
        }

        private string BuildMappingTsv()
        {
            var sb = new StringBuilder("name\ttier\tgame_basename\tgame_class\tmetadata\tmapping\tmarket_feed\tmarket_key\tprice_state\tchaos_per_item\n");
            foreach (var (entry, item) in MappingItems())
            {
                var mapping = _bridge.GetMapping(item);
                var price = _prices.Get(item);
                sb.Append(item.Name).Append('\t')
                    .Append(entry?.TierName ?? "-").Append('\t')
                    .Append(mapping.GameBaseName ?? "").Append('\t')
                    .Append(mapping.GameClassName ?? "").Append('\t')
                    .Append(item.Metadata).Append('\t')
                    .Append(mapping.Status).Append('\t')
                    .Append(item.Feed).Append('\t')
                    .Append(mapping.GameBaseName ?? item.Name).Append('\t')
                    .Append(price.State).Append('\t')
                    .Append(Num(price.Value)).Append('\n');
            }

            return sb.ToString();
        }

        private string BuildSetupText()
        {
            var sb = new StringBuilder();
            sb.Append("strategy\t").Append(Preferences.Strategy.Id).Append('\n');
            sb.Append("crystal_resonance_allocated\t").Append(_setup.CrystalResonanceAllocated?.ToString() ?? "unknown").Append('\n');
            sb.Append("current_map\t").Append(_setup.CurrentMap?.ToString() ?? "not in a map").Append('\n');
            sb.Append("last_map\t").Append(_setup.LastMap?.ToString() ?? "none yet").Append('\n');
            sb.Append("setup_used\t").Append(_setup.Resolve()).Append('\n');
            sb.Append("atlas_passives\t").Append(string.Join(", ", _setup.AtlasPassiveNames)).Append('\n');
            foreach (var (stat, value) in _setup.EssenceMapStats())
                sb.Append(stat).Append('\t').Append(value).Append('\n');
            return sb.ToString();
        }

        private string BuildCorruptionCsv()
        {
            var sb = new StringBuilder(CorruptionLog.CsvHeader).Append('\n');
            foreach (var record in _corruptionLog.Recent)
                sb.Append(CorruptionLog.ToCsvLine(record)).Append('\n');
            return sb.ToString();
        }

        private string BuildPerformanceText()
        {
            var perf = _tracker.Performance;
            var last = perf.Last;
            return string.Join("\n",
                $"Tracker, last second: {last.Ticks} ticks, {last.AvgTickMicros:0} us avg, {last.MaxTickMicros:0} us max per tick",
                $"Entities visited: {last.EntityVisits}/s ({last.MonolithChecks} monolith, {last.MonsterVisits} monster, {last.SeedVisits} reseed). " +
                $"Old design: {perf.EntityListSize} x 20 = {perf.EntityListSize * 20}/s",
                $"Label list scans: {last.LabelScans}/s ({last.LabelsVisited} labels read), label analyses: {last.Analyses}/s",
                $"Monoliths loaded: {last.CandidateMonoliths}, essences tracked: {last.Tracked}. " +
                $"Price sample: {_prices.LastSampleDurationTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00} ms") + "\n";
        }

        private static string Num(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

        #endregion
    }
}
