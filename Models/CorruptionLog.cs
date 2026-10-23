using BetterEssenceCorruptionHelper.Analysis;
using BetterEssenceCorruptionHelper.Strategy;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Models
{
    /// <summary>
    /// One observed corruption: what the monster held before and after, which outcome that was, and
    /// the listing prices at that moment. Values count unpriced essences as 0c
    /// (<see cref="UnpricedCount"/> says how many).
    /// </summary>
    internal sealed record CorruptionRecord(
        DateTime At,
        string Area,
        IReadOnlyList<string> Before,
        IReadOnlyList<string> After,
        CorruptionOutcome? Outcome,
        bool Ambiguous,
        string StrategyId,
        CorruptionSetup Setup,
        bool WasRecommended,
        double? ValueBefore,
        double? ValueAfter,
        double? VaalOrbCost,
        int UnpricedCount)
    {
        /// <summary>Value after − value before − Vaal Orb, at listing prices; null when any part is unknown.</summary>
        public double? RealizedGain =>
            ValueBefore is { } before && ValueAfter is { } after && VaalOrbCost is { } cost ? after - before - cost : null;
    }

    /// <summary>
    /// Session tally of observed corruption outcomes, optionally appended to a CSV file. Over time this
    /// is exactly the sample data needed to estimate the outcome odds no source publishes.
    /// Main thread only.
    /// </summary>
    internal sealed class CorruptionLog
    {
        public const int MaxRecent = 100;

        public const string CsvHeader =
            "time_utc,area,strategy,stability_scarab,crystal_resonance,recommended,outcome,ambiguous,before,after,value_before_c,value_after_c,vaal_orb_c,realized_gain_c,unpriced";

        private readonly List<CorruptionRecord> _recent = new();
        private readonly Dictionary<CorruptionOutcome, int> _counts = new();

        public int Total { get; private set; }
        public int Unclassified { get; private set; }
        public double RealizedGainSum { get; private set; }
        public int RealizedGainCount { get; private set; }

        /// <summary>Bumped on every record, for cached display text.</summary>
        public int Version { get; private set; }

        public IReadOnlyList<CorruptionRecord> Recent => _recent;

        public int CountOf(CorruptionOutcome outcome) => _counts.GetValueOrDefault(outcome);

        /// <summary>Adds a record; appends it to <paramref name="csvPath"/> when given (errors are returned, not thrown).</summary>
        public string? Record(CorruptionRecord record, string? csvPath)
        {
            Total++;
            if (record.Outcome is { } outcome)
                _counts[outcome] = CountOf(outcome) + 1;
            else
                Unclassified++;

            if (record.RealizedGain is { } gain)
            {
                RealizedGainSum += gain;
                RealizedGainCount++;
            }

            _recent.Add(record);
            if (_recent.Count > MaxRecent)
                _recent.RemoveAt(0);

            Version++;

            if (csvPath == null)
                return null;

            try
            {
                var writeHeader = !File.Exists(csvPath);
                using var writer = File.AppendText(csvPath);
                if (writeHeader)
                    writer.WriteLine(CsvHeader);
                writer.WriteLine(ToCsvLine(record));
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static string ToCsvLine(CorruptionRecord r)
        {
            static string Num(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
            static string Text(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

            return string.Join(",",
                r.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Text(r.Area),
                r.StrategyId,
                r.Setup.StabilityScarab ? "1" : "0",
                r.Setup.CrystalResonance ? "1" : "0",
                r.WasRecommended ? "1" : "0",
                r.Outcome?.ToString() ?? "Unclassified",
                r.Ambiguous ? "1" : "0",
                Text(string.Join(";", r.Before)),
                Text(string.Join(";", r.After)),
                Num(r.ValueBefore),
                Num(r.ValueAfter),
                Num(r.VaalOrbCost),
                Num(r.RealizedGain),
                r.UnpricedCount.ToString(CultureInfo.InvariantCulture));
        }
    }
}
