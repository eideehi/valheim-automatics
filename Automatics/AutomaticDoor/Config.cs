using BepInEx.Configuration;

namespace Automatics.AutomaticDoor
{
    internal static class Config
    {
        private const string Section = "automatic_door";

        private static ConfigEntry<bool> _automaticDoorEnabled;
        private static ConfigEntry<float> _updateInterval;
        private static ConfigEntry<float> _playerSearchRadius;

        public static bool AutomaticDoorEnabled => _automaticDoorEnabled.Value;
        public static float UpdateInterval => _updateInterval.Value;
        public static float PlayerSearchRadius => _playerSearchRadius.Value;

        public static void Initialize()
        {
            Configuration.ResetOrder();
            _automaticDoorEnabled = Configuration.Bind(Section, "automatic_door_enabled", true);
            _updateInterval = Configuration.Bind(Section, "update_interval", 0.1f, (0.1f, 8f));
            _playerSearchRadius = Configuration.Bind(Section, "player_search_radius", 2.5f, (1f, 4f));

            _updateInterval.SettingChanged += (sender, args) =>
            {
                AutomaticDoor.ChangeInterval(_updateInterval.Value);
            };
        }
    }
}