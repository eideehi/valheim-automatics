using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Automatics.ModUtils
{
    public static class ConsoleCommand
    {
        private static readonly Dictionary<string, Terminal.ConsoleCommand> Commands;

        static ConsoleCommand()
        {
            Commands = new Dictionary<string, Terminal.ConsoleCommand>();
        }

        [Conditional("DEBUG")]
        private static void PrintCommand(string command)
        {
            Automatics.ModLogger.LogDebug($"[COMMAND]: ### {command}");
            foreach (var line in Usage(command).Split('\n'))
            {
                Automatics.ModLogger.LogDebug($"[COMMAND]: {line}");
            }
        }

        public static IEnumerable<Terminal.ConsoleCommand> GetAllCommands()
        {
            return Commands.Values.ToList();
        }

        public static void Register(string command, string description, Terminal.ConsoleEvent action, Terminal.ConsoleOptionsFetcher optionsFetcher = null, bool isCheat = false, bool isNetwork = false, bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false)
        {
            var lowerCommand = command.ToLower();
            Commands[lowerCommand] = new Terminal.ConsoleCommand(lowerCommand, description, action, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild, optionsFetcher);

            PrintCommand(command);
        }

        public static void Register(string command, Terminal.ConsoleEvent action, Terminal.ConsoleOptionsFetcher optionsFetcher = null, bool isCheat = false, bool isNetwork = false, bool onlyServer = false, bool isSecret = false, bool allowInDevBuild = false)
        {
            Register(command, Description(command.ToLower()), action, optionsFetcher, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild);
        }

        public static string Usage(string command)
        {
            return L10N.Localize($"@command_{command}_usage");
        }

        public static string SyntaxError(string command)
        {
            return L10N.Localize($"@command_syntax_error_format", command, Usage(command));
        }

        public static string ArgumentError(string command, string arg)
        {
            return L10N.Localize($"@command_argument_error_format", arg, Usage(command));
        }

        private static string Description(string command)
        {
            return L10N.Localize($"@command_description_format", $"@command_{command}_description", command);
        }
    }
}