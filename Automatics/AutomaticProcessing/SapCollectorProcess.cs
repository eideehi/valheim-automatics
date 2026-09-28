using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class SapCollectorProcess
    {
        public static bool Store(SapCollector sapCollector, ZNetView zNetView, int increaseCount)
        {
            if (!Module.Running) return false;
            if (!Config.EnableAutomaticProcessing) return false;
            if (!Objects.HasValidOwnership(zNetView)) return false;

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
            var containers = Logics.GetNearbyContainers(sapCollectorName, origin)
                .Select(x => x.container).ToList();
            // The product stack limit covers every container in range together.
            var capacity = Logics.GetProductCapacity(containers.Select(x => x.GetInventory()),
                sapName, sapData.m_maxStackSize, maxProductStacks);
            foreach (var container in containers)
            {
                if (sapRemaining <= 0) break;

                var inventory = container.GetInventory();

                var amount = Mathf.Min(capacity, sapRemaining);
                if (amount <= 0) break;

                if (Config.StoreOnlyIfProductExists(sapCollectorName) &&
                    !Inventories.HaveItem(inventory, sapName, 0, WorldLevelMatchMode.Ignore, 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                var storedSapCount = Logics.AddItemAndGetCountDelta(inventory,
                    sapItem.gameObject, sapName, amount);
                if (storedSapCount <= 0) continue;
                capacity -= storedSapCount;

                Logics.StoreLog(sapName, storedSapCount, container.m_name,
                    container.transform.position, sapCollectorName, origin);
                sapRemaining -= storedSapCount;
            }

            zNetView.GetZDO().Set("level", Mathf.Clamp(sapRemaining, 0, sapCollector.m_maxLevel));
            return true;
        }
    }
}
