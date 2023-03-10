using System;
using System.Collections.Generic;
using System.Linq;
using Automatics.ConsoleCommands;
using ModUtils;
using NDesk.Options;
using UnityEngine;

namespace Automatics.Debug
{
    internal static class Commands
    {
        private class Give : Command
        {
            private int _quality;
            private int _stackSize;

            public Give() : base("give")
            {
            }

            protected override OptionSet CreateOptionSet()
            {
                return new OptionSet
                {
                    {
                        "q|quality=",
                        Automatics.L10N.Translate("@command_give_option_quality_description"),
                        (int x) => _quality = x
                    },
                    {
                        "n|stack-size=",
                        Automatics.L10N.Translate("@command_give_option_stack_size_description"),
                        (int x) => _stackSize = x
                    }
                };
            }

            protected override void ResetOptions()
            {
                base.ResetOptions();
                _quality = 1;
                _stackSize = 1;
            }

            protected override List<string> GetSuggestions()
            {
                return (from x in GetAllItems()
                        let name = x.m_itemData.m_shared.m_name.Trim()
                        where name.StartsWith("$")
                        select Csv.Escape(Automatics.L10N.Translate(name)))
                    .Distinct()
                    .ToList();
            }

            protected override void CommandAction(Terminal.ConsoleEventArgs args)
            {
                if (!ParseArgs(args)) return;

                foreach (var target in extraOptions.Take(1))
                {
                    var matches = new List<ItemDrop>();
                    foreach (var item in GetAllItems())
                    {
                        var name = item.m_itemData.m_shared.m_name;
                        var itemName = Csv.Escape(Automatics.L10N.Translate(name));
                        if (itemName == target)
                        {
                            GiveItem(item, _stackSize, _quality);
                            return;
                        }

                        if (itemName.Contains(name))
                            matches.Add(item);
                    }

                    var result = matches.Count;
                    switch (result)
                    {
                        case 0:
                            args.Context.AddString("item not found");
                            break;
                        case 1:
                            GiveItem(matches[0], _stackSize, _quality);
                            break;
                        default:
                            args.Context.AddString("item many matches");
                            break;
                    }
                }
            }
        }

        private class GiveMaterials : Command
        {
            private int _mult;

            public GiveMaterials() : base("givematerials")
            {
            }

            protected override OptionSet CreateOptionSet()
            {
                return new OptionSet
                {
                    {
                        "n|multiplier=",
                        Automatics.L10N.Translate(
                            "@command_givematerials_option_multiplier_description"),
                        (int x) => _mult = x
                    }
                };
            }

            protected override void ResetOptions()
            {
                base.ResetOptions();
                _mult = 1;
            }

            protected override List<string> GetSuggestions()
            {
                var list = (from x in GetAllPieces() select x.m_name).ToList();
                list.AddRange(from x in GetAllRecipes() select x.m_item.m_itemData.m_shared.m_name);

                return (from x in list select Csv.Escape(Automatics.L10N.Translate(x)))
                    .Distinct().ToList();
            }

            protected override void CommandAction(Terminal.ConsoleEventArgs args)
            {
                if (!ParseArgs(args)) return;

                foreach (var target in extraOptions.Take(1))
                {
                    foreach (var piece in GetAllPieces())
                    {
                        var pieceName = Csv.Escape(Automatics.L10N.Translate(piece.m_name));
                        if (pieceName != target) continue;

                        foreach (var resource in piece.m_resources)
                            GiveItem(resource.m_resItem, resource.m_amount * _mult, 1);
                        return;
                    }

                    foreach (var recipe in GetAllRecipes())
                    {
                        var itemName =
                            Csv.Escape(
                                Automatics.L10N.Translate(recipe.m_item.m_itemData.m_shared
                                    .m_name));
                        if (itemName != target) continue;

                        foreach (var resource in recipe.m_resources)
                            GiveItem(resource.m_resItem, resource.m_amount * _mult, 1);
                        return;
                    }

                    args.Context.AddString("item not found");
                    break;
                }
            }
        }

        private class PrintLocations2 : Command
        {
            private string _include;
            private string _exclude;
            private int _count;
            private bool _verbose;

            public PrintLocations2() : base("printlocations")
            {
            }

            private bool Matches(string name)
            {
                if (!string.IsNullOrEmpty(_include) &&
                    name.IndexOf(_include, StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (!string.IsNullOrEmpty(_exclude) &&
                    name.IndexOf(_exclude, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                return true;
            }

            protected override OptionSet CreateOptionSet()
            {
                return new OptionSet
                {
                    {
                        "i|include=",
                        Automatics.L10N.Translate(
                            "@command_printlocations_option_include_description"),
                        x => _include = x
                    },
                    {
                        "e|exclude=",
                        Automatics.L10N.Translate(
                            "@command_printlocations_option_exclude_description"),
                        x => _exclude = x
                    },
                    {
                        "n|count=",
                        Automatics.L10N.Translate(
                            "@command_printlocations_option_count_description"),
                        (int x) => _count = x
                    },
                    {
                        "v|verbose",
                        Automatics.L10N.Translate(
                            "@command_printlocations_option_verbose_description"),
                        x => _verbose = x != null
                    }
                };
            }

            protected override void ResetOptions()
            {
                base.ResetOptions();
                _include = "";
                _exclude = "";
                _count = 8;
                _verbose = false;
            }

            protected override void CommandAction(Terminal.ConsoleEventArgs args)
            {
                if (!ParseArgs(args)) return;

                var origin = Player.m_localPlayer.transform.position;

                Automatics.Logger.Debug($"Run command: {args.FullLine}");

                var knownLocation = new HashSet<string>();
                foreach (var location in ZoneSystem.instance.m_locationInstances.Values
                             .Where(x => Matches(x.m_location.m_prefabName))
                             .OrderBy(x => Vector3.Distance(origin, x.m_position))
                             .Take(_count))
                {
                    if (!_verbose && !knownLocation.Add(location.m_location.m_prefabName)) continue;

                    var message = $"  \"{location.m_location.m_prefabName}\" {location.m_position}";
                    args.Context.AddString(message);
                    Automatics.Logger.Debug(message);
                }
            }
        }

        private static readonly Lazy<IEnumerable<Piece>> AllPiecesLazy;

        static Commands()
        {
            AllPiecesLazy = new Lazy<IEnumerable<Piece>>(() =>
            {
                var pieces = new HashSet<Piece>();

                var knownTables = new HashSet<PieceTable>();
                var knownPieces = new HashSet<string>();
                foreach (var item in GetAllItems())
                {
                    var table = item.m_itemData.m_shared.m_buildPieces;
                    if (table == null || !knownTables.Add(table)) continue;

                    foreach (var piece in table.m_pieces)
                    {
                        var component = piece.GetComponent<Piece>();
                        if (!component.m_enabled) continue;

                        switch (component.m_category)
                        {
                            case Piece.PieceCategory.Building:
                            case Piece.PieceCategory.Crafting:
                            case Piece.PieceCategory.Furniture:
                                break;

                            default:
                                continue;
                        }

                        if (knownPieces.Add(component.m_name))
                            pieces.Add(component);
                    }
                }

                return pieces.AsEnumerable();
            });
        }

        public static void Register()
        {
            new Give().Register(true);
            new GiveMaterials().Register(true);
            new PrintLocations2().Register(true);
        }

        private static IEnumerable<ItemDrop> GetAllItems()
        {
            return ObjectDB.instance.m_items.Select(x => x.GetComponent<ItemDrop>());
        }

        private static IEnumerable<Piece> GetAllPieces()
        {
            return AllPiecesLazy.Value;
        }

        private static IEnumerable<Recipe> GetAllRecipes()
        {
            return ObjectDB.instance.m_recipes.Where(x => x.m_item != null);
        }

        private static void GiveItem(ItemDrop item, int count, int quality)
        {
            var player = Player.m_localPlayer;
            if (!player) return;

            var inventory = player.GetInventory();
            while (count > 0)
            {
                var prefab = item.gameObject;
                var data = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                var amount = Mathf.Min(count, data.m_shared.m_maxStackSize);

                data.m_dropPrefab = prefab;
                data.m_stack = amount;
                data.m_quality = Mathf.Min(quality, data.m_shared.m_maxQuality);

                if (!inventory.AddItem(data))
                    break;

                player.ShowPickupMessage(data, amount);
                count -= amount;
            }
        }
    }
}