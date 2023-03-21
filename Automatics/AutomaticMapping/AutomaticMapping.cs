using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Automatics.Valheim;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class AutomaticMapping
    {
        private static float _staticObjectMappingTimer;

        private static bool CanStaticObjectMapping(float delta, bool takeInput)
        {
            if (Config.StaticObjectMappingKey.MainKey != KeyCode.None)
                return takeInput && Config.StaticObjectMappingKey.IsDown();

            if (Config.StaticObjectMappingInterval <= 0) return false;

            _staticObjectMappingTimer += delta;
            if (_staticObjectMappingTimer < Config.StaticObjectMappingInterval) return false;
            _staticObjectMappingTimer = 0f;

            return true;
        }

        public static void Cleanup()
        {
            DynamicObjectMapping.Cleanup();
            StaticObjectMapping.Cleanup();
        }

        public static void Mapping(Player player, float delta, bool takeInput)
        {
            if (Game.IsPaused()) return;
            if (player != Player.m_localPlayer || !player.IsOwner()) return;
            if (!Config.EnableAutomaticMapping)
            {
                DynamicObjectMapping.RemoveCachedPins();
                StaticObjectMapping.RemoveCachedPins();
                return;
            }

            DynamicObjectMapping.Mapping(delta);

            if (CanStaticObjectMapping(delta, takeInput))
                StaticObjectMapping.Mapping();
        }

        [UsedImplicitly]
        public static bool SetSaveFlag(Vector3 pos, float radius)
        {
            var pinData = Map.GetClosestPin(pos, radius, x => x.m_ownerID == 0L && !x.m_save);
            if (pinData is null) return false;

            return StaticObjectMapping.SetSaveFlag(pinData) ||
                   DynamicObjectMapping.SetSaveFlag(pinData);
        }
    }

    internal static class DynamicObjectMapping
    {
        private static readonly Dictionary<ZDOID, Minimap.PinData> PinDataCache;
        private static readonly IDictionary<ZDOID, Minimap.PinData> VehiclePinCache;
        private static readonly HashSet<ZDOID> KnownObjects;
        private static readonly ISet<ZDOID> EmptyCacheKeys;

        static DynamicObjectMapping()
        {
            PinDataCache = new Dictionary<ZDOID, Minimap.PinData>();
            VehiclePinCache = new Dictionary<ZDOID, Minimap.PinData>();
            KnownObjects = new HashSet<ZDOID>();
            EmptyCacheKeys = new HashSet<ZDOID>(0);
        }

        private static bool GetAnimal(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Animal.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningAnimal.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetMonster(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Monster.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningMonster.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetVehicle(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (MappingObject.Vehicle.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningVehicle.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        public static void Cleanup()
        {
            PinDataCache.Clear();
            VehiclePinCache.Clear();
            KnownObjects.Clear();
        }

        public static void OnObjectDestroy(Component component)
        {
            if (!Config.EnableAutomaticMapping) return;
            if (component.GetComponent<Ship>() || component.GetComponent<Vagon>())
            {
                if (!Objects.GetZdoid(component, out var uniqueId)) return;
                if (!VehiclePinCache.TryGetValue(uniqueId, out var pin)) return;
                Map.RemovePin(pin);
            }
        }

        public static void RemoveCachedPins(ISet<ZDOID> excludes = null)
        {
            if (excludes is null)
                excludes = EmptyCacheKeys;

            foreach (var pair in PinDataCache.Where(x => !excludes.Contains(x.Key)).ToList())
            {
                PinDataCache.Remove(pair.Key);
                if (!pair.Value.m_save)
                    Map.RemovePin(pair.Value);
            }
        }

        public static bool SetSaveFlag(Minimap.PinData pinData)
        {
            foreach (var pair in PinDataCache
                         .Where(pair => ReferenceEquals(pair.Value, pinData)).ToList())
            {
                pair.Value.m_save = true;
                PinDataCache.Remove(pair.Key);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    Automatics.L10N.Localize("@message_automatic_mapping_pin_saved",
                        pinData.m_name.Replace("\n", "")));
                return true;
            }

            return false;
        }

        public static void Mapping(float delta)
        {
            if (Config.DynamicObjectMappingRange <= 0) return;

            var origin = Player.m_localPlayer.transform.position;

            KnownObjects.Clear();

            foreach (var character in Character.GetAllCharacters())
            {
                if (character.IsPlayer()) continue;

                var distance = Vector3.Distance(origin, character.transform.position);
                if (distance > Config.DynamicObjectMappingRange) continue;

                var characterName = character.m_name;
                if (GetAnimal(characterName, out var data))
                {
                    if (!data.IsAllowed) continue;
                    if (character.IsTamed() && Config.NotPinningTamedAnimals) continue;
                    AddOrUpdatePin(character, delta);
                }
                else if (GetMonster(characterName, out data))
                {
                    if (!data.IsAllowed) continue;
                    AddOrUpdatePin(character, delta);
                }
            }

            if (Config.AllowPinningAnimal.Contains("Fish"))
                foreach (var fish in FishCache.GetAllInstance())
                {
                    var distance = Vector3.Distance(origin, fish.transform.position);
                    if (distance > Config.DynamicObjectMappingRange) continue;
                    AddOrUpdatePin(fish, delta);
                }

            if (Config.AllowPinningAnimal.Contains("Bird"))
                foreach (var bird in BirdCache.GetAllInstance())
                {
                    var distance = Vector3.Distance(origin, bird.transform.position);
                    if (distance > Config.DynamicObjectMappingRange) continue;
                    AddOrUpdatePin(bird, delta);
                }

            VehicleMapping(delta);

            RemoveCachedPins(KnownObjects);
        }

        private static void VehicleMapping(float delta)
        {
            if (!Config.AllowPinningVehicle.Any()) return;

            var vehicles = new List<Component>();

            vehicles.AddRange(ShipCache.GetAllInstance());
            vehicles.AddRange(Reflections.GetStaticField<Vagon, List<Vagon>>("m_instances") ??
                              new List<Vagon>(0));

            var origin = Player.m_localPlayer.transform.position;
            foreach (var vehicle in vehicles)
            {
                if (!Objects.GetZdoid(vehicle, out var uniqueId)) continue;

                var pos = vehicle.transform.position;
                if (Vector3.Distance(origin, pos) > Config.DynamicObjectMappingRange) continue;

                var name = Objects.GetName(vehicle);
                if (!GetVehicle(name, out var vehicleData) || !vehicleData.IsAllowed) continue;

                if (VehiclePinCache.TryGetValue(uniqueId, out var pin))
                {
                    pin.m_pos = Vector3.MoveTowards(pin.m_pos, pos, 200f * delta);
                }
                else
                {
                    pin = Map.AddPin(pos, name, true, CreateTarget(name));
                    VehiclePinCache.Add(uniqueId, pin);
                }
            }
        }

        private static void AddOrUpdatePin(Character character, float delta)
        {
            var uniqueId = character.GetZDOID();
            if (!KnownObjects.Add(uniqueId)) return;

            var pinName = character.m_name;
            var level = character.GetLevel();
            if (level > 1)
            {
                var symbol =
                    Automatics.L10N.Translate("@text_automatic_mapping_creature_level_symbol");
                var sb = new StringBuilder(pinName).Append("\n");
                for (var i = 1; i < level; i++) sb.Append(symbol);
                pinName = sb.ToString();
            }

            var pos = character.transform.position;
            if (!PinDataCache.TryGetValue(uniqueId, out var pinData))
                AddPin(uniqueId, pos, pinName, CreateTarget(character.m_name, level));
            else
                UpdatePin(pinData, pinName, pos, delta);
        }

        private static void AddOrUpdatePin(Component component, float delta)
        {
            if (!Objects.GetZdoid(component, out var uniqueId)) return;
            if (!KnownObjects.Add(uniqueId)) return;

            var pos = component.transform.position;
            string pinName;
            if (component is RandomFlyingBird bird)
            {
                var birdName = Objects.GetPrefabName(bird.gameObject).ToLower();
                pinName = $"@animal_{birdName}";
            }
            else
            {
                pinName = Objects.GetName(component);
            }

            if (!PinDataCache.TryGetValue(uniqueId, out var pinData))
                AddPin(uniqueId, pos, pinName, CreateTarget(pinName));
            else
                UpdatePin(pinData, pinData.m_name, pos, delta);
        }

        private static void AddPin(ZDOID uniqueId, Vector3 pos, string pinName, Target target)
        {
            PinDataCache.Add(uniqueId, Map.AddPin(pos, pinName, false, target));
        }

        private static void UpdatePin(Minimap.PinData pinData, string pinName, Vector3 pos,
            float delta)
        {
            if (!string.IsNullOrEmpty(pinData.m_name))
                pinData.m_name = pinName;

            if (pinData.m_pos != pos)
                pinData.m_pos = Vector3.MoveTowards(pinData.m_pos, pos, 200f * delta);
        }

        private static Target CreateTarget(string name, int level = 0)
        {
            return level <= 0
                ? new Target { name = name }
                : new Target { name = name, metadata = new MetaData { level = level } };
        }
    }

    internal static class StaticObjectMapping
    {
        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> ObjectMaskLazy;
        private static readonly Lazy<int> DungeonMaskLazy;
        private static readonly Dictionary<Collider, Component> StaticObjectCache;
        private static readonly Dictionary<MapPinIdentify, Minimap.PinData> PinDataCache;
        private static readonly HashSet<MapPinIdentify> KnownObjects;
        private static readonly ISet<MapPinIdentify> EmptyCacheKeys;

        private static float _lastCacheUpdateTime;

        private static int ObjectMask => ObjectMaskLazy.Value;
        private static int DungeonMask => DungeonMaskLazy.Value;

        static StaticObjectMapping()
        {
            ColliderBuffer = new Collider[4096];
            ObjectMaskLazy = new Lazy<int>(() => LayerMask.GetMask("item", "piece",
                "piece_nonsolid", "Default", "static_solid", "Default_small", "character",
                "character_net", /*TODO: "terrain",*/ "vehicle"));
            DungeonMaskLazy = new Lazy<int>(() => LayerMask.GetMask("character_trigger"));
            StaticObjectCache = new Dictionary<Collider, Component>();
            PinDataCache = new Dictionary<MapPinIdentify, Minimap.PinData>();
            KnownObjects = new HashSet<MapPinIdentify>();
            EmptyCacheKeys = new HashSet<MapPinIdentify>(0);
        }

        private static bool GetFlora(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Flora.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningFlora.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetMineral(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Mineral.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningMineral.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetSpawner(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Spawner.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningSpawner.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetOther(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (MappingObject.Other.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningOther.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetDungeon(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Dungeon.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningDungeon.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetSpot(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Spot.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningSpot.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static void RemoveCache(Minimap.PinData pinData)
        {
            foreach (var pair in PinDataCache.Where(x => ReferenceEquals(x.Value, pinData))
                         .ToList())
                PinDataCache.Remove(pair.Key);
        }

        [UsedImplicitly]
        public static void OnObjectDestroy(Component component, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticMapping) return;
            if (!Config.RemovePinsOfDestroyedObject) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            Minimap.PinData pinData = null;
            var name = Objects.GetName(component);
            if (GetFlora(name, out var data))
            {
                if (!data.IsAllowed) return;

                var node = component.GetComponent<FloraNode>();
                if (!node)
                    pinData = Map.RemovePin(component.transform.position);
                else if (node.Network.NodeCount <= 1)
                    pinData = Map.RemovePin(node.Network.Center);
            }
            else if (GetMineral(name, out data))
            {
                if (!data.IsAllowed) return;
                pinData = Map.RemovePin(GetCollidersCenter(GetMineralColliders()));
            }
            else if (GetSpawner(name, out data) ||
                     GetOther(name, out data))
            {
                if (!data.IsAllowed) return;
                pinData = Map.RemovePin(component.transform.position);
            }

            if (pinData != null)
                RemoveCache(pinData);

            IReadOnlyCollection<Collider> GetMineralColliders()
            {
                var empty = Array.Empty<Collider>();
                switch (component)
                {
                    case MineRock rock:
                        return Reflections.GetField<Collider[]>(rock, "m_hitAreas") ?? empty;
                    case MineRock5 rock5:
                        return rock5.gameObject.GetComponentsInChildren<Collider>() ?? empty;
                    case Destructible destructible:
                    {
                        var collider = destructible.GetComponentInChildren<Collider>();
                        return collider ? new[] { collider } : empty;
                    }
                    default:
                        return empty;
                }
            }

            Vector3 GetCollidersCenter(IReadOnlyCollection<Collider> colliders)
            {
                var pos = colliders.Aggregate(Vector3.zero,
                    (current, collider) => current + collider.bounds.center);
                return pos / colliders.Count;
            }
        }

        public static void Cleanup()
        {
            StaticObjectCache.Clear();
            PinDataCache.Clear();
            KnownObjects.Clear();
        }

        public static void RemoveCachedPins(ISet<MapPinIdentify> excludes = null)
        {
            if (excludes is null)
                excludes = EmptyCacheKeys;

            foreach (var pair in PinDataCache.Where(x => !excludes.Contains(x.Key)).ToList())
            {
                PinDataCache.Remove(pair.Key);
                if (!pair.Value.m_save)
                    Map.RemovePin(pair.Value);
            }
        }

        public static bool SetSaveFlag(Minimap.PinData pinData)
        {
            foreach (var pair in PinDataCache
                         .Where(x => ReferenceEquals(x.Value, pinData)).ToList())
            {
                pair.Value.m_save = true;
                PinDataCache.Remove(pair.Key);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    Automatics.L10N.Localize("@message_automatic_mapping_pin_saved",
                        pinData.m_name.Replace("\n", "")));
                return true;
            }

            return false;
        }

        public static void Mapping()
        {
            if (Config.StaticObjectMappingRange <= 0) return;

            var origin = Player.m_localPlayer.transform.position;

            CacheStaticObjects(origin);

            KnownObjects.Clear();

            foreach (var pair in StaticObjectCache)
            {
                var collider = pair.Key;
                var component = pair.Value;
                if (!collider || !component) continue;

                if (Vector3.Distance(origin, component.transform.position) >
                    Config.StaticObjectMappingRange) continue;

                var name = Objects.GetName(component);
                if (FloraMapping(component, name)) continue;
                if (MineralMapping(component, name)) continue;
                if (SpawnerMapping(component, name)) continue;
                if (OtherMapping(component, name)) continue;
                if (PortalMapping(component, name)) continue;
            }

            foreach (var location in from x in ZoneSystem.instance.m_locationInstances.Values
                     where Vector3.Distance(origin, x.m_position) <= Config.LocationMappingRange
                     select x)
            {
                if (DungeonMapping(location)) continue;
                if (SpotMapping(location)) continue;
            }

            RemoveCachedPins(KnownObjects);
        }

        private static void CacheStaticObjects(Vector3 origin)
        {
            if (Time.time - _lastCacheUpdateTime < Config.StaticObjectCachingInterval) return;
            _lastCacheUpdateTime = Time.time;

            var range = Config.StaticObjectMappingRange;
            if (range <= 0) return;

            StaticObjectCache.Clear();
            foreach (var (collider, component, _) in Objects.GetInsideSphere(origin, range * 1.5f,
                         GetStaticObject, ColliderBuffer, ObjectMask))
                if (!StaticObjectCache.ContainsKey(collider))
                    StaticObjectCache.Add(collider, component);
        }

        private static Component GetStaticObject(Collider collider)
        {
            if (StaticObjectCache.TryGetValue(collider, out var component)) return component;

            component = AsStaticObject(collider);
            StaticObjectCache.Add(collider, component);
            return component;
        }

        private static Component AsStaticObject(Collider collider)
        {
            var component = collider.GetComponentInParent<IDestructible>() as Component;
            if (!component)
                component = collider.GetComponentInParent<Interactable>() as Component;
            if (!component)
                component = collider.GetComponentInParent<Hoverable>() as Component;
            if (!component) return null;

            var name = Objects.GetName(component);
            if (string.IsNullOrEmpty(name)) return null;

            if (GetFlora(name, out var data)) return data.IsAllowed ? component : null;
            if (GetMineral(name, out data)) return data.IsAllowed ? component : null;
            if (GetSpawner(name, out data)) return data.IsAllowed ? component : null;
            if (GetOther(name, out data)) return data.IsAllowed ? component : null;

            var component2 = component.GetComponent<TeleportWorld>() as Component;
            if (component2) return Config.AllowPinningPortal ? component2 : null;

            return null;
        }

        private static bool FloraMapping(Component component, string name)
        {
            if (!GetFlora(name, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            if (!KnownObjects.Add(new MapPinIdentify(uniqueId))) return true;

            if (ValheimObject.Flora.GetName(data.Identifier, out var label))
                name = label;

            var flora = FloraNode.Find(x => x.transform.position == component.transform.position);
            if (!flora || !flora.IsValid()) return true;

            var network = flora.Network;
            if (network == null) return true;

            var save = Config.SaveStaticObjectPins;
            if (network.IsDirty)
            {
                var removed = Map.RemovePin(network.Center, predicate: x => true);
                if (removed != null)
                {
                    save = removed.m_save;
                    RemoveCache(removed);
                }

                network.Update();
            }

            foreach (var member in network.GetAllNodes().Where(x => x.IsValid()))
                KnownObjects.Add(new MapPinIdentify(member.UniqueId));

            var pos = network.Center;
            if (Map.HavePinInRange(pos, 1f)) return true;

            var size = network.NodeCount;
            var pinName = size > 1
                ? Automatics.L10N.LocalizeTextOnly("@text_automatic_mapping_flora_cluster_pin_name",
                    name, size)
                : name;

            AddPin(uniqueId, pos, pinName, save, CreateTarget(name));
            return true;
        }

        private static bool MineralMapping(Component component, string name)
        {
            IEnumerable<Collider> GetColliders()
            {
                var empty = Array.Empty<Collider>();
                switch (component)
                {
                    case MineRock rock:
                    {
                        var array = Reflections.GetField<Collider[]>(rock, "m_hitAreas");
                        if (array == null) return empty;

                        if (!Objects.GetZNetView(rock, out var zNetView)) return empty;

                        for (var i = 0; i < array.Length; i++)
                            if (zNetView.GetZDO().GetFloat("Health" + i, rock.m_health) <= 0f)
                                return empty;

                        return array;
                    }
                    case MineRock5 rock5:
                    {
                        if (!Reflections.InvokeMethod<bool>(rock5, "NonDestroyed")) return empty;
                        return rock5.gameObject.GetComponentsInChildren<Collider>() ?? empty;
                    }
                    case Destructible destructible:
                    {
                        var collider = destructible.GetComponentInChildren<Collider>();
                        return !collider ? empty : new[] { collider };
                    }
                    default:
                    {
                        return empty;
                    }
                }
            }

            if (!GetMineral(name, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            if (!KnownObjects.Add(new MapPinIdentify(uniqueId))) return true;

            if (ValheimObject.Mineral.GetName(data.Identifier, out var label))
                name = label;

            var pos = Vector3.zero;
            var count = 0;
            var maxHeight = float.MinValue;

            foreach (var collider in GetColliders())
            {
                ++count;
                var bounds = collider.bounds;
                pos += bounds.center;
                maxHeight = Mathf.Max(maxHeight, bounds.max.y);
            }

            if (count == 0 || pos == Vector3.zero) return true;

            pos /= count;

            if (Map.HavePinInRange(pos, 1f)) return true;

            if (Config.NeedToEquipWishboneForUndergroundMinerals)
                if (maxHeight < ZoneSystem.instance.GetGroundHeight(pos))
                {
                    var items = Player.m_localPlayer.GetInventory().GetEquipedtems();
                    if (items.Select(x => x.m_shared.m_name).All(x => x != "$item_wishbone"))
                        return true;
                }

            AddPin(uniqueId, pos, name, CreateTarget(name));
            return true;
        }

        private static bool SpawnerMapping(Component component, string name)
        {
            if (!GetSpawner(name, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            if (!KnownObjects.Add(new MapPinIdentify(uniqueId))) return true;

            if (ValheimObject.Spawner.GetName(data.Identifier, out var label))
                name = label;

            var position = component.transform.position;
            if (!Map.HavePinInRange(position, 1f))
                AddPin(uniqueId, position, name, CreateTarget(name));

            return true;
        }

        private static bool OtherMapping(Component component, string name)
        {
            if (!GetOther(name, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            if (!KnownObjects.Add(new MapPinIdentify(uniqueId))) return true;

            if (MappingObject.Other.GetName(data.Identifier, out var label))
                name = label;

            var position = component.transform.position;
            if (!Map.HavePinInRange(position, 1f))
                AddPin(uniqueId, position, name, CreateTarget(name));

            return true;
        }

        private static bool PortalMapping(Component component, string name)
        {
            if (!Config.AllowPinningPortal) return false;

            var portal = component.GetComponent<TeleportWorld>();
            if (!portal) return false;

            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            if (!KnownObjects.Add(new MapPinIdentify(uniqueId))) return true;

            var tag = portal.GetText();
            var pinName = string.IsNullOrEmpty(tag)
                ? Automatics.L10N.Translate("@text_automatic_mapping_empty_portal_tag")
                : tag;

            var pos = component.transform.position;
            var pinData = Map.GetClosestPin(pos);
            if (pinData == null)
                AddPin(uniqueId, pos, pinName, true, CreateTarget(name));
            else
                pinData.m_name = pinName;

            return true;
        }

        private static bool DungeonMapping(ZoneSystem.LocationInstance instance)
        {
            Teleport GetTeleport(Collider collider)
            {
                return collider.GetComponent<Teleport>();
            }

            var prefabName = instance.m_location.m_prefabName;
            if (!GetDungeon(prefabName, out var data)) return false;
            if (!data.IsAllowed) return true;

            if (!ValheimObject.Dungeon.GetName(data.Identifier, out var name))
                name = $"@location_{prefabName.ToLower()}";

            var pos = instance.m_position;
            var radius = instance.m_location.m_exteriorRadius;
            foreach (var (collider, _, _) in Objects
                         .GetInsideSphere(pos, radius, GetTeleport, ColliderBuffer, DungeonMask)
                         .OrderBy(x => x.distance))
            {
                var entrance = collider.bounds.center;
                if (!KnownObjects.Add(new MapPinIdentify(entrance))) return true;

                if (!Map.HavePinInRange(entrance, 1f))
                    AddPin(ZDOID.None, entrance, name, CreateTarget(name));

                return true;
            }

            if (!KnownObjects.Add(new MapPinIdentify(pos))) return true;

            if (!Map.HavePinInRange(pos, radius, x => x.m_name == name))
            {
                AddPin(ZDOID.None, pos, name, CreateTarget(name));
                Automatics.Logger.Warning(() => $"Dungeon {name} has no entrance.");
            }

            return true;
        }

        private static bool SpotMapping(ZoneSystem.LocationInstance instance)
        {
            var prefabName = instance.m_location.m_prefabName;
            if (!GetSpot(prefabName, out var data)) return false;
            if (!data.IsAllowed) return true;

            var pos = instance.m_position;
            if (!KnownObjects.Add(new MapPinIdentify(pos))) return true;

            if (!ValheimObject.Spot.GetName(data.Identifier, out var name))
                name = $"@location_{prefabName.ToLower()}";

            if (!Map.HavePinInRange(pos, 1f))
                AddPin(ZDOID.None, pos, name, CreateTarget(name));

            return true;
        }

        private static void AddPin(ZDOID uniqueId, Vector3 pos, string pinName, bool save,
            Target target)
        {
            var pinData = Map.AddPin(pos, pinName, save, target);
            var identify = uniqueId.IsNone()
                ? new MapPinIdentify(pos)
                : new MapPinIdentify(uniqueId);
            PinDataCache.Add(identify, pinData);
        }

        private static void AddPin(ZDOID uniqueId, Vector3 pos, string pinName, Target target)
        {
            AddPin(uniqueId, pos, pinName, Config.SaveStaticObjectPins, target);
        }

        private static Target CreateTarget(string name)
        {
            return new Target { name = name };
        }

        public struct MapPinIdentify : IEquatable<MapPinIdentify>
        {
            public readonly ZDOID UniqueId;
            public readonly Vector3 Pos;
            private int _hash;

            private MapPinIdentify(ZDOID uniqueId, Vector3 pos)
            {
                UniqueId = uniqueId;
                Pos = pos;
                _hash = 0;
            }

            public MapPinIdentify(ZDOID uniqueId) : this(uniqueId, Vector3.zero)
            {
            }

            public MapPinIdentify(Vector3 pos) : this(ZDOID.None, pos)
            {
            }

            public bool IsUniqueId()
            {
                return UniqueId.userID != 0 && UniqueId.id != 0;
            }

            public bool IsPos()
            {
                return Pos != Vector3.zero;
            }

            public override int GetHashCode()
            {
                if (_hash == 0)
                    _hash = UniqueId.GetHashCode() ^ Pos.GetHashCode();
                return _hash;
            }

            public bool UniqueIdEquals(ZDOID id)
            {
                return UniqueId == id;
            }

            public bool PosEquals(Vector3 pos)
            {
                return Pos == pos;
            }

            public bool Equals(MapPinIdentify other)
            {
                if (IsUniqueId() && other.IsUniqueId()) return UniqueIdEquals(other.UniqueId);
                if (IsPos() && other.IsPos()) return PosEquals(other.Pos);
                return false;
            }
        }
    }
}