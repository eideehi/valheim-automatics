using System;
using System.Collections.Generic;
using Automatics.ModUtils;
using BepInEx.Configuration;

namespace Automatics.AutomaticProcessing
{
    using AcceptableType = AcceptableValueEnum<Type>;

    internal static class Config
    {
        private const string Section = "automatic_processing";

        private static ConfigEntry<bool> _enableAutomaticProcessing;
        private static Dictionary<string, ConfigEntry<Type>> _allowProcessing;
        private static Dictionary<string, ConfigEntry<int>> _containerSearchRange;
        private static Dictionary<string, ConfigEntry<int>> _materialCountOfSuppressProcessing;
        private static Dictionary<string, ConfigEntry<int>> _fuelCountOfSuppressProcessing;
        private static Dictionary<string, ConfigEntry<int>> _productCountOfSuppressProcessing;
        private static Dictionary<string, ConfigEntry<bool>> _refuelOnlyWhenMaterialsSupplied;

        public static bool EnableAutomaticProcessing => _enableAutomaticProcessing.Value;

        public static Type AllowProcessing(string target) =>
            _allowProcessing.TryGetValue(target, out var entry) ? entry.Value : Type.None;

        public static int ContainerSearchRange(string target) =>
            _containerSearchRange.TryGetValue(target, out var entry) ? entry.Value : 0;

        public static int MaterialCountOfSuppressProcessing(string target) =>
            _materialCountOfSuppressProcessing.TryGetValue(target, out var entry) ? entry.Value : 0;

        public static int FuelCountOfSuppressProcessing(string target) =>
            _fuelCountOfSuppressProcessing.TryGetValue(target, out var entry) ? entry.Value : 0;

        public static int ProductCountOfSuppressProcessing(string target) =>
            _productCountOfSuppressProcessing.TryGetValue(target, out var entry) ? entry.Value : 0;

        public static bool RefuelOnlyWhenMaterialsSupplied(string target) =>
            _refuelOnlyWhenMaterialsSupplied.TryGetValue(target, out var entry) && entry.Value;

        public static void Initialize()
        {
            Action<ConfigurationManagerAttributes> Initializer(string key, string displayName)
            {
                return x =>
                {
                    x.DispName = L10N.Localize($"@config_automatic_processing_{key}_name", displayName);
                    x.Description = L10N.Localize($"@config_automatic_processing_{key}_description", displayName);
                };
            }

            Configuration.ChangeSection(Section);
            _enableAutomaticProcessing = Configuration.Bind("enable_automatic_processing", true);

            _allowProcessing = new Dictionary<string, ConfigEntry<Type>>();
            _containerSearchRange = new Dictionary<string, ConfigEntry<int>>();
            _materialCountOfSuppressProcessing = new Dictionary<string, ConfigEntry<int>>();
            _fuelCountOfSuppressProcessing = new Dictionary<string, ConfigEntry<int>>();
            _productCountOfSuppressProcessing = new Dictionary<string, ConfigEntry<int>>();
            _refuelOnlyWhenMaterialsSupplied = new Dictionary<string, ConfigEntry<bool>>();

            var acceptStoreOnly = new AcceptableType(Type.None, Type.Store);
            var acceptRefuelOnly = new AcceptableType(Type.None, Type.Refuel);
            var acceptCraftAndStore = new AcceptableType(Type.None, Type.Craft, Type.Store);
            var acceptAll = new AcceptableType(Type.None, Type.Craft, Type.Refuel, Type.Store);

            var configData = new Dictionary<string, (Type, AcceptableType)>
            {
                //{ Target.ArtisanTable, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.Beehive, (Type.Store, acceptStoreOnly) },
                { Target.Bonfire, (Type.Refuel, acceptRefuelOnly) },
                { Target.BlastFurnace, (Type.Craft | Type.Refuel | Type.Store, acceptAll) },
                { Target.Campfire, (Type.Refuel, acceptRefuelOnly) },
                //{ Target.Cauldron, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.CharcoalKiln, (Type.Craft | Type.Store, acceptCraftAndStore) },
                { Target.CookingStation, (Type.Store, acceptCraftAndStore) },
                { Target.Fermenter, (Type.Craft | Type.Store, acceptCraftAndStore) },
                //{ Target.Forge, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.HangingBrazier, (Type.Refuel, acceptRefuelOnly) },
                { Target.Hearth, (Type.Refuel, acceptRefuelOnly) },
                { Target.IronCookingStation, (Type.Store, acceptCraftAndStore) },
                { Target.JackOTurnip, (Type.Refuel, acceptRefuelOnly) },
                { Target.Sconce, (Type.Refuel, acceptRefuelOnly) },
                { Target.Smelter, (Type.Craft | Type.Refuel | Type.Store, acceptAll) },
                { Target.SpinningWheel, (Type.Store, acceptCraftAndStore) },
                { Target.StandingBlueBurningIronTorch, (Type.Refuel, acceptRefuelOnly) },
                { Target.StandingBrazier, (Type.Refuel, acceptRefuelOnly) },
                { Target.StandingGreenBurningIronTorch, (Type.Refuel, acceptRefuelOnly) },
                { Target.StandingIronTorch, (Type.Refuel, acceptRefuelOnly) },
                { Target.StandingWoodTorch, (Type.Refuel, acceptRefuelOnly) },
                { Target.StoneOven, (Type.Craft | Type.Refuel | Type.Store, acceptAll) },
                //{ Target.Stonecutter, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
                { Target.Windmill, (Type.Store, acceptCraftAndStore) },
                //{ Target.Workbench, (Type.None, new AcceptableType(Type.None, Type.Craft)) },
            };

            foreach (var target in Target.All)
            {
                var (defaultType, acceptableValue) = configData[target];

                var displayName = L10N.Translate(target);
                var rawName = target.Substring(1);
                var key = $"allow_processing_by_{rawName}";
                _allowProcessing[target] =
                    Configuration.Bind(key, defaultType, acceptableValue, Initializer("allow_processing_by", displayName));

                key = $"container_search_range_by_{rawName}";
                _containerSearchRange[target] =
                    Configuration.Bind(key, 8, (1, 64), Initializer("container_search_range_by", displayName));

                if (acceptableValue.IsValid(Type.Craft))
                {
                    key = $"{rawName}_material_count_of_suppress_processing";
                    _materialCountOfSuppressProcessing[target] =
                        Configuration.Bind(key, 1, (0, 9999), Initializer("material_count_of_suppress_processing", displayName));
                }

                if (acceptableValue.IsValid(Type.Refuel))
                {
                    key = $"{rawName}_fuel_count_of_suppress_processing";
                    _fuelCountOfSuppressProcessing[target] =
                        Configuration.Bind(key, 1, (0, 9999), Initializer("fuel_count_of_suppress_processing", displayName));
                }

                if (acceptableValue.IsValid(Type.Store))
                {
                    key = $"{rawName}_product_count_of_suppress_processing";
                    _productCountOfSuppressProcessing[target] =
                        Configuration.Bind(key, 0, (0, 9999), Initializer("product_count_of_suppress_processing", displayName));
                }

                if (acceptableValue.IsValid(Type.Refuel) && acceptableValue.IsValid(Type.Craft))
                {
                    key = $"{rawName}_refuel_only_when_materials_supplied";
                    _refuelOnlyWhenMaterialsSupplied[target] =
                        Configuration.Bind(key, false, initializer: Initializer("refuel_only_when_materials_supplied", displayName));
                }
            }
        }
    }
}