using static Automatics.ValheimCharacter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
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
            if (Config.DynamicObjectSearchRange <= 0)
            {
                (from x in DynamicPins select x.Data).ToList().ForEach(Map.RemovePin);
                return;
            }

            var knownId = new HashSet<ZDOID>();
            foreach (var @object in
                     from x in GetNearbyObjects(origin)
                     orderby x.Item2
                     select x.Item1)
            {
                if (Utility.GetZdoid(@object, out var id) && knownId.Add(id))
                    AddOrUpdatePin(id, @object.transform.position, GetObjectName(@object), delta);
            }

            (from x in DynamicPins where !knownId.Contains(x.Id) select x.Data).ToList().ForEach(Map.RemovePin);
        }

        private static string GetObjectName(MonoBehaviour @object)
        {
            switch (@object)
            {
                case Character character:
                {
                    var level = character.GetLevel();
                    if (level <= 1) return character.GetHoverName();

                    var levelSymbol = L10N.Translate("@character_level_symbol");
                    var sb = new StringBuilder(character.GetHoverName()).Append(" ");
                    for (var i = 1; i < level; i++) sb.Append(levelSymbol);

                    return sb.ToString();
                }
                case RandomFlyingBird bird:
                {
                    var name = Utility.GetPrefabName(bird.gameObject);
                    return L10N.Translate($"@animal_{name.ToLower()}");
                }
                default:
                    return L10N.Localize(Utility.GetName(@object));
            }
        }

        private static void AddOrUpdatePin(ZDOID id, Vector3 pos, string name, float delta)
        {
            var pin = DynamicPins.FirstOrDefault(x => x.Id == id);
            if (pin == null)
            {
                DynamicPins.Add(new DynamicMapPin(id, Map.AddPin(pos, name, false)));
            }
            else
            {
                var data = pin.Data;

                data.m_name = name;

                if (data.m_pos != pos)
                    data.m_pos = Vector3.MoveTowards(data.m_pos, pos, 200f * delta);
            }
        }

        private static IEnumerable<(MonoBehaviour, float)> GetNearbyObjects(Vector3 pos)
        {
            return Utility.GetObjectsInSphere(pos, Config.DynamicObjectSearchRange, GetObject, ColliderBuffer,
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
            if (!collider.attachedRigidbody) return null;

            switch (collider.attachedRigidbody.GetComponent<MonoBehaviour>())
            {
                case Humanoid player when player.IsPlayer():
                    return null;
                case Character animal when animal.GetComponent<Tameable>() || animal.GetComponent<AnimalAI>():
                {
                    if (animal.IsTamed() && Config.IgnoreTamedAnimals) return null;
                    if (Animal.GetFlag(animal.m_name, out var flag) && !Config.IsAllowPinning(flag))
                        return null;
                    return animal;
                }
                case Character monster when monster.GetComponent<MonsterAI>():
                {
                    if (monster.GetFaction() == Character.Faction.Boss) return null;
                    if (Monster.GetFlag(monster.m_name, out var flag) && !Config.IsAllowPinning(flag))
                        return null;
                    return monster;
                }
                case Fish fish:
                    return Config.IsAllowPinning(Animal.Flag.Fish) ? fish : null;
                case RandomFlyingBird bird:
                    return Config.IsAllowPinning(Animal.Flag.Bird) ? bird : null;
                default:
                    return null;
            }
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