using static Automatics.ValheimCharacter;
using static Automatics.ValheimLocation;
using static Automatics.ValheimObject;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class AutomaticMapPinning
    {
        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> DynamicObjectMask;
        private static readonly Lazy<int> StaticObjectMask;
        private static readonly Lazy<int> DungeonMask;

        private static HashSet<DynamicPin> AllDynamicPin { get; }
        private static Minimap Map => Minimap.instance;

        private static float _lastStaticObjectSearch;

        static AutomaticMapPinning()
        {
            ColliderBuffer = new Collider[1024];
            DynamicObjectMask = new Lazy<int>(() => LayerMask.GetMask("character", "hitbox"));
            StaticObjectMask = new Lazy<int>(() =>
                LayerMask.GetMask("Default", "static_solid", "Default_small", "piece_nonsolid", "item"));
            DungeonMask = new Lazy<int>(() => LayerMask.GetMask("character_trigger"));
            AllDynamicPin = new HashSet<DynamicPin>();
        }

        public static bool IsActive()
        {
            return Config.AutomaticMapPinningEnabled && !Game.IsPaused() && Player.m_localPlayer;
        }

        public static void RemoveStaticPin(Vector3 pos)
        {
            var pin = GetAllPin().FirstOrDefault(x => x.m_save && x.m_pos == pos);
            if (pin == null)
            {
                TryUpdateFloraClusterPin(pos);
                return;
            }

            Map.RemovePin(pin);
            Log.Debug(() => $"Remove pin: [name: {pin.m_name}, pos: {pin.m_pos}]");
        }

        public static void OnRemovePin(Minimap.PinData pin)
        {
            if (pin.m_save) return;
            if (AllDynamicPin.RemoveWhere(x => x.Data.m_pos == pin.m_pos) > 0)
                Log.Debug(() => $"Remove pin: [name: {pin.m_name}, pos: {pin.m_pos}]");
        }

        public static void OnUpdate()
        {
            var player = Player.m_localPlayer;

            var location = Location.GetLocation(player.transform.position);
            if (location && location.m_hasInterior) return;

            DynamicPinning(player, Time.deltaTime);
            StaticPinning(player);
        }

        private static void DynamicPinning(Player player, float delta)
        {
            if (Config.DynamicObjectSearchRange <= 0)
            {
                (from x in AllDynamicPin select x.Data).ToList().ForEach(Map.RemovePin);
                return;
            }

            var knownId = new HashSet<ZDOID>();
            foreach (var @object in
                     from x in GetNearbyDynamicObjects(player.transform.position)
                     orderby x.Item2
                     select x.Item1)
            {
                if (Utility.GetZdoid(@object, out var id) && knownId.Add(id))
                    AddOrUpdateDynamicPin(id, @object.transform.position, GetDynamicObjectName(@object), delta);
            }

            (from x in AllDynamicPin where !knownId.Contains(x.Id) select x.Data).ToList().ForEach(Map.RemovePin);
        }

        private static string GetDynamicObjectName(MonoBehaviour @object)
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

        private static void AddOrUpdateDynamicPin(ZDOID id, Vector3 pos, string name, float delta)
        {
            var pin = AllDynamicPin.FirstOrDefault(x => x.Id == id);
            if (pin == null)
            {
                AllDynamicPin.Add(new DynamicPin(id, AddPin(pos, name, false)));
            }
            else
            {
                var data = pin.Data;

                data.m_name = name;

                if (data.m_pos != pos)
                    data.m_pos = Vector3.MoveTowards(data.m_pos, pos, 200f * delta);
            }
        }

        private static void StaticPinning(Player player)
        {
            if (Time.time - _lastStaticObjectSearch < Config.StaticObjectSearchInterval) return;
            StaticObjectPinning(player);
            LocationPinning(player);
            _lastStaticObjectSearch = Time.time;
        }

        private static void StaticObjectPinning(Player player)
        {
            if (Config.StaticObjectSearchRange <= 0) return;

            var knownId = new HashSet<ZDOID> { ZDOID.None };
            foreach (var @object in
                     from x in GetNearbyStaticObjects(player.transform.position)
                     orderby x.Item2
                     select x.Item1)
            {
                if (Utility.GetZdoid(@object, out var id) && !knownId.Add(id)) continue;

                var name = Utility.GetName(@object);
                if (FloraPinning(@object, name, ref knownId)) continue;
                if (VeinPinning(@object, name, ref knownId)) continue;
                if (SpawnerPinning(@object, name, ref knownId)) continue;
                if (OtherPinning(@object, name, ref knownId)) continue;
            }
        }

        private static bool FloraPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!(@object is Pickable flora)) return false;
            if (!Flora.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag)) return true;

            var cluster = new List<(ZDOID, Pickable)>();
            FindFloraCluster(ref cluster, flora, name);

            var count = cluster.Count;
            if (count <= 1)
            {
                var pos = flora.transform.position;
                if (!HavePinInRange(pos, 1f))
                    AddPin(pos, L10N.Translate(name), true);
            }
            else
            {
                var pos = Vector3.zero;
                foreach (var (id, pickable) in cluster)
                {
                    knownId.Add(id);
                    pos += pickable.transform.position;
                }

                pos /= count;

                if (!HavePinInRange(pos, 1f))
                    AddPin(pos, L10N.Localize("@pin_cluster_format", name, count), true);
            }

            return true;
        }

        private static void FindFloraCluster(ref List<(ZDOID, Pickable)> result, Pickable flora, string name)
        {
            if (!Utility.GetZdoid(flora, out var id) || result.Any(x => x.Item1 == id)) return;
            result.Add((id, flora));

            var origin = flora.transform.position;
            foreach (var (pickable, _) in Utility.GetObjectsInSphere(origin, Config.FloraPinMergeRange,
                         x => x.GetComponentInParent<Pickable>(), 256, StaticObjectMask.Value))
            {
                if (Utility.GetName(pickable) == name)
                    FindFloraCluster(ref result, pickable, name);
            }
        }

        private static void TryUpdateFloraClusterPin(Vector3 pos)
        {
            var (flora, _) = Utility.GetObjectsInSphere(pos, 0.1f, x => x.GetComponentInParent<Pickable>(),
                ColliderBuffer, StaticObjectMask.Value).FirstOrDefault();
            var name = Utility.GetName(flora);
            if (!Flora.GetFlag(name, out _)) return;

            var cluster = new List<(ZDOID, Pickable)>();
            FindFloraCluster(ref cluster, flora, name);

            var count = cluster.Count;
            if (count <= 1) return;

            var old = Vector3.zero;
            var @new = Vector3.zero;
            foreach (var memberPos in from Pickable member in cluster select member.transform.position)
            {
                if (memberPos != pos) @new += memberPos;
                old += memberPos;
            }

            old /= count;
            if (!HavePinInRange(old, 1f)) return;
            Map.RemovePin(old, 1f);

            @new /= count - 1;
            AddPin(@new, L10N.Localize("@pin_cluster_format", name, count - 1), true);
        }

        private static bool VeinPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!(@object is IDestructible)) return false;
            if (!Vein.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag)) return true;

            var position = @object.transform.position;
            if (HavePinInRange(position, 1f)) return true;

            var height = ZoneSystem.instance.GetGroundHeight(position);
            if (position.y < height && Config.InGroundVeinsNeedWishbone)
            {
                var items = Player.m_localPlayer.GetInventory().GetEquipedtems();
                if (items.Select(x => x.m_shared.m_name).All(x => x != "$item_wishbone")) return true;
            }

            AddPin(position, L10N.Translate(name), true);
            return true;
        }

        private static bool SpawnerPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!@object.GetComponent<SpawnArea>()) return false;
            if (!Spawner.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag)) return true;

            var position = @object.transform.position;
            if (HavePinInRange(position, 1f)) return true;

            AddPin(position, L10N.Translate(name), true);
            return true;
        }

        private static bool OtherPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!Other.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag) || !Other.GetName(flag, out name)) return true;

            var position = @object.transform.position;
            if (HavePinInRange(position, 1f)) return true;

            AddPin(position, L10N.Localize(name), true);
            return true;
        }

        private static void LocationPinning(Player player)
        {
            if (Config.LocationSearchRange <= 0) return;

            var origin = player.transform.position;

            foreach (var teleport in
                     from x in Utility.GetObjectsInSphere(origin, Config.LocationSearchRange,
                         x => x.GetComponent<Teleport>(), ColliderBuffer, DungeonMask.Value)
                     where !HavePinInRange(x.Item1.transform.position, 1f)
                     select x.Item1)
            {
                if (!Dungeon.GetFlag(teleport.m_enterText, out var flag)) continue;
                if (!Config.IsAllowPinning(flag)) continue;

                AddPin(teleport.transform.position, L10N.Translate(teleport.m_enterText), true);
            }

            foreach (var instance in from x in ZoneSystem.instance.m_locationInstances
                     where !HavePinInRange(x.Value.m_position, 1f) &&
                           Vector3.Distance(origin, x.Value.m_position) <= Config.LocationSearchRange
                     select x.Value)
            {
                if (!Spot.GetFlag(instance.m_location.m_prefabName, out var flag)) continue;
                if (!Config.IsAllowPinning(flag) || !Spot.GetName(flag, out var name)) continue;

                AddPin(instance.m_position, L10N.Translate(name), true);
            }
        }

        private static IEnumerable<Minimap.PinData> GetAllPin()
        {
            return Reflection.GetField<List<Minimap.PinData>>(Map, "m_pins");
        }

        private static bool HavePinInRange(Vector3 pos, float radius)
        {
            return GetAllPin().Any(data => Utils.DistanceXZ(data.m_pos, pos) <= radius);
        }

        private static IEnumerable<(MonoBehaviour, float)> GetNearbyDynamicObjects(Vector3 pos)
        {
            return Utility.GetObjectsInSphere(pos, Config.DynamicObjectSearchRange, GetValidDynamicObject,
                ColliderBuffer, DynamicObjectMask.Value);
        }

        private static MonoBehaviour GetValidDynamicObject(Collider collider)
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

        private static IEnumerable<(MonoBehaviour, float)> GetNearbyStaticObjects(Vector3 pos)
        {
            return Utility.GetObjectsInSphere(pos, Config.StaticObjectSearchRange, GetValidStaticObject, ColliderBuffer,
                StaticObjectMask.Value);
        }

        private static MonoBehaviour GetValidStaticObject(Collider collider)
        {
            var @object =
                collider.GetComponentInParent<Pickable>() ??
                collider.GetComponentInParent<IDestructible>() as MonoBehaviour ??
                collider.GetComponentInParent<Interactable>() as MonoBehaviour ??
                collider.GetComponentInParent<Hoverable>() as MonoBehaviour;

            var name = Utility.GetName(@object);
            if (string.IsNullOrEmpty(name)) return null;

            if (Flora.GetFlag(name, out var flag1)) return Config.IsAllowPinning(flag1) ? @object : null;
            if (Vein.GetFlag(name, out var flag2)) return Config.IsAllowPinning(flag2) ? @object : null;
            if (Spawner.GetFlag(name, out var flag3)) return Config.IsAllowPinning(flag3) ? @object : null;
            if (Other.GetFlag(name, out var flag4)) return Config.IsAllowPinning(flag4) ? @object : null;

            return null;
        }

        private static Minimap.PinData AddPin(Vector3 pos, string name, bool save)
        {
            var data = Map.AddPin(pos, Minimap.PinType.Icon3, name, save, false);
            Log.Debug(() => $"Add pin: [name: {data.m_name}, pos: {data.m_pos}]");
            return data;
        }

        private class DynamicPin
        {
            public readonly ZDOID Id;
            public readonly Minimap.PinData Data;

            public DynamicPin(ZDOID id, Minimap.PinData data)
            {
                Id = id;
                Data = data;
            }
        }
    }
}