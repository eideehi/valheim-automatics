using Automatics.ModUtils;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Automatics
{
    internal static class Config
    {
        private const int NexusID = 1700;

        private static ConfigEntry<bool> _enableLogging;
        private static ConfigEntry<LogLevel> _logLevelToAllowLogging;
        private static ConfigEntry<string> _resourcesDirectory;

        internal static bool EnableLogging => _enableLogging.Value;
        internal static LogLevel LogLevelToAllowLogging => _logLevelToAllowLogging.Value;
        internal static string ResourcesDirectory => _resourcesDirectory.Value;

        public static void Initialize()
        {
            Configuration.ChangeSection("hidden");
            Configuration.Bind("NexusID", NexusID, initializer: x =>
            {
                x.Browsable = false;
                x.ReadOnly = true;
            });

            Configuration.ChangeSection("system");
            _enableLogging = Configuration.Bind("enable_logging", false);
            _logLevelToAllowLogging = Configuration.Bind("log_level_to_allow_logging", LogLevel.All ^ (LogLevel.Debug | LogLevel.Info));
            _resourcesDirectory = Configuration.Bind("resources_directory", "");
        }
    }
}