using System.Collections.Generic;
using System.Linq;
using Automatics.ModUtils;

namespace Automatics
{
    internal static class Command
    {
        private static void ShowUsage(Terminal.ConsoleEventArgs args)
        {
            var filter = args.Length >= 2 ? args[1].ToLower() : "";
            if (string.IsNullOrEmpty(filter))
            {
                args.Context.AddString(ConsoleCommand.Usage("automatics"));
                return;
            }

            var usage = "";
            foreach (var x in ConsoleCommand.GetAllCommands().OrderBy(x => x.Command))
            {
                if (x.IsSecret || !x.IsValid(args.Context)) continue;

                var command = x.Command;
                if (command == filter)
                {
                    usage = ConsoleCommand.Usage(command);
                    break;
                }

                if (string.IsNullOrEmpty(usage) && command.Contains(filter))
                {
                    usage = ConsoleCommand.Usage(command);
                }
            }

            args.Context.AddString(usage ?? L10N.Translate("@command_automatics_command_not_found"));
            args.Context.AddString("");
        }

        private static List<string> ShowUsageOptions()
        {
            return (from x in ConsoleCommand.GetAllCommands()
                where !x.IsSecret && x.Command != "automatics"
                select x.Command).ToList();
        }

        public static void Register()
        {
            ConsoleCommand.Register("automatics", L10N.Localize("@command_automatics_description"), ShowUsage,
                ShowUsageOptions);
        }
    }
}