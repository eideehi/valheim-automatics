using System;
using System.Collections.Generic;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class Map
    {
        // Cached once in OnMinimapStart so Map query helpers skip the
        // per-call Harmony reflection lookups. EmptyPinList is the safe
        // return when Minimap.instance is not yet live.
        private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>> _pinsRef;
        private static Action<Minimap> _updatePinsInvoker;
        private static readonly List<Minimap.PinData> EmptyPinList = new List<Minimap.PinData>();

        private static Minimap ValheimMap => Minimap.instance;

        public static void OnMinimapStart()
        {
            // Bind each accessor in its own try/catch so a future Valheim
            // update that reshapes Minimap.m_pins or renames UpdatePins only
            // disables the affected slot. The Reflections.* fallbacks in
            // GetAllPins / RefreshPins go through Harmony's Traverse wrapper
            // and tolerate a null cache entry.
            if (_pinsRef == null)
            {
                try
                {
                    _pinsRef = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
                }
                catch (Exception e)
                {
                    Automatics.Logger.Warning(() =>
                        $"Failed to bind Minimap.m_pins field ref; falling back to reflection: {e.Message}");
                    _pinsRef = null;
                }
            }

            if (_updatePinsInvoker == null)
            {
                try
                {
                    var method = AccessTools.Method(typeof(Minimap), "UpdatePins");
                    _updatePinsInvoker = method != null
                        ? AccessTools.MethodDelegate<Action<Minimap>>(method)
                        : null;
                    if (_updatePinsInvoker == null)
                        Automatics.Logger.Warning(() =>
                            "Minimap.UpdatePins not found; falling back to reflection.");
                }
                catch (Exception e)
                {
                    Automatics.Logger.Warning(() =>
                        $"Failed to bind Minimap.UpdatePins delegate; falling back to reflection: {e.Message}");
                    _updatePinsInvoker = null;
                }
            }
        }

        private static List<Minimap.PinData> GetAllPins()
        {
            var map = ValheimMap;
            if (!map) return EmptyPinList;
            return _pinsRef != null ? _pinsRef(map) : FallbackGetAllPins(map);
        }

        private static List<Minimap.PinData> FallbackGetAllPins(Minimap map)
        {
            return Reflections.GetField<List<Minimap.PinData>>(map, "m_pins") ?? EmptyPinList;
        }

        private static void DestroyPinMarker(Minimap.PinData pinData)
        {
            Reflections.InvokeMethod(ValheimMap, "DestroyPinMarker", pinData);
        }

        public static Minimap.PinData GetPin(Predicate<Minimap.PinData> predicate)
        {
            var pins = GetAllPins();
            for (var i = 0; i < pins.Count; i++)
            {
                var pinData = pins[i];
                if (!pinData.m_uiElement || !pinData.m_uiElement.gameObject.activeInHierarchy)
                    continue;
                if (predicate == null || predicate(pinData))
                    return pinData;
            }

            return null;
        }

        public static Minimap.PinData GetClosestPin(Vector3 pos, float radius = 1f,
            Predicate<Minimap.PinData> predicate = null)
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotGetClosestPin))
            {
                var radiusSq = radius * radius;
                var pins = GetAllPins();
                Minimap.PinData result = null;
                var minDistanceSq = float.MaxValue;

                for (var i = 0; i < pins.Count; i++)
                {
                    var pinData = pins[i];
                    if (!pinData.m_uiElement || !pinData.m_uiElement.gameObject.activeInHierarchy)
                        continue;

                    var dx = pos.x - pinData.m_pos.x;
                    var dz = pos.z - pinData.m_pos.z;
                    var distanceSq = dx * dx + dz * dz;
                    if (distanceSq > radiusSq || distanceSq >= minDistanceSq) continue;
                    if (predicate != null && !predicate(pinData)) continue;

                    result = pinData;
                    minDistanceSq = distanceSq;
                }

                return result;
            }
        }

        public static bool HavePinInRange(Vector3 pos, float radius,
            Predicate<Minimap.PinData> predicate = null)
        {
            var radiusSq = radius * radius;
            var pins = GetAllPins();
            for (var i = 0; i < pins.Count; i++)
            {
                var pinData = pins[i];
                if (!pinData.m_uiElement || !pinData.m_uiElement.gameObject.activeInHierarchy)
                    continue;

                var dx = pos.x - pinData.m_pos.x;
                var dz = pos.z - pinData.m_pos.z;
                var distanceSq = dx * dx + dz * dz;
                if (distanceSq > radiusSq) continue;
                if (predicate != null && !predicate(pinData)) continue;

                return true;
            }

            return false;
        }

        public static Minimap.PinData AddPin(Vector3 pos, string name, bool save, Target target)
        {
            return AddPin(pos, IconPack.GetPinType(target), name, save);
        }

        public static bool ContainsPin(Minimap.PinData pinData)
        {
            if (pinData == null) return false;
            var pins = GetAllPins();
            for (var i = 0; i < pins.Count; i++)
                if (ReferenceEquals(pins[i], pinData))
                    return true;
            return false;
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
                var map = ValheimMap;
                if (!map) return;

                if (_updatePinsInvoker != null)
                    _updatePinsInvoker(map);
                else
                    Reflections.InvokeMethod(map, "UpdatePins");
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
