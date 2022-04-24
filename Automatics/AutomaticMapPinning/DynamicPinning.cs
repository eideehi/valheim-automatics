using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    using PinData = Minimap.PinData;

    internal static class DynamicPinning
    {
        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> ObjectMaskLazy;
        private static readonly HashSet<DynamicPin> DynamicPins;

        private static ConditionalWeakTable<Collider, Component> _objectCache;

        static DynamicPinning()
        {
            ColliderBuffer = new Collider[4096];
            ObjectMaskLazy = new Lazy<int>(() => LayerMask.GetMask("character", "hitbox"));
            DynamicPins = new HashSet<DynamicPin>();
            _objectCache = new ConditionalWeakTable<Collider, Component>();
        }

        private static int ObjectMask => ObjectMaskLazy.Value;

        public static void OnSettingChanged(object sender, EventArgs e)
        {
            _objectCache = new ConditionalWeakTable<Collider, Component>();
        }

        public static void RemoveDynamicPin(PinData data)
        {
            if (!data.m_save)
                DynamicPins.RemoveWhere(x => x.Data.m_pos == data.m_pos);
        }

        public static void Run(Vector3 origin, float delta)
        {
            if (!Config.AutomaticMapPinningEnabled || Config.DynamicObjectSearchRange <= 0)
            {
                DynamicPins.Select(x => x.Data).ToList().ForEach(Map.RemovePin);
                return;
            }

            CreaturePinning(origin, delta);
            ShipPinning(origin, delta);
        }

        private static void CreaturePinning(Vector3 origin, float delta)
        {
            var knownId = new HashSet<ZDOID> { ZDOID.None };

            CharacterPinning(origin, delta, knownId);
            OtherCreaturePinning(origin, delta, knownId);

            (from x in DynamicPins where !knownId.Contains(x.ObjectId) select x.Data).ToList().ForEach(Map.RemovePin);
        }

        private static void CharacterPinning(Vector3 origin, float delta, ISet<ZDOID> knownId)
        {
            foreach (var character in Character.GetAllCharacters())
            {
                if (character.IsPlayer()) continue;

                var distance = Vector3.Distance(origin, character.transform.position);
                if (distance > Config.DynamicObjectSearchRange) continue;

                var name = character.m_name;
                if (Core.IsAnimal(name, out var animalData))
                {
                    if (!animalData.IsAllowed) continue;
                    if (character.IsTamed() && Config.IgnoreTamedAnimals) continue;
                }
                else if (Core.IsMonster(name, out var monsterData))
                {
                    if (!monsterData.IsAllowed) continue;
                }
                else
                {
                    continue;
                }

                var id = character.GetZDOID();
                if (knownId.Add(id))
                    AddOrUpdatePin(id, character, delta);
            }
        }

        private static void OtherCreaturePinning(Vector3 origin, float delta, ISet<ZDOID> knownId)
        {
            foreach (var (_, component, _) in GetNearbyObjects(origin))
            {
                if (Obj.GetZdoid(component, out var id) && knownId.Add(id))
                    AddOrUpdatePin(id, component, delta);
            }
        }

        private static void ShipPinning(Vector3 origin, float delta)
        {
            if (!Config.IsAllowPinningShip) return;

            foreach (var ship in ShipCache.GetAllInstance())
            {
                var pos = ship.transform.position;
                if (Vector3.Distance(origin, pos) > Config.DynamicObjectSearchRange) continue;

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

                    Map.AddPin(pos, L10N.TranslateInternalNameOnly(name), true, CreateTarget(name, 0));
                }
            }
        }

        private static void AddOrUpdatePin(ZDOID id, Component component, float delta)
        {
            var pos = component.transform.position;
            var (target, pinName) = GetPinningData(component);

            var pin = DynamicPins.FirstOrDefault(x => x.ObjectId == id);
            if (pin == null)
            {
                DynamicPins.Add(new DynamicPin(id, Map.AddPin(pos, pinName, false, target)));
            }
            else
            {
                var data = pin.Data;

                data.m_name = pinName;

                if (data.m_pos != pos)
                    data.m_pos = Vector3.MoveTowards(data.m_pos, pos, 200f * delta);
            }
        }

        private static IEnumerable<(Collider, Component, float)> GetNearbyObjects(Vector3 pos)
        {
            return Obj.GetInsideSphere(pos, Config.DynamicObjectSearchRange, GetObject, ColliderBuffer, ObjectMask);
        }

        private static Component GetObject(Collider collider)
        {
            if (_objectCache.TryGetValue(collider, out var component)) return component;

            component = ConvertObject(collider);
            _objectCache.Add(collider, component);
            return component;
        }

        private static Component ConvertObject(Collider collider)
        {
            var bird = collider.GetComponentInParent<RandomFlyingBird>();
            if (bird != null && Core.IsAnimal(GetBirdName(bird), out var data) && data.IsAllowed)
                return bird;

            var fish = collider.GetComponentInParent<Fish>();
            if (fish != null && Core.IsAnimal(Obj.GetName(fish), out data) && data.IsAllowed)
                return fish;

            return null;
        }

        private static string GetBirdName(RandomFlyingBird bird)
        {
            var name = Obj.GetPrefabName(bird.gameObject).ToLower();
            return $"@animal_{name}";
        }

        private static (PinningTarget Target, string PinName) GetPinningData(Component component)
        {
            switch (component)
            {
                case Character character:
                    return GetPinningData(character);

                case RandomFlyingBird bird:
                {
                    var name = GetBirdName(bird);
                    return (CreateTarget(name, 0), L10N.Translate(name));
                }

                default:
                {
                    var name = Obj.GetName(component);
                    return (CreateTarget(name, 0), L10N.Localize(name));
                }
            }
        }

        private static (PinningTarget Target, string PinName) GetPinningData(Character character)
        {
            var name = character.m_name;
            var level = character.GetLevel();
            if (level <= 1) return (CreateTarget(name, level), character.GetHoverName());

            var symbol = L10N.Translate("@character_level_symbol");
            var sb = new StringBuilder(character.GetHoverName()).Append(" ");
            for (var i = 1; i < level; i++) sb.Append(symbol);

            return (CreateTarget(name, level), sb.ToString());
        }

        private static PinningTarget CreateTarget(string name, int level)
        {
            return level <= 0
                ? new PinningTarget { name = name }
                : new PinningTarget { name = name, metadata = new MetaData { level = level } };
        }

        private class DynamicPin
        {
            public readonly ZDOID ObjectId;
            public readonly PinData Data;

            public DynamicPin(ZDOID objectId, PinData data)
            {
                ObjectId = objectId;
                Data = data;
            }
        }
    }
}