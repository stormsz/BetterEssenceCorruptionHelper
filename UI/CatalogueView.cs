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
        private static readonly Vector4 AlwaysCell = new(0.22f, 0.68f, 0.30f, 0.95f);
        private static readonly Vector4 NeverCell = new(0.62f, 0.18f, 0.16f, 0.75f);
        private static readonly Vector4 MissingCell = new(0f, 0f, 0f, 0.4f);
        private static readonly Vector4 BandRow = new(1f, 1f, 1f, 0.035f);
        private static readonly Vector4 ProblemBox = new(0.45f, 0.08f, 0.06f, 0.85f);
        private static readonly Vector4 WarningBox = new(0.40f, 0.30f, 0.05f, 0.75f);
        private static readonly Vector4 AutoVaalBorder = new(0.20f, 0.88f, 0.40f, 0.95f);
        private static readonly Vector4 ProfitGreen = new(0.25f, 0.95f, 0.35f, 1f);
        private static readonly Vector4 WarnAmber = new(0.96f, 0.78f, 0.25f, 1f);
        private static readonly Vector4 LossRed = new(0.95f, 0.36f, 0.36f, 1f);
        private static readonly Vector4 NeutralGray = new(0.68f, 0.68f, 0.68f, 1f);

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

            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("Strategy:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260);
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
            ImGui.TextDisabled(strategy.Summary);

            if (strategy.Notes.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    foreach (var note in strategy.Notes)
                        ImGui.Text(note);
                    ImGui.EndTooltip();
                }
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
            {
                DrawGrid("essenceMedsGrid", MedsGroups, MedsTiers);
            }
            else
            {
                DrawGrid("essenceCorruptedGrid", CorruptedGroups, CorruptedTiers);
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();
                DrawGrid("essenceAllGrid", OtherGroupsWithMeds, OtherTiers);
            }
            ImGui.Spacing();
            ImGui.Spacing();
            DrawLegend();
        }

        /// <summary>Why the Profit strategy can't price anything right now, or null when Ninja Price is ready.</summary>
        private SetupStatus.Status? NinjaPriceProblem() => _prices.Status switch
        {
            ProviderStatus.Ready => null,
            ProviderStatus.Disabled => new SetupStatus.Status(SetupStatus.Level.Problem,
                "Ninja Price is disabled in ExileApi",
                "Enable the 'Ninja Price' plugin in ExileApi's Plugins menu. Until then, only monsters holding a MEDS essence get a Vaal Orb."),
            ProviderStatus.NotInstalled => new SetupStatus.Status(SetupStatus.Level.Problem,
                "Ninja Price plugin is not installed",
                "Install the Ninja Price (Get-Chaos-Value) plugin. Until then, only monsters holding a MEDS essence get a Vaal Orb."),
            ProviderStatus.FailedCompilation => new SetupStatus.Status(SetupStatus.Level.Problem,
                "Ninja Price failed to compile",
                "Check ExileCore compilation logs for Ninja Price errors. Until then, only monsters holding a MEDS essence get a Vaal Orb."),
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
            var problem = _prices.Status switch
            {
                ProviderStatus.Disabled => "Ninja Price is disabled in ExileApi - enable it in the Plugins menu.",
                ProviderStatus.NotInstalled => "Ninja Price is not installed - install the Ninja Price plugin to see prices.",
                ProviderStatus.FailedCompilation => "Ninja Price failed to compile - check ExileCore errors.",
                ProviderStatus.Loading => "Ninja Price is still loading prices.",
                ProviderStatus.EssenceDataMissing => "Ninja Price has no essence prices - is Essence valuation off in its settings?",
                ProviderStatus.Unavailable => "Ninja Price isn't running - enable it to see prices.",
                _ => null,
            };
            if (problem != null)
            {
                ImGui.TextColored(WarnColor, problem);
                return;
            }

            var size = ImGui.GetTextLineHeight();
            if (_icons.For("Vaal") is { } vaalIcon)
            {
                ImGui.Image(vaalIcon, new Vector2(size, size));
                ImGui.SameLine(0, 5);
            }

            ImGui.Text($"Vaal Orb {Format.Chaos(vaal.Chaos)}");
            if (_prices.LastSampleMs is { } sampled)
            {
                ImGui.SameLine(0, 10);
                ImGui.TextDisabled($"·  Auto-refreshed {Format.Age(_clockMs() - sampled)} ago (every {PriceBook.RefreshIntervalMs / 1000}s · prices & planner recommendations)");
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

        /// <summary>A cell's background and border under the current strategy.</summary>
        private (Vector4? Background, Vector4? Border) CellState(EssenceEntry entry, StrategyMode mode)
        {
            if (entry.IsCorruptionOnly)
                return (null, null);

            var preferences = Preferences;
            switch (mode)
            {
                case StrategyMode.FixedSet:
                    return preferences.Strategy.CorruptsByRule(entry)
                        ? (null, AutoVaalBorder)
                        : (null, null);

                case StrategyMode.NoCorruption:
                    return (null, null);
            }

            var assessment = SingleProfit(entry);
            var profit = assessment?.ExpectedNetGain;
            return preferences.Choice(entry) switch
            {
                EssenceChoice.Always => (AlwaysCell, AutoVaalBorder),
                EssenceChoice.Never => (NeverCell, null),
                _ when profit >= preferences.EffectiveMinExpectedProfit => (null, AutoVaalBorder),
                _ => (null, null),
            };
        }


        private void DrawGridCell(EssenceEntry entry, StrategyMode mode, long now)
        {
            var value = _values.Get(entry);
            var (background, border) = CellState(entry, mode);
            if (background is { } bg)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.GetColorU32(bg));

            var textColor = value.Source switch
            {
                ValueSource.Market => ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
                _ => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
            };

            var text = Format.Value(value);
            var textSize = ImGui.CalcTextSize(text);
            var availWidth = ImGui.GetContentRegionAvail().X;

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0f, 0f, 0f, 0f));
            if (ImGui.Selectable($"{text}##cell{entry.Name}", false, ImGuiSelectableFlags.None, new Vector2(availWidth, 0)))
            {
                if (mode == StrategyMode.Profit)
                    Preferences.CycleChoice(entry);
            }
            ImGui.PopStyleColor();

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();

            // Center the cell value text horizontally and vertically
            var textX = min.X + Math.Max(0f, (max.X - min.X - textSize.X) * 0.5f);
            var textY = min.Y + Math.Max(0f, (max.Y - min.Y - textSize.Y) * 0.5f);
            ImGui.GetWindowDrawList().AddText(new Vector2(textX, textY), ImGui.GetColorU32(textColor), text);

            if (border is { } b)
                ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(b), 0f, ImDrawFlags.None, 1.5f);

            if (mode == StrategyMode.Profit && ImGui.IsItemClicked(ImGuiMouseButton.Right))
                Preferences.SetChoice(entry, EssenceChoice.Auto);

            if (ImGui.IsItemHovered())
                DrawCellTooltip(entry, value, SingleProfit(entry), mode);
        }

        private void DrawCellTooltip(EssenceEntry entry, EssenceValue value, CorruptionAssessment? assessment, StrategyMode mode)
        {
            ImGui.BeginTooltip();

            // 1. Essence Name & Current Price with Source
            ImGui.TextColored(GroupColor(entry.Group), entry.Name);
            ImGui.TextDisabled("Value: ");
            ImGui.SameLine(0, 0);
            if (value.Chaos is { } chaos)
            {
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), Format.Chaos(chaos));
                ImGui.SameLine(0, 6);
                if (value.Source == ValueSource.Market)
                    ImGui.TextDisabled("(Source: Ninja Price)");
                else if (value.Source == ValueSource.Capped)
                    ImGui.TextDisabled($"(Source: Ninja Price {Format.Chaos(value.MarketChaos!.Value)} capped at {value.Basis!.TierName} tier)");
                else
                    ImGui.TextDisabled($"(Source: Ninja Price unlisted -> {Format.Chaos(EssenceValues.CheapValue)} base)");
            }
            else
            {
                ImGui.TextDisabled("No price available from Ninja Price");
            }

            if (entry.IsCorruptionOnly)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextDisabled("Corrupted essence. Obtained only by Vaaling purple MEDS (Misery, Envy, Dread, Scorn).");
                ImGui.TextDisabled("Cannot be modified by a Vaal Orb.");
                ImGui.EndTooltip();
                return;
            }

            if (mode == StrategyMode.NoCorruption)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextDisabled("Deafening farming: never uses Vaal Orbs (kill monster and boss to duplicate).");
                ImGui.EndTooltip();
                return;
            }

            if (mode == StrategyMode.FixedSet)
            {
                ImGui.Spacing();
                ImGui.Separator();
                if (Preferences.Strategy.CorruptsByRule(entry))
                    ImGui.TextColored(ProfitGreen, "Strategy Target: Corrupt monster holding MEDS for chance at Horror, Delirium, Hysteria, or Insanity.");
                else
                    ImGui.TextDisabled("Strategy Target: Kill as-is (not a MEDS essence).");
                ImGui.EndTooltip();
                return;
            }

            // Profit Mode: Full Assessment Breakdown
            ImGui.Spacing();
            ImGui.Separator();

            if (assessment == null)
            {
                ImGui.TextDisabled("No price data to evaluate expected value.");
                ImGui.EndTooltip();
                return;
            }

            ImGui.TextDisabled("A Vaal Orb on a monster holding only this can:");
            foreach (var outcome in assessment.Outcomes)
            {
                switch (outcome.Outcome)
                {
                    case CorruptionOutcome.NoChange:
                        ImGui.TextDisabled("  Do nothing: ");
                        ImGui.SameLine(0, 0);
                        ImGui.TextColored(NeutralGray, "+0c");
                        break;

                    case CorruptionOutcome.TierUpgrade:
                        if (EssenceCatalogue.UpgradeOf(entry) is { } up && up != entry)
                        {
                            ImGui.TextDisabled($"  Upgrade to {up.Name}: ");
                            ImGui.SameLine(0, 0);
                            var upGain = outcome.MinGain;
                            ImGui.TextColored(upGain > 0 ? ProfitGreen : (upGain < 0 ? LossRed : NeutralGray), Format.SignedChaos(upGain));
                        }
                        else
                        {
                            ImGui.TextDisabled("  Upgrade tier: ");
                            ImGui.SameLine(0, 0);
                            ImGui.TextColored(NeutralGray, "already Deafening (+0c)");
                        }
                        break;

                    case CorruptionOutcome.AddEssence:
                        ImGui.TextDisabled("  Add essence: ");
                        ImGui.SameLine(0, 0);
                        var addGain = outcome.UniformMeanGain ?? 0;
                        ImGui.TextColored(addGain > 0 ? ProfitGreen : NeutralGray, $"{Format.SignedChaos(addGain)} typical value");
                        break;

                    case CorruptionOutcome.Transform:
                        var candidates = EssenceCatalogue.TransformCandidates(entry);
                        var targetDesc = entry.IsMeds
                            ? "Transform into Corrupted: "
                            : candidates.Count > 0
                                ? $"Transform into {candidates[0].TierName}: "
                                : "Transform: ";

                        ImGui.TextDisabled($"  {targetDesc}");
                        ImGui.SameLine(0, 0);
                        var tGain = outcome.UniformMeanGain ?? 0;
                        var tColor = tGain > 0 ? ProfitGreen : (tGain < 0 ? LossRed : NeutralGray);
                        ImGui.TextColored(tColor, $"{Format.SignedChaos(tGain)} avg ({Format.SignedChaos(outcome.MinGain)} to {Format.SignedChaos(outcome.MaxGain)})");

                        if (candidates.Count > 0)
                        {
                            var ownPrice = value.Chaos ?? EssenceValues.CheapValue;
                            var iconSize = ImGui.GetTextLineHeight();
                            ImGui.TextDisabled("    ");
                            for (var i = 0; i < candidates.Count; i++)
                            {
                                var cand = candidates[i];
                                var candPrice = _values.ValueOf(cand) ?? EssenceValues.CheapValue;
                                var netGain = candPrice - ownPrice;
                                var netColor = netGain > 0 ? ProfitGreen : (netGain < 0 ? LossRed : NeutralGray);

                                ImGui.SameLine(0, 0);
                                if (_icons.For(cand.TypeName) is { } icon)
                                {
                                    ImGui.Image(icon, new Vector2(iconSize, iconSize));
                                    ImGui.SameLine(0, 3);
                                }

                                ImGui.TextDisabled($"{cand.TypeName} ");
                                ImGui.SameLine(0, 0);
                                ImGui.TextColored(netColor, Format.SignedChaos(netGain));

                                if (i < candidates.Count - 1)
                                {
                                    ImGui.SameLine(0, 0);
                                    ImGui.TextDisabled("  ·  ");
                                }
                            }
                        }
                        break;
                }
            }

            ImGui.Spacing();
            ImGui.Separator();

            // Vaal Orb Cost
            ImGui.TextDisabled("Vaal Orb cost: ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(LossRed, $"-{Format.Chaos(assessment.VaalOrbCost)}");

            // Expected Profit & Decision
            var ev = assessment.ExpectedNetGain;
            var wantedEv = Preferences.EffectiveMinExpectedProfit;
            var evColor = ev >= wantedEv ? ProfitGreen : (ev > 0 ? WarnAmber : LossRed);

            ImGui.Text("Expected Profit: ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(evColor, Format.SignedChaos(ev));

            var choice = Preferences.Choice(entry);
            string decisionText;
            Vector4 decisionColor;

            switch (choice)
            {
                case EssenceChoice.Always:
                    decisionText = "ALWAYS VAAL (manual override)";
                    decisionColor = ProfitGreen;
                    break;
                case EssenceChoice.Never:
                    decisionText = "NEVER VAAL (manual override)";
                    decisionColor = LossRed;
                    break;
                default:
                    if (ev >= wantedEv)
                    {
                        decisionText = wantedEv > 0
                            ? $"VAAL (EV >= +{Format.Chaos(wantedEv)} target)"
                            : "VAAL (positive EV)";
                        decisionColor = ProfitGreen;
                    }
                    else
                    {
                        decisionText = ev > 0
                            ? $"KILL AS-IS (EV below +{Format.Chaos(wantedEv)} target)"
                            : "KILL AS-IS (expected loss)";
                        decisionColor = LossRed;
                    }
                    break;
            }

            ImGui.Text("Decision: ");
            ImGui.SameLine(0, 0);
            ImGui.TextColored(decisionColor, decisionText);

            ImGui.TextDisabled("Left-click: Always -> Never -> Auto  ·  Right-click: Reset");
            ImGui.EndTooltip();
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

        /// <summary>Clean, compact legend under the tables without bulky boxes.</summary>
        private void DrawLegend()
        {
            var mode = Preferences.Strategy.Mode;

            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            if (mode == StrategyMode.Profit)
            {
                SwatchBorder(AutoVaalBorder, $"Auto corrupt (>={Format.Chaos(Preferences.EffectiveMinExpectedProfit)})");
                ImGui.SameLine(0, 16);
                Swatch(AlwaysCell, "Always (you)");
                ImGui.SameLine(0, 16);
                Swatch(NeverCell, "Never (you)");
                ImGui.SameLine(0, 16);
                Sample(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], $"Unlisted ({Format.Chaos(EssenceValues.CheapValue)})");

                var picks = Preferences.EssenceOverrides.Count;
                if (picks > 0)
                {
                    ImGui.SameLine(0, 20);
                    if (ImGui.SmallButton($"Reset picks ({picks})"))
                        Preferences.ClearChoices();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Reset all manual always/never picks back to automatic.\nRight-click any cell or row name to reset just that one.");
                }
            }
            else if (mode == StrategyMode.FixedSet)
            {
                SwatchBorder(AutoVaalBorder, "Corrupt target (MEDS)");
                ImGui.SameLine(0, 16);
                ImGui.TextDisabled("Horror, Delirium, Hysteria, and Insanity cannot be Vaaled.");
            }
            else
            {
                ImGui.TextDisabled("Deafening boss farming: Kill all essences, then kill map boss. No Vaal Orbs.");
            }
        }

        private static void SwatchBorder(Vector4 borderColor, string label)
        {
            var p0 = ImGui.GetCursorScreenPos();
            var size = new Vector2(26f, ImGui.GetFrameHeight());
            var p1 = p0 + size;
            ImGui.GetWindowDrawList().AddRect(p0, p1, ImGui.GetColorU32(borderColor), 0f, ImDrawFlags.None, 1.5f);
            ImGui.Dummy(size);
            ImGui.SameLine(0, 6);
            ImGui.Text(label);
        }

        private static void Swatch(Vector4 color, string label)
        {
            var p0 = ImGui.GetCursorScreenPos();
            var size = new Vector2(26f, ImGui.GetFrameHeight());
            var p1 = p0 + size;
            ImGui.GetWindowDrawList().AddRectFilled(p0, p1, ImGui.GetColorU32(color with { W = Math.Max(color.W, 0.6f) }));
            ImGui.Dummy(size);
            ImGui.SameLine(0, 6);
            ImGui.Text(label);
        }

        private static void Sample(Vector4 textColor, string label)
        {
            ImGui.BeginGroup();
            var p0 = ImGui.GetCursorScreenPos();
            var size = new Vector2(26f, ImGui.GetFrameHeight());
            var p1 = p0 + size;
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(p0, p1, ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.14f, 0.9f)));
            drawList.AddRect(p0, p1, ImGui.GetColorU32(new Vector4(0.35f, 0.35f, 0.35f, 0.6f)));
            const string text = "-";
            var textSize = ImGui.CalcTextSize(text);
            var textX = p0.X + (size.X - textSize.X) * 0.5f;
            var textY = p0.Y + (size.Y - textSize.Y) * 0.5f - 1f;
            drawList.AddText(new Vector2(textX, textY), ImGui.GetColorU32(textColor), text);
            ImGui.Dummy(size);
            ImGui.SameLine(0, 6);
            ImGui.Text(label);
            ImGui.EndGroup();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Ninja Price has no price for it (shown as '-'), so it counts as {Format.Chaos(EssenceValues.CheapValue)} in calculations.");
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
            ImGui.SameLine();
            if (ImGui.Button("Reset session stats"))
            {
                _corruptionLog.ResetSession();
                _copyNote = "session stats reset";
            }

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
            if (!ImGui.BeginTable("bechMapping", 4, flags, new Vector2(0, 240)))
                return;

            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Game files");
            ImGui.TableSetupColumn("Ninja Price");
            ImGui.TableSetupColumn("Value used");
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
            sb.Append("ninja_price_status\t").Append(_prices.Status).Append('\n');
            var np = _bridge.GetStatus();
            sb.Append("ninja_price_plugin_state\t").Append(np.PluginState).Append('\n');
            sb.Append("ninja_price_bridge_registered\t").Append(np.IsBridgeRegistered).Append('\n');
            if (!string.IsNullOrEmpty(np.FailureDetails))
                sb.Append("ninja_price_failure_details\t").Append(np.FailureDetails).Append('\n');
            sb.Append("atlas_passives\t").Append(string.Join(", ", _setup.AtlasPassiveNames)).Append('\n');
            foreach (var (stat, value) in _setup.EssenceMapStats())
                sb.Append(stat).Append('\t').Append(value).Append('\n');
            sb.Append("session_monsters_killed\t").Append(_corruptionLog.TotalKilled).Append('\n');
            sb.Append("session_corruptions\t").Append(_corruptionLog.Total).Append('\n');
            sb.Append("session_recommended_corruptions\t").Append(_corruptionLog.RecommendedCorruptions).Append('\n');
            sb.Append("session_mistaken_corruptions\t").Append(_corruptionLog.MistakenCorruptions).Append('\n');
            sb.Append("session_missed_corruptions\t").Append(_corruptionLog.TotalMissed).Append('\n');
            sb.Append("session_realized_gain_chaos\t").Append(_corruptionLog.RealizedGainSum).Append('\n');
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
