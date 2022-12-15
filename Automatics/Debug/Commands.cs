using System;
using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.Debug
{
    internal static class Commands
    {
        private static readonly Lazy<IEnumerable<Piece>> AllPiecesLazy;

        private static readonly Dictionary<string, Func<Collider, MonoBehaviour>>
            ObjectColliderConvertors;

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

            ObjectColliderConvertors = new Dictionary<string, Func<Collider, MonoBehaviour>>
            {
                { "Character", x => x.GetComponentInParent<Character>() },
                { "Bird", x => x.GetComponentInParent<RandomFlyingBird>() },
                { "Fish", x => x.GetComponentInParent<Fish>() },
                { "Pickable", x => x.GetComponentInParent<Pickable>() },
                { "Destructible", x => x.GetComponentInParent<IDestructible>() as MonoBehaviour },
                { "Interactable", x => x.GetComponentInParent<Interactable>() as MonoBehaviour },
                { "Hoverable", x => x.GetComponentInParent<Hoverable>() as MonoBehaviour }
            };
        }

        public static void Register()
        {
            ConsoleCommand.Register("give", Give, GiveOptions, true);
            ConsoleCommand.Register("givematerials", GiveMaterials, GiveMaterialsOptions, true);
            ConsoleCommand.Register("printobject", PrintObject, PrintObjectOptions, true);
            ConsoleCommand.Register("printlocations2", PrintLocations2);
        }

        private static void Give(Terminal.ConsoleEventArgs args)
        {
            var name = args.Length > 1 ? args[1] : "";
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
                var itemName = Automatics.L10N.Translate(item.m_itemData.m_shared.m_name)
                    .Replace(" ", "_");
                if (itemName == name)
                {
                    GiveItem(item, count, quality);
                    return;
                }

                if (itemName.Contains(name))
                    items.Add(item);
            }

            if (items.Count == 1) GiveItem(items[0], count, quality);
        }

        private static List<string> GiveOptions()
        {
            return (from x in GetAllItems()
                    let name = x.m_itemData.m_shared.m_name.Trim()
                    where name.StartsWith("$")
                    select Automatics.L10N.Translate(name).Replace(" ", "_"))
                .Distinct()
                .ToList();
        }

        private static void GiveMaterials(Terminal.ConsoleEventArgs args)
        {
            var name = args.Length > 1 ? args[1] : "";
            var count = args.TryParameterInt(2);

            Automatics.Logger.Debug(() => $"Run givematerials {name} {count}");

            var exit = false;
            foreach (var piece in GetAllPieces())
            {
                var pieceName = Automatics.L10N.Translate(piece.m_name.Trim()).Replace(" ", "_");
                if (pieceName != name) continue;
                exit = true;
                foreach (var resource in piece.m_resources)
                    GiveItem(resource.m_resItem, resource.m_amount * count, 1);
            }

            if (exit) return;

            foreach (var recipe in GetAllRecipes())
            {
                var itemName = Automatics.L10N
                    .Translate(recipe.m_item.m_itemData.m_shared.m_name.Trim()).Replace(" ", "_");
                if (itemName != name) continue;

                foreach (var resource in recipe.m_resources)
                    GiveItem(resource.m_resItem, resource.m_amount * count, 1);
            }
        }

        private static List<string> GiveMaterialsOptions()
        {
            var list = (from x in GetAllPieces() select x.m_name).ToList();
            list.AddRange(from x in GetAllRecipes() select x.m_item.m_itemData.m_shared.m_name);
            return (from x in list select Automatics.L10N.Translate(x.Trim()).Replace(" ", "_"))
                .Distinct().ToList();
        }

        private static void PrintObject(Terminal.ConsoleEventArgs args)
        {
            if (!args.TryParameterInt(2, out var range))
            {
                args.Context.AddString(ConsoleCommand.SyntaxError("printobject"));
                return;
            }

            var type = args[1];
            var filter = args.Length > 3 ? args[3] : "";

            if (!ObjectColliderConvertors.TryGetValue(type, out var convertor))
            {
                args.Context.AddString(ConsoleCommand.ArgumentError("printobject", type));
                return;
            }

            Automatics.Logger.Debug(() => $"Run command: printobject {type} {range} {filter}");

            var strings = new HashSet<string>();
            var pos = Player.m_localPlayer.transform.position;
            foreach (var (_, obj, _) in Objects.GetInsideSphere(pos, range, convertor, range * 16)
                         .OrderBy(x => x.distance))
                switch (obj)
                {
                    case Humanoid humanoid when humanoid.IsPlayer():
                        break;

                    case Character character:
                    {
                        var name = character.m_name;
                        strings.Add($"{Automatics.L10N.Translate(name)}: [type: {obj.GetType()}, raw_name: {name}, layer: {Layer(obj)}]");
                        break;
                    }

                    case RandomFlyingBird bird:
                    {
                        var prefabName = Objects.GetPrefabName(bird.gameObject);
                        var name = $"@animal_{prefabName.ToLower()}";
                        strings.Add(
                            $"{Automatics.L10N.Translate(name)}: [type: {obj.GetType()}, raw_name: {name}, layer: {Layer(obj)}]");
                        break;
                    }

                    default:
                    {
                        var name = Objects.GetName(obj);
                        strings.Add(
                            $"{Automatics.L10N.Localize(name)}: [type: {obj.GetType()}, raw_name: {name}, layer: {Layer(obj)}]");
                        break;
                    }
                }

            if (!string.IsNullOrEmpty(filter))
                strings.RemoveWhere(x => !x.Contains(filter));

            if (strings.Count > 0)
            {
                foreach (var x in strings)
                {
                    args.Context.AddString(x);
                    Automatics.Logger.Debug(() => x);
                }

                args.Context.AddString(Automatics.L10N.Localize("@command_print_result",
                    strings.Count));
            }
            else
            {
                args.Context.AddString(Automatics.L10N.Translate("@command_print_result_empty"));
            }

            string Layer(Component @object)
            {
                return LayerMask.LayerToName(@object.gameObject.layer);
            }
        }

        private static List<string> PrintObjectOptions()
        {
            return ObjectColliderConvertors.Keys.ToList();
        }

        private static void PrintLocations2(Terminal.ConsoleEventArgs args)
        {
            var filter = (args.Length > 1 ? args[1] : "").ToLower();
            var count = args.TryParameterInt(2, 8);
            var distinct = args.Length > 3 && args[3].ToLower() == "true";
            var origin = Player.m_localPlayer.transform.position;

            Automatics.Logger.Debug($"Run command: {filter} {count} {distinct}");

            var knownLocation = new HashSet<string>();
            foreach (var location in ZoneSystem.instance.m_locationInstances.Values
                         .Where(x => x.m_location.m_prefabName.ToLower().Contains(filter))
                         .OrderBy(x => Vector3.Distance(origin, x.m_position))
                         .Take(count))
            {
                if (distinct && !knownLocation.Add(location.m_location.m_prefabName)) continue;

                var message = $"\"{location.m_location.m_prefabName}\" {location.m_position}";
                args.Context.AddString(message);
                Automatics.Logger.Debug(message);
            }
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