using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class Command
    {
        private static readonly Lazy<int> ObjectMask;
        private static readonly Dictionary<string, Func<Collider, MonoBehaviour>> AllTypeConvertor;

        static Command()
        {
            ObjectMask = new Lazy<int>(() => LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
                "piece_nonsolid", "character", "hitbox", "vehicle", "item"));

            AllTypeConvertor = new Dictionary<string, Func<Collider, MonoBehaviour>>
            {
                { "Character", x => x.GetComponent<Character>() },
                { "Bird", x => x.GetComponent<RandomFlyingBird>() },
                { "Fish", x => x.GetComponent<Fish>() },
                { "Pickable", x => x.GetComponent<Pickable>() },
                { "Destructible", x => x.GetComponent<IDestructible>() as MonoBehaviour },
                { "Interactable", x => x.GetComponent<Interactable>() as MonoBehaviour },
                { "Hoverable", x => x.GetComponent<Hoverable>() as MonoBehaviour },
            };
        }

        private static void PrintObject(Terminal.ConsoleEventArgs args)
        {
            if (!(args.Length >= 3 && int.TryParse(args[2], out var range)))
            {
                args.Context.AddString(ConsoleCommand.SyntaxError("printobject"));
                return;
            }

            var type = args[1];
            var filter = args.Length >= 4 ? args[3] : "";

            if (!AllTypeConvertor.TryGetValue(type, out var convertor))
            {
                args.Context.AddString(ConsoleCommand.ArgumentError(type));
                return;
            }

            Log.Debug(() => $"Run command: printobject {type} {range} {filter}");

            var strings = new HashSet<string>();
            var pos = Player.m_localPlayer.transform.position;
            foreach (var @object in
                     from x in Utility.GetObjectsInSphere(pos, range, convertor, range * 16, ObjectMask.Value)
                     orderby x.Item2
                     select x.Item1)
            {
                switch (@object)
                {
                    case Humanoid humanoid when humanoid.IsPlayer():
                        break;
                    case RandomFlyingBird bird:
                    {
                        var prefabName = Utility.GetPrefabName(bird.gameObject).ToLower();
                        var name = $"@animal_{prefabName}";
                        strings.Add($"{L10N.Translate(name)}: [type: {@object.GetType()}, raw_name: {name}, layer: {Layer(@object)}]");
                        break;
                    }
                    default:
                    {
                        var name = Utility.GetName(@object);
                        strings.Add($"{L10N.Localize(name)}: [type: {@object.GetType()}, raw_name: {name}, layer: {Layer(@object)}]");
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(filter))
                strings.RemoveWhere(x => !x.Contains(filter));

            if (strings.Count > 0)
            {
                foreach (var x in strings)
                {
                    args.Context.AddString(x);
                    Log.Debug(() => x);
                }

                args.Context.AddString(L10N.Localize("@command_print_result", strings.Count));
            }
            else
            {
                args.Context.AddString(L10N.Translate("@command_print_result_empty"));
            }

            string Layer(Component @object)
            {
                return LayerMask.LayerToName(@object.gameObject.layer);
            }
        }

        public static void Register()
        {
            ConsoleCommand.Register("printobject", PrintObject, optionsFetcher: () => AllTypeConvertor.Keys.ToList(), isCheat: true);
        }
    }
}