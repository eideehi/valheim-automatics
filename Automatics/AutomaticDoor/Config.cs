using Automatics.ModUtils;
using BepInEx.Configuration;

namespace Automatics.AutomaticDoor
{
    internal static class Config
    {
        private const string Section = "automatic_door";

        private static ConfigEntry<bool> _automaticDoorEnabled;
        private static ConfigEntry<float> _intervalToOpen;
        private static ConfigEntry<float> _intervalToClose;
        private static ConfigEntry<float> _playerSearchRadiusToOpen;
        private static ConfigEntry<float> _playerSearchRadiusToClose;

        public static bool AutomaticDoorEnabled => _automaticDoorEnabled.Value;
        public static float IntervalToOpen => _intervalToOpen.Value;
        public static float IntervalToClose => _intervalToClose.Value;
        public static float PlayerSearchRadiusToOpen => _playerSearchRadiusToOpen.Value;
        public static float PlayerSearchRadiusToClose => _playerSearchRadiusToClose.Value;

        public static void Initialize()
        {
            Configuration.ResetOrder();
            _automaticDoorEnabled = Configuration.Bind(Section, "automatic_door_enabled", true);
            _intervalToOpen = Configuration.Bind(Section, "interval_to_open", 0.1f, (0.1f, 8f));
            _intervalToClose = Configuration.Bind(Section, "interval_to_close", 0.1f, (0.1f, 8f));
            _playerSearchRadiusToOpen = Configuration.Bind(Section, "player_search_radius_to_open", 2.5f, (1f, 8f));
            _playerSearchRadiusToClose = Configuration.Bind(Section, "player_search_radius_to_close", 2.5f, (1f, 8f));

            _intervalToOpen.SettingChanged += (sender, args) => { AutomaticDoor.ChangeInterval(true); };
            _intervalToClose.SettingChanged += (sender, args) => { AutomaticDoor.ChangeInterval(false); };
        }
    }
}