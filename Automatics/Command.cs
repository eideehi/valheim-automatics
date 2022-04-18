using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

        private static void PrintNames(Terminal.ConsoleEventArgs args)
        {
            var arguments = ParseArgs(args.FullLine);
            if (!arguments.Any())
            {
                args.Context.AddString(ConsoleCommand.SyntaxError("printnames"));
                return;
            }

            Log.Debug(() => $"Print name: {string.Join(", ", arguments)}");

            var filters = arguments.Select(arg =>
                arg.StartsWith("r/", StringComparison.OrdinalIgnoreCase)
                    ? (Regex: true, Value: arg.Substring(2))
                    : (Regex: false, Value: arg))
                .ToList();
            foreach (var (key, value) in from translation in GetAllTranslations()
                     let key = translation.Key.StartsWith("automatics_")
                         ? $"@{translation.Key.Substring(11)}"
                         : $"${translation.Key}"
                     let value = translation.Value
                     where filters.All(filter =>
                         filter.Regex
                             ? Regex.IsMatch(key, filter.Value) ||
                               Regex.IsMatch(value, filter.Value)
                             : key.IndexOf(filter.Value, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               value.IndexOf(filter.Value, StringComparison.OrdinalIgnoreCase) >= 0)
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

        private static List<string> ParseArgs(string line)
        {
            var args = new List<string>();
            var buffer = new StringBuilder();
            var inQuotes = false;
            var escaped = false;

            foreach (var c in line)
            {
                if (c == '\\' && !escaped)
                {
                    escaped = true;
                }
                else if (c == '"' && !escaped)
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ' ' && !inQuotes)
                {
                    args.Add(buffer.ToString());
                    buffer.Clear();
                    escaped = false;
                }
                else
                {
                    buffer.Append(c);
                    escaped = false;
                }
            }

            if (buffer.Length > 0)
            {
                args.Add(buffer.ToString());
            }

            return args.Skip(1).ToList();
        }

        public static void Register()
        {
            ConsoleCommand.Register("automatics", L10N.Localize("@command_automatics_description"), ShowUsage,
                ShowUsageOptions);
            ConsoleCommand.Register("printnames", PrintNames);
        }
    }
}