using BepInEx.Configuration;

namespace Automatics.AutomaticRepair
{
    internal static class Config
    {
        private const string Section = "automatic_repair";

        private static ConfigEntry<bool> _moduleDisable;
        private static ConfigEntry<bool> _enableAutomaticRepair;
        private static ConfigEntry<int> _craftingStationSearchRange;
        private static ConfigEntry<bool> _repairItemsWhenAccessingTheCraftingStation;
        private static ConfigEntry<Message> _itemRepairMessage;
        private static ConfigEntry<int> _pieceSearchRange;
        private static ConfigEntry<Message> _pieceRepairMessage;

        public static bool IsModuleDisabled => _moduleDisable.Value;
        public static bool EnableAutomaticRepair => _enableAutomaticRepair.Value;
        public static int CraftingStationSearchRange => _craftingStationSearchRange.Value;

        public static bool RepairItemsWhenAccessingTheCraftingStation =>
            _repairItemsWhenAccessingTheCraftingStation.Value;

        public static Message ItemRepairMessage => _itemRepairMessage.Value;
        public static int PieceSearchRange => _pieceSearchRange.Value;
        public static Message PieceRepairMessage => _pieceRepairMessage.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _moduleDisable = config.Bind("module_disable", false, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_description");
            });
            if (_moduleDisable.Value) return;

            _enableAutomaticRepair = config.Bind("enable_automatic_repair", true);
            _craftingStationSearchRange = config.Bind("crafting_station_search_range", 16, (0, 64));
            _repairItemsWhenAccessingTheCraftingStation = config.Bind("repair_items_when_accessing_the_crafting_station", false);
            _itemRepairMessage = config.Bind("item_repair_message", Message.None);
            _pieceSearchRange = config.Bind("piece_search_range", 16, (0, 64));
            _pieceRepairMessage = config.Bind("piece_repair_message", Message.None);
        }
    }
}