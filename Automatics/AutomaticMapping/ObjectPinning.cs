using System;
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
            if (!takeInput || Config.PinObjectKey.MainKey == KeyCode.None) return;
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
                Message(player, "@message_automatic_mapping_pin_no_hit");
                return null;
            }

            var component = hit.collider.GetComponentInParent<IDestructible>() as Component ??
                            hit.collider.GetComponentInParent<Interactable>() as Component ??
                            hit.collider.GetComponentInParent<Hoverable>() as Component ??
                            hit.collider.GetComponentInParent<Character>();
            var position = hit.point;
            var name = "";
            var type = Minimap.PinType.Icon3;
            if (component)
            {
                var internalName = GetInternalName(hit.collider, component);
                // A display name keeps a manual vehicle pin out of legacy automatic-pin
                // adoption, which matches the vehicle's untranslated name.
                name = Localization.instance.Localize(GetDisplayName(hit.collider, component, internalName));
                var character = component.GetComponent<Character>();
                type = IconPack.GetPinType(new Target
                {
                    name = internalName,
                    prefabName = Objects.GetPrefabName(component.gameObject),
                    metadata = character ? new MetaData { level = character.GetLevel() } : null
                });
                position = component.transform.position;
            }

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
