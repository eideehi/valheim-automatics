using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class FireplaceProcess
    {
        [UsedImplicitly]
        public static void Refuel(Fireplace fire, Piece piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fireplaceName = piece.m_name;
            if (!Logics.IsAllowProcessing(fireplaceName, Process.Refuel)) return;

            var fuel = zNetView.GetZDO().GetFloat("fuel");
            if (Mathf.CeilToInt(fuel) >= fire.m_maxFuel) return;
            if (Config.RefuelOnlyWhenOutOfFuel(fireplaceName) && fuel > 0f) return;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(fireplaceName);
            var origin = fire.transform.position;
            var fuelName = fire.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var (container, _) in Logics.GetNearbyContainers(fireplaceName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, minFuelCount + 1)) continue;

                inventory.RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");

                Logics.RefuelLog(fuelName, 1, fireplaceName, origin, container.m_name,
                    container.transform.position);
                break;
            }
        }
    }
}