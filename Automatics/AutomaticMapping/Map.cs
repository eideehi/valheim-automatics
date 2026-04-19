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

            // Minimap.Start invokes LoadMapData before this postfix runs,
            // so AddPin_Postfix coverage depends on module load order.
            // Rebuilding from the live list also drops stale PinData
            // references from a prior session.
            PinIndex.Clear();
            var pins = GetAllPins();
            for (var i = 0; i < pins.Count; i++)
                PinIndex.Track(pins[i]);
        }

        public static List<Minimap.PinData> GetAllPins()
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

        private static bool IsActive(Minimap.PinData pinData)
        {
            return pinData.m_uiElement && pinData.m_uiElement.gameObject.activeInHierarchy;
        }

        public static Minimap.PinData GetClosestPin(Vector3 pos, float radius = 1f,
            Predicate<Minimap.PinData> predicate = null)
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotGetClosestPin))
            {
                var radiusSq = radius * radius;
                Minimap.PinData result = null;
                var minDistanceSq = float.MaxValue;

                var minCx = Mathf.FloorToInt((pos.x - radius) / PinIndex.CellSize);
                var maxCx = Mathf.FloorToInt((pos.x + radius) / PinIndex.CellSize);
                var minCz = Mathf.FloorToInt((pos.z - radius) / PinIndex.CellSize);
                var maxCz = Mathf.FloorToInt((pos.z + radius) / PinIndex.CellSize);

                for (var cx = minCx; cx <= maxCx; cx++)
                for (var cz = minCz; cz <= maxCz; cz++)
                {
                    if (!PinIndex.TryGetCell(new CellKey(cx, cz), out var cellPins)) continue;
                    for (var i = 0; i < cellPins.Count; i++)
                    {
                        var pinData = cellPins[i];
                        if (!IsActive(pinData)) continue;

                        var dx = pos.x - pinData.m_pos.x;
                        var dz = pos.z - pinData.m_pos.z;
                        var distanceSq = dx * dx + dz * dz;
                        if (distanceSq > radiusSq || distanceSq >= minDistanceSq) continue;
                        if (predicate != null && !predicate(pinData)) continue;

                        result = pinData;
                        minDistanceSq = distanceSq;
                    }
                }

                var transients = PinIndex.TransientPins;
                for (var i = 0; i < transients.Count; i++)
                {
                    var pinData = transients[i];
                    if (!IsActive(pinData)) continue;

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

            var minCx = Mathf.FloorToInt((pos.x - radius) / PinIndex.CellSize);
            var maxCx = Mathf.FloorToInt((pos.x + radius) / PinIndex.CellSize);
            var minCz = Mathf.FloorToInt((pos.z - radius) / PinIndex.CellSize);
            var maxCz = Mathf.FloorToInt((pos.z + radius) / PinIndex.CellSize);

            for (var cx = minCx; cx <= maxCx; cx++)
            for (var cz = minCz; cz <= maxCz; cz++)
            {
                if (!PinIndex.TryGetCell(new CellKey(cx, cz), out var cellPins)) continue;
                for (var i = 0; i < cellPins.Count; i++)
                {
                    var pinData = cellPins[i];
                    if (!IsActive(pinData)) continue;

                    var dx = pos.x - pinData.m_pos.x;
                    var dz = pos.z - pinData.m_pos.z;
                    var distanceSq = dx * dx + dz * dz;
                    if (distanceSq > radiusSq) continue;
                    if (predicate != null && !predicate(pinData)) continue;

                    return true;
                }
            }

            var transients = PinIndex.TransientPins;
            for (var i = 0; i < transients.Count; i++)
            {
                var pinData = transients[i];
                if (!IsActive(pinData)) continue;

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

        /// <summary>
        /// Raw membership check with no UI-active filter, so pins without
        /// a live marker (just added or mid-rebuild) still return
        /// <c>true</c>. Use <see cref="GetClosestPin"/> when UI visibility
        /// matters.
        /// </summary>
        public static bool ContainsPin(Minimap.PinData pinData)
        {
            return PinIndex.Contains(pinData);
        }

        /// <summary>
        /// Writes <paramref name="newPosition"/> and migrates the spatial
        /// index cell when the move crosses a boundary. Transient and
        /// untracked pins fall through on the index side so callers do
        /// not need to know which collection owns the pin.
        /// </summary>
        public static void MovePin(Minimap.PinData pinData, Vector3 newPosition)
        {
            if (pinData == null) return;
            PinIndex.Move(pinData, newPosition);
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
