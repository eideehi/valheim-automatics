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
        private static ConfigEntry<KeyboardShortcut> _toggleAutomaticDoorEnabledKey;

        public static bool AutomaticDoorEnabled
        {
            get => _automaticDoorEnabled.Value;
            set => _automaticDoorEnabled.Value = value;
        }

        public static float IntervalToOpen => _intervalToOpen.Value;
        public static float IntervalToClose => _intervalToClose.Value;
        public static float PlayerSearchRadiusToOpen => _playerSearchRadiusToOpen.Value;
        public static float PlayerSearchRadiusToClose => _playerSearchRadiusToClose.Value;
        public static KeyboardShortcut ToggleAutomaticDoorEnabledKey => _toggleAutomaticDoorEnabledKey.Value;

        public static void Initialize()
        {
            Configuration.ChangeSection(Section);
            _automaticDoorEnabled = Configuration.Bind("automatic_door_enabled", true);
            _intervalToOpen = Configuration.Bind("interval_to_open", 0.1f, (0.1f, 8f));
            _intervalToClose = Configuration.Bind("interval_to_close", 0.1f, (0.1f, 8f));
            _playerSearchRadiusToOpen = Configuration.Bind("player_search_radius_to_open", 2.5f, (1f, 8f));
            _playerSearchRadiusToClose = Configuration.Bind("player_search_radius_to_close", 2.5f, (1f, 8f));
            _toggleAutomaticDoorEnabledKey = Configuration.Bind("toggle_automatic_door_enabled_key", new KeyboardShortcut());

            _intervalToOpen.SettingChanged += (sender, args) => { AutomaticDoor.ChangeInterval(true); };
            _intervalToClose.SettingChanged += (sender, args) => { AutomaticDoor.ChangeInterval(false); };
        }
    }
}