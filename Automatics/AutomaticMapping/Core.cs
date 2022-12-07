using ModUtils;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    using static Valheim.Creature;
    using static Valheim.Object;
    using static Valheim.Location;
    using static ValheimObject;

    internal static class Core
    {
        [AutomaticsInitializer(2)]
        private static void Initialize()
        {
            Config.Initialize();

            PickableCache.OnCacheAdded += pickable =>
            {
                if (IsFlora(Objects.GetName(pickable), out _))
                    pickable.gameObject.AddComponent<FloraObject>();
            };

            ShipCache.OnCacheAdded += ship =>
            {
                var shipObj = ship.GetComponent<WearNTear>();
                if (shipObj == null) return;

                shipObj.m_onDestroyed += () =>
                {
                    if (Map.FindPinInRange(ship.transform.position, 4f, out var data))
                        Map.RemovePin(data);
                };
            };
        }

        public static void OnUpdateMap(Player player, float delta, bool takeInput)
        {
            if (Game.IsPaused()) return;
            if (player.InInterior()) return;

            var origin = player.transform.position;
            DynamicPinning.Run(origin, delta);
            StaticPinning.Run(origin, takeInput);
        }

        public static bool IsActive()
        {
            return Config.EnableAutomaticMapping && Player.m_localPlayer != null;
        }

        public static bool IsAnimal(string name, out (Animal Animal, bool IsCustom, bool IsAllowed) data)
        {
            if (GetAnimal(name, out var animal))
            {
                data = (animal, false, (Config.AllowPinningAnimal & animal) != 0);
                return true;
            }

            if (IsInNameList(name, Config.AllowPinningAnimalCustom))
            {
                data = (Animal.None, true, true);
                return true;
            }

            data = (Animal.None, false, false);
            return false;
        }

        public static bool IsMonster(string name, out (Monster Monster, bool IsCustom, bool IsAllowed) data)
        {
            if (GetMonster(name, out var monster))
            {
                data = (monster, false, (Config.AllowPinningMonster & monster) != 0);
                return true;
            }

            if (IsInNameList(name, Config.AllowPinningMonsterCustom))
            {
                data = (Monster.None, true, true);
                return true;
            }

            data = (Monster.None, false, false);
            return false;
        }

        public static bool IsFlora(string name, out (Flora Flora, bool IsCustom, bool IsAllowed) data)
        {
            if (GetFlora(name, out var flora))
            {
                data = (flora, false, (Config.AllowPinningFlora & flora) != 0);
                return true;
            }

            if (IsInNameList(name, Config.AllowPinningFloraCustom))
            {
                data = (Flora.None, true, true);
                return true;
            }

            data = (Flora.None, false, false);
            return false;
        }

        public static bool IsMineralDeposit(string name,
            out (MineralDeposit Deposit, bool IsCustom, bool IsAllowed) data)
        {
            if (GetMineralDeposit(name, out var deposit))
            {
                data = (deposit, false, (Config.AllowPinningVein & deposit) != 0);
                return true;
            }

            if (IsInNameList(name, Config.AllowPinningVeinCustom))
            {
                data = (MineralDeposit.None, true, true);
                return true;
            }

            data = (MineralDeposit.None, false, false);
            return false;
        }

        public static bool IsSpawner(string name, out (Spawner Spawner, bool IsCustom, bool IsAllowed) data)
        {
            if (GetSpawner(name, out var spawner))
            {
                data = (spawner, false, (Config.AllowPinningSpawner & spawner) != 0);
                return true;
            }

            if (IsInNameList(name, Config.AllowPinningSpawnerCustom))
            {
                data = (Spawner.None, true, true);
                return true;
            }

            data = (Spawner.None, false, false);
            return false;
        }

        public static bool IsOther(string name, out (Other Other, bool IsCustom, bool IsAllowed) data)
        {
            if (GetOther(name, out var other))
            {
                data = (other, false, (Config.AllowPinningOther & other) != 0);
                return true;
            }

            if (IsInNameList(name, Config.AllowPinningOtherCustom))
            {
                data = (Other.None, true, true);
                return true;
            }

            data = (Other.None, false, false);
            return false;
        }

        public static bool IsDungeon(string name, out (Dungeon Dungeon, bool IsCustom, bool IsAllowed) data)
        {
            if (GetDungeon(name, out var dungeon))
            {
                data = (dungeon, false, (Config.AllowPinningDungeon & dungeon) != 0);
                return true;
            }

            if (IsInPrefabList(name, Config.AllowPinningDungeonCustom))
            {
                data = (Dungeon.None, true, true);
                return true;
            }

            data = (Dungeon.None, false, false);
            return false;
        }

        public static bool IsSpot(string name, out (Spot Spot, bool IsCustom, bool IsAllowed) data)
        {
            if (GetSpot(name, out var spot))
            {
                data = (spot, false, (Config.AllowPinningSpot & spot) != 0);
                return true;
            }

            if (IsInPrefabList(name, Config.AllowPinningSpotCustom))
            {
                data = (Spot.None, true, true);
                return true;
            }

            data = (Spot.None, false, false);
            return false;
        }

        private static bool IsInNameList(string internalName, StringList list)
        {
            if (!list.Any()) return false;

            var displayName = Automatics.L10N.TranslateInternalName(internalName);
            return list.Any(x =>
                L10N.IsInternalName(x)
                    ? internalName.Equals(x, StringComparison.Ordinal)
                    : displayName.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsInPrefabList(string prefabName, StringList list)
        {
            if (!list.Any()) return false;

            return list.Any(x =>
                x.StartsWith("r/", StringComparison.OrdinalIgnoreCase)
                    ? Regex.IsMatch(prefabName, x.Substring(2))
                    : prefabName.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    [DisallowMultipleComponent]
    public sealed class ShipCache : InstanceCache<Ship>
    {
    }
}