using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class AutomaticRefuel
    {
        private static string LogMessage(string fuelName, int count, Container container, string destName,
            Vector3 destPos)
        {
            return count == 1
                ? $"Refueled {L10N.Translate(fuelName)} in {L10N.Translate(destName)} {destPos} from {L10N.Translate(container.m_name)} {container.transform.position}"
                : $"Refueled {L10N.Translate(fuelName)} x{count} in {L10N.Translate(destName)} {destPos} from {L10N.Translate(container.m_name)} {container.transform.position}";
        }

        public static void Run(CookingStation piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (!piece.m_useFuel) return;

            var stationName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(stationName, Type.Refuel)) return;

            if (zNetView.GetZDO().GetFloat("fuel") > piece.m_maxFuel - 1f) return;

            var minFuelCount = Config.GetItemCountThatSuppressAutomaticRefuel(stationName);
            var origin = piece.transform.position;
            var fuelName = piece.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var container in Core.GetNearbyContainers(stationName, origin))
            {
                var fuelCount = container.GetInventory().CountItems(fuelName);
                if (fuelCount == 0) continue;
                if (minFuelCount > 0 && fuelCount <= minFuelCount) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");
                Log.Debug(() => LogMessage(fuelName, 1, container, stationName, origin));
                break;
            }
        }

        public static void Run(Fireplace fire, Piece piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fireplaceName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(fireplaceName, Type.Refuel)) return;

            if (Mathf.CeilToInt(zNetView.GetZDO().GetFloat("fuel")) >= fire.m_maxFuel) return;

            var minFuelCount = Config.GetItemCountThatSuppressAutomaticRefuel(fireplaceName);
            var origin = fire.transform.position;
            var fuelName = fire.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var container in Core.GetNearbyContainers(fireplaceName, origin))
            {
                var fuelCount = container.GetInventory().CountItems(fuelName);
                if (fuelCount == 0) continue;
                if (minFuelCount > 0 && fuelCount <= minFuelCount) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");
                Log.Debug(() => LogMessage(fuelName, 1, container, fireplaceName, origin));
                break;
            }
        }

        public static void Run(Smelter piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var smelterName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(smelterName, Type.Refuel)) return;

            if (zNetView.GetZDO().GetFloat("fuel") >= piece.m_maxFuel - 1) return;

            var minFuelCount = Config.GetItemCountThatSuppressAutomaticRefuel(smelterName);
            var origin = piece.transform.position;
            var fuelName = piece.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var container in Core.GetNearbyContainers(smelterName, origin))
            {
                var fuelCount = container.GetInventory().CountItems(fuelName);
                if (fuelCount == 0) continue;
                if (minFuelCount > 0 && fuelCount <= minFuelCount) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");
                Log.Debug(() => LogMessage(fuelName, 1, container, smelterName, origin));
                break;
            }
        }
    }
}