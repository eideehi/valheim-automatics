using BepInEx.Configuration;
using ModUtils;

namespace Automatics.AutomaticWoodcutting
{
    internal static class Config
    {
        private const string Section = "automatic_woodcutting";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<bool> _enableAutomaticWoodcutting;
        private static ConfigEntry<float> _woodcuttingInterval;
        private static ConfigEntry<int> _woodcuttingRange;
        private static ConfigEntry<StringList> _allowWoodcuttingTree;
        private static ConfigEntry<bool> _needToEquipAxe;
        private static ConfigEntry<KeyboardShortcut> _woodcuttingKey;
        private static ConfigEntry<bool> _enableAreaWoodcutting;
        private static ConfigEntry<float> _areaWoodcuttingRadius;
        private static ConfigEntry<int> _areaWoodcuttingMaxTargets;
        private static ConfigEntry<bool> _areaWoodcuttingSameKindFirst;

        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;
        public static bool EnableAutomaticWoodcutting => _enableAutomaticWoodcutting.Value;
        internal static ConfigEntry<bool> EnableAutomaticWoodcuttingEntry => _enableAutomaticWoodcutting;
        public static float WoodcuttingInterval => _woodcuttingInterval.Value;
        internal static ConfigEntry<float> WoodcuttingIntervalEntry => _woodcuttingInterval;
        public static int WoodcuttingRange => _woodcuttingRange.Value;
        internal static ConfigEntry<int> WoodcuttingRangeEntry => _woodcuttingRange;
        public static StringList AllowWoodcuttingTree => _allowWoodcuttingTree.Value;
        internal static ConfigEntry<StringList> AllowWoodcuttingTreeEntry => _allowWoodcuttingTree;
        public static bool NeedToEquipAxe => _needToEquipAxe.Value;
        internal static ConfigEntry<bool> NeedToEquipAxeEntry => _needToEquipAxe;
        public static KeyboardShortcut WoodcuttingKey => _woodcuttingKey.Value;
        public static bool EnableAreaWoodcutting => _enableAreaWoodcutting.Value;
        public static float AreaWoodcuttingRadius => _areaWoodcuttingRadius.Value;
        internal static ConfigEntry<float> AreaWoodcuttingRadiusEntry => _areaWoodcuttingRadius;
        public static int AreaWoodcuttingMaxTargets => _areaWoodcuttingMaxTargets.Value;
        internal static ConfigEntry<int> AreaWoodcuttingMaxTargetsEntry => _areaWoodcuttingMaxTargets;
        public static bool AreaWoodcuttingSameKindFirst => _areaWoodcuttingSameKindFirst.Value;
        internal static ConfigEntry<bool> AreaWoodcuttingSameKindFirstEntry => _areaWoodcuttingSameKindFirst;

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

            _enableAutomaticWoodcutting = config.Bind("enable_automatic_woodcutting", true);
            _woodcuttingInterval = config.Bind("woodcutting_interval", 1.5f, (0f, 4f));
            _woodcuttingRange = config.Bind("woodcutting_range", 3, (0, 32));
            _allowWoodcuttingTree = config.BindValheimObjectList("allow_woodcutting_tree", Globals.Tree);
            _needToEquipAxe = config.Bind("need_to_equip_axe", true);
            _woodcuttingKey = config.Bind("woodcutting_key", new KeyboardShortcut());
            _enableAreaWoodcutting = config.Bind("enable_area_woodcutting", true);
            _areaWoodcuttingRadius = config.Bind("area_woodcutting_radius", 3f, (1f, 10f));
            _areaWoodcuttingMaxTargets = config.Bind("area_woodcutting_max_targets", 8, (1, 32));
            _areaWoodcuttingSameKindFirst = config.Bind("area_woodcutting_same_kind_first", true);

            config.ChangeSection("general", 32);
            config.BindCustomValheimObject("custom_tree", Globals.Tree);
        }
    }
}
