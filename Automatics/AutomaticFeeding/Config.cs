using BepInEx.Configuration;

namespace Automatics.AutomaticFeeding
{
    internal static class Config
    {
        private const string Section = "automatic_feeding";

        private static ConfigEntry<bool> _moduleDisable;
        private static ConfigEntry<bool> _enableAutomaticFeeding;
        private static ConfigEntry<int> _feedSearchRange;
        private static ConfigEntry<bool> _needGetCloseToEatTheFeed;
        private static ConfigEntry<AnimalType> _allowToFeedFromContainer;
        private static ConfigEntry<AnimalType> _allowToFeedFromPlayer;

        public static bool IsModuleDisabled => _moduleDisable.Value;
        public static bool EnableAutomaticFeeding => _enableAutomaticFeeding.Value;
        public static float FeedSearchRange => _feedSearchRange.Value;
        public static bool NeedGetCloseToEatTheFeed => _needGetCloseToEatTheFeed.Value;
        public static AnimalType AllowToFeedFromContainer => _allowToFeedFromContainer.Value;
        public static AnimalType AllowToFeedFromPlayer => _allowToFeedFromPlayer.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _moduleDisable = config.Bind("module_disable", false, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_description");
            });
            if (_moduleDisable.Value) return;

            _enableAutomaticFeeding = config.Bind("enable_automatic_feeding", true);
            _feedSearchRange = config.Bind("feed_search_range", 0, (0, 64));
            _needGetCloseToEatTheFeed = config.Bind("need_get_close_to_eat_the_feed", false);
            _allowToFeedFromContainer = config.Bind("allow_to_feed_from_container", AnimalType.Tamed);
            _allowToFeedFromPlayer = config.Bind("allow_to_feed_from_player", AnimalType.None);
        }
    }
}