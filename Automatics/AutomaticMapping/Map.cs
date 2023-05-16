using System;
using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class Map
    {
        private static Minimap ValheimMap => Minimap.instance;

        private static List<Minimap.PinData> GetAllPins()
        {
            return Reflections.GetField<List<Minimap.PinData>>(ValheimMap, "m_pins");
        }

        private static void DestroyPinMarker(Minimap.PinData pinData)
        {
            Reflections.InvokeMethod(ValheimMap, "DestroyPinMarker", pinData);
        }

        public static Minimap.PinData GetPin(Predicate<Minimap.PinData> predicate)
        {
            return GetAllPins()
                .Where(x => x.m_uiElement && x.m_uiElement.gameObject.activeInHierarchy)
                .FirstOrDefault(predicate.Invoke);
        }

        public static Minimap.PinData GetClosestPin(Vector3 pos, float radius = 1f,
            Predicate<Minimap.PinData> predicate = null)
        {
            if (predicate == null)
                predicate = x => true;

            Minimap.PinData result = null;
            var minDistance = float.MaxValue;
            foreach (var pinData in GetAllPins().Where(x =>
                         x.m_uiElement && x.m_uiElement.gameObject.activeInHierarchy))
            {
                var distance = Utils.DistanceXZ(pos, pinData.m_pos);
                if (distance > radius || distance >= minDistance) continue;
                if (!predicate.Invoke(pinData)) continue;

                result = pinData;
                minDistance = distance;
            }

            return result;
        }

        public static bool HavePinInRange(Vector3 pos, float radius,
            Predicate<Minimap.PinData> predicate = null)
        {
            bool IsInRange(Minimap.PinData x) => Utils.DistanceXZ(x.m_pos, pos) <= radius;

            Predicate<Minimap.PinData> isValidPin;
            if (predicate == null)
                isValidPin = IsInRange;
            else
                isValidPin = x => IsInRange(x) && predicate(x);

            return GetPin(isValidPin) != null;
        }

        public static Minimap.PinData AddPin(Vector3 pos, string name, bool save, Target target)
        {
            return AddPin(pos, IconPack.GetPinType(target), name, save);
        }

        private static string EscapePinName(string name)
        {
            return name.Replace("\n", "");
        }

        private static Minimap.PinData AddPin(Vector3 pos, Minimap.PinType type, string name,
            bool save)
        {
            if (name.StartsWith("@")) name = "$automatics_" + name.Substring(1);
            var pinData = ValheimMap.AddPin(pos, type, name, save, false);
            Automatics.Logger.Debug(() =>
                $"Add pin: [name: {EscapePinName(name)}, pos: {pinData.m_pos}, icon: {(int)type}]");
            return pinData;
        }

        public static Minimap.PinData RemovePin(Minimap.PinData pinData)
        {
            if (pinData.m_uiElement)
                DestroyPinMarker(pinData);

            if (!GetAllPins().Remove(pinData)) return null;

            Automatics.Logger.Debug(() =>
                $"Remove pin: [name: {EscapePinName(pinData.m_name)}, pos: {pinData.m_pos}, icon: {(int)pinData.m_type}]");
            return pinData;
        }

        public static Minimap.PinData RemovePin(Vector3 pos, float radius = 1f,
            Predicate<Minimap.PinData> predicate = null)
        {
            if (predicate == null)
                predicate = x => x.m_save;

            var pinData = GetClosestPin(pos, radius, predicate);
            return pinData != null ? RemovePin(pinData) : null;
        }
    }
}