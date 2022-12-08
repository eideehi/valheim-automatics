using BepInEx.Configuration;

namespace Automatics.AutomaticMining
{
    internal static class Config
    {
        private const string Section = "automatic_mining";

        private static ConfigEntry<bool> _enableAutomaticMining;
        private static ConfigEntry<bool> _needToEquipPickaxe;
        private static ConfigEntry<float> _miningInterval;
        private static ConfigEntry<KeyboardShortcut> _miningKey;
        private static ConfigEntry<int> _miningRange;
        private static ConfigEntry<bool> _allowMiningUndergroundMinerals;
        private static ConfigEntry<bool> _needToEquipWishboneForMiningUndergroundMinerals;

        public static bool EnableAutomaticMining => _enableAutomaticMining.Value;
        public static bool NeedToEquipPickaxe => _needToEquipPickaxe.Value;
        public static float MiningInterval => _miningInterval.Value;
        public static KeyboardShortcut MiningKey => _miningKey.Value;
        public static int MiningRange => _miningRange.Value;
        public static bool AllowMiningUndergroundMinerals => _allowMiningUndergroundMinerals.Value;

        public static bool NeedToEquipWishboneForMiningUndergroundMinerals =>
            _needToEquipWishboneForMiningUndergroundMinerals.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _enableAutomaticMining = config.Bind("enable_automatic_mining", true);
            _needToEquipPickaxe = config.Bind("need_to_equip_pickaxe", true);
            _miningInterval = config.Bind("mining_interval", 1.5f, (0.1f, 4f));
            _miningKey = config.Bind("mining_key", new KeyboardShortcut());
            _miningRange = config.Bind("mining_range", 3, (0, 32));
            _allowMiningUndergroundMinerals =
                config.Bind("allow_mining_underground_minerals", true);
            _needToEquipWishboneForMiningUndergroundMinerals =
                config.Bind("need_to_equip_wishbone_for_mining_underground_minerals", true);
        }
    }
}