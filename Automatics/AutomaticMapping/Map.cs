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
            using (MappingProfiler.BeginScope(MappingProfiler.SlotGetClosestPin))
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

        public static bool ContainsPin(Minimap.PinData pinData)
        {
            return pinData != null && GetAllPins().Contains(pinData);
        }

        /// <summary>
        /// Updates a pin's position via a single funnelled entry point. The
        /// current implementation is a trivial shim that simply assigns
        /// <see cref="Minimap.PinData.m_pos"/>; C-1 will later replace the
        /// body with spatial-index-aware cell migration. Callers that mutate
        /// <c>m_pos</c> (AnimatePins / dirty Flora handling / any future pin
        /// movement path) must route writes through this helper so that the
        /// future index remains coherent without touching call sites again.
        /// </summary>
        public static void MovePin(Minimap.PinData pinData, Vector3 newPosition)
        {
            if (pinData == null) return;
            pinData.m_pos = newPosition;
        }

        public static void RefreshPins()
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotRefreshPins))
            {
                if (!ValheimMap) return;

                Reflections.InvokeMethod(ValheimMap, "UpdatePins");
            }
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
            if (pinData == null) return null;
            if (pinData.m_uiElement)
                DestroyPinMarker(pinData);

            if (!GetAllPins().Remove(pinData)) return null;

            Automatics.Logger.Debug(() =>
                $"Remove pin: [name: {EscapePinName(pinData.m_name)}, pos: {pinData.m_pos}, icon: {(int)pinData.m_type}]");
            AutomaticMapping.OnRemovePin(pinData);
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
