using BetterEssenceCorruptionHelper.Strategy;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System;
using System.Collections.Generic;

namespace BetterEssenceCorruptionHelper
{
    /// <summary>
    /// Scans the player's inventory for Vaal Orbs so the user can be alerted when running
    /// a corruption strategy without sufficient Vaal Orbs (since in-game, without Vaal Orbs,
    /// the Vaal button on essence monoliths does not even appear).
    /// </summary>
    internal sealed class InventoryScanner(GameController? gameController)
    {
        private const long ScanIntervalMs = 400; // Throttle to 2.5 scans/sec to save CPU
        private readonly GameController? _gameController = gameController;
        private long _nextScanMs;

        /// <summary>Total count of Vaal Orbs detected across main inventory, expanded inventory, and cursor.</summary>
        public int VaalOrbCount { get; private set; }

        /// <summary>True once at least one scan has run while in-game.</summary>
        public bool HasScanned { get; private set; }

        /// <summary>Version counter bumped whenever the detected Vaal Orb count changes.</summary>
        public int Version { get; private set; }

        /// <summary>Forces an immediate scan on next Update (e.g. on area change or after corruption).</summary>
        public void Invalidate() => _nextScanMs = 0;

        /// <summary>
        /// Updates the scanner if due. Safe to call every frame from Tick on the main thread.
        /// </summary>
        public void Update(long nowMs)
        {
            if (nowMs < _nextScanMs)
                return;

            _nextScanMs = nowMs + ScanIntervalMs;

            if (_gameController == null || !_gameController.InGame || _gameController.IsLoading)
                return;

            var newCount = ScanPlayerInventories();
            if (!HasScanned || newCount != VaalOrbCount)
            {
                VaalOrbCount = newCount;
                HasScanned = true;
                Version++;
            }
        }

        /// <summary>
        /// Pure evaluation method: determines whether a Vaal Orb alert should be raised.
        /// </summary>
        public static bool ShouldAlert(bool alertEnabled, int vaalCount, int minThreshold, StrategyMode strategyMode)
        {
            if (!alertEnabled)
                return false;

            // Deafening Boss Farming explicitly uses no Vaal Orbs, so never alert there
            if (strategyMode == StrategyMode.NoCorruption)
                return false;

            return vaalCount < minThreshold;
        }

        /// <summary>
        /// Evaluates whether the current inventory state warrants an alert under the active strategy.
        /// </summary>
        public bool ShouldAlert(Settings settings)
        {
            if (!HasScanned)
                return false;

            return ShouldAlert(
                settings.VaalOrbAlert.EnableAlert.Value,
                VaalOrbCount,
                settings.VaalOrbAlert.MinVaalOrbs.Value,
                settings.Tracking.Strategy.Mode);
        }

        private int ScanPlayerInventories()
        {
            try
            {
                var serverData = _gameController?.IngameState?.ServerData;
                if (serverData == null)
                    return VaalOrbCount;

                var playerInventories = serverData.PlayerInventories;
                if (playerInventories == null || playerInventories.Count == 0)
                    return VaalOrbCount;

                var total = 0;
                total += CountInSlot(playerInventories, InventorySlotE.MainInventory1);
                total += CountInSlot(playerInventories, InventorySlotE.ExpandedMainInventory1);
                total += CountInSlot(playerInventories, InventorySlotE.Cursor1);

                return total;
            }
            catch
            {
                // Return previous valid count on transient game memory read exceptions
                return VaalOrbCount;
            }
        }

        private int CountInSlot(IList<InventoryHolder> inventories, InventorySlotE slot)
        {
            var targetId = (int)slot;
            InventoryHolder? holder = null;

            for (var i = 0; i < inventories.Count; i++)
            {
                var h = inventories[i];
                if (h != null && (h.Id == targetId || (int)h.TypeId == targetId))
                {
                    holder = h;
                    break;
                }
            }

            if (holder == null && targetId >= 0 && targetId < inventories.Count)
                holder = inventories[targetId];

            if (holder?.Inventory?.Items is not { } items)
                return 0;

            var sum = 0;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.Address == 0)
                    continue;

                if (IsVaalOrb(item))
                {
                    if (item.TryGetComponent<Stack>(out var stack) && stack.Size > 0)
                        sum += stack.Size;
                    else
                        sum += 1;
                }
            }

            return sum;
        }

        private bool IsVaalOrb(Entity item)
        {
            var path = item.Path;
            if (string.IsNullOrEmpty(path))
                return false;

            // Direct path match
            if (IsVaalOrbPath(path))
                return true;

            // Game file translation fallback
            if (_gameController?.Files?.BaseItemTypes?.Translate(path)?.BaseName is { } baseName)
                return string.Equals(baseName, "Vaal Orb", StringComparison.OrdinalIgnoreCase);

            return false;
        }

        /// <summary>
        /// Matches standard PoE resource paths for Vaal Orbs.
        /// </summary>
        public static bool IsVaalOrbPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            return path.Equals("Metadata/Items/Currency/CurrencyVaal", StringComparison.OrdinalIgnoreCase) ||
                   path.Equals("Metadata/Items/Currency/CurrencyVaalOrb", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/CurrencyVaal", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/CurrencyVaalOrb", StringComparison.OrdinalIgnoreCase);
        }
    }
}
