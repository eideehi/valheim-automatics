using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Automatics.Valheim;
using HarmonyLib;
using JetBrains.Annotations;
using ModUtils;
using Splatform;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class DynamicObjectMapping
    {
        private const float PinSmoothingTime = 0.2f;
        private const float PinSnapDistance = 96f;
        private const float PinSnapDistanceSq = PinSnapDistance * PinSnapDistance;

        // A scanned position within ~0.1 m of the previous target is
        // treated as noise: PinTargetCache is not rewritten and the pin
        // stays out of the dirty set. The same radius, paired with a
        // ~0.01 m/s velocity floor, is what lets AnimatePins declare a
        // pin settled so idle creatures and docked ships never touch
        // the foreach body.
        private const float PinTargetEpsilonSq = 0.01f;
        private const float PinVelocitySettledSq = 0.0001f;

        private enum CharacterKind
        {
            None,
            Animal,
            Monster
        }

        // BaseName tracks the m_name the identifier was derived from: a tamed
        // creature renamed via Tameable.SetName changes m_name without touching
        // any registry, so the Version check alone would serve a stale
        // classification. Entries mutate in place on refresh to avoid
        // per-registry-bump allocations.
        private sealed class CachedIdentifier
        {
            public int Version;
            public string BaseName;
            public CharacterKind Kind;
            public string Identifier;
        }

        // BaseName exists for the same tamed-rename reason as in CachedIdentifier;
        // without it a renamed creature would keep its old pin name until the
        // level changed.
        private sealed class LevelPinNameCache
        {
            public int Level;
            public string BaseName;
            public string PinName;
        }

        private static readonly Dictionary<ZDOID, Minimap.PinData> PinDataCache;
        private static readonly Dictionary<Minimap.PinData, ZDOID> PinKeyCache;
        private static readonly Dictionary<ZDOID, PinCategory> PinCategoryCache =
            new Dictionary<ZDOID, PinCategory>();
        private static readonly Dictionary<ZDOID, Vector3> PinTargetCache;
        private static readonly Dictionary<ZDOID, Vector3> PinVelocityCache;
        private static readonly IDictionary<ZDOID, Minimap.PinData> VehiclePinCache;
        private static readonly Dictionary<Minimap.PinData, ZDOID> VehiclePinKeyCache;
        private static readonly Dictionary<ZDOID, Vector3> VehiclePinTargetCache;
        private static readonly Dictionary<ZDOID, Vector3> VehiclePinVelocityCache;
        private static readonly HashSet<ZDOID> KnownObjects;
        private static readonly ISet<ZDOID> EmptyCacheKeys;
        private static readonly List<Fish> FishBuffer;
        private static readonly List<RandomFlyingBird> BirdBuffer;
        private static readonly List<Piece> ShipBuffer;
        private static readonly List<Component> VehicleBuffer;
        // Reused by RemoveCachedPins so the stale-key drain does not allocate
        // a new List every tick.
        private static readonly List<ZDOID> RemoveKeyBuffer;

        // AnimatePins walks only pins whose target moved or whose
        // SmoothDamp has not yet converged. Ids are added when a scan
        // crosses the target-epsilon threshold and cleared once the
        // pin snaps to target with near-zero velocity.
        private static readonly HashSet<ZDOID> DirtyPins;
        private static readonly HashSet<ZDOID> VehicleDirtyPins;
        // Snapshot buffer so AnimatePins can drop settled ids mid-iteration
        // without allocating or mutating the live set during enumeration.
        private static readonly List<ZDOID> DirtyDrainBuffer;

        private const float VehiclePinAdoptionRadius = 8f;
        private const float VehiclePinAdoptionRadiusSq =
            VehiclePinAdoptionRadius * VehiclePinAdoptionRadius;

        // Weak keys so destroyed Character instances fall out without manual
        // bookkeeping; nothing else scans these tables.
        private static readonly ConditionalWeakTable<Character, CachedIdentifier>
            CharacterIdentifierCache = new ConditionalWeakTable<Character, CachedIdentifier>();
        private static readonly ConditionalWeakTable<Character, LevelPinNameCache>
            CharacterLevelNameCache = new ConditionalWeakTable<Character, LevelPinNameCache>();

        // Bumped on Animal / Monster registry mutation only — deliberately
        // independent of the static-mapping classifier version so custom_flora
        // edits do not invalidate character caches and vice versa.
        private static int _dynamicClassifierVersion;

        // Vagon.m_instances resolved once into a cached static-field ref so the
        // 10 Hz vehicle scan does not allocate a Traverse per call. Guarded bind
        // falls back to reflection if the field is renamed (mirrors Map._pinsRef).
        private static AccessTools.FieldRef<List<Vagon>> _vagonInstancesRef;
        private static bool _vagonInstancesRefBound;

        static DynamicObjectMapping()
        {
            PinDataCache = new Dictionary<ZDOID, Minimap.PinData>();
            PinKeyCache = new Dictionary<Minimap.PinData, ZDOID>();
            PinTargetCache = new Dictionary<ZDOID, Vector3>();
            PinVelocityCache = new Dictionary<ZDOID, Vector3>();
            VehiclePinCache = new Dictionary<ZDOID, Minimap.PinData>();
            VehiclePinKeyCache = new Dictionary<Minimap.PinData, ZDOID>();
            VehiclePinTargetCache = new Dictionary<ZDOID, Vector3>();
            VehiclePinVelocityCache = new Dictionary<ZDOID, Vector3>();
            KnownObjects = new HashSet<ZDOID>();
            EmptyCacheKeys = new HashSet<ZDOID>(0);
            FishBuffer = new List<Fish>();
            BirdBuffer = new List<RandomFlyingBird>();
            ShipBuffer = new List<Piece>();
            VehicleBuffer = new List<Component>();
            RemoveKeyBuffer = new List<ZDOID>();
            DirtyPins = new HashSet<ZDOID>();
            VehicleDirtyPins = new HashSet<ZDOID>();
            DirtyDrainBuffer = new List<ZDOID>();
        }

        /// <summary>
        ///     Called when the module is turned on. Registry changes made while it was off were
        ///     not observed, so every cached character classification is made stale and resolved
        ///     again from the live registries on its next scan.
        /// </summary>
        internal static void InvalidateClassification()
        {
            _dynamicClassifierVersion++;
        }

        // Subscribed to ValheimObject.RegistryChanged through the module's scope while it runs.
        internal static void OnValheimObjectRegistryChanged(ValheimObject obj)
        {
            // Only the registries the dynamic scan reads from (Animal /
            // Monster) should invalidate cached identifiers. Static-domain
            // changes flow through StaticObjectMapping's own handler.
            if (ReferenceEquals(obj, ValheimObject.Animal) ||
                ReferenceEquals(obj, ValheimObject.Monster))
            {
                _dynamicClassifierVersion++;
            }
        }

        /// <summary>
        /// Resolves a <see cref="Character"/> to its Animal / Monster
        /// identifier through a weak per-Character cache. Negative results
        /// are cached as <see cref="CharacterKind.None"/> so unmappable
        /// creatures stop paying dict-lookup cost every tick. The cache
        /// self-revalidates on registry-version bump or when
        /// <c>character.m_name</c> drifts (tamed rename via
        /// <c>Tameable.SetName</c>), so a rename that moves a creature in or
        /// out of allowlist membership is reflected on the next scan.
        /// </summary>
        private static bool TryResolveCharacterIdentifier(Character character,
            out CharacterKind kind, out string identifier)
        {
            var version = _dynamicClassifierVersion;
            var name = character.m_name;
            if (CharacterIdentifierCache.TryGetValue(character, out var entry) &&
                entry.Version == version &&
                ReferenceEquals(entry.BaseName, name))
            {
                kind = entry.Kind;
                identifier = entry.Identifier;
                return kind != CharacterKind.None;
            }

            CharacterKind resolvedKind;
            string resolvedIdentifier;
            if (ValheimObject.Animal.GetIdentify(name, out var animalIdent))
            {
                resolvedKind = CharacterKind.Animal;
                resolvedIdentifier = animalIdent;
            }
            else if (ValheimObject.Monster.GetIdentify(name, out var monsterIdent))
            {
                resolvedKind = CharacterKind.Monster;
                resolvedIdentifier = monsterIdent;
            }
            else
            {
                resolvedKind = CharacterKind.None;
                resolvedIdentifier = null;
            }

            if (entry == null)
            {
                CharacterIdentifierCache.Add(character, new CachedIdentifier
                {
                    Version = version,
                    BaseName = name,
                    Kind = resolvedKind,
                    Identifier = resolvedIdentifier
                });
            }
            else
            {
                entry.Version = version;
                entry.BaseName = name;
                entry.Kind = resolvedKind;
                entry.Identifier = resolvedIdentifier;
            }

            kind = resolvedKind;
            identifier = resolvedIdentifier;
            return kind != CharacterKind.None;
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
            PinCategoryCache.Clear();
            PinTargetCache.Clear();
            PinVelocityCache.Clear();
            VehiclePinCache.Clear();
            VehiclePinKeyCache.Clear();
            VehiclePinTargetCache.Clear();
            VehiclePinVelocityCache.Clear();
            KnownObjects.Clear();
            DirtyPins.Clear();
            VehicleDirtyPins.Clear();
        }

        public static void OnObjectDestroy(Component component)
        {
            // Reached from a vehicle's destroy callback, which stays on the vehicle while the
            // module is off.
            if (!Module.Running) return;
            if (component.GetComponent<Ship>() || component.GetComponent<Vagon>())
            {
                if (!Objects.GetZdoid(component, out var uniqueId)) return;
                RemoveVehiclePin(uniqueId);
            }
        }

        // Reached for every world object whose ZDO is destroyed, in every game that holds it (see
        // AutomaticMapping). Only vehicles are keyed in the vehicle pin cache, and a vehicle whose
        // own destroy callback ran here first has no entry left.
        internal static void OnZdoDestroyed(ZDOID uniqueId)
        {
            RemoveVehiclePin(uniqueId);
        }

        private static void RemoveVehiclePin(ZDOID uniqueId)
        {
            if (!VehiclePinCache.TryGetValue(uniqueId, out var pin)) return;
            RemoveVehiclePinFromCache(uniqueId);
            if (!Config.EnableAutomaticMapping) return;
            Map.RemovePin(pin);
        }

        public static bool RemoveCachedPins(ISet<ZDOID> excludes = null)
        {
            // A null excludes set means a full turn-off (mapping disabled or
            // DynamicObjectMappingRange <= 0), not a normal scan. Drain the vehicle pin
            // cache on that path too so its ZDOID->PinData entries do not leak for the
            // session; the saved vehicle minimap pins persist and are re-adopted by
            // TryFindExistingVehiclePin when mapping resumes.
            var removed = false;
            if (excludes is null)
                removed = RemoveVehicleCachedPins();

            if (PinDataCache.Count == 0) return removed;

            if (excludes is null)
                excludes = EmptyCacheKeys;

            RemoveKeyBuffer.Clear();
            foreach (var key in PinDataCache.Keys)
            {
                if (!excludes.Contains(key))
                    RemoveKeyBuffer.Add(key);
            }

            for (var i = 0; i < RemoveKeyBuffer.Count; i++)
            {
                var key = RemoveKeyBuffer[i];
                if (!PinDataCache.TryGetValue(key, out var pinData)) continue;
                removed = true;
                PinDataCache.Remove(key);
                PinCategoryCache.Remove(key);
                PinKeyCache.Remove(pinData);
                PinTargetCache.Remove(key);
                PinVelocityCache.Remove(key);
                DirtyPins.Remove(key);
                if (!pinData.m_save)
                    Map.RemovePin(pinData);
            }

            RemoveKeyBuffer.Clear();
            return removed;
        }

        public static void OnRemovePin(Minimap.PinData pinData)
        {
            RemovePinFromCache(pinData);
            RemoveVehiclePinFromCache(pinData);
        }

        // Drops every vehicle pin cache entry without touching the saved minimap pins
        // (vehicle pins are created with save=true). Used when the dynamic scan turns off
        // so the per-vehicle cache dictionaries do not accumulate for the whole session.
        private static bool RemoveVehicleCachedPins()
        {
            if (VehiclePinCache.Count == 0 && VehicleDirtyPins.Count == 0) return false;

            VehiclePinCache.Clear();
            VehiclePinKeyCache.Clear();
            VehiclePinTargetCache.Clear();
            VehiclePinVelocityCache.Clear();
            VehicleDirtyPins.Clear();
            return true;
        }

        /// <summary>
        ///     Called before the game saves the map, so a saved vehicle pin is written where its
        ///     vehicle is rather than at a point on its way there.
        /// </summary>
        public static void FlushVehiclePins()
        {
            if (!Module.Running) return;
            MoveVehiclePinsToTargets();
        }

        /// <summary>
        ///     Moves every vehicle pin this module holds to the last position its vehicle was seen
        ///     at. Not gated: a disable calls it after the module has stopped, so the pins it then
        ///     lets go of as ordinary saved pins are left where their vehicles are.
        /// </summary>
        internal static void MoveVehiclePinsToTargets()
        {
            if (VehiclePinCache.Count == 0) return;

            RemoveKeyBuffer.Clear();
            foreach (var pair in VehiclePinCache)
            {
                var uniqueId = pair.Key;
                var pinData = pair.Value;
                if (pinData == null ||
                    !Map.ContainsPin(pinData) ||
                    !VehiclePinTargetCache.TryGetValue(uniqueId, out var target))
                {
                    RemoveKeyBuffer.Add(uniqueId);
                    continue;
                }

                Map.MovePin(pinData, target);
                VehiclePinVelocityCache[uniqueId] = Vector3.zero;
                VehicleDirtyPins.Remove(uniqueId);
            }

            for (var i = 0; i < RemoveKeyBuffer.Count; i++)
                RemoveVehiclePinFromCache(RemoveKeyBuffer[i]);

            RemoveKeyBuffer.Clear();
        }

        public static bool SetSaveFlag(Minimap.PinData pinData)
        {
            if (!RemovePinFromCache(pinData)) return false;

            Map.UntrackAutomaticPin(pinData);
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

                AnimateDirty(PinDataCache, PinTargetCache, PinVelocityCache, DirtyPins, delta);
                AnimateDirty(VehiclePinCache, VehiclePinTargetCache, VehiclePinVelocityCache,
                    VehicleDirtyPins, delta);
            }
        }

        private static void AnimateDirty(
            IDictionary<ZDOID, Minimap.PinData> pinCache,
            IDictionary<ZDOID, Vector3> targetCache,
            IDictionary<ZDOID, Vector3> velocityCache,
            HashSet<ZDOID> dirty,
            float delta)
        {
            if (dirty.Count == 0) return;

            // Snapshot to let the loop body mutate `dirty` (removing settled
            // entries) without invalidating the enumerator.
            DirtyDrainBuffer.Clear();
            foreach (var id in dirty) DirtyDrainBuffer.Add(id);

            for (var i = 0; i < DirtyDrainBuffer.Count; i++)
            {
                var id = DirtyDrainBuffer[i];
                if (!pinCache.TryGetValue(id, out var pinData) || pinData == null ||
                    !targetCache.TryGetValue(id, out var target))
                {
                    dirty.Remove(id);
                    continue;
                }

                if (StepPinAnimation(velocityCache, id, pinData, target, delta))
                    dirty.Remove(id);
            }

            DirtyDrainBuffer.Clear();
        }

        // Returns true once the pin has converged (within the target
        // epsilon and below the velocity floor) so the caller can drop
        // it from the dirty set. Assumes delta > 0 — AnimatePins guards
        // that before calling.
        private static bool StepPinAnimation(
            IDictionary<ZDOID, Vector3> velocityCache, ZDOID uniqueId,
            Minimap.PinData pinData, Vector3 target, float delta)
        {
            var current = pinData.m_pos;
            if (!velocityCache.TryGetValue(uniqueId, out var velocity))
                velocity = Vector3.zero;

            Vector3 next;
            if ((current - target).sqrMagnitude >= PinSnapDistanceSq)
            {
                next = target;
                velocity = Vector3.zero;
            }
            else
            {
                next = Vector3.SmoothDamp(current, target, ref velocity, PinSmoothingTime,
                    Mathf.Infinity, delta);
            }

            var settled = (next - target).sqrMagnitude < PinTargetEpsilonSq &&
                          velocity.sqrMagnitude < PinVelocitySettledSq;
            if (settled)
            {
                next = target;
                velocity = Vector3.zero;
            }

            velocityCache[uniqueId] = velocity;
            Map.MovePin(pinData, next);
            return settled;
        }

        public static void Mapping(float delta)
        {
            using (MappingProfiler.BeginScope(MappingProfiler.SlotDynamicMapping))
            {
                var range = Config.DynamicObjectMappingRange;
                if (range <= 0)
                {
                    RemoveCachedPins();
                    return;
                }

                var rangeSq = (float)range * range;
                var origin = Player.m_localPlayer.transform.position;

                KnownObjects.Clear();

                // Vehicle has its own allowlist check inside VehicleMapping;
                // the sub-loops below benefit from a matching early-out when
                // their category is empty.
                var animalAllowlist = Config.AllowPinningAnimal;
                var monsterAllowlist = Config.AllowPinningMonster;
                var skipCharacters = animalAllowlist.Count == 0 && monsterAllowlist.Count == 0;
                var notPinningTamed = Config.NotPinningTamedAnimals;

                if (!skipCharacters)
                {
                    foreach (var character in Character.GetAllCharacters())
                    {
                        if (character.IsPlayer()) continue;

                        var position = character.transform.position;
                        if ((origin - position).sqrMagnitude > rangeSq) continue;

                        if (!TryResolveCharacterIdentifier(character, out var kind,
                                out var identifier)) continue;

                        if (kind == CharacterKind.Animal)
                        {
                            if (!animalAllowlist.Contains(identifier)) continue;
                            if (character.IsTamed() && notPinningTamed) continue;
                        }
                        else
                        {
                            if (!monsterAllowlist.Contains(identifier)) continue;
                        }

                        AddOrUpdatePin(character, kind, delta);
                    }
                }

                if (Config.PinAnimalIncludesFish)
                {
                    FishCache.Fill(FishBuffer);
                    for (var i = 0; i < FishBuffer.Count; i++)
                    {
                        var fish = FishBuffer[i];
                        if ((origin - fish.transform.position).sqrMagnitude > rangeSq) continue;
                        AddOrUpdatePin(fish, delta);
                    }
                }

                if (Config.PinAnimalIncludesBird)
                {
                    BirdCache.Fill(BirdBuffer);
                    for (var i = 0; i < BirdBuffer.Count; i++)
                    {
                        var bird = BirdBuffer[i];
                        if ((origin - bird.transform.position).sqrMagnitude > rangeSq) continue;
                        AddOrUpdatePin(bird, delta);
                    }
                }

                VehicleMapping(origin, rangeSq, delta);

                RemoveCachedPins(KnownObjects);
            }
        }

        private static void VehicleMapping(Vector3 origin, float rangeSq, float delta)
        {
            if (Config.AllowPinningVehicle.Count == 0) return;

            VehicleBuffer.Clear();
            ShipCache.Fill(ShipBuffer);
            for (var i = 0; i < ShipBuffer.Count; i++)
                VehicleBuffer.Add(ShipBuffer[i]);

            if (!_vagonInstancesRefBound)
            {
                _vagonInstancesRefBound = true;
                try
                {
                    var field = AccessTools.Field(typeof(Vagon), "m_instances");
                    _vagonInstancesRef = field != null
                        ? AccessTools.StaticFieldRefAccess<List<Vagon>>(field)
                        : null;
                }
                catch (Exception e)
                {
                    Automatics.Logger.Warning(() =>
                        $"Failed to bind Vagon.m_instances field ref; falling back to reflection: {e.Message}");
                    _vagonInstancesRef = null;
                }
            }

            var wagons = _vagonInstancesRef != null
                ? _vagonInstancesRef()
                : Reflections.GetStaticField<Vagon, List<Vagon>>("m_instances");
            if (wagons != null)
                for (var i = 0; i < wagons.Count; i++)
                    VehicleBuffer.Add(wagons[i]);

            for (var i = 0; i < VehicleBuffer.Count; i++)
            {
                var vehicle = VehicleBuffer[i];
                if (!Objects.GetZdoid(vehicle, out var uniqueId)) continue;

                var pos = vehicle.transform.position;
                if ((origin - pos).sqrMagnitude > rangeSq) continue;

                var name = Objects.GetName(vehicle);
                if (!GetVehicle(name, out var vehicleData) || !vehicleData.IsAllowed) continue;

                if (TryGetCachedVehiclePin(uniqueId, out _))
                {
                    TryMarkTargetDirty(VehiclePinTargetCache, VehicleDirtyPins, uniqueId, pos);
                }
                else
                {
                    var target = CreateTarget(vehicle.gameObject, name);
                    if (TryFindExistingVehiclePin(pos, name, target, out var pin))
                    {
                        CacheVehiclePin(uniqueId, pin);
                        MarkVehiclePin(pin);
                        VehiclePinTargetCache[uniqueId] = pin.m_pos;
                        VehiclePinVelocityCache[uniqueId] = Vector3.zero;
                        TryMarkTargetDirty(VehiclePinTargetCache, VehicleDirtyPins, uniqueId, pos);
                    }
                    else
                    {
                        pin = Map.AddPin(pos, name, true, target);
                        MarkVehiclePin(pin);
                        CacheVehiclePin(uniqueId, pin);
                        VehiclePinTargetCache[uniqueId] = pos;
                        VehiclePinVelocityCache[uniqueId] = Vector3.zero;
                    }
                }
            }
        }

        private static bool TryGetCachedVehiclePin(ZDOID uniqueId, out Minimap.PinData pinData)
        {
            if (VehiclePinCache.TryGetValue(uniqueId, out pinData) &&
                pinData != null &&
                Map.ContainsPin(pinData))
                return true;

            RemoveVehiclePinFromCache(uniqueId);
            pinData = null;
            return false;
        }

        private static bool TryFindExistingVehiclePin(Vector3 pos, string pinName, Target target,
            out Minimap.PinData pinData)
        {
            var pinType = IconPack.GetPinType(target);
            var pins = Map.GetAllPins();
            var bestDistanceSq = float.MaxValue;
            pinData = null;

            for (var i = 0; i < pins.Count; i++)
            {
                // The type is checked for every candidate, not only for an unmarked one: a saved
                // pin carries its author and a name a user can type, so without the vehicle pin
                // type a manual pin of the same name could be adopted, and then moved or removed
                // with the vehicle.
                var candidate = pins[i];
                if (candidate == null ||
                    !candidate.m_save ||
                    candidate.m_ownerID != 0L ||
                    candidate.m_name != pinName ||
                    !IsVehiclePinTypeCompatible(candidate.m_type, pinType) ||
                    VehiclePinKeyCache.ContainsKey(candidate) ||
                    (!IsMarkedVehiclePin(candidate) &&
                     !IsLikelyLegacyVehiclePin(candidate, pinName, pinType)))
                    continue;

                var distanceSq = HorizontalDistanceSq(pos, candidate.m_pos);
                if (distanceSq > VehiclePinAdoptionRadiusSq ||
                    distanceSq >= bestDistanceSq)
                    continue;

                pinData = candidate;
                bestDistanceSq = distanceSq;
            }

            return pinData != null;
        }

        private static bool IsLikelyLegacyVehiclePin(Minimap.PinData pinData, string pinName,
            Minimap.PinType currentPinType)
        {
            return pinData != null &&
                   !pinData.m_author.IsValid &&
                   !pinData.m_checked &&
                   IsInternalPinName(pinName) &&
                   IsVehiclePinTypeCompatible(pinData.m_type, currentPinType);
        }

        private static bool IsInternalPinName(string pinName)
        {
            return !string.IsNullOrEmpty(pinName) && pinName[0] == '$';
        }

        private static bool IsVehiclePinTypeCompatible(Minimap.PinType pinType,
            Minimap.PinType currentPinType)
        {
            return pinType == currentPinType || pinType == Minimap.PinType.Icon3;
        }

        private static float HorizontalDistanceSq(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private static void CacheVehiclePin(ZDOID uniqueId, Minimap.PinData pinData)
        {
            Map.TrackAutomaticPin(pinData);

            if (VehiclePinCache.TryGetValue(uniqueId, out var existing) &&
                !ReferenceEquals(existing, pinData))
                VehiclePinKeyCache.Remove(existing);

            VehiclePinCache[uniqueId] = pinData;
            VehiclePinKeyCache[pinData] = uniqueId;
        }

        private static void MarkVehiclePin(Minimap.PinData pinData)
        {
            if (pinData == null || !TryGetLocalPlatformUserId(out var userId)) return;
            pinData.m_author = userId;
        }

        private static bool IsMarkedVehiclePin(Minimap.PinData pinData)
        {
            return pinData != null &&
                   pinData.m_author.IsValid &&
                   TryGetLocalPlatformUserId(out var userId) &&
                   pinData.m_author == userId;
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

        private static bool RemoveVehiclePinFromCache(Minimap.PinData pinData)
        {
            if (pinData == null || !VehiclePinKeyCache.TryGetValue(pinData, out var uniqueId))
                return false;

            RemoveVehiclePinFromCache(uniqueId);
            return true;
        }

        private static bool RemoveVehiclePinFromCache(ZDOID uniqueId)
        {
            var found = VehiclePinCache.TryGetValue(uniqueId, out var pinData);
            if (found)
            {
                VehiclePinCache.Remove(uniqueId);
                if (pinData != null)
                    VehiclePinKeyCache.Remove(pinData);
            }

            VehiclePinTargetCache.Remove(uniqueId);
            VehiclePinVelocityCache.Remove(uniqueId);
            VehicleDirtyPins.Remove(uniqueId);
            return found;
        }

        private static void AddOrUpdatePin(Character character, CharacterKind kind, float delta)
        {
            var uniqueId = character.GetZDOID();
            if (!KnownObjects.Add(uniqueId)) return;

            var baseName = character.m_name;
            var level = character.GetLevel();
            var pinName = GetOrBuildPinName(character, baseName, level);

            var pos = character.transform.position;
            if (!PinDataCache.TryGetValue(uniqueId, out var pinData))
                AddPin(uniqueId, pos, pinName,
                    CreateTarget(character.gameObject, baseName, level));
            else
                UpdatePin(uniqueId, pinData, pinName, pos, delta);
            if (PinDataCache.ContainsKey(uniqueId))
                PinCategoryCache[uniqueId] = kind == CharacterKind.Animal
                    ? PinCategory.Animal : PinCategory.Monster;
        }

        /// <summary>
        /// Memoizes the level-symbol-appended pin name per Character so the
        /// scan path stops allocating a StringBuilder every tick. Rebuilds
        /// when the level or base name changes.
        /// </summary>
        private static string GetOrBuildPinName(Character character, string baseName, int level)
        {
            if (CharacterLevelNameCache.TryGetValue(character, out var entry) &&
                entry.Level == level &&
                ReferenceEquals(entry.BaseName, baseName))
            {
                return entry.PinName;
            }

            var pinName = BuildLevelPinName(baseName, level);
            if (entry == null)
            {
                CharacterLevelNameCache.Add(character, new LevelPinNameCache
                {
                    Level = level,
                    BaseName = baseName,
                    PinName = pinName
                });
            }
            else
            {
                entry.Level = level;
                entry.BaseName = baseName;
                entry.PinName = pinName;
            }

            return pinName;
        }

        private static string BuildLevelPinName(string baseName, int level)
        {
            if (level <= 1) return baseName;

            var symbol =
                Automatics.L10N.Translate("@text_automatic_mapping_creature_level_symbol");
            var sb = new StringBuilder(baseName).Append("\n");
            for (var i = 1; i < level; i++) sb.Append(symbol);
            return sb.ToString();
        }

        private static void AddOrUpdatePin(Component component, float delta)
        {
            if (!Objects.GetZdoid(component, out var uniqueId)) return;
            if (!KnownObjects.Add(uniqueId)) return;

            var pos = component.transform.position;
            // Build the pin name only on the add path: the steady-state update path
            // discards it (UpdatePin keeps pinData.m_name), so building it every
            // 10 Hz scan wasted ~3-4 string allocations per in-range object.
            if (!PinDataCache.TryGetValue(uniqueId, out var pinData))
            {
                var pinName = component is RandomFlyingBird bird
                    ? $"@animal_{Objects.GetPrefabName(bird.gameObject).ToLower()}"
                    : Objects.GetName(component);
                AddPin(uniqueId, pos, pinName, CreateTarget(component.gameObject, pinName));
            }
            else
            {
                UpdatePin(uniqueId, pinData, pinData.m_name, pos, delta);
            }
            if (PinDataCache.ContainsKey(uniqueId))
                PinCategoryCache[uniqueId] = PinCategory.Animal;
        }

        private static void AddPin(ZDOID uniqueId, Vector3 pos, string pinName, Target target)
        {
            if (Map.ShouldSuppressTransientAutomaticPin(pos)) return;

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
            PinCategoryCache.Remove(uniqueId);
            PinTargetCache.Remove(uniqueId);
            PinVelocityCache.Remove(uniqueId);
            DirtyPins.Remove(uniqueId);
            return true;
        }

        public static bool OwnsPin(Minimap.PinData pinData)
        {
            return pinData != null &&
                   (PinKeyCache.ContainsKey(pinData) ||
                    VehiclePinKeyCache.ContainsKey(pinData));
        }

        internal static bool TryGetPinCategory(Minimap.PinData pinData, out PinCategory category)
        {
            if (VehiclePinKeyCache.ContainsKey(pinData))
            {
                category = PinCategory.Vehicle;
                return true;
            }
            category = default;
            return PinKeyCache.TryGetValue(pinData, out var id) &&
                   PinCategoryCache.TryGetValue(id, out category);
        }

        internal static Minimap.PinData GetCachedPin(ZDOID uniqueId)
        {
            if (PinDataCache.TryGetValue(uniqueId, out var pin)) return pin;
            return VehiclePinCache.TryGetValue(uniqueId, out pin) ? pin : null;
        }

        internal static ZDOID GetCreatureId(Minimap.PinData pinData)
        {
            if (pinData == null || !PinKeyCache.TryGetValue(pinData, out var uniqueId))
                return ZDOID.None;
            var instance = ZNetScene.instance ? ZNetScene.instance.FindInstance(uniqueId) : null;
            var zdo = ZDOMan.instance?.GetZDO(uniqueId);
            var source = instance ? instance : zdo != null && ZNetScene.instance
                ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return source && source.GetComponent<Character>() ? uniqueId : ZDOID.None;
        }

        internal static bool TryGetPinId(Minimap.PinData pinData, out ZDOID id)
        {
            return PinKeyCache.TryGetValue(pinData, out id) || VehiclePinKeyCache.TryGetValue(pinData, out id);
        }

        internal static GameObject FindLiveInstance(Minimap.PinData pinData)
        {
            if (pinData == null || !ZNetScene.instance) return null;

            if (!PinKeyCache.TryGetValue(pinData, out var uniqueId) &&
                !VehiclePinKeyCache.TryGetValue(pinData, out uniqueId))
                return null;

            return ZNetScene.instance.FindInstance(uniqueId);
        }

        private static void UpdatePin(ZDOID uniqueId, Minimap.PinData pinData, string pinName, Vector3 pos,
            float delta)
        {
            if (Map.ShouldSuppressTransientAutomaticPin(pos))
            {
                RemovePinFromCache(pinData);
                Map.RemovePin(pinData);
                return;
            }

            // The empty-name guard preserves intentionally blank pins
            // (CreaturePinTextHidden etc). The ref-equality check skips the
            // write when the memoized pin name is the same instance the
            // previous tick stored — ordinary case during steady state.
            if (!string.IsNullOrEmpty(pinData.m_name) &&
                !ReferenceEquals(pinData.m_name, pinName))
                pinData.m_name = pinName;

            TryMarkTargetDirty(PinTargetCache, DirtyPins, uniqueId, pos);
        }

        // Shared by dynamic and vehicle scan paths: rewrites the per-pin
        // target only when the new position has drifted past the
        // target-epsilon threshold, and re-enters the pin into the dirty
        // set so AnimatePins picks it up on the next tick.
        private static bool TryMarkTargetDirty(
            IDictionary<ZDOID, Vector3> targetCache, HashSet<ZDOID> dirty,
            ZDOID uniqueId, Vector3 pos)
        {
            if (targetCache.TryGetValue(uniqueId, out var oldTarget) &&
                (oldTarget - pos).sqrMagnitude < PinTargetEpsilonSq)
            {
                return false;
            }

            targetCache[uniqueId] = pos;
            dirty.Add(uniqueId);
            return true;
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
}
