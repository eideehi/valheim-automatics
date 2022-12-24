using BepInEx.Configuration;

namespace Automatics.AutomaticPickup
{
    internal static class Config
    {
        private const string Section = "automatic_pickup";

        private static ConfigEntry<bool> _moduleDisable;
        private static ConfigEntry<float> _automaticPickupRange;
        private static ConfigEntry<float> _automaticPickupInterval;
        private static ConfigEntry<KeyboardShortcut> _pickupAllNearbyKey;

        public static bool IsModuleDisabled => _moduleDisable.Value;

        public static float AutomaticPickupRange => _automaticPickupRange.Value;
        public static float AutomaticPickupInterval => _automaticPickupInterval.Value;
        public static KeyboardShortcut PickupAllNearbyKey => _pickupAllNearbyKey.Value;

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

            _automaticPickupRange = config.Bind("automatic_pickup_range", 4f, (1f, 64f));
            _automaticPickupInterval = config.Bind("automatic_pickup_interval", 0.5f, (0f, 4f));
            _pickupAllNearbyKey = config.Bind("pickup_all_nearby_key", new KeyboardShortcut());
        }
    }
}