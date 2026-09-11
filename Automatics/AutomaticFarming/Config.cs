using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;

namespace Automatics.AutomaticFarming
{
    internal static class Config
    {
        private const string Section = "automatic_farming";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<bool> _enableAutomaticFarming;
        private static ConfigEntry<float> _farmingInterval;
        private static ConfigEntry<int> _farmingRange;
        private static ConfigEntry<StringList> _allowFarmingCrop;
        private static ConfigEntry<int> _seedReserve;
        private static ConfigEntry<SeedSource> _seedSource;
        private static ConfigEntry<bool> _enableProactiveSowing;
        private static ConfigEntry<float> _sowingSpacingFactor;
        private static ConfigEntry<KeyboardShortcut> _farmingKey;
        private static ConfigEntry<KeyboardShortcut> _designateContainerKey;

        // Null-safe so the dynamic pickup-exclusion guard can query the module
        // state even before this module's initializer runs or when it is disabled
        // (the remaining entries are only bound while the module is enabled).
        public static bool ModuleDisabled =>
            _module == null || _module.Value == AutomaticsModule.Disabled;

        public static bool EnableAutomaticFarming => _enableAutomaticFarming.Value;
        public static float FarmingInterval => _farmingInterval.Value;
        public static int FarmingRange => _farmingRange.Value;
        public static StringList AllowFarmingCrops => _allowFarmingCrop.Value;
        public static int SeedReserve => _seedReserve.Value;
        public static SeedSource SeedSource => _seedSource.Value;
        public static bool EnableProactiveSowing => _enableProactiveSowing.Value;
        public static float SowingSpacingFactor => _sowingSpacingFactor.Value;
        public static KeyboardShortcut FarmingKey => _farmingKey.Value;
        public static KeyboardShortcut DesignateContainerKey => _designateContainerKey.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _module = config.Bind("module", AutomaticsModule.Disabled, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_description");
            });

            if (_module.Value == AutomaticsModule.Disabled) return;

            _enableAutomaticFarming = config.Bind("enable_automatic_farming", true);
            _farmingInterval = config.Bind("farming_interval", 1.5f, (0.1f, 4f));
            _farmingRange = config.Bind("farming_range", 8, (1, 64));
            _allowFarmingCrop = config.BindValheimObjectList("allow_farming_crop", ValheimObject.Flora);
            _seedReserve = config.Bind("seed_reserve", 10, (0, 999));
            _seedSource = config.Bind("seed_source", SeedSource.Inventory);
            _enableProactiveSowing = config.Bind("enable_proactive_sowing", true);
            _sowingSpacingFactor = config.Bind("sowing_spacing_factor", 1f, (0.5f, 4f));
            _farmingKey = config.Bind("farming_key", new KeyboardShortcut());
            _designateContainerKey = config.Bind("designate_container_key", new KeyboardShortcut());
        }
    }

    public enum SeedSource
    {
        [LocalizedDescription(Automatics.L10NPrefix, "@config_automatic_farming_seed_source_inventory")]
        Inventory,

        [LocalizedDescription(Automatics.L10NPrefix, "@config_automatic_farming_seed_source_containers")]
        Containers
    }
}
