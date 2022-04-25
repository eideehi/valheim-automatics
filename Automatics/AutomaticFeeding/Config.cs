using Automatics.ModUtils;
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
            Configuration.ChangeSection(Section);
            _enableAutomaticFeeding = Configuration.Bind("enable_automatic_feeding", true);
            _feedSearchRange = Configuration.Bind("feed_search_range", 0, (0, 64));
            _needGetCloseToEatTheFeed = Configuration.Bind("need_get_close_to_eat_the_feed", false);
            _allowToFeedFromContainer = Configuration.Bind("allow_to_feed_from_container", Animal.Tamed);
        }
    }
}