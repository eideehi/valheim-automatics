using BepInEx.Configuration;
using BepInEx.Logging;
using ModUtils;

namespace Automatics
{
    internal static class Config
    {
        private const int NexusID = 1700;

        private static ConfigEntry<string> _resourcesDirectory;

        private static ConfigEntry<bool> _logEnabled;
        private static ConfigEntry<LogLevel> _allowedLogLevel;

        public static string ResourcesDirectory => _resourcesDirectory.Value;

        public static bool LogEnabled => _logEnabled.Value;
        public static LogLevel AllowedLogLevel => _allowedLogLevel.Value;

        public static bool Initialized { get; private set; }
        public static Configuration Instance { get; private set; }

        public static bool IsLogEnabled(LogLevel level)
        {
            return !Initialized || (LogEnabled && (AllowedLogLevel & level) != 0);
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
            _resourcesDirectory = Instance.Bind("resources_directory", "");

            Initialized = true;
        }
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