using System;
using System.Collections.Generic;
using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.Debug
{
    internal static class Command
    {
        private static readonly Lazy<IEnumerable<Piece>> LazyAllPieces;

        static Command()
        {
            LazyAllPieces = new Lazy<IEnumerable<Piece>>(() =>
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

        public static void RegisterCommands()
        {
            ConsoleCommand.Register("h", Help);
            ConsoleCommand.Register("give", Give, optionsFetcher: GiveOptions, isCheat: true);
            ConsoleCommand.Register("givematerials", GiveMaterials, optionsFetcher: GiveMaterialsOptions,
                isCheat: true);
        }

        private static void Help(Terminal.ConsoleEventArgs args)
        {
            var filter = args.Length >= 2 ? args[1] : "";
            foreach (var command in from x in GetCommands().Values
                     where !x.IsSecret && x.IsValid(args.Context) &&
                           (x.Command.Contains(filter) || x.Description.Contains(filter))
                     orderby x.Command
                     select x.Command + " - " + x.Description)
            {
                args.Context.AddString(command);
            }
        }

        private static void Give(Terminal.ConsoleEventArgs args)
        {
            var name = args.Length >= 2 ? args[1] : "";
            if (string.IsNullOrEmpty(name))
            {
                args.Context.AddString(ConsoleCommand.SyntaxError("give"));
                return;
            }

            var count = args.TryParameterInt(2);
            var quality = args.TryParameterInt(3);

            var items = new List<ItemDrop>();
            foreach (var item in GetAllItems())
            {
                var itemName = L10N.Translate(item.m_itemData.m_shared.m_name).Replace(" ", "_");
                if (itemName == name)
                {
                    GiveItem(item, count, quality);
                    return;
                }

                if (itemName.Contains(name))
                    items.Add(item);
            }

            if (items.Count == 1)
            {
                GiveItem(items[0], count, quality);
            }
        }

        private static List<string> GiveOptions() =>
            (from x in GetAllItems()
                let name = x.m_itemData.m_shared.m_name.Trim()
                where name.StartsWith("$")
                select L10N.Translate(name).Replace(" ", "_"))
            .Distinct()
            .ToList();

        private static void GiveMaterials(Terminal.ConsoleEventArgs args)
        {
            var name = args.Length >= 2 ? args[1] : "";
            var count = args.TryParameterInt(2);

            Log.Debug(() => $"Run givematerials {name} {count}");

            var exit = false;
            foreach (var piece in GetAllPieces())
            {
                var pieceName = L10N.Translate(piece.m_name.Trim()).Replace(" ", "_");
                if (pieceName != name) continue;
                exit = true;
                foreach (var resource in piece.m_resources)
                {
                    GiveItem(resource.m_resItem, resource.m_amount * count, 1);
                }
            }

            if (exit) return;

            foreach (var recipe in GetAllRecipes())
            {
                var itemName = L10N.Translate(recipe.m_item.m_itemData.m_shared.m_name.Trim()).Replace(" ", "_");
                if (itemName != name) continue;

                foreach (var resource in recipe.m_resources)
                {
                    GiveItem(resource.m_resItem, resource.m_amount * count, 1);
                }
            }
        }

        private static List<string> GiveMaterialsOptions()
        {
            var list = (from x in GetAllPieces() select x.m_name).ToList();
            list.AddRange(from x in GetAllRecipes() select x.m_item.m_itemData.m_shared.m_name);
            return (from x in list select L10N.Translate(x.Trim()).Replace(" ", "_")).Distinct().ToList();
        }

        private static Dictionary<string, Terminal.ConsoleCommand> GetCommands()
        {
            return Reflection.GetStaticField<Terminal, Dictionary<string, Terminal.ConsoleCommand>>("commands");
        }

        private static IEnumerable<ItemDrop> GetAllItems()
        {
            return ObjectDB.instance.m_items.Select(x => x.GetComponent<ItemDrop>());
        }

        private static IEnumerable<Piece> GetAllPieces()
        {
            return LazyAllPieces.Value;
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