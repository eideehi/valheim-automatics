using System.Linq;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class SapCollectorProcess
    {
        public static bool Store(SapCollector sapCollector, ZNetView zNetView, int increaseCount)
        {
            if (!Module.Running) return false;
            if (!Objects.HasValidOwnership(zNetView)) return false;

            var sapCollectorName = sapCollector.m_name;
            if (!Logics.IsAllowProcessing(sapCollectorName, Process.Store)) return false;

            var sapUnits = zNetView.GetZDO().GetInt("level") + increaseCount;
            if (sapUnits <= 0) return false;

            var sapItem = sapCollector.m_spawnItem;
            var sapData = sapItem.m_itemData.m_shared;
            var sapName = sapData.m_name;
            // One unit yields what extracting it by hand yields (SapCollector.RPC_Extract), the
            // world's resource rate included.
            var sapPerUnit = Game.instance.ScaleDrops(sapItem.m_itemData, 1);

            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(sapCollectorName);
            var origin = sapCollector.transform.position;
            var unitsRemaining = sapUnits;
            var containers = Logics.GetNearbyContainers(sapCollectorName, origin)
                .Select(x => x.container).ToList();
            var inventories = containers.Select(Logics.ReadInventory).ToList();
            // The product stack limit covers every container in range together. It is read
            // again after the gate, which can reload the container about to be written. The
            // limit and the room are counted in items, and only whole units are stored: a unit
            // goes in with its whole yield or stays in the collector.
            int Capacity() => Logics.GetProductCapacity(inventories, sapName,
                sapData.m_maxStackSize, maxProductStacks);
            Container askable = null;
            foreach (var container in containers)
            {
                if (unitsRemaining <= 0) break;
                if (Capacity() < sapPerUnit) break;

                // A container without room for one whole unit is passed over before the gate,
                // as a full one is.
                if (ContainerAccess.GetRoom(container, sapItem.gameObject) < sapPerUnit) continue;
                if (!Logics.CanStore(sapCollectorName, container, sapItem.gameObject, sapName))
                    continue;
                if (!Logics.TryClaimContainer(container, ref askable)) continue;

                var units = Mathf.Min(unitsRemaining, Capacity() / sapPerUnit);
                if (units <= 0) break;
                var inventory = container.GetInventory();
                if (!Logics.AcceptsProduct(sapCollectorName, inventory, sapName)) continue;

                // One add stores at most one stack (Inventory.AddItem(GameObject, int)).
                var room = Mathf.Min(ContainerAccess.GetRoom(inventory, sapItem.gameObject),
                    sapData.m_maxStackSize);
                units = Mathf.Min(units, room / sapPerUnit);
                if (units <= 0) continue;

                var storedSapCount = Logics.AddItemAndGetCountDelta(inventory,
                    sapItem.gameObject, sapName, units * sapPerUnit);
                if (storedSapCount <= 0) continue;

                Logics.StoreLog(sapName, storedSapCount, container.m_name,
                    container.transform.position, sapCollectorName, origin);
                unitsRemaining -= storedSapCount / sapPerUnit;
            }

            if (unitsRemaining > 0 && Capacity() >= sapPerUnit) Logics.AskFor(askable);
            zNetView.GetZDO().Set("level", Mathf.Clamp(unitsRemaining, 0, sapCollector.m_maxLevel));
            return true;
        }
    }
}
