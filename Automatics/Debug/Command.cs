using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Automatics.Debug
{
    internal static class Command
    {
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

        private static Dictionary<string, Terminal.ConsoleCommand> GetCommands()
        {
            return Traverse.Create<Terminal>().Field<Dictionary<string, Terminal.ConsoleCommand>>("commands").Value;
        }

        private static void Give(Terminal.ConsoleEventArgs args)
        {
            var name = args.Length >= 2 ? args[1] : "";
            if (string.IsNullOrEmpty(name))
            {
                args.Context.AddString(ConsoleCommand.SyntaxError("give"));
                return;
            }

            var count = args.Length >= 3 && int.TryParse(args[2], out var @int) ? @int : 1;
            var quality = args.Length >= 4 && int.TryParse(args[3], out @int) ? @int : 1;

            var items = new List<ItemDrop>();
            foreach (var item in Resources.FindObjectsOfTypeAll<ItemDrop>())
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

        private static List<string> GiveOptions() =>
            (from x in Resources.FindObjectsOfTypeAll<ItemDrop>()
                let name = x.m_itemData.m_shared.m_name.Trim()
                where name.StartsWith("$")
                select L10N.Translate(name).Replace(" ", "_"))
            .Distinct()
            .ToList();

        private static void GiveMaterials(Terminal.ConsoleEventArgs args)
        {
            var name = args.Length >= 2 ? args[1] : "";
            var count = args.Length >= 3 && int.TryParse(args[2], out var @int) ? @int : 1;

            Log.Debug(() => $"Run givematerials {name} {count}");

            var exit = false;
            foreach (var piece in Resources.FindObjectsOfTypeAll<Piece>())
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

            foreach (var recipe in from x in Resources.FindObjectsOfTypeAll<Recipe>() where x && x.m_item select x)
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
            var result = (from piece in Resources.FindObjectsOfTypeAll<Piece>()
                    where piece && !piece.m_targetNonPlayerBuilt
                    select piece.m_name)
                .ToList();
            result.AddRange(Resources.FindObjectsOfTypeAll<Recipe>().Where(x => x && x.m_item)
                .Select(recipe => recipe.m_item.m_itemData.m_shared.m_name));
            return (from x in result select L10N.Translate(x.Trim())).Distinct().ToList();
        }
    }
}