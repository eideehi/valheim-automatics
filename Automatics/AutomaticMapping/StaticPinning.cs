using ModUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    using Dungeon = Valheim.Dungeon.Flags;
    using Spot = Valheim.Spot.Flags;
    using static ValheimObject;

    internal static class StaticPinning
    {
        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> ObjectMaskLazy;
        private static readonly Lazy<int> DungeonMaskLazy;

        private static ConditionalWeakTable<Collider, Component> _objectCache;
        private static float _lastRunningTime;
        private static bool _busy;

        static StaticPinning()
        {
            ColliderBuffer = new Collider[4096];
            ObjectMaskLazy = new Lazy<int>(() => LayerMask.GetMask("Default", "static_solid", "Default_small",
                "character_trigger", "piece_nonsolid", "item"));
            DungeonMaskLazy = new Lazy<int>(() => LayerMask.GetMask("character_trigger"));
            _objectCache = new ConditionalWeakTable<Collider, Component>();
        }

        private static int ObjectMask => ObjectMaskLazy.Value;

        private static int DungeonMask => DungeonMaskLazy.Value;

        public static void OnSettingChanged(object sender, EventArgs e)
        {
            _objectCache = new ConditionalWeakTable<Collider, Component>();
        }

        public static void OnFloraSettingChanged(object sender, EventArgs e)
        {
            Automatics.AddTimer(nameof(ClearFlora), ClearFlora, 3f);
        }

        private static void ClearFlora()
        {
            if (_busy)
            {
                Automatics.AddTimer(nameof(ClearFlora), ClearFlora, 0.1f);
                return;
            }

            _busy = true;

            foreach (var pickable in PickableCache.GetAllInstance())
            {
                var flora = pickable.GetComponent<FloraObject>();
                if (flora == null) continue;

                Map.RemovePin(flora.Network?.Center ?? Vector3.zero);
                UnityEngine.Object.Destroy(flora);
            }

            Automatics.AddTimer(nameof(SetFlora), SetFlora, 0.1f);
        }

        private static void SetFlora()
        {
            foreach (var pickable in PickableCache.GetAllInstance())
            {
                var name = Objects.GetName(pickable);
                var isAllowedFlora = Core.IsFlora(name, out var data) && data.IsAllowed;
                if (!isAllowedFlora) continue;

                var flora = pickable.GetComponent<FloraObject>();
                if (flora == null)
                    pickable.gameObject.AddComponent<FloraObject>();
            }

            _busy = false;
        }

        public static void Run(Vector3 origin, bool takeInput)
        {
            if (!Config.EnableAutomaticMapping) return;

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
            foreach (var (collider, component, _) in GetNearbyObjects(origin).OrderBy(x => x.distance))
            {
                if (Objects.GetZdoid(component, out var id) && !knownId.Add(id)) continue;

                var name = Objects.GetName(component);
                if (FloraPinning(collider, component, name, knownId)) continue;
                if (MineralPinning(collider, component, name, knownId)) continue;
                if (SpawnerPinning(collider, component, name, knownId)) continue;
                if (OtherPinning(collider, component, name, knownId)) continue;
            }
        }

        private static bool FloraPinning(Collider collider, Component component, string name, ISet<ZDOID> knownId)
        {
            if (!Core.IsFlora(name, out var data)) return false;
            if (!data.IsAllowed) return true;

            if (!(component is Pickable))
                Automatics.Logger.Warning(() => $"Flora {name} {collider.bounds.center} is not Pickable.");

            var flora = FloraObject.Find(x => x.transform.position == component.transform.position);
            if (flora == null || !flora.IsValid()) return true;

            var cluster = flora.Network;
            if (cluster == null) return true;

            if (cluster.IsDirty)
            {
                Map.RemovePin(cluster.Center);
                cluster.Update();
            }

            foreach (var member in cluster.GetAllNodes().Where(x => x.IsValid()))
                knownId.Add(member.ZdoId);

            var position = cluster.Center;
            if (Map.HavePinInRange(position, 1f)) return true;

            var size = cluster.NodeCount;
            var pinName = size > 1
                ? Automatics.L10N.Localize("@text_automatic_mapping_flora_cluster_pin_name", name, size)
                : Automatics.L10N.Translate(name);

            Map.AddPin(position, pinName, true, CreateTarget(name));
            return true;
        }

        private static bool MineralPinning(Collider collider, Component component, string name,
            ISet<ZDOID> knownId)
        {
            if (!Core.IsMineral(name, out var data)) return false;
            if (!data.IsAllowed) return true;

            if (!(component is IDestructible))
                Automatics.Logger.Warning(() => $"Mineral deposit {name} {collider.bounds.center} is not IDestructible.");

            var position = collider.bounds.center;
            if (Map.HavePinInRange(position, 1f)) return true;

            if (Config.NeedToEquipWishboneForUndergroundDeposits)
            {
                var maxHeight = collider.bounds.max.y;
                if (maxHeight < ZoneSystem.instance.GetGroundHeight(position))
                {
                    var items = Player.m_localPlayer.GetInventory().GetEquipedtems();
                    if (items.Select(x => x.m_shared.m_name).All(x => x != "$item_wishbone")) return true;
                }
            }

            Map.AddPin(position, Automatics.L10N.Translate(name), true, CreateTarget(name));
            return true;
        }

        private static bool SpawnerPinning(Collider collider, Component component, string name, ISet<ZDOID> knownId)
        {
            if (!Core.IsSpawner(name, out var data)) return false;
            if (!data.IsAllowed) return true;

            if (component.GetComponent<SpawnArea>() == null)
                Automatics.Logger.Warning(() => $"Spawner {name} {collider.bounds.center} is not SpawnArea.");

            var position = component.transform.position;
            if (Map.HavePinInRange(position, 1f)) return true;

            Map.AddPin(position, Automatics.L10N.Translate(name), true, CreateTarget(name));
            return true;
        }

        private static bool OtherPinning(Collider collider, Component component, string name, ISet<ZDOID> knownId)
        {
            if (!Core.IsOther(name, out var data)) return false;
            if (!data.IsAllowed) return true;

            if (data.Other != Other.None)
                GetOtherName(data.Other, out name);

            if (data.Other == Other.Portal)
            {
                PortalPinning(component);
            }
            else
            {
                var position = component.transform.position;
                if (Map.HavePinInRange(position, 1f)) return true;

                Map.AddPin(position, Automatics.L10N.Localize(name), true, new PinningTarget { name = name });
            }

            return true;
        }

        private static void PortalPinning(Component component)
        {
            var tag = "";

            var portal = component.GetComponent<TeleportWorld>();
            if (portal)
                tag = portal.GetText();

            var name = string.IsNullOrEmpty(tag) ? Automatics.L10N.Translate("@text_automatic_mapping_empty_portal_tag") : tag;
            var position = component.transform.position;
            if (Map.FindPinInRange(position, 1f, out var data) && data.m_name == name) return;

            if (data == null)
                Map.AddPin(position, Minimap.PinType.Icon4, name, true);
            else
                data.m_name = name;
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
            Teleport GetTeleport(Collider collider) => collider.GetComponent<Teleport>();

            var prefabName = instance.m_location.m_prefabName;
            if (!Core.IsDungeon(prefabName, out var data)) return false;
            if (!data.IsAllowed) return true;

            string name;
            if (data.Dungeon != Dungeon.None && !data.IsCustom)
                Valheim.Dungeon.GetName(data.Dungeon, out name);
            else
                name = $"@location_{prefabName.ToLower()}";

            var teleports = Objects.GetInsideSphere(instance.m_position, 16f, GetTeleport, ColliderBuffer, DungeonMask);
            if (teleports.Count > 0)
            {
                foreach (var position in teleports
                             .OrderBy(x => x.distance)
                             .Take(1)
                             .Where(x => !Map.HavePinInRange(x.collider.bounds.center, 1f))
                             .Select(x => x.collider.bounds.center))
                {
                    Map.AddPin(position, Automatics.L10N.Translate(name), true, CreateTarget(name));
                }
            }
            else if (!Map.HavePinInRange(instance.m_position, 1f))
            {
                Map.AddPin(instance.m_position, Automatics.L10N.Translate(name), true, CreateTarget(name));
                Automatics.Logger.Warning(() => $"Dungeon {name} has no entrance.");
            }

            return true;
        }

        private static bool SpotPinning(ZoneSystem.LocationInstance instance)
        {
            var prefabName = instance.m_location.m_prefabName;
            if (!Core.IsSpot(prefabName, out var data)) return false;
            if (!data.IsAllowed) return true;

            string name;
            if (data.Spot != Spot.None && !data.IsCustom)
                Valheim.Spot.GetName(data.Spot, out name);
            else
                name = $"@location_{prefabName.ToLower()}";

            Map.AddPin(instance.m_position, Automatics.L10N.Translate(name), true, CreateTarget(name));
            return true;
        }

        private static PinningTarget CreateTarget(string name)
        {
            return new PinningTarget { name = name };
        }

        private static IEnumerable<(Collider, Component, float distance)> GetNearbyObjects(Vector3 pos)
        {
            return Objects.GetInsideSphere(pos, Config.StaticObjectSearchRange, GetObject, ColliderBuffer, ObjectMask);
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
            var component = collider.GetComponentInParent<Pickable>() ??
                            collider.GetComponentInParent<IDestructible>() as Component ??
                            collider.GetComponentInParent<Interactable>() as Component ??
                            collider.GetComponentInParent<Hoverable>() as Component;

            var name = Objects.GetName(component);
            if (string.IsNullOrEmpty(name)) return null;

            if (Core.IsFlora(name, out var flora)) return flora.IsAllowed ? component : null;
            if (Core.IsMineral(name, out var deposit)) return deposit.IsAllowed ? component : null;
            if (Core.IsSpawner(name, out var spawner)) return spawner.IsAllowed ? component : null;
            if (Core.IsOther(name, out var other)) return other.IsAllowed ? component : null;

            return null;
        }
    }
}