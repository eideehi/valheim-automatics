using System.Collections.Generic;
using Automatics.ModUtils;
using BepInEx.Configuration;
using AcceptableType =
    Automatics.ModUtils.Configuration.AcceptableValueEnum<Automatics.AutomaticProcessing.Type>;

namespace Automatics.AutomaticProcessing
{
    internal static class Config
    {
        private const string Section = "automatic_processing";

        private static ConfigEntry<bool> _automaticProcessingEnabled;
        private static Dictionary<string, ConfigEntry<Type>> _targetByAllowAutomaticProcessingType;
        private static Dictionary<string, ConfigEntry<int>> _targetByContainerSearchRange;

        public static bool AutomaticProcessingEnabled => _automaticProcessingEnabled.Value;

        public static bool IsAllowAutomaticProcessing(string target, Type type) =>
            _targetByAllowAutomaticProcessingType.TryGetValue(target, out var entry) && (entry.Value & type) != 0;

        public static int GetContainerSearchRange(string target, int @default = 0) =>
            _targetByContainerSearchRange.TryGetValue(target, out var entry)
                ? entry.Value
                : @default;

        public static void Initialize()
        {
            Configuration.ResetOrder();
            _automaticProcessingEnabled = Configuration.Bind(Section, "automatic_processing_enabled", true);

            _targetByAllowAutomaticProcessingType = new Dictionary<string, ConfigEntry<Type>>();
            _targetByContainerSearchRange = new Dictionary<string, ConfigEntry<int>>();

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

                var targetName = L10N.Translate(target);
                var key = $"{target.Substring(1)}_allow_automatic_processing";
                _targetByAllowAutomaticProcessingType[target] =
                    Configuration.Bind(Section, key, defaultType, acceptableType, x =>
                    {
                        x.DispName = L10N.Localize("@config_allow_automatic_processing_name", targetName);
                        x.Description = L10N.Localize("@config_allow_automatic_processing_description", targetName);
                    });

                key = $"{target.Substring(1)}_container_search_range";
                var acceptableValue = new AcceptableValueRange<int>(1, 32);
                _targetByContainerSearchRange[target] =
                    Configuration.Bind(Section, key, 8, acceptableValue, x =>
                    {
                        x.DispName = L10N.Localize("@config_container_search_range_name", targetName);
                        x.Description = L10N.Localize("@config_container_search_range_description", targetName);
                    });
            }
        }
    }
}