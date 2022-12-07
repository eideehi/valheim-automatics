using BepInEx.Configuration;

namespace Automatics.AutomaticFeeding
{
    internal static class Config
    {
        private const string Section = "automatic_feeding";

        private static ConfigEntry<bool> _enableAutomaticFeeding;
        private static ConfigEntry<int> _feedSearchRange;
        private static ConfigEntry<bool> _needGetCloseToEatTheFeed;
        private static ConfigEntry<Animal> _allowToFeedFromContainer;

        public static bool EnableAutomaticFeeding => _enableAutomaticFeeding.Value;
        public static float FeedSearchRange => _feedSearchRange.Value;
        public static bool NeedGetCloseToEatTheFeed => _needGetCloseToEatTheFeed.Value;
        public static Animal AllowToFeedFromContainer => _allowToFeedFromContainer.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _enableAutomaticFeeding = config.Bind("enable_automatic_feeding", true);
            _feedSearchRange = config.Bind("feed_search_range", 0, (0, 64));
            _needGetCloseToEatTheFeed = config.Bind("need_get_close_to_eat_the_feed", false);
            _allowToFeedFromContainer = config.Bind("allow_to_feed_from_container", Animal.Tamed);
        }
    }
}