using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticStorage
{
    internal static class AutomaticStorage
    {
        private sealed class ContainerTarget
        {
            public int Index { get; set; }
            public Container Container { get; set; }
            public Inventory Inventory { get; set; }
        }

        internal sealed class Result
        {
            public int StoredItems { get; set; }
            public int TouchedContainers { get; set; }
        }

        /// <summary>
        ///     The one method that moves items, and so the stop boundary for them (R1.5a). It checks
        ///     <see cref="Module.Running" /> once, as its first statement, and never again; see
        ///     there for why.
        /// </summary>
        public static Result Store(Player player)
        {
            if (!Module.Running) return new Result();

            var result = new Result();
            if (player == null) return result;

            var playerInventory = player.GetInventory();
            if (playerInventory == null) return result;

            var containers = GetNearbyContainers(player).ToList();
            if (containers.Count == 0) return result;

            var exclusions = ItemExclusionMatcher.Create(Config.ExcludedItems);
            var touchedContainers = new HashSet<int>();
            foreach (var item in playerInventory.GetAllItemsInGridOrder().ToList())
            {
                if (!CanStoreItem(player, item, exclusions)) continue;

                var remaining = item.m_stack;
                while (remaining > 0)
                {
                    var routes = GetRoutes(item, containers);
                    if (routes.Count == 0) break;

                    var movedInPass = 0;
                    foreach (var route in routes)
                    {
                        if (remaining <= 0) break;

                        if (route.ContainerIndex < 0 || route.ContainerIndex >= containers.Count)
                            continue;

                        var target = containers[route.ContainerIndex];
                        if (!ContainerAccess.CanDirectlyMutateContainer(player, target.Container))
                            continue;

                        var moved = route.Kind == StorageRouteKind.ExactStack
                            ? TransferExactStacks(player, playerInventory, item, remaining, target)
                            : TransferAny(player, playerInventory, item, remaining, target);
                        if (moved <= 0) continue;

                        remaining -= moved;
                        movedInPass += moved;
                        result.StoredItems += moved;
                        touchedContainers.Add(target.Index);

                        Automatics.Logger.Debug(() =>
                            $"Stored {item.m_shared.m_name} x{moved} in {Objects.GetName(target.Container)}");
                    }

                    if (movedInPass == 0) break;
                }
            }

            result.TouchedContainers = touchedContainers.Count;
            return result;
        }

        public static void ShowResultMessage(Player player, Result result)
        {
            if (player == null || result == null || result.StoredItems <= 0) return;
            if (Config.StorageMessage == Message.None) return;

            var type = Config.StorageMessage.ToMessageType();
            player.Message(type,
                Automatics.L10N.Localize("@message_automatic_storage_stored_items",
                    result.StoredItems, result.TouchedContainers));
        }

        /// <summary>
        ///     Everything this module keeps between calls: the invalid exclusion patterns it has
        ///     already warned about. A disable starts again from here, so a pattern that is still
        ///     invalid is reported once more after the module is turned back on (R6.1).
        /// </summary>
        internal static void ResetSession()
        {
            ItemExclusionMatcher.ForgetReportedRegexes();
        }

        private static List<StorageRoute> GetRoutes(ItemDrop.ItemData item,
            IReadOnlyList<ContainerTarget> containers)
        {
            var itemSnapshot = CreateSnapshot(item);
            var containerSnapshots = containers.Where(IsValidTarget)
                                               .Select(CreateSnapshot)
                                               .ToList();
            return StoragePlanner.Plan(itemSnapshot, containerSnapshots);
        }

        private static IEnumerable<ContainerTarget> GetNearbyContainers(Player player)
        {
            var origin = player.transform.position;
            var range = Config.StorageSearchRange;
            var limit = Config.ContainerReferenceLimit;

            var index = 0;
            foreach (var container in ContainerCache.GetAllInstance()
                         .Where(x => x != null)
                         .Select(x => new
                         {
                             Container = x,
                             Distance = Vector3.Distance(origin, x.transform.position)
                         })
                         .Where(x => x.Distance <= range)
                         .Where(x => ContainerAccess.IsAllowed(x.Container, Config.AllowContainer))
                         .Where(x => ContainerAccess.CanDirectlyMutateContainer(player, x.Container))
                         .OrderBy(x => x.Distance)
                         .Take(limit > 0 ? limit : int.MaxValue))
            {
                yield return new ContainerTarget
                {
                    Index = index++,
                    Container = container.Container,
                    Inventory = container.Container.GetInventory()
                };
            }
        }

        private static bool CanStoreItem(Player player, ItemDrop.ItemData item,
            ItemExclusionMatcher exclusions)
        {
            if (player == null || item == null || item.m_shared == null) return false;
            if (item.m_stack <= 0) return false;
            if (item.m_dropPrefab == null) return false;
            if (item.m_equipped || player.IsItemEquiped(item)) return false;
            if (item.m_shared.m_questItem) return false;
            if (!Config.StoreHotbarItems && item.m_gridPos.y == 0) return false;
            if (!Config.IsAllowedItemType(item.m_shared.m_itemType)) return false;
            if (exclusions.IsExcluded(item)) return false;

            return true;
        }

        private static int TransferExactStacks(Player player, Inventory playerInventory,
            ItemDrop.ItemData sourceItem, int sourceRemaining, ContainerTarget target)
        {
            var exactSpace = GetExactFreeStackSpace(target.Inventory, sourceItem);
            if (exactSpace <= 0) return 0;

            return TransferAmount(player, playerInventory, sourceItem, sourceRemaining, target,
                Mathf.Min(sourceRemaining, exactSpace));
        }

        private static int TransferAny(Player player, Inventory playerInventory,
            ItemDrop.ItemData sourceItem, int sourceRemaining, ContainerTarget target)
        {
            var moved = 0;
            var maxStackSize = Mathf.Max(1, sourceItem.m_shared.m_maxStackSize);
            while (sourceRemaining > moved)
            {
                var chunk = Mathf.Min(maxStackSize, sourceRemaining - moved);
                var accepted = TransferAmount(player, playerInventory, sourceItem,
                    sourceRemaining - moved, target, chunk);
                if (accepted <= 0) break;

                moved += accepted;
                if (accepted < chunk) break;
            }

            return moved;
        }

        private static int TransferAmount(Player player, Inventory playerInventory,
            ItemDrop.ItemData sourceItem, int sourceRemaining, ContainerTarget target, int amount)
        {
            if (playerInventory == null || sourceItem == null || target?.Inventory == null)
                return 0;
            if (amount <= 0 || sourceRemaining <= 0) return 0;
            if (!playerInventory.GetAllItems().Contains(sourceItem)) return 0;
            if (!ContainerAccess.CanDirectlyMutateContainer(player, target.Container)) return 0;
            if (!ContainerAccess.TryPrepareOwnedContainerMutation(target.Container)) return 0;

            var amountToMove = Mathf.Min(amount, sourceRemaining, sourceItem.m_stack);
            if (amountToMove <= 0) return 0;

            var beforeTransfer = CaptureTransferState(target.Inventory, sourceItem);
            var before = CountExactItems(target.Inventory, sourceItem);
            var clone = CloneForTransfer(sourceItem, amountToMove);
            target.Inventory.AddItem(clone);
            var accepted = Mathf.Clamp(CountExactItems(target.Inventory, sourceItem) - before,
                0, amountToMove);
            if (accepted <= 0) return 0;

            if (!playerInventory.RemoveItem(sourceItem, accepted))
            {
                var rolledBack = RollbackTransfer(target.Inventory, sourceItem, beforeTransfer,
                    accepted);
                Automatics.Logger.Error(
                    $"Failed to remove stored item from player inventory: {sourceItem.m_shared.m_name} x{accepted}. " +
                    $"Rolled back {rolledBack} destination items.");
                return 0;
            }

            return accepted;
        }

        private static ItemDrop.ItemData CloneForTransfer(ItemDrop.ItemData item, int amount)
        {
            var clone = item.Clone();
            clone.m_stack = amount;
            clone.m_gridPos = new Vector2i(-1, -1);
            return clone;
        }

        private static int CountExactItems(Inventory inventory, ItemDrop.ItemData item)
        {
            if (inventory == null || item == null || item.m_shared == null) return 0;

            var count = 0;
            foreach (var storedItem in inventory.GetAllItems())
            {
                if (!IsSameStack(storedItem, item)) continue;
                count += storedItem.m_stack;
            }

            return count;
        }

        private static Dictionary<ItemDrop.ItemData, int> CaptureTransferState(Inventory inventory,
            ItemDrop.ItemData item)
        {
            var state = new Dictionary<ItemDrop.ItemData, int>();
            if (inventory == null || item == null) return state;

            foreach (var storedItem in inventory.GetAllItems())
            {
                if (!IsSameStack(storedItem, item)) continue;
                state[storedItem] = storedItem.m_stack;
            }

            return state;
        }

        private static int RollbackTransfer(Inventory inventory, ItemDrop.ItemData item,
            Dictionary<ItemDrop.ItemData, int> beforeTransfer, int amount)
        {
            if (inventory == null || item == null || amount <= 0) return 0;
            if (beforeTransfer == null)
                beforeTransfer = new Dictionary<ItemDrop.ItemData, int>();

            var removed = 0;
            foreach (var storedItem in inventory.GetAllItems().ToList())
            {
                if (removed >= amount) break;
                if (!IsSameStack(storedItem, item)) continue;

                var beforeStack = beforeTransfer.TryGetValue(storedItem, out var stack)
                    ? stack
                    : 0;
                var addedStack = Mathf.Max(0, storedItem.m_stack - beforeStack);
                var removeCount = Mathf.Min(amount - removed, addedStack);
                if (removeCount <= 0) continue;
                if (!inventory.RemoveItem(storedItem, removeCount)) continue;

                removed += removeCount;
            }

            return removed;
        }

        private static int GetExactFreeStackSpace(Inventory inventory, ItemDrop.ItemData item)
        {
            if (inventory == null || item == null || item.m_shared == null) return 0;

            var space = 0;
            foreach (var storedItem in inventory.GetAllItems())
            {
                if (!IsSameStack(storedItem, item)) continue;
                if (storedItem.m_stack >= storedItem.m_shared.m_maxStackSize) continue;

                space += storedItem.m_shared.m_maxStackSize - storedItem.m_stack;
            }

            return space;
        }

        private static bool IsSameStack(ItemDrop.ItemData storedItem, ItemDrop.ItemData item)
        {
            return storedItem != null &&
                   storedItem.m_shared != null &&
                   item != null &&
                   item.m_shared != null &&
                   storedItem.m_shared.m_name == item.m_shared.m_name &&
                   storedItem.m_quality == item.m_quality &&
                   storedItem.m_worldLevel == item.m_worldLevel;
        }

        private static StorageItemSnapshot CreateSnapshot(ItemDrop.ItemData item)
        {
            return new StorageItemSnapshot(
                item.m_shared.m_name,
                item.m_quality,
                Convert.ToInt32(item.m_worldLevel),
                Convert.ToInt32(item.m_shared.m_itemType),
                item.m_stack,
                Mathf.Max(1, item.m_shared.m_maxStackSize));
        }

        private static StorageContainerSnapshot CreateSnapshot(ContainerTarget target)
        {
            return new StorageContainerSnapshot(
                target.Index,
                target.Inventory.GetEmptySlots(),
                target.Inventory.GetAllItems().Where(x => x != null && x.m_shared != null)
                      .Select(CreateSnapshot));
        }

        private static bool IsValidTarget(ContainerTarget target)
        {
            return target != null &&
                   target.Container != null &&
                   target.Inventory != null &&
                   Objects.GetZNetView(target.Container, out var nview) &&
                   nview != null &&
                   nview.IsValid() &&
                   nview.GetZDO() != null;
        }

        private sealed class ItemExclusionMatcher
        {
            private static readonly HashSet<string> ReportedInvalidRegexes =
                new HashSet<string>(StringComparer.Ordinal);

            private readonly List<string> _partialMatches;
            private readonly List<Regex> _regexes;

            private ItemExclusionMatcher(List<string> partialMatches, List<Regex> regexes)
            {
                _partialMatches = partialMatches;
                _regexes = regexes;
            }

            public static ItemExclusionMatcher Create(IEnumerable<string> entries)
            {
                var partialMatches = new List<string>();
                var regexes = new List<Regex>();
                foreach (var entry in entries ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    var trimmed = entry.Trim();
                    if (!trimmed.StartsWith("r/", StringComparison.Ordinal))
                    {
                        partialMatches.Add(trimmed);
                        continue;
                    }

                    var pattern = trimmed.Substring(2);
                    try
                    {
                        regexes.Add(new Regex(pattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                    }
                    catch (ArgumentException e)
                    {
                        if (ReportedInvalidRegexes.Add(pattern))
                            Automatics.Logger.Warning(
                                $"Invalid automatic storage exclusion regex `{pattern}` ignored: {e.Message}");
                    }
                }

                return new ItemExclusionMatcher(partialMatches, regexes);
            }

            public static void ForgetReportedRegexes()
            {
                ReportedInvalidRegexes.Clear();
            }

            public bool IsExcluded(ItemDrop.ItemData item)
            {
                var names = GetCandidateNames(item).ToList();
                foreach (var partialMatch in _partialMatches)
                foreach (var name in names)
                    if (name.IndexOf(partialMatch, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;

                foreach (var regex in _regexes)
                foreach (var name in names)
                    if (regex.IsMatch(name))
                        return true;

                return false;
            }

            private static IEnumerable<string> GetCandidateNames(ItemDrop.ItemData item)
            {
                if (item == null || item.m_shared == null) yield break;

                var sharedName = item.m_shared.m_name;
                if (!string.IsNullOrEmpty(sharedName))
                {
                    yield return sharedName;
                    if (sharedName.Length > 1 && (sharedName[0] == '$' || sharedName[0] == '@'))
                        yield return sharedName.Substring(1);

                    var localized = Automatics.L10N.TranslateInternalName(sharedName);
                    if (!string.IsNullOrEmpty(localized))
                        yield return localized;
                }

                if (item.m_dropPrefab != null && !string.IsNullOrEmpty(item.m_dropPrefab.name))
                    yield return item.m_dropPrefab.name;
            }
        }
    }
}
