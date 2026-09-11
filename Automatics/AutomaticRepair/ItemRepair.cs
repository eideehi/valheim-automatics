using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticRepair
{
    internal static class ItemRepair
    {
        private static readonly List<ItemDrop.ItemData> WornItemsBuffer;

        static ItemRepair()
        {
            WornItemsBuffer = new List<ItemDrop.ItemData>();
        }

        internal static void Cleanup()
        {
            WornItemsBuffer.Clear();
        }

        private static void ShowRepairMessage(Player player, int repairCount)
        {
            if (repairCount == 0) return;

            Automatics.Logger.Debug(() => $"Repaired {repairCount} items");
            if (Config.ItemRepairMessage == Message.None) return;

            var type = Config.ItemRepairMessage.ToMessageType();
            player.Message(type,
                Automatics.L10N.Localize("@message_automatic_repair_repaired_the_items",
                    repairCount));
        }

        private static IEnumerable<CraftingStation> GetAllCraftingStations()
        {
            return Reflections.GetStaticField<CraftingStation, List<CraftingStation>>(
                       "m_allStations") ??
                   Enumerable.Empty<CraftingStation>();
        }

        private static int RepairAll(Player player, CraftingStation station)
        {
            if (!CanUseRepairStation(player, station)) return 0;

            WornItemsBuffer.Clear();
            player.GetInventory().GetWornItems(WornItemsBuffer);
            var repaired = 0;
            foreach (var item in WornItemsBuffer)
                if (RepairOne(player, station, item))
                    repaired++;
            return repaired;
        }

        private static bool CanUseRepairStation(Player player, CraftingStation station)
        {
            return player.NoCostCheat() && station == null ||
                   station != null && station.CheckUsable(player, false);
        }

        private static bool RepairOne(Player player, CraftingStation station,
            ItemDrop.ItemData item)
        {
            if (!CanRepair(player, station, item)) return false;

            item.m_durability = item.GetMaxDurability();

            Automatics.Logger.Debug(() =>
                $"Repair item: [{item.m_shared.m_name}({Automatics.L10N.Translate(item.m_shared.m_name)})]");
            return true;
        }

        private static bool CanRepair(Player player, CraftingStation station,
            ItemDrop.ItemData item)
        {
            if (!item.m_shared.m_canBeReparied) return false;
            if (player.NoCostCheat()) return true;
            if (station == null) return false;

            var recipe = ObjectDB.instance.GetRecipe(item);
            if (recipe == null) return false;
            if (recipe.m_craftingStation == null && recipe.m_repairStation == null) return false;

            var stationMatches =
                recipe.m_repairStation != null && recipe.m_repairStation.m_name == station.m_name ||
                recipe.m_craftingStation != null &&
                recipe.m_craftingStation.m_name == station.m_name;
            if (!stationMatches && item.m_worldLevel >= Game.m_worldLevel) return false;

            return Mathf.Min(station.GetLevel(), 4) >= recipe.m_minStationLevel;
        }

        public static void CraftingStationInteractHook(Player player, CraftingStation station)
        {
            if (!Module.Running) return;
            if (player == null || player != Player.m_localPlayer || !Objects.HasValidOwnership(player)) return;
            if (!Config.EnableAutomaticRepair) return;
            if (!Config.RepairItemsWhenAccessingTheCraftingStation) return;

            var count = RepairAll(player, station);
            if (count == 0) return;

            station.m_repairItemDoneEffects.Create(station.transform.position, Quaternion.identity);
            ShowRepairMessage(player, count);
        }

        public static void Repair(Player player)
        {
            if (!Module.Running) return;
            if (player == null || player != Player.m_localPlayer || !Objects.HasValidOwnership(player)) return;
            // No-cost-cheat repair needs no crafting station, so it must not be
            // gated by the station search range (which can be set to 0 to disable
            // only the station-based path).
            if (player.NoCostCheat())
            {
                ShowRepairMessage(player, RepairAll(player, null));
                return;
            }

            if (Config.CraftingStationSearchRange <= 0) return;

            var range = Config.CraftingStationSearchRange;
            var origin = player.transform.position;
            var count = 0;
            foreach (var station in GetAllCraftingStations())
            {
                // Unity-lifetime guard: m_allStations can hold a destroyed station.
                if (station == null) continue;
                if (Vector3.Distance(origin, station.transform.position) > range) continue;
                if (!station.CheckUsable(player, false)) continue;
                count += RepairAll(player, station);
            }

            ShowRepairMessage(player, count);
        }
    }
}
