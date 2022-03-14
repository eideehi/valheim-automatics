using static Automatics.ValheimObject;
using static Automatics.ValheimLocation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class StaticMapPinning
    {
        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> LazyObjectMask;
        private static readonly Lazy<int> LazyDungeonMask;

        public static int ObjectMask => LazyObjectMask.Value;

        private static ConditionalWeakTable<Collider, MonoBehaviour> _objectCache;
        private static float _lastRunningTime;
        private static bool _busy;

        static StaticMapPinning()
        {
            ColliderBuffer = new Collider[1024];
            LazyObjectMask = new Lazy<int>(() =>
                LayerMask.GetMask("Default", "static_solid", "Default_small", "piece_nonsolid", "item"));
            LazyDungeonMask = new Lazy<int>(() => LayerMask.GetMask("character_trigger"));
            _objectCache = new ConditionalWeakTable<Collider, MonoBehaviour>();
        }

        public static void ClearObjectCache()
        {
            _objectCache = new ConditionalWeakTable<Collider, MonoBehaviour>();
        }

        public static void RemovePin(Vector3 pos)
        {
            var pin = Map.Pins.FirstOrDefault(x => x.m_save && x.m_pos == pos);
            if (pin != null)
                Map.RemovePin(pin);
        }

        public static void Run(Vector3 origin)
        {
            if (Time.time - _lastRunningTime < Config.StaticObjectSearchInterval) return;

            if (_busy) return;
            _busy = true;

            ObjectPinning(origin);
            LocationPinning(origin);

            _lastRunningTime = Time.time;
            _busy = false;
        }

        private static void ObjectPinning(Vector3 origin)
        {
            if (Config.StaticObjectSearchRange <= 0) return;

            var knownId = new HashSet<ZDOID> { ZDOID.None };
            foreach (var @object in
                     from x in GetNearbyObjects(origin)
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
            if (!(@object is Pickable pickable)) return false;
            if (!Flora.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag)) return true;

            var flora = FloraObject.Find(x => x.transform.position == pickable.transform.position);
            if (!flora.IsValid()) return true;

            var cluster = flora.Cluster;
            if (cluster == null) return true;

            if (cluster.IsDirty)
            {
                RemovePin(cluster.Center);
                cluster.Update();
            }

            foreach (var member in cluster.Members.Where(x => x.IsValid()))
                knownId.Add(member.ZdoId);

            var pos = cluster.Center;
            if (Map.HavePinInRange(pos, 1f)) return true;

            var size = cluster.Size;
            if (size == 1)
                Map.AddPin(pos, L10N.Translate(name), true);
            else if (size > 1)
                Map.AddPin(pos, L10N.Localize("@pin_cluster_format", name, size), true);

            return true;
        }

        private static bool VeinPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!(@object is IDestructible)) return false;
            if (!Vein.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag)) return true;

            var position = @object.transform.position;
            if (Map.HavePinInRange(position, 1f)) return true;

            if (Config.InGroundVeinsNeedWishbone)
            {
                var collider = @object.GetComponentInChildren<Collider>();
                var objectMaxY = collider ? collider.bounds.max.y : position.y;
                if (objectMaxY < ZoneSystem.instance.GetGroundHeight(position))
                {
                    var items = Player.m_localPlayer.GetInventory().GetEquipedtems();
                    if (items.Select(x => x.m_shared.m_name).All(x => x != "$item_wishbone")) return true;
                }
            }

            Map.AddPin(position, L10N.Translate(name), true);
            return true;
        }

        private static bool SpawnerPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!@object.GetComponent<SpawnArea>()) return false;
            if (!Spawner.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag)) return true;

            var position = @object.transform.position;
            if (Map.HavePinInRange(position, 1f)) return true;

            Map.AddPin(position, L10N.Translate(name), true);
            return true;
        }

        private static bool OtherPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!Other.GetFlag(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag) || !Other.GetName(flag, out name)) return true;

            var position = @object.transform.position;
            if (Map.HavePinInRange(position, 1f)) return true;

            Map.AddPin(position, L10N.Localize(name), true);
            return true;
        }

        private static void LocationPinning(Vector3 origin)
        {
            if (Config.LocationSearchRange <= 0) return;

            foreach (var teleport in
                     from x in Utility.GetObjectsInSphere(origin, Config.LocationSearchRange,
                         x => x.GetComponent<Teleport>(), ColliderBuffer, LazyDungeonMask.Value)
                     where !Map.HavePinInRange(x.Item1.transform.position, 1f)
                     select x.Item1)
            {
                if (!Dungeon.GetFlag(teleport.m_enterText, out var flag)) continue;
                if (!Config.IsAllowPinning(flag)) continue;

                Map.AddPin(teleport.transform.position, L10N.Translate(teleport.m_enterText), true);
            }

            foreach (var instance in from x in ZoneSystem.instance.m_locationInstances.Values
                     where Vector3.Distance(origin, x.m_position) <= Config.LocationSearchRange &&
                           !Map.HavePinInRange(x.m_position, 1f)
                     select x)
            {
                if (!Spot.GetFlag(instance.m_location.m_prefabName, out var flag)) continue;
                if (!Config.IsAllowPinning(flag) || !Spot.GetName(flag, out var name)) continue;

                Map.AddPin(instance.m_position, L10N.Translate(name), true);
            }
        }

        private static IEnumerable<(MonoBehaviour, float)> GetNearbyObjects(Vector3 pos)
        {
            return Utility.GetObjectsInSphere(pos, Config.StaticObjectSearchRange, GetObject, ColliderBuffer,
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
    }
}