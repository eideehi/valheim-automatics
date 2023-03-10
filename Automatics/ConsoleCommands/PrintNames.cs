using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ModUtils;

namespace Automatics.ConsoleCommands
{
    internal class PrintNames : Command
    {
        public PrintNames() : base("printnames")
        {
            HaveExtraOption = true;
            HaveExtraDescription = true;
        }

        protected override void CommandAction(Terminal.ConsoleEventArgs args)
        {
            if (!ParseArgs(args)) return;

            Automatics.Logger.Message(() => $"Command exec: {args.FullLine}");

            var filters = extraOptions.Select(arg =>
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
                             : key.IndexOf(filter.Value, StringComparison.OrdinalIgnoreCase) >=
                               0 ||
                               value.IndexOf(filter.Value,
                                   StringComparison.OrdinalIgnoreCase) >= 0)
                     select (key, value))
            {
                var text =
                    Automatics.L10N.LocalizeTextOnly("@command_printnames_result_format", key,
                        value);
                args.Context.AddString(text);
                Automatics.Logger.Message(() => $"  {text}");
            }

            args.Context.AddString("");

            Dictionary<string, string> GetAllTranslations()
            {
                return Reflections.GetField<Dictionary<string, string>>(Localization.instance,
                    "m_translations");
            }
        }
    }
}