using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class BeehiveProcess
    {
        public static bool Store(Beehive beehive, ZNetView zNetView, int increaseCount)
        {
            if (!Module.Running) return false;
            if (!Objects.HasValidOwnership(zNetView)) return false;

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
            var containers = Logics.GetNearbyContainers(beehiveName, origin)
                .Select(x => x.container).ToList();
            // The product stack limit covers every container in range together.
            var capacity = Logics.GetProductCapacity(containers.Select(x => x.GetInventory()),
                honeyName, honeyData.m_maxStackSize, maxProductStacks);
            foreach (var container in containers)
            {
                if (honeyRemaining <= 0) break;

                var inventory = container.GetInventory();

                var amount = Mathf.Min(capacity, honeyRemaining);
                if (amount <= 0) break;

                if (Config.StoreOnlyIfProductExists(beehiveName) &&
                    !Inventories.HaveItem(inventory, honeyName, 0, WorldLevelMatchMode.Ignore, 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                var storedHoneyCount = Logics.AddItemAndGetCountDelta(inventory,
                    honeyItem.gameObject, honeyName, amount);
                if (storedHoneyCount <= 0) continue;
                capacity -= storedHoneyCount;

                Logics.StoreLog(honeyName, storedHoneyCount, container.m_name,
                    container.transform.position, beehiveName, origin);
                honeyRemaining -= storedHoneyCount;
            }

            zNetView.GetZDO().Set("level", Mathf.Clamp(honeyRemaining, 0, beehive.m_maxHoney));
            return true;
        }
    }
}
