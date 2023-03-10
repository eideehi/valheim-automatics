using System;
using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;

namespace Automatics.AutomaticMapping
{
    internal static class Config
    {
        private const string Section = "automatic_mapping";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<bool> _moduleDisable;
        private static ConfigEntry<bool> _enableAutomaticMapping;
        private static ConfigEntry<int> _dynamicObjectMappingRange;
        private static ConfigEntry<int> _staticObjectMappingRange;
        private static ConfigEntry<int> _locationMappingRange;
        private static ConfigEntry<StringList> _allowPinningAnimal;
        private static ConfigEntry<StringList> _allowPinningMonster;
        private static ConfigEntry<StringList> _allowPinningFlora;
        private static ConfigEntry<StringList> _allowPinningMineral;
        private static ConfigEntry<StringList> _allowPinningSpawner;
        private static ConfigEntry<StringList> _allowPinningOther;
        private static ConfigEntry<StringList> _allowPinningDungeon;
        private static ConfigEntry<StringList> _allowPinningSpot;
        private static ConfigEntry<StringList> _allowPinningVehicle;
        private static ConfigEntry<bool> _allowPinningPortal;
        private static ConfigEntry<bool> _notPinningTamedAnimals;
        private static ConfigEntry<float> _staticObjectMappingInterval;
        private static ConfigEntry<int> _staticObjectCachingInterval;
        private static ConfigEntry<bool> _saveStaticObjectPins;
        private static ConfigEntry<bool> _removePinsOfDestroyedObject;
        private static ConfigEntry<int> _floraPinMergeRange;
        private static ConfigEntry<bool> _needToEquipWishboneForUndergroundMinerals;
        private static ConfigEntry<KeyboardShortcut> _staticObjectMappingKey;

        public static bool ModuleDisabled => _module.Value == AutomaticsModule.Disabled;
        public static bool IsModuleDisabled => _moduleDisable.Value;
        public static bool EnableAutomaticMapping => _enableAutomaticMapping.Value;
        public static int DynamicObjectMappingRange => _dynamicObjectMappingRange.Value;
        public static int StaticObjectMappingRange => _staticObjectMappingRange.Value;
        public static int LocationMappingRange => _locationMappingRange.Value;
        public static StringList AllowPinningAnimal => _allowPinningAnimal.Value;
        public static StringList AllowPinningMonster => _allowPinningMonster.Value;
        public static StringList AllowPinningFlora => _allowPinningFlora.Value;
        public static StringList AllowPinningMineral => _allowPinningMineral.Value;
        public static StringList AllowPinningSpawner => _allowPinningSpawner.Value;
        public static StringList AllowPinningDungeon => _allowPinningDungeon.Value;
        public static StringList AllowPinningSpot => _allowPinningSpot.Value;
        public static StringList AllowPinningOther => _allowPinningOther.Value;
        public static StringList AllowPinningVehicle => _allowPinningVehicle.Value;
        public static bool AllowPinningPortal => _allowPinningPortal.Value;
        public static bool NotPinningTamedAnimals => _notPinningTamedAnimals.Value;
        public static float StaticObjectMappingInterval => _staticObjectMappingInterval.Value;
        public static float StaticObjectCachingInterval => _staticObjectCachingInterval.Value;
        public static bool SaveStaticObjectPins => _saveStaticObjectPins.Value;
        public static bool RemovePinsOfDestroyedObject => _removePinsOfDestroyedObject.Value;
        public static int FloraPinMergeRange => _floraPinMergeRange.Value;

        public static bool NeedToEquipWishboneForUndergroundMinerals =>
            _needToEquipWishboneForUndergroundMinerals.Value;

        public static KeyboardShortcut StaticObjectMappingKey => _staticObjectMappingKey.Value;

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

            _enableAutomaticMapping = config.Bind("enable_automatic_mapping", true);
            _dynamicObjectMappingRange = config.Bind("dynamic_object_mapping_range", 64, (0, 128));
            _staticObjectMappingRange = config.Bind("static_object_mapping_range", 32, (0, 128));
            _locationMappingRange = config.Bind("location_mapping_range", 96, (0, 128));
            _allowPinningAnimal = config.BindValheimObjectList("allow_pinning_animal", ValheimObject.Animal);
            _allowPinningMonster = config.BindValheimObjectList("allow_pinning_monster", ValheimObject.Monster);
            _allowPinningFlora = config.BindValheimObjectList("allow_pinning_flora",
                ValheimObject.Flora,
                includes: new[]
                {
                    "Raspberries", "Mushroom", "Blueberries", "CarrotSeeds", "Thistle",
                    "TurnipSeeds", "Cloudberries", "JotunPuffs", "Magecap"
                });
            _allowPinningMineral = config.BindValheimObjectList("allow_pinning_mineral", ValheimObject.Mineral, excludes: new[] { "ObsidianDeposit" });
            _allowPinningSpawner = config.BindValheimObjectList("allow_pinning_spawner", ValheimObject.Spawner, Array.Empty<string>());
            _allowPinningVehicle = config.BindValheimObjectList("allow_pinning_vehicle", MappingObject.Vehicle, includes: new[] { "Karve", "Longship" });
            _allowPinningOther = config.BindValheimObjectList("allow_pinning_other", MappingObject.Other, includes: new[] { "WildBeehive" });
            _allowPinningDungeon = config.BindValheimObjectList("allow_pinning_dungeon", ValheimObject.Dungeon);
            _allowPinningSpot = config.BindValheimObjectList("allow_pinning_spot", ValheimObject.Spot);
            _allowPinningPortal = config.Bind("allow_pinning_portal", true);
            _notPinningTamedAnimals = config.Bind("not_pinning_tamed_animals", true);
            _staticObjectMappingInterval = config.Bind("static_object_mapping_interval", 0.25f, (0f, 4f));
            _staticObjectCachingInterval = config.Bind("static_object_caching_interval", 3, (1, 8));
            _saveStaticObjectPins = config.Bind("save_static_object_pins", false);
            _removePinsOfDestroyedObject = config.Bind("remove_pins_of_destroyed_object", true);
            _floraPinMergeRange = config.Bind("flora_pins_merge_range", 8, (0, 16));
            _needToEquipWishboneForUndergroundMinerals = config.Bind("need_to_equip_wishbone_for_underground_minerals", true);
            _staticObjectMappingKey = config.Bind("static_object_mapping_key", new KeyboardShortcut());

            config.ChangeSection("general", 128);
            config.BindCustomValheimObject("custom_vehicle", MappingObject.Vehicle);
            config.BindCustomValheimObject("custom_other", MappingObject.Other);
        }
    }
}