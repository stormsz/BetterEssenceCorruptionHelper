using BetterEssenceCorruptionHelper.Catalogue;
using ExileCore;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterEssenceCorruptionHelper.Pricing
{
    /// <summary>Status of the Ninja Price plugin within ExileCore's plugin manager.</summary>
    internal enum NinjaPluginState
    {
        /// <summary>PluginManager is null or was not queried.</summary>
        Unknown,

        /// <summary>Ninja Price is not installed (not found in loaded plugins or failed plugins).</summary>
        NotInstalled,

        /// <summary>Ninja Price failed to compile on startup.</summary>
        FailedCompilation,

        /// <summary>Ninja Price is installed, but the user has unchecked/disabled it in ExileApi.</summary>
        Disabled,

        /// <summary>Ninja Price is installed and enabled in ExileApi.</summary>
        Enabled,
    }

    /// <summary>Combined status from both PluginManager and PluginBridge.</summary>
    internal readonly record struct NinjaPriceStatus(
        NinjaPluginState PluginState,
        bool IsBridgeRegistered,
        string? FailureDetails = null)
    {
        public bool IsInstalled => PluginState is NinjaPluginState.Enabled or NinjaPluginState.Disabled or NinjaPluginState.FailedCompilation;
        public bool IsEnabled => PluginState == NinjaPluginState.Enabled;
    }

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
    internal sealed class NinjaPriceBridge(
        GameController gameController,
        Func<ExileCore.Shared.PluginManager?>? pluginManagerProvider = null)
    {
        public const string MethodName = "NinjaPrice.GetBaseItemTypeValue";

        private readonly GameController _gameController = gameController;
        private readonly Func<ExileCore.Shared.PluginManager?>? _pluginManagerProvider = pluginManagerProvider;

        /// <summary>Game definitions by catalogue item, once found. Only real definitions are cached.</summary>
        private readonly Dictionary<MarketItem, BaseItemType> _gameTypes = new();
        private readonly Dictionary<MarketItem, ItemMapping> _mappings = new();

        /// <summary>Stand-in definitions for items the game files can't (yet) provide.</summary>
        private readonly Dictionary<MarketItem, BaseItemType> _standIns = new();

        public ItemMapping GetMapping(MarketItem item) =>
            _mappings.TryGetValue(item, out var mapping) ? mapping : new ItemMapping(ItemMappingStatus.Pending, null, null);

        /// <summary>
        /// Defensively inspects both ExileCore's PluginManager and PluginBridge to determine whether
        /// Ninja Price is installed, enabled, and answering queries. Never throws.
        /// </summary>
        public NinjaPriceStatus GetStatus()
        {
            var pluginState = NinjaPluginState.Unknown;
            string? failureDetails = null;

            // 1. Defensively inspect PluginManager
            try
            {
                var pm = _pluginManagerProvider?.Invoke();
                if (pm != null)
                {
                    // Check failed source plugins first (e.g. compilation error)
                    try
                    {
                        var failed = pm.FailedSourcePlugins;
                        if (failed != null && !failed.IsEmpty)
                        {
                            foreach (var (path, error) in failed)
                            {
                                if (IsNinjaPricePathOrName(path))
                                {
                                    pluginState = NinjaPluginState.FailedCompilation;
                                    failureDetails = error;
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore dictionary access issues
                    }

                    if (pluginState == NinjaPluginState.Unknown)
                    {
                        var plugins = pm.Plugins;
                        if (plugins != null)
                        {
                            PluginWrapper? ninjaWrapper = null;
                            try
                            {
                                // Defensive snapshot against concurrent collection modification
                                var snapshot = plugins.ToArray();
                                foreach (var wrapper in snapshot)
                                {
                                    if (wrapper != null && IsNinjaPriceWrapper(wrapper))
                                    {
                                        ninjaWrapper = wrapper;
                                        break;
                                    }
                                }
                            }
                            catch
                            {
                                // Fallback index-based traversal if ToArray or enumeration threw
                                try
                                {
                                    for (var i = 0; i < plugins.Count; i++)
                                    {
                                        var w = plugins[i];
                                        if (w != null && IsNinjaPriceWrapper(w))
                                        {
                                            ninjaWrapper = w;
                                            break;
                                        }
                                    }
                                }
                                catch
                                {
                                    // Ignored
                                }
                            }

                            if (ninjaWrapper != null)
                            {
                                pluginState = ninjaWrapper.IsEnable ? NinjaPluginState.Enabled : NinjaPluginState.Disabled;
                            }
                            else if (pm.AllPluginsLoaded)
                            {
                                pluginState = NinjaPluginState.NotInstalled;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[BECH] PluginManager inspection error: {ex.Message}");
            }

            // 2. Defensively inspect PluginBridge
            var bridgeRegistered = false;
            try
            {
                var bridge = _gameController?.PluginBridge;
                if (bridge != null)
                {
                    var method = bridge.GetMethod<Func<BaseItemType, double>>(MethodName);
                    bridgeRegistered = method != null;
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"[BECH] PluginBridge inspection error: {ex.Message}");
            }

            return new NinjaPriceStatus(pluginState, bridgeRegistered, failureDetails);
        }

        private static bool IsNinjaPricePathOrName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Contains("Get-Chaos-Value", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Ninja Price", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("NinjaPrice", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNinjaPriceWrapper(PluginWrapper wrapper)
        {
            if (wrapper == null) return false;

            if (wrapper.Name is { } name && (
                name.Equals("Ninja Price", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NinjaPrice", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Get-Chaos-Value", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            try
            {
                if (wrapper.Plugin != null)
                {
                    var typeFullName = wrapper.Plugin.GetType().FullName;
                    if (typeFullName != null && (
                        typeFullName.StartsWith("Ninja_Price", StringComparison.OrdinalIgnoreCase) ||
                        typeFullName.StartsWith("NinjaPrice", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignored
            }

            if (IsNinjaPricePathOrName(wrapper.PathOnDisk) || IsNinjaPricePathOrName(wrapper.SourceProjectPath))
                return true;

            return false;
        }

        /// <summary>
        /// Looks the bridge method up. Returns null while Ninja Price hasn't registered it. Called by
        /// <see cref="PriceBook"/> only when it has no working delegate.
        /// </summary>
        public PriceQuery? Resolve()
        {
            var status = GetStatus();
            if (status.PluginState is NinjaPluginState.Disabled or NinjaPluginState.FailedCompilation or NinjaPluginState.NotInstalled)
                return null;

            Func<BaseItemType, double>? method;
            try
            {
                method = _gameController?.PluginBridge?.GetMethod<Func<BaseItemType, double>>(MethodName);
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
