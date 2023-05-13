using System;
using System.Collections.Generic;
using System.Linq;
using ModUtils;
using NDesk.Options;
using UnityEngine;

namespace Automatics.ConsoleCommands
{
    internal class RemoveMapPins : Command
    {
        private int _radius;
        private string _include;
        private string _exclude;
        private bool _allowNonDuplicatePins;

        public RemoveMapPins() : base("removemappins")
        {
        }

        protected override OptionSet CreateOptionSet()
        {
            return new OptionSet
            {
                {
                    "r|radius=",
                    Automatics.L10N.Translate("@command_removemappins_option_radius_description"),
                    (int x) => _radius = x
                },
                {
                    "i|include=",
                    Automatics.L10N.Translate("@command_removemappins_option_include_description"),
                    x => _include = x
                },
                {
                    "e|exclude=",
                    Automatics.L10N.Translate("@command_removemappins_option_exclude_description"),
                    x => _exclude = x
                },
                {
                    "a|allow_non_duplicate_pins",
                    Automatics.L10N.Translate(
                        "@command_removemappins_option_allow_non_duplicate_pins_description"),
                    x => _allowNonDuplicatePins = x != null
                }
            };
        }

        protected override void ResetOptions()
        {
            base.ResetOptions();
            _radius = 0;
            _include = "";
            _exclude = "";
            _allowNonDuplicatePins = false;
        }

        protected override void CommandAction(Terminal.ConsoleEventArgs args)
        {
            if (!Player.m_localPlayer) return;
            if (!ParseArgs(args)) return;

            var origin = Player.m_localPlayer.transform.position;

            var pins = new List<Minimap.PinData>();
            foreach (var pin in Reflections.GetField<List<Minimap.PinData>>(Minimap.instance,
                         "m_pins"))
            {
                if (_radius > 0 && Utils.DistanceXZ(origin, pin.m_pos) > _radius) continue;

                var name = Automatics.L10N.Translate(pin.m_name);
                if (!string.IsNullOrEmpty(_include) &&
                    name.IndexOf(_include, StringComparison.OrdinalIgnoreCase) == -1) continue;
                if (!string.IsNullOrEmpty(_exclude) &&
                    name.IndexOf(_exclude, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                pins.Add(pin);
            }

            if (!_allowNonDuplicatePins)
            {
                var positions = new HashSet<Vector3>();
                pins = pins.Where(pin => !positions.Add(pin.m_pos)).ToList();
            }

            var msg = Automatics.L10N.Localize("@command_removemappins_message_remove_pins",
                pins.Count);
            args.Context.AddString(msg);
            Automatics.Logger.Message(msg);
            pins.ForEach(pin =>
            {
                msg = Automatics.L10N.Localize("@command_removemappins_message_pin_data",
                    pin.m_name, pin.m_pos, Utils.DistanceXZ(origin, pin.m_pos));
                args.Context.AddString(msg);
                Automatics.Logger.Message(msg);
                Minimap.instance.RemovePin(pin);
            });
        }
    }
}