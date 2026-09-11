using System;
using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class Config
    {
        private const string Section = "automatic_mapping";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<bool> _enableAutomaticMapping;
        private static ConfigEntry<int> _dynamicObjectMappingRange;
        private static ConfigEntry<int> _staticObjectMappingRange;
        private static ConfigEntry<int> _locationMappingRange;
        private static ConfigEntry<bool> _hideUnexploredAutomaticMappingPins;
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
        private static ConfigEntry<KeyboardShortcut> _navigationStartKey;
        private static ConfigEntry<bool> _mappingPerformanceLog;

        /// <summary>
        ///     The toggle the module registry follows. It is exposed so the registry can read the
        ///     user's choice and subscribe to its changes; nothing writes it back (R1.3b), and no
        ///     hot path reads it either - those read the applied state on <see cref="Module" />
        ///     instead (R1.5).
        /// </summary>
        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;

        public static bool EnableAutomaticMapping => _enableAutomaticMapping.Value;
        public static int DynamicObjectMappingRange => _dynamicObjectMappingRange.Value;
        public static int StaticObjectMappingRange => _staticObjectMappingRange.Value;
        public static int LocationMappingRange => _locationMappingRange.Value;
        public static bool HideUnexploredAutomaticMappingPins =>
            _hideUnexploredAutomaticMappingPins?.Value == true;
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
        public static KeyboardShortcut NavigationStartKey => _navigationStartKey.Value;
        public static bool MappingPerformanceLog => _mappingPerformanceLog.Value;

        // Fish and Bird drive separate scan paths (FishCache / BirdCache) but
        // live as magic-string entries inside the Animal allowlist. Caching
        // these two flags here lets the hot scan loop skip the per-tick
        // StringList.Contains walk; the SettingChanged bridge below refreshes
        // them when the user edits the allowlist.
        public static bool PinAnimalIncludesFish { get; private set; }
        public static bool PinAnimalIncludesBird { get; private set; }

        /// <summary>
        /// Raised when one of the static-mapping allowlists
        /// (<c>allow_pinning_flora / mineral / spawner / other / dungeon / spot</c>)
        /// changes at runtime. Subscribers are expected to run a targeted
        /// invalidation pass so that already-added pins in the affected
        /// category are reconciled with the new allowlist even while C-6's
        /// sweep generation is frozen in degraded mode. The event argument
        /// identifies the affected registry (ValheimObject / MappingObject
        /// instance) so subscribers can scope their work by domain.
        /// </summary>
        internal static event Action<ValheimObject> StaticAllowlistChanged;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _module = config.Bind("module", AutomaticsModule.Enabled, initializer: x =>
            {
                x.DispName =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_name");
                x.Description =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_description");
            });

            // Bound unconditionally (R5.1). The module can be turned on while the game runs, so
            // the settings it needs have to exist whether it starts on or off; the early return
            // that used to sit here left them null and made a runtime enable impossible. The
            // SettingChanged bridges below are therefore live while the module is off too. The
            // allowlist bridges raise an event nothing subscribes to until the module is turned
            // on, the animal-list bridge only refreshes two cached flags, and the one bridge that
            // redraws the map checks the module's applied state first. The values bound here also
            // pick up anything the user set before turning the module off, and they apply as soon
            // as it is turned on.
            _enableAutomaticMapping = config.Bind("enable_automatic_mapping", true);
            _dynamicObjectMappingRange = config.Bind("dynamic_object_mapping_range", 64, (0, 128));
            _staticObjectMappingRange = config.Bind("static_object_mapping_range", 32, (0, 128));
            _locationMappingRange = config.Bind("location_mapping_range", 96, (0, 128));
            _hideUnexploredAutomaticMappingPins = config.Bind("hide_unexplored_automatic_mapping_pins", false);
            _allowPinningAnimal = config.BindValheimObjectList("allow_pinning_animal", ValheimObject.Animal);
            _allowPinningMonster = config.BindValheimObjectList("allow_pinning_monster", ValheimObject.Monster);
            _allowPinningFlora = config.BindValheimObjectList("allow_pinning_flora",
                ValheimObject.Flora,
                includes: new[]
                {
                    "Raspberries", "Mushroom", "Blueberries", "CarrotSeeds", "Thistle",
                    "TurnipSeeds", "Cloudberries", "JotunPuffs", "Magecap",
                    "Fiddlehead", "SmokePuff", "Vineberry", "VineberrySeeds"
                });
            _allowPinningMineral = config.BindValheimObjectList("allow_pinning_mineral", ValheimObject.Mineral, excludes: new[] { "ObsidianDeposit" });
            _allowPinningSpawner = config.BindValheimObjectList("allow_pinning_spawner", ValheimObject.Spawner, Array.Empty<string>());
            _allowPinningVehicle = config.BindValheimObjectList("allow_pinning_vehicle", MappingObject.Vehicle, includes: new[] { "Karve", "Longship", "Drakkar" });
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
            _navigationStartKey = config.Bind("navigation_start_key",
                new KeyboardShortcut(KeyCode.LeftShift));
            _mappingPerformanceLog = config.Bind("mapping_performance_log", false);

            // Bridge BepInEx SettingChanged events to StaticAllowlistChanged so that
            // subscribers (e.g. targeted pin invalidation) do not need access to the
            // private ConfigEntry fields. Note that Other is a MappingObject entry —
            // not ValheimObject.Other — and must pass that exact instance through so
            // subscribers doing ReferenceEquals checks see the correct registry.
            RefreshAnimalListFlags();
            _allowPinningAnimal.SettingChanged += (_, __) => RefreshAnimalListFlags();
            _hideUnexploredAutomaticMappingPins.SettingChanged += OnHideUnexploredPinsChanged;

            _allowPinningFlora.SettingChanged += (_, __) => StaticAllowlistChanged?.Invoke(ValheimObject.Flora);
            _allowPinningMineral.SettingChanged += (_, __) => StaticAllowlistChanged?.Invoke(ValheimObject.Mineral);
            _allowPinningSpawner.SettingChanged += (_, __) => StaticAllowlistChanged?.Invoke(ValheimObject.Spawner);
            _allowPinningOther.SettingChanged += (_, __) => StaticAllowlistChanged?.Invoke(MappingObject.Other);
            _allowPinningDungeon.SettingChanged += (_, __) => StaticAllowlistChanged?.Invoke(ValheimObject.Dungeon);
            _allowPinningSpot.SettingChanged += (_, __) => StaticAllowlistChanged?.Invoke(ValheimObject.Spot);

            config.ChangeSection("general", 128);
            config.BindCustomValheimObject("custom_vehicle", MappingObject.Vehicle);
            config.BindCustomValheimObject("custom_other", MappingObject.Other);
        }

        /// <summary>
        ///     Redraws the pins when hiding unexplored automatic pins is switched. The bridge is
        ///     bound whether the module runs or not, so it checks the applied state first: while
        ///     the module is off the map is left exactly as the game draws it.
        /// </summary>
        private static void OnHideUnexploredPinsChanged(object sender, EventArgs e)
        {
            if (!Module.Running) return;
            Map.RefreshPins();
        }

        private static void RefreshAnimalListFlags()
        {
            var list = _allowPinningAnimal?.Value;
            PinAnimalIncludesFish = list != null && list.Contains("Fish");
            PinAnimalIncludesBird = list != null && list.Contains("Bird");
        }
    }
}
