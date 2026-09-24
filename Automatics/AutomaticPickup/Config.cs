using BepInEx.Configuration;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    internal static class Config
    {
        private const string Section = "automatic_pickup";

        private static ConfigEntry<AutomaticsModule> _module;
        private static ConfigEntry<float> _automaticPickupRange;
        private static ConfigEntry<float> _automaticPickupInterval;
        private static ConfigEntry<KeyboardShortcut> _pickupAllNearbyKey;

        /// <summary>
        ///     The toggle the module registry follows. It is exposed so the registry can read the
        ///     user's choice and subscribe to its changes; nothing writes it back (R1.3b), and no
        ///     hot path reads it either - those read the applied state on <see cref="Module" />
        ///     instead (R1.5).
        /// </summary>
        public static ConfigEntry<AutomaticsModule> ModuleEntry => _module;

        public static float AutomaticPickupRange => _automaticPickupRange.Value;
        public static float AutomaticPickupInterval => _automaticPickupInterval.Value;
        public static KeyboardShortcut PickupAllNearbyKey => _pickupAllNearbyKey.Value;
        public static ConfigEntry<KeyboardShortcut> PickupAllNearbyKeyEntry => _pickupAllNearbyKey;
        public static ConfigEntry<float> AutomaticPickupIntervalEntry => _automaticPickupInterval;

        public static void Initialize()
        {
            var config = global::Automatics.Config.Instance;

            config.ChangeSection(Section);
            _module = config.Bind("module", AutomaticsModule.Disabled, initializer: x =>
            {
                x.DispName =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_name");
                x.Description =
                    Automatics.L10N.Translate("@config_common_disable_module_runtime_description");
            });

            // Bound unconditionally (R5.1). The module can be turned on while the game runs, so
            // the settings it needs have to exist whether it starts on or off; the early return
            // that used to sit here left them null and made a runtime enable impossible. Nothing
            // below has a side effect beyond the bind itself - this section has no
            // SettingChanged bridge - so binding a disabled module costs nothing but the lines
            // it writes to the config file.
            _automaticPickupRange = config.Bind("automatic_pickup_range", 4f, (1f, 64f));
            _automaticPickupInterval = config.Bind("automatic_pickup_interval", 0.5f, (0f, 4f));
            // Bound by default. A config that bound the key when binding it still turned periodic
            // pickup off has had its interval set to 0 once (ConfigMigration.DecouplePickupKey).
            _pickupAllNearbyKey = config.Bind("pickup_all_nearby_key",
                new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift));
        }
    }
}