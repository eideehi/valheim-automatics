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
            var inventories = containers.Select(Logics.ReadInventory).ToList();
            // The product stack limit covers every container in range together. It is read
            // again after the gate, which can reload the container about to be written.
            int Capacity() => Logics.GetProductCapacity(inventories, wispName,
                wispData.m_maxStackSize, maxProductStacks);
            foreach (var container in containers)
            {
                if (Capacity() <= 0) break;

                if (!Logics.CanStore(wispSpawnerName, container, wispItem.gameObject, wispName))
                    continue;
                if (!Logics.TryClaimContainer(container)) continue;

                if (Capacity() <= 0) break;
                var inventory = container.GetInventory();
                if (!Logics.AcceptsProduct(wispSpawnerName, inventory, wispName)) continue;
                var storedItemCount =
                    Logics.AddItemAndGetCountDelta(inventory, wispItem.gameObject, wispName, 1);
                if (storedItemCount <= 0) continue;

                zNetView.GetZDO().Set("LastSpawn", ZNet.instance.GetTime().Ticks);
                Logics.StoreLog(wispName, storedItemCount, container.m_name,
                    container.transform.position, wispSpawnerName, origin);
                return true;
            }

            return false;
        }
    }
}
