using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticRepair
{
    internal static class ItemRepair
    {
        private static readonly List<ItemDrop.ItemData> WornItemsBuffer;

        // The recipe of each item of WornItemsBuffer, by index, looked up when a usable station
        // first needs it and kept for the rest of the pass.
        private static readonly List<Recipe> RecipeBuffer;
        private static bool _recipesResolved;

        static ItemRepair()
        {
            WornItemsBuffer = new List<ItemDrop.ItemData>();
            RecipeBuffer = new List<Recipe>();
        }

        internal static void Cleanup()
        {
            WornItemsBuffer.Clear();
            RecipeBuffer.Clear();
            _recipesResolved = false;
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

        // Collects the carried items that are worn and can be repaired at all.
        private static void CollectWornItems(Player player)
        {
            RecipeBuffer.Clear();
            _recipesResolved = false;
            WornItemsBuffer.Clear();
            player.GetInventory().GetWornItems(WornItemsBuffer);
            WornItemsBuffer.RemoveAll(item => !item.m_shared.m_canBeReparied);
        }

        private static Recipe GetRecipe(int index)
        {
            if (!_recipesResolved)
            {
                RecipeBuffer.Clear();
                foreach (var item in WornItemsBuffer)
                    RecipeBuffer.Add(ObjectDB.instance.GetRecipe(item));
                _recipesResolved = true;
            }

            return RecipeBuffer[index];
        }

        private static int RepairAll(Player player, CraftingStation station)
        {
            if (!CanUseRepairStation(player, station)) return 0;

            CollectWornItems(player);
            return RepairWornItems(player, station);
        }

        // Repairs the collected items that the station can repair. The station has already
        // been checked for usability.
        private static int RepairWornItems(Player player, CraftingStation station)
        {
            var repaired = 0;
            for (var i = 0; i < WornItemsBuffer.Count; i++)
                if (RepairOne(player, station, i))
                    repaired++;
            return repaired;
        }

        private static bool CanUseRepairStation(Player player, CraftingStation station)
        {
            return player.NoCostCheat() && station == null ||
                   station != null && station.CheckUsable(player, false);
        }

        private static bool RepairOne(Player player, CraftingStation station, int index)
        {
            var item = WornItemsBuffer[index];
            // An earlier station of the pass has already repaired it.
            if (item.m_durability >= item.GetMaxDurability()) return false;
            if (!CanRepair(player, station, item, index)) return false;

            player.RaiseSkill(Skills.SkillType.Crafting,
                1f - item.m_durability / item.GetMaxDurability());
            item.m_durability = item.GetMaxDurability();

            Automatics.Logger.Debug(() =>
                $"Repair item: [{item.m_shared.m_name}({Automatics.L10N.Translate(item.m_shared.m_name)})]");
            return true;
        }

        private static bool CanRepair(Player player, CraftingStation station,
            ItemDrop.ItemData item, int index)
        {
            if (player.NoCostCheat()) return true;
            if (station == null) return false;

            var recipe = GetRecipe(index);
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
            var noCost = player.NoCostCheat();
            if (!noCost && Config.CraftingStationSearchRange <= 0) return;

            // Nothing worn: no station, roof or fire probe is worth making.
            CollectWornItems(player);
            if (WornItemsBuffer.Count == 0) return;

            if (noCost)
            {
                ShowRepairMessage(player, RepairWornItems(player, null));
                return;
            }

            var range = Config.CraftingStationSearchRange;
            var origin = player.transform.position;
            var count = 0;
            foreach (var station in GetAllCraftingStations())
            {
                // Unity-lifetime guard: m_allStations can hold a destroyed station.
                if (station == null) continue;
                if (Vector3.Distance(origin, station.transform.position) > range) continue;
                if (!station.CheckUsable(player, false)) continue;
                count += RepairWornItems(player, station);
            }

            ShowRepairMessage(player, count);
        }
    }
}
