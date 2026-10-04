using System;
using Automatics.Valheim;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class ObjectPinning
    {
        private static readonly AccessTools.FieldRef<Player, int> InteractMask =
            AccessTools.FieldRefAccess<Player, int>("m_interactMask");

        internal static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (!Module.Running) return;
            if (!player || player != Player.m_localPlayer || !player.IsOwner()) return;
            if (!takeInput || Minimap.InTextInput() || Config.PinObjectKey.MainKey == KeyCode.None) return;
            if (!Config.PinObjectKey.IsDown()) return;
            if (!GameCamera.instance) return;

            // Runs inside Player.Update every frame: an exception must not escape into it.
            try
            {
                var camera = GameCamera.instance.transform;
                TryPinFromRay(player, new Ray(camera.position, camera.forward));
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Failed to pin the object in view\n{e}");
            }
        }

        internal static Minimap.PinData TryPinFromRay(Player player, Ray ray)
        {
            if (!Module.Running) return null;
            if (!player || player != Player.m_localPlayer || !player.IsOwner()) return null;
            if (!Minimap.instance || Localization.instance == null) return null;

            if (!TryResolveFromRay(player, ray, out var aimed))
            {
                Message(player, "@message_automatic_mapping_pin_no_hit");
                return null;
            }
            var position = aimed.Position;
            var name = aimed.Name;
            var type = aimed.Type;

            // Include pins without a live marker: outside the minimap view, with the minimap
            // hidden, or added this frame.
            if (Map.GetClosestPin(position, 2f,
                    pin => pin.m_save && !Map.IsAutomaticPin(pin) && pin.m_name == name,
                    includeInactive: true) != null)
            {
                Message(player, "@message_automatic_mapping_pin_duplicate");
                return null;
            }

            var added = Minimap.instance.AddPin(position, type, name, true, false);
            Message(player, "@message_automatic_mapping_pin_added");
            return added;
        }

        internal sealed class AimedObject
        {
            public Component Component;
            public GameObject Instance;
            public string Name;
            public Vector3 Position;
            public Minimap.PinType Type;
            public ZDOID UniqueId;
            public Location Location;
            public string StoredName;
        }

        internal static bool TryResolveFromRay(Player player, Ray ray, out AimedObject aimed,
            AimedObject cached = null)
        {
            aimed = null;
            // The nearest hit can be the player's body in third-person view. Keep looking
            // past their colliders, but never through another object or the terrain.
            var hits = Physics.RaycastAll(ray, Config.PinObjectRange, InteractMask(player));
            var nearest = float.PositiveInfinity;
            var hit = default(RaycastHit);
            foreach (var candidate in hits)
            {
                if (candidate.collider.transform.IsChildOf(player.transform)) continue;
                if (candidate.distance >= nearest) continue;
                nearest = candidate.distance;
                hit = candidate;
            }

            if (!hit.collider)
            {
                return false;
            }

            var component = hit.collider.GetComponentInParent<IDestructible>() as Component ??
                            hit.collider.GetComponentInParent<Interactable>() as Component ??
                            hit.collider.GetComponentInParent<Hoverable>() as Component ??
                            hit.collider.GetComponentInParent<Character>();
            var aimedComponent = component;
            var position = hit.point;
            var name = "";
            var type = Minimap.PinType.Icon3;
            if (TryResolveLocation(hit, component, out var location, out var locationName))
            {
                aimedComponent = location;
                name = Automatics.L10N.TranslateInternalName(locationName);
                type = IconPack.GetPinType(new Target
                {
                    name = locationName,
                    prefabName = Objects.GetPrefabName(location.gameObject)
                });
                position = location.transform.position;
            }
            else if (component)
            {
                var internalName = GetInternalName(hit.collider, component);
                // A display name keeps a manual vehicle pin out of legacy automatic-pin
                // adoption, which matches the vehicle's untranslated name.
                name = cached != null && cached.Instance == component.gameObject && cached.Component == component
                    ? cached.Name
                    : Localization.instance.Localize(GetDisplayName(hit.collider, component, internalName));
                var character = component.GetComponent<Character>();
                type = IconPack.GetPinType(new Target
                {
                    name = internalName,
                    prefabName = Objects.GetPrefabName(component.gameObject),
                    metadata = character ? new MetaData { level = character.GetLevel() } : null
                });
                position = component.transform.position;
            }

            var instance = aimedComponent ? aimedComponent.gameObject : hit.collider.gameObject;
            var nview = instance.GetComponent<ZNetView>();
            aimed = new AimedObject
            {
                Component = aimedComponent,
                Instance = instance,
                Name = name,
                Position = position,
                Type = type,
                UniqueId = nview && nview.GetZDO() != null ? nview.GetZDO().m_uid : ZDOID.None,
                Location = location,
                StoredName = location ? Map.StoredPinName(locationName) : name
            };
            return true;
        }

        private static bool TryResolveLocation(RaycastHit hit, Component component,
            out Location location, out string name)
        {
            location = null;
            name = "";
            var collider = hit.collider;
            var piece = collider.GetComponentInParent<Piece>();
            if ((piece && piece.IsPlacedByPlayer()) || collider.GetComponentInParent<Character>() ||
                collider.GetComponentInParent<MineRock>() || collider.GetComponentInParent<MineRock5>() ||
                (component && ValheimObject.Mineral.GetIdentify(Objects.GetName(component), out _)))
                return false;

            // Loose items and vegetation remain individual objects even in a location hierarchy.
            if (collider.GetComponentInParent<ItemDrop>() || collider.GetComponentInParent<Pickable>() ||
                collider.GetComponentInParent<TreeBase>() || collider.GetComponentInParent<TreeLog>())
                return false;

            // The game spawns a location's networked parts (its pieces, destructibles and
            // interactables) as scene roots, never under the Location, so only its static
            // structure is found here. Pieces always go to the area test below, which picks the
            // nearest location when areas overlap. An unrecognized parent also tries that test.
            if (!piece)
            {
                var parent = collider.GetComponentInParent<Location>();
                if (!parent)
                {
                    var proxy = collider.GetComponentInParent<LocationProxy>();
                    if (proxy) parent = proxy.GetComponentInChildren<Location>();
                }
                if (parent && TryGetLocationLabel(parent, out name))
                {
                    location = parent;
                    return true;
                }
            }

            // Non-piece networked parts of a location, such as destructibles and interactables
            // like rune stones, keep their own pins. World-generated pieces, terrain and unnamed
            // hits resolve by area. The interact mask has no liquid layer, so a ray over water
            // or tar reaches the terrain below and resolves like other ground.
            if (!piece && component && !collider.GetComponentInParent<Heightmap>())
                return false;

            // Dungeon interiors sit high above their zone, under a generator that is not a child
            // of the Location; resolve them by zone, as Location.GetLocation does.
            if (Character.InInterior(hit.point))
            {
                var zoneLocation = Location.GetZoneLocation(hit.point);
                if (!zoneLocation || !TryGetLocationLabel(zoneLocation, out name)) return false;
                location = zoneLocation;
                return true;
            }

            var locations = StaticObjectMapping.LoadedLocations;
            if (locations == null) return false;
            Location nearest = null;
            var closest = float.PositiveInfinity;
            foreach (var candidate in locations)
            {
                if (!candidate || !candidate.gameObject.activeInHierarchy) continue;
                // Strictly inside, as Location.IsInside tests.
                var distance = Utils.DistanceXZ(hit.point, candidate.transform.position);
                if (distance >= candidate.m_exteriorRadius || distance >= closest) continue;
                closest = distance;
                nearest = candidate;
            }
            // An unrecognized nearest location leaves the usual object or ground pin; a farther
            // recognized one is not used instead.
            if (!nearest || !TryGetLocationLabel(nearest, out name))
            {
                name = "";
                return false;
            }
            location = nearest;
            return true;
        }

        private static bool TryGetLocationLabel(Location location, out string name)
        {
            if (StaticObjectMapping.TryGetLocationName(Objects.GetPrefabName(location.gameObject), out name) &&
                IsTranslated(name))
                return true;
            name = location.m_discoverLabel;
            if (IsTranslated(name)) return true;
            name = "";
            return false;
        }

        // False for an empty name or one whose translation is missing, so a pin never shows a raw
        // key or a prefab name.
        private static bool IsTranslated(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var localized = Automatics.L10N.TranslateInternalName(name);
            if (L10N.IsInternalName(name) && localized ==
                (name.StartsWith("@") ? "automatics_" + name.Substring(1) : name.Substring(1)))
                return false;
            return !string.IsNullOrWhiteSpace(localized) && !localized.Contains("$") && !localized.Contains("@");
        }

        // The name the icon pack keys on: a building piece's $-token, which its WearNTear lacks,
        // otherwise the name Automatic Mapping uses for the object.
        private static string GetInternalName(Collider collider, Component component)
        {
            var piece = collider.GetComponentInParent<Piece>();
            if (piece && !string.IsNullOrEmpty(piece.m_name)) return piece.m_name;
            return Objects.GetName(component);
        }

        // Objects.GetName falls back to the prefab name when the component has no m_name and no
        // Hoverable of its own; the hover name on a parent is what the player sees instead.
        private static string GetDisplayName(Collider collider, Component component, string internalName)
        {
            if (internalName != Objects.GetPrefabName(component.gameObject)) return internalName;
            var hoverable = collider.GetComponentInParent<Hoverable>();
            var hoverName = hoverable is HoverText text ? text.m_text : hoverable?.GetHoverName();
            return string.IsNullOrEmpty(hoverName) ? internalName : hoverName;
        }

        private static void Message(Player player, string key)
        {
            player.Message(MessageHud.MessageType.Center, Automatics.L10N.Localize(key));
        }
    }
}
