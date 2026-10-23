using BetterEssenceCorruptionHelper.Catalogue;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Strategy
{
    /// <summary>
    /// The map setup that changes what a Vaal Orb does to an essence monster (poewiki, 3.29):
    /// <list type="bullet">
    /// <item>Essence Scarab of Stability: "Corrupting an Essence in Area can only result in upgrading or
    /// transforming Essences" - the no-change and add-an-essence outcomes are removed.</item>
    /// <item>Crystal Resonance (Atlas passive): "Using a Vaal Orb on Imprisoned Monsters in your Maps
    /// replaces all Essences with one of the Essences on the Imprisoned Monster" - so it can never
    /// produce a Group F essence (Maxroll: it "prevents you from upgrading essences into corrupted
    /// essences").</item>
    /// </list>
    /// </summary>
    internal readonly record struct CorruptionSetup(bool StabilityScarab, bool CrystalResonance);

    /// <summary>How a strategy decides.</summary>
    internal enum StrategyMode
    {
        /// <summary>Corrupt when the expected gain beats the Vaal Orb; the user can force essences in or out.</summary>
        Profit,

        /// <summary>Corrupt monsters holding one of a fixed set of essences (<see cref="StrategyDefinition.CorruptsByRule"/>).</summary>
        FixedSet,

        /// <summary>Never corrupt.</summary>
        NoCorruption,
    }

    /// <summary>
    /// A farming workflow. Data only - <see cref="StrategyEvaluator"/> applies it. Only
    /// <see cref="StrategyMode.Profit"/> takes the user's per-essence choices into account; the other
    /// strategies are exactly what they say.
    /// </summary>
    internal sealed record StrategyDefinition(
        string Id,
        string DisplayName,
        string Summary,
        StrategyMode Mode,
        Func<EssenceEntry, bool> CorruptsByRule,
        int DefaultMinPackSize,
        CorruptionSetup ExpectedSetup,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// The supported strategies. To change a rule after a balance patch, edit the definition here;
    /// to add a workflow, add a definition to <see cref="All"/>.
    /// </summary>
    internal static class Strategies
    {
        /// <summary>
        /// Value-driven: per monster, the expected essence value after a Vaal Orb (each outcome the map
        /// setup allows counted as equally likely - see CorruptionModel) against the orb's price.
        /// </summary>
        public static readonly StrategyDefinition Profit = new(
            Id: "profit",
            DisplayName: "Profit (Expected Value)",
            Summary: "Corrupts when the average outcome beats the Vaal Orb cost at live prices.",
            Mode: StrategyMode.Profit,
            CorruptsByRule: _ => false,
            DefaultMinPackSize: 0,
            ExpectedSetup: new CorruptionSetup(StabilityScarab: false, CrystalResonance: false),
            Notes: []);

        /// <summary>
        /// Maxroll "Corrupted Essences" (https://maxroll.gg/poe/currency/corrupted-essences, 3.29):
        /// Essence Scarab of Stability, no Crystal Resonance; corrupt purple MEDS (Misery, Envy, Dread,
        /// Scorn) for a chance at Group F (Horror, Delirium, Hysteria, Insanity).
        /// </summary>
        public static readonly StrategyDefinition Meds = new(
            Id: "meds",
            DisplayName: "MEDS Corruption",
            Summary: "Corrupts purple MEDS for a 50/50 chance at Horror, Delirium, Hysteria, or Insanity.",
            Mode: StrategyMode.FixedSet,
            CorruptsByRule: e => e.IsMeds,
            DefaultMinPackSize: 6,
            ExpectedSetup: new CorruptionSetup(StabilityScarab: true, CrystalResonance: false),
            Notes:
            [
                "Run with Essence Scarab of Stability. Do not allocate Crystal Resonance.",
            ]);

        /// <summary>
        /// Maxroll "Deafening Essences" (https://maxroll.gg/poe/currency/deafening-essences, 3.29):
        /// Crystal Resonance allocated, which prevents corrupting into corrupted essences.
        /// </summary>
        public static readonly StrategyDefinition Deafening = new(
            Id: "deafening",
            DisplayName: "Deafening Boss Farming",
            Summary: "Kill essence mobs, then slay map boss to duplicate Deafening essences. No Vaal Orbs.",
            Mode: StrategyMode.NoCorruption,
            CorruptsByRule: _ => false,
            DefaultMinPackSize: 0,
            ExpectedSetup: new CorruptionSetup(StabilityScarab: false, CrystalResonance: true),
            Notes:
            [
                "Requires Crystal Resonance on Atlas tree. Vaal Orbs are disabled.",
            ]);

        public static IReadOnlyList<StrategyDefinition> All { get; } = [Profit, Meds, Deafening];

        /// <summary>Fresh installs start on the value-driven strategy.</summary>
        public static StrategyDefinition Default => Profit;

        public static StrategyDefinition? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

        public static StrategyDefinition Get(string? id) => Find(id) ?? Default;
    }
}
