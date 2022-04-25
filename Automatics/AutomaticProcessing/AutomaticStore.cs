using System.Collections.Generic;
using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class AutomaticStore
    {
        private static string LogMessage(string itemName, int count, string srcName, Vector3 srcPos, Container dest)
        {
            return count == 1
                ? $"Stored {L10N.Translate(itemName)} in {L10N.Translate(dest.m_name)} {dest.transform.position} from {L10N.Translate(srcName)} {srcPos}"
                : $"Stored {L10N.Translate(itemName)} x{count} in {L10N.Translate(dest.m_name)} {dest.transform.position} from {L10N.Translate(srcName)} {srcPos}";
        }

        public static bool Run(Beehive piece, ZNetView zNetView, int increaseCount)
        {
            if (!Config.EnableAutomaticProcessing) return true;

            var beehiveName = piece.m_name;
            if (!Core.IsAllowProcessing(beehiveName, Type.Store)) return true;

            var honeyCount = zNetView.GetZDO().GetInt("level") + increaseCount;
            if (honeyCount <= 0) return true;

            var honeyItem = piece.m_honeyItem;
            var honeyData = honeyItem.m_itemData.m_shared;
            var honeyName = honeyData.m_name;

            var maxProductCount = Config.ProductCountOfSuppressProcessing(beehiveName);
            var origin = piece.transform.position;
            var honeyRemaining = honeyCount;
            foreach (var (container, honeyCountBefore) in
                     from x in Core.GetNearbyContainers(beehiveName, origin)
                     let count = x.GetInventory().CountItems(honeyName)
                     let distance = Vector3.Distance(origin, x.transform.position)
                     orderby count descending, distance
                     select (x, count))
            {
                if (honeyRemaining <= 0) break;

                var inventory = container.GetInventory();

                if (maxProductCount > 0 && inventory.CountItems(honeyName) > maxProductCount - honeyRemaining) continue;
                if (!inventory.AddItem(honeyItem.gameObject, honeyRemaining)) continue;

                var storedHoneyCount = inventory.CountItems(honeyName) - honeyCountBefore;
                Log.Debug(() => LogMessage(honeyName, storedHoneyCount, beehiveName, origin, container));
                honeyRemaining -= storedHoneyCount;
            }

            zNetView.GetZDO().Set("level", Mathf.Clamp(honeyRemaining, 0, piece.m_maxHoney));
            return false;
        }

        public static void Run(CookingStation piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var stationName = piece.m_name;
            if (!Core.IsAllowProcessing(stationName, Type.Store)) return;

            var doneItems = new List<(int, string)>(piece.m_slots.Length);
            for (var i = 0; i < piece.m_slots.Length; i++)
            {
                var slotItem = zNetView.GetZDO().GetString("slot" + i);
                if (string.IsNullOrEmpty(slotItem)) continue;
                if (!Reflection.InvokeMethod<bool>(piece, "IsItemDone", slotItem)) continue;

                doneItems.Add((i, slotItem));
            }

            if (doneItems.Count == 0) return;

            var maxProductCount = Config.ProductCountOfSuppressProcessing(stationName);
            var origin = piece.transform.position;
            var containerWithDistanceList = Core.GetNearbyContainers(stationName, origin)
                .Select(x => (x, Vector3.Distance(origin, x.transform.position))).ToList();

            foreach (var (slot, itemId) in doneItems)
            {
                var conversion = piece.m_conversion.FirstOrDefault(x => x.m_to.gameObject.name == itemId);
                if (conversion == null) continue;

                var item = conversion.m_to;
                var itemData = item.m_itemData.m_shared;
                var itemName = itemData.m_name;

                var container = (from x in containerWithDistanceList
                        let count = x.Item1.GetInventory().CountItems(itemName)
                        where maxProductCount == 0 || count < maxProductCount
                        orderby count descending, x.Item2
                        select x.Item1)
                    .FirstOrDefault(x => x.GetInventory().AddItem(item.gameObject, 1));
                if (container == null) continue;

                zNetView.GetZDO().Set("slot" + slot, "");
                zNetView.GetZDO().Set("slot" + slot, 0f);
                zNetView.GetZDO().Set("slotstatus" + slot, 0);
                zNetView.InvokeRPC(ZNetView.Everybody, "SetSlotVisual", slot, "");
                Log.Debug(() => LogMessage(itemName, 1, stationName, origin, container));
            }
        }

        public static void Run(Fermenter piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fermenterName = piece.m_name;
            if (!Core.IsAllowProcessing(fermenterName, Type.Store)) return;

            if (Reflection.InvokeMethod<int>(piece, "GetStatus") != 3) return;

            var itemId = zNetView.GetZDO().GetString("Content");
            var conversion = piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == itemId);
            if (conversion == null) return;

            var maxProductCount = Config.ProductCountOfSuppressProcessing(fermenterName);
            var origin = piece.transform.position;
            var item = conversion.m_to;
            var itemData = item.m_itemData.m_shared;
            var itemName = itemData.m_name;

            var productRemaining = conversion.m_producedItems;
            foreach (var (container, itemCountBefore) in
                     from x in Core.GetNearbyContainers(fermenterName, origin)
                     let distance = Vector3.Distance(origin, x.transform.position)
                     let count = x.GetInventory().CountItems(itemName)
                     orderby count descending, distance
                     select (x, count))
            {
                if (productRemaining <= 0) break;

                var inventory = container.GetInventory();

                if (maxProductCount > 0 && itemCountBefore > maxProductCount - productRemaining) continue;
                if (!inventory.AddItem(item.gameObject, productRemaining)) continue;

                var storedItemCount = inventory.CountItems(itemName) - itemCountBefore;
                Log.Debug(() => LogMessage(itemName, storedItemCount, fermenterName, origin, container));
                productRemaining -= storedItemCount;
            }

            if (productRemaining == conversion.m_producedItems) return;

            zNetView.GetZDO().Set("Content", "");
            zNetView.GetZDO().Set("StartTime", 0);
            piece.m_spawnEffects.Create(piece.m_outputPoint.transform.position, Quaternion.identity);
        }

        public static bool Run(Smelter piece, string ore, int stack)
        {
            if (!Config.EnableAutomaticProcessing) return true;

            var smelterName = piece.m_name;
            if (!Core.IsAllowProcessing(smelterName, Type.Store)) return true;

            var conversion = piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == ore);
            if (conversion == null) return true;

            var maxProductCount = Config.ProductCountOfSuppressProcessing(smelterName);
            var origin = piece.transform.position;
            var item = conversion.m_to;
            var itemData = item.m_itemData.m_shared;
            var itemName = itemData.m_name;
            var containers =
                from x in Core.GetNearbyContainers(smelterName, origin)
                let distance = Vector3.Distance(origin, x.transform.position)
                let count = x.GetInventory().CountItems(itemName)
                orderby count descending, distance
                select (x, count);

            var productRemaining = stack;
            foreach (var (container, itemCountBefore) in containers)
            {
                if (productRemaining <= 0) break;

                var inventory = container.GetInventory();

                if (maxProductCount > 0 && itemCountBefore > maxProductCount - productRemaining) continue;
                if (!inventory.AddItem(item.gameObject, stack)) continue;

                var storedItemCount = inventory.CountItems(itemName) - itemCountBefore;
                Log.Debug(() => LogMessage(itemName, storedItemCount, smelterName, origin, container));
                productRemaining -= storedItemCount;
            }

            if (productRemaining == stack) return true;

            while (productRemaining > 0)
            {
                Object.Instantiate(conversion.m_to.gameObject, piece.m_outputPoint.position,
                    piece.m_outputPoint.rotation).GetComponent<ItemDrop>().m_itemData.m_stack = stack;
                productRemaining--;
            }

            piece.m_produceEffects.Create(origin, piece.transform.rotation);
            return false;
        }
    }
}