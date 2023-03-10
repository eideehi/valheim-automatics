using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;

namespace Automatics.AutomaticMining
{
    internal static class Config
    {
        private const string Section = "automatic_mining";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<bool> _moduleDisable;
        private static ConfigEntry<bool> _enableAutomaticMining;
        private static ConfigEntry<float> _miningInterval;
        private static ConfigEntry<int> _miningRange;
        private static ConfigEntry<StringList> _allowMiningMineral;
        private static ConfigEntry<bool> _needToEquipPickaxe;
        private static ConfigEntry<bool> _allowMiningUndergroundMinerals;
        private static ConfigEntry<bool> _needToEquipWishboneForUndergroundMinerals;
        private static ConfigEntry<KeyboardShortcut> _miningKey;

        public static bool ModuleDisabled => _module.Value == AutomaticsModule.Disabled;
        public static bool IsModuleDisabled => _moduleDisable.Value;
        public static bool EnableAutomaticMining => _enableAutomaticMining.Value;
        public static float MiningInterval => _miningInterval.Value;
        public static int MiningRange => _miningRange.Value;
        public static StringList AllowMiningMinerals => _allowMiningMineral.Value;
        public static bool NeedToEquipPickaxe => _needToEquipPickaxe.Value;
        public static bool AllowMiningUndergroundMinerals => _allowMiningUndergroundMinerals.Value;
        public static bool NeedToEquipWishboneForUndergroundMinerals => _needToEquipWishboneForUndergroundMinerals.Value;
        public static KeyboardShortcut MiningKey => _miningKey.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _moduleDisable = config.Bind("module_disable", false, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_old_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_description");
            });
            _module = config.Bind("module", AutomaticsModule.Enabled, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_description");
            });
            if (_moduleDisable.Value) _module.Value = AutomaticsModule.Disabled;
            _moduleDisable.SettingChanged += (_, __) =>
            {
                _module.Value = _moduleDisable.Value
                    ? AutomaticsModule.Disabled
                    : AutomaticsModule.Enabled;
            };
            if (_moduleDisable.Value || _module.Value == AutomaticsModule.Disabled) return;

            _enableAutomaticMining = config.Bind("enable_automatic_mining", true);
            _miningInterval = config.Bind("mining_interval", 1.5f, (0.1f, 4f));
            _miningRange = config.Bind("mining_range", 3, (0, 32));
            _allowMiningMineral = config.BindValheimObjectList("allow_mining_mineral", ValheimObject.Mineral);
            _needToEquipPickaxe = config.Bind("need_to_equip_pickaxe", true);
            _allowMiningUndergroundMinerals = config.Bind("allow_mining_underground_minerals", true);
            _needToEquipWishboneForUndergroundMinerals = config.Bind("need_to_equip_wishbone_for_mining_underground_minerals", true);
            _miningKey = config.Bind("mining_key", new KeyboardShortcut());
        }
    }
}