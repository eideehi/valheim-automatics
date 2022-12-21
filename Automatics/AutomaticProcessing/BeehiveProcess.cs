using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class BeehiveProcess
    {
        public static bool Store(Beehive beehive, ZNetView zNetView, int increaseCount)
        {
            if (!Config.EnableAutomaticProcessing) return false;

            var beehiveName = beehive.m_name;
            if (!Logics.IsAllowProcessing(beehiveName, Process.Store)) return false;

            var honeyCount = zNetView.GetZDO().GetInt("level") + increaseCount;
            if (honeyCount <= 0) return false;

            var honeyItem = beehive.m_honeyItem;
            var honeyData = honeyItem.m_itemData.m_shared;
            var honeyName = honeyData.m_name;

            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(beehiveName);
            var origin = beehive.transform.position;
            var honeyRemaining = honeyCount;
            foreach (var (container, _) in Logics.GetNearbyContainers(beehiveName, origin))
            {
                if (honeyRemaining <= 0) break;

                var inventory = container.GetInventory();

                var honeyCountInContainer = inventory.CountItems(honeyName);
                var amount = honeyRemaining;
                var stacks = honeyCountInContainer / honeyData.m_maxStackSize;

                if (maxProductStacks > 0)
                {
                    if (stacks >= maxProductStacks) continue;
                    var freeStackSpace = inventory.FindFreeStackSpace(honeyName);
                    if (freeStackSpace == 0) continue;
                    amount = Mathf.Min(amount, freeStackSpace);
                }

                if (!inventory.AddItem(honeyItem.gameObject, amount)) continue;

                var storedHoneyCount = inventory.CountItems(honeyName) - honeyCountInContainer;
                Logics.StoreLog(honeyName, storedHoneyCount, container.m_name,
                    container.transform.position, beehiveName, origin);
                honeyRemaining -= storedHoneyCount;
            }

            zNetView.GetZDO().Set("level", Mathf.Clamp(honeyRemaining, 0, beehive.m_maxHoney));
            return true;
        }
    }
}