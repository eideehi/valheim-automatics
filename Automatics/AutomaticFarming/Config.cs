using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

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

        private static ConfigEntry<bool> _enableGridPlanting;
        private static ConfigEntry<KeyboardShortcut> _gridPlantingKey;
        private static ConfigEntry<int> _gridPlantingSize;

        /// <summary>
        ///     The toggle the module registry follows. It is exposed so the registry can read the
        ///     user's choice and subscribe to its changes; nothing writes it back (R1.3b), and no
        ///     hot path reads it either - those read the applied state on <see cref="Module" />
        ///     instead (R1.5).
        /// </summary>
        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;

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

        public static bool EnableGridPlanting => _enableGridPlanting.Value;
        public static KeyboardShortcut GridPlantingKey => _gridPlantingKey.Value;
        public static int GridPlantingSize => _gridPlantingSize.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _module = config.Bind("module", AutomaticsModule.Disabled, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_runtime_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_runtime_description");
            });

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
            _enableGridPlanting = config.Bind("enable_grid_planting", true);
            _gridPlantingKey = config.Bind("grid_planting_key", new KeyboardShortcut(KeyCode.LeftAlt));
            _gridPlantingSize = config.Bind("grid_planting_size", 5, (1, 9));
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
