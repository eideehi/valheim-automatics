using Automatics.ModUtils;
using BepInEx.Configuration;

namespace Automatics.AutomaticFeeding
{
    internal static class Config
    {
        private const string Section = "automatic_feeding";

        private static ConfigEntry<bool> _automaticFeedingEnabled;
        private static ConfigEntry<int> _feedSearchRange;
        private static ConfigEntry<bool> _needCloseToEatTheFeed;
        private static ConfigEntry<Animal> _allowToFeedFromContainer;

        public static bool AutomaticFeedingEnabled => _automaticFeedingEnabled.Value;
        public static float FeedSearchRange => _feedSearchRange.Value;
        public static bool NeedCloseToEatTheFeed => _needCloseToEatTheFeed.Value;
        public static Animal AllowToFeedFromContainer => _allowToFeedFromContainer.Value;

        public static void Initialize()
        {
            Configuration.ChangeSection(Section);
            _automaticFeedingEnabled = Configuration.Bind("automatic_feeding_enabled", true);
            _feedSearchRange = Configuration.Bind("feed_search_range", -1, (-1, 64));
            _needCloseToEatTheFeed = Configuration.Bind("need_close_to_eat_the_feed", false);
            _allowToFeedFromContainer = Configuration.Bind("allow_to_feed_from_container", Animal.Tamed);
        }
    }
}