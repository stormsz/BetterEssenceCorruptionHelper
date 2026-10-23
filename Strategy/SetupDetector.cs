using ExileCore;
using ExileCore.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Strategy
{
    /// <summary>
    /// Reads what actually shapes corruption from the game instead of asking the user:
    /// <list type="bullet">
    /// <item>In a map: the area's own stats, which include atlas passives and scarabs in effect there
    /// (the same <c>IngameState.Data.MapStats</c> ExpeditionIcons and HarvestPicker read).
    /// <c>MapStableEssenceCorruption</c> is the Essence Scarab of Stability ("can only result in
    /// upgrading or transforming"); <c>MapEssenceXModsInheritedByMapBoss</c> is Crystal Resonance's
    /// "A Map Boss is granted a random Essence Modifier" line.</item>
    /// <item>Anywhere (e.g. the hideout, before opening a map): the allocated atlas passives
    /// (<c>ServerData.AtlasPassiveSkillIds</c>), named through the game's PassiveSkills file, to see
    /// whether Crystal Resonance is allocated.</item>
    /// </list>
    /// Both are re-read every few seconds and on area change; nothing here is expensive.
    /// </summary>
    internal sealed class SetupDetector(GameController gameController)
    {
        public const string CrystalResonanceName = "Crystal Resonance";
        private const long RefreshMs = 3000;

        private readonly GameController _gameController = gameController;
        private long _nextRefreshMs;

        /// <summary>True/false once the atlas tree could be read; null while unknown.</summary>
        public bool? CrystalResonanceAllocated { get; private set; }

        /// <summary>Setup in effect in the current map, or null outside maps (town, hideout, not loaded).</summary>
        public CorruptionSetup? CurrentMap { get; private set; }

        /// <summary>The last map's setup, kept through hideout trips so previews there use what you run.</summary>
        public CorruptionSetup? LastMap { get; private set; }

        /// <summary>Bumped when anything detected changes, so cached value ranges refresh.</summary>
        public int Version { get; private set; }

        /// <summary>Allocated atlas passive names as last read (for the diagnostics copy).</summary>
        public IReadOnlyList<string> AtlasPassiveNames { get; private set; } = [];

        public void Invalidate() => _nextRefreshMs = 0;

        public void Update(long nowMs)
        {
            if (nowMs < _nextRefreshMs)
                return;

            _nextRefreshMs = nowMs + RefreshMs;
            var resonance = ReadCrystalResonance(out var names);
            var map = ReadCurrentMap();

            if (resonance != CrystalResonanceAllocated || map != CurrentMap)
                Version++;

            CrystalResonanceAllocated = resonance;
            CurrentMap = map;
            LastMap = map ?? LastMap;
            AtlasPassiveNames = names;
        }

        /// <summary>
        /// The setup to analyse corruption with: the current map's; outside maps the last map's scarab
        /// and the atlas tree's Crystal Resonance; nothing known = neither.
        /// </summary>
        public CorruptionSetup Resolve() =>
            CurrentMap ?? new CorruptionSetup(
                StabilityScarab: LastMap?.StabilityScarab ?? false,
                CrystalResonance: CrystalResonanceAllocated ?? LastMap?.CrystalResonance ?? false);

        /// <summary>Essence-related stats of the current map (diagnostics copy).</summary>
        public List<(string Stat, int Value)> EssenceMapStats()
        {
            var result = new List<(string, int)>();
            try
            {
                var stats = _gameController.IngameState.Data.MapStats;
                if (stats == null)
                    return result;

                foreach (var (stat, value) in stats)
                {
                    var name = stat.ToString();
                    if (name.Contains("Essence", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Imprisoned", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Monolith", StringComparison.OrdinalIgnoreCase))
                        result.Add((name, value));
                }
            }
            catch
            {
                // not in a map / not readable
            }

            return result.OrderBy(s => s.Item1, StringComparer.Ordinal).ToList();
        }

        private CorruptionSetup? ReadCurrentMap()
        {
            try
            {
                if (!_gameController.InGame || _gameController.Area.CurrentArea.IsPeaceful)
                    return null;

                var stats = _gameController.IngameState.Data.MapStats;
                if (stats == null)
                    return null;

                return new CorruptionSetup(
                    StabilityScarab: stats.GetValueOrDefault(GameStat.MapStableEssenceCorruption) > 0,
                    CrystalResonance: stats.GetValueOrDefault(GameStat.MapEssenceXModsInheritedByMapBoss) > 0);
            }
            catch
            {
                return null;
            }
        }

        private bool? ReadCrystalResonance(out IReadOnlyList<string> names)
        {
            names = [];
            try
            {
                if (!_gameController.InGame)
                    return CrystalResonanceAllocated;

                var ids = _gameController.IngameState.ServerData.AtlasPassiveSkillIds;
                if (ids == null)
                    return null;

                var passives = _gameController.Files.PassiveSkills;
                var resolved = new List<string>();
                foreach (var id in ids)
                {
                    var name = passives.GetPassiveSkillByPassiveId(id)?.Name;
                    if (!string.IsNullOrEmpty(name))
                        resolved.Add(name);
                }

                names = resolved;

                // No names at all means the tree couldn't be read, not "nothing allocated".
                if (resolved.Count == 0)
                    return ids.Count == 0 ? false : null;

                return resolved.Contains(CrystalResonanceName);
            }
            catch
            {
                return null;
            }
        }
    }
}
