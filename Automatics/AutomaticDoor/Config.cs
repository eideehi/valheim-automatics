using BepInEx.Configuration;
using ModUtils;

namespace Automatics.AutomaticDoor
{
    using Door = ValheimDoor.Flag;

    internal static class Config
    {
        private const string Section = "automatic_door";

        private static ConfigEntry<bool> _enableAutomaticDoor;
        private static ConfigEntry<Door> _allowAutomaticDoor;
        private static ConfigEntry<StringList> _allowAutomaticDoorCustom;
        private static ConfigEntry<float> _intervalToOpen;
        private static ConfigEntry<float> _intervalToClose;
        private static ConfigEntry<float> _distanceForAutomaticOpening;
        private static ConfigEntry<float> _distanceForAutomaticClosing;
        private static ConfigEntry<KeyboardShortcut> _automaticDoorEnableDisableToggle;
        private static ConfigEntry<Message> _automaticDoorEnableDisableToggleMessage;

        public static bool EnableAutomaticDoor
        {
            get => _enableAutomaticDoor.Value;
            set => _enableAutomaticDoor.Value = value;
        }

        public static Door AllowAutomaticDoor => _allowAutomaticDoor.Value;
        public static StringList AllowAutomaticDoorCustom => _allowAutomaticDoorCustom.Value;
        public static float IntervalToOpen => _intervalToOpen.Value;
        public static float IntervalToClose => _intervalToClose.Value;
        public static float DistanceForAutomaticOpening => _distanceForAutomaticOpening.Value;
        public static float DistanceForAutomaticClosing => _distanceForAutomaticClosing.Value;
        public static KeyboardShortcut AutomaticDoorEnableDisableToggle => _automaticDoorEnableDisableToggle.Value;
        public static Message AutomaticDoorEnableDisableToggleMessage => _automaticDoorEnableDisableToggleMessage.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _enableAutomaticDoor = config.Bind("enable_automatic_door", true);
            _allowAutomaticDoor = config.Bind("allow_automatic_door", Door.All ^ Door.WoodShutter);
            _allowAutomaticDoorCustom = config.Bind("allow_automatic_door_custom", new StringList());
            _intervalToOpen = config.Bind("interval_to_open", 0.1f, (0f, 8f));
            _intervalToClose = config.Bind("interval_to_close", 0.1f, (0f, 8f));
            _distanceForAutomaticOpening = config.Bind("distance_for_automatic_opening", 2.5f, (1f, 8f));
            _distanceForAutomaticClosing = config.Bind("distance_for_automatic_closing", 2.5f, (1f, 8f));
            _automaticDoorEnableDisableToggle = config.Bind("automatic_door_enable_disable_toggle", new KeyboardShortcut());
            _automaticDoorEnableDisableToggleMessage = config.Bind("automatic_door_enable_disable_toggle_message", Message.Center);

            _intervalToOpen.SettingChanged += (_, __) => { AutomaticDoor.ChangeInterval(true); };
            _intervalToClose.SettingChanged += (_, __) => { AutomaticDoor.ChangeInterval(false); };
        }
    }
}