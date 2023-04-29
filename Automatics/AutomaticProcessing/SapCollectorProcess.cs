using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class SapCollectorProcess
    {
        public static bool Store(SapCollector sapCollector, ZNetView zNetView, int increaseCount)
        {
            if (!Config.EnableAutomaticProcessing) return false;

            var sapCollectorName = sapCollector.m_name;
            if (!Logics.IsAllowProcessing(sapCollectorName, Process.Store)) return false;

            var sapCount = zNetView.GetZDO().GetInt("level") + increaseCount;
            if (sapCount <= 0) return false;

            var sapItem = sapCollector.m_spawnItem;
            var sapData = sapItem.m_itemData.m_shared;
            var sapName = sapData.m_name;

            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(sapCollectorName);
            var origin = sapCollector.transform.position;
            var sapRemaining = sapCount;
            foreach (var (container, _) in Logics.GetNearbyContainers(sapCollectorName, origin))
            {
                if (sapRemaining <= 0) break;

                var inventory = container.GetInventory();

                var sapCountInContainer = inventory.CountItems(sapName);
                var amount = sapRemaining;
                var stacks = sapCountInContainer / sapData.m_maxStackSize;

                if (maxProductStacks > 0)
                {
                    if (stacks >= maxProductStacks) continue;
                    var freeStackSpace = inventory.FindFreeStackSpace(sapName);
                    if (freeStackSpace == 0) continue;
                    amount = Mathf.Min(amount, freeStackSpace);
                }

                if (Config.StoreOnlyIfProductExists(sapCollectorName) &&
                    !Inventories.HaveItem(inventory, sapName, 1)) continue;
                if (!inventory.AddItem(sapItem.gameObject, amount)) continue;

                var storedSapCount = inventory.CountItems(sapName) - sapCountInContainer;
                Logics.StoreLog(sapName, storedSapCount, container.m_name,
                    container.transform.position, sapCollectorName, origin);
                sapRemaining -= storedSapCount;
            }

            zNetView.GetZDO().Set("level", Mathf.Clamp(sapRemaining, 0, sapCollector.m_maxLevel));
            return true;
        }
    }
}