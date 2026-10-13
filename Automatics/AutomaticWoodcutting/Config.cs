using BepInEx.Configuration;

namespace Automatics.AutomaticWoodcutting
{
    internal static class Config
    {
        private const string Section = "automatic_woodcutting";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<float> _woodcuttingInterval;
        private static ConfigEntry<int> _woodcuttingRange;
        private static ConfigEntry<KeyboardShortcut> _woodcuttingKey;

        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;
        public static float WoodcuttingInterval => _woodcuttingInterval.Value;
        internal static ConfigEntry<float> WoodcuttingIntervalEntry => _woodcuttingInterval;
        public static int WoodcuttingRange => _woodcuttingRange.Value;
        internal static ConfigEntry<int> WoodcuttingRangeEntry => _woodcuttingRange;
        public static KeyboardShortcut WoodcuttingKey => _woodcuttingKey.Value;

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

            _woodcuttingInterval = config.Bind("woodcutting_interval", 1.5f, (0f, 4f));
            _woodcuttingRange = config.Bind("woodcutting_range", 3, (0, 32));
            _woodcuttingKey = config.Bind("woodcutting_key", new KeyboardShortcut());
        }
    }
}
