using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    using Object = Valheim.Object;
    using Location = Valheim.Location;

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
            ColliderBuffer = new Collider[4096];
            LazyObjectMask = new Lazy<int>(() =>
                LayerMask.GetMask("Default", "static_solid", "Default_small", "character_trigger", "piece_nonsolid",
                    "item"));
            LazyDungeonMask = new Lazy<int>(() => LayerMask.GetMask("character_trigger"));
            _objectCache = new ConditionalWeakTable<Collider, MonoBehaviour>();
        }

        public static bool IsFlora(Pickable pickable)
        {
            var name = Obj.GetName(pickable);
            return Object.IsFlora(name) || Config.IsCustomFlora(name);
        }

        public static bool IsVein(Destructible destructible)
        {
            var name = Obj.GetName(destructible);
            return Object.IsMineralDeposit(name) || Config.IsCustomFlora(name);
        }

        public static void ClearObjectCache()
        {
            _objectCache = new ConditionalWeakTable<Collider, MonoBehaviour>();
        }

        public static void Run(Vector3 origin, bool takeInput)
        {
            if (!Config.AutomaticMapPinningEnabled) return;

            if (Config.StaticObjectSearchKey.MainKey != KeyCode.None)
            {
                if (!takeInput || !Config.StaticObjectSearchKey.IsDown()) return;
            }
            else
            {
                if (Time.time - _lastRunningTime < Config.StaticObjectSearchInterval) return;
            }

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
                if (Obj.GetZdoid(@object, out var id) && !knownId.Add(id)) continue;

                var name = Obj.GetName(@object);
                if (FloraPinning(@object, name, ref knownId)) continue;
                if (VeinPinning(@object, name, ref knownId)) continue;
                if (SpawnerPinning(@object, name, ref knownId)) continue;
                if (OtherPinning(@object, name, ref knownId)) continue;
            }
        }

        private static bool FloraPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!(@object is Pickable pickable)) return false;
            if (Object.GetFlora(name, out var flag))
            {
                if (!Config.IsAllowPinning(flag)) return true;
            }
            else if (!Config.IsCustomFlora(name))
            {
                return false;
            }

            var flora = FloraObject.Find(x => x.transform.position == pickable.transform.position);
            if (!flora.IsValid()) return true;

            var cluster = flora.Network;
            if (cluster == null) return true;

            if (cluster.IsDirty)
            {
                Map.RemovePin(cluster.Center);
                cluster.Update();
            }

            foreach (var member in cluster.GetAllNodes().Where(x => x.IsValid()))
                knownId.Add(member.ZdoId);

            var pos = cluster.Center;
            if (Map.HavePinInRange(pos, 1f)) return true;

            var size = cluster.NodeCount;
            if (size == 1)
                Map.AddPin(pos, L10N.Translate(name), true, new Target { name = name });
            else if (size > 1)
                Map.AddPin(pos, L10N.Localize("@flora_cluster_format", name, size), true, new Target { name = name });

            return true;
        }

        private static bool VeinPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!(@object is IDestructible)) return false;
            if (Object.GetMineralDeposit(name, out var flag))
            {
                if (!Config.IsAllowPinning(flag)) return true;
            }
            else if (!Config.IsCustomVein(name))
            {
                return false;
            }

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

            Map.AddPin(position, L10N.Translate(name), true, new Target { name = name });
            return true;
        }

        private static bool SpawnerPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!@object.GetComponent<SpawnArea>()) return false;
            if (OtherObject.GetSpawner(name, out var flag))
            {
                if (!Config.IsAllowPinning(flag)) return true;
            }
            else if (!Config.IsCustomSpawner(name))
            {
                return false;
            }

            var position = @object.transform.position;
            if (Map.HavePinInRange(position, 1f)) return true;

            Map.AddPin(position, L10N.Translate(name), true, new Target { name = name });
            return true;
        }

        private static bool OtherPinning(MonoBehaviour @object, string name, ref HashSet<ZDOID> knownId)
        {
            if (!OtherObject.GetEtcetera(name, out var flag)) return false;
            if (!Config.IsAllowPinning(flag) || !OtherObject.GetEtceteraName(flag, out name)) return true;

            switch (flag)
            {
                case OtherObject.Etcetera.Portal:
                {
                    PortalPinning(@object);
                    break;
                }
                default:
                {
                    var position = @object.transform.position;
                    if (Map.HavePinInRange(position, 1f)) return true;

                    Map.AddPin(position, L10N.Localize(name), true, new Target { name = name });
                    break;
                }
            }

            return true;
        }

        private static void PortalPinning(MonoBehaviour @object)
        {
            var tag = "";

            var portal = @object.GetComponent<TeleportWorld>();
            if (portal)
                tag = portal.GetText();

            var name = string.IsNullOrEmpty(tag) ? L10N.Translate("@portal_tag_empty") : tag;
            var position = @object.transform.position;

            if (!Map.FindPinInRange(position, 1f, out var data) || data.m_name != name)
            {
                if (data == null)
                    Map.AddPin(position, Minimap.PinType.Icon4, name, true);
                else
                    data.m_name = name;
            }
        }

        private static void LocationPinning(Vector3 origin)
        {
            if (Config.LocationSearchRange <= 0) return;

            foreach (var instance in from x in ZoneSystem.instance.m_locationInstances.Values
                     where Vector3.Distance(origin, x.m_position) <= Config.LocationSearchRange &&
                           !Map.HavePinInRange(x.m_position, 1f)
                     select x)
            {
                if (DungeonPinning(instance)) continue;
                if (SpotPinning(instance)) continue;
            }
        }

        private static bool DungeonPinning(ZoneSystem.LocationInstance instance)
        {
            if (!Location.GetDungeon(instance.m_location.m_prefabName, out var flag)) return false;
            if (!Config.IsAllowPinning(flag) || !Location.GetDungeonName(flag, out var name)) return true;

            foreach (var teleport in
                     from x in Obj.GetInSphere(instance.m_position, 16f,
                         x => x.GetComponent<Teleport>(), ColliderBuffer, LazyDungeonMask.Value)
                     where !Map.HavePinInRange(x.Item1.transform.position, 1f)
                     select x.Item1)
            {
                Map.AddPin(teleport.transform.position, L10N.Translate(name), true, new Target { name = name });
                break;
            }

            return true;
        }

        private static bool SpotPinning(ZoneSystem.LocationInstance instance)
        {
            if (!Location.GetSpot(instance.m_location.m_prefabName, out var flag)) return false;
            if (!Config.IsAllowPinning(flag) || !Location.GetSpotName(flag, out var name)) return true;

            Map.AddPin(instance.m_position, L10N.Translate(name), true, new Target { name = name });
            return true;
        }

        private static IEnumerable<(MonoBehaviour, float)> GetNearbyObjects(Vector3 pos)
        {
            return Obj.GetInSphere(pos, Config.StaticObjectSearchRange, GetObject, ColliderBuffer,
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

            var name = Obj.GetName(@object);
            if (string.IsNullOrEmpty(name)) return null;

            if (Object.GetFlora(name, out var flag1)) return Config.IsAllowPinning(flag1) ? @object : null;
            if (Object.GetMineralDeposit(name, out var flag2)) return Config.IsAllowPinning(flag2) ? @object : null;
            if (OtherObject.GetSpawner(name, out var flag3)) return Config.IsAllowPinning(flag3) ? @object : null;
            if (OtherObject.GetEtcetera(name, out var flag4)) return Config.IsAllowPinning(flag4) ? @object : null;

            return null;
        }
    }
}