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

        private static float _lastQuickCraftReset;
        private static float _lastQuickRefuelReset;

        static SmelterProcess()
        {
            SkipQuickCraft = new HashSet<ZDOID>();
            FirstQuickCraft = new HashSet<ZDOID>();
            SkipQuickRefuel = new HashSet<ZDOID>();
            FirstQuickRefuel = new HashSet<ZDOID>();
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
        }

        [UsedImplicitly]
        public static string QuickCraft(Smelter smelter, ZNetView zNetView, float loopCount,
            string ore)
        {
            if (loopCount < 1f) return ore;
            if (!string.IsNullOrEmpty(ore)) return ore;
            if (!Config.EnableAutomaticProcessing) return ore;

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
                Container materialContainer = null;
                var productContainerFound = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containersWithInventory)
                {
                    if (materialContainer && productContainerFound) break;

                    if (materialContainer == null)
                        if (Inventories.HaveItem(inventory, materialData.m_name,
                                minMaterialCount + 1))
                            materialContainer = container;

                    if (maxProductStacks > 0 && !productContainerFound)
                    {
                        var productCount = inventory.CountItems(productData.m_name);
                        var stacks = productCount / productData.m_maxStackSize;
                        if (stacks < maxProductStacks)
                            productContainerFound = inventory.CanAddItem(productItem, 1);
                    }
                }

                if (materialContainer && (maxProductStacks == 0 || productContainerFound))
                {
                    var item = materialContainer.GetInventory().GetItem(materialData.m_name);
                    materialContainer.GetInventory().RemoveOneItem(item);

                    Reflections.InvokeMethod(smelter, "QueueOre", item.m_dropPrefab.name);

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
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Craft)) return;

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

            foreach (var conversion in conversions)
            {
                Container materialContainer = null;
                var productContainerFound = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containersWithInventory)
                {
                    if (materialContainer && productContainerFound) break;

                    if (materialContainer == null)
                        if (Inventories.HaveItem(inventory, materialData.m_name,
                                minMaterialCount + 1))
                            materialContainer = container;

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

                if (materialContainer && (maxProductStacks == 0 || productContainerFound))
                {
                    var item = materialContainer.GetInventory().GetItem(materialData.m_name);
                    materialContainer.GetInventory().RemoveOneItem(item);
                    zNetView.InvokeRPC("AddOre", item.m_dropPrefab.name);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position, smelterName,
                        origin, productData.m_name);
                    break;
                }
            }
        }

        [UsedImplicitly]
        public static float QuickRefuel(Smelter smelter, ZNetView zNetView, float loopCount,
            float fuel)
        {
            if (loopCount < 1f) return fuel;
            if (fuel > 0f) return fuel;
            if (!Config.EnableAutomaticProcessing) return fuel;

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
                if (!Inventories.HaveItem(inventory, fuelName, minFuelCount + 1)) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");

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
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Refuel)) return;

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

            foreach (var (container, _) in Logics.GetNearbyContainers(smelterName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, minFuelCount + 1)) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");

                Logics.RefuelLog(fuelName, 1, smelterName, origin, container.m_name,
                    container.transform.position);
                break;
            }
        }

        public static bool Store(Smelter smelter, string ore, int stack)
        {
            if (!Config.EnableAutomaticProcessing) return false;

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
                var itemCountBefore = inventory.CountItems(itemName);
                if (Config.StoreOnlyIfProductExists(smelterName) &&
                    !Inventories.HaveItem(inventory, itemName, 1)) continue;
                if (!inventory.AddItem(item.gameObject, stack)) continue;

                var storedItemCount = inventory.CountItems(itemName) - itemCountBefore;
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