using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ModUtils;
using NDesk.Options;

namespace Automatics
{
    internal static class Commands
    {
        private class ShowCommands : Command
        {
            private bool _showAll;
            private bool _verbose;

            public ShowCommands() : base("automatics")
            {
                HaveExtraOption = true;
            }

            protected override OptionSet CreateOptionSet()
            {
                return new OptionSet
                {
                    {
                        "a|all",
                        Automatics.L10N.Translate("@command_automatics_option_all_description"),
                        v => _showAll = v != null
                    },
                    {
                        "v|verbose",
                        Automatics.L10N.Translate("@command_automatics_option_verbose_description"),
                        v => _verbose = v != null
                    }
                };
            }

            protected override void ResetOptions()
            {
                base.ResetOptions();
                _showAll = false;
                _verbose = false;
            }

            protected override void CommandAction(Terminal.ConsoleEventArgs args)
            {
                if (!ParseArgs(args)) return;

                if (showHelp)
                {
                    args.Context.AddString(Help());
                    return;
                }

                if (!extraOptions.Any())
                {
                    args.Context.AddString(Print(_verbose));
                    return;
                }

                foreach (var (cmd, instance) in GetAllCommands())
                    if (extraOptions.Any(x => cmd.Contains(x)))
                    {
                        args.Context.AddString(instance.Print(_verbose));
                        if (!_showAll) break;
                    }
            }
        }

        private class PrintNames : Command
        {
            public PrintNames() : base("printnames")
            {
                HaveExtraOption = true;
                HaveExtraDescription = true;
            }

            protected override void CommandAction(Terminal.ConsoleEventArgs args)
            {
                if (!ParseArgs(args)) return;

                if (showHelp)
                {
                    args.Context.AddString(Help());
                    return;
                }

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

        public static void Register()
        {
            new ShowCommands().Register();
            new PrintNames().Register();
        }
    }

    internal abstract class Command
    {
        private static readonly Dictionary<string, Command> Commands;
        private static readonly List<string> EmptyList;

        static Command()
        {
            Commands = new Dictionary<string, Command>();
            EmptyList = new List<string>();
        }

        private readonly Lazy<OptionSet> _optionsLazy;

        protected readonly string command;

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        protected OptionSet options => _optionsLazy.Value;

        protected List<string> extraOptions;
        protected bool showHelp;

        protected bool HaveExtraOption { get; set; }
        protected bool HaveExtraDescription { get; set; }

        protected Command(string command)
        {
            this.command = command.ToLower();
            _optionsLazy = new Lazy<OptionSet>(() =>
            {
                var optionSet = CreateOptionSet();
                optionSet.Add("h|help",
                    Automatics.L10N.Translate("@command_common_help_description"),
                    v => showHelp = v != null);
                return optionSet;
            });
            extraOptions = new List<string>();
        }

        private static IEnumerable<string> ParseArgs(string line)
        {
            var args = new List<string>();
            var buffer = new StringBuilder();
            var inQuotes = false;
            var escaped = false;

            foreach (var c in line)
                switch (c)
                {
                    case '\\' when !escaped:
                        escaped = true;
                        continue;

                    case '"' when !escaped:
                        inQuotes = !inQuotes;
                        continue;

                    case ' ' when !inQuotes:
                        args.Add(buffer.ToString());
                        buffer.Clear();
                        escaped = false;
                        continue;

                    default:
                        buffer.Append(c);
                        escaped = false;
                        continue;
                }

            if (buffer.Length > 0) args.Add(buffer.ToString());

            return args.Skip(1).ToList();
        }

        protected static IEnumerable<(string Command, Command Instance)> GetAllCommands()
        {
            return Commands.Select(x => (x.Key, x.Value));
        }

        [Conditional("DEBUG")]
        private void DebugLog()
        {
            Automatics.Logger.Debug($"[COMMAND]: ### {command}");
            foreach (var line in Help().Split(new[] { '\n' }, StringSplitOptions.None))
                Automatics.Logger.Debug($"[COMMAND]: {line.Trim()}");
        }

        protected abstract void CommandAction(Terminal.ConsoleEventArgs args);

        protected virtual OptionSet CreateOptionSet()
        {
            return new OptionSet();
        }

        protected virtual List<string> GetSuggestions()
        {
            return EmptyList;
        }

        protected virtual void ResetOptions()
        {
            showHelp = false;
        }

        protected bool ParseArgs(Terminal.ConsoleEventArgs args)
        {
            ResetOptions();
            try
            {
                extraOptions = options.Parse(ParseArgs(args.FullLine));
                return true;
            }
            catch (OptionException e)
            {
                args.Context.AddString($"{command}:");
                args.Context.AddString(e.Message);
                args.Context.AddString(
                    Automatics.L10N.LocalizeTextOnly("@command_common_option_parse_error",
                        command));
                return false;
            }
        }

        protected string Usage()
        {
            return Automatics.L10N.Translate($"@command_{command}_usage");
        }

        protected string Description()
        {
            return Automatics.L10N.Translate($"@command_{command}_description");
        }

        protected string ExtraOption()
        {
            return Automatics.L10N.Translate($"@command_{command}_extra_option");
        }

        protected string ExtraDescription()
        {
            return Automatics.L10N.Translate($"@command_{command}_extra_description");
        }

        protected string Help()
        {
            var writer = new StringWriter();
            writer.WriteLine(Usage());
            writer.WriteLine(Description());
            if (HaveExtraOption)
            {
                writer.WriteLine();
                writer.WriteLine(ExtraOption());
            }
            writer.WriteLine();
            writer.WriteLine(Automatics.L10N.Translate("@command_common_help_options_label"));
            options.WriteOptionDescriptions(writer);
            if (HaveExtraDescription)
            {
                writer.WriteLine();
                writer.WriteLine(ExtraDescription());
            }
            return writer.ToString();
        }

        public string Print(bool verbose)
        {
            return !verbose ? $"\"{command}\" {Description()}" : Help();
        }

        public void Register(bool isCheat = false, bool isNetwork = false, bool onlyServer = false,
            bool isSecret = false, bool allowInDevBuild = false)
        {
            Commands[command] = this;
            _ = new Terminal.ConsoleCommand(command, Description(),
                CommandAction, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild,
                GetSuggestions);
            DebugLog();
        }
    }
}