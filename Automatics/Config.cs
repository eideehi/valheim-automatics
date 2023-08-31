using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using BepInEx.Configuration;
using BepInEx.Logging;
using ModUtils;

namespace Automatics
{
    internal static class Config
    {
        private const int NexusID = 1700;

        private static ConfigEntry<bool> _logEnabled;
        private static ConfigEntry<LogLevel> _allowedLogLevel;

        public static bool LogEnabled => _logEnabled.Value;
        public static LogLevel AllowedLogLevel => _allowedLogLevel.Value;

        public static bool Initialized { get; private set; }
        public static Configuration Instance { get; private set; }

        public static bool IsLogEnabled(LogLevel level)
        {
            return !Initialized || (LogEnabled && (AllowedLogLevel & level) != 0);
        }

        public static ConfigEntry<List<ObjectElement>> BindCustomValheimObject(
            this Configuration config, string key, ValheimObject obj)
        {
            var entry = config.Bind(key, new List<ObjectElement>());
            if (entry.Value.Any())
                obj.RegisterCustom(entry.Value);

            entry.SettingChanged += (_, __) => obj.RegisterCustom(entry.Value);
            return entry;
        }

        public static ConfigEntry<StringList> BindValheimObjectList(this Configuration config,
            string key, ValheimObject obj, IEnumerable<string> defaults = null,
            IEnumerable<string> includes = null, IEnumerable<string> excludes = null)
        {
            var defaultValue = new List<string>();
            defaultValue.AddRange(defaults ?? obj.GetAllElements().Select(x => x.identifier));

            if (!(includes is null))
                defaultValue.RemoveAll(x => !includes.Contains(x));

            if (!(excludes is null))
                defaultValue.RemoveAll(excludes.Contains);

            return config.Bind(key, new StringList(defaultValue), initializer: x =>
            {
                x.CustomDrawer = ConfigurationCustomDrawer.MultiSelect(
                    () => obj.GetAllElements().Select(y => y.identifier),
                    identifier => obj.GetName(identifier, out var name)
                        ? Automatics.L10N.TranslateInternalName(name)
                        : "INTERNAL ERROR");
            });
        }

        public static void Initialize(ConfigFile config)
        {
            if (Initialized) return;

            Instance = new Configuration(config, Automatics.L10N);

            Instance.ChangeSection("hidden");
            Instance.Bind("NexusID", NexusID, initializer: x =>
            {
                x.Browsable = false;
                x.ReadOnly = true;
            });

            Instance.ChangeSection("system");
            _logEnabled = Instance.Bind("enable_logging", false);
            _allowedLogLevel = Instance.Bind("log_level_to_allow_logging",
                LogLevel.All ^ (LogLevel.Debug | LogLevel.Info));

            Instance.ChangeSection("general");
            Instance.BindCustomValheimObject("custom_animal", ValheimObject.Animal);
            Instance.BindCustomValheimObject("custom_dungeon", ValheimObject.Dungeon);
            Instance.BindCustomValheimObject("custom_flora", ValheimObject.Flora);
            Instance.BindCustomValheimObject("custom_mineral", ValheimObject.Mineral);
            Instance.BindCustomValheimObject("custom_monster", ValheimObject.Monster);
            Instance.BindCustomValheimObject("custom_spawner", ValheimObject.Spawner);
            Instance.BindCustomValheimObject("custom_spot", ValheimObject.Spot);

            Initialized = true;
        }
    }

    public enum AutomaticsModule
    {
        [LocalizedDescription(Automatics.L10NPrefix, "@message_module_enabled")]
        Enabled,

        [LocalizedDescription(Automatics.L10NPrefix, "@message_module_disabled")]
        Disabled,
    }

    public enum Message
    {
        [LocalizedDescription(Automatics.L10NPrefix, "@message_none")]
        None,

        [LocalizedDescription(Automatics.L10NPrefix, "@message_center")]
        Center,

        [LocalizedDescription(Automatics.L10NPrefix, "@message_top_left")]
        TopLeft
    }
}