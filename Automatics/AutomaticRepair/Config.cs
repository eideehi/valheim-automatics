using Automatics.ModUtils;
using BepInEx.Configuration;

namespace Automatics.AutomaticRepair
{
    internal static class Config
    {
        private const string Section = "automatic_repair";

        private static ConfigEntry<bool> _enableAutomaticRepair;
        private static ConfigEntry<int> _craftingStationSearchRange;
        private static ConfigEntry<bool> _repairItemsWhenAccessingTheCraftingStation;
        private static ConfigEntry<Message> _itemRepairMessage;
        private static ConfigEntry<int> _pieceSearchRange;
        private static ConfigEntry<Message> _pieceRepairMessage;

        public static bool EnableAutomaticRepair => _enableAutomaticRepair.Value;
        public static int CraftingStationSearchRange => _craftingStationSearchRange.Value;
        public static bool RepairItemsWhenAccessingTheCraftingStation => _repairItemsWhenAccessingTheCraftingStation.Value;
        public static Message ItemRepairMessage => _itemRepairMessage.Value;
        public static int PieceSearchRange => _pieceSearchRange.Value;
        public static Message PieceRepairMessage => _pieceRepairMessage.Value;

        public static void Initialize()
        {
            Configuration.ChangeSection(Section);
            _enableAutomaticRepair = Configuration.Bind("enable_automatic_repair", true);
            _craftingStationSearchRange = Configuration.Bind("crafting_station_search_range", 16, (0, 64));
            _repairItemsWhenAccessingTheCraftingStation = Configuration.Bind("repair_items_when_accessing_the_crafting_station", false);
            _itemRepairMessage = Configuration.Bind("item_repair_message", Message.None);
            _pieceSearchRange = Configuration.Bind("piece_search_range", 16, (0, 64));
            _pieceRepairMessage = Configuration.Bind("piece_repair_message", Message.None);
        }
    }
}