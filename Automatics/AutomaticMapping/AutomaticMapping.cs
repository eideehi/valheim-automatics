using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Automatics.Valheim;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class AutomaticMapping
    {
        private static void RemoveCachedPins()
        {
            DynamicObjectMapping.RemoveCachedPins();
            StaticObjectMapping.RemoveCachedPins();
            Map.RefreshPins();
        }

        private static bool CanRun(Player player)
        {
            return !Game.IsPaused() &&
                   player == Player.m_localPlayer &&
                   player.IsOwner() &&
                   ZNetScene.instance.IsAreaReady(player.transform.position);
        }

        public static void Cleanup()
        {
            Navigation.Cleanup();
            DynamicObjectMapping.Cleanup();
            StaticObjectMapping.Cleanup();
            MappingProfiler.Reset();
        }

        public static void DynamicMapping(Player player, float delta)
        {
            MappingProfiler.FlushIfDue();

            if (!CanRun(player)) return;
            if (!Config.EnableAutomaticMapping)
            {
                RemoveCachedPins();
                return;
            }

            DynamicObjectMapping.Mapping(delta);
            Map.RefreshPins();
        }

        public static void Mapping(Player player, float delta, bool takeInput)
        {
            if (!CanRun(player)) return;
            if (!Config.EnableAutomaticMapping)
            {
                RemoveCachedPins();
                return;
            }

            StaticObjectMapping.Mapping(delta, takeInput);
        }

        public static void OnRemovePin(Minimap.PinData pinData)
        {
            Navigation.OnRemovePin(pinData);
            DynamicObjectMapping.OnRemovePin(pinData);
            StaticObjectMapping.OnRemovePin(pinData);
        }

        public static void AnimatePins(float delta)
        {
            DynamicObjectMapping.AnimatePins(delta);
        }

        [UsedImplicitly]
        public static bool SetSaveFlag(Vector3 pos, float radius)
        {
            var pinData = Map.GetClosestPin(pos, radius, x => x.m_ownerID == 0L && !x.m_save);
            if (pinData is null) return false;

            return StaticObjectMapping.SetSaveFlag(pinData) ||
                   DynamicObjectMapping.SetSaveFlag(pinData);
        }
    }

    internal static class DynamicObjectMapping
    {
        private const float PinSmoothingTime = 0.2f;
        private const float PinSnapDistance = 96f;

        private static readonly Dictionary<ZDOID, Minimap.PinData> PinDataCache;
        private static readonly Dictionary<Minimap.PinData, ZDOID> PinKeyCache;
        private static readonly Dictionary<ZDOID, Vector3> PinTargetCache;
        private static readonly Dictionary<ZDOID, Vector3> PinVelocityCache;
        private static readonly IDictionary<ZDOID, Minimap.PinData> VehiclePinCache;
        private static readonly Dictionary<ZDOID, Vector3> VehiclePinTargetCache;
        private static readonly Dictionary<ZDOID, Vector3> VehiclePinVelocityCache;
        private static readonly HashSet<ZDOID> KnownObjects;
        private static readonly ISet<ZDOID> EmptyCacheKeys;
        private static readonly List<Fish> FishBuffer;
        private static readonly List<RandomFlyingBird> BirdBuffer;
        private static readonly List<Piece> ShipBuffer;
        private static readonly List<Component> VehicleBuffer;

        static DynamicObjectMapping()
        {
            PinDataCache = new Dictionary<ZDOID, Minimap.PinData>();
            PinKeyCache = new Dictionary<Minimap.PinData, ZDOID>();
            PinTargetCache = new Dictionary<ZDOID, Vector3>();
            PinVelocityCache = new Dictionary<ZDOID, Vector3>();
            VehiclePinCache = new Dictionary<ZDOID, Minimap.PinData>();
            VehiclePinTargetCache = new Dictionary<ZDOID, Vector3>();
            VehiclePinVelocityCache = new Dictionary<ZDOID, Vector3>();
            KnownObjects = new HashSet<ZDOID>();
            EmptyCacheKeys = new HashSet<ZDOID>(0);
            FishBuffer = new List<Fish>();
            BirdBuffer = new List<RandomFlyingBird>();
            ShipBuffer = new List<Piece>();
            VehicleBuffer = new List<Component>();
        }

        private static bool GetAnimal(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Animal.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningAnimal.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetMonster(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Monster.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningMonster.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetVehicle(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (MappingObject.Vehicle.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningVehicle.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        public static void Cleanup()
        {
            PinDataCache.Clear();
            PinKeyCache.Clear();
            PinTargetCache.Clear();
            PinVelocityCache.Clear();
            VehiclePinCache.Clear();
            VehiclePinTargetCache.Clear();
            VehiclePinVelocityCache.Clear();
            KnownObjects.Clear();
        }

        public static void OnObjectDestroy(Component component)
        {
            if (!Config.EnableAutomaticMapping) return;
            if (component.GetComponent<Ship>() || component.GetComponent<Vagon>())
            {
                if (!Objects.GetZdoid(component, out var uniqueId)) return;
                if (!VehiclePinCache.TryGetValue(uniqueId, out var pin)) return;
                VehiclePinTargetCache.Remove(uniqueId);
                VehiclePinVelocityCache.Remove(uniqueId);
                Map.RemovePin(pin);
            }
        }

        public static void RemoveCachedPins(ISet<ZDOID> excludes = null)
        {
            if (!PinDataCache.Any()) return;

            if (excludes is null)
                excludes = EmptyCacheKeys;

            foreach (var key in PinDataCache.Keys.Where(x => !excludes.Contains(x)).ToList())
            {
                var pinData = PinDataCache[key];
                PinDataCache.Remove(key);
                PinKeyCache.Remove(pinData);
                PinTargetCache.Remove(key);
                PinVelocityCache.Remove(key);
                if (!pinData.m_save)
                    Map.RemovePin(pinData);
            }
        }

        public static void OnRemovePin(Minimap.PinData pinData)
        {
            RemovePinFromCache(pinData);
        }

        public static bool SetSaveFlag(Minimap.PinData pinData)
        {
            if (!RemovePinFromCache(pinData)) return false;

            pinData.m_save = true;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                Automatics.L10N.Localize("@message_automatic_mapping_pin_saved",
                    pinData.m_name.Replace("\n", "")));
            return true;
        }

        public static void AnimatePins(float delta)
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotAnimatePins))
            {
                if (delta <= 0f) return;

                foreach (var pair in PinDataCache)
                {
                    if (!PinTargetCache.TryGetValue(pair.Key, out var target)) continue;

                    var pinData = pair.Value;
                    if (pinData != null && pinData.m_pos != target)
                        Map.MovePin(pinData, InterpolatePinPosition(PinVelocityCache, pair.Key,
                            pinData.m_pos, target, delta));
                }

                foreach (var pair in VehiclePinCache)
                {
                    if (!VehiclePinTargetCache.TryGetValue(pair.Key, out var target)) continue;

                    var pinData = pair.Value;
                    if (pinData != null && pinData.m_pos != target)
                        Map.MovePin(pinData, InterpolatePinPosition(VehiclePinVelocityCache,
                            pair.Key, pinData.m_pos, target, delta));
                }
            }
        }

        public static void Mapping(float delta)
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotDynamicMapping))
            {
                if (Config.DynamicObjectMappingRange <= 0)
                {
                    RemoveCachedPins();
                    return;
                }

                var origin = Player.m_localPlayer.transform.position;

                KnownObjects.Clear();

                foreach (var character in Character.GetAllCharacters())
                {
                    if (character.IsPlayer()) continue;

                    var distance = Vector3.Distance(origin, character.transform.position);
                    if (distance > Config.DynamicObjectMappingRange) continue;

                    var characterName = character.m_name;
                    if (GetAnimal(characterName, out var data))
                    {
                        if (!data.IsAllowed) continue;
                        if (character.IsTamed() && Config.NotPinningTamedAnimals) continue;
                        AddOrUpdatePin(character, delta);
                    }
                    else if (GetMonster(characterName, out data))
                    {
                        if (!data.IsAllowed) continue;
                        AddOrUpdatePin(character, delta);
                    }
                }

                if (Config.AllowPinningAnimal.Contains("Fish"))
                {
                    FishCache.Fill(FishBuffer);
                    foreach (var fish in FishBuffer)
                    {
                        var distance = Vector3.Distance(origin, fish.transform.position);
                        if (distance > Config.DynamicObjectMappingRange) continue;
                        AddOrUpdatePin(fish, delta);
                    }
                }

                if (Config.AllowPinningAnimal.Contains("Bird"))
                {
                    BirdCache.Fill(BirdBuffer);
                    foreach (var bird in BirdBuffer)
                    {
                        var distance = Vector3.Distance(origin, bird.transform.position);
                        if (distance > Config.DynamicObjectMappingRange) continue;
                        AddOrUpdatePin(bird, delta);
                    }
                }

                VehicleMapping(delta);

                RemoveCachedPins(KnownObjects);
            }
        }

        private static void VehicleMapping(float delta)
        {
            if (!Config.AllowPinningVehicle.Any()) return;

            VehicleBuffer.Clear();
            ShipCache.Fill(ShipBuffer);
            foreach (var ship in ShipBuffer)
                VehicleBuffer.Add(ship);

            var wagons = Reflections.GetStaticField<Vagon, List<Vagon>>("m_instances");
            if (wagons != null)
                foreach (var wagon in wagons)
                    VehicleBuffer.Add(wagon);

            var origin = Player.m_localPlayer.transform.position;
            foreach (var vehicle in VehicleBuffer)
            {
                if (!Objects.GetZdoid(vehicle, out var uniqueId)) continue;

                var pos = vehicle.transform.position;
                if (Vector3.Distance(origin, pos) > Config.DynamicObjectMappingRange) continue;

                var name = Objects.GetName(vehicle);
                if (!GetVehicle(name, out var vehicleData) || !vehicleData.IsAllowed) continue;

                if (VehiclePinCache.TryGetValue(uniqueId, out var pin))
                {
                    VehiclePinTargetCache[uniqueId] = pos;
                }
                else
                {
                    pin = Map.AddPin(pos, name, true, CreateTarget(vehicle.gameObject, name));
                    VehiclePinCache.Add(uniqueId, pin);
                    VehiclePinTargetCache[uniqueId] = pos;
                    VehiclePinVelocityCache[uniqueId] = Vector3.zero;
                }
            }
        }

        private static void AddOrUpdatePin(Character character, float delta)
        {
            var uniqueId = character.GetZDOID();
            if (!KnownObjects.Add(uniqueId)) return;

            var pinName = character.m_name;
            var level = character.GetLevel();
            if (level > 1)
            {
                var symbol =
                    Automatics.L10N.Translate("@text_automatic_mapping_creature_level_symbol");
                var sb = new StringBuilder(pinName).Append("\n");
                for (var i = 1; i < level; i++) sb.Append(symbol);
                pinName = sb.ToString();
            }

            var pos = character.transform.position;
            if (!PinDataCache.TryGetValue(uniqueId, out var pinData))
                AddPin(uniqueId, pos, pinName,
                    CreateTarget(character.gameObject, character.m_name, level));
            else
                UpdatePin(uniqueId, pinData, pinName, pos, delta);
        }

        private static void AddOrUpdatePin(Component component, float delta)
        {
            if (!Objects.GetZdoid(component, out var uniqueId)) return;
            if (!KnownObjects.Add(uniqueId)) return;

            var pos = component.transform.position;
            string pinName;
            if (component is RandomFlyingBird bird)
            {
                var birdName = Objects.GetPrefabName(bird.gameObject).ToLower();
                pinName = $"@animal_{birdName}";
            }
            else
            {
                pinName = Objects.GetName(component);
            }

            if (!PinDataCache.TryGetValue(uniqueId, out var pinData))
                AddPin(uniqueId, pos, pinName, CreateTarget(component.gameObject, pinName));
            else
                UpdatePin(uniqueId, pinData, pinData.m_name, pos, delta);
        }

        private static void AddPin(ZDOID uniqueId, Vector3 pos, string pinName, Target target)
        {
            var pinData = Map.AddPin(pos, pinName, false, target);
            if (PinDataCache.TryGetValue(uniqueId, out var data) && !ReferenceEquals(data, pinData))
            {
                PinKeyCache.Remove(data);
                Automatics.Logger.Warning(() =>
                    $"PinData is already exists: [Existing: {data.m_name}{data.m_pos}, New: {pinName}{pos}]");
            }

            PinDataCache[uniqueId] = pinData;
            PinKeyCache[pinData] = uniqueId;
            PinTargetCache[uniqueId] = pos;
            PinVelocityCache[uniqueId] = Vector3.zero;
        }

        private static bool RemovePinFromCache(Minimap.PinData pinData)
        {
            if (!PinKeyCache.TryGetValue(pinData, out var uniqueId)) return false;

            PinKeyCache.Remove(pinData);
            PinDataCache.Remove(uniqueId);
            PinTargetCache.Remove(uniqueId);
            PinVelocityCache.Remove(uniqueId);
            return true;
        }

        private static void UpdatePin(ZDOID uniqueId, Minimap.PinData pinData, string pinName, Vector3 pos,
            float delta)
        {
            if (!string.IsNullOrEmpty(pinData.m_name))
                pinData.m_name = pinName;

            PinTargetCache[uniqueId] = pos;
        }

        private static Vector3 InterpolatePinPosition(
            IDictionary<ZDOID, Vector3> velocityCache, ZDOID uniqueId, Vector3 current,
            Vector3 target, float delta)
        {
            if (delta <= 0f)
                return target;

            if (Vector3.Distance(current, target) >= PinSnapDistance)
            {
                velocityCache[uniqueId] = Vector3.zero;
                return target;
            }

            if (!velocityCache.TryGetValue(uniqueId, out var velocity))
                velocity = Vector3.zero;

            var next = Vector3.SmoothDamp(current, target, ref velocity, PinSmoothingTime,
                Mathf.Infinity, delta);
            velocityCache[uniqueId] = velocity;
            return next;
        }

        private static Target CreateTarget(GameObject prefab, string name, int level = 0)
        {
            return level <= 0
                ? new Target { name = name, prefabName = Objects.GetPrefabName(prefab) }
                : new Target
                {
                    name = name, prefabName = Objects.GetPrefabName(prefab),
                    metadata = new MetaData { level = level }
                };
        }
    }

    internal static class StaticObjectMapping
    {
        private const int ColliderBufferInitialLength = 4096;
        private const int ColliderBufferMaxLength = 16384;
        private const float FailedScanCooldownSeconds = 1f;
        private const float TileMarginMeters = 2f;
        private const int RetryTileBudgetPerFrame = 4;
        private const int MaxTileDepth = 4;

        // A-12: ColliderBuffer and StaticObjectCache are intentionally mutable
        // (no `readonly`) so that the primary scan path can grow the buffer on
        // saturation and swap the pending cache wholesale after a successful
        // non-saturated pass.
        private static Collider[] ColliderBuffer;
        private static Dictionary<Collider, ClassifiedStaticObject> StaticObjectCache;
        private static readonly Lazy<int> ObjectMaskLazy;
        private static readonly Lazy<int> DungeonMaskLazy;
        private static readonly Dictionary<MapPinIdentify, PinCacheEntry> PinDataCache;
        private static readonly Dictionary<Minimap.PinData, HashSet<MapPinIdentify>> PinKeyCache;
        private static readonly List<FloraNode> FloraNodesBuffer;
        private static readonly HashSet<FloraNetwork> SeenNetworksThisPass;
        private static readonly HashSet<Minimap.PinData> OwnedFloraPinsScratch;
        private static readonly HashSet<Minimap.PinData> StaleRemovalScratch;

        private static float _lastCacheUpdateTime;
        private static float _mappingTimer;

        // A-12 two-phase fill state. The pending cache accumulates the current
        // scan's output; it only swaps into StaticObjectCache after a fully
        // non-saturated scan. While _cacheIncomplete is true we keep serving
        // the previous (known-good) StaticObjectCache to prevent pin flicker.
        private static Dictionary<Collider, ClassifiedStaticObject> _pendingStaticObjectCache;
        private static bool _cacheIncomplete;
        private static float _failedScanAnchor;
        private static PendingScanSnapshot? _pendingScanSnapshot;

        // Tile-split fallback queue. Populated when the single-sphere scan
        // saturates at the buffer ceiling; each tick processes up to
        // RetryTileBudgetPerFrame tiles via OverlapBoxNonAlloc. Saturated
        // tiles split into four quadrants until MaxTileDepth. While the
        // queue is non-empty the primary single-sphere path is skipped; any
        // snapshot-parameter drift clears the queue and restarts the scan.
        private static readonly Queue<PendingTile> PendingTiles = new Queue<PendingTile>();

        // Sticky flag set when ProcessTileBudget hits a max-depth tile that
        // still saturates. Because the buffer contents are truncated, that
        // sub-region's scan is known-incomplete. Carried across per-tick
        // budget calls so the completion path can commit in degraded mode
        // (keep _cacheIncomplete=true, suppressing stale-pin pruning) rather
        // than signaling a clean rebuild that would let EndPassAndPruneStale
        // drop auto pins whose colliders were truncated out.
        private static bool _tileFallbackSawTruncation;

        // Targeted-invalidation bookkeeping. _staticClassifierVersion ticks
        // whenever a registry relevant to the static mapping path (Flora /
        // Mineral / Spawner / Other / Dungeon / Spot) mutates.
        // _committedStaticClassifierVersion records the version in effect when
        // StaticObjectCache was last successfully rebuilt. Mapping() compares
        // the two before iterating and runs a targeted reconciliation pass
        // when they diverge. The allowlist set records which registries saw a
        // Config.AllowPinning* change since the last reconciliation so the
        // per-registry stale-removal pass only touches entries in the affected
        // kinds.
        private static int _staticClassifierVersion;
        private static int _committedStaticClassifierVersion;
        private static readonly HashSet<ValheimObject> PendingAllowlistInvalidations
            = new HashSet<ValheimObject>();
        private static bool _registrySubscriptionsBound;

        // Sweep generation. LastSeenSweep on each PinCacheEntry is tagged with
        // _currentSweepId whenever the entry is observed during a pass. At
        // pass end, entries whose max LastSeenSweep across all keys trails the
        // current generation are retired via the existing saved-pin policy.
        // _currentPassId is an independent counter used by the Flora per-pass
        // network guard. While _cacheIncomplete is true the sweep generation
        // is frozen so partial scans cannot prune live pins.
        private static int _currentSweepId;
        private static int _currentPassId;

        private static int ObjectMask => ObjectMaskLazy.Value;
        private static int DungeonMask => DungeonMaskLazy.Value;

        static StaticObjectMapping()
        {
            ColliderBuffer = new Collider[ColliderBufferInitialLength];
            ObjectMaskLazy = new Lazy<int>(() => LayerMask.GetMask("item", "piece",
                "piece_nonsolid", "Default", "static_solid", "Default_small", "character",
                "character_net", /*TODO: "terrain",*/ "vehicle"));
            DungeonMaskLazy = new Lazy<int>(() => LayerMask.GetMask("character_trigger"));
            StaticObjectCache = new Dictionary<Collider, ClassifiedStaticObject>();
            _pendingStaticObjectCache = new Dictionary<Collider, ClassifiedStaticObject>();
            PinDataCache = new Dictionary<MapPinIdentify, PinCacheEntry>();
            PinKeyCache = new Dictionary<Minimap.PinData, HashSet<MapPinIdentify>>();
            FloraNodesBuffer = new List<FloraNode>();
            SeenNetworksThisPass = new HashSet<FloraNetwork>();
            OwnedFloraPinsScratch = new HashSet<Minimap.PinData>();
            StaleRemovalScratch = new HashSet<Minimap.PinData>();
        }

        private static bool CanMapping(float delta, bool takeInput)
        {
            if (Config.StaticObjectMappingKey.MainKey != KeyCode.None)
                return takeInput && Config.StaticObjectMappingKey.IsDown();

            if (Config.StaticObjectMappingInterval <= 0) return false;

            _mappingTimer += delta;
            if (_mappingTimer < Config.StaticObjectMappingInterval) return false;
            _mappingTimer = 0f;

            return true;
        }

        private static bool GetFlora(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Flora.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningFlora.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetMineral(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Mineral.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningMineral.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetSpawner(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Spawner.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningSpawner.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetOther(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (MappingObject.Other.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningOther.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetDungeon(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Dungeon.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningDungeon.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetSpot(string name, out (string Identifier, bool IsAllowed) data)
        {
            if (ValheimObject.Spot.GetIdentify(name, out var identifier))
            {
                data = (identifier, Config.AllowPinningSpot.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static void RemoveCache(Minimap.PinData pinData)
        {
            RemovePinFromCache(pinData);
        }

        [UsedImplicitly]
        public static void OnObjectDestroy(Component component, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticMapping) return;
            if (!Config.RemovePinsOfDestroyedObject) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            Minimap.PinData pinData = null;
            var name = Objects.GetName(component);
            if (GetFlora(name, out var data))
            {
                if (!data.IsAllowed) return;

                var node = component.GetComponent<FloraNode>();
                if (!node)
                    pinData = Map.RemovePin(component.transform.position);
                else if (node.Network.NodeCount <= 1)
                    pinData = Map.RemovePin(node.Network.Center);
            }
            else if (GetMineral(name, out data))
            {
                if (!data.IsAllowed) return;
                pinData = Map.RemovePin(GetCollidersCenter(GetMineralColliders()));
            }
            else if (GetSpawner(name, out data) ||
                     GetOther(name, out data))
            {
                if (!data.IsAllowed) return;
                pinData = Map.RemovePin(component.transform.position);
            }

            if (pinData != null)
                RemoveCache(pinData);

            IReadOnlyCollection<Collider> GetMineralColliders()
            {
                var empty = Array.Empty<Collider>();
                switch (component)
                {
                    case MineRock rock:
                        return Reflections.GetField<Collider[]>(rock, "m_hitAreas") ?? empty;
                    case MineRock5 rock5:
                        return rock5.gameObject.GetComponentsInChildren<Collider>() ?? empty;
                    case Destructible destructible:
                    {
                        var collider = destructible.GetComponentInChildren<Collider>();
                        return collider ? new[] { collider } : empty;
                    }
                    default:
                        return empty;
                }
            }

            Vector3 GetCollidersCenter(IReadOnlyCollection<Collider> colliders)
            {
                var pos = colliders.Aggregate(Vector3.zero,
                    (current, collider) => current + collider.bounds.center);
                return pos / colliders.Count;
            }
        }

        public static void Cleanup()
        {
            StaticObjectCache.Clear();
            _pendingStaticObjectCache.Clear();
            PinDataCache.Clear();
            PinKeyCache.Clear();
            SeenNetworksThisPass.Clear();
            OwnedFloraPinsScratch.Clear();
            StaleRemovalScratch.Clear();
            PendingTiles.Clear();
            _tileFallbackSawTruncation = false;
            _pendingScanSnapshot = null;
            _cacheIncomplete = false;
            _failedScanAnchor = 0f;
            _lastCacheUpdateTime = 0f;
            _mappingTimer = 0f;
            _committedStaticClassifierVersion = _staticClassifierVersion;
            PendingAllowlistInvalidations.Clear();
            _currentSweepId = 0;
            _currentPassId = 0;
        }

        public static void RemoveCachedPins()
        {
            if (PinDataCache.Count == 0) return;

            StaleRemovalScratch.Clear();
            foreach (var pair in PinDataCache)
                StaleRemovalScratch.Add(pair.Value.PinData);

            foreach (var pinData in StaleRemovalScratch)
            {
                RemovePinFromCache(pinData);
                if (!pinData.m_save)
                    Map.RemovePin(pinData);
            }

            StaleRemovalScratch.Clear();
        }

        public static void OnRemovePin(Minimap.PinData pinData)
        {
            RemovePinFromCache(pinData);
        }

        public static bool SetSaveFlag(Minimap.PinData pinData)
        {
            if (!RemovePinFromCache(pinData)) return false;

            pinData.m_save = true;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                Automatics.L10N.Localize("@message_automatic_mapping_pin_saved",
                    pinData.m_name.Replace("\n", "")));
            return true;
        }

        public static void Mapping(float delta, bool takeInput)
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotStaticMapping))
            {
                EnsureRegistrySubscriptions();

                if (Config.StaticObjectMappingRange <= 0)
                {
                    RemoveCachedPins();
                    return;
                }

                if (!CanMapping(delta, takeInput)) return;

                var origin = Player.m_localPlayer.transform.position;

                // Order matters: prune pins whose classification / allowlist
                // just moved so CacheStaticObjects' forced rescan rebuilds
                // from the live registry, and the iteration below sees a cache
                // that is consistent with the surviving PinDataCache entries.
                ApplyTargetedInvalidations();

                CacheStaticObjects(origin);

                BeginPass();

                foreach (var pair in StaticObjectCache)
                {
                    var collider = pair.Key;
                    var classification = pair.Value;
                    var component = classification.Component;
                    if (!collider || !component) continue;

                    // Use the live transform position at the range gate.
                    // classification.Position is a snapshot from cache-fill time
                    // and can drift when a classified component rides a moving
                    // parent (e.g. a portal on a ship), which is why the old
                    // Dictionary<Collider, Component> cache read the live
                    // transform every pass. Honor that contract here.
                    var pos = component.transform.position;
                    if (Vector3.Distance(origin, pos) > Config.StaticObjectMappingRange) continue;
                    if (!ZNetScene.instance.IsAreaReady(pos)) continue;

                    // Classification already knows which kind this collider is,
                    // so the remaining work is just dispatching to the right
                    // mapping function. The functions still re-run GetFlora /
                    // GetMineral / … today for allowlist + identifier handling;
                    // a future refactor can take the cached Kind + Identifier
                    // directly without touching the call sites.
                    var name = classification.SourceToken;
                    switch (classification.Kind)
                    {
                        case PinKind.Flora:
                            FloraMapping(component, name);
                            break;
                        case PinKind.Mineral:
                            MineralMapping(component, name);
                            break;
                        case PinKind.Spawner:
                            SpawnerMapping(component, name);
                            break;
                        case PinKind.Other:
                            OtherMapping(component, name);
                            break;
                        case PinKind.Portal:
                            PortalMapping(component, name);
                            break;
                    }
                }

                foreach (var location in from x in ZoneSystem.instance.m_locationInstances.Values
                         where Vector3.Distance(origin, x.m_position) <= Config.LocationMappingRange
                         select x)
                {
                    if (DungeonMapping(location)) continue;
                    if (SpotMapping(location)) continue;
                }

                EndPassAndPruneStale();
            }
        }

        /// <summary>
        /// Advances the Flora per-pass network guard and stamps the sweep
        /// generation that entries observed during this pass will be tagged
        /// with. While <see cref="_cacheIncomplete"/> is true the sweep
        /// generation is *not* advanced — the iteration still runs on whatever
        /// portion of the cache survived saturation, but stale-removal at
        /// pass end sees every entry as "current" and leaves previously-added
        /// pins alone. Stale removal waits until a pass sees the whole
        /// neighborhood, not a partial slice of it.
        /// </summary>
        private static void BeginPass()
        {
            _currentPassId++;
            SeenNetworksThisPass.Clear();
            if (!_cacheIncomplete)
                _currentSweepId++;
        }

        /// <summary>
        /// Retires PinCacheEntries whose <see cref="PinCacheEntry.LastSeenSweep"/>
        /// trailed the current sweep generation — i.e. they were not touched by
        /// any mapping call during this pass. Flora clusters register the same
        /// PinData under many keys, so the "seen" judgement is computed per
        /// PinData (max LastSeenSweep across all keys) rather than per key.
        ///
        /// Skipped entirely while <c>_cacheIncomplete</c> is true so saturated
        /// scans cannot prune live pins (<see cref="BeginPass"/> already
        /// prevents the generation from advancing, but we also do not want to
        /// interpret "0-delta" as stale during the first post-saturation pass).
        /// </summary>
        private static void EndPassAndPruneStale()
        {
            if (_cacheIncomplete) return;
            if (PinDataCache.Count == 0) return;

            StaleRemovalScratch.Clear();
            foreach (var keys in PinKeyCache)
            {
                var pinData = keys.Key;
                var maxSeen = int.MinValue;
                foreach (var key in keys.Value)
                {
                    if (!PinDataCache.TryGetValue(key, out var entry)) continue;
                    if (entry.LastSeenSweep > maxSeen) maxSeen = entry.LastSeenSweep;
                }

                if (maxSeen < _currentSweepId)
                    StaleRemovalScratch.Add(pinData);
            }

            foreach (var pinData in StaleRemovalScratch)
            {
                RemovePinFromCache(pinData);
                if (!pinData.m_save)
                    Map.RemovePin(pinData);
            }

            StaleRemovalScratch.Clear();
        }

        private static void MarkSeen(MapPinIdentify identify)
        {
            if (PinDataCache.TryGetValue(identify, out var entry))
                entry.LastSeenSweep = _currentSweepId;
        }

        private static void MarkSeen(IEnumerable<FloraNode> nodes)
        {
            foreach (var node in nodes)
                MarkSeen(new MapPinIdentify(node.UniqueId));
        }

        /// <summary>
        /// A-12 primary path. Runs a single OverlapSphere scan, builds the
        /// result in a pending cache, and only swaps it into StaticObjectCache
        /// after confirming the scan did not saturate the buffer. On
        /// saturation the buffer grows (up to <see cref="ColliderBufferMaxLength"/>)
        /// and the scan retries after a cooldown instead of corrupting the
        /// live cache with truncated results.
        ///
        /// <para>
        /// The pending-fill path is additionally guarded by a
        /// <see cref="PendingScanSnapshot"/>: the snapshot records the origin,
        /// range, layer mask, and static classifier version that produced the
        /// current pending cache. Before each retry/swap we compare the live
        /// parameters against the snapshot through a shared
        /// <c>originTolerance</c>. A mismatch throws away the incomplete work
        /// and starts the scan over so we do not commit a cache whose
        /// parameters drifted while it was being built.
        /// </para>
        ///
        /// <para>
        /// When even the ceiling buffer saturates, the scan switches to a
        /// recursive <c>OverlapBoxNonAlloc</c> tile fallback: the sphere's
        /// bounding cube is split 2×2 in XZ (Y stays full to preserve
        /// vertical coverage). Each tick processes up to
        /// <see cref="RetryTileBudgetPerFrame"/> tiles; saturated tiles
        /// recurse into four sub-tiles until <see cref="MaxTileDepth"/>.
        /// Overlapping box margins are absorbed by idempotent indexer
        /// insertion into the pending cache.
        /// </para>
        /// </summary>
        private static void CacheStaticObjects(Vector3 origin)
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotCacheStaticObjects))
            {
                var now = Time.time;

                // Interval gate during steady state; cooldown gate only while
                // a primary retry is waiting (no queued tiles). Tile-mode
                // ticks bypass the cooldown because per-frame progress is
                // already throttled by RetryTileBudgetPerFrame and the
                // enclosing Mapping cadence.
                if (_cacheIncomplete && PendingTiles.Count == 0)
                {
                    if (now - _failedScanAnchor < FailedScanCooldownSeconds) return;
                }
                else if (!_cacheIncomplete &&
                         now - _lastCacheUpdateTime < Config.StaticObjectCachingInterval)
                {
                    return;
                }

                var range = Config.StaticObjectMappingRange;
                if (range <= 0) return;

                var mask = ObjectMask;
                var classifierVersion = _staticClassifierVersion;
                var originTolerance = ComputeOriginTolerance(range);

                // If a previous scan was left incomplete and the live scan
                // parameters drifted beyond the tolerance, discard the stale
                // pending state (including the tile queue) rather than
                // committing a cache with mismatched origin / range / mask /
                // classifier version.
                if (_pendingScanSnapshot.HasValue)
                {
                    var snapshot = _pendingScanSnapshot.Value;
                    if (snapshot.Mask != mask ||
                        !Mathf.Approximately(snapshot.Range, range) ||
                        snapshot.StaticClassifierVersion != classifierVersion ||
                        Vector3.Distance(origin, snapshot.Origin) > originTolerance)
                    {
                        _pendingStaticObjectCache.Clear();
                        PendingTiles.Clear();
                        _tileFallbackSawTruncation = false;
                        _pendingScanSnapshot = null;
                    }
                }

                // Fallback path: work down the tile queue before attempting a
                // new primary scan. ProcessTileBudget commits the swap when
                // every tile has been drained non-saturated.
                if (PendingTiles.Count > 0 && _pendingScanSnapshot.HasValue)
                {
                    ProcessTileBudget(_pendingScanSnapshot.Value, origin, originTolerance, now,
                        classifierVersion);
                    return;
                }

                var buffer = ColliderBuffer;
                var size = Physics.OverlapSphereNonAlloc(origin, range * 1.5f, buffer, mask);

                if (size >= buffer.Length)
                {
                    // Saturation: Unity silently truncates results, so we must
                    // NOT swap into StaticObjectCache. First try to grow the
                    // buffer up to the ceiling; once the ceiling saturates,
                    // seed the tile-split fallback so subsequent ticks scan
                    // smaller OverlapBox sub-regions instead of the
                    // truncation-prone single sphere.
                    var newLength = Mathf.Min(buffer.Length * 2, ColliderBufferMaxLength);
                    if (newLength > buffer.Length)
                    {
                        ColliderBuffer = new Collider[newLength];
                        Automatics.Logger.Warning(() =>
                            $"[AutomaticMapping] Physics scan saturated at {buffer.Length}; " +
                            $"grew ColliderBuffer to {newLength}, retrying after cooldown.");
                    }
                    else
                    {
                        Automatics.Logger.Warning(() =>
                            $"[AutomaticMapping] Physics scan saturated at buffer ceiling " +
                            $"({buffer.Length}); switching to OverlapBox tile fallback.");
                        SeedInitialTiles(origin, range);
                    }

                    _cacheIncomplete = true;
                    _failedScanAnchor = now;
                    _pendingStaticObjectCache.Clear();
                    _pendingScanSnapshot = new PendingScanSnapshot
                    {
                        Origin = origin,
                        Range = range,
                        Mask = mask,
                        StaticClassifierVersion = classifierVersion
                    };
                    return;
                }

                // Non-saturated: build into pending, then swap wholesale.
                // Indexer assignment keeps insertion idempotent in case the
                // same collider is classified more than once (e.g. via a
                // tile-fallback retry that overlaps a prior pending-fill).
                var pending = _pendingStaticObjectCache;
                pending.Clear();
                for (var i = 0; i < size; i++)
                {
                    var collider = buffer[i];
                    if (collider == null) continue;
                    var classified = ClassifyStaticObject(collider);
                    if (!classified.HasValue) continue;
                    pending[collider] = classified.Value;
                }

                // Re-check snapshot parameters before swapping so that a
                // parameter drift between scan entry and commit (origin move,
                // classifier bump, etc.) invalidates the pending cache
                // instead of being committed with mixed parameters.
                if (_pendingScanSnapshot.HasValue)
                {
                    var snapshot = _pendingScanSnapshot.Value;
                    if (snapshot.Mask != mask ||
                        !Mathf.Approximately(snapshot.Range, range) ||
                        snapshot.StaticClassifierVersion != classifierVersion ||
                        Vector3.Distance(origin, snapshot.Origin) > originTolerance)
                    {
                        _pendingStaticObjectCache.Clear();
                        PendingTiles.Clear();
                        _tileFallbackSawTruncation = false;
                        _pendingScanSnapshot = null;
                        _cacheIncomplete = true;
                        _failedScanAnchor = now;
                        return;
                    }
                }

                CommitPendingSwap(now, classifierVersion);
            }
        }

        /// <summary>
        /// Runs up to <see cref="RetryTileBudgetPerFrame"/> tiles from
        /// <see cref="PendingTiles"/> through <c>OverlapBoxNonAlloc</c>.
        /// Saturated tiles split into four quadrants until
        /// <see cref="MaxTileDepth"/>; at max depth the truncated result is
        /// classified best-effort, logged, and flips
        /// <see cref="_tileFallbackSawTruncation"/>. Completion (queue empty)
        /// discards the pending cache if origin has drifted past
        /// <paramref name="originTolerance"/>; otherwise commits via
        /// <see cref="CommitPendingSwap"/> for a clean rebuild, or
        /// <see cref="CommitPendingSwapDegraded"/> when the truncation flag
        /// is set.
        /// </summary>
        private static void ProcessTileBudget(PendingScanSnapshot snapshot, Vector3 origin,
            float originTolerance, float now, int classifierVersion)
        {
            var halfY = snapshot.Range * 1.5f;
            var buffer = ColliderBuffer;
            var mask = snapshot.Mask;
            var pending = _pendingStaticObjectCache;
            var budget = RetryTileBudgetPerFrame;

            while (budget > 0 && PendingTiles.Count > 0)
            {
                var tile = PendingTiles.Dequeue();
                budget--;

                var halfExtents = new Vector3(tile.HalfXZ + TileMarginMeters, halfY,
                    tile.HalfXZ + TileMarginMeters);
                var n = Physics.OverlapBoxNonAlloc(tile.Center, halfExtents, buffer,
                    Quaternion.identity, mask);

                if (n >= buffer.Length)
                {
                    if (tile.Depth < MaxTileDepth)
                    {
                        SplitTile(tile);
                        continue;
                    }

                    _tileFallbackSawTruncation = true;
                    Automatics.Logger.Warning(() =>
                        $"[AutomaticMapping] OverlapBox tile at depth {MaxTileDepth} " +
                        $"saturated {buffer.Length}; classifying truncated results. Pins " +
                        "in this sub-region may be incomplete until the player moves.");
                }

                for (var i = 0; i < n; i++)
                {
                    var collider = buffer[i];
                    if (collider == null) continue;
                    var classified = ClassifyStaticObject(collider);
                    if (!classified.HasValue) continue;
                    pending[collider] = classified.Value;
                }
            }

            if (PendingTiles.Count > 0) return;

            // All tiles processed: re-check origin drift before committing.
            if (Vector3.Distance(origin, snapshot.Origin) > originTolerance)
            {
                _pendingStaticObjectCache.Clear();
                _tileFallbackSawTruncation = false;
                _pendingScanSnapshot = null;
                _failedScanAnchor = now;
                return;
            }

            if (_tileFallbackSawTruncation)
            {
                // At least one tile exhausted MaxTileDepth and still
                // saturated: its sub-region is known-incomplete. Swap the
                // fresh results so new colliders still reach the mapping
                // loop, but keep _cacheIncomplete=true so
                // EndPassAndPruneStale does not treat the truncated region
                // as authoritative and retire pins whose colliders fell
                // outside the buffer. Cooldown gates the next primary
                // retry; the player moving out of the dense cluster gives
                // the scan a chance to complete cleanly.
                CommitPendingSwapDegraded(now);
                return;
            }

            CommitPendingSwap(now, classifierVersion);
        }

        private static void CommitPendingSwap(float now, int classifierVersion)
        {
            var previous = StaticObjectCache;
            StaticObjectCache = _pendingStaticObjectCache;
            previous.Clear();
            _pendingStaticObjectCache = previous;

            _lastCacheUpdateTime = now;
            _cacheIncomplete = false;
            _pendingScanSnapshot = null;
            _tileFallbackSawTruncation = false;
            PendingTiles.Clear();
            _committedStaticClassifierVersion = classifierVersion;
        }

        /// <summary>
        /// Degraded-mode swap used when tile fallback completed with at
        /// least one known-truncated max-depth tile. Swaps pending into
        /// live so newly-scanned colliders are available, but leaves
        /// <see cref="_cacheIncomplete"/> set and arms the cooldown so
        /// stale-pin pruning stays suppressed and a fresh primary scan is
        /// attempted later.
        /// </summary>
        private static void CommitPendingSwapDegraded(float now)
        {
            var previous = StaticObjectCache;
            StaticObjectCache = _pendingStaticObjectCache;
            previous.Clear();
            _pendingStaticObjectCache = previous;

            PendingTiles.Clear();
            _pendingScanSnapshot = null;
            _tileFallbackSawTruncation = false;
            _failedScanAnchor = now;
            // _cacheIncomplete intentionally stays true.
            // _lastCacheUpdateTime / _committedStaticClassifierVersion are
            // not advanced — this swap is not a clean rebuild.
        }

        private static void SeedInitialTiles(Vector3 origin, float range)
        {
            // The sphere's bounding cube is 2 * range * 1.5f per side. Split
            // in XZ into 4 equal quadrants, each with an unmargined XZ
            // half-side of 0.75 * range. Y extent stays full. Clear first so
            // the helper establishes a fresh queue regardless of caller state.
            PendingTiles.Clear();
            EnqueueQuadrants(origin, range * 0.75f, depth: 1);
        }

        private static void SplitTile(PendingTile parent)
        {
            EnqueueQuadrants(parent.Center, parent.HalfXZ * 0.5f, parent.Depth + 1);
        }

        private static void EnqueueQuadrants(Vector3 center, float childHalf, int depth)
        {
            for (var dx = 0; dx < 2; dx++)
            {
                for (var dz = 0; dz < 2; dz++)
                {
                    var offsetX = dx == 0 ? -childHalf : childHalf;
                    var offsetZ = dz == 0 ? -childHalf : childHalf;
                    PendingTiles.Enqueue(new PendingTile
                    {
                        Center = new Vector3(center.x + offsetX, center.y, center.z + offsetZ),
                        HalfXZ = childHalf,
                        Depth = depth
                    });
                }
            }
        }

        /// <summary>
        /// Derives the origin tolerance used for snapshot carryover / discard
        /// decisions. The piecewise formula keeps short-range scans strict
        /// (no drift allowed at <c>range &lt;= 4</c>, 0.1 m budget for
        /// 4 &lt; range &lt; 6) and linearly relaxes for longer ranges
        /// (<c>0.5 * range - 2</c> once range reaches 6 m).
        /// </summary>
        private static float ComputeOriginTolerance(float mappingRange)
        {
            const float safetyMargin = 2f;
            if (mappingRange <= 4f) return 0f;
            var tolerance = 0.5f * mappingRange - safetyMargin;
            return tolerance < 1f ? 0.1f : tolerance;
        }

        /// <summary>
        /// Pure classifier: given a collider, identify whether it maps to a
        /// static pin category (Flora/Mineral/Spawner/Other/Portal) and return
        /// a self-contained <see cref="ClassifiedStaticObject"/> snapshot.
        /// Allowlist filtering is deliberately *not* performed here; the
        /// iteration in <see cref="Mapping"/> still consults the per-category
        /// mapping functions which re-check <c>Config.AllowPinning*</c> so that
        /// runtime allowlist toggles take effect without a cache rebuild.
        /// Memoization is the caller's responsibility (see the pending-fill
        /// indexer assignment in <see cref="CacheStaticObjects"/>).
        /// </summary>
        private static ClassifiedStaticObject? ClassifyStaticObject(Collider collider)
        {
            var component = collider.GetComponentInParent<IDestructible>() as Component;
            if (!component)
                component = collider.GetComponentInParent<Interactable>() as Component;
            if (!component)
                component = collider.GetComponentInParent<Hoverable>() as Component;
            if (!component) return null;

            var sourceToken = Objects.GetName(component);
            if (string.IsNullOrEmpty(sourceToken)) return null;

            if (!ClassifyStaticComponent(component, sourceToken, out var kind, out var identifier))
                return null;

            // Portal parity with the legacy Dictionary<Collider,Component> cache:
            // AsStaticObject used to store the TeleportWorld component for
            // portals, so downstream Objects.GetName and the Target.name passed
            // to CreateTarget resolved from TeleportWorld — not from the
            // IDestructible/Interactable/Hoverable parent. Swap the cached
            // component here and re-derive SourceToken from it so custom portal
            // icon matching by Target.name keeps its old contract.
            if (kind == PinKind.Portal)
            {
                var teleportWorld = component.GetComponent<TeleportWorld>() as Component;
                if (teleportWorld)
                {
                    component = teleportWorld;
                    sourceToken = Objects.GetName(component);
                }
            }

            return new ClassifiedStaticObject
            {
                Component = component,
                Kind = kind,
                Identifier = identifier,
                Position = component.transform.position,
                SourceToken = sourceToken
            };
        }

        /// <summary>
        /// Fill-path classifier: given a concrete component and its pre-fetched
        /// source token, walks the static-mapping registries in priority order
        /// (Flora → Mineral → Spawner → Other → Portal) to resolve
        /// <see cref="PinKind"/> + identifier. The Portal branch is component-
        /// aware (it needs a <c>TeleportWorld</c> component lookup), which is
        /// why targeted invalidation cannot re-use this helper verbatim and
        /// falls back to <see cref="ClassifyStaticComponentSourceToken"/>.
        /// Dungeon and Spot are deliberately excluded — they only originate
        /// from ZoneSystem location scans, not component scans.
        /// </summary>
        private static bool ClassifyStaticComponent(Component component, string sourceToken,
            out PinKind kind, out string identifier)
        {
            if (ClassifyStaticComponentSourceToken(sourceToken, out kind, out identifier))
                return true;

            if (component.GetComponent<TeleportWorld>())
            {
                kind = PinKind.Portal;
                identifier = string.Empty;
                return true;
            }

            kind = default;
            identifier = string.Empty;
            return false;
        }

        /// <summary>
        /// Source-token-only variant of <see cref="ClassifyStaticComponent"/>
        /// used by targeted invalidation on <c>PinSourceDomain.Component</c>
        /// entries. Cannot resolve the Portal branch (which needs a component
        /// lookup) and intentionally excludes Dungeon/Spot (Location domain
        /// only).
        /// </summary>
        private static bool ClassifyStaticComponentSourceToken(string sourceToken,
            out PinKind kind, out string identifier)
        {
            if (GetFlora(sourceToken, out var data))
            {
                kind = PinKind.Flora;
                identifier = data.Identifier;
                return true;
            }
            if (GetMineral(sourceToken, out data))
            {
                kind = PinKind.Mineral;
                identifier = data.Identifier;
                return true;
            }
            if (GetSpawner(sourceToken, out data))
            {
                kind = PinKind.Spawner;
                identifier = data.Identifier;
                return true;
            }
            if (GetOther(sourceToken, out data))
            {
                kind = PinKind.Other;
                identifier = data.Identifier;
                return true;
            }

            kind = default;
            identifier = string.Empty;
            return false;
        }

        /// <summary>
        /// Classifies a ZoneSystem location prefab name as Dungeon or Spot.
        /// Used by both the location-scan fill path and targeted invalidation
        /// of <c>PinSourceDomain.Location</c> entries.
        /// </summary>
        private static bool ClassifyStaticLocation(string prefabName, out PinKind kind,
            out string identifier)
        {
            if (GetDungeon(prefabName, out var data))
            {
                kind = PinKind.Dungeon;
                identifier = data.Identifier;
                return true;
            }
            if (GetSpot(prefabName, out data))
            {
                kind = PinKind.Spot;
                identifier = data.Identifier;
                return true;
            }

            kind = default;
            identifier = string.Empty;
            return false;
        }

        /// <summary>
        /// Wires up the registry / allowlist change subscribers on the first
        /// <see cref="Mapping"/> call. Bound once for the lifetime of the
        /// AppDomain — <see cref="Cleanup"/> intentionally does not flip the
        /// flag because re-binding without unbinding would register duplicate
        /// delegates and double-increment the classifier version.
        /// </summary>
        private static void EnsureRegistrySubscriptions()
        {
            if (_registrySubscriptionsBound) return;
            _registrySubscriptionsBound = true;
            ValheimObject.RegistryChanged += OnValheimObjectRegistryChanged;
            Config.StaticAllowlistChanged += OnStaticAllowlistChanged;
        }

        private static void OnValheimObjectRegistryChanged(ValheimObject obj)
        {
            // Filter to the registries that the static mapping path actually
            // consumes. Vehicle / Animal / Monster live in the dynamic domain
            // and must not perturb the static classifier version.
            if (ReferenceEquals(obj, ValheimObject.Flora) ||
                ReferenceEquals(obj, ValheimObject.Mineral) ||
                ReferenceEquals(obj, ValheimObject.Spawner) ||
                ReferenceEquals(obj, ValheimObject.Dungeon) ||
                ReferenceEquals(obj, ValheimObject.Spot) ||
                ReferenceEquals(obj, MappingObject.Other))
            {
                _staticClassifierVersion++;
            }
        }

        private static void OnStaticAllowlistChanged(ValheimObject registry)
        {
            // Allowlist toggles do not invalidate classifier output — they only
            // change whether the iteration acts on a given kind. Queue a
            // targeted invalidation for the specific registry so the next
            // Mapping call only revisits entries in the affected domain.
            PendingAllowlistInvalidations.Add(registry);
        }

        /// <summary>
        /// Maps a <see cref="PinKind"/> to the <see cref="ValheimObject"/>
        /// registry that owns its allowlist. Used to restrict
        /// allowlist-triggered invalidation to the affected kinds.
        /// </summary>
        private static ValheimObject RegistryForKind(PinKind kind)
        {
            switch (kind)
            {
                case PinKind.Flora: return ValheimObject.Flora;
                case PinKind.Mineral: return ValheimObject.Mineral;
                case PinKind.Spawner: return ValheimObject.Spawner;
                case PinKind.Other: return MappingObject.Other;
                case PinKind.Dungeon: return ValheimObject.Dungeon;
                case PinKind.Spot: return ValheimObject.Spot;
                default: return null;
            }
        }

        /// <summary>
        /// Reconciles PinDataCache with the current registry / allowlist
        /// state. Runs on every <see cref="Mapping"/> tick — cheap no-op when
        /// nothing has changed since the last run. Two triggers:
        ///
        /// <list type="bullet">
        ///   <item><description>Static registry mutation: <c>_staticClassifierVersion</c>
        ///     diverges from <c>_committedStaticClassifierVersion</c>. Re-runs
        ///     the component / location classifier on every cached entry and
        ///     retires the ones whose classification changed or is no longer
        ///     allow-listed, then forces a fresh scan by clearing the
        ///     caching-interval gate.</description></item>
        ///   <item><description>Allowlist mutation via BepInEx SettingChanged:
        ///     only the entries in the affected registry are rescanned for
        ///     allowlist-drop removal; classification is not re-checked since
        ///     the registry itself did not move.</description></item>
        /// </list>
        ///
        /// Stale removal uses the existing saved-pin policy
        /// (<c>!pinData.m_save</c> ⇒ remove from minimap; always clear cache).
        /// Portal <see cref="PinSourceDomain.Component"/> entries are skipped
        /// because they are always created with <c>save=true</c> and removing
        /// them from the minimap would contradict the saved-pin contract.
        /// </summary>
        private static void ApplyTargetedInvalidations()
        {
            var registryChanged = _committedStaticClassifierVersion != _staticClassifierVersion;
            var allowlistChanged = PendingAllowlistInvalidations.Count > 0;
            if (!registryChanged && !allowlistChanged) return;

            if (PinDataCache.Count == 0)
            {
                if (registryChanged)
                {
                    _committedStaticClassifierVersion = _staticClassifierVersion;
                    _lastCacheUpdateTime = 0f;
                }
                PendingAllowlistInvalidations.Clear();
                return;
            }

            StaleRemovalScratch.Clear();
            foreach (var pair in PinDataCache)
            {
                var entry = pair.Value;
                if (entry?.PinData == null) continue;

                if (ShouldRetireEntry(entry, registryChanged))
                    StaleRemovalScratch.Add(entry.PinData);
            }

            foreach (var pinData in StaleRemovalScratch)
            {
                RemovePinFromCache(pinData);
                if (!pinData.m_save)
                    Map.RemovePin(pinData);
            }

            StaleRemovalScratch.Clear();

            if (registryChanged)
            {
                // Force a fresh OverlapSphere scan on the next pass so the
                // StaticObjectCache itself reflects the new classifier without
                // having to wait for StaticObjectCachingInterval.
                _committedStaticClassifierVersion = _staticClassifierVersion;
                _lastCacheUpdateTime = 0f;
            }

            PendingAllowlistInvalidations.Clear();
        }

        private static bool ShouldRetireEntry(PinCacheEntry entry, bool registryChanged)
        {
            // Portal Component-domain entries are opt-out only: invalidation
            // would remove a save=true pin that user-semantics treats as
            // "auto-add disabled from now on", which would surprise the user.
            if (entry.Domain == PinSourceDomain.Component && entry.Kind == PinKind.Portal)
                return false;

            // Allowlist-only mutations scope to the affected registry so we
            // don't re-run IsAllowedForKind on every unrelated pin. Registry
            // mutations force a full sweep because the classifier itself moved.
            var allowlistScope = !registryChanged;
            if (allowlistScope)
            {
                var registry = RegistryForKind(entry.Kind);
                if (registry == null || !PendingAllowlistInvalidations.Contains(registry))
                    return false;
            }

            switch (entry.Domain)
            {
                case PinSourceDomain.Component:
                    if (registryChanged)
                    {
                        if (!ClassifyStaticComponentSourceToken(entry.SourceToken, out var kind,
                                out var identifier))
                            return true;
                        if (kind != entry.Kind || identifier != entry.Identifier)
                            return true;
                    }

                    return !IsAllowedForKind(entry.Kind, entry.Identifier);

                case PinSourceDomain.Location:
                    if (registryChanged)
                    {
                        if (!ClassifyStaticLocation(entry.SourceToken, out var kind,
                                out var identifier))
                            return true;
                        if (kind != entry.Kind || identifier != entry.Identifier)
                            return true;
                    }

                    return !IsAllowedForKind(entry.Kind, entry.Identifier);

                default:
                    return false;
            }
        }

        private static bool IsAllowedForKind(PinKind kind, string identifier)
        {
            switch (kind)
            {
                case PinKind.Flora: return Config.AllowPinningFlora.Contains(identifier);
                case PinKind.Mineral: return Config.AllowPinningMineral.Contains(identifier);
                case PinKind.Spawner: return Config.AllowPinningSpawner.Contains(identifier);
                case PinKind.Other: return Config.AllowPinningOther.Contains(identifier);
                case PinKind.Portal: return Config.AllowPinningPortal;
                case PinKind.Dungeon: return Config.AllowPinningDungeon.Contains(identifier);
                case PinKind.Spot: return Config.AllowPinningSpot.Contains(identifier);
                default: return false;
            }
        }

        private static bool FloraMapping(Component component, string name)
        {
            var sourceToken = name;
            if (!GetFlora(sourceToken, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;

            var displayName = ValheimObject.Flora.GetName(data.Identifier, out var label)
                ? label
                : name;

            var flora = FloraNode.Find(uniqueId);
            if (!flora || !flora.IsValid()) return true;

            var network = flora.Network;
            if (network == null) return true;

            // Dirty path bypasses the per-pass guard so a merge can fully
            // reconcile cluster pins even if an earlier collider in this pass
            // already touched the same network in its clean state.
            if (network.IsDirty)
            {
                HandleDirtyFloraCluster(component, network, data.Identifier, sourceToken,
                    displayName);
                SeenNetworksThisPass.Add(network);
                return true;
            }

            // Clean path: at most one collider per network per pass does work.
            if (!SeenNetworksThisPass.Add(network)) return true;

            network.FillValidNodes(FloraNodesBuffer);

            if (TryGetCachedPin(FloraNodesBuffer, out var cachedPin))
            {
                CachePin(FloraNodesBuffer, cachedPin, PinKind.Flora, data.Identifier, sourceToken,
                    PinSourceDomain.Component);
                return true;
            }

            var pos = network.Center;
            if (Map.GetClosestPin(pos) != null) return true;

            var pinName = ComputeFloraPinName(displayName, network.NodeCount);
            var pinData = AddPin(uniqueId, pos, pinName, Config.SaveStaticObjectPins,
                CreateTarget(component.gameObject, displayName), PinKind.Flora, data.Identifier,
                sourceToken, PinSourceDomain.Component);
            CachePin(FloraNodesBuffer, pinData, PinKind.Flora, data.Identifier, sourceToken,
                PinSourceDomain.Component);
            return true;
        }

        private static void HandleDirtyFloraCluster(Component component, FloraNetwork network,
            string identifier, string sourceToken, string displayName)
        {
            network.FillValidNodes(FloraNodesBuffer);

            // CollectDistinctOwnedPins: owned = present in PinDataCache via one
            // of the cluster's node keys. User-saved / detached pins at the old
            // center are intentionally excluded — they live only on the minimap
            // and the mutate-in-place path has no ownership claim on them.
            OwnedFloraPinsScratch.Clear();
            foreach (var node in FloraNodesBuffer)
            {
                if (PinDataCache.TryGetValue(new MapPinIdentify(node.UniqueId), out var entry) &&
                    entry.PinData != null)
                    OwnedFloraPinsScratch.Add(entry.PinData);
            }

            network.Update();

            Minimap.PinData canonical = null;
            foreach (var pin in OwnedFloraPinsScratch)
            {
                if (canonical == null)
                {
                    canonical = pin;
                    continue;
                }

                // Merge collapsed multiple owned clusters into one network. The
                // surplus pins are Automatics-owned (present in PinDataCache), so
                // saved-pin policy does not apply — remove them unconditionally.
                RemovePinFromCache(pin);
                Map.RemovePin(pin);
            }

            var center = network.Center;
            var pinName = ComputeFloraPinName(displayName, network.NodeCount);

            if (canonical != null)
            {
                Map.MovePin(canonical, center);
                canonical.m_name = pinName;
                // Drop the canonical pin's previously cached keys before
                // re-registering the current valid node set. Without this,
                // keys for nodes that have since been destroyed or merged
                // away from this network stay in PinKeyCache[canonical] and
                // keep max(LastSeenSweep) pinned to the current generation,
                // which blocks EndPassAndPruneStale from ever retiring the
                // pin and inflates both dictionaries on long-lived worlds.
                // The minimap PinData itself stays in place because we do
                // not call Map.RemovePin here.
                RemovePinFromCache(canonical);
                CachePin(FloraNodesBuffer, canonical, PinKind.Flora, identifier, sourceToken,
                    PinSourceDomain.Component);
            }
            else if (Map.GetClosestPin(center) == null)
            {
                var newPin = Map.AddPin(center, pinName, Config.SaveStaticObjectPins,
                    CreateTarget(component.gameObject, displayName));
                CachePin(FloraNodesBuffer, newPin, PinKind.Flora, identifier, sourceToken,
                    PinSourceDomain.Component);
            }

            OwnedFloraPinsScratch.Clear();
        }

        private static string ComputeFloraPinName(string displayName, int nodeCount)
        {
            return nodeCount > 1
                ? Automatics.L10N.LocalizeTextOnly(
                    "@text_automatic_mapping_flora_cluster_pin_name", displayName, nodeCount)
                : displayName;
        }

        private static bool MineralMapping(Component component, string name)
        {
            IEnumerable<Collider> GetColliders()
            {
                var empty = Array.Empty<Collider>();
                switch (component)
                {
                    case MineRock rock:
                    {
                        var array = Reflections.GetField<Collider[]>(rock, "m_hitAreas");
                        if (array == null) return empty;

                        if (!Objects.GetZNetView(rock, out var zNetView)) return empty;

                        for (var i = 0; i < array.Length; i++)
                            if (zNetView.GetZDO().GetFloat("Health" + i, rock.m_health) <= 0f)
                                return empty;

                        return array;
                    }
                    case MineRock5 rock5:
                    {
                        if (!Reflections.InvokeMethod<bool>(rock5, "NonDestroyed")) return empty;
                        return rock5.gameObject.GetComponentsInChildren<Collider>() ?? empty;
                    }
                    case Destructible destructible:
                    {
                        var collider = destructible.GetComponentInChildren<Collider>();
                        return !collider ? empty : new[] { collider };
                    }
                    default:
                    {
                        return empty;
                    }
                }
            }

            var sourceToken = name;
            if (!GetMineral(sourceToken, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            var identify = new MapPinIdentify(uniqueId);
            if (TryGetCachedPin(identify, out _))
            {
                MarkSeen(identify);
                return true;
            }

            if (ValheimObject.Mineral.GetName(data.Identifier, out var label))
                name = label;

            var pos = Vector3.zero;
            var count = 0;
            var maxHeight = float.MinValue;

            foreach (var collider in GetColliders())
            {
                ++count;
                var bounds = collider.bounds;
                pos += bounds.center;
                maxHeight = Mathf.Max(maxHeight, bounds.max.y);
            }

            if (count == 0 || pos == Vector3.zero) return true;

            pos /= count;

            if (Map.GetClosestPin(pos) != null) return true;

            if (Config.NeedToEquipWishboneForUndergroundMinerals)
                if (maxHeight < ZoneSystem.instance.GetGroundHeight(pos))
                {
                    var items = Player.m_localPlayer.GetInventory().GetEquippedItems();
                    if (items.Select(x => x.m_shared.m_name).All(x => x != "$item_wishbone"))
                        return true;
                }

            AddPin(uniqueId, pos, name, CreateTarget(component.gameObject, name), PinKind.Mineral,
                data.Identifier, sourceToken, PinSourceDomain.Component);
            return true;
        }

        private static bool SpawnerMapping(Component component, string name)
        {
            var sourceToken = name;
            if (!GetSpawner(sourceToken, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            var identify = new MapPinIdentify(uniqueId);
            if (TryGetCachedPin(identify, out _))
            {
                MarkSeen(identify);
                return true;
            }

            if (ValheimObject.Spawner.GetName(data.Identifier, out var label))
                name = label;

            var position = component.transform.position;
            if (Map.GetClosestPin(position) == null)
                AddPin(uniqueId, position, name, CreateTarget(component.gameObject, name),
                    PinKind.Spawner, data.Identifier, sourceToken, PinSourceDomain.Component);

            return true;
        }

        private static bool OtherMapping(Component component, string name)
        {
            var sourceToken = name;
            if (!GetOther(sourceToken, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            var identify = new MapPinIdentify(uniqueId);
            if (TryGetCachedPin(identify, out _))
            {
                MarkSeen(identify);
                return true;
            }

            if (MappingObject.Other.GetName(data.Identifier, out var label))
                name = label;

            var position = component.transform.position;
            if (Map.GetClosestPin(position) == null)
                AddPin(uniqueId, position, name, CreateTarget(component.gameObject, name),
                    PinKind.Other, data.Identifier, sourceToken, PinSourceDomain.Component);

            return true;
        }

        private static bool PortalMapping(Component component, string name)
        {
            if (!Config.AllowPinningPortal) return false;

            var portal = component.GetComponent<TeleportWorld>();
            if (!portal) return false;

            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            var identify = new MapPinIdentify(uniqueId);

            var tag = portal.GetText();
            var pinName = string.IsNullOrEmpty(tag)
                ? Automatics.L10N.Translate("@text_automatic_mapping_empty_portal_tag")
                : tag;

            if (TryGetCachedPin(identify, out var pinData))
            {
                pinData.m_name = pinName;
                MarkSeen(identify);
                return true;
            }

            var pos = component.transform.position;
            pinData = Map.GetClosestPin(pos);
            if (pinData == null)
                AddPin(uniqueId, pos, pinName, true, CreateTarget(component.gameObject, name),
                    PinKind.Portal, string.Empty, name, PinSourceDomain.Component);
            else
                pinData.m_name = pinName;

            return true;
        }

        private static bool DungeonMapping(ZoneSystem.LocationInstance instance)
        {
            Teleport GetTeleport(Collider collider)
            {
                return collider.GetComponent<Teleport>();
            }

            var prefabName = instance.m_location.m_prefabName;
            if (!GetDungeon(prefabName, out var data)) return false;
            if (!data.IsAllowed) return true;

            if (!ValheimObject.Dungeon.GetName(data.Identifier, out var name))
                name = $"@location_{prefabName.ToLower()}";

            var pos = instance.m_position;
            var radius = instance.m_location.m_exteriorRadius;
            foreach (var (collider, _, _) in Objects
                         .GetInsideSphere(pos, radius, GetTeleport, ColliderBuffer, DungeonMask)
                         .OrderBy(x => x.distance))
            {
                var entrance = collider.bounds.center;
                var identify = new MapPinIdentify(entrance);
                if (TryGetCachedPin(identify, out _))
                {
                    MarkSeen(identify);
                    return true;
                }

                if (Map.GetClosestPin(entrance) == null)
                    AddPin(ZDOID.None, entrance, name, CreateTarget(prefabName, name),
                        PinKind.Dungeon, data.Identifier, prefabName, PinSourceDomain.Location);

                return true;
            }

            var locationIdentify = new MapPinIdentify(pos);
            if (TryGetCachedPin(locationIdentify, out _))
            {
                MarkSeen(locationIdentify);
                return true;
            }

            if (Map.GetClosestPin(pos, radius, x => x.m_name == name) == null)
            {
                AddPin(ZDOID.None, pos, name, CreateTarget(prefabName, name), PinKind.Dungeon,
                    data.Identifier, prefabName, PinSourceDomain.Location);
                Automatics.Logger.Warning(() => $"Dungeon {name} has no entrance.");
            }

            return true;
        }

        private static bool SpotMapping(ZoneSystem.LocationInstance instance)
        {
            var prefabName = instance.m_location.m_prefabName;
            if (!GetSpot(prefabName, out var data)) return false;
            if (!data.IsAllowed) return true;

            var pos = instance.m_position;
            var identify = new MapPinIdentify(pos);
            if (TryGetCachedPin(identify, out _))
            {
                MarkSeen(identify);
                return true;
            }

            if (!ValheimObject.Spot.GetName(data.Identifier, out var name))
                name = $"@location_{prefabName.ToLower()}";

            if (Map.GetClosestPin(pos) == null)
                AddPin(ZDOID.None, pos, name, CreateTarget(prefabName, name), PinKind.Spot,
                    data.Identifier, prefabName, PinSourceDomain.Location);

            return true;
        }

        private static Minimap.PinData AddPin(ZDOID uniqueId, Vector3 pos, string pinName, bool save,
            Target target, PinKind kind, string identifier, string sourceToken,
            PinSourceDomain domain)
        {
            var pinData = Map.AddPin(pos, pinName, save, target);
            var identify = uniqueId.IsNone()
                ? new MapPinIdentify(pos)
                : new MapPinIdentify(uniqueId);
            if (PinDataCache.TryGetValue(identify, out var existing) &&
                !ReferenceEquals(existing.PinData, pinData))
            {
                Automatics.Logger.Warning(() =>
                    $"PinData is already exists: [Existing: {existing.PinData.m_name}{existing.PinData.m_pos}, New: {pinName}{pos}]");
            }

            CachePin(identify, pinData, kind, identifier, sourceToken, domain);
            return pinData;
        }

        private static void AddPin(ZDOID uniqueId, Vector3 pos, string pinName, Target target,
            PinKind kind, string identifier, string sourceToken, PinSourceDomain domain)
        {
            AddPin(uniqueId, pos, pinName, Config.SaveStaticObjectPins, target, kind, identifier,
                sourceToken, domain);
        }

        private static bool TryGetCachedPin(MapPinIdentify identify, out Minimap.PinData pinData)
        {
            if (PinDataCache.TryGetValue(identify, out var entry) && entry?.PinData != null)
            {
                pinData = entry.PinData;
                return true;
            }

            pinData = null;
            return false;
        }

        private static bool TryGetCachedPin(IEnumerable<FloraNode> nodes,
            out Minimap.PinData pinData)
        {
            foreach (var node in nodes)
                if (TryGetCachedPin(new MapPinIdentify(node.UniqueId), out pinData))
                    return true;

            pinData = null;
            return false;
        }

        private static void CachePin(IEnumerable<FloraNode> nodes, Minimap.PinData pinData,
            PinKind kind, string identifier, string sourceToken, PinSourceDomain domain)
        {
            foreach (var node in nodes)
                CachePin(new MapPinIdentify(node.UniqueId), pinData, kind, identifier, sourceToken,
                    domain);
        }

        private static void CachePin(MapPinIdentify identify, Minimap.PinData pinData,
            PinKind kind, string identifier, string sourceToken, PinSourceDomain domain)
        {
            if (PinDataCache.TryGetValue(identify, out var existing))
            {
                if (ReferenceEquals(existing.PinData, pinData))
                {
                    existing.Kind = kind;
                    existing.Identifier = identifier;
                    existing.SourceToken = sourceToken;
                    existing.Domain = domain;
                    existing.LastSeenSweep = _currentSweepId;
                    return;
                }

                RemovePinKey(existing.PinData, identify);
            }

            PinDataCache[identify] = new PinCacheEntry
            {
                PinData = pinData,
                Kind = kind,
                Identifier = identifier,
                SourceToken = sourceToken,
                Domain = domain,
                LastSeenSweep = _currentSweepId
            };

            if (!PinKeyCache.TryGetValue(pinData, out var keys))
            {
                keys = new HashSet<MapPinIdentify>();
                PinKeyCache.Add(pinData, keys);
            }

            keys.Add(identify);
        }

        private static bool RemovePinFromCache(Minimap.PinData pinData)
        {
            if (!PinKeyCache.TryGetValue(pinData, out var keys)) return false;

            PinKeyCache.Remove(pinData);
            foreach (var key in keys)
                PinDataCache.Remove(key);

            return true;
        }

        private static void RemovePinKey(Minimap.PinData pinData, MapPinIdentify identify)
        {
            if (!PinKeyCache.TryGetValue(pinData, out var keys)) return;

            keys.Remove(identify);
            if (keys.Count == 0)
                PinKeyCache.Remove(pinData);
        }

        private static Target CreateTarget(string prefabName, string name)
        {
            return new Target { name = name, prefabName = prefabName };
        }

        private static Target CreateTarget(GameObject prefab, string name)
        {
            return CreateTarget(Objects.GetPrefabName(prefab), name);
        }

        public struct MapPinIdentify : IEquatable<MapPinIdentify>
        {
            public readonly ZDOID UniqueId;
            public readonly Vector3 Pos;

            private MapPinIdentify(ZDOID uniqueId, Vector3 pos)
            {
                UniqueId = uniqueId;
                Pos = pos;
            }

            public MapPinIdentify(ZDOID uniqueId) : this(uniqueId, Vector3.zero)
            {
            }

            public MapPinIdentify(Vector3 pos) : this(ZDOID.None, pos)
            {
            }

            public bool IsUniqueId()
            {
                return UniqueId.UserID != 0 && UniqueId.ID != 0;
            }

            public bool IsPos()
            {
                return Pos != Vector3.zero;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (UniqueId.GetHashCode() * 397) ^ Pos.GetHashCode();
                }
            }

            public bool UniqueIdEquals(ZDOID id)
            {
                return UniqueId == id;
            }

            public bool PosEquals(Vector3 pos)
            {
                return Pos == pos;
            }

            public bool Equals(MapPinIdentify other)
            {
                if (IsUniqueId() && other.IsUniqueId()) return UniqueIdEquals(other.UniqueId);
                if (IsPos() && other.IsPos()) return PosEquals(other.Pos);
                return false;
            }
        }
    }
}
