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
            if (!Config.AutomaticProcessingEnabled) return true;

            var beehiveName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(beehiveName, Type.Store)) return true;

            var honeyCount = zNetView.GetZDO().GetInt("level") + increaseCount;
            if (honeyCount <= 0) return true;

            var honeyItem = piece.m_honeyItem;
            var honeyName = honeyItem.m_itemData.m_shared.m_name;

            var origin = piece.transform.position;
            var totalStoredCount = 0;
            foreach (var (container, honeyCountBefore) in
                     from x in Core.GetNearbyContainers(beehiveName, origin)
                     let count = x.Item1.GetInventory().CountItems(honeyName)
                     orderby count descending, x.Item2
                     select (x.Item1, count))
            {
                if (totalStoredCount >= honeyCount) break;

                var inventory = container.GetInventory();
                if (!inventory.AddItem(honeyItem.gameObject, honeyCount)) continue;

                var storedHoneyCount = inventory.CountItems(honeyName) - honeyCountBefore;
                Log.Debug(() => LogMessage(honeyName, storedHoneyCount, beehiveName, origin, container));
                totalStoredCount += storedHoneyCount;
            }

            zNetView.GetZDO().Set("level", Mathf.Clamp(honeyCount - totalStoredCount, 0, piece.m_maxHoney));
            return false;
        }

        public static void Run(CookingStation piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var stationName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(stationName, Type.Store)) return;

            var doneItems = new List<(int, string)>(piece.m_slots.Length);
            for (var i = 0; i < piece.m_slots.Length; i++)
            {
                var slotItem = zNetView.GetZDO().GetString("slot" + i);
                if (string.IsNullOrEmpty(slotItem)) continue;
                if (!Reflection.InvokeMethod<bool>(piece, "IsItemDone", slotItem)) continue;

                doneItems.Add((i, slotItem));
            }

            if (doneItems.Count == 0) return;

            var origin = piece.transform.position;
            var containersWithDistance = Core.GetNearbyContainers(stationName, origin).ToList();
            foreach (var (slot, itemId) in doneItems)
            {
                var conversion = piece.m_conversion.FirstOrDefault(x => x.m_to.gameObject.name == itemId);
                if (conversion == null) continue;

                var item = conversion.m_to;
                var itemName = item.m_itemData.m_shared.m_name;
                var container = (from x in containersWithDistance
                        orderby x.Item1.GetInventory().CountItems(itemName) descending, x.Item2
                        select x.Item1)
                    .FirstOrDefault(x => x.GetInventory().AddItem(item.gameObject, 1));
                if (container == null) continue;

                zNetView.GetZDO().Set("slot" + slot, "");
                zNetView.GetZDO().Set("slot" + slot, 0f);
                zNetView.GetZDO().Set("slotstatus" + slot, 0);
                Log.Debug(() => LogMessage(itemName, 1, stationName, origin, container));
            }
        }

        public static void Run(Fermenter piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fermenterName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(fermenterName, Type.Store)) return;

            if (Reflection.InvokeMethod<int>(piece, "GetStatus") != 3) return;

            var itemId = zNetView.GetZDO().GetString("Content");

            zNetView.GetZDO().Set("Content", "");
            zNetView.GetZDO().Set("StartTime", 0);
            piece.m_spawnEffects.Create(piece.m_outputPoint.transform.position, Quaternion.identity);

            var conversion = piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == itemId);
            if (conversion == null) return;

            var origin = piece.transform.position;
            var item = conversion.m_to;
            var itemName = item.m_itemData.m_shared.m_name;
            var totalStoredCount = 0;
            foreach (var (container, itemCountBefore) in
                     from x in Core.GetNearbyContainers(fermenterName, origin)
                     let count = x.Item1.GetInventory().CountItems(itemName)
                     orderby count descending, x.Item2
                     select (x.Item1, count))
            {
                if (totalStoredCount >= conversion.m_producedItems) break;

                var inventory = container.GetInventory();
                if (!inventory.AddItem(item.gameObject, conversion.m_producedItems)) continue;

                var storedItemCount = inventory.CountItems(itemName) - itemCountBefore;
                Log.Debug(() => LogMessage(itemName, storedItemCount, fermenterName, origin, container));
                totalStoredCount += storedItemCount;
            }
        }

        public static bool Run(Smelter piece, string ore, int stack)
        {
            if (!Config.AutomaticProcessingEnabled) return true;

            var smelterName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(smelterName, Type.Store)) return true;

            var conversion = piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == ore);
            if (conversion == null) return true;

            var origin = piece.transform.position;
            var item = conversion.m_to;
            var itemName = item.m_itemData.m_shared.m_name;
            var containers =
                from x in Core.GetNearbyContainers(smelterName, origin)
                let count = x.Item1.GetInventory().CountItems(itemName)
                orderby count descending, x.Item2
                select (x.Item1, count);

            var storedCount = 0;
            foreach (var (container, itemCountBefore) in containers)
            {
                if (storedCount >= stack) break;

                var inventory = container.GetInventory();
                if (!inventory.AddItem(item.gameObject, stack)) continue;

                var storedItemCount = inventory.CountItems(itemName) - itemCountBefore;
                Log.Debug(() => LogMessage(itemName, storedItemCount, smelterName, origin, container));
                storedCount += storedItemCount;
            }

            if (storedCount == 0) return true;

            piece.m_produceEffects.Create(origin, piece.transform.rotation);
            return false;
        }
    }
}