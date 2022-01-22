using HarmonyLib;

namespace Automatics
{
    public static class ConsoleCommand
    {
        public static void Register(string command, Terminal.ConsoleEvent action,
            bool isCheat = false, bool isNetwork = false, bool onlyServer = false,
            bool isSecret = false, bool allowInDevBuild = false,
            Terminal.ConsoleOptionsFetcher optionsFetcher = null)
        {
            Automatics.ModLogger.LogDebug($"Register command: {command}");
            _ = new Terminal.ConsoleCommand(command,
                L10N.Localize($"@command_{command}_option @command_{command}_description"),
                action, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild, optionsFetcher);
        }

        public static string SyntaxError(string command)
        {
            return L10N.Localize($"@command_syntax_error {command} @command_{command}_option");
        }

        public static string ArgumentError(string arg)
        {
            return L10N.Localize($"@command_argument_error {arg}");
        }
    }

    [HarmonyPatch]
    internal static partial class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Terminal), "Awake")]
        private static void TerminalAwakePostfix(Terminal __instance)
        {
            Automatics.ModLogger.LogInfo("Start registering console commands");
            AutomaticMapPinning.Command.Register();
        }
    }
}