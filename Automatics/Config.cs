using BepInEx.Configuration;
using BepInEx.Logging;

namespace Automatics
{
    public partial class Automatics
    {
        private const int NexusID = 1700;

        private static ConfigEntry<bool> _loggingEnabled;
        private static ConfigEntry<LogLevel> _allowedLogLevel;

        internal static bool LoggingEnabled => _loggingEnabled.Value;
        internal static bool AllowedLogLevel(LogLevel level) => (_allowedLogLevel.Value & level) != 0;

        private static void InitializeConfigurations()
        {
            ModLogger.LogInfo("Start configurations initialization");

            Configuration.ResetOrder();
            Configuration.Bind("hidden", "NexusID", NexusID, initializer: x =>
            {
                x.Browsable = false;
                x.ReadOnly = true;
            });

            Configuration.ResetOrder();
            _loggingEnabled = Configuration.Bind("logging", "logging_enabled", false);
            _allowedLogLevel = Configuration.Bind("logging", "allowed_log_level", LogLevel.All ^ (LogLevel.Debug | LogLevel.Info));

            AutomaticDoor.Config.Initialize();
            AutomaticMapPinning.Config.Initialize();
            AutomaticProcessing.Config.Initialize();
        }
    }
}