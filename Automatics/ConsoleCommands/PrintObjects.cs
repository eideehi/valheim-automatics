using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Automatics.AutomaticMapping;
using Automatics.Valheim;
using ModUtils;
using NDesk.Options;
using UnityEngine;

namespace Automatics.ConsoleCommands
{
    internal class PrintObjects : Command
    {
        private static readonly Collider[] ColliderBuffer;

        static PrintObjects()
        {
            ColliderBuffer = new Collider[4096];
        }

        private int _radius;
        private int _number;
        private string _include;
        private string _exclude;

        public PrintObjects() : base("printobjects")
        {
            HaveExtraOption = true;
            HaveExtraDescription = true;
        }

        private static (bool IsValid, bool IsRegex, string Value) CreateFilter(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return (false, false, "");
            return arg.StartsWith("r/", StringComparison.OrdinalIgnoreCase)
                ? (true, true, arg.Substring(2))
                : (true, false, arg);
        }

        private static bool MatchesFilter(string name,
            (bool IsValid, bool IsRegex, string Value) filter)
        {
            var match = filter.IsRegex
                ? Regex.IsMatch(name, filter.Value)
                : name.IndexOf(filter.Value, StringComparison.OrdinalIgnoreCase) >= 0;
            if (match) return true;

            var localized = Automatics.L10N.TranslateInternalName(name);
            if (name != localized)
                match = filter.IsRegex
                    ? Regex.IsMatch(localized, filter.Value)
                    : localized.IndexOf(filter.Value, StringComparison.OrdinalIgnoreCase) >= 0;
            return match;
        }

        private static void PrintLine(Terminal.ConsoleEventArgs args, string message)
        {
            args.Context.AddString(message);
            foreach (var line in message.Split(new[] { '\n' }, StringSplitOptions.None))
                Automatics.Logger.Message(line.Trim());
        }

        protected override OptionSet CreateOptionSet()
        {
            return new OptionSet
            {
                {
                    "r|radius=",
                    Automatics.L10N.Translate(
                        "@command_printobjects_option_radius_description"),
                    (int x) => _radius = x
                },
                {
                    "n|number=",
                    Automatics.L10N.Translate(
                        "@command_printobjects_option_number_description"),
                    (int x) => _number = x
                },
                {
                    "i|include=",
                    Automatics.L10N.Translate(
                        "@command_printobjects_option_include_description"),
                    x => _include = x
                },
                {
                    "e|exclude=",
                    Automatics.L10N.Translate(
                        "@command_printobjects_option_exclude_description"),
                    x => _exclude = x
                }
            };
        }

        protected override List<string> GetSuggestions()
        {
            return new List<string>
            {
                "animal",
                "dungeon",
                "door",
                "container",
                "flora",
                "mineral",
                "monster",
                "other",
                "spawner",
                "spot",
                "vehicle"
            };
        }

        protected override void ResetOptions()
        {
            base.ResetOptions();
            _radius = 32;
            _number = 4;
            _include = "";
            _exclude = "";
        }

        protected override void CommandAction(Terminal.ConsoleEventArgs args)
        {
            if (!ParseArgs(args)) return;

            Automatics.Logger.Message(() => $"Command exec: {args.FullLine}");

            var type = (extraOptions.FirstOrDefault() ?? "").ToLower();
            switch (type)
            {
                case "animal":
                    PrintAnimal(args);
                    return;
                case "monster":
                    PrintMonster(args);
                    return;
                case "flora":
                    PrintFlora(args);
                    return;
                case "mineral":
                    PrintMineral(args);
                    return;
                case "spawner":
                    PrintSpawner(args);
                    return;
                case "vehicle":
                    PrintVehicle(args);
                    return;
                case "other":
                    PrintOther(args);
                    return;
                case "dungeon":
                    PrintDungeon(args);
                    return;
                case "spot":
                    PrintSpot(args);
                    return;
                case "door":
                    PrintDoor(args);
                    return;
                case "container":
                    PrintContainer(args);
                    return;
            }
        }

        private void PrintObject(Terminal.ConsoleEventArgs args, string type,
            ValheimObject vObject, IEnumerable<MonoBehaviour> objects,
            Predicate<MonoBehaviour> predicate = null)
        {
            if (predicate is null)
                predicate = behaviour => true;

            var objectType =
                Automatics.L10N.Translate($"@command_printobjects_message_type_{type}");

            var include = CreateFilter(_include);
            var exclude = CreateFilter(_exclude);

            var knownNames = new HashSet<string>();
            var count = 0;
            var origin = Player.m_localPlayer.transform.position;
            foreach (var (obj, distance) in from x in objects
                     let distance = Vector3.Distance(origin, x.transform.position)
                     where distance < _radius && predicate.Invoke(x)
                     orderby distance
                     select (x, distance.ToString("F1")))
            {
                var name = Objects.GetName(obj);
                if (!knownNames.Add(name)) continue;

                if (include.IsValid && !MatchesFilter(name, include)) continue;
                if (exclude.IsValid && MatchesFilter(name, exclude)) continue;

                if (++count > _number) continue;

                var defined = vObject.GetIdentify(name, out var identifier);
                if (!defined)
                    identifier =
                        PrefabNameToIdentifier(Objects.GetPrefabName(obj.gameObject));

                var localizedName = Automatics.L10N.Translate(name);
                var message = defined
                    ? Automatics.L10N.LocalizeTextOnly(
                        "@command_printobjects_message_result_defined",
                        localizedName, name, objectType, obj.transform.position, distance,
                        identifier)
                    : Automatics.L10N.LocalizeTextOnly("@command_printobjects_message_result",
                        localizedName, name, obj.transform.position, distance, identifier, name);

                PrintLine(args, message);
                args.Context.AddString("");
            }

            if (count > _number)
                PrintLine(args,
                    Automatics.L10N.Localize("@command_printobjects_message_result_more",
                        count - _number, objectType));
            else if (count == 0)
                PrintLine(args,
                    Automatics.L10N.Localize("@command_printobjects_message_result_empty",
                        objectType));
        }

        private void PrintAnimal(Terminal.ConsoleEventArgs args)
        {
            var animals = new List<MonoBehaviour>();
            animals.AddRange(Character.GetAllCharacters().Where(x =>
                x.GetComponent<AnimalAI>() || x.GetComponent<Tameable>()));
            animals.AddRange(FishCache.GetAllInstance());
            animals.AddRange(BirdCache.GetAllInstance());

            PrintObject(args, "animal", ValheimObject.Animal, animals, x => true);
        }

        private void PrintMonster(Terminal.ConsoleEventArgs args)
        {
            PrintObject(args, "monster", ValheimObject.Monster, Character.GetAllCharacters(),
                x => x.GetComponent<MonsterAI>() && !x.GetComponent<Tameable>());
        }

        private void PrintFlora(Terminal.ConsoleEventArgs args)
        {
            PrintObject(args, "flora", ValheimObject.Flora, PickableCache.GetAllInstance(),
                x => true);
        }

        private void PrintMineral(Terminal.ConsoleEventArgs args)
        {
            PrintObject(args, "mineral", ValheimObject.Mineral, GetObjects(x =>
            {
                var mineRock = x.GetComponent<MineRock>();
                if (mineRock) return mineRock;
                var mineRock5 = x.GetComponent<MineRock5>();
                if (mineRock5) return mineRock5;
                return x.GetComponent<Destructible>();
            }));
        }

        private void PrintSpawner(Terminal.ConsoleEventArgs args)
        {
            PrintObject(args, "spawner", ValheimObject.Spawner,
                GetObjects(x => x.GetComponent<SpawnArea>()));
        }

        private void PrintVehicle(Terminal.ConsoleEventArgs args)
        {
            var vehicles = new List<MonoBehaviour>();
            vehicles.AddRange(ShipCache.GetAllInstance());
            vehicles.AddRange(Reflections.GetStaticField<Vagon, List<Vagon>>("m_instances") ??
                              new List<Vagon>(0));
            PrintObject(args, "vehicle", MappingObject.Vehicle, vehicles, x => true);
        }

        private void PrintOther(Terminal.ConsoleEventArgs args)
        {
            PrintObject(args, "other", MappingObject.Other, GetObjects(x =>
            {
                if (x.GetComponent<IDestructible>() is MonoBehaviour x1) return x1;
                if (x.GetComponent<Interactable>() is MonoBehaviour x2) return x2;
                if (x.GetComponent<Hoverable>() is MonoBehaviour x3) return x3;
                return null;
            }));
        }

        private void PrintDungeon(Terminal.ConsoleEventArgs args)
        {
            var objectType =
                Automatics.L10N.Translate($"@command_printobjects_message_type_dungeon");

            var count = 0;
            var knownNames = new HashSet<string>();
            var origin = Player.m_localPlayer.transform.position;
            foreach (var location in from x in ZoneSystem.instance
                         .m_locationInstances.Values
                     where Vector3.Distance(origin, x.m_position) < _radius
                     select x)
            {
                var prefabName = location.m_location.m_prefabName;
                var name = prefabName;
                if (!knownNames.Add(name)) continue;

                var defined = ValheimObject.Dungeon.GetIdentify(name, out var identifier);
                if (!defined)
                    identifier = PrefabNameToIdentifier(name);

                var hasEntrance = false;
                var pos = location.m_position;
                foreach (var (x, y, _) in Objects
                             .GetInsideSphere(pos, location.m_location.m_exteriorRadius,
                                 x => x.GetComponent<Teleport>(), ColliderBuffer,
                                 LayerMask.GetMask("character_trigger"))
                             .OrderBy(x => x.distance))
                {
                    name = y.m_enterText;
                    var localizedName = Automatics.L10N.Translate(name);
                    var center = x.bounds.center;
                    var distance = Vector3.Distance(origin, center).ToString("F1");
                    var message = defined
                        ? Automatics.L10N.LocalizeTextOnly(
                            "@command_printobjects_message_result_defined",
                            localizedName, name, objectType, center, distance, identifier)
                        : Automatics.L10N.LocalizeTextOnly(
                            "@command_printobjects_message_result",
                            localizedName, name, center, distance, identifier, prefabName);
                    PrintLine(args, message);

                    hasEntrance = true;
                    count++;
                    break;
                }

                if (!hasEntrance) continue;
                if (count >= _number) break;

                args.Context.AddString("");
            }

            if (count > _number)
                PrintLine(args,
                    Automatics.L10N.Localize("@command_printobjects_message_result_more",
                        count - _number, objectType));
            else if (count == 0)
                PrintLine(args,
                    Automatics.L10N.Localize("@command_printobjects_message_result_empty",
                        objectType));
        }

        private void PrintSpot(Terminal.ConsoleEventArgs args)
        {
            var objectType =
                Automatics.L10N.Translate($"@command_printobjects_message_type_spot");

            var count = 0;
            var knownNames = new HashSet<string>();
            var origin = Player.m_localPlayer.transform.position;
            foreach (var (location, distance) in from x in ZoneSystem.instance
                         .m_locationInstances.Values
                     let distance = Vector3.Distance(origin, x.m_position)
                     where distance < _radius
                     select (x, distance))
            {
                var prefabName = location.m_location.m_prefabName;
                var name = prefabName;
                if (!knownNames.Add(name)) continue;

                var defined = ValheimObject.Spot.GetIdentify(name, out var identifier);
                if (!defined)
                    identifier = PrefabNameToIdentifier(name);

                var hasEntrance = false;
                var pos = location.m_position;
                foreach (var (_, x, _) in Objects
                             .GetInsideSphere(pos, location.m_location.m_exteriorRadius,
                                 x => x.GetComponent<Teleport>(), ColliderBuffer,
                                 LayerMask.GetMask("character_trigger"))
                             .OrderBy(x => x.distance))
                {
                    hasEntrance = true;
                    break;
                }

                if (hasEntrance) continue;

                if (defined)
                    ValheimObject.Spot.GetName(identifier, out name);
                else
                    name = $"@location_{name.ToLower()}";

                var localizedName = Automatics.L10N.Translate(name);
                var distanceText = distance.ToString("F1");
                var message = defined
                    ? Automatics.L10N.LocalizeTextOnly(
                        "@command_printobjects_message_result_defined",
                        localizedName, name, objectType, pos, distanceText, identifier)
                    : Automatics.L10N.LocalizeTextOnly(
                        "@command_printobjects_message_result",
                        localizedName, name, pos, distanceText, identifier, prefabName);
                PrintLine(args, message);

                if (++count >= _number) break;

                args.Context.AddString("");
            }

            if (count > _number)
                PrintLine(args,
                    Automatics.L10N.Localize("@command_printobjects_message_result_more",
                        count - _number, objectType));
            else if (count == 0)
                PrintLine(args,
                    Automatics.L10N.Localize("@command_printobjects_message_result_empty",
                        objectType));
        }

        private void PrintDoor(Terminal.ConsoleEventArgs args)
        {
            PrintObject(args, "door", AutomaticDoor.Globals.Door,
                GetObjects(x => x.GetComponent<Door>()));
        }

        private void PrintContainer(Terminal.ConsoleEventArgs args)
        {
            PrintObject(args, "container", AutomaticProcessing.Globals.Container,
                ContainerCache.GetAllInstance());
        }

        private IEnumerable<MonoBehaviour> GetObjects(
            Func<MonoBehaviour, MonoBehaviour> converter = null)
        {
            if (converter is null)
                converter = x => x;

            var result = new List<MonoBehaviour>(4096);

            var origin = Player.m_localPlayer.transform.position;
            var size = Physics.OverlapBoxNonAlloc(origin,
                new Vector3(_radius, _radius, _radius), ColliderBuffer);
            for (var i = 0; i < size; i++)
            {
                var obj = ColliderBuffer[i].GetComponentInParent<MonoBehaviour>();
                if (!obj) continue;

                obj = converter(obj);
                if (obj)
                    result.Add(obj);
            }

            return result;
        }

        private static string PrefabNameToIdentifier(string prefabName)
        {
            var sb = new StringBuilder();

            var initial = true;
            foreach (var c in prefabName.ToCharArray())
                switch (c)
                {
                    case '_':
                    {
                        initial = true;
                        continue;
                    }
                    case char _ when char.IsLetter(c) || char.IsDigit(c):
                    {
                        sb.Append(initial ? char.ToUpper(c) : c);
                        initial = false;
                        break;
                    }
                }

            return sb.ToString();
        }
    }
}