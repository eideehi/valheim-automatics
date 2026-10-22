using System.Linq;
using Automatics.Valheim;
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
            // One wisp yields what picking it by hand yields (Pickable.RPC_Pick, which has no
            // skill bonus without a player), the world's resource rate included.
            var wispYield = pickable.m_dontScale
                ? pickable.m_amount
                : Mathf.Max(pickable.m_minAmountScaled,
                    Game.instance.ScaleDrops(pickable.m_itemPrefab, pickable.m_amount));

            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(wispSpawnerName);
            var origin = wispSpawner.transform.position;
            var containers = Logics.GetNearbyContainers(wispSpawnerName, origin)
                .Select(x => x.container).ToList();
            var inventories = containers.Select(Logics.ReadInventory).ToList();
            // The product stack limit covers every container in range together. It is read
            // again after the gate, which can reload the container about to be written. The
            // limit and the room are counted in items, and the whole yield goes into one
            // container, or the wisp is lured as usual.
            int Capacity() => Logics.GetProductCapacity(inventories, wispName,
                wispData.m_maxStackSize, maxProductStacks);
            Container askable = null;
            foreach (var container in containers)
            {
                if (Capacity() < wispYield) break;

                // A container without room for the whole yield is passed over before the gate,
                // as a full one is.
                if (ContainerAccess.GetRoom(container, wispItem.gameObject) < wispYield) continue;
                if (!Logics.CanStore(wispSpawnerName, container, wispItem.gameObject, wispName))
                    continue;
                if (!Logics.TryClaimContainer(container, ref askable)) continue;

                if (Capacity() < wispYield) break;
                var inventory = container.GetInventory();
                if (!Logics.AcceptsProduct(wispSpawnerName, inventory, wispName)) continue;
                // One add stores at most one stack (Inventory.AddItem(GameObject, int)).
                if (Mathf.Min(ContainerAccess.GetRoom(inventory, wispItem.gameObject),
                        wispData.m_maxStackSize) < wispYield)
                    continue;
                var storedItemCount = Logics.AddItemAndGetCountDelta(inventory,
                    wispItem.gameObject, wispName, wispYield);
                if (storedItemCount <= 0) continue;

                zNetView.GetZDO().Set("LastSpawn", ZNet.instance.GetTime().Ticks);
                Logics.StoreLog(wispName, storedItemCount, container.m_name,
                    container.transform.position, wispSpawnerName, origin);
                return true;
            }

            if (Capacity() >= wispYield) Logics.AskFor(askable);
            return false;
        }
    }
}
