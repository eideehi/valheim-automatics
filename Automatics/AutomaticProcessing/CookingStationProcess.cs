using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using ModUtils;

namespace Automatics.AutomaticProcessing
{
    internal static class CookingStationProcess
    {
        private static bool IsFireLit(CookingStation cookingStation)
        {
            return Reflections.InvokeMethod<bool>(cookingStation, "IsFireLit");
        }

        private static CookingStation.ItemConversion GetItemConversion(
            CookingStation cookingStation, string item)
        {
            return Reflections.InvokeMethod<CookingStation.ItemConversion>(cookingStation,
                "GetItemConversion", item);
        }

        public static void Craft(CookingStation cookingStation, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var stationName = cookingStation.m_name;
            if (!Logics.IsAllowProcessing(stationName, Process.Craft)) return;

            if (cookingStation.m_requireFire && !IsFireLit(cookingStation)) return;
            if (cookingStation.m_useFuel && zNetView.GetZDO().GetFloat("fuel") <= 0f) return;

            var productCounts = new Dictionary<string, int>();
            var freeSlotCount = 0;
            var slotSize = cookingStation.m_slots.Length;
            for (var slot = 0; slot < slotSize; slot++)
            {
                var item = zNetView.GetZDO().GetString("slot" + slot);
                if (string.IsNullOrEmpty(item))
                {
                    freeSlotCount++;
                    continue;
                }

                if (item == cookingStation.m_overCookedItem.name) continue;

                var conversion = GetItemConversion(cookingStation, item);
                if (conversion == null) continue;

                var productName = conversion.m_to.m_itemData.m_shared.m_name;
                productCounts[productName] =
                    productCounts.TryGetValue(productName, out var count) ? count + 1 : 1;
            }

            if (freeSlotCount == 0) return;
            if (Config.SupplyOnlyWhenMaterialsRunOut(stationName) &&
                freeSlotCount != slotSize) return;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(stationName);
            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(stationName);

            var origin = cookingStation.transform.position;
            var containersWithInventory = Logics
                .GetNearbyContainers(stationName, origin)
                .Select(x => (x.container, x.container.GetInventory())).ToList();

            foreach (var conversion in cookingStation.m_conversion)
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
                    zNetView.InvokeRPC("AddItem", item.m_dropPrefab.name);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position, stationName,
                        origin, productData.m_name);
                    break;
                }
            }
        }

        [UsedImplicitly]
        public static float Refuel(CookingStation cookingStation, ZNetView zNetView, float fuel)
        {
            if (!Config.EnableAutomaticProcessing) return fuel;
            if (fuel > cookingStation.m_maxFuel - 1f) return fuel;

            var stationName = cookingStation.m_name;
            if (!Logics.IsAllowProcessing(stationName, Process.Refuel)) return fuel;
            if (Config.RefuelOnlyWhenOutOfFuel(stationName) && fuel > 0f) return fuel;
            if (Config.RefuelOnlyWhenMaterialsSupplied(stationName) &&
                Reflections.InvokeMethod<int>(cookingStation, "GetFreeSlot") < 0)
                return fuel;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(stationName);
            var transform = cookingStation.transform;
            var origin = transform.position;
            var fuelName = cookingStation.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var (container, _) in Logics.GetNearbyContainers(stationName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, minFuelCount + 1)) continue;

                inventory.RemoveItem(fuelName, 1);
                fuel += 1f;
                cookingStation.m_fuelAddedEffects.Create(origin, transform.rotation, transform);

                Logics.RefuelLog(fuelName, 1, stationName, origin, container.m_name,
                    container.transform.position);
                break;
            }

            return fuel;
        }

        [UsedImplicitly]
        public static bool Store(CookingStation cookingStation, ZNetView zNetView, int slot,
            CookingStation.ItemConversion conversion)
        {
            if (!Config.EnableAutomaticProcessing) return false;

            var stationName = cookingStation.m_name;
            if (!Logics.IsAllowProcessing(stationName, Process.Store)) return false;

            var item = conversion.m_to;
            var itemName = item.m_itemData.m_shared.m_name;

            var origin = cookingStation.transform.position;
            foreach (var (container, _) in Logics.GetNearbyContainers(stationName, origin))
            {
                var inventory = container.GetInventory();
                if (!inventory.AddItem(item.gameObject, 1)) continue;

                zNetView.GetZDO().Set("slot" + slot, "");
                zNetView.GetZDO().Set("slot" + slot, 0f);
                zNetView.GetZDO().Set("slotstatus" + slot, 0);
                zNetView.InvokeRPC(ZNetView.Everybody, "SetSlotVisual", slot, "");

                Logics.StoreLog(itemName, 1, container.m_name,
                    container.transform.position, stationName, origin);
                return true;
            }

            return false;
        }
    }
}