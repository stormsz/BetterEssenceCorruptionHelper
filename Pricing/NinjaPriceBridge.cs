using BetterEssenceCorruptionHelper.Catalogue;
using ExileCore;
using ExileCore.PoEMemory.Models;
using System;
using System.Collections.Generic;

namespace BetterEssenceCorruptionHelper.Pricing
{
    /// <summary>How a catalogue item was matched to the game's own item definition.</summary>
    internal enum ItemMappingStatus
    {
        /// <summary>Game files not readable yet; priced through a stand-in definition for now.</summary>
        Pending,

        /// <summary>BaseItemTypes has the catalogue metadata and its BaseName equals the catalogue name.</summary>
        Verified,

        /// <summary>BaseItemTypes has the metadata but a different BaseName (catalogue out of date?).</summary>
        NameMismatch,

        /// <summary>The metadata is not in BaseItemTypes; priced through a stand-in definition.</summary>
        NotInGameFiles,
    }

    internal readonly record struct ItemMapping(ItemMappingStatus Status, string? GameBaseName, string? GameClassName);

    /// <summary>
    /// Talks to the installed Ninja Price plugin (instantsc/Get-Chaos-Value, verified against the
    /// installed source at Plugins/Source/Get-Chaos-Value, commit eacc26c).
    ///
    /// <para>
    /// Bridge contract (Main.cs): Ninja Price registers
    /// <c>"NinjaPrice.GetBaseItemTypeValue"</c> as <c>Func&lt;BaseItemType, double&gt;</c>. It builds a
    /// <c>CustomItem(BaseItemType)</c> - whose <c>CurrencyInfo.StackSize</c> defaults to 1 - and returns
    /// <c>PriceData.MinChaosValue</c>. For essences (any BaseName containing "Essence") that is
    /// <c>1 * Essences.LinesByName[BaseName].ChaosEquivalent</c>; for the Vaal Orb it is
    /// <c>1 * Currency.LinesByName["Vaal Orb"].ChaosEquivalent</c>. So the value is per single item and
    /// must not be divided by any stack size. A name it cannot find - or data not loaded yet, or the
    /// item type's valuation disabled in its settings - leaves the value at its default 0, and any
    /// exception is swallowed into 0 as well.
    /// </para>
    ///
    /// <para>
    /// The bridge has no "found" flag, no last-update time and no poe.ninja detailsId, so
    /// <see cref="PriceBook"/> infers loading/unlisted from the pattern of zeroes and tracks the age
    /// of its own samples. Calls run synchronously on the calling (main) thread against Ninja Price's
    /// in-memory data - no I/O, and Ninja Price downloads on its own background task.
    /// </para>
    /// </summary>
    internal sealed class NinjaPriceBridge(GameController gameController)
    {
        public const string MethodName = "NinjaPrice.GetBaseItemTypeValue";

        private readonly GameController _gameController = gameController;

        /// <summary>Game definitions by catalogue item, once found. Only real definitions are cached.</summary>
        private readonly Dictionary<MarketItem, BaseItemType> _gameTypes = new();
        private readonly Dictionary<MarketItem, ItemMapping> _mappings = new();

        /// <summary>Stand-in definitions for items the game files can't (yet) provide.</summary>
        private readonly Dictionary<MarketItem, BaseItemType> _standIns = new();

        public int ResolveCount { get; private set; }

        public ItemMapping GetMapping(MarketItem item) =>
            _mappings.TryGetValue(item, out var mapping) ? mapping : new ItemMapping(ItemMappingStatus.Pending, null, null);

        /// <summary>
        /// Looks the bridge method up. Returns null while Ninja Price hasn't registered it. Called by
        /// <see cref="PriceBook"/> only when it has no working delegate.
        /// </summary>
        public PriceQuery? Resolve()
        {
            ResolveCount++;

            Func<BaseItemType, double>? method;
            try
            {
                method = _gameController.PluginBridge.GetMethod<Func<BaseItemType, double>>(MethodName);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[BECH] Ninja Price bridge lookup failed: {ex.Message}");
                return null;
            }

            return method == null ? null : item => method(GetBaseItemType(item));
        }

        /// <summary>
        /// The game's own definition for a catalogue item - exactly what Ninja Price would see for the
        /// real item - or a stand-in carrying the catalogue name while game files can't provide it.
        /// </summary>
        private BaseItemType GetBaseItemType(MarketItem item)
        {
            if (_gameTypes.TryGetValue(item, out var known))
                return known;

            BaseItemType? fromGame = null;
            try
            {
                if (_gameController.InGame)
                    fromGame = _gameController.Files.BaseItemTypes.Translate(item.Metadata);
            }
            catch
            {
                // Files not ready - stand-in below, try again next sample.
            }

            if (fromGame != null && !string.IsNullOrEmpty(fromGame.BaseName))
            {
                var status = fromGame.BaseName == item.Name ? ItemMappingStatus.Verified : ItemMappingStatus.NameMismatch;
                _mappings[item] = new ItemMapping(status, fromGame.BaseName, fromGame.ClassName);
                _gameTypes[item] = fromGame;

                if (status == ItemMappingStatus.NameMismatch)
                    DebugWindow.LogError($"[BECH] '{item.Metadata}' is '{fromGame.BaseName}' in game files, catalogue says '{item.Name}'");

                return fromGame;
            }

            // Only call it missing once the files demonstrably work (the Vaal Orb always exists).
            var filesReady = item != EssenceCatalogue.VaalOrb && _gameTypes.ContainsKey(EssenceCatalogue.VaalOrb);
            _mappings[item] = new ItemMapping(filesReady ? ItemMappingStatus.NotInGameFiles : ItemMappingStatus.Pending, null, null);

            if (!_standIns.TryGetValue(item, out var standIn))
            {
                // Same shape PathfindSanctum hands Ninja Price: it only reads BaseName, ClassName and
                // Metadata (as CustomItem.Path) to decide the item type and the lookup key.
                standIn = new BaseItemType { BaseName = item.Name, ClassName = "StackableCurrency", Metadata = item.Metadata };
                _standIns[item] = standIn;
            }

            return standIn;
        }

        /// <summary>
        /// Essence items in the game files that the catalogue doesn't know about (by metadata). Walks all
        /// BaseItemTypes once (~10k entries), so it is only called from the diagnostics view.
        /// </summary>
        public List<(string Metadata, string BaseName)> FindUncataloguedEssences()
        {
            var result = new List<(string, string)>();
            if (!_gameController.InGame)
                return result;

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in EssenceCatalogue.All)
                known.Add(entry.Metadata);

            foreach (var type in _gameController.Files.BaseItemTypes.Contents.Values)
            {
                var metadata = type?.Metadata;
                if (metadata == null || !metadata.StartsWith(EssenceCatalogue.EssenceMetadataPrefix, StringComparison.Ordinal))
                    continue;

                if (!known.Contains(metadata))
                    result.Add((metadata, type!.BaseName ?? ""));
            }

            return result;
        }
    }
}
