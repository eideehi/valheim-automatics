using System;
using BepInEx.Logging;

namespace Automatics.ModUtils
{
    internal static class Log
    {
        private static bool CanLogging(LogLevel level)
        {
            return Config.EnableLogging && (Config.LogLevelToAllowLogging & level) != 0;
        }

        public static void Fatal(Func<string> message)
        {
            if (CanLogging(LogLevel.Fatal))
                Automatics.ModLogger.LogFatal(message.Invoke());
        }

        public static void Error(Func<string> message)
        {
            if (CanLogging(LogLevel.Error))
                Automatics.ModLogger.LogError(message.Invoke());
        }

        public static void Warning(Func<string> message)
        {
            if (CanLogging(LogLevel.Warning))
                Automatics.ModLogger.LogWarning(message.Invoke());
        }

        public static void Info(Func<string> message)
        {
            if (CanLogging(LogLevel.Info))
                Automatics.ModLogger.LogInfo(message.Invoke());
        }

        public static void Message(Func<string> message)
        {
            if (CanLogging(LogLevel.Message))
                Automatics.ModLogger.LogMessage(message.Invoke());
        }

        public static void Debug(Func<string> message)
        {
            if (CanLogging(LogLevel.Debug))
                Automatics.ModLogger.LogDebug(message.Invoke());
        }
    }
}