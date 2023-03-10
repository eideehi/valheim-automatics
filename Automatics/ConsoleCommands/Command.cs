using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using NDesk.Options;

namespace Automatics.ConsoleCommands
{
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

        private bool _showHelp;

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
                    v => _showHelp = v != null);
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
            _showHelp = false;
        }

        protected bool ParseArgs(Terminal.ConsoleEventArgs args)
        {
            ResetOptions();
            try
            {
                extraOptions = options.Parse(ParseArgs(args.FullLine));
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

            if (!_showHelp) return true;

            args.Context.AddString(Help());
            return false;
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