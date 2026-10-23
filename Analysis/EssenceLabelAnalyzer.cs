using BetterEssenceCorruptionHelper.Catalogue;
using BetterEssenceCorruptionHelper.Models;
using ExileCore.PoEMemory.Elements;
using System;
using System.Collections.Generic;

namespace BetterEssenceCorruptionHelper.Analysis
{
    /// <summary>
    /// Reads an essence monolith's ground label into an <see cref="EssenceAnalysis"/>.
    ///
    /// The label's second child holds one text line per essence (e.g. "Deafening Essence of Misery"),
    /// plus a "Corrupted" line once the monster has been corrupted, plus - since 3.18.1 - the monster's
    /// modifiers. Essence lines are matched to the catalogue by exact name, with a tolerant fallback
    /// for decorated text; anything else is ignored except the corruption marker.
    /// </summary>
    internal static class EssenceLabelAnalyzer
    {
        private const string EssenceOf = "Essence of ";

        /// <summary>Last logged read failure (main thread only).</summary>
        private static string? _lastError;

        /// <summary>
        /// Analyzes an essence monolith label. Returns <see cref="EssenceAnalysis.Invalid"/> on an
        /// unexpected structure or a failed read rather than throwing.
        /// </summary>
        public static EssenceAnalysis Analyze(LabelOnGround label)
        {
            try
            {
                var children = label?.Label?.Children;
                if (children == null || children.Count < 2)
                    return EssenceAnalysis.Invalid;

                var container = children[1];
                if (container?.Children == null || container.ChildCount == 0)
                    return EssenceAnalysis.Invalid;

                var lines = new List<string>();
                foreach (var child in container.Children)
                {
                    var text = child?.Text;
                    if (!string.IsNullOrEmpty(text))
                        lines.Add(text);
                }

                // Also check if any other direct child of label.Label has corrupted text (e.g. status badge)
                for (var i = 0; i < children.Count; i++)
                {
                    if (i == 1) continue;
                    var t = children[i]?.Text;
                    if (!string.IsNullOrEmpty(t) && t.Contains("corrupted", StringComparison.OrdinalIgnoreCase))
                        lines.Add("Corrupted");
                }

                return AnalyzeLines(lines);
            }
            catch (Exception ex)
            {
                // Read every 200ms per label: log a failure once, not on every read.
                if (ex.Message != _lastError)
                {
                    _lastError = ex.Message;
                    ExileCore.DebugWindow.LogError($"Essence analysis failed: {ex.Message}");
                }

                return EssenceAnalysis.Invalid;
            }
        }

        /// <summary>
        /// A cheap value that changes whenever the label gains or loses a line (e.g. the "Corrupted" line
        /// a Vaal Orb adds) or child elements (e.g. the Remnant button disappearing on corruption).
        /// One element read instead of reading every line's text. -1 when unreadable.
        /// </summary>
        public static long ReadContentSignature(LabelOnGround label)
        {
            try
            {
                var children = label?.Label?.Children;
                if (children == null || children.Count < 2)
                    return -1;

                return ((long)children.Count << 32) | (uint)(children[1]?.ChildCount ?? 0);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Builds the analysis from the label's text lines.</summary>
        public static EssenceAnalysis AnalyzeLines(IEnumerable<string> lines)
        {
            var corrupted = false;
            var essences = new List<EssenceEntry>();
            List<string>? unrecognized = null;
            List<string>? unresolvedTypes = null;

            foreach (var line in lines)
            {
                if (TryParseEssenceLine(line, out var entry, out var typeName))
                {
                    if (entry != null)
                    {
                        essences.Add(entry);
                    }
                    else
                    {
                        (unrecognized ??= []).Add(line.Trim());
                        if (typeName != null && EssenceCatalogue.IsKnownType(typeName))
                            (unresolvedTypes ??= []).Add(typeName);
                    }
                    continue;
                }

                // Essence names never contain "Corrupted", so this can only be the corruption marker
                // (matched case-insensitively for "CORRUPTED" / "Corrupted").
                if (line.Contains("corrupted", StringComparison.OrdinalIgnoreCase))
                    corrupted = true;
            }

            return new EssenceAnalysis(true, corrupted, essences, unrecognized ?? (IReadOnlyList<string>)[], unresolvedTypes);
        }

        /// <summary>
        /// True when the line names an essence. <paramref name="entry"/> is its catalogue entry, or null
        /// when the line looks like an essence ("... Essence of X") that the catalogue doesn't know.
        /// </summary>
        public static bool TryParseEssenceLine(string line, out EssenceEntry? entry) => TryParseEssenceLine(line, out entry, out _);

        /// <summary>As above, also returning the type word after "Essence of" (null for an exact match).</summary>
        public static bool TryParseEssenceLine(string line, out EssenceEntry? entry, out string? typeName)
        {
            entry = null;
            typeName = null;
            var text = line.Trim();

            // Exact name - the normal case, one dictionary lookup.
            entry = EssenceCatalogue.FindByName(text);
            if (entry != null)
                return true;

            var at = text.IndexOf(EssenceOf, StringComparison.Ordinal);
            if (at < 0)
                return false;

            // Decorated text (markup, counts): pick out "<Tier> Essence of <Type>" by its words.
            var typeStart = at + EssenceOf.Length;
            var typeEnd = typeStart;
            while (typeEnd < text.Length && char.IsLetter(text[typeEnd]))
                typeEnd++;
            var type = text[typeStart..typeEnd];
            typeName = type;

            var tierEnd = at;
            while (tierEnd > 0 && text[tierEnd - 1] == ' ')
                tierEnd--;
            var tierStart = tierEnd;
            while (tierStart > 0 && char.IsLetter(text[tierStart - 1]))
                tierStart--;
            var tierWord = text[tierStart..tierEnd];

            var tier = Enum.TryParse<EssenceTier>(tierWord, ignoreCase: false, out var parsed) &&
                       parsed is >= EssenceTier.Whispering and <= EssenceTier.Deafening
                ? parsed
                : EssenceTier.Corrupted;

            entry = EssenceCatalogue.Find(type, tier);
            return true;
        }
    }
}
