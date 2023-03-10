using System;
using NDesk.Options;

namespace Automatics.ConsoleCommands
{
    internal class ShowCommands : Command
    {
        private string _include;
        private string _exclude;
        private bool _verbose;

        public ShowCommands() : base("automatics")
        {
        }

        protected override OptionSet CreateOptionSet()
        {
            return new OptionSet
            {
                {
                    "i|include=",
                    Automatics.L10N.Translate("@command_automatics_option_include_description"),
                    x => _include = x
                },
                {
                    "e|exclude=",
                    Automatics.L10N.Translate("@command_automatics_option_exclude_description"),
                    x => _exclude = x
                },
                {
                    "v|verbose",
                    Automatics.L10N.Translate("@command_automatics_option_verbose_description"),
                    x => _verbose = x != null
                }
            };
        }

        protected override void ResetOptions()
        {
            base.ResetOptions();
            _include = "";
            _exclude = "";
            _verbose = false;
        }

        protected override void CommandAction(Terminal.ConsoleEventArgs args)
        {
            if (!ParseArgs(args)) return;

            foreach (var (cmd, instance) in GetAllCommands())
            {
                if (!string.IsNullOrEmpty(_include) &&
                    cmd.IndexOf(_include, StringComparison.OrdinalIgnoreCase) == -1) continue;
                if (!string.IsNullOrEmpty(_exclude) &&
                    cmd.IndexOf(_exclude, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                args.Context.AddString(instance.Print(_verbose));
            }
        }
    }
}