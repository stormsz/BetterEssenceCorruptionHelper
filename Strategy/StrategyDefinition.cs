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
        /// setup allows counted as equally likely - no odds are published, see CorruptionModel) against
        /// the orb's price. MEDS come out on top whenever corrupted essences are worth it; anything else
        /// qualifies on its own merits, and it all follows the prices as Ninja Price refreshes.
        /// </summary>
        public static readonly StrategyDefinition Profit = new(
            Id: "profit",
            DisplayName: "Profit (expected value)",
            Summary: "Corrupt a monster when a Vaal Orb is expected to add more value than it costs, at current prices.",
            Mode: StrategyMode.Profit,
            CorruptsByRule: _ => false,
            DefaultMinPackSize: 0,
            ExpectedSetup: new CorruptionSetup(StabilityScarab: false, CrystalResonance: false),
            Notes:
            [
                "Click an essence to always corrupt it, again to never corrupt it, again for automatic.",
                "The scarab and Crystal Resonance are read from the game.",
                "Nobody publishes the odds, so every possible outcome counts as equally likely.",
            ]);

        /// <summary>
        /// Maxroll "Corrupted Essences" (https://maxroll.gg/poe/currency/corrupted-essences, 3.29,
        /// updated 2026-08-04): Essence Scarab of Stability, no Crystal Resonance; "If you find any of the
        /// purple Essences (MEDS - Misery, Envy, Dread, Scorn), use a Vaal Orb to Corrupt them". Every
        /// MEDS tier counts because the Group F result does not depend on tier (poewiki). The 6+ pack
        /// rule is this plugin's long-standing heuristic, kept as a default.
        /// </summary>
        public static readonly StrategyDefinition Meds = new(
            Id: "meds",
            DisplayName: "MEDS corruption",
            Summary: "Corrupt every monster holding a MEDS essence (Misery, Envy, Dread, Scorn). Kill the rest.",
            Mode: StrategyMode.FixedSet,
            CorruptsByRule: e => e.IsMeds,
            DefaultMinPackSize: 6,
            ExpectedSetup: new CorruptionSetup(StabilityScarab: true, CrystalResonance: false),
            Notes:
            [
                "Use the Essence Scarab of Stability.",
                "Crystal Resonance must not be allocated.",
            ]);

        /// <summary>
        /// Maxroll "Deafening Essences" (https://maxroll.gg/poe/currency/deafening-essences, 3.29,
        /// updated 2026-08-04): Crystal Resonance allocated, which "prevents you from upgrading essences
        /// into corrupted essences so don't use your Vaal Orbs on them".
        /// </summary>
        public static readonly StrategyDefinition Deafening = new(
            Id: "deafening",
            DisplayName: "Deafening farming (no corruption)",
            Summary: "Crystal Resonance boss farming: kill every essence monster, boss last. No Vaal Orbs.",
            Mode: StrategyMode.NoCorruption,
            CorruptsByRule: _ => false,
            DefaultMinPackSize: 0,
            ExpectedSetup: new CorruptionSetup(StabilityScarab: false, CrystalResonance: true),
            Notes:
            [
                "Allocate Crystal Resonance.",
            ]);

        public static IReadOnlyList<StrategyDefinition> All { get; } = [Profit, Meds, Deafening];

        /// <summary>Fresh installs start on the value-driven strategy.</summary>
        public static StrategyDefinition Default => Profit;

        public static StrategyDefinition? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

        public static StrategyDefinition Get(string? id) => Find(id) ?? Default;
    }
}
