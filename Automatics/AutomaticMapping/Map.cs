using System;
using System.Collections.Generic;
using HarmonyLib;
using ModUtils;
using Splatform;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class Map
    {
        // Bound once, by the first Attach, so Map query helpers skip the
        // per-call Harmony reflection lookups. EmptyPinList is the safe
        // return when Minimap.instance is not yet live.
        private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>> _pinsRef;
        private static Action<Minimap> _updatePinsInvoker;
        private static Func<Minimap, Vector3, bool> _isExploredInvoker;
        private static readonly List<Minimap.PinData> EmptyPinList = new List<Minimap.PinData>();
        private static readonly HashSet<Minimap.PinData> AutomaticPins =
            new HashSet<Minimap.PinData>();

        private static Minimap ValheimMap => Minimap.instance;

        /// <summary>
        ///     Binds the minimap accessors and builds the pin index from the pins the minimap has.
        ///     Called from the Minimap.Start postfix for a new minimap, and when the module is
        ///     turned on for the one that already exists.
        /// </summary>
        public static void Attach(Minimap map)
        {
            // Bind each accessor in its own try/catch so a future Valheim
            // update that reshapes Minimap.m_pins or renames UpdatePins only
            // disables the affected slot. The Reflections.* fallbacks in
            // GetAllPins / RefreshPins go through Harmony's Traverse wrapper
            // and tolerate a null cache entry; IsExplored intentionally fails
            // closed so unexplored-pin hiding never reveals pins on bind failure.
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

            if (_isExploredInvoker == null)
            {
                try
                {
                    var method = AccessTools.Method(typeof(Minimap), "IsExplored",
                        new[] { typeof(Vector3) });
                    _isExploredInvoker = method != null
                        ? AccessTools.MethodDelegate<Func<Minimap, Vector3, bool>>(method)
                        : null;
                    if (_isExploredInvoker == null)
                        Automatics.Logger.Warning(() =>
                            "Minimap.IsExplored not found; hiding unexplored automatic pins will fail closed.");
                }
                catch (Exception e)
                {
                    Automatics.Logger.Warning(() =>
                        $"Failed to bind Minimap.IsExplored delegate; hiding unexplored automatic pins will fail closed: {e.Message}");
                    _isExploredInvoker = null;
                }
            }

            // Minimap.Start invokes LoadMapData before this postfix runs,
            // so AddPin_Postfix coverage depends on module load order.
            // A minimap the module is turned on for has pins the index
            // never saw at all. Rebuilding from the live list covers both,
            // and also drops stale PinData references from a prior session.
            AutomaticPins.Clear();
            PinIndex.Clear();
            var pins = GetAllPins(map);
            for (var i = 0; i < pins.Count; i++)
            {
                PinIndex.Track(pins[i]);
                if (IsPersistedAutomaticPin(pins[i]))
                    TrackAutomaticPin(pins[i]);
            }
        }

        /// <summary>
        ///     Drops the pin index and the set of pins recognised as automatic. A disable calls it
        ///     after it has removed the pins it held; the next <see cref="Attach" /> rebuilds both
        ///     from the pins the minimap has then.
        /// </summary>
        public static void Detach()
        {
            AutomaticPins.Clear();
            PinIndex.Clear();
        }

        public static List<Minimap.PinData> GetAllPins()
        {
            return GetAllPins(ValheimMap);
        }

        private static List<Minimap.PinData> GetAllPins(Minimap map)
        {
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
            Predicate<Minimap.PinData> predicate = null, bool includeInactive = false)
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
                        if (!includeInactive && !IsActive(pinData)) continue;

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
                    if (!includeInactive && !IsActive(pinData)) continue;

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

        public static void TrackAutomaticPin(Minimap.PinData pinData)
        {
            if (pinData != null)
                AutomaticPins.Add(pinData);
        }

        public static void UntrackAutomaticPin(Minimap.PinData pinData)
        {
            if (pinData != null)
                AutomaticPins.Remove(pinData);
        }

        public static void ClearAutomaticPins()
        {
            AutomaticPins.Clear();
        }

        public static bool IsExplored(Vector3 pos)
        {
            var map = ValheimMap;
            if (!map || _isExploredInvoker == null) return false;
            return _isExploredInvoker(map, pos);
        }

        public static bool ShouldSuppressTransientAutomaticPin(Vector3 pos)
        {
            return ShouldFilterAutomaticPinAt(pos);
        }

        public static bool ShouldHideAutomaticPin(Minimap.PinData pinData)
        {
            // Called from the transpiled UpdatePins for every pin: stopped, the game draws them all.
            if (!Module.Running) return false;

            // Cheap feature gate first: with unexplored-pin hiding disabled (the
            // default) this collapses to bool reads, skipping the per-pin hash
            // lookups in IsAutomaticPin and the IsExplored map call. Inlines
            // ShouldFilterAutomaticPinAt (ShouldFilterAutomaticPins && !IsExplored).
            return pinData != null &&
                   ShouldFilterAutomaticPins() &&
                   IsAutomaticPin(pinData) &&
                   !IsExplored(pinData.m_pos);
        }

        public static void DestroyHiddenAutomaticPinMarkers()
        {
            if (!Module.Running) return;
            if (!ShouldFilterAutomaticPins()) return;

            var pins = GetAllPins();
            for (var i = 0; i < pins.Count; i++)
            {
                var pinData = pins[i];
                if (pinData == null || !pinData.m_uiElement) continue;
                if (ShouldHideAutomaticPin(pinData))
                    DestroyPinMarker(pinData);
            }
        }

        internal static bool IsAutomaticPin(Minimap.PinData pinData)
        {
            return AutomaticPins.Contains(pinData) ||
                   DynamicObjectMapping.OwnsPin(pinData) ||
                   StaticObjectMapping.OwnsPin(pinData);
        }

        private static bool IsPersistedAutomaticPin(Minimap.PinData pinData)
        {
            return pinData != null &&
                   pinData.m_save &&
                   pinData.m_ownerID == 0L &&
                   pinData.m_author.IsValid &&
                   TryGetLocalPlatformUserId(out var userId) &&
                   pinData.m_author == userId;
        }

        private static void MarkAutomaticPin(Minimap.PinData pinData)
        {
            TrackAutomaticPin(pinData);
            if (pinData == null || !pinData.m_save) return;
            if (TryGetLocalPlatformUserId(out var userId))
                pinData.m_author = userId;
        }

        private static bool TryGetLocalPlatformUserId(out PlatformUserID userId)
        {
            userId = PlatformUserID.None;
            var platform = PlatformManager.DistributionPlatform;
            var localUser = platform?.LocalUser;
            if (localUser == null) return false;

            userId = localUser.PlatformUserID;
            return userId.IsValid;
        }

        private static bool ShouldFilterAutomaticPinAt(Vector3 pos)
        {
            return ShouldFilterAutomaticPins() && !IsExplored(pos);
        }

        private static bool ShouldFilterAutomaticPins()
        {
            return Module.Running &&
                   Config.EnableAutomaticMapping &&
                   Config.HideUnexploredAutomaticMappingPins;
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

        // The name a pin added through AddPin is stored with: an Automatics token (@...) is written
        // as the $automatics_... key the game's localization resolves.
        public static string StoredPinName(string name)
        {
            return name.StartsWith("@") ? "$automatics_" + name.Substring(1) : name;
        }

        private static Minimap.PinData AddPin(Vector3 pos, Minimap.PinType type, string name,
            bool save)
        {
            name = StoredPinName(name);
            var pinData = ValheimMap.AddPin(pos, type, name, save, false);
            MarkAutomaticPin(pinData);
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
