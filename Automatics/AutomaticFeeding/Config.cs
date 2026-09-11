using BepInEx.Configuration;

namespace Automatics.AutomaticFeeding
{
    internal static class Config
    {
        private const string Section = "automatic_feeding";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<bool> _enableAutomaticFeeding;
        private static ConfigEntry<int> _feedSearchRange;
        private static ConfigEntry<bool> _needGetCloseToEatTheFeed;
        private static ConfigEntry<AnimalType> _allowToFeedFromContainer;
        private static ConfigEntry<AnimalType> _allowToFeedFromPlayer;

        /// <summary>
        ///     The toggle the module registry follows. The registry reads it and subscribes to its
        ///     changes; nothing writes it back, and the entry points read the applied state on
        ///     <see cref="Module" /> instead (R1.5).
        /// </summary>
        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;

        public static bool EnableAutomaticFeeding => _enableAutomaticFeeding.Value;
        public static float FeedSearchRange => _feedSearchRange.Value;
        public static bool NeedGetCloseToEatTheFeed => _needGetCloseToEatTheFeed.Value;
        public static AnimalType AllowToFeedFromContainer => _allowToFeedFromContainer.Value;
        public static AnimalType AllowToFeedFromPlayer => _allowToFeedFromPlayer.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _module = config.Bind("module", AutomaticsModule.Disabled, initializer: x =>
            {
                x.DispName =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_name");
                x.Description =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_description");
            });

            // Bound unconditionally (R5.1). The module can be turned on while the game runs, so
            // the settings it needs have to exist whether it starts on or off. This section has no
            // SettingChanged bridge, so binding a disabled module costs nothing but the lines it
            // writes to the config file.
            _enableAutomaticFeeding = config.Bind("enable_automatic_feeding", true);
            _feedSearchRange = config.Bind("feed_search_range", 0, (0, 64));
            _needGetCloseToEatTheFeed = config.Bind("need_get_close_to_eat_the_feed", false);
            _allowToFeedFromContainer = config.Bind("allow_to_feed_from_container", AnimalType.Tamed);
            _allowToFeedFromPlayer = config.Bind("allow_to_feed_from_player", AnimalType.None);
        }
    }
}