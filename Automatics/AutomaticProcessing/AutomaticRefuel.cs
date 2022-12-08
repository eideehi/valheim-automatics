using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class AutomaticRefuel
    {
        private static string LogMessage(string fuelName, int count, Container container,
            string destName,
            Vector3 destPos)
        {
            return count == 1
                ? $"Refueled {Automatics.L10N.Translate(fuelName)} in {Automatics.L10N.Translate(destName)} {destPos} from {Automatics.L10N.Translate(container.m_name)} {container.transform.position}"
                : $"Refueled {Automatics.L10N.Translate(fuelName)} x{count} in {Automatics.L10N.Translate(destName)} {destPos} from {Automatics.L10N.Translate(container.m_name)} {container.transform.position}";
        }

        public static void Run(CookingStation piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            if (!piece.m_useFuel) return;

            var stationName = piece.m_name;
            if (!Core.IsAllowProcessing(stationName, Type.Refuel)) return;

            if (zNetView.GetZDO().GetFloat("fuel") > piece.m_maxFuel - 1f) return;

            if (Config.RefuelOnlyWhenMaterialsSupplied(stationName))
                if (Reflections.InvokeMethod<int>(piece, "GetFreeSlot") < 0)
                    return;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(stationName);
            var origin = piece.transform.position;
            var fuelName = piece.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var container in Core.GetNearbyContainers(stationName, origin))
            {
                var fuelCount = container.GetInventory().CountItems(fuelName);
                if (fuelCount == 0) continue;
                if (minFuelCount > 0 && fuelCount <= minFuelCount) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");
                Automatics.Logger.Debug(() =>
                    LogMessage(fuelName, 1, container, stationName, origin));
                break;
            }
        }

        public static void Run(Fireplace fire, Piece piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fireplaceName = piece.m_name;
            if (!Core.IsAllowProcessing(fireplaceName, Type.Refuel)) return;

            if (Mathf.CeilToInt(zNetView.GetZDO().GetFloat("fuel")) >= fire.m_maxFuel) return;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(fireplaceName);
            var origin = fire.transform.position;
            var fuelName = fire.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var container in Core.GetNearbyContainers(fireplaceName, origin))
            {
                var fuelCount = container.GetInventory().CountItems(fuelName);
                if (fuelCount == 0) continue;
                if (minFuelCount > 0 && fuelCount <= minFuelCount) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");
                Automatics.Logger.Debug(() =>
                    LogMessage(fuelName, 1, container, fireplaceName, origin));
                break;
            }
        }

        public static void Run(Smelter piece, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var smelterName = piece.m_name;
            if (!Core.IsAllowProcessing(smelterName, Type.Refuel)) return;

            if (zNetView.GetZDO().GetFloat("fuel") >= piece.m_maxFuel - 1) return;

            if (Config.RefuelOnlyWhenMaterialsSupplied(smelterName))
                if (zNetView.GetZDO().GetInt("queued") == 0)
                    return;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(smelterName);
            var origin = piece.transform.position;
            var fuelName = piece.m_fuelItem.m_itemData.m_shared.m_name;

            foreach (var container in Core.GetNearbyContainers(smelterName, origin))
            {
                var fuelCount = container.GetInventory().CountItems(fuelName);
                if (fuelCount == 0) continue;
                if (minFuelCount > 0 && fuelCount <= minFuelCount) continue;

                container.GetInventory().RemoveItem(fuelName, 1);
                zNetView.InvokeRPC("AddFuel");
                Automatics.Logger.Debug(() =>
                    LogMessage(fuelName, 1, container, smelterName, origin));
                break;
            }
        }
    }
}