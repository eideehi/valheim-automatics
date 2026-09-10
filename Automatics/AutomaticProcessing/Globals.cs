using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    using ContainerList = List<(Container container, float distance)>;

    internal static class Globals
    {
        public static ValheimObject Container => ValheimObject.Container;
    }

    internal static class Logics
    {
        private static readonly Dictionary<string, ContainerList> Containers;
        private static float _lastContainersReset;

        static Logics()
        {
            Containers = new Dictionary<string, ContainerList>();
        }

        public static void Cleanup()
        {
            Containers.Clear();
            _lastContainersReset = 0f;
        }

        public static void CraftingLog(string materialName, int count, string fromName,
            Vector3 fromPos, string toName, Vector3 toPos, string productName)
        {
            const string format = "{0} x{1} was set from {2}{3} to {4}{5} for crafting {6}";
            Automatics.Logger.Debug(() =>
                string.Format(format, Automatics.L10N.Translate(materialName), count,
                    Automatics.L10N.Translate(fromName), fromPos, Automatics.L10N.Translate(toName),
                    toPos, Automatics.L10N.Translate(productName)));
        }

        // Shared body for the item-from-container-to-station transfer logs, which differ
        // only in their format string. CraftingLog keeps its own shape (extra product arg).
        private static void TransferLog(string format, string itemName, int count,
            string toName, Vector3 toPos, string fromName, Vector3 fromPos)
        {
            Automatics.Logger.Debug(() =>
                string.Format(format, Automatics.L10N.Translate(itemName), count,
                    Automatics.L10N.Translate(toName), toPos,
                    Automatics.L10N.Translate(fromName), fromPos));
        }

        public static void RefuelLog(string fuelName, int count, string toName, Vector3 toPos,
            string fromName, Vector3 fromPos)
            => TransferLog("Refueled {0} x{1} in {2}{3} from {4}{5}", fuelName, count, toName,
                toPos, fromName, fromPos);

        public static void StoreLog(string productName, int count, string toName, Vector3 toPos,
            string fromName, Vector3 fromPos)
            => TransferLog("Stored {0} x{1} in {2}{3} from {4}{5}", productName, count, toName,
                toPos, fromName, fromPos);

        public static void ChargeLog(string itemName, int count, string toName, Vector3 toPos,
            string fromName, Vector3 fromPos)
            => TransferLog("Charge {0} x{1} to {2}{3} from {4}{5}", itemName, count, toName,
                toPos, fromName, fromPos);

        public static bool IsAllowContainer(Container container)
        {
            return ContainerAccess.IsAllowed(container, Config.AllowContainer);
        }

        public static bool IsAllowProcessing(string target, Process type)
        {
            return (Config.AllowProcessing(target) & type) != 0;
        }

        // Container.OnContainerChanged only saves to the ZDO when the local
        // peer owns the ZNetView, so claim ownership immediately before mutating.
        // Processing runs owner-side, which includes dedicated servers where
        // there is no local player, so the mutation gate is server-aware: with a
        // local player it enforces the full player access rules (ward,
        // private/group, in-use, wagon), and without one it falls back to the
        // owner-only claim path so server-side processing keeps working.
        public static bool TryClaimContainer(Container container)
        {
            return ContainerAccess.TryPrepareContainerMutationServerAware(
                Player.m_localPlayer, container);
        }

        public static bool TryRemoveItem(Inventory inventory, string itemName, int minCount,
            out string prefabName)
        {
            return TryRemoveItem(inventory, itemName, minCount, out prefabName, out _);
        }

        /// <summary>
        /// Removes one matching item and reports both the drop prefab name and the removed
        /// item's cheat provenance, which the vanilla add RPCs carry alongside the name.
        /// </summary>
        /// <param name="requiredPrefabName">
        /// When set, only items whose drop prefab has this exact name are eligible. The
        /// vanilla add paths validate the item against the station's conversion list before
        /// removing it (Smelter.OnAddOre / CookingStation.CookItem / Fermenter.AddItem all
        /// call IsItemAllowed first), and items are looked up here by shared display name,
        /// which several prefabs can share. Filtering before the removal keeps an item the
        /// receiving RPC would reject in the container instead of destroying it.
        /// </param>
        public static bool TryRemoveItem(Inventory inventory, string itemName, int minCount,
            out string prefabName, out bool cheated, string requiredPrefabName = null)
        {
            prefabName = null;
            cheated = false;
            if (inventory == null || string.IsNullOrEmpty(itemName)) return false;

            if (!Inventories.HaveItem(inventory, itemName, 0, WorldLevelMatchMode.Ignore,
                    minCount + 1))
                return false;

            foreach (var item in Inventories.GetItems(inventory, itemName, 0,
                         WorldLevelMatchMode.Ignore))
            {
                if (requiredPrefabName != null &&
                    (item.m_dropPrefab == null || item.m_dropPrefab.name != requiredPrefabName))
                    continue;

                if (TryRemoveItem(inventory, item, out prefabName, out cheated))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Non-mutating counterpart of the by-name <see cref="TryRemoveItem(Inventory,string,int,out string,out bool,string)" />:
        /// returns the item that call would remove, or null when the inventory holds no
        /// eligible one. Probing has to be possible without touching the container, because
        /// claiming a container cannot be undone (see <see cref="TryTakeMaterial" />), so
        /// eligibility must be decided before the claim.
        /// </summary>
        public static ItemDrop.ItemData FindRemovableItem(Inventory inventory, string itemName,
            int minCount, string requiredPrefabName = null)
        {
            if (inventory == null || string.IsNullOrEmpty(itemName)) return null;

            if (!Inventories.HaveItem(inventory, itemName, 0, WorldLevelMatchMode.Ignore,
                    minCount + 1))
                return null;

            return Inventories.GetItems(inventory, itemName, 0, WorldLevelMatchMode.Ignore)
                .FirstOrDefault(x => x.m_dropPrefab != null &&
                                     (requiredPrefabName == null ||
                                      x.m_dropPrefab.name == requiredPrefabName));
        }

        /// <summary>
        /// Takes one material from the first candidate container that actually holds an item
        /// the station accepts. Candidates are chosen by shared display name while the
        /// removal is filtered by drop prefab name, so a candidate can match the name and
        /// still hold nothing eligible; when that happens the search continues with the next
        /// candidate instead of abandoning the conversion for the tick, which a permanent
        /// decoy item would otherwise do forever.
        /// </summary>
        /// <remarks>
        /// Eligibility is probed with <see cref="FindRemovableItem" /> before
        /// <see cref="TryClaimContainer" /> is called, so a container holding no eligible
        /// item is skipped without being claimed. That is the whole guarantee: claiming
        /// takes ZDO ownership (ZNetView.ClaimOwnership -> ZDO.SetOwner plus an
        /// owner-revision bump) and nothing here can hand ownership back, while
        /// ContainerAccess claims before its post-claim access checks and a removal can
        /// still fail after a successful probe. Ownership can therefore change on a
        /// container this ends up not taking from; that matches the pre-existing behaviour
        /// and is not something the caller can undo.
        /// </remarks>
        public static bool TryTakeMaterial(IEnumerable<Container> candidates, string itemName,
            int minCount, string requiredPrefabName, out Container source, out string prefabName,
            out bool cheated)
        {
            source = null;
            prefabName = null;
            cheated = false;
            if (candidates == null) return false;

            foreach (var container in candidates)
            {
                if (!container) continue;

                var inventory = container.GetInventory();
                if (FindRemovableItem(inventory, itemName, minCount, requiredPrefabName) == null)
                    continue;

                if (!TryClaimContainer(container)) continue;
                // Removal goes back through the filtered by-name path on purpose: the
                // drop-prefab guard that keeps a would-be-rejected item from being destroyed
                // stays on the removal itself, not only on the pre-claim probe.
                if (!TryRemoveItem(inventory, itemName, minCount, out prefabName, out cheated,
                        requiredPrefabName))
                    continue;

                source = container;
                return true;
            }

            prefabName = null;
            cheated = false;
            return false;
        }

        public static bool TryRemoveItem(Inventory inventory, ItemDrop.ItemData item,
            out string prefabName)
        {
            return TryRemoveItem(inventory, item, out prefabName, out _);
        }

        public static bool TryRemoveItem(Inventory inventory, ItemDrop.ItemData item,
            out string prefabName, out bool cheated)
        {
            prefabName = null;
            cheated = false;
            if (inventory == null || item == null || item.m_dropPrefab == null) return false;

            prefabName = item.m_dropPrefab.name;
            cheated = item.m_cheated;
            return inventory.RemoveItem(item, 1);
        }

        public static int AddItemAndGetCountDelta(Inventory inventory, GameObject prefab,
            string itemName, int amount)
        {
            if (inventory == null || prefab == null || amount <= 0) return 0;

            var itemCountBefore = inventory.CountItems(itemName);
            inventory.AddItem(prefab, amount);
            return Mathf.Max(0, inventory.CountItems(itemName) - itemCountBefore);
        }

        public static int GetProductStackLimitedAmount(Inventory inventory, string itemName,
            int maxStackSize, int maxProductStacks, int amount)
        {
            if (inventory == null || amount <= 0) return 0;
            if (maxProductStacks <= 0) return amount;

            var maxProductCount = maxProductStacks * Mathf.Max(1, maxStackSize);
            var remainingCapacity = maxProductCount - inventory.CountItems(itemName);
            return Mathf.Clamp(remainingCapacity, 0, amount);
        }

        public static IEnumerable<(Container container, float distance)> GetNearbyContainers(
            string target, Vector3 origin)
        {
            if (Time.time - _lastContainersReset > 1f)
            {
                _lastContainersReset = Time.time;
                Containers.Clear();
            }

            var range = Config.ContainerSearchRange(target);
            var limit = Config.ContainerReferenceLimit(target);
            // Key on the exact origin components, not Vector3.GetHashCode(): two processors
            // with the same name/range/limit at different positions can share a hash and
            // would otherwise read each other's (wrongly ordered) container list.
            var cacheKey = target + ":" + origin.x + ":" + origin.y + ":" + origin.z +
                           ":" + range + ":" + limit;
            if (Containers.TryGetValue(cacheKey, out var cache)) return cache.Where(x => x.container);

            var containers = new ContainerList();
            Containers[cacheKey] = containers;

            var player = Player.m_localPlayer;
            if (range > 0)
                containers.AddRange((from x in ContainerCache.GetAllInstance()
                    let distance = Vector3.Distance(origin, x.transform.position)
                    where distance <= range &&
                          IsAllowContainer(x) &&
                          (player == null || ContainerAccess.CanUseContainer(player, x))
                    orderby distance
                    select (x, distance)).Take(limit > 0 ? limit : int.MaxValue));

            // Filter destroyed containers on this path too, matching the cache-hit branch.
            return containers.Where(x => x.container);
        }
    }
}
