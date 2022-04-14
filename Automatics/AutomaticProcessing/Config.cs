using System.Collections.Generic;
using Automatics.ModUtils;
using BepInEx.Configuration;

namespace Automatics.AutomaticProcessing
{
    using AcceptableType = Configuration.AcceptableValueEnum<Type>;

    internal static class Config
    {
        private const string Section = "automatic_processing";

        private static ConfigEntry<bool> _automaticProcessingEnabled;
        private static Dictionary<string, ConfigEntry<Type>> _targetByAllowAutomaticProcessingType;
        private static Dictionary<string, ConfigEntry<int>> _targetByContainerSearchRange;
        private static Dictionary<string, ConfigEntry<int>> _targetByItemCountThatSuppressAutomaticCraft;
        private static Dictionary<string, ConfigEntry<int>> _targetByItemCountThatSuppressAutomaticRefuel;
        private static Dictionary<string, ConfigEntry<int>> _targetByItemCountThatSuppressAutomaticStore;
        private static Dictionary<string, ConfigEntry<bool>> _targetByRefuelOnlyWhenMaterialsSupplied;

        public static bool AutomaticProcessingEnabled => _automaticProcessingEnabled.Value;

        public static bool IsAllowAutomaticProcessing(string target, Type type) =>
            _targetByAllowAutomaticProcessingType.TryGetValue(target, out var entry) && (entry.Value & type) != 0;

        public static int GetContainerSearchRange(string target, int @default = 0) =>
            _targetByContainerSearchRange.TryGetValue(target, out var entry)
                ? entry.Value
                : @default;

        public static int GetItemCountThatSuppressAutomaticCraft(string target, int @default = 0) =>
            _targetByItemCountThatSuppressAutomaticCraft.TryGetValue(target, out var entry)
                ? entry.Value
                : @default;

        public static int GetItemCountThatSuppressAutomaticRefuel(string target, int @default = 0) =>
            _targetByItemCountThatSuppressAutomaticRefuel.TryGetValue(target, out var entry)
                ? entry.Value
                : @default;

        public static int GetItemCountThatSuppressAutomaticStore(string target, int @default = 0) =>
            _targetByItemCountThatSuppressAutomaticStore.TryGetValue(target, out var entry)
                ? entry.Value
                : @default;

        public static bool IsRefuelOnlyWhenMaterialsSupplied(string target, bool @default = false) =>
            _targetByRefuelOnlyWhenMaterialsSupplied.TryGetValue(target, out var entry)
                ? entry.Value
                : @default;

        public static void Initialize()
        {
            Configuration.ChangeSection(Section);
            _automaticProcessingEnabled = Configuration.Bind("automatic_processing_enabled", true);

            _targetByAllowAutomaticProcessingType = new Dictionary<string, ConfigEntry<Type>>();
            _targetByContainerSearchRange = new Dictionary<string, ConfigEntry<int>>();
            _targetByItemCountThatSuppressAutomaticCraft = new Dictionary<string, ConfigEntry<int>>();
            _targetByItemCountThatSuppressAutomaticRefuel = new Dictionary<string, ConfigEntry<int>>();
            _targetByItemCountThatSuppressAutomaticStore = new Dictionary<string, ConfigEntry<int>>();
            _targetByRefuelOnlyWhenMaterialsSupplied = new Dictionary<string, ConfigEntry<bool>>();

            var acceptStore = new AcceptableType(Type.None, Type.Store);
            var acceptRefuel = new AcceptableType(Type.None, Type.Refuel);
            var acceptCraftStore = new AcceptableType(Type.None, Type.Craft, Type.Store);
            var acceptAll = new AcceptableType(Type.None, Type.Craft, Type.Refuel, Type.Store);
            var targetByConfigData = new Dictionary<string, (Type, AcceptableType)>
            {
                //{ Target.ArtisanTable, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.Beehive, (Type.Store, acceptStore) },
                { Target.Bonfire, (Type.Refuel, acceptRefuel) },
                { Target.BlastFurnace, (Type.Craft | Type.Refuel | Type.Store, acceptAll) },
                { Target.Campfire, (Type.Refuel, acceptRefuel) },
                //{ Target.Cauldron, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.CharcoalKiln, (Type.Craft | Type.Store, acceptCraftStore) },
                { Target.CookingStation, (Type.Store, acceptCraftStore) },
                { Target.Fermenter, (Type.Craft | Type.Store, acceptCraftStore) },
                //{ Target.Forge, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.HangingBrazier, (Type.Refuel, acceptRefuel) },
                { Target.Hearth, (Type.Refuel, acceptRefuel) },
                { Target.IronCookingStation, (Type.Store, acceptCraftStore) },
                { Target.JackOTurnip, (Type.Refuel, acceptRefuel) },
                { Target.Sconce, (Type.Refuel, acceptRefuel) },
                { Target.Smelter, (Type.Craft | Type.Refuel | Type.Store, acceptAll) },
                { Target.SpinningWheel, (Type.Store, acceptCraftStore) },
                { Target.StandingBlueBurningIronTorch, (Type.Refuel, acceptRefuel) },
                { Target.StandingBrazier, (Type.Refuel, acceptRefuel) },
                { Target.StandingGreenBurningIronTorch, (Type.Refuel, acceptRefuel) },
                { Target.StandingIronTorch, (Type.Refuel, acceptRefuel) },
                { Target.StandingWoodTorch, (Type.Refuel, acceptRefuel) },
                { Target.StoneOven, (Type.Craft | Type.Refuel | Type.Store, acceptAll) },
                //{ Target.Stonecutter, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.Windmill, (Type.Store, acceptCraftStore) },
                //{ Target.Workbench, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
            };

            foreach (var target in Target.All)
            {
                var (defaultType, acceptableType) = targetByConfigData[target];

                var displayName = L10N.Translate(target);
                var rawName = target.Substring(1);
                var key = $"{rawName}_allow_automatic_processing";
                _targetByAllowAutomaticProcessingType[target] =
                    Configuration.Bind(key, defaultType, acceptableType, x =>
                    {
                        x.DispName = L10N.Localize("@config_allow_automatic_processing_name", displayName);
                        x.Description = L10N.Localize("@config_allow_automatic_processing_description", displayName);
                    });

                key = $"{rawName}_container_search_range";
                var acceptableValue = new AcceptableValueRange<int>(1, 64);
                _targetByContainerSearchRange[target] =
                    Configuration.Bind(key, 8, acceptableValue, x =>
                    {
                        x.DispName = L10N.Localize("@config_container_search_range_name", displayName);
                        x.Description = L10N.Localize("@config_container_search_range_description", displayName);
                    });

                if (acceptableType.IsValid(Type.Craft))
                {
                    key = $"{rawName}_material_count_that_suppress_automatic_process";
                    acceptableValue = new AcceptableValueRange<int>(0, 9999);
                    _targetByItemCountThatSuppressAutomaticCraft[target] =
                        Configuration.Bind(key, 1, acceptableValue, x =>
                        {
                            x.DispName = L10N.Localize("@config_material_count_that_suppress_automatic_process_name", displayName);
                            x.Description = L10N.Localize("@config_material_count_that_suppress_automatic_process_description", displayName);
                        });
                }

                if (acceptableType.IsValid(Type.Refuel))
                {
                    key = $"{rawName}_fuel_count_that_suppress_automatic_process";
                    acceptableValue = new AcceptableValueRange<int>(0, 9999);
                    _targetByItemCountThatSuppressAutomaticRefuel[target] =
                        Configuration.Bind(key, 1, acceptableValue, x =>
                        {
                            x.DispName = L10N.Localize("@config_fuel_count_that_suppress_automatic_process_name", displayName);
                            x.Description = L10N.Localize("@config_fuel_count_that_suppress_automatic_process_description", displayName);
                        });
                }

                if (acceptableType.IsValid(Type.Store))
                {
                    key = $"{rawName}_product_count_that_suppress_automatic_store";
                    acceptableValue = new AcceptableValueRange<int>(0, 9999);
                    _targetByItemCountThatSuppressAutomaticStore[target] =
                        Configuration.Bind(key, 0, acceptableValue, x =>
                        {
                            x.DispName = L10N.Localize("@config_product_count_that_suppress_automatic_store_name", displayName);
                            x.Description = L10N.Localize("@config_product_count_that_suppress_automatic_store_description", displayName);
                        });
                }

                if (acceptableType.IsValid(Type.Refuel) && acceptableType.IsValid(Type.Craft))
                {
                    key = $"{rawName}_refuel_only_when_materials_supplied";
                    _targetByRefuelOnlyWhenMaterialsSupplied[target] =
                        Configuration.Bind(key, false, initializer: x =>
                        {
                            x.DispName = L10N.Localize("@config_refuel_only_when_materials_supplied_name", displayName);
                            x.Description = L10N.Localize("@config_refuel_only_when_materials_supplied_description", displayName);
                        });
                }
            }
        }
    }
}