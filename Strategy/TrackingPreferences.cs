using BetterEssenceCorruptionHelper.Catalogue;
using System.Collections.Generic;

namespace BetterEssenceCorruptionHelper.Strategy
{
    /// <summary>A user's choice for one essence under the Profit strategy.</summary>
    internal enum EssenceChoice
    {
        /// <summary>Let the expected value decide.</summary>
        Auto,

        /// <summary>Always corrupt monsters holding it.</summary>
        Always,

        /// <summary>Never corrupt monsters holding it.</summary>
        Never,
    }

    /// <summary>
    /// The user's strategy and choices, persisted with the plugin settings (ExileCore serializes the
    /// public properties to JSON).
    ///
    /// <para>
    /// Only explicit choices are stored: an essence without an entry in <see cref="EssenceOverrides"/>
    /// is automatic, and null rule values mean the strategy's default. So a fresh install, and settings
    /// from versions without a "Tracking" block, start on the defaults; nothing is reset on startup,
    /// and choices survive switching strategies (only the Profit strategy uses them - the others are
    /// fixed rules). The map setup is not a preference: <see cref="SetupDetector"/> reads it from the
    /// game. Fields from earlier builds (setup overrides, assumed odds) are ignored when loading.
    /// </para>
    /// </summary>
    public sealed class TrackingPreferences
    {
        public const int CurrentSchemaVersion = 1;

        /// <summary>Default minimum expected profit for the Profit strategy, in chaos.</summary>
        public const float DefaultMinExpectedProfit = 1f;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string StrategyId { get; set; } = Strategies.Default.Id;

        /// <summary>Per-essence choices by name: true = always corrupt, false = never; absent = automatic.</summary>
        public Dictionary<string, bool> EssenceOverrides { get; set; } = new();

        /// <summary>MEDS strategy: also corrupt monsters with at least this many essences. Null = default, 0 = off.</summary>
        public int? MinPackSize { get; set; }

        /// <summary>Profit strategy: corrupt when the expected gain after the Vaal Orb is at least this (chaos). Null = default.</summary>
        public float? MinExpectedProfit { get; set; }

        /// <summary>Bumped on every change, so derived state (recommendations, value ranges) can refresh. Not persisted.</summary>
        internal int Version { get; private set; }

        internal StrategyDefinition Strategy => Strategies.Get(StrategyId);

        internal int EffectiveMinPackSize => MinPackSize ?? Strategy.DefaultMinPackSize;

        internal double EffectiveMinExpectedProfit => MinExpectedProfit ?? DefaultMinExpectedProfit;

        /// <summary>Group F only exists on monsters that are already corrupted, so it can't be chosen.</summary>
        internal static bool CanChoose(EssenceEntry entry) => !entry.IsCorruptionOnly;

        internal EssenceChoice Choice(EssenceEntry entry) =>
            EssenceOverrides.TryGetValue(entry.Name, out var always) ? always ? EssenceChoice.Always : EssenceChoice.Never : EssenceChoice.Auto;

        internal void SetChoice(EssenceEntry entry, EssenceChoice choice)
        {
            if (!CanChoose(entry) || choice == Choice(entry))
                return;

            if (choice == EssenceChoice.Auto)
                EssenceOverrides.Remove(entry.Name);
            else
                EssenceOverrides[entry.Name] = choice == EssenceChoice.Always;
            Changed();
        }

        /// <summary>Click cycle: automatic, always, never, automatic.</summary>
        internal void CycleChoice(EssenceEntry entry) => SetChoice(entry, Choice(entry) switch
        {
            EssenceChoice.Auto => EssenceChoice.Always,
            EssenceChoice.Always => EssenceChoice.Never,
            _ => EssenceChoice.Auto,
        });

        /// <summary>Drops every always/never choice, so every essence is automatic again. Rule values stay.</summary>
        internal void ClearChoices()
        {
            if (EssenceOverrides.Count == 0)
                return;

            EssenceOverrides.Clear();
            Changed();
        }

        /// <summary>Switches strategy. Explicit per-essence choices are kept on purpose.</summary>
        internal void SelectStrategy(string id)
        {
            if (Strategies.Find(id) == null || id == StrategyId)
                return;

            StrategyId = id;
            Changed();
        }

        internal void Changed() => Version++;

        /// <summary>
        /// Repairs whatever came out of the settings file: unknown strategy ids (e.g. from a newer
        /// version), out-of-range values, missing objects, older schema versions. Returns a note for
        /// the log when something was changed.
        /// </summary>
        internal string? Normalize()
        {
            var notes = new List<string>();

            EssenceOverrides ??= new Dictionary<string, bool>();

            if (Strategies.Find(StrategyId) == null)
            {
                notes.Add($"unknown strategy '{StrategyId}', using '{Strategies.Default.Id}'");
                StrategyId = Strategies.Default.Id;
            }

            if (MinPackSize is < 0)
                MinPackSize = 0;

            if (MinExpectedProfit is { } profit && !float.IsFinite(profit))
                MinExpectedProfit = null;

            // Schema 1 is the first persisted layout; later versions migrate from here.
            if (SchemaVersion < CurrentSchemaVersion)
                SchemaVersion = CurrentSchemaVersion;

            Changed();
            return notes.Count > 0 ? string.Join("; ", notes) : null;
        }
    }
}
