using BepInEx.Configuration;

namespace Automatics.AutomaticSupply
{
    internal static class Config
    {
        private const string Section = "automatic_supply";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<int> _supplyRange;

        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;
        public static int SupplyRange => _supplyRange.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            // Off by default for everyone, upgraders included: the config migrator does not list
            // this section, so a config written before this module existed keeps it off too.
            _module = config.Bind("module", AutomaticsModule.Disabled, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_runtime_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_runtime_description");
            });

            _supplyRange = config.Bind("supply_range", 10, (1, 64));
        }
    }
}
