using System.Linq;
using Automatics.Valheim;
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

            // The hive keeps no more units than Beehive.IncreseLevel would; units above its
            // maximum are lost, as they are when nothing is stored.
            var honeyUnits = Mathf.Min(zNetView.GetZDO().GetInt("level") + increaseCount,
                beehive.m_maxHoney);
            if (honeyUnits <= 0) return false;

            var honeyItem = beehive.m_honeyItem;
            var honeyData = honeyItem.m_itemData.m_shared;
            var honeyName = honeyData.m_name;
            // One unit yields what extracting it by hand yields (Beehive.RPC_Extract), the
            // world's resource rate included.
            var honeyPerUnit = Game.instance.ScaleDrops(honeyItem.m_itemData, 1);

            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(beehiveName);
            var origin = beehive.transform.position;
            var unitsRemaining = honeyUnits;
            var containers = Logics.GetNearbyContainers(beehiveName, origin)
                .Select(x => x.container).ToList();
            var inventories = containers.Select(Logics.ReadInventory).ToList();
            // The product stack limit covers every container in range together. It is read
            // again after the gate, which can reload the container about to be written. The
            // limit and the room are counted in items, and only whole units are stored: a unit
            // goes in with its whole yield or stays in the hive.
            int Capacity() => Logics.GetProductCapacity(inventories, honeyName,
                honeyData.m_maxStackSize, maxProductStacks);
            Container askable = null;
            foreach (var container in containers)
            {
                if (unitsRemaining <= 0) break;
                if (Capacity() < honeyPerUnit) break;

                // A container without room for one whole unit is passed over before the gate,
                // as a full one is.
                if (ContainerAccess.GetRoom(container, honeyItem.gameObject) < honeyPerUnit)
                    continue;
                if (!Logics.CanStore(beehiveName, container, honeyItem.gameObject, honeyName))
                    continue;
                if (!Logics.TryClaimContainer(container, ref askable)) continue;

                var units = Mathf.Min(unitsRemaining, Capacity() / honeyPerUnit);
                if (units <= 0) break;
                var inventory = container.GetInventory();
                if (!Logics.AcceptsProduct(beehiveName, inventory, honeyName)) continue;

                // One add stores at most one stack (Inventory.AddItem(GameObject, int)).
                var room = Mathf.Min(ContainerAccess.GetRoom(inventory, honeyItem.gameObject),
                    honeyData.m_maxStackSize);
                units = Mathf.Min(units, room / honeyPerUnit);
                if (units <= 0) continue;

                var storedHoneyCount = Logics.AddItemAndGetCountDelta(inventory,
                    honeyItem.gameObject, honeyName, units * honeyPerUnit);
                if (storedHoneyCount <= 0) continue;

                Logics.StoreLog(honeyName, storedHoneyCount, container.m_name,
                    container.transform.position, beehiveName, origin);
                unitsRemaining -= storedHoneyCount / honeyPerUnit;
            }

            if (unitsRemaining > 0 && Capacity() >= honeyPerUnit) Logics.AskFor(askable);
            zNetView.GetZDO().Set("level", Mathf.Clamp(unitsRemaining, 0, beehive.m_maxHoney));
            return true;
        }
    }
}
