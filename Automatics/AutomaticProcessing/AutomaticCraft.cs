﻿using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class AutomaticCraft
    {
        private static string LogMessage(string fromItem, int count, string toItem,
            Container container,
            string destName, Vector3 destPos)
        {
            return count == 1
                ? $"{Automatics.L10N.Translate(fromItem)} was set from {Automatics.L10N.Translate(container.m_name)} {container.transform.position} to {Automatics.L10N.Translate(destName)} {destPos} for crafting {Automatics.L10N.Translate(toItem)}"
                : $"{Automatics.L10N.Translate(fromItem)} x{count} was set from {Automatics.L10N.Translate(container.m_name)} {container.transform.position} to {Automatics.L10N.Translate(destName)} {destPos} for crafting {Automatics.L10N.Translate(toItem)}";
        }

        public static void Run(CookingStation piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var stationName = piece.m_name;
            if (!Core.IsAllowProcessing(stationName, Type.Craft)) return;

            if (piece.m_requireFire && !Reflections.InvokeMethod<bool>(piece, "IsFireLit")) return;
            if (piece.m_useFuel && zNetView.GetZDO().GetFloat("fuel") <= 0f) return;

            var cookingProductCounts = new Dictionary<string, int>();
            var freeSlot = -1;
            for (var slot = 0; slot < piece.m_slots.Length; slot++)
            {
                var item = zNetView.GetZDO().GetString("slot" + slot);
                if (string.IsNullOrEmpty(item))
                {
                    freeSlot = slot;
                    continue;
                }

                var conversion =
                    Reflections.InvokeMethod<CookingStation.ItemConversion>(piece,
                        "GetItemConversion", item);
                if (conversion == null) continue;

                var productName = conversion.m_to.m_itemData.m_shared.m_name;
                cookingProductCounts[productName] =
                    cookingProductCounts.TryGetValue(productName, out var count) ? count + 1 : 1;
            }

            if (freeSlot == -1) return;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(stationName);
            var maxProductCount = Config.ProductCountOfSuppressProcessing(stationName);

            var origin = piece.transform.position;
            var containerWithInventoryList = Core.GetNearbyContainers(stationName, origin)
                .Select(x => (x, x.GetInventory())).ToList();

            foreach (var conversion in piece.m_conversion)
            {
                Container materialContainer = null;
                var hasProductContainer = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containerWithInventoryList)
                {
                    if (materialContainer != null && hasProductContainer) break;

                    if (materialContainer == null)
                    {
                        var material = inventory.GetItem(materialData.m_name);
                        if (material != null)
                            if (minMaterialCount == 0 || inventory.CountItems(materialData.m_name) >
                                minMaterialCount)
                                materialContainer = container;
                    }

                    if (!hasProductContainer)
                    {
                        var cookingCount =
                            cookingProductCounts.TryGetValue(productData.m_name, out var count)
                                ? count
                                : 0;
                        if (maxProductCount == 0 || inventory.CountItems(productData.m_name) <
                            maxProductCount - cookingCount)
                            hasProductContainer = inventory.CanAddItem(productItem, 1);
                    }
                }

                if (materialContainer != null && hasProductContainer)
                {
                    var item = materialContainer.GetInventory().GetItem(materialData.m_name);
                    materialContainer.GetInventory().RemoveOneItem(item);
                    zNetView.InvokeRPC("AddItem", item.m_dropPrefab.name);
                    Automatics.Logger.Debug(() => LogMessage(materialData.m_name, 1,
                        productData.m_name, materialContainer,
                        stationName, origin));
                    break;
                }
            }
        }

        public static void Run(CraftingStation piece, ZNetView zNetView)
        {
            // Not yet implemented.
            /*
            if (!Config.EnableAutomaticProcessing) return;
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Craft)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            */
        }

        public static void Run(Fermenter piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fermenterName = piece.m_name;
            if (!Core.IsAllowProcessing(fermenterName, Type.Craft)) return;

            if (Reflections.InvokeMethod<int>(piece, "GetStatus") != 0) return;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(fermenterName);
            var maxProductCount = Config.ProductCountOfSuppressProcessing(fermenterName);

            var origin = piece.transform.position;
            var containerWithInventoryList = Core.GetNearbyContainers(fermenterName, origin)
                .Select(x => (x, x.GetInventory())).ToList();

            foreach (var conversion in piece.m_conversion)
            {
                Container materialContainer = null;
                var hasProductContainer = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containerWithInventoryList)
                {
                    if (materialContainer != null && hasProductContainer) break;

                    if (materialContainer == null)
                    {
                        var material = inventory.GetItem(materialData.m_name);
                        if (material != null)
                            if (minMaterialCount == 0 || inventory.CountItems(materialData.m_name) >
                                minMaterialCount)
                                materialContainer = container;
                    }

                    if (!hasProductContainer)
                    {
                        var count = conversion.m_producedItems;
                        if (maxProductCount == 0 || inventory.CountItems(productData.m_name) <
                            maxProductCount - count)
                            hasProductContainer = inventory.CanAddItem(productItem, count);
                    }
                }

                if (materialContainer != null && hasProductContainer)
                {
                    var item = materialContainer.GetInventory().GetItem(materialData.m_name);
                    materialContainer.GetInventory().RemoveOneItem(item);
                    zNetView.InvokeRPC("AddItem", item.m_dropPrefab.name);
                    Automatics.Logger.Debug(() => LogMessage(materialData.m_name, 1,
                        productData.m_name, materialContainer,
                        fermenterName, origin));
                    break;
                }
            }
        }

        public static void Run(Smelter piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var smelterName = piece.m_name;
            if (!Core.IsAllowProcessing(smelterName, Type.Craft)) return;

            var oreCount = zNetView.GetZDO().GetInt("queued");
            if (oreCount >= piece.m_maxOre) return;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(smelterName);
            var maxProductCount = Config.ProductCountOfSuppressProcessing(smelterName);

            var smeltingProductCounts = new Dictionary<string, int>();
            var conversions = new List<Smelter.ItemConversion>();
            for (var i = 0; i < oreCount; i++)
            {
                var queuedOre = zNetView.GetZDO().GetString("item" + i);
                if (string.IsNullOrEmpty(queuedOre)) continue;

                var conversion =
                    piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == queuedOre);
                if (conversion == null) continue;

                conversions.Add(conversion);
                var productName = conversion.m_to.m_itemData.m_shared.m_name;
                smeltingProductCounts[productName] =
                    smeltingProductCounts.TryGetValue(productName, out var count) ? count + 1 : 1;
            }

            conversions.Reverse();
            foreach (var conversion in piece.m_conversion.Where(x => !conversions.Contains(x)))
                conversions.Add(conversion);

            var origin = piece.transform.position;
            var containerWithInventoryList = Core.GetNearbyContainers(smelterName, origin)
                .Select(x => (x, x.GetInventory())).ToList();

            foreach (var conversion in conversions)
            {
                Container materialContainer = null;
                var hasProductContainer = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containerWithInventoryList)
                {
                    if (materialContainer != null && hasProductContainer) break;

                    if (materialContainer == null)
                    {
                        var material = inventory.GetItem(materialData.m_name);
                        if (material != null)
                            if (minMaterialCount == 0 || inventory.CountItems(materialData.m_name) >
                                minMaterialCount)
                                materialContainer = container;
                    }

                    if (!hasProductContainer)
                    {
                        var smeltingCount =
                            smeltingProductCounts.TryGetValue(productData.m_name, out var count)
                                ? count
                                : 0;
                        if (maxProductCount == 0 || inventory.CountItems(productData.m_name) <
                            maxProductCount - smeltingCount)
                            hasProductContainer = inventory.CanAddItem(productItem, 1);
                    }
                }

                if (materialContainer != null && hasProductContainer)
                {
                    var item = materialContainer.GetInventory().GetItem(materialData.m_name);
                    materialContainer.GetInventory().RemoveOneItem(item);
                    zNetView.InvokeRPC("AddOre", item.m_dropPrefab.name);
                    Automatics.Logger.Debug(() => LogMessage(materialData.m_name, 1,
                        productData.m_name, materialContainer,
                        smelterName, origin));
                    break;
                }
            }
        }
    }
}