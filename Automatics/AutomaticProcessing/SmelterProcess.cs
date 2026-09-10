using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class SmelterProcess
    {
        private static readonly HashSet<ZDOID> SkipQuickCraft;
        private static readonly HashSet<ZDOID> FirstQuickCraft;
        private static readonly HashSet<ZDOID> SkipQuickRefuel;
        private static readonly HashSet<ZDOID> FirstQuickRefuel;

        // The per-frame Craft/Refuel postfixes (Smelter.UpdateSmelter runs every
        // frame on the owner) throttle their nearby-container scan: after a pass
        // that adds nothing, the smelter is skipped for ~1s instead of re-running
        // the Dictionary/List/LINQ scan every frame. Mirrors the QuickCraft path.
        private static readonly HashSet<ZDOID> SkipCraft;
        private static readonly HashSet<ZDOID> SkipRefuel;

        private static float _lastQuickCraftReset;
        private static float _lastQuickRefuelReset;
        private static float _lastCraftReset;
        private static float _lastRefuelReset;

        static SmelterProcess()
        {
            SkipQuickCraft = new HashSet<ZDOID>();
            FirstQuickCraft = new HashSet<ZDOID>();
            SkipQuickRefuel = new HashSet<ZDOID>();
            FirstQuickRefuel = new HashSet<ZDOID>();
            SkipCraft = new HashSet<ZDOID>();
            SkipRefuel = new HashSet<ZDOID>();
        }

        private static Smelter.ItemConversion GetItemConversion(Smelter smelter, string queuedOre)
        {
            return smelter.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == queuedOre);
        }

        public static void Cleanup()
        {
            SkipQuickCraft.Clear();
            FirstQuickCraft.Clear();
            SkipQuickRefuel.Clear();
            FirstQuickRefuel.Clear();
            SkipCraft.Clear();
            SkipRefuel.Clear();
        }

        [UsedImplicitly]
        public static string QuickCraft(Smelter smelter, ZNetView zNetView, float loopCount,
            string ore)
        {
            if (loopCount < 1f) return ore;
            if (!string.IsNullOrEmpty(ore)) return ore;
            if (!Config.EnableAutomaticProcessing) return ore;
            if (!Objects.HasValidOwnership(zNetView)) return ore;

            if (Time.time - _lastQuickCraftReset > 1f)
            {
                _lastQuickCraftReset = Time.time;
                SkipQuickCraft.Clear();
                FirstQuickCraft.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipQuickCraft.Contains(uid)) return ore;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Craft)) return ore;

            // Smelter.OnAddOre refuses once the queue is full; QuickCraft is injected
            // ahead of UpdateSmelter's own m_maxOre gate, so enforce the same bound here
            // (notably m_maxOre == 0, where the station accepts no ore at all).
            if (zNetView.GetZDO().GetInt("queued") >= smelter.m_maxOre) return ore;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(smelterName);
            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(smelterName);

            var transform = smelter.transform;
            var origin = transform.position;
            var containersWithInventory = Logics.GetNearbyContainers(smelterName, origin)
                .Select(x => (x.container, x.container.GetInventory()))
                .ToList();

            var oreAdded = false;
            foreach (var conversion in smelter.m_conversion)
            {
                var materialContainers = new List<Container>();
                var productContainerFound = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containersWithInventory)
                {
                    // Keep every container that satisfies the material predicate, not just
                    // the first: the pick is by shared display name while the removal is by
                    // drop prefab name, so the first match is not guaranteed to hold an item
                    // this conversion accepts. Latching only the first lets a permanent
                    // display-name decoy starve the conversion for good.
                    if (Inventories.HaveItem(inventory, materialData.m_name,
                            0, WorldLevelMatchMode.Ignore, minMaterialCount + 1))
                        materialContainers.Add(container);

                    if (maxProductStacks > 0 && !productContainerFound)
                    {
                        var productCount = inventory.CountItems(productData.m_name);
                        var stacks = productCount / productData.m_maxStackSize;
                        if (stacks < maxProductStacks)
                            productContainerFound = inventory.CanAddItem(productItem, 1);
                    }
                }

                if (materialContainers.Count > 0 &&
                    (maxProductStacks == 0 || productContainerFound))
                {
                    // Walks the candidates in distance order, skipping any that holds no
                    // eligible item without claiming it.
                    if (!Logics.TryTakeMaterial(materialContainers, materialData.m_name,
                            minMaterialCount, conversion.m_from.gameObject.name,
                            out var materialContainer, out var prefabName, out var cheated))
                        continue;

                    // Smelter.QueueOre(string name, bool cheated): `cheated` records whether
                    // the queued ore was cheat-spawned, and is what RPC_AddOre forwards.
                    Reflections.InvokeMethod(smelter, "QueueOre", prefabName, cheated);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position, smelterName,
                        origin, productData.m_name);

                    oreAdded = true;
                    break;
                }
            }

            if (!oreAdded)
            {
                SkipQuickCraft.Add(uid);
                return ore;
            }

            if (FirstQuickCraft.Add(uid))
                smelter.m_oreAddedEffects.Create(origin, transform.rotation);

            return zNetView.GetZDO().GetString("item0");
        }

        [UsedImplicitly]
        public static void Craft(Smelter smelter, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Craft)) return;

            if (Time.time - _lastCraftReset > 1f)
            {
                _lastCraftReset = Time.time;
                SkipCraft.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipCraft.Contains(uid)) return;

            var oreCount = zNetView.GetZDO().GetInt("queued");
            if (oreCount >= smelter.m_maxOre) return;
            if (Config.SupplyOnlyWhenMaterialsRunOut(smelterName) && oreCount > 0) return;

            var productCounts = new Dictionary<string, int>();
            var conversions = new List<Smelter.ItemConversion>();
            for (var i = 0; i < oreCount; i++)
            {
                var queuedOre = zNetView.GetZDO().GetString("item" + i);
                if (string.IsNullOrEmpty(queuedOre)) continue;

                var conversion = GetItemConversion(smelter, queuedOre);
                if (conversion == null) continue;

                conversions.Add(conversion);
                var productName = conversion.m_to.m_itemData.m_shared.m_name;
                productCounts[productName] =
                    productCounts.TryGetValue(productName, out var count) ? count + 1 : 1;
            }

            conversions.Reverse();
            conversions.AddRange(smelter.m_conversion.Where(x => !conversions.Contains(x)));

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(smelterName);
            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(smelterName);

            var origin = smelter.transform.position;
            var containersWithInventory = Logics.GetNearbyContainers(smelterName, origin)
                .Select(x => (x.container, x.container.GetInventory()))
                .ToList();

            var oreAdded = false;
            foreach (var conversion in conversions)
            {
                var materialContainers = new List<Container>();
                var productContainerFound = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containersWithInventory)
                {
                    // Keep every container that satisfies the material predicate, not just
                    // the first: the pick is by shared display name while the removal is by
                    // drop prefab name, so the first match is not guaranteed to hold an item
                    // this conversion accepts. Latching only the first lets a permanent
                    // display-name decoy starve the conversion for good.
                    if (Inventories.HaveItem(inventory, materialData.m_name,
                            0, WorldLevelMatchMode.Ignore, minMaterialCount + 1))
                        materialContainers.Add(container);

                    if (maxProductStacks > 0 && !productContainerFound)
                    {
                        if (!productCounts.TryGetValue(productData.m_name, out var cookingCount))
                            cookingCount = 0;

                        var productCount = inventory.CountItems(productData.m_name) + cookingCount;
                        var stacks = productCount / productData.m_maxStackSize;
                        if (stacks < maxProductStacks)
                            productContainerFound = inventory.CanAddItem(productItem, 1);
                    }
                }

                if (materialContainers.Count > 0 &&
                    (maxProductStacks == 0 || productContainerFound))
                {
                    // Walks the candidates in distance order, skipping any that holds no
                    // eligible item without claiming it.
                    if (!Logics.TryTakeMaterial(materialContainers, materialData.m_name,
                            minMaterialCount, conversion.m_from.gameObject.name,
                            out var materialContainer, out var prefabName, out var cheated))
                        continue;
                    // Smelter registers Register<string, bool>("RPC_AddOre", RPC_AddOre).
                    zNetView.InvokeRPC("RPC_AddOre", prefabName, cheated);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position, smelterName,
                        origin, productData.m_name);

                    oreAdded = true;
                    break;
                }
            }

            if (!oreAdded) SkipCraft.Add(uid);
        }

        [UsedImplicitly]
        public static float QuickRefuel(Smelter smelter, ZNetView zNetView, float loopCount,
            float fuel)
        {
            if (loopCount < 1f) return fuel;
            if (fuel > 0f) return fuel;
            if (!Config.EnableAutomaticProcessing) return fuel;
            if (!Objects.HasValidOwnership(zNetView)) return fuel;

            if (Time.time - _lastQuickRefuelReset > 1f)
            {
                _lastQuickRefuelReset = Time.time;
                SkipQuickRefuel.Clear();
                FirstQuickRefuel.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipQuickRefuel.Contains(uid)) return fuel;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Refuel)) return fuel;

            if (Config.RefuelOnlyWhenMaterialsSupplied(smelterName) &&
                zNetView.GetZDO().GetInt("queued") == 0)
                return fuel;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(smelterName);
            var transform = smelter.transform;
            var origin = transform.position;
            var fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;

            var fuelAdded = false;
            foreach (var (container, _) in Logics.GetNearbyContainers(smelterName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, 0, WorldLevelMatchMode.Ignore, minFuelCount + 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                if (!Logics.TryRemoveItem(inventory, fuelName, minFuelCount, out _)) continue;
                zNetView.InvokeRPC("RPC_AddFuel");

                Logics.RefuelLog(fuelName, 1, smelterName, origin, container.m_name,
                    container.transform.position);

                fuelAdded = true;
                break;
            }

            if (!fuelAdded)
            {
                SkipQuickRefuel.Add(uid);
                return fuel;
            }

            fuel += 1f;
            zNetView.GetZDO().Set("fuel", fuel);

            if (FirstQuickRefuel.Add(uid))
                smelter.m_fuelAddedEffects.Create(origin, transform.rotation, transform);

            return fuel;
        }

        [UsedImplicitly]
        public static void Refuel(Smelter smelter, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Refuel)) return;

            if (Time.time - _lastRefuelReset > 1f)
            {
                _lastRefuelReset = Time.time;
                SkipRefuel.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipRefuel.Contains(uid)) return;

            var fuel = zNetView.GetZDO().GetFloat("fuel");
            if (fuel >= smelter.m_maxFuel - 1) return;
            if (Config.RefuelOnlyWhenOutOfFuel(smelterName) && fuel > 0f) return;
            if (Config.RefuelOnlyWhenMaterialsSupplied(smelterName) &&
                zNetView.GetZDO().GetInt("queued") == 0)
                return;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(smelterName);
            var transform = smelter.transform;
            var origin = transform.position;
            var fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;

            var fuelAdded = false;
            foreach (var (container, _) in Logics.GetNearbyContainers(smelterName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, 0, WorldLevelMatchMode.Ignore, minFuelCount + 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                if (!Logics.TryRemoveItem(inventory, fuelName, minFuelCount, out _)) continue;
                zNetView.InvokeRPC("RPC_AddFuel");

                Logics.RefuelLog(fuelName, 1, smelterName, origin, container.m_name,
                    container.transform.position);

                fuelAdded = true;
                break;
            }

            if (!fuelAdded) SkipRefuel.Add(uid);
        }

        public static bool Store(Smelter smelter, string ore, int stack)
        {
            if (!Config.EnableAutomaticProcessing) return false;
            if (!Objects.HasValidOwnership(smelter, out var zNetView)) return false;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Store)) return false;

            var conversion = GetItemConversion(smelter, ore);
            if (conversion == null) return false;

            var origin = smelter.transform.position;
            var item = conversion.m_to;
            var itemData = item.m_itemData.m_shared;
            var itemName = itemData.m_name;

            var productRemaining = stack;
            foreach (var (container, _) in Logics.GetNearbyContainers(smelterName, origin))
            {
                if (productRemaining <= 0) break;

                var inventory = container.GetInventory();
                if (Config.StoreOnlyIfProductExists(smelterName) &&
                    !Inventories.HaveItem(inventory, itemName, 0, WorldLevelMatchMode.Ignore, 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                var storedItemCount = Logics.AddItemAndGetCountDelta(inventory, item.gameObject,
                    itemName, productRemaining);
                if (storedItemCount <= 0) continue;

                Logics.StoreLog(itemName, storedItemCount, container.m_name,
                    container.transform.position, smelterName, origin);
                productRemaining -= storedItemCount;
            }

            if (productRemaining == stack) return false;

            smelter.m_produceEffects.Create(origin, smelter.transform.rotation);
            if (productRemaining > 0)
            {
                var instance = Object.Instantiate(conversion.m_to.gameObject,
                    smelter.m_outputPoint.position, smelter.m_outputPoint.rotation);
                instance.GetComponent<ItemDrop>().m_itemData.m_stack = productRemaining;
            }

            return true;
        }
    }
}
