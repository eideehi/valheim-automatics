using ModUtils;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticRepair
{
    using MessageType = MessageHud.MessageType;

    internal static class RepairItems
    {
        private static readonly List<ItemDrop.ItemData> WornItemsBuffer;

        private static float _lastRunningTime;

        static RepairItems()
        {
            WornItemsBuffer = new List<ItemDrop.ItemData>();
        }

        private static bool Runnable()
        {
            return !Game.IsPaused() && Time.time - _lastRunningTime >= 1f;
        }

        private static void ShowRepairMessage(Player player, int repairCount)
        {
            if (repairCount == 0) return;

            Automatics.Logger.Debug(() => $"Repaired {repairCount} items");
            if (Config.ItemRepairMessage == Message.None) return;

            var type = Config.ItemRepairMessage == Message.Center ? MessageType.Center : MessageType.TopLeft;
            player.Message(type, Automatics.L10N.Localize("@message_automatic_repair_repaired_the_items", repairCount));
        }

        private static IEnumerable<CraftingStation> GetAllCraftingStations()
        {
            return Reflections.GetStaticField<CraftingStation, List<CraftingStation>>("m_allStations") ??
                   Enumerable.Empty<CraftingStation>();
        }

        private static int RepairAll(Player player, CraftingStation station)
        {
            WornItemsBuffer.Clear();
            player.GetInventory().GetWornItems(WornItemsBuffer);
            return WornItemsBuffer.Count(x => RepairOne(player, station, x));
        }

        private static bool RepairOne(Player player, CraftingStation station, ItemDrop.ItemData item)
        {
            if (!CanRepair(player, station, item)) return false;

            item.m_durability = item.GetMaxDurability();

            Automatics.Logger.Debug(() => $"Repair item: [{item.m_shared.m_name}({Automatics.L10N.Translate(item.m_shared.m_name)})]");
            return true;
        }

        private static bool CanRepair(Player player, CraftingStation station, ItemDrop.ItemData item)
        {
            if (!item.m_shared.m_canBeReparied) return false;
            if (player.NoCostCheat()) return true;

            var recipe = ObjectDB.instance.GetRecipe(item);
            return recipe != null
                   && (recipe.m_craftingStation != null || recipe.m_repairStation != null)
                   && ((recipe.m_craftingStation != null && recipe.m_craftingStation.m_name == station.m_name) ||
                       (recipe.m_repairStation != null && recipe.m_repairStation.m_name == station.m_name))
                   && station.GetLevel() >= recipe.m_minStationLevel;
        }

        public static void CraftingStationInteractHook(Player player, CraftingStation station)
        {
            if (!Config.EnableAutomaticRepair) return;
            if (!Config.RepairItemsWhenAccessingTheCraftingStation) return;

            var count = RepairAll(player, station);
            if (count == 0) return;

            station.m_repairItemDoneEffects.Create(station.transform.position, Quaternion.identity);
            ShowRepairMessage(player, count);
        }

        public static void Run(Player player, bool takeInput)
        {
            if (!Config.EnableAutomaticRepair) return;
            if (Config.CraftingStationSearchRange <= 0) return;
            if (!Runnable()) return;

            var range = Config.CraftingStationSearchRange;
            var origin = player.transform.position;
            var count = (from x in GetAllCraftingStations()
                where x.CheckUsable(player, false)
                let distance = Vector3.Distance(origin, x.transform.position)
                where distance <= range
                select RepairAll(player, x)).Sum();

            ShowRepairMessage(player, count);

            _lastRunningTime = Time.time;
        }
    }
}