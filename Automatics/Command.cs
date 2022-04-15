using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Automatics.ModUtils;

namespace Automatics
{
    internal static class Command
    {
        private static readonly char[] ArgsSeparator;

        static Command()
        {
            ArgsSeparator = new[] { ' ' };
        }

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

        private static void PrintNames(Terminal.ConsoleEventArgs args)
        {
            var split = args.FullLine.Split(ArgsSeparator, 2);
            if (split.Length != 2 || string.IsNullOrEmpty(split[1]))
            {
                args.Context.AddString(ConsoleCommand.SyntaxError("printnames"));
                return;
            }

            var regex = split[1].StartsWith("r/", true, CultureInfo.CurrentCulture);
            var arg = regex ? split[1].Substring(2) : split[1].ToLower();

            Log.Debug(() => $"Print name: [arg: {arg}, regex: {regex}]");

            foreach (var (key, value) in from translation in GetAllTranslations()
                     let key = translation.Key.StartsWith("automatics_")
                         ? $"@{translation.Key.Substring(11)}"
                         : $"${translation.Key}"
                     let value = translation.Value
                     where Regex.IsMatch(key, @"^[$@](animal|enemy|item|location|piece)_") &&
                           (regex
                               ? Regex.IsMatch(key, arg) || Regex.IsMatch(value, arg)
                               : key.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 value.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                     select (key, value))
            {
                var text = L10N.LocalizeWithoutTranslateWords("@command_printnames_result_format", key, value);
                args.Context.AddString(text);
                Log.Debug(() => $"  {text}");
            }

            args.Context.AddString("");
        }

        private static Dictionary<string, string> GetAllTranslations()
        {
            return Reflection.GetField<Dictionary<string, string>>(Localization.instance, "m_translations");
        }

        public static void Register()
        {
            ConsoleCommand.Register("automatics", L10N.Localize("@command_automatics_description"), ShowUsage,
                ShowUsageOptions);
            ConsoleCommand.Register("printnames", PrintNames);
        }
    }
}