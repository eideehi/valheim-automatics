using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Automatics.ModUtils;
using Automatics.Valheim;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    using Animal = Creature.Animal;

    internal static class DynamicMapPinning
    {
        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> LazyObjectMask;
        private static readonly HashSet<DynamicMapPin> DynamicPins;

        private static ConditionalWeakTable<Collider, MonoBehaviour> _objectCache;

        public static int ObjectMask => LazyObjectMask.Value;

        static DynamicMapPinning()
        {
            ColliderBuffer = new Collider[1024];
            LazyObjectMask = new Lazy<int>(() => LayerMask.GetMask("character", "hitbox"));
            DynamicPins = new HashSet<DynamicMapPin>();
            _objectCache = new ConditionalWeakTable<Collider, MonoBehaviour>();
        }

        public static void ClearObjectCache()
        {
            _objectCache = new ConditionalWeakTable<Collider, MonoBehaviour>();
        }

        public static void RemovePin(Minimap.PinData pin)
        {
            if (!pin.m_save)
                DynamicPins.RemoveWhere(x => x.Data.m_pos == pin.m_pos);
        }

        public static void Run(Vector3 origin, float delta)
        {
            if (!Config.AutomaticMapPinningEnabled || Config.DynamicObjectSearchRange <= 0)
            {
                (from x in DynamicPins select x.Data).ToList().ForEach(Map.RemovePin);
                return;
            }

            CharacterPinning(origin, delta);
            ShipPinning(origin, delta);
        }

        private static void CharacterPinning(Vector3 origin, float delta)
        {
            var knownId = new HashSet<ZDOID>();
            foreach (var @object in
                     from x in GetNearbyObjects(origin)
                     orderby x.Item2
                     select x.Item1)
            {
                if (!Obj.GetZdoid(@object, out var id) || !knownId.Add(id)) continue;
                if (@object is Character animal && animal.IsTamed() && Config.IgnoreTamedAnimals) continue;

                AddOrUpdatePin(id, @object, delta);
            }

            (from x in DynamicPins where !knownId.Contains(x.Id) select x.Data).ToList().ForEach(Map.RemovePin);
        }

        private static void ShipPinning(Vector3 origin, float delta)
        {
            if (!Config.IsAllowPinningShip) return;

            foreach (var ship in ShipCache.GetAllInstance())
            {
                var pos = ship.transform.position;
                if (Utils.DistanceXZ(origin, pos) > Config.DynamicObjectSearchRange) continue;

                if (Map.FindPinInRange(pos, 4f, out var data))
                {
                    if (data.m_pos != pos)
                        data.m_pos = Vector3.MoveTowards(data.m_pos, pos, 200f * delta);
                }
                else
                {
                    var name = "";
                    var shipPiece = ship.GetComponent<Piece>();
                    if (shipPiece != null)
                        name = shipPiece.m_name;

                    if (string.IsNullOrEmpty(name))
                        name = Obj.GetName(ship);

                    Map.AddPin(pos, L10N.TranslateInternalNameOnly(name), true, new Target{ name = name });
                }
            }
        }

        private static (string, string) GetObjectNames(MonoBehaviour @object)
        {
            switch (@object)
            {
                case Character character:
                {
                    var level = character.GetLevel();
                    if (level <= 1) return (character.m_name, character.GetHoverName());

                    var levelSymbol = L10N.Translate("@character_level_symbol");
                    var sb = new StringBuilder(character.GetHoverName()).Append(" ");
                    for (var i = 1; i < level; i++) sb.Append(levelSymbol);

                    return (character.m_name, sb.ToString());
                }

                case RandomFlyingBird bird:
                {
                    var name = Obj.GetPrefabName(bird.gameObject);
                    return (name, L10N.Translate($"@animal_{name.ToLower()}"));
                }

                default:
                {
                    var name = Obj.GetName(@object);
                    return (name, L10N.Localize(name));
                }
            }
        }

        private static void AddOrUpdatePin(ZDOID id, MonoBehaviour @object, float delta)
        {
            var pos = @object.transform.position;
            var (iconName, pinName) = GetObjectNames(@object);

            var pin = DynamicPins.FirstOrDefault(x => x.Id == id);
            if (pin == null)
            {
                var target = new Target{ name = iconName, metadata = new MetaData{ level = @object is Character x ? x.GetLevel() : 0 }};
                DynamicPins.Add(new DynamicMapPin(id, Map.AddPin(pos, pinName, false, target)));
            }
            else
            {
                var data = pin.Data;

                data.m_name = pinName;

                if (data.m_pos != pos)
                    data.m_pos = Vector3.MoveTowards(data.m_pos, pos, 200f * delta);
            }
        }

        private static IEnumerable<(MonoBehaviour, float)> GetNearbyObjects(Vector3 pos)
        {
            return Obj.GetInSphere(pos, Config.DynamicObjectSearchRange, GetObject, ColliderBuffer,
                ObjectMask);
        }

        private static MonoBehaviour GetObject(Collider collider)
        {
            if (_objectCache.TryGetValue(collider, out var @object)) return @object;

            @object = ConvertObject(collider);
            _objectCache.Add(collider, @object);
            return @object;
        }

        private static MonoBehaviour ConvertObject(Collider collider)
        {
            switch (collider.GetComponentInParent<MonoBehaviour>())
            {
                case Humanoid humanoid when humanoid.IsPlayer():
                    return null;

                case RandomFlyingBird bird:
                    return Config.IsAllowPinning(Animal.Bird) ? bird : null;

                case Character animal when IsAnimal(animal):
                {
                    if (Creature.GetAnimal(animal.m_name, out var flag) && Config.IsAllowPinning(flag)) return animal;
                    return Config.IsCustomAnimal(animal.m_name) ? animal : null;
                }

                case Character monster when IsMonster(monster):
                {
                    if (monster.GetFaction() == Character.Faction.Boss) return null;
                    if (Creature.GetMonster(monster.m_name, out var flag) && Config.IsAllowPinning(flag)) return monster;
                    return Config.IsCustomMonster(monster.m_name) ? monster : null;
                }

                default:
                {
                    var fish = collider.GetComponentInParent<Fish>();
                    if (fish != null)
                        return Config.IsAllowPinning(Animal.Fish) ? fish : null;

                    return null;
                }
            }
        }

        private static bool IsAnimal(Character character)
        {
            if (character.GetComponent<Tameable>() || character.GetComponent<AnimalAI>()) return true;
            return Config.IsCustomAnimal(character.m_name);
        }

        private static bool IsMonster(Character character)
        {
            if (character.GetComponent<MonsterAI>()) return true;
            return Config.IsCustomMonster(character.m_name);
        }

        private class DynamicMapPin
        {
            public readonly ZDOID Id;
            public readonly Minimap.PinData Data;

            public DynamicMapPin(ZDOID id, Minimap.PinData data)
            {
                Id = id;
                Data = data;
            }
        }
    }
}