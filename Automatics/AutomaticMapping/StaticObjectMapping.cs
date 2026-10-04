using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Automatics.Valheim;
using JetBrains.Annotations;
using ModUtils;
using Splatform;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
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

        // Location.s_allLocations is a private static List<Location>; the field
        // reference is set once via initializer and only mutated in-place by
        // Awake/OnDestroy, so resolving it once via Traverse is safe and avoids
        // per-tick reflection inside Mapping.
        private const string LocationCloneSuffix = "(Clone)";
        private static readonly Lazy<List<Location>> AllLocationsLazy = new Lazy<List<Location>>(
            () => Reflections.GetStaticField<Location, List<Location>>("s_allLocations"));

        internal static IReadOnlyList<Location> LoadedLocations => AllLocationsLazy.Value;

        // The name Automatic Mapping gives a known dungeon or spot. It can be the fallback
        // `@location_<prefab>` key without a translation; callers that show it to the player
        // check the translation first. Automatic pins keep using the name as is.
        internal static bool TryGetLocationName(string prefabName, out string name)
        {
            if (ValheimObject.Dungeon.GetIdentify(prefabName, out var identifier))
            {
                name = GetLocationName(ValheimObject.Dungeon, identifier, prefabName);
                return true;
            }
            if (ValheimObject.Spot.GetIdentify(prefabName, out identifier))
            {
                name = GetLocationName(ValheimObject.Spot, identifier, prefabName);
                return true;
            }
            name = "";
            return false;
        }

        private static string GetLocationName(ValheimObject registry, string identifier, string prefabName)
        {
            return registry.GetName(identifier, out var name) ? name : $"@location_{prefabName.ToLower()}";
        }

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
            // The key runs one pass whatever the interval is, and the interval starts over.
            if (takeInput && !Minimap.InTextInput() && Config.StaticObjectMappingKey.MainKey != KeyCode.None &&
                Config.StaticObjectMappingKey.IsDown())
            {
                _mappingTimer = 0f;
                return true;
            }

            if (Config.StaticObjectMappingInterval <= 0) return false;

            _mappingTimer += delta;
            if (_mappingTimer < Config.StaticObjectMappingInterval) return false;
            _mappingTimer = 0f;

            return true;
        }

        // Shared body for the per-domain resolvers below: classify a name through the
        // domain registry and report whether the resulting identifier is allow-listed.
        private static bool ResolveStaticObject(ValheimObject registry, StringList allowlist,
            string name, out (string Identifier, bool IsAllowed) data)
        {
            if (registry.GetIdentify(name, out var identifier))
            {
                data = (identifier, allowlist.Contains(identifier));
                return true;
            }

            data = ("", false);
            return false;
        }

        private static bool GetFlora(string name, out (string Identifier, bool IsAllowed) data)
            => ResolveStaticObject(ValheimObject.Flora, Config.AllowPinningFlora, name, out data);

        private static bool GetMineral(string name, out (string Identifier, bool IsAllowed) data)
            => ResolveStaticObject(ValheimObject.Mineral, Config.AllowPinningMineral, name, out data);

        private static bool GetSpawner(string name, out (string Identifier, bool IsAllowed) data)
            => ResolveStaticObject(ValheimObject.Spawner, Config.AllowPinningSpawner, name, out data);

        private static bool GetOther(string name, out (string Identifier, bool IsAllowed) data)
            => ResolveStaticObject(MappingObject.Other, Config.AllowPinningOther, name, out data);

        private static bool GetDungeon(string name, out (string Identifier, bool IsAllowed) data)
            => ResolveStaticObject(ValheimObject.Dungeon, Config.AllowPinningDungeon, name, out data);

        private static string StripCloneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.EndsWith(LocationCloneSuffix, StringComparison.Ordinal))
                return name.Substring(0, name.Length - LocationCloneSuffix.Length);
            return name;
        }

        private static bool GetSpot(string name, out (string Identifier, bool IsAllowed) data)
            => ResolveStaticObject(ValheimObject.Spot, Config.AllowPinningSpot, name, out data);

        [UsedImplicitly]
        public static void OnObjectDestroy(Component component, ZNetView zNetView)
        {
            // try/finally so a gated early return still drops the cached
            // colliders. MineRock5_DamageArea_Transpiler only routes
            // fully-destroyed rocks here, so the entry is never useful again
            // and holding it would leak until the next world unload. The
            // module gate is the first statement inside the try: this is
            // reached from destroy callbacks that stay on objects while the
            // module is off, and from the SetPicked and DamageArea transpilers.
            try
            {
                if (!Module.Running) return;
                if (!Config.EnableAutomaticMapping) return;
                if (!Config.RemovePinsOfDestroyedObject) return;
                if (zNetView == null || !zNetView.IsValid()) return;
                if (!Objects.GetZdoid(component, out var uniqueId)) return;

                var identify = new MapPinIdentify(uniqueId);
                var name = Objects.GetName(component);
                if (GetFlora(name, out var data))
                {
                    if (!data.IsAllowed) return;

                    var node = component.GetComponent<FloraNode>();
                    if (!node || node.Network == null || node.Network.NodeCount <= 1)
                        RemoveOwnedPinIfSingleLiveFloraOwner(identify);
                }
                else if (GetMineral(name, out data))
                {
                    if (!data.IsAllowed) return;
                    RemoveOwnedPin(identify);
                }
                else if (GetSpawner(name, out data) ||
                         GetOther(name, out data))
                {
                    if (!data.IsAllowed) return;
                    RemoveOwnedPin(identify);
                }
                else if (component.GetComponent<TeleportWorld>())
                {
                    if (!Config.AllowPinningPortal) return;
                    RemoveOwnedPin(identify);
                }
            }
            finally
            {
                if (component is MineRock5 rock5)
                    MineRock5Cache.Unregister(rock5);
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

            Map.UntrackAutomaticPin(pinData);
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

                var staticRange = Config.StaticObjectMappingRange;
                var staticRangeSq = (float)staticRange * staticRange;

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
                    if ((origin - pos).sqrMagnitude > staticRangeSq) continue;
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

                var locationRange = Config.LocationMappingRange;
                var locationRangeSq = (float)locationRange * locationRange;
                foreach (var pair in ZoneSystem.instance.m_locationInstances)
                {
                    var location = pair.Value;
                    if ((origin - location.m_position).sqrMagnitude > locationRangeSq) continue;
                    if (DungeonMapping(location)) continue;
                    if (SpotMapping(location)) continue;
                }

                // ZoneSystem.m_locationInstances is server-only, so non-host
                // peers (dedicated-server clients, P2P clients) reach this
                // point with no dungeon pins added. Location.s_allLocations
                // is populated by every LocationProxy spawn regardless of
                // role, so iterate it here as a fallback. Gate on Count == 0
                // to keep SP / host on the single location-domain path
                // above: running both paths exposes a no-entrance dedup
                // race where SnapToGround.Snap shifts the spawned
                // transform's Y while LocationInstance.m_position stays at
                // the RegisterLocation Y, producing distinct
                // MapPinIdentify keys that only Map.GetClosestPin's radius
                // dedup papers over. Spot locations stay on the
                // m_locationInstances path; #30 is scoped to dungeons.
                if (ZoneSystem.instance.m_locationInstances.Count == 0)
                {
                    var allLocations = AllLocationsLazy.Value;
                    if (allLocations != null)
                    {
                        for (var i = 0; i < allLocations.Count; i++)
                        {
                            var loc = allLocations[i];
                            if (loc == null || !loc.m_hasInterior) continue;
                            var locPos = loc.transform.position;
                            if ((origin - locPos).sqrMagnitude > locationRangeSq) continue;
                            DungeonMappingFromLocation(loc);
                        }
                    }
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
        /// Called when the module is turned on. Registry and allowlist changes
        /// made while it was off were not observed, so the classification is
        /// rebuilt from the live registries: the next <see cref="Mapping"/>
        /// sees a new classifier version and forces a fresh scan. The two
        /// handlers below are subscribed through the module's scope while it
        /// runs, and removed with it.
        /// </summary>
        internal static void InvalidateClassification()
        {
            _staticClassifierVersion++;
        }

        internal static void OnValheimObjectRegistryChanged(ValheimObject obj)
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

        internal static void OnStaticAllowlistChanged(ValheimObject registry)
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
            if (TryFindAutomaticPin(pos, CreateTarget(component.gameObject, displayName), displayName,
                    true, out var adopted))
            {
                CachePin(FloraNodesBuffer, adopted, PinKind.Flora, data.Identifier, sourceToken,
                    PinSourceDomain.Component);
                return true;
            }

            if (Map.GetClosestPin(pos,
                    includeInactive: true) != null)
                return true;

            var pinName = ComputeFloraPinName(displayName, network.NodeCount);
            var pinData = AddPin(uniqueId, pos, pinName, Config.SaveStaticObjectPins,
                CreateTarget(component.gameObject, displayName), PinKind.Flora, data.Identifier,
                sourceToken, PinSourceDomain.Component);
            if (pinData != null)
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
                if (!canonical.m_save && Map.ShouldSuppressTransientAutomaticPin(center))
                {
                    RemovePinFromCache(canonical);
                    Map.RemovePin(canonical);
                    OwnedFloraPinsScratch.Clear();
                    return;
                }

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
            else if (Map.GetClosestPin(center,
                         includeInactive: true) == null)
            {
                var newPin = AddStaticPin(center, pinName, Config.SaveStaticObjectPins,
                    CreateTarget(component.gameObject, displayName));
                if (newPin != null)
                    CachePin(FloraNodesBuffer, newPin, PinKind.Flora, identifier, sourceToken,
                        PinSourceDomain.Component);
            }

            OwnedFloraPinsScratch.Clear();
        }

        internal static string ComputeFloraPinName(string displayName, int nodeCount)
        {
            return nodeCount > 1
                ? Automatics.L10N.LocalizeTextOnly(
                    "@text_automatic_mapping_flora_cluster_pin_name", displayName, nodeCount)
                : displayName;
        }

        private static bool MineralMapping(Component component, string name)
        {
            var sourceToken = name;
            if (!GetMineral(sourceToken, out var data)) return false;
            if (!data.IsAllowed) return true;
            if (!Objects.GetZdoid(component, out var uniqueId)) return true;
            var identify = new MapPinIdentify(uniqueId);

            // Short-circuit cached minerals before the MineRock5 health-package
            // decode in TryGetMineralPosition and the GetName rename, neither of
            // which the cached branch uses.
            if (TryGetCachedPin(identify, out _))
            {
                MarkSeen(identify);
                return true;
            }

            if (ValheimObject.Mineral.GetName(data.Identifier, out var label))
                name = label;

            Vector3 pos;
            float maxHeight;
            if (!TryGetMineralPosition(component, out pos, out maxHeight)) return true;

            if (TryFindAutomaticPin(pos, CreateTarget(component.gameObject, name), name, false,
                    out var adopted))
            {
                CachePin(identify, adopted, PinKind.Mineral, data.Identifier, sourceToken,
                    PinSourceDomain.Component);
                return true;
            }

            if (Map.GetClosestPin(pos,
                    includeInactive: true) != null)
                return true;

            if (Config.NeedToEquipWishboneForUndergroundMinerals)
                if (maxHeight < ZoneSystem.instance.GetGroundHeight(pos))
                {
                    var items = Player.m_localPlayer.GetInventory().GetEquippedItems();
                    var hasWishbone = false;
                    for (var i = 0; i < items.Count; i++)
                        if (items[i].m_shared.m_name == "$item_wishbone")
                        {
                            hasWishbone = true;
                            break;
                        }

                    if (!hasWishbone) return true;
                }

            AddPin(uniqueId, pos, name, CreateTarget(component.gameObject, name), PinKind.Mineral,
                data.Identifier, sourceToken, PinSourceDomain.Component);
            return true;
        }

        // MineRock5 reads Awake-time bounds plus current per-area health so
        // partially mined rocks stay pinned while dead child colliders are
        // ignored. Other mineral shapes keep live-bounds aggregation since
        // their colliders stay active for the object's lifetime.
        private static bool TryGetMineralPosition(Component component, out Vector3 position,
            out float maxHeight)
        {
            position = Vector3.zero;
            maxHeight = float.MinValue;

            if (component is MineRock5 rock5)
                return MineRock5Cache.TryGetLivePosition(rock5, out position, out maxHeight);

            if (component is MineRock rock)
                return TryGetMineRockPosition(rock, out position, out maxHeight);

            var colliders = GetMineralColliders(component);
            var count = 0;
            var sum = Vector3.zero;
            for (var i = 0; i < colliders.Length; i++)
                AddColliderBounds(colliders[i], ref sum, ref maxHeight, ref count);

            // count > 0 already proves at least one valid collider was aggregated; a zero
            // sum is a legitimate centroid (e.g. a node centered on the world origin).
            if (count == 0) return false;

            position = sum / count;
            return true;
        }

        private static bool TryGetMineRockPosition(MineRock rock, out Vector3 position,
            out float maxHeight)
        {
            position = Vector3.zero;
            maxHeight = float.MinValue;

            var colliders = Reflections.GetField<Collider[]>(rock, "m_hitAreas");
            if (colliders == null) return false;
            if (!Objects.GetZNetView(rock, out var zNetView)) return false;
            var zdo = zNetView.GetZDO();
            if (zdo == null) return false;

            var count = 0;
            var sum = Vector3.zero;
            for (var i = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (!collider) continue;
                if (zdo.GetFloat("Health" + i, rock.m_health) <= 0f) continue;
                AddColliderBounds(collider, ref sum, ref maxHeight, ref count);
            }

            // count > 0 already proves at least one valid collider was aggregated; a zero
            // sum is a legitimate centroid (e.g. a node centered on the world origin).
            if (count == 0) return false;

            position = sum / count;
            return true;
        }

        private static Collider[] GetMineralColliders(Component component)
        {
            var empty = Array.Empty<Collider>();
            switch (component)
            {
                case Destructible destructible:
                {
                    var collider = destructible.GetComponentInChildren<Collider>();
                    return !collider ? empty : new[] { collider };
                }
                default:
                    return empty;
            }
        }

        private static void AddColliderBounds(Collider collider, ref Vector3 sum,
            ref float maxHeight, ref int count)
        {
            if (!collider) return;

            var bounds = collider.bounds;
            sum += bounds.center;
            if (bounds.max.y > maxHeight) maxHeight = bounds.max.y;
            count++;
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
            if (TryFindAutomaticPin(position, CreateTarget(component.gameObject, name), name, false,
                    out var adopted))
                CachePin(identify, adopted, PinKind.Spawner, data.Identifier, sourceToken,
                    PinSourceDomain.Component);
            else if (Map.GetClosestPin(position,
                         includeInactive: true) == null)
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
            if (TryFindAutomaticPin(position, CreateTarget(component.gameObject, name), name, false,
                    out var adopted))
                CachePin(identify, adopted, PinKind.Other, data.Identifier, sourceToken,
                    PinSourceDomain.Component);
            else if (Map.GetClosestPin(position,
                         includeInactive: true) == null)
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
            if (TryFindAutomaticPin(pos, CreateTarget(component.gameObject, name), pinName, false,
                    out var adopted))
            {
                CachePin(identify, adopted, PinKind.Portal, string.Empty, name,
                    PinSourceDomain.Component);
                return true;
            }

            AddPin(uniqueId, pos, pinName, true, CreateTarget(component.gameObject, name),
                PinKind.Portal, string.Empty, name, PinSourceDomain.Component);

            return true;
        }

        // A saved pin this module added earlier but no longer tracks - its object left the scan
        // range, or the world was reloaded or the module turned off and on - is taken back into
        // the cache instead of being skipped or pinned again, so the destroyed-object rule and
        // tag updates reach it. Only a pin Automatics recognizes as its own is taken: pins the
        // player placed or saved by clicking, and pins shared by others, are left alone, and so
        // are pins another object or a creature still holds. An unsaved pin is removed from the
        // map when it leaves the cache, so only saved pins can be waiting here. The pin must also
        // be the object's own - the same icon and the name it would be added with (a prefix for a
        // flora cluster, whose name carries a count) - so a location or creature pin that happens
        // to lie within reach is never taken. A portal whose tag changed while it was not tracked
        // is not recognized and gets a pin of its own, as before.
        private static bool TryFindAutomaticPin(Vector3 pos, Target target, string name, bool prefix,
            out Minimap.PinData pinData)
        {
            var type = IconPack.GetPinType(target);
            var stored = Map.StoredPinName(name);
            pinData = Map.GetClosestPin(pos, 1f,
                x => x.m_save && x.m_type == type &&
                     (prefix ? x.m_name.StartsWith(stored, StringComparison.Ordinal) : x.m_name == stored) &&
                     !OwnsPin(x) && !DynamicObjectMapping.OwnsPin(x) && Map.IsAutomaticPin(x),
                includeInactive: true);
            return pinData != null;
        }

        private static bool DungeonMapping(ZoneSystem.LocationInstance instance)
        {
            return TryAddDungeonPin(
                instance.m_location.m_prefabName,
                instance.m_position,
                instance.m_location.m_exteriorRadius);
        }

        // Client-side fallback for non-host peers (dedicated-server clients,
        // P2P clients), where ZoneSystem.m_locationInstances is server-only.
        // LocationProxy spawns the underlying Location prefab on clients too,
        // so Location.s_allLocations is populated regardless of network role.
        // The caller gates this on m_locationInstances.Count == 0; SP / host
        // route every dungeon through the location-domain loop above instead.
        private static bool DungeonMappingFromLocation(Location location)
        {
            if (location == null || !location.m_hasInterior) return false;
            var prefabName = StripCloneSuffix(location.gameObject.name);
            return TryAddDungeonPin(prefabName, location.transform.position,
                location.m_exteriorRadius);
        }

        private static bool TryAddDungeonPin(string prefabName, Vector3 pos, float radius)
        {
            Teleport GetTeleport(Collider collider)
            {
                return collider.GetComponent<Teleport>();
            }

            if (!GetDungeon(prefabName, out var data)) return false;
            if (!data.IsAllowed) return true;

            var name = GetLocationName(ValheimObject.Dungeon, data.Identifier, prefabName);

            // Linear min-scan instead of OrderBy: only the nearest teleport
            // collider is consumed, so sorting the whole candidate list would
            // allocate a second list + IComparer<T> for nothing.
            var candidates = Objects.GetInsideSphere(pos, radius, GetTeleport, ColliderBuffer,
                DungeonMask);
            Collider closest = null;
            var closestDistance = float.MaxValue;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.distance >= closestDistance) continue;
                closest = candidate.collider;
                closestDistance = candidate.distance;
            }

            if (closest != null)
            {
                var entrance = closest.bounds.center;
                var identify = new MapPinIdentify(entrance);
                if (TryGetCachedPin(identify, out _))
                {
                    MarkSeen(identify);
                    return true;
                }

                if (Map.GetClosestPin(entrance,
                        includeInactive: true) == null)
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

            if (Map.GetClosestPin(pos, radius, x => x.m_name == name,
                    includeInactive: true) == null)
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

            var name = GetLocationName(ValheimObject.Spot, data.Identifier, prefabName);

            if (Map.GetClosestPin(pos,
                    includeInactive: true) == null)
                AddPin(ZDOID.None, pos, name, CreateTarget(prefabName, name), PinKind.Spot,
                    data.Identifier, prefabName, PinSourceDomain.Location);

            return true;
        }

        private static Minimap.PinData AddPin(ZDOID uniqueId, Vector3 pos, string pinName, bool save,
            Target target, PinKind kind, string identifier, string sourceToken,
            PinSourceDomain domain)
        {
            var pinData = AddStaticPin(pos, pinName, save, target);
            if (pinData == null) return null;

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

        private static Minimap.PinData AddStaticPin(Vector3 pos, string pinName, bool save,
            Target target)
        {
            return !save && Map.ShouldSuppressTransientAutomaticPin(pos)
                ? null
                : Map.AddPin(pos, pinName, save, target);
        }

        private static void AddPin(ZDOID uniqueId, Vector3 pos, string pinName, Target target,
            PinKind kind, string identifier, string sourceToken, PinSourceDomain domain)
        {
            AddPin(uniqueId, pos, pinName, Config.SaveStaticObjectPins, target, kind, identifier,
                sourceToken, domain);
        }

        internal static bool TryGetCachedPin(MapPinIdentify identify, out Minimap.PinData pinData)
        {
            if (PinDataCache.TryGetValue(identify, out var entry) && entry?.PinData != null)
            {
                pinData = entry.PinData;
                return true;
            }

            pinData = null;
            return false;
        }

        private static bool TryGetCachedPin(List<FloraNode> nodes,
            out Minimap.PinData pinData)
        {
            foreach (var node in nodes)
                if (TryGetCachedPin(new MapPinIdentify(node.UniqueId), out pinData))
                    return true;

            pinData = null;
            return false;
        }

        private static Minimap.PinData RemoveOwnedPin(MapPinIdentify identify)
        {
            if (!TryGetCachedPin(identify, out var pinData)) return null;

            RemovePinFromCache(pinData);
            return Map.RemovePin(pinData) ?? pinData;
        }

        private static Minimap.PinData RemoveOwnedPinIfSingleLiveFloraOwner(
            MapPinIdentify identify)
        {
            if (!TryGetCachedPin(identify, out var pinData)) return null;
            if (!PinKeyCache.TryGetValue(pinData, out var keys)) return RemoveOwnedPin(identify);

            var liveOwnerCount = 0;
            foreach (var key in keys)
            {
                if (!key.IsUniqueId() || FloraNode.Find(key.UniqueId) != null)
                    liveOwnerCount++;
                if (liveOwnerCount > 1) return null;
            }

            return RemoveOwnedPin(identify);
        }

        private static void CachePin(List<FloraNode> nodes, Minimap.PinData pinData,
            PinKind kind, string identifier, string sourceToken, PinSourceDomain domain)
        {
            foreach (var node in nodes)
                CachePin(new MapPinIdentify(node.UniqueId), pinData, kind, identifier, sourceToken,
                    domain);
        }

        private static void CachePin(MapPinIdentify identify, Minimap.PinData pinData,
            PinKind kind, string identifier, string sourceToken, PinSourceDomain domain)
        {
            Map.TrackAutomaticPin(pinData);

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

        public static bool OwnsPin(Minimap.PinData pinData)
        {
            return pinData != null && PinKeyCache.ContainsKey(pinData);
        }

        internal static bool TryGetPinKind(Minimap.PinData pinData, out PinKind kind)
        {
            if (PinKeyCache.TryGetValue(pinData, out var keys))
                foreach (var key in keys)
                    if (PinDataCache.TryGetValue(key, out var entry))
                    {
                        kind = entry.Kind;
                        return true;
                    }
            kind = default;
            return false;
        }

        internal static bool TryGetPinKeys(Minimap.PinData pinData, out HashSet<MapPinIdentify> keys)
        {
            return PinKeyCache.TryGetValue(pinData, out keys);
        }

        internal static GameObject FindLiveInstance(Minimap.PinData pinData)
        {
            if (pinData == null || !ZNetScene.instance ||
                !PinKeyCache.TryGetValue(pinData, out var keys) || keys.Count != 1)
                return null;

            foreach (var key in keys)
                return key.IsUniqueId() ? ZNetScene.instance.FindInstance(key.UniqueId) : null;

            return null;
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
            private enum IdentifyKind : byte
            {
                None,
                UniqueId,
                Position
            }

            // Location-domain pins are minimap-horizontal identities. Keep a rounded
            // X/Z key so a valid origin position is representable and small Y drift
            // between LocationInstance and spawned Location transforms cannot split
            // one location into multiple cache keys.
            private const float PositionKeyScale = 10f;

            private readonly IdentifyKind _kind;
            private readonly int _posXKey;
            private readonly int _posZKey;

            public readonly ZDOID UniqueId;
            public readonly Vector3 Pos;

            private MapPinIdentify(IdentifyKind kind, ZDOID uniqueId, Vector3 pos)
            {
                _kind = kind;
                UniqueId = uniqueId;
                Pos = pos;
                _posXKey = ToPositionKey(pos.x);
                _posZKey = ToPositionKey(pos.z);
            }

            public MapPinIdentify(ZDOID uniqueId) : this(IdentifyKind.UniqueId, uniqueId,
                Vector3.zero)
            {
            }

            public MapPinIdentify(Vector3 pos) : this(IdentifyKind.Position, ZDOID.None, pos)
            {
            }

            public bool IsUniqueId()
            {
                return _kind == IdentifyKind.UniqueId &&
                       UniqueId.UserID != 0 &&
                       UniqueId.ID != 0;
            }

            public bool IsPos()
            {
                return _kind == IdentifyKind.Position;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (int)_kind;
                    if (IsUniqueId())
                        hash = (hash * 397) ^ UniqueId.GetHashCode();
                    else if (IsPos())
                    {
                        hash = (hash * 397) ^ _posXKey;
                        hash = (hash * 397) ^ _posZKey;
                    }

                    return hash;
                }
            }

            public bool UniqueIdEquals(ZDOID id)
            {
                return UniqueId == id;
            }

            public bool PosEquals(Vector3 pos)
            {
                return _posXKey == ToPositionKey(pos.x) &&
                       _posZKey == ToPositionKey(pos.z);
            }

            public bool Equals(MapPinIdentify other)
            {
                if (IsUniqueId() && other.IsUniqueId()) return UniqueIdEquals(other.UniqueId);
                if (IsPos() && other.IsPos()) return PosEquals(other.Pos);
                return false;
            }

            private static int ToPositionKey(float value)
            {
                return Mathf.RoundToInt(value * PositionKeyScale);
            }
        }
    }
}
