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
            if (!Module.Running) return;
            if (!Config.EnableAutomaticProcessing) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

            var stationName = cookingStation.m_name;
            if (!Logics.IsAllowProcessing(stationName, Process.Craft)) return;

            if (cookingStation.m_requireFire && !IsFireLit(cookingStation)) return;
            // A station without fuel takes materials only while Refuel can bring fuel to cook
            // them; the game itself accepts materials whatever the fuel is.
            if (cookingStation.m_useFuel && zNetView.GetZDO().GetFloat("fuel") <= 0f &&
                !CanRefuelFromContainers(cookingStation, stationName)) return;

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
            var inventories = containersWithInventory.Select(x => x.Item2).ToList();

            foreach (var conversion in cookingStation.m_conversion)
            {
                var materialContainers = new List<Container>();

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
                }

                // Products already queued or cooking count toward the limit too.
                productCounts.TryGetValue(productData.m_name, out var pending);
                var productContainerFound = maxProductStacks > 0 && materialContainers.Count > 0 &&
                    Logics.CanTakeMoreProduct(inventories,
                        productItem, pending, 1, maxProductStacks);

                if (materialContainers.Count > 0 &&
                    (maxProductStacks == 0 || productContainerFound))
                {
                    // Walks the candidates in distance order, skipping any that holds no
                    // eligible item without claiming it.
                    if (!Logics.TryTakeMaterial(materialContainers, materialData.m_name,
                            minMaterialCount, conversion.m_from.gameObject.name,
                            out var materialContainer, out var prefabName, out var cheated))
                        continue;
                    // CookingStation registers Register<string, bool>("RPC_AddItem", RPC_AddItem).
                    zNetView.InvokeRPC("RPC_AddItem", prefabName, cheated);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position, stationName,
                        origin, productData.m_name);
                    break;
                }
            }
        }

        private static bool CanRefuelFromContainers(CookingStation cookingStation, string stationName)
        {
            if (!cookingStation.m_fuelItem) return false;
            if (!Logics.IsAllowProcessing(stationName, Process.Refuel)) return false;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(stationName);
            var fuelName = cookingStation.m_fuelItem.m_itemData.m_shared.m_name;
            return Logics.GetNearbyContainers(stationName, cookingStation.transform.position)
                .Any(x => Inventories.HaveItem(x.container.GetInventory(), fuelName, 0,
                    WorldLevelMatchMode.Ignore, minFuelCount + 1));
        }

        // Adds one fuel item from a nearby container, up to the amount the game itself accepts.
        // Called once per cooking update, whatever the station's fuel is, so a station that has
        // run out is refuelled too.
        public static void Refuel(CookingStation cookingStation, ZNetView zNetView)
        {
            if (!Module.Running) return;
            if (!Config.EnableAutomaticProcessing) return;
            if (!Objects.HasValidOwnership(zNetView)) return;
            if (!cookingStation.m_useFuel || !cookingStation.m_fuelItem) return;

            var fuel = zNetView.GetZDO().GetFloat("fuel");
            if (fuel > cookingStation.m_maxFuel - 1f) return;

            var stationName = cookingStation.m_name;
            if (!Logics.IsAllowProcessing(stationName, Process.Refuel)) return;
            if (Config.RefuelOnlyWhenOutOfFuel(stationName) && fuel > 0f) return;
            if (Config.RefuelOnlyWhenMaterialsSupplied(stationName) &&
                !Reflections.InvokeMethod<bool>(cookingStation, "HaveUncookedItem"))
                return;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(stationName);
            var transform = cookingStation.transform;
            var origin = transform.position;
            var fuelName = cookingStation.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var (container, _) in Logics.GetNearbyContainers(stationName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, 0, WorldLevelMatchMode.Ignore, minFuelCount + 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                if (!Logics.TryRemoveItem(inventory, fuelName, minFuelCount, out _)) continue;
                // CookingStation registers Register("RPC_AddFuel", RPC_AddFuel), which adds one
                // fuel and plays the fuel-added effect.
                zNetView.InvokeRPC("RPC_AddFuel");

                Logics.RefuelLog(fuelName, 1, stationName, origin, container.m_name,
                    container.transform.position);
                break;
            }
        }

        [UsedImplicitly]
        public static bool Store(CookingStation cookingStation, ZNetView zNetView, int slot,
            CookingStation.ItemConversion conversion)
        {
            if (!Module.Running) return false;
            if (!Config.EnableAutomaticProcessing) return false;
            if (!Objects.HasValidOwnership(zNetView)) return false;

            var stationName = cookingStation.m_name;
            if (!Logics.IsAllowProcessing(stationName, Process.Store)) return false;

            var item = conversion.m_to;
            var itemName = item.m_itemData.m_shared.m_name;

            var origin = cookingStation.transform.position;
            foreach (var (container, _) in Logics.GetNearbyContainers(stationName, origin))
            {
                var inventory = container.GetInventory();
                if (Config.StoreOnlyIfProductExists(stationName) &&
                    !Inventories.HaveItem(inventory, itemName, 0, WorldLevelMatchMode.Ignore, 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;
                var storedItemCount =
                    Logics.AddItemAndGetCountDelta(inventory, item.gameObject, itemName, 1);
                if (storedItemCount <= 0) continue;

                zNetView.GetZDO().Set("slot" + slot, "");
                zNetView.GetZDO().Set("slot" + slot, 0f);
                zNetView.GetZDO().Set("slotstatus" + slot, 0);
                zNetView.InvokeRPC(ZNetView.Everybody, "RPC_SetSlotVisual", slot, "");

                Logics.StoreLog(itemName, storedItemCount, container.m_name,
                    container.transform.position, stationName, origin);
                return true;
            }

            return false;
        }
    }
}
