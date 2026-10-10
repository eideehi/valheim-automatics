using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class CookingStationProcess
    {
        private static bool IsFireLit(CookingStation cookingStation)
        {
            return Reflections.InvokeMethod<bool>(cookingStation, "IsFireLit");
        }

        // The player who built the station earns the skill and the bonus food for what
        // Automatics cooks at it, as the game gives them to whoever puts food in or takes it
        // out by hand. A player's skills exist only in their own game, so this is the local
        // player when they built the station, and nobody when another player did or the
        // station was not built by a player.
        private static Player GetLocalBuilder(CookingStation cookingStation)
        {
            var player = Player.m_localPlayer;
            if (!player) return null;

            var playerId = player.GetPlayerID();
            if (playerId == 0L) return null;

            var piece = cookingStation.GetComponentInParent<Piece>();
            return piece && piece.GetCreator() == playerId ? player : null;
        }

        private static void RaiseSkill(CookingStation cookingStation, Player builder, float value)
        {
            if (builder && cookingStation.m_skill != Skills.SkillType.None)
                builder.RaiseSkill(cookingStation.m_skill, value);
        }

        // The bonus roll of CookingStation.OnInteract, with the builder's skill.
        private static int RollBonusYield(CookingStation cookingStation, Player builder)
        {
            if (!builder || !cookingStation.m_canGiveBonusYield || !InventoryGui.instance) return 0;

            var skillFactor = builder.GetSkillFactor(cookingStation.m_skill);
            return Random.value < skillFactor * InventoryGui.instance.m_craftBonusChance
                ? InventoryGui.instance.m_craftBonusAmount
                : 0;
        }

        private static void CreateBonusYieldEffects(CookingStation cookingStation, int bonus)
        {
            var position = cookingStation.transform.position;
            if (DamageText.instance)
                DamageText.instance.ShowText(DamageText.TextType.Bonus, position + Vector3.up,
                    $"+{bonus}", player: true);
            if (InventoryGui.instance)
                InventoryGui.instance.m_craftBonusEffect.Create(position, Quaternion.identity);
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
                    // The game raises the skill by 0.4 when food is put in by hand.
                    RaiseSkill(cookingStation, GetLocalBuilder(cookingStation), 0.4f);

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

                // As when cooked food is taken out by hand, the skill rises by 0.6 before the
                // bonus is rolled. The bonus goes into the same container, and what does not
                // fit there is not given.
                var builder = GetLocalBuilder(cookingStation);
                RaiseSkill(cookingStation, builder, 0.6f);
                var bonus = RollBonusYield(cookingStation, builder);
                if (bonus > 0)
                {
                    var storedBonusCount =
                        Logics.AddItemAndGetCountDelta(inventory, item.gameObject, itemName, bonus);
                    if (storedBonusCount > 0)
                    {
                        CreateBonusYieldEffects(cookingStation, storedBonusCount);
                        storedItemCount += storedBonusCount;
                    }
                }

                Logics.StoreLog(itemName, storedItemCount, container.m_name,
                    container.transform.position, stationName, origin);
                return true;
            }

            return false;
        }
    }
}
