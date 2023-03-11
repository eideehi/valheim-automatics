using BepInEx.Configuration;
using ModUtils;

namespace Automatics.AutomaticDoor
{
    internal static class Config
    {
        private const string Section = "automatic_door";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<bool> _moduleDisabled;
        private static ConfigEntry<bool> _enableAutomaticDoor;
        private static ConfigEntry<StringList> _allowAutomaticDoor;
        private static ConfigEntry<float> _intervalToOpen;
        private static ConfigEntry<float> _intervalToClose;
        private static ConfigEntry<float> _distanceForAutomaticOpening;
        private static ConfigEntry<float> _distanceForAutomaticClosing;
        private static ConfigEntry<Message> _automaticDoorEnableDisableToggleMessage;
        private static ConfigEntry<KeyboardShortcut> _automaticDoorEnableDisableToggle;

        public static bool ModuleDisabled => _module.Value == AutomaticsModule.Disabled;
        public static bool IsModuleDisabled => _moduleDisabled.Value;

        public static bool EnableAutomaticDoor
        {
            get => _enableAutomaticDoor.Value;
            set => _enableAutomaticDoor.Value = value;
        }

        public static StringList AllowAutomaticDoor => _allowAutomaticDoor.Value;
        public static float IntervalToOpen => _intervalToOpen.Value;
        public static float IntervalToClose => _intervalToClose.Value;
        public static float DistanceForAutomaticOpening => _distanceForAutomaticOpening.Value;
        public static float DistanceForAutomaticClosing => _distanceForAutomaticClosing.Value;

        public static Message AutomaticDoorEnableDisableToggleMessage =>
            _automaticDoorEnableDisableToggleMessage.Value;

        public static KeyboardShortcut AutomaticDoorEnableDisableToggle =>
            _automaticDoorEnableDisableToggle.Value;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _moduleDisabled = config.Bind("disable_module", false, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_old_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_description");
                x.Browsable = false;
            });
            _module = config.Bind("module", AutomaticsModule.Enabled, initializer: x =>
            {
                x.DispName = Automatics.L10N.Translate("@config_common_disable_module_name");
                x.Description = Automatics.L10N.Translate("@config_common_disable_module_description");
            });
            if (_moduleDisabled.Value) _module.Value = AutomaticsModule.Disabled;
            _module.SettingChanged += (_, __) =>
            {
                _moduleDisabled.Value = _module.Value == AutomaticsModule.Disabled;
            };
            if (_moduleDisabled.Value || _module.Value == AutomaticsModule.Disabled) return;

            _enableAutomaticDoor = config.Bind("enable_automatic_door", true);
            _allowAutomaticDoor = config.BindValheimObjectList("allow_automatic_door", Globals.Door, excludes: new[] { "WoodShutter" });
            _intervalToOpen = config.Bind("interval_to_open", 0.1f, (0f, 8f));
            _intervalToClose = config.Bind("interval_to_close", 0.1f, (0f, 8f));
            _distanceForAutomaticOpening = config.Bind("distance_for_automatic_opening", 2.5f, (1f, 8f));
            _distanceForAutomaticClosing = config.Bind("distance_for_automatic_closing", 2.5f, (1f, 8f));
            _automaticDoorEnableDisableToggleMessage = config.Bind("automatic_door_enable_disable_toggle_message", Message.Center);
            _automaticDoorEnableDisableToggle = config.Bind("automatic_door_enable_disable_toggle", new KeyboardShortcut());

            config.ChangeSection("general", 64);
            config.BindCustomValheimObject("custom_door", Globals.Door);
        }
    }
}