using System.Linq;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class WispSpawnerProcess
    {
        [UsedImplicitly]
        public static bool Store(WispSpawner wispSpawner, ZNetView zNetView)
        {
            if (!Module.Running) return false;
            if (!Config.EnableAutomaticProcessing) return false;
            if (!Objects.HasValidOwnership(zNetView)) return false;

            var wispSpawnerName = wispSpawner.m_name;
            if (!Logics.IsAllowProcessing(wispSpawnerName, Process.Store)) return false;

            var wispPrefab = wispSpawner.m_wispPrefab;
            var pickable = wispPrefab.GetComponent<Pickable>();
            if (!pickable) return false;

            var wispItem = pickable.m_itemPrefab.GetComponent<ItemDrop>();
            if (!wispItem) return false;

            var wispData = wispItem.m_itemData.m_shared;
            var wispName = wispData.m_name;

            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(wispSpawnerName);
            var origin = wispSpawner.transform.position;
            var containers = Logics.GetNearbyContainers(wispSpawnerName, origin)
                .Select(x => x.container).ToList();
            // The product stack limit covers every container in range together.
            var capacity = Logics.GetProductCapacity(containers.Select(x => x.GetInventory()),
                wispName, wispData.m_maxStackSize, maxProductStacks);
            foreach (var container in containers)
            {
                var inventory = container.GetInventory();

                var amount = Mathf.Min(capacity, 1);
                if (amount <= 0) break;

                if (Config.StoreOnlyIfProductExists(wispSpawnerName) &&
                    !Inventories.HaveItem(inventory, wispName, 0, WorldLevelMatchMode.Ignore, 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;
                var storedItemCount =
                    Logics.AddItemAndGetCountDelta(inventory, wispItem.gameObject, wispName, 1);
                if (storedItemCount <= 0) continue;
                capacity -= storedItemCount;

                zNetView.GetZDO().Set("LastSpawn", ZNet.instance.GetTime().Ticks);
                Logics.StoreLog(wispName, storedItemCount, container.m_name,
                    container.transform.position, wispSpawnerName, origin);
                return true;
            }

            return false;
        }
    }
}
