using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.AutomaticMapping;
using Automatics.Valheim;
using HarmonyLib;
using ModUtils;
using Splatform;
using UnityEngine;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;

namespace Automatics.SmokeTest
{
    // The one rule every pin removal in this file follows: a check takes off the map only a pin it
    // created, one that was not on the map when the check took this snapshot at its start. Any
    // other pin, such as a saved pin of yours that a test object took as its own, or an automatic
    // pin the pin key saved in place, stays on the map as the check found it: the hold Automatic
    // Mapping's static cache took on it during the check is let go, the hold it had is given back,
    // and its saved and automatic state is put back.
    internal sealed class SmokePins
    {
        private static readonly System.Reflection.FieldInfo PinDataCacheField =
            AccessTools.Field(typeof(StaticObjectMapping), "PinDataCache");

        private static readonly System.Reflection.FieldInfo PinKeyCacheField =
            AccessTools.Field(typeof(StaticObjectMapping), "PinKeyCache");

        private sealed class Found
        {
            public bool Saved;
            public bool Automatic;
            public Dictionary<StaticObjectMapping.MapPinIdentify, PinCacheEntry> Hold;
        }

        private readonly Dictionary<Minimap.PinData, Found> _found = new Dictionary<Minimap.PinData, Found>();

        public SmokePins()
        {
            var cache = PinDataCacheField?.GetValue(null) as Dictionary<StaticObjectMapping.MapPinIdentify, PinCacheEntry>;
            foreach (var pin in Map.GetAllPins())
            {
                if (pin == null || _found.ContainsKey(pin)) continue;
                var found = new Found { Saved = pin.m_save, Automatic = Map.IsAutomaticPin(pin) };
                if (cache != null && StaticObjectMapping.TryGetPinKeys(pin, out var keys))
                {
                    found.Hold = new Dictionary<StaticObjectMapping.MapPinIdentify, PinCacheEntry>();
                    foreach (var key in keys)
                        if (cache.TryGetValue(key, out var entry)) found.Hold[key] = entry;
                }
                _found[pin] = found;
            }
        }

        public bool Created(Minimap.PinData pin)
        {
            return pin != null && !_found.ContainsKey(pin);
        }

        public void Remove(Minimap map, Minimap.PinData pin)
        {
            if (pin == null) return;
            if (!_found.TryGetValue(pin, out var found))
            {
                if (map) map.RemovePin(pin);
                return;
            }

            StaticObjectMapping.OnRemovePin(pin);
            pin.m_save = found.Saved;
            if (found.Automatic) Map.TrackAutomaticPin(pin);
            else Map.UntrackAutomaticPin(pin);
            var cache = PinDataCacheField?.GetValue(null) as Dictionary<StaticObjectMapping.MapPinIdentify, PinCacheEntry>;
            var keys = PinKeyCacheField?.GetValue(null) as Dictionary<Minimap.PinData, HashSet<StaticObjectMapping.MapPinIdentify>>;
            if (found.Hold == null || cache == null || keys == null) return;
            var held = new HashSet<StaticObjectMapping.MapPinIdentify>();
            foreach (var pair in found.Hold)
                if (!cache.ContainsKey(pair.Key))
                {
                    cache[pair.Key] = pair.Value;
                    held.Add(pair.Key);
                }
            if (held.Count > 0) keys[pin] = held;
        }
    }

    // Runs synchronously once its first frame has passed: no map save, item update or player
    // input can occur between adding a test pin and removing it. The section waits that one
    // frame only after spawning the deposits of the destroyed-object checks, before any test pin
    // exists. The drop also has the session's usual cleanup.
    internal static class PinFeatureSmoke
    {
        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            var map = Minimap.instance;
            if (!run.IsCurrent || !player || !map || !GameCamera.instance || !ZNetScene.instance ||
                Localization.instance == null || !ZNet.instance || !ZNet.instance.IsServer() ||
                ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("pin_section", "requires a loaded single-player world, minimap and camera");
                yield break;
            }
            if (!MappingModule.Running)
            {
                run.Skip("pin_section", "Automatic Mapping is not running; enable the module first");
                yield break;
            }

            var sectionPins = new SmokePins();
            var created = new List<Minimap.PinData>();
            var deposits = new List<TestDeposit>();
            Func<string, bool> cleanup = _ =>
            {
                if (deposits.Count > 0) RemoveDeposits(map, sectionPins, deposits);
                foreach (var pin in created) sectionPins.Remove(map, pin);
                return true;
            };
            SmokeSession.TrackHandBack(cleanup);
            ItemDrop drop = null;
            GameObject piece = null;
            try
            {
                if (SpawnDestroyedObjectDeposits(run, player, deposits))
                {
                    // A tin deposit reads its hit areas in Start, which runs before its first
                    // frame. No test pin exists yet, and the deposits' colliders stay off until the
                    // section's own pass, so no scan of the game's pins them meanwhile and no save
                    // can keep such a pin.
                    yield return null;
                    if (!run.IsCurrent || !player || !map || !GameCamera.instance || !ZNetScene.instance ||
                        !MappingModule.Running)
                    {
                        const string gone =
                            "the world, the camera or Automatic Mapping went away while the deposits settled";
                        SkipDestroyedObjectChecks(run, gone);
                        run.Skip("pin_section", gone);
                        yield break;
                    }
                    CheckDestroyedObjectPins(run, player, map, deposits, created);
                    RemoveDeposits(map, sectionPins, deposits);
                }

                Minimap.PinData objectPin = null;
                var camera = GameCamera.instance.transform;
                var prefab = ZNetScene.instance.GetPrefab("Wood");
                if (!prefab || !prefab.GetComponent<ItemDrop>())
                    run.Skip("pin_object_saved", "the Wood prefab is unavailable");
                else
                {
                    drop = FeaturesSmokeCommand.SpawnTestDrop(prefab,
                        camera.position + camera.forward * 3f + camera.right * 2f, false);
                    if (!drop || !Objects.GetZNetView(drop, out var nview) ||
                        nview.GetZDO() == null || nview.GetZDO().Persistent)
                        run.Skip("pin_object_saved", "could not prepare a non-persistent Wood drop");
                    else
                    {
                        Physics.SyncTransforms();
                        objectPin = CheckObject(run, player, drop, created);
                    }
                }

                CheckHidden(run, objectPin);

                piece = SpawnTestPiece(camera.position + camera.forward * 4f - camera.right * 2f,
                    camera.forward, true, out var pieceSkip);
                if (!piece)
                    run.Skip("pin_piece_name", pieceSkip);
                else
                {
                    Physics.SyncTransforms();
                    CheckPiece(run, player, piece, created);
                }

                CheckDetachedPiece(run, player, piece, created);
                var navigationChecked = false;
                CheckLocation(run, player, created, false, ref navigationChecked);
                CheckLocation(run, player, created, true, ref navigationChecked);
                if (!navigationChecked)
                    run.Skip("navhotkey_location", "no eligible loaded location ray was available in the location pin checks");
                CheckObjectsInLocation(run, player, created);
                CheckInterior(run, player, created);
                CheckGround(run, player, created);
                CheckMiss(run, player, created);
                CheckSavedPinRecognition(run, player, map, created);
                CheckUncachedAutomaticPin(run, player, map, created);
                CheckLoadingAreaKeepsPin(run, player, map, created);
                CheckMinedDeposit(run, player, map, created);
                CheckDepositsSharingPin(run, player, map, created);
                CheckLoadedLocationSpot(run, player, map, created);
                CheckDungeonWaitsForLocation(run, player, map, created);
                CheckStaticClassificationFollowsObject(run, player);
                CheckFloraPatchGrouping(run, player);
                CheckRuntimeFloraPinsLoadedPlant(run, player, map, created);
            }
            finally
            {
                // Only pins added during our synchronous action calls, and the deposits' pins, are
                // in this list.
                try
                {
                    if (cleanup("pin feature smoke")) SmokeSession.ReleaseHandBack(cleanup);
                }
                finally
                {
                    try
                    {
                        FeaturesSmokeCommand.RemoveTestDrop(drop, "pin feature smoke");
                    }
                    finally
                    {
                        if (piece && SmokeSession.DestroyTestObject(piece, 0, "pin feature smoke"))
                            SmokeSession.ReleaseSpawn(piece);
                    }
                }
            }
            if (created.All(pin => !Map.GetAllPins().Contains(pin)))
                run.Pass("pin_cleanup", $"removed {created.Count} test pin(s)");
            else
                run.Fail("pin_cleanup", "a test pin remains on the map");
        }

        private static Minimap.PinData CheckObject(FeatureRun run, Player player, ItemDrop drop,
            List<Minimap.PinData> created)
        {
            var collider = drop.GetComponentsInChildren<Collider>().FirstOrDefault(x => x.enabled);
            if (!collider)
            {
                run.Skip("pin_object_saved", "the test drop has no enabled collider");
                run.Skip("pin_duplicate", "no object ray was available");
                return null;
            }
            var origin = GameCamera.instance.transform.position;
            var ray = new Ray(origin, collider.bounds.center - origin);
            if (!FirstHit(player, ray, out var hit) ||
                !hit.collider.transform.IsChildOf(drop.transform))
            {
                run.Skip("pin_object_saved", "the camera ray to the test drop is obstructed");
                run.Skip("pin_duplicate", "no unobstructed object ray was available");
                return null;
            }
            var internalName = Objects.GetName(drop);
            var name = Localization.instance.Localize(internalName);
            if (HasManualPin(drop.transform.position, name))
            {
                run.Skip("pin_object_saved", "an existing saved Wood pin is within 2 m; move and retry");
                run.Skip("pin_duplicate", "the object already has a saved pin");
                return null;
            }
            var expectedType = IconPack.GetPinType(new Target
            {
                name = internalName,
                prefabName = Objects.GetPrefabName(drop.gameObject)
            });
            var count = Map.GetAllPins().Count;
            var added = Invoke(player, ray, created);
            if (added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                !added.m_author.IsValid && added.m_ownerID == 0 && added.m_name == name &&
                !name.Contains("$") && added.m_type == expectedType &&
                Vector3.Distance(added.m_pos, drop.transform.position) < 0.01f &&
                !Map.IsAutomaticPin(added) && !Map.ShouldHideAutomaticPin(added))
                run.Pass("pin_object_saved", $"saved unauthored '{name}', type={expectedType}; not automatic or hidden");
            else
                run.Fail("pin_object_saved", "the object ray did not create exactly one ordinary saved pin with its localized name and icon");

            if (added == null)
            {
                run.Skip("pin_duplicate", "the first ray created no pin");
                return null;
            }
            // In the same frame, so the new pin has no live minimap marker yet.
            count = Map.GetAllPins().Count;
            if (Invoke(player, ray, created) == null && Map.GetAllPins().Count == count)
                run.Pass("pin_duplicate");
            else
                run.Fail("pin_duplicate", "the second ray added a pin");
            return added;
        }

        // Hiding unexplored pins applies to automatic pins only. The setting is turned on for
        // this check alone, through the session, and put back before it returns.
        private static void CheckHidden(FeatureRun run, Minimap.PinData pin)
        {
            if (pin == null)
            {
                run.Skip("pin_not_hidden", "no manual object pin was created");
                return;
            }
            if (!MappingConfig.EnableAutomaticMapping)
            {
                run.Skip("pin_not_hidden", "enable_automatic_mapping is off, so no pin is ever hidden");
                return;
            }
            var entry = MappingConfig.HideUnexploredAutomaticMappingPinsEntry;
            if (entry == null)
            {
                run.Fail("pin_not_hidden", "the hide_unexplored_automatic_mapping_pins entry is not bound");
                return;
            }

            try
            {
                if (!entry.Value)
                {
                    if (!SmokeSession.BeginSetting(run.RunId, entry, out var setting, out var refusal) ||
                        !SmokeSession.WriteSetting(run.RunId, setting, true, out refusal))
                    {
                        run.Skip("pin_not_hidden", $"{entry.Definition.Key} could not be turned on: {refusal}");
                        return;
                    }
                }

                if (!MappingConfig.HideUnexploredAutomaticMappingPins)
                    run.Skip("pin_not_hidden", "hiding unexplored automatic pins did not take effect");
                else if (!Map.ShouldHideAutomaticPin(pin))
                    run.Pass("pin_not_hidden", "with hide_unexplored_automatic_mapping_pins on");
                else
                    run.Fail("pin_not_hidden", "the manual pin is hidden like an automatic pin");
            }
            finally
            {
                var results = SmokeSession.RestoreSettings(run.RunId);
                if (results.Count > 0)
                {
                    var detail = string.Join("; ", results.Select(x => x.Value));
                    if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                        run.Fail("pin_settings_restored", detail);
                    else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                              x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                        run.Skip("pin_settings_restored", "restoration skipped: " + detail);
                    else
                        run.Pass("pin_settings_restored", detail);
                }
            }
        }

        // A building piece: its WearNTear has no name, so the pin takes the Piece's.
        private static void CheckPiece(FeatureRun run, Player player, GameObject go,
            List<Minimap.PinData> created)
        {
            var piece = go.GetComponent<Piece>();
            var collider = go.GetComponentsInChildren<Collider>()
                .FirstOrDefault(x => x.enabled && !x.isTrigger);
            if (!piece || !collider)
            {
                run.Skip("pin_piece_name", "the test piece has no Piece or no enabled collider");
                return;
            }
            var origin = GameCamera.instance.transform.position;
            var ray = new Ray(origin, collider.bounds.center - origin);
            if (!FirstHit(player, ray, out var hit) || !hit.collider.transform.IsChildOf(go.transform))
            {
                run.Skip("pin_piece_name", "the camera ray to the test piece is obstructed");
                return;
            }
            var prefabName = Objects.GetPrefabName(go);
            var name = Localization.instance.Localize(piece.m_name);
            if (HasManualPin(go.transform.position, name))
            {
                run.Skip("pin_piece_name", "an existing saved pin of that piece is within 2 m; move and retry");
                return;
            }
            var expectedType = IconPack.GetPinType(new Target { name = piece.m_name, prefabName = prefabName });
            var count = Map.GetAllPins().Count;
            var added = Invoke(player, ray, created);
            if (added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                piece.IsPlacedByPlayer() && !string.IsNullOrEmpty(name) && added.m_name == name && name != prefabName &&
                !name.Contains("$") && added.m_type == expectedType &&
                Vector3.Distance(added.m_pos, go.transform.position) < 0.01f &&
                !Map.IsAutomaticPin(added))
                run.Pass("pin_piece_name", $"'{name}' (prefab {prefabName}), type={expectedType}");
            else
                run.Fail("pin_piece_name",
                    $"expected one saved pin named '{name}' with type {expectedType}, got '{added?.m_name}' (prefab {prefabName})");
        }

        // A building piece kept out of every world save and recorded with the session, so a run
        // cut short still has it removed. Vanilla Piece.SetCreator writes only while the creator
        // is 0, so a piece that must stay world-generated is spawned with playerCreator false and
        // never given one. Returns null with the reason when none could be set up.
        private static GameObject SpawnTestPiece(Vector3 position, Vector3 forward, bool playerCreator,
            out string skip)
        {
            var facing = Vector3.ProjectOnPlane(-forward, Vector3.up);
            var rotation = facing.sqrMagnitude > 0.001f ? Quaternion.LookRotation(facing) : Quaternion.identity;
            var go = SpawnTestObject(new[] { "woodwall", "piece_workbench" }, position, rotation, out skip,
                prefab => prefab.GetComponent<Piece>());
            if (!go) return null;
            var piece = go.GetComponent<Piece>();
            if (playerCreator) piece.SetCreator(Player.m_localPlayer.GetPlayerID(), default);
            if (piece.IsPlacedByPlayer() == playerCreator) return go;
            skip = playerCreator
                ? "the test piece did not take the player as its creator"
                : "the test piece has a creator although none was set";
            RemoveTestObject(go);
            return null;
        }

        // Spawns the first available prefab non-persistent and records it with the session, so a
        // run cut short still has it removed. Returns null with the reason when none could be set up.
        private static GameObject SpawnTestObject(string[] prefabNames, Vector3 position, Quaternion rotation,
            out string skip, Func<GameObject, bool> accept = null)
        {
            skip = $"none of {string.Join(", ", prefabNames)} is available";
            foreach (var prefabName in prefabNames)
            {
                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (!prefab || (accept != null && !accept(prefab))) continue;

                var go = UnityEngine.Object.Instantiate(prefab, position, rotation);
                SmokeSession.TrackSpawn(go);
                if (!Objects.GetZNetView(go.transform, out var nview) || nview == null ||
                    !nview.IsValid() || nview.GetZDO() == null)
                {
                    skip = $"the spawned {prefabName} has no ZDO";
                    RemoveTestObject(go);
                    return null;
                }
                if (!nview.IsOwner()) nview.ClaimOwnership();
                nview.GetZDO().Persistent = false;
                return go;
            }
            return null;
        }

        // Deactivated first: ZNetScene destroys the GameObject only at the end of the frame, and
        // its colliders must not block the rays of the checks that follow.
        private static void RemoveTestObject(GameObject go)
        {
            if (!go) return;
            go.SetActive(false);
            Physics.SyncTransforms();
            if (SmokeSession.DestroyTestObject(go, 0, "pin feature smoke")) SmokeSession.ReleaseSpawn(go);
        }

        // Loaded early-content locations whose recognized name the action would show. The explicit
        // allow list also keeps the output free of later content names.
        private static readonly string[] EarlyLocations =
        {
            "BurialChambers", "TrollCave", "SunkenCrypts", "MountainCave",
            "InfestedTree", "FireHole", "DrakeNest", "GoblinCamp", "TarPit"
        };

        private sealed class KnownLocation
        {
            public Location Location;
            public string Prefab;
            public string Label;
            public string Name;
            public Vector3 Position => Location.transform.position;
        }

        private static KnownLocation Recognize(Location location)
        {
            if (!location || !location.gameObject.activeInHierarchy) return null;
            var prefab = Objects.GetPrefabName(location.gameObject);
            var registry = ValheimObject.Dungeon;
            if (!registry.GetIdentify(prefab, out var identifier))
            {
                registry = ValheimObject.Spot;
                if (!registry.GetIdentify(prefab, out identifier)) return null;
            }
            if (!EarlyLocations.Contains(identifier) || !registry.GetName(identifier, out var label) ||
                string.IsNullOrEmpty(label))
                return null;
            var name = Automatics.L10N.TranslateInternalName(label);
            var missing = label[0] == '@' ? "automatics_" + label.Substring(1) :
                label[0] == '$' ? label.Substring(1) : null;
            if (string.IsNullOrWhiteSpace(name) || name == missing || name.Contains("$") || name.Contains("@"))
                return null;
            return new KnownLocation { Location = location, Prefab = prefab, Label = label, Name = name };
        }

        // Exterior locations within pin range with a recognized name and no manual pin of it yet.
        private static List<KnownLocation> ExteriorLocations(Vector3 origin)
        {
            var result = new List<KnownLocation>();
            var locations = StaticObjectMapping.LoadedLocations;
            if (locations == null) return result;
            foreach (var location in locations)
            {
                var known = Recognize(location);
                if (known == null || location.m_exteriorRadius <= 0f || Character.InInterior(known.Position) ||
                    Vector3.Distance(origin, known.Position) > MappingConfig.PinObjectRange ||
                    HasManualPin(known.Position, known.Name))
                    continue;
                result.Add(known);
            }
            return result;
        }

        // How many loaded location areas strictly contain the point, as Location.IsInside tests.
        private static int ContainingAreas(Vector3 point)
        {
            var locations = StaticObjectMapping.LoadedLocations;
            return locations == null ? 0 : locations.Count(location => location &&
                location.gameObject.activeInHierarchy &&
                Utils.DistanceXZ(point, location.transform.position) < location.m_exteriorRadius);
        }

        // The oracle asserts a location only when it is the one area containing the point, so
        // overlapping areas (including locations outside the allow list) never decide a result.
        private static bool OnlyInside(Vector3 point, Location location)
        {
            return !Character.InInterior(point) &&
                   Utils.DistanceXZ(point, location.transform.position) < location.m_exteriorRadius &&
                   ContainingAreas(point) == 1;
        }

        // Moves a test object to spots inside the location's area until a camera ray's first hit
        // is the object at a point only that area contains.
        private static bool TryArrange(Player player, GameObject go, KnownLocation location, out Ray ray)
        {
            ray = default(Ray);
            var origin = GameCamera.instance.transform.position;
            var center = location.Position;
            var spots = new List<Vector3> { center + Vector3.up * 3f };
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Mathf.PI / 4f;
                var spot = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) *
                    location.Location.m_exteriorRadius * 0.4f;
                spot.y = ZoneSystem.instance.GetGroundHeight(spot) + 1f;
                spots.Add(spot);
            }
            foreach (var spot in spots)
            {
                go.transform.position = spot;
                Physics.SyncTransforms();
                foreach (var collider in go.GetComponentsInChildren<Collider>()
                             .Where(x => x.enabled && !x.isTrigger && x.gameObject.activeInHierarchy).Take(3))
                {
                    var candidate = new Ray(origin, collider.bounds.center - origin);
                    if (!FirstHit(player, candidate, out var hit) || !hit.collider.transform.IsChildOf(go.transform) ||
                        !OnlyInside(hit.point, location.Location))
                        continue;
                    ray = candidate;
                    return true;
                }
            }
            return false;
        }

        // Uses loaded locations without modifying them: location prefabs can alter terrain. The
        // world-generated piece is spawned here without a creator and never given one; the
        // player's piece is the one CheckPiece used, whose creator SpawnTestPiece set.
        private static void CheckDetachedPiece(FeatureRun run, Player player, GameObject playerPiece,
            List<Minimap.PinData> created)
        {
            const string check = "pin_location_detached_piece";
            var smokePins = new SmokePins();
            var camera = GameCamera.instance.transform;
            var locations = ExteriorLocations(camera.position);
            var worldPiece = SpawnTestPiece(camera.position + camera.forward * 4f + camera.up * 3f,
                camera.forward, false, out var skip);
            KnownLocation found = null;
            try
            {
                if (!worldPiece)
                    run.Skip(check, skip);
                else
                {
                    var piece = worldPiece.GetComponent<Piece>();
                    foreach (var location in locations)
                    {
                        if (!TryArrange(player, worldPiece, location, out var ray)) continue;
                        found = location;
                        var kept = ExpectLocationPinKept(location, out var keptDetail);
                        var count = Map.GetAllPins().Count;
                        var added = Invoke(player, ray, created);
                        var expectedType = IconPack.GetPinType(new Target
                        {
                            name = location.Label, prefabName = location.Prefab
                        });
                        if (!piece.IsPlacedByPlayer() && !worldPiece.GetComponentInParent<Location>() &&
                            (kept != null
                                ? kept(added, count)
                                : added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                                  added.m_name == location.Name && added.m_type == expectedType &&
                                  !Map.IsAutomaticPin(added) &&
                                  Vector3.Distance(added.m_pos, location.Position) < 0.01f))
                            run.Pass(check, kept != null
                                ? "a detached piece with creator 0 resolved the containing location: " + keptDetail
                                : "a detached piece with creator 0 saved the containing location's name, icon and position");
                        else
                            run.Fail(check, "the detached world-generated piece did not save exactly one location pin");
                        // A pin the key added goes; a location pin it saved in place is put back.
                        smokePins.Remove(Minimap.instance, added);
                        break;
                    }
                    if (found == null)
                        run.Skip(check, "no loaded early-content location area within range has an unobstructed test-piece ray at a point only it contains and no existing manual pin; stand near a dungeon entrance or tar pit and retry; location prefabs are not spawned because they can alter terrain or entities");
                }
            }
            finally
            {
                RemoveTestObject(worldPiece);
            }

            CheckPlayerPiece(run, player, playerPiece, found ?? locations.FirstOrDefault(), created);
        }

        private static void CheckPlayerPiece(FeatureRun run, Player player, GameObject go,
            KnownLocation location, List<Minimap.PinData> created)
        {
            const string check = "pin_location_player_piece";
            var smokePins = new SmokePins();
            var piece = go ? go.GetComponent<Piece>() : null;
            if (!piece || !piece.IsPlacedByPlayer())
            {
                run.Skip(check, "no test piece with a player creator is available");
                return;
            }
            if (location == null)
            {
                run.Skip(check, "no suitable loaded early-content location area is within range");
                return;
            }
            var originalPosition = go.transform.position;
            try
            {
                if (!TryArrange(player, go, location, out var ray))
                {
                    run.Skip(check, "no unobstructed ray to the player's test piece at a point only the location's area contains");
                    return;
                }
                var pieceName = Localization.instance.Localize(piece.m_name);
                if (HasManualPin(go.transform.position, pieceName))
                {
                    run.Skip(check, "an existing saved pin of that piece is within 2 m of the test spot");
                    return;
                }
                var count = Map.GetAllPins().Count;
                var added = Invoke(player, ray, created);
                var expectedType = IconPack.GetPinType(new Target
                {
                    name = piece.m_name, prefabName = Objects.GetPrefabName(go)
                });
                if (piece.IsPlacedByPlayer() && added != null && Map.GetAllPins().Count == count + 1 &&
                    added.m_save && added.m_name == pieceName && added.m_name != location.Name &&
                    added.m_type == expectedType && !Map.IsAutomaticPin(added) &&
                    Vector3.Distance(added.m_pos, go.transform.position) < 0.01f)
                    run.Pass(check, "a piece with a player creator inside the location's area saved the piece itself");
                else
                    run.Fail(check, "the player-built piece inside the location did not save its own pin");
                smokePins.Remove(Minimap.instance, added);
            }
            finally
            {
                go.transform.position = originalPosition;
                Physics.SyncTransforms();
            }
        }

        // A tree, a deposit and a creature inside a location's area pin as themselves. Each is
        // spawned non-persistent and removed before the next is spawned.
        private static void CheckObjectsInLocation(FeatureRun run, Player player, List<Minimap.PinData> created)
        {
            var smokePins = new SmokePins();
            var checks = new[]
            {
                new KeyValuePair<string, string[]>("pin_location_tree", new[] { "Beech1", "Birch1", "Oak1" }),
                new KeyValuePair<string, string[]>("pin_location_deposit", new[] { "MineRock_Tin", "rock4_copper" }),
                new KeyValuePair<string, string[]>("pin_location_creature", new[] { "Deer", "Boar" })
            };
            var camera = GameCamera.instance.transform;
            var locations = ExteriorLocations(camera.position);
            foreach (var entry in checks)
            {
                var check = entry.Key;
                if (locations.Count == 0)
                {
                    run.Skip(check, "no loaded early-content location area without a manual pin is within range; stand near a dungeon entrance or tar pit and retry");
                    continue;
                }
                var go = SpawnTestObject(entry.Value, camera.position + camera.forward * 4f + camera.up * 3f,
                    Quaternion.identity, out var skip);
                try
                {
                    if (!go)
                    {
                        run.Skip(check, skip);
                        continue;
                    }
                    KnownLocation found = null;
                    var ray = default(Ray);
                    foreach (var location in locations)
                    {
                        if (!TryArrange(player, go, location, out ray)) continue;
                        found = location;
                        break;
                    }
                    if (found == null)
                    {
                        run.Skip(check, "no unobstructed ray to the test object at a point only one location area contains");
                        continue;
                    }
                    if (Map.GetClosestPin(go.transform.position, 2f, pin => pin.m_save && !Map.IsAutomaticPin(pin),
                            includeInactive: true) != null)
                    {
                        run.Skip(check, "an existing saved pin is within 2 m of the test spot");
                        continue;
                    }
                    var count = Map.GetAllPins().Count;
                    var added = Invoke(player, ray, created);
                    if (added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                        added.m_name != found.Name && !Map.IsAutomaticPin(added) &&
                        Vector3.Distance(added.m_pos, go.transform.position) < 0.01f)
                        run.Pass(check, $"{Objects.GetPrefabName(go)} inside the location's area saved its own pin '{added.m_name}'");
                    else
                        run.Fail(check, $"{Objects.GetPrefabName(go)} inside the location's area did not save its own pin");
                    smokePins.Remove(Minimap.instance, added);
                }
                finally
                {
                    RemoveTestObject(go);
                }
            }
        }

        // Interior rooms are 5000 m above their zone, beyond pin range from outdoors, so this
        // check runs only when the camera is already inside a loaded dungeon interior.
        private static void CheckInterior(FeatureRun run, Player player, List<Minimap.PinData> created)
        {
            const string check = "pin_location_interior";
            var smokePins = new SmokePins();
            var origin = GameCamera.instance.transform.position;
            if (!Character.InInterior(origin))
            {
                run.Skip(check, "the camera is not inside a dungeon interior, which is out of pin range from outdoors; enter an early-content dungeon and retry");
                return;
            }
            var directions = new List<Vector3> { GameCamera.instance.transform.forward, Vector3.down };
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Mathf.PI / 4f;
                directions.Add(new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)));
            }
            foreach (var direction in directions)
            {
                var ray = new Ray(origin, direction);
                if (!FirstHit(player, ray, out var hit) || !Character.InInterior(hit.point) ||
                    !IsLocationSurface(hit.collider))
                    continue;
                var location = Recognize(Location.GetZoneLocation(hit.point));
                if (location == null || HasManualPin(location.Position, location.Name)) continue;
                var parent = hit.collider.GetComponentInParent<Location>();
                if (parent && parent != location.Location) continue;

                var kept = ExpectLocationPinKept(location, out var keptDetail);
                var count = Map.GetAllPins().Count;
                var added = Invoke(player, ray, created);
                var expectedType = IconPack.GetPinType(new Target
                {
                    name = location.Label, prefabName = location.Prefab
                });
                if (kept != null
                        ? kept(added, count)
                        : added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                          added.m_name == location.Name && added.m_type == expectedType && !Map.IsAutomaticPin(added) &&
                          Vector3.Distance(added.m_pos, location.Position) < 0.01f)
                    run.Pass(check, kept != null
                        ? "an interior surface resolved its zone location: " + keptDetail
                        : "an interior surface saved its zone location's name, icon and entrance position");
                else
                    run.Fail(check, "the interior ray did not save exactly one pin for its zone location");
                // A pin the key added goes; a location pin it saved in place is put back.
                smokePins.Remove(Minimap.instance, added);
                return;
            }
            run.Skip(check, "no interior surface of a recognized early-content dungeon without an existing manual pin is in view; face a wall and retry");
        }

        // A hit the action resolves as location structure rather than as an object of its own.
        private static bool IsLocationSurface(Collider collider)
        {
            var piece = collider.GetComponentInParent<Piece>();
            if (collider.isTrigger || (piece && piece.IsPlacedByPlayer()) ||
                collider.GetComponentInParent<Character>() || collider.GetComponentInParent<MineRock>() ||
                collider.GetComponentInParent<MineRock5>() || collider.GetComponentInParent<ItemDrop>() ||
                collider.GetComponentInParent<Pickable>() || collider.GetComponentInParent<TreeBase>() ||
                collider.GetComponentInParent<TreeLog>())
                return false;
            return piece || (collider.GetComponentInParent<IDestructible>() == null &&
                             collider.GetComponentInParent<Interactable>() == null &&
                             collider.GetComponentInParent<Hoverable>() == null);
        }

        private static bool HasManualPin(Vector3 position, string name)
        {
            // Include pins without a live minimap marker, as the action does.
            return Map.GetClosestPin(position, 2f,
                pin => pin.m_save && !Map.IsAutomaticPin(pin) && Localization.instance.Localize(pin.m_name) == name,
                includeInactive: true) != null;
        }

        // A location's automatic pin, found as the action finds it, is kept instead of joined by a
        // new pin: an unsaved one comes back saved in place and no longer automatic, and a saved
        // one makes the press add nothing. Null when the location has no such pin, so the press
        // adds a pin of its own.
        private static Func<Minimap.PinData, int, bool> ExpectLocationPinKept(KnownLocation location,
            out string detail)
        {
            var stored = Map.StoredPinName(location.Label);
            var pin = Map.GetClosestPin(location.Position, location.Location.m_exteriorRadius,
                x => x.m_ownerID == 0L && Map.IsAutomaticPin(x) &&
                     (x.m_name == stored || x.m_name == location.Name),
                includeInactive: true);
            detail = null;
            if (pin == null) return null;
            var saved = pin.m_save;
            var name = pin.m_name;
            var position = pin.m_pos;
            var type = pin.m_type;
            detail = saved
                ? "its pin was already saved, so the key added nothing"
                : "the key saved its automatic pin in place";
            return (added, count) => Map.GetAllPins().Count == count && (saved
                ? added == null
                : ReferenceEquals(added, pin) && pin.m_save && !Map.IsAutomaticPin(pin) && pin.m_name == name &&
                  pin.m_pos == position && pin.m_type == type);
        }

        // Read existing locations only: spawning their prefab can generate terrain or entities.
        private static void CheckLocation(FeatureRun run, Player player,
            List<Minimap.PinData> created, bool ground, ref bool navigationChecked)
        {
            var check = ground ? "pin_location_ground" : "pin_location_wall";
            var smokePins = new SmokePins();
            var origin = GameCamera.instance.transform.position;
            var overlapping = false;
            foreach (var location in ExteriorLocations(origin))
            {
                var rays = new List<Ray>();
                if (ground)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var angle = i * Mathf.PI / 4f;
                        var point = location.Position + new Vector3(Mathf.Cos(angle), 0f,
                            Mathf.Sin(angle)) * location.Location.m_exteriorRadius * 0.5f;
                        rays.Add(new Ray(origin, point - origin));
                    }
                }
                else
                {
                    foreach (var collider in location.Location.GetComponentsInChildren<Collider>())
                        if (collider.enabled && !collider.isTrigger)
                            rays.Add(new Ray(origin, collider.bounds.center - origin));
                }
                foreach (var ray in rays)
                {
                    if (!FirstHit(player, ray, out var hit)) continue;
                    if (ground)
                    {
                        if (!hit.collider.GetComponentInParent<Heightmap>() || Character.InInterior(hit.point) ||
                            Utils.DistanceXZ(hit.point, location.Position) >= location.Location.m_exteriorRadius)
                            continue;
                        // The action takes the nearest containing area; assert only a point one area contains.
                        if (ContainingAreas(hit.point) > 1)
                        {
                            overlapping = true;
                            continue;
                        }
                    }
                    else
                    {
                        // Static structure under the Location; pieces resolve by area instead.
                        var collider = hit.collider;
                        var destructible = collider.GetComponentInParent<IDestructible>() as Component;
                        if (collider.isTrigger || collider.GetComponentInParent<Piece>() ||
                            collider.GetComponentInParent<Character>() ||
                            collider.GetComponentInParent<MineRock>() || collider.GetComponentInParent<MineRock5>() ||
                            collider.GetComponentInParent<ItemDrop>() || collider.GetComponentInParent<Pickable>() ||
                            collider.GetComponentInParent<TreeBase>() || collider.GetComponentInParent<TreeLog>() ||
                            collider.GetComponentInParent<Heightmap>() ||
                            (destructible && ValheimObject.Mineral.GetIdentify(Objects.GetName(destructible), out _)) ||
                            collider.GetComponentInParent<Location>() != location.Location) continue;
                    }
                    if (!navigationChecked)
                    {
                        CheckLocationNavigation(run, player, ray, location);
                        navigationChecked = true;
                    }
                    var kept = ExpectLocationPinKept(location, out var keptDetail);
                    var count = Map.GetAllPins().Count;
                    var added = Invoke(player, ray, created);
                    var expectedType = IconPack.GetPinType(new Target
                    {
                        name = location.Label, prefabName = location.Prefab
                    });
                    if (kept != null
                            ? kept(added, count) &&
                              Invoke(player, ray, created) == null && Map.GetAllPins().Count == count
                            : added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                              added.m_name == location.Name && added.m_type == expectedType && !Map.IsAutomaticPin(added) &&
                              Vector3.Distance(added.m_pos, location.Position) < 0.01f &&
                              Invoke(player, ray, created) == null && Map.GetAllPins().Count == count + 1)
                        run.Pass(check, kept != null
                            ? "the location ray resolved the recognized location: " + keptDetail + "; repeat adds nothing"
                            : "one saved location pin with the recognized name, icon and position; repeat adds nothing");
                    else
                        run.Fail(check, "the location ray did not add exactly one recognized location pin or allowed a duplicate");
                    // Let the other ray check exercise the same location without a test duplicate. A
                    // pin the key added goes; a location pin it saved in place is put back.
                    smokePins.Remove(Minimap.instance, added);
                    return;
                }
            }
            run.Skip(check, overlapping
                ? "every terrain point found lies inside more than one location area; move to a spot where one location's area stands alone and retry"
                : "no visible eligible loaded location without an existing manual pin; stand near a dungeon entrance or tar pit and retry; location spawning is not used");
        }

        private static void CheckLocationNavigation(FeatureRun run, Player player, Ray ray,
            KnownLocation location)
        {
            const string check = "navhotkey_location";
            StaticObjectMapping.TryGetCachedPin(
                new StaticObjectMapping.MapPinIdentify(location.Position), out var pin);
            if (pin == null)
            {
                var entrances = Physics.OverlapSphere(location.Position, location.Location.m_exteriorRadius,
                        LayerMask.GetMask("character_trigger"), QueryTriggerInteraction.Collide)
                    .Where(collider => collider.GetComponent<Teleport>())
                    .OrderBy(collider => Vector3.Distance(location.Position, collider.bounds.center));
                foreach (var entrance in entrances)
                    if (StaticObjectMapping.TryGetCachedPin(
                            new StaticObjectMapping.MapPinIdentify(entrance.bounds.center), out pin))
                        break;
            }
            if (pin == null)
            {
                run.Skip(check, "the loaded location has no cached position or entrance pin to use as an independent expectation");
                return;
            }
            var key = MappingConfig.NavigateToAimedObjectKeyEntry;
            if (!SmokeSession.BeginSetting(run.RunId, key, out var setting, out var refusal))
            {
                run.Skip(check, refusal);
                return;
            }
            var original = Navigation.CaptureTarget();
            try
            {
                if (!SmokeSession.WriteSetting(run.RunId, setting,
                        new BepInEx.Configuration.KeyboardShortcut(KeyCode.F8), out refusal))
                {
                    run.Skip(check, refusal);
                    return;
                }
                Navigation.ClearTarget();
                var count = Map.GetAllPins().Count;
                Navigation.TryNavigateFromRay(player, ray, true);
                if (ReferenceEquals(Navigation.TargetPin, pin) && Navigation.TemporaryMarker == null &&
                    Map.GetAllPins().Count == count)
                    run.Pass(check, "the existing location pin is selected by identity without adding a marker");
                else
                    run.Fail(check, "the location ray did not select its owned automatic pin without adding a marker");
            }
            finally
            {
                Navigation.RestoreTarget(original);
                var restored = SmokeSession.RestoreSettings(run.RunId);
                foreach (var result in restored)
                    if (result.Key == SmokeSession.ToggleOutcome.Failed)
                        run.Fail(check, "shortcut restoration failed: " + result.Value);
                    else if (result.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                             result.Key == SmokeSession.ToggleOutcome.NotThisRun)
                        run.Skip(check, "shortcut restoration skipped: " + result.Value);
            }
        }

        private static void CheckGround(FeatureRun run, Player player,
            List<Minimap.PinData> created)
        {
            var origin = GameCamera.instance.transform.position;
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Mathf.PI / 4f;
                var ray = new Ray(origin, new Vector3(Mathf.Cos(angle), -1f, Mathf.Sin(angle)));
                if (!FirstHit(player, ray, out var hit) || !hit.collider.GetComponentInParent<Heightmap>() ||
                    hit.collider.GetComponentInParent<IDestructible>() != null ||
                    hit.collider.GetComponentInParent<Interactable>() != null ||
                    hit.collider.GetComponentInParent<Hoverable>() != null ||
                    hit.collider.GetComponentInParent<Character>() != null) continue;
                if (HasManualPin(hit.point, "")) continue;
                if (StaticObjectMapping.LoadedLocations != null && StaticObjectMapping.LoadedLocations.Any(
                        location => location && location.gameObject.activeInHierarchy &&
                                    Utils.DistanceXZ(hit.point, location.transform.position) <= location.m_exteriorRadius))
                    continue;

                var count = Map.GetAllPins().Count;
                var added = Invoke(player, ray, created);
                if (added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                    !added.m_author.IsValid && added.m_name == "" && added.m_type == Minimap.PinType.Icon3 &&
                    Vector3.Distance(added.m_pos, hit.point) < 0.01f &&
                    !Map.IsAutomaticPin(added) && !Map.ShouldHideAutomaticPin(added))
                    run.Pass("pin_bare_ground");
                else
                    run.Fail("pin_bare_ground", "the terrain ray did not create an unnamed saved Icon3 pin at the hit point");
                return;
            }
            run.Skip("pin_bare_ground", "no bare ground without an existing unnamed pin is visible within range; stand outdoors and retry");
        }

        private static void CheckMiss(FeatureRun run, Player player,
            List<Minimap.PinData> created)
        {
            var origin = GameCamera.instance.transform.position;
            foreach (var direction in new[] { Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
            {
                var ray = new Ray(origin, direction);
                if (FirstHit(player, ray, out _)) continue;
                var count = Map.GetAllPins().Count;
                if (Invoke(player, ray, created) == null && Map.GetAllPins().Count == count)
                    run.Pass("pin_no_hit");
                else
                    run.Fail("pin_no_hit", "an empty ray added a pin");
                return;
            }
            run.Skip("pin_no_hit", "every test direction hits something within range; stand outdoors and retry");
        }

        // The game adds every saved pin back through Minimap.AddPin when it loads the map data, so
        // a pin added through it here takes the same path. Only a pin Automatics saved itself (no
        // owner, the local platform user as its author) is recognized as automatic. The other two
        // differ from it in one field each: a pin placed or saved by clicking has no author, and a
        // shared pin has an owner.
        private static void CheckSavedPinRecognition(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            const string automaticCheck = "pin_saved_automatic_recognized";
            const string manualCheck = "pin_saved_manual_not_recognized";
            const string sharedCheck = "pin_shared_not_recognized";
            var localUser = PlatformManager.DistributionPlatform?.LocalUser;
            var author = localUser != null ? localUser.PlatformUserID : PlatformUserID.None;
            if (!author.IsValid)
            {
                const string reason = "no local platform user is available to author a saved pin";
                run.Skip(automaticCheck, reason);
                run.Skip(manualCheck, reason);
                run.Skip(sharedCheck, reason);
                return;
            }

            var origin = player.transform.position;
            Minimap.PinData Add(string name, float offset, long owner, PlatformUserID pinAuthor)
            {
                var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
                try
                {
                    return map.AddPin(origin + Vector3.right * offset, Minimap.PinType.Icon3, name, true,
                        false, owner, pinAuthor);
                }
                finally
                {
                    // Include the pin even if an AddPin postfix throws after adding it.
                    created.AddRange(Map.GetAllPins().Where(pin => !before.Contains(pin)));
                }
            }

            var automatic = Add("Automatics smoke saved automatic pin", 1f, 0L, author);
            if (Map.IsAutomaticPin(automatic))
                run.Pass(automaticCheck, "a saved pin with no owner and the local author");
            else
                run.Fail(automaticCheck, "a saved pin with no owner and the local author was not recognized as automatic");

            var manual = Add("Automatics smoke saved manual pin", 2f, 0L, PlatformUserID.None);
            if (!Map.IsAutomaticPin(manual))
                run.Pass(manualCheck, "a saved pin with no owner and no author");
            else
                run.Fail(manualCheck, "a saved pin with no author was recognized as automatic");

            var shared = Add("Automatics smoke shared pin", 3f, long.MinValue, author);
            if (!Map.IsAutomaticPin(shared))
                run.Pass(sharedCheck, "a saved pin with an owner and the local author");
            else
                run.Fail(sharedCheck, "a saved pin with an owner was recognized as automatic");
        }

        // A saved automatic pin the static scan's cache does not hold - restored with the map and
        // not reached by a scan yet, or left when its object went out of range - is still that
        // object's pin, so the key adds no second saved pin beside it. Once the ray resolves to
        // the object, the action returns null without a new pin only where it reports the
        // duplicate. A Stone drop is the object: no scan caches it, and its name is not the Wood
        // of the section's earlier pins.
        private static void CheckUncachedAutomaticPin(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            const string check = "pin_saved_automatic_uncached_duplicate";
            var localUser = PlatformManager.DistributionPlatform?.LocalUser;
            var author = localUser != null ? localUser.PlatformUserID : PlatformUserID.None;
            if (!author.IsValid)
            {
                run.Skip(check, "no local platform user is available to author a saved pin");
                return;
            }
            var prefab = ZNetScene.instance.GetPrefab("Stone");
            if (!prefab || !prefab.GetComponent<ItemDrop>())
            {
                run.Skip(check, "the Stone prefab is unavailable");
                return;
            }

            var camera = GameCamera.instance.transform;
            ItemDrop drop = null;
            try
            {
                drop = FeaturesSmokeCommand.SpawnTestDrop(prefab,
                    camera.position + camera.forward * 3f - camera.right * 1f, false);
                if (!drop || !Objects.GetZNetView(drop, out var nview) ||
                    nview.GetZDO() == null || nview.GetZDO().Persistent)
                {
                    run.Skip(check, "could not prepare a non-persistent Stone drop");
                    return;
                }
                Physics.SyncTransforms();
                var collider = drop.GetComponentsInChildren<Collider>().FirstOrDefault(x => x.enabled);
                if (!collider)
                {
                    run.Skip(check, "the test drop has no enabled collider");
                    return;
                }
                var origin = camera.position;
                var ray = new Ray(origin, collider.bounds.center - origin);
                if (!FirstHit(player, ray, out var hit) || !hit.collider.transform.IsChildOf(drop.transform) ||
                    !ObjectPinning.TryResolveFromRay(player, ray, out var aimed) ||
                    aimed.Instance != drop.gameObject)
                {
                    run.Skip(check, "the camera ray to the test drop is obstructed");
                    return;
                }
                if (Map.GetClosestPin(aimed.Position, 2f,
                        pin => pin.m_save && Localization.instance.Localize(pin.m_name) == aimed.Name,
                        includeInactive: true) != null)
                {
                    run.Skip(check, "an existing saved pin of that name is within 2 m; move and retry");
                    return;
                }

                var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
                Minimap.PinData saved;
                try
                {
                    saved = map.AddPin(aimed.Position, aimed.Type, Map.StoredPinName(Objects.GetName(drop)),
                        true, false, 0L, author);
                }
                finally
                {
                    // Include the pin even if an AddPin postfix throws after adding it.
                    created.AddRange(Map.GetAllPins().Where(pin => !before.Contains(pin)));
                }
                if (!Map.IsAutomaticPin(saved) || StaticObjectMapping.OwnsPin(saved) ||
                    StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(aimed.UniqueId), out _))
                {
                    run.Fail(check, "precondition failed: the test pin is not an automatic pin outside the static cache");
                    return;
                }

                var count = Map.GetAllPins().Count;
                var added = Invoke(player, ray, created);
                if (added == null && Map.GetAllPins().Count == count &&
                    ReferenceEquals(ObjectPinning.FindStaticPin(aimed), saved))
                    run.Pass(check, $"'{aimed.Name}': the key reported the saved automatic pin as the object's pin");
                else
                    run.Fail(check, added != null
                        ? "the key added a second saved pin beside the object's saved automatic pin"
                        : "the key added no pin, but the object's lookup did not return its saved automatic pin");
            }
            finally
            {
                FeaturesSmokeCommand.RemoveTestDrop(drop, "pin feature smoke");
            }
        }

        // The static scan leaves an object whose area is still loading to a later pass, and the
        // pin the object already has must survive that pass: pruning it would take it off the map
        // while the area loads. A copper deposit is spawned just across the zone border nearest to
        // the player, inside the static range, and a static pass that starts with a fresh scan of
        // the area pins it. A second pass then runs while the game reports that zone, and only
        // that zone, as still loading. Both passes go through the entry point the minimap update
        // calls.
        private static void CheckLoadingAreaKeepsPin(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            const string check = "pin_static_loading_area_kept";
            var interval = MappingConfig.StaticObjectMappingInterval;
            var range = MappingConfig.StaticObjectMappingRange;
            if (!MappingConfig.EnableAutomaticMapping || interval <= 0f || range <= 0)
            {
                run.Skip(check, "enable_automatic_mapping is off, or static_object_mapping_interval or static_object_mapping_range is 0, so no periodic static pass runs");
                return;
            }
            if (!MappingConfig.AllowPinningMineral.Contains("CopperDeposit"))
            {
                run.Skip(check, "copper deposits are not in allow_pinning_mineral");
                return;
            }
            if (!ModuleRegistry.RuntimeToggleSupported)
            {
                run.Skip(check, "this Harmony build cannot remove patches at runtime, so the zone readiness patch is not added");
                return;
            }
            var cacheIncomplete = AccessTools.Field(typeof(StaticObjectMapping), "_cacheIncomplete");
            var lastScan = AccessTools.Field(typeof(StaticObjectMapping), "_lastCacheUpdateTime");
            var passId = AccessTools.Field(typeof(StaticObjectMapping), "_currentPassId");
            if ((bool)cacheIncomplete.GetValue(null))
            {
                run.Skip(check, "the static scan here is incomplete, so it prunes no pin; move away from dense builds and retry");
                return;
            }

            // 2 m past the zone border nearest to the player, on the ground.
            var origin = player.transform.position;
            var zone = ZoneSystem.GetZone(origin);
            var center = ZoneSystem.GetZonePos(zone);
            const float half = ZoneSystem.c_ZoneSizeHalf;
            var toBorder = new[]
            {
                new Vector3(center.x + half - origin.x, 0f, 0f), new Vector3(center.x - half - origin.x, 0f, 0f),
                new Vector3(0f, 0f, center.z + half - origin.z), new Vector3(0f, 0f, center.z - half - origin.z)
            }.OrderBy(x => x.magnitude).First();
            var spot = origin + toBorder + toBorder.normalized * 2f;
            spot.y = ZoneSystem.instance.GetGroundHeight(spot);
            var loadingZone = ZoneSystem.GetZone(spot);
            if (loadingZone == zone || Vector3.Distance(origin, spot) > range - 1f)
            {
                run.Skip(check, $"no point across a zone border is within static_object_mapping_range ({range} m); move a few metres and retry");
                return;
            }
            if (Map.GetClosestPin(spot, 8f, includeInactive: true) != null)
            {
                run.Skip(check, "a pin is within 8 m of the test spot across the nearest zone border; move and retry");
                return;
            }

            var smokePins = new SmokePins();
            var go = SpawnTestObject(new[] { "rock4_copper" }, spot, Quaternion.identity, out var skip,
                prefab => prefab.GetComponent<MineRock5>());
            Minimap.PinData pin = null;
            try
            {
                if (!go)
                {
                    run.Skip(check, skip);
                    return;
                }
                Physics.SyncTransforms();
                var deposit = go.GetComponent<MineRock5>();
                if (!Objects.GetZdoid(deposit, out var id) ||
                    ZoneSystem.GetZone(deposit.transform.position) != loadingZone)
                {
                    run.Skip(check, "the spawned deposit has no ZDO or did not land across the zone border");
                    return;
                }
                var identify = new StaticObjectMapping.MapPinIdentify(id);

                // A cleared scan time makes the first pass scan the area again, as a classifier change does.
                lastScan.SetValue(null, 0f);
                var before = (int)passId.GetValue(null);
                global::Automatics.AutomaticMapping.AutomaticMapping.Mapping(player, interval, false);
                if ((int)passId.GetValue(null) == before)
                {
                    run.Skip(check, "the first static pass did not run (the game is paused or the area around you is still loading); retry");
                    return;
                }
                if (!StaticObjectMapping.TryGetCachedPin(identify, out pin))
                {
                    run.Skip(check, "the first static pass added no pin for the deposit; the mineral category or unexplored automatic pins may be hidden, or your simulation distance does not reach the zones around it");
                    return;
                }
                if (smokePins.Created(pin)) created.Add(pin);

                _loadingZone = loadingZone;
                _loadingQueries = 0;
                var patcher = new Harmony("automatics.smoke.pin.loading." + run.RunId);
                var removal = "";
                int after;
                try
                {
                    patcher.Patch(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady)),
                        prefix: new HarmonyMethod(typeof(PinFeatureSmoke), nameof(ReportZoneLoading)));
                    before = (int)passId.GetValue(null);
                    _loadingActive = true;
                    global::Automatics.AutomaticMapping.AutomaticMapping.Mapping(player, interval, false);
                    _loadingActive = false;
                    after = (int)passId.GetValue(null);
                }
                finally
                {
                    // Off before the removal, so a patch that stays answers as the game does.
                    _loadingActive = false;
                    try
                    {
                        patcher.UnpatchSelf();
                        if (Harmony.HasAnyPatches(patcher.Id))
                            removal = "; the test patch of ZNetScene.IsAreaReady could not be removed and stays inactive until restart";
                    }
                    catch (Exception e)
                    {
                        removal = "; removing the test patch of ZNetScene.IsAreaReady failed and it stays inactive until restart: " + e.Message;
                    }
                }

                var kept = Map.GetAllPins().Contains(pin) &&
                           StaticObjectMapping.TryGetCachedPin(identify, out var cached) && ReferenceEquals(cached, pin);
                if (after == before)
                    run.Skip(check, "the second static pass did not run (the game is paused or the area around you is still loading); retry" + removal);
                else if (_loadingQueries == 0)
                    run.Fail(check, "the static pass did not ask whether the deposit's zone had loaded" + removal);
                else if (kept)
                    run.Pass(check, "the deposit's pin stayed on the map and in the static cache through a pass that left its loading zone for later" + removal);
                else
                    run.Fail(check, "a static pass that left the deposit's loading zone for later removed the deposit's pin" + removal);
            }
            finally
            {
                // Whatever pin the deposit holds goes by the rule before the deposit does, so the
                // deposit's destruction cannot take a pin of yours that it took as its own.
                if (go && Objects.GetZdoid(go.transform, out var id) &&
                    StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(id), out var held))
                    smokePins.Remove(map, held);
                smokePins.Remove(map, pin);
                RemoveTestObject(go);
            }
        }

        private static Vector2s _loadingZone;
        private static int _loadingQueries;
        private static bool _loadingActive;

        // Reports the test zone as still loading during the check's second pass only. Every other
        // call, including any after the check if the patch could not be removed, goes to the game.
        private static bool ReportZoneLoading(Vector3 point, ref bool __result)
        {
            if (!_loadingActive || ZoneSystem.GetZone(point) != _loadingZone) return true;
            _loadingQueries++;
            __result = false;
            return false;
        }

        private const string MinedDepositCheck = "pin_mined_deposit_one_pin";
        private const string MinedDepositKeyCheck = "pin_key_mined_deposit_duplicate";
        private const string NearestDepositCheck = "pin_adjacent_deposits_nearest_adopts";
        private const string TiedDepositsCheck = "pin_tied_deposits_adopt_none";

        // What the game does when it loads an object from its ZDO, and the static pass's mapping of
        // one mineral deposit with what it keeps for the pass: the deposits it has mapped and
        // whether a wishbone is equipped.
        private static readonly System.Reflection.MethodInfo CreateObjectMethod =
            AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });

        private static readonly System.Reflection.MethodInfo MineralMappingMethod =
            AccessTools.Method(typeof(StaticObjectMapping), "MineralMapping");

        private static readonly System.Reflection.FieldInfo MineralsMappedField =
            AccessTools.Field(typeof(StaticObjectMapping), "MineralsMappedThisPass");

        private static readonly System.Reflection.FieldInfo WishboneField =
            AccessTools.Field(typeof(StaticObjectMapping), "_wishboneEquippedThisPass");

        // The copper deposits and the pins one deposit check creates, all taken away when it is
        // disposed, before the next check runs; its pins also join the section's list, which
        // retries a removal that fails. Every removal goes through the snapshot taken when the
        // fixture is made (SmokePins), and each deposit lets go of its pin before it goes, so
        // neither the cleanup nor a deposit's destruction can take a pin of yours off the map.
        private sealed class DepositFixture : IDisposable
        {
            private readonly Minimap _map;
            private readonly List<Minimap.PinData> _section;
            private readonly SmokePins _pins = new SmokePins();
            private readonly HashSet<Minimap.PinData> _created = new HashSet<Minimap.PinData>();
            private readonly List<GameObject> _deposits = new List<GameObject>();
            private readonly List<ZDOID> _ids = new List<ZDOID>();

            public DepositFixture(Minimap map, List<Minimap.PinData> section)
            {
                _map = map;
                _section = section;
            }

            public GameObject Spawn(Vector3 position, out string skip)
            {
                var go = SpawnTestObject(new[] { "rock4_copper" }, position, Quaternion.identity, out skip,
                    prefab => prefab.GetComponent<MineRock5>());
                if (!go) return null;
                _deposits.Add(go);
                if (Objects.GetZdoid(go.transform, out var id)) _ids.Add(id);
                return go;
            }

            // Unloads the deposit as the game unloads an object whose zone leaves the active area,
            // keeping its ZDO as a world object's persistent one stays, and loads it again from that
            // ZDO at once, as the game loads it. A ZDO left without an instance is destroyed before
            // the game could spawn the deposit from it again.
            public GameObject Reload(GameObject go)
            {
                var nview = go.GetComponent<ZNetView>();
                var zdo = nview.GetZDO();
                var instances = (Dictionary<ZDO, ZNetView>)SceneInstancesField.GetValue(ZNetScene.instance);
                nview.ResetZDO();
                go.SetActive(false);
                UnityEngine.Object.Destroy(go);
                SmokeSession.ReleaseSpawn(go);
                _deposits.Remove(go);
                instances.Remove(zdo);
                GameObject loaded = null;
                try
                {
                    loaded = CreateObjectMethod.Invoke(ZNetScene.instance, new object[] { zdo }) as GameObject;
                }
                finally
                {
                    if (loaded)
                    {
                        SmokeSession.TrackSpawn(loaded);
                        _deposits.Add(loaded);
                    }
                    else if (ZDOMan.instance.GetZDO(zdo.m_uid) != null)
                    {
                        if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
                        ZDOMan.instance.DestroyZDO(zdo);
                        SendDestroyedMethod?.Invoke(ZDOMan.instance, null);
                    }
                }
                Physics.SyncTransforms();
                return loaded;
            }

            // Runs the action and records every pin it adds to the map as the fixture's own.
            public T Record<T>(Func<T> action)
            {
                var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
                try
                {
                    return action();
                }
                finally
                {
                    foreach (var pin in Map.GetAllPins())
                        if (!before.Contains(pin)) Own(pin);
                }
            }

            // A pin the check created becomes the fixture's; a pin that was already on the map does not.
            public void Own(Minimap.PinData pin)
            {
                if (_pins.Created(pin) && _created.Add(pin)) _section.Add(pin);
            }

            public void Remove(Minimap.PinData pin)
            {
                _pins.Remove(_map, pin);
            }

            // Lets go of the pin each test deposit holds, by the rule; the one kept, a pin the check
            // created, only leaves the static cache and stays on the map.
            public void Release(Minimap.PinData kept = null)
            {
                foreach (var id in _ids)
                {
                    if (!StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(id), out var pin))
                        continue;
                    if (ReferenceEquals(pin, kept)) StaticObjectMapping.OnRemovePin(pin);
                    else _pins.Remove(_map, pin);
                }
            }

            public void Dispose()
            {
                try
                {
                    Release();
                }
                finally
                {
                    foreach (var pin in _created) _pins.Remove(_map, pin);
                    foreach (var go in _deposits) RemoveTestObject(go);
                }
            }
        }

        // Why the deposit checks cannot run, or null: they map copper deposits, and their saved
        // test pin needs a local platform user as its author.
        private static string DepositPreflight(out PlatformUserID author)
        {
            var localUser = PlatformManager.DistributionPlatform?.LocalUser;
            author = localUser != null ? localUser.PlatformUserID : PlatformUserID.None;
            if (!MappingConfig.AllowPinningMineral.Contains("CopperDeposit"))
                return "copper deposits are not in allow_pinning_mineral";
            if (!author.IsValid)
                return "no local platform user is available to author a saved pin";
            return null;
        }

        // Two ground spots for the deposit checks, a spot and the one 4 m to its right as seen from
        // you, the nearest first and in front of you first, from 7 m to 27 m out, the spot within
        // maxDistance of you. No pin may be within 10 m of either, so that none takes part: neither
        // a pin of yours nor one the section's earlier checks still hold, which the search moves
        // away from.
        private static bool TryFindDepositSpots(Player player, float maxDistance, out Vector3 spot,
            out Vector3 beside)
        {
            var forward = Vector3.ProjectOnPlane(GameCamera.instance.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up);
            forward.Normalize();
            Vector3 Ground(Vector3 point)
            {
                point.y = ZoneSystem.instance.GetGroundHeight(point);
                return point;
            }

            for (var distance = 7f; distance <= maxDistance && distance <= 27f; distance += 5f)
            for (var i = 0; i < 8; i++)
            {
                var direction = Quaternion.Euler(0f, i * 45f, 0f) * forward;
                spot = Ground(player.transform.position + direction * distance);
                beside = Ground(spot + Vector3.Cross(Vector3.up, direction) * 4f);
                if (Map.GetClosestPin(spot, 10f, includeInactive: true) == null &&
                    Map.GetClosestPin(beside, 10f, includeInactive: true) == null)
                    return true;
            }
            spot = beside = Vector3.zero;
            return false;
        }

        private const string NoDepositSpot =
            "every spot 7 to 27 m around you, within reach, has a pin within 10 m; move to open ground and retry";

        // A deposit's pin stands where the deposit's hit areas were centred when the pin was added,
        // and mining moves that centre. A non-persistent copper deposit is spawned at a spot no pin
        // is near (TryFindDepositSpots), within the static range. A saved automatic pin under its
        // name and icon is added where its centre is with the fewest of its hit areas mined that
        // put the pin more than 1.5 m from the deposit's own position, where the key's ray resolves
        // the deposit. More hit areas, nearest that position first, are then marked mined in the
        // deposit's ZDO, until its centre is more than 1.5 m on from the pin, and the deposit is
        // unloaded and loaded again from its ZDO, as the game loads a deposit mined in an earlier
        // session: its mined hit areas are off from the start. The pin key, and then a static pass
        // that starts with a fresh scan of the area, must take the saved pin as the deposit's own
        // and add no second pin.
        private static void CheckMinedDeposit(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            var interval = MappingConfig.StaticObjectMappingInterval;
            var range = MappingConfig.StaticObjectMappingRange;
            var reason = DepositPreflight(out var author);
            var spot = Vector3.zero;
            if (!MappingConfig.EnableAutomaticMapping || interval <= 0f || range <= 0)
                reason = "enable_automatic_mapping is off, or static_object_mapping_interval or static_object_mapping_range is 0, so no periodic static pass runs";
            else if (CreateObjectMethod == null || SceneInstancesField == null)
                reason = "ZNetScene.CreateObject or ZNetScene.m_instances was not found in this game version";
            else if ((bool)AccessTools.Field(typeof(StaticObjectMapping), "_cacheIncomplete").GetValue(null))
                reason = "the static scan here is incomplete, so a pass does not scan the area again; move away from dense builds and retry";
            else if (reason == null && !TryFindDepositSpots(player, range - 1f, out spot, out _))
                reason = NoDepositSpot + $" (static_object_mapping_range is {range} m)";
            if (reason != null)
            {
                run.Skip(MinedDepositCheck, reason);
                run.Skip(MinedDepositKeyCheck, reason);
                return;
            }

            using (var fixture = new DepositFixture(map, created))
            {
                var go = fixture.Spawn(spot, out var skip);
                if (go)
                {
                    Physics.SyncTransforms();
                    var colliders = go.GetComponentsInChildren<Collider>();
                    if (colliders.Length < 2 || !Objects.GetZNetView(go.transform, out var nview) ||
                        nview.GetZDO() == null)
                        skip = "the spawned deposit has fewer than two hit areas or no ZDO";
                    else if (!PlanMining(go.transform.position, colliders, out var pinAt, out var mined,
                                 out var minedCentre))
                        skip = "mining the deposit's hit areas cannot move its centre 1.5 m from its position and then 1.5 m on from there";
                    else
                    {
                        var package = new ZPackage();
                        package.Write(mined.Length);
                        var health = go.GetComponent<MineRock5>().m_health;
                        foreach (var gone in mined) package.Write(gone ? 0f : health);
                        var identify = new StaticObjectMapping.MapPinIdentify(nview.GetZDO().m_uid);
                        nview.GetZDO().Set(ZDOVars.s_health, Convert.ToBase64String(package.GetArray()));

                        var loaded = fixture.Reload(go);
                        var deposit = loaded ? loaded.GetComponent<MineRock5>() : null;
                        if (!deposit || deposit.GetComponentsInChildren<Collider>(true)
                                .Count(x => !x.gameObject.activeInHierarchy) != mined.Count(x => x))
                            skip = "the deposit loaded again from its ZDO did not come back with its mined hit areas off";
                        else
                        {
                            var saved = fixture.Record(() => AddSavedDepositPin(map, loaded, pinAt, author));
                            if (!Map.IsAutomaticPin(saved) || StaticObjectMapping.OwnsPin(saved) ||
                                StaticObjectMapping.TryGetCachedPin(identify, out _))
                            {
                                const string precondition =
                                    "precondition failed: the test pin is not an automatic pin outside the static cache";
                                run.Fail(MinedDepositKeyCheck, precondition);
                                run.Fail(MinedDepositCheck, precondition);
                                return;
                            }
                            CheckMinedDepositKey(run, player, map, fixture, loaded, saved);
                            CheckMinedDepositPass(run, player, interval, fixture, identify, saved, minedCentre);
                            return;
                        }
                    }
                }
                run.Skip(MinedDepositKeyCheck, skip);
                run.Skip(MinedDepositCheck, skip);
            }
        }

        // The ray action at the mined deposit, through the first of its standing hit areas an
        // unobstructed camera ray reaches.
        private static void CheckMinedDepositKey(FeatureRun run, Player player, Minimap map, DepositFixture fixture,
            GameObject deposit, Minimap.PinData saved)
        {
            var camera = GameCamera.instance.transform.position;
            foreach (var collider in deposit.GetComponentsInChildren<Collider>())
            {
                var ray = new Ray(camera, collider.bounds.center - camera);
                if (!FirstHit(player, ray, out var hit) || !hit.collider.transform.IsChildOf(deposit.transform) ||
                    !ObjectPinning.TryResolveFromRay(player, ray, out var aimed) || aimed.Instance != deposit)
                    continue;

                var count = Map.GetAllPins().Count;
                var added = fixture.Record(() => ObjectPinning.TryPinFromRay(player, ray));
                var none = added == null && Map.GetAllPins().Count == count;
                // A pin the key added must not stand beside the deposit during the static pass.
                fixture.Remove(added);
                if (none && ReferenceEquals(ObjectPinning.FindStaticPin(aimed), saved))
                    run.Pass(MinedDepositKeyCheck,
                        "the key reported the mined deposit's saved pin, more than 1.5 m from the deposit's position, as its pin");
                else
                    run.Fail(MinedDepositKeyCheck, !none
                        ? "the key added a second pin beside the mined deposit's saved pin"
                        : "the key added no pin, but the deposit's lookup did not return its saved pin");
                return;
            }
            run.Skip(MinedDepositKeyCheck, "no unobstructed camera ray reaches the test deposit");
        }

        // A static pass that starts with a fresh scan of the area, through the entry point the
        // minimap update calls, as pin_static_loading_area_kept runs it. It maps the real objects
        // around you as any pass does, and their pins stay.
        private static void CheckMinedDepositPass(FeatureRun run, Player player, float interval,
            DepositFixture fixture, StaticObjectMapping.MapPinIdentify identify, Minimap.PinData saved,
            Vector3 minedCentre)
        {
            var lastScan = AccessTools.Field(typeof(StaticObjectMapping), "_lastCacheUpdateTime");
            var passId = AccessTools.Field(typeof(StaticObjectMapping), "_currentPassId");
            lastScan.SetValue(null, 0f);
            var before = (int)passId.GetValue(null);
            var pins = new HashSet<Minimap.PinData>(Map.GetAllPins());
            var beside = new List<Minimap.PinData>();
            Minimap.PinData held = null;
            try
            {
                global::Automatics.AutomaticMapping.AutomaticMapping.Mapping(player, interval, false);
            }
            finally
            {
                // A pin the pass gave the deposit is the fixture's.
                beside.AddRange(Map.GetAllPins()
                    .Where(x => !pins.Contains(x) && Utils.DistanceXZ(x.m_pos, minedCentre) <= 1f));
                beside.ForEach(fixture.Own);
                if (StaticObjectMapping.TryGetCachedPin(identify, out held)) fixture.Own(held);
            }

            if ((int)passId.GetValue(null) == before)
                run.Skip(MinedDepositCheck,
                    "the static pass did not run (the game is paused or the area around you is still loading); retry");
            else if (ReferenceEquals(held, saved) && Map.GetAllPins().Contains(saved) && beside.Count == 0)
                run.Pass(MinedDepositCheck,
                    "the mined deposit, loaded again with its centre more than 1.5 m from its saved pin, took that pin as its own and got no second pin");
            else
                run.Fail(MinedDepositCheck, beside.Count > 0
                    ? "the static pass added a second pin at the mined deposit's centre beside its saved pin"
                    : "the static pass did not take the saved pin as the mined deposit's own");
        }

        // Hit areas to mark mined, nearest the deposit's position first, so the centre of the rest
        // moves away from that position: where the centre is with the fewest of them mined that put
        // it more than 1.5 m from the position, which hit areas must then be mined to move it more
        // than 1.5 m on from there with at least one left, and where it then is.
        private static bool PlanMining(Vector3 position, Collider[] colliders, out Vector3 pinAt,
            out bool[] mined, out Vector3 minedCentre)
        {
            var centres = colliders.Select(x => x.bounds.center).ToArray();
            var full = centres.Aggregate(Vector3.zero, (sum, x) => sum + x) / centres.Length;
            var away = Vector3.ProjectOnPlane(full - position, Vector3.up);
            if (away.sqrMagnitude < 0.01f)
                away = Vector3.ProjectOnPlane(GameCamera.instance.transform.right, Vector3.up);
            away.Normalize();
            var order = Enumerable.Range(0, centres.Length)
                .OrderBy(i => Vector3.Dot(centres[i] - full, away)).ToArray();
            Vector3 CentreAfter(int count) =>
                order.Skip(count).Aggregate(Vector3.zero, (sum, i) => sum + centres[i]) / (centres.Length - count);

            pinAt = minedCentre = Vector3.zero;
            mined = new bool[centres.Length];
            var first = -1;
            for (var count = 0; count < centres.Length - 1 && first < 0; count++)
                if (Utils.DistanceXZ(CentreAfter(count), position) > 1.5f)
                    first = count;
            if (first < 0) return false;

            pinAt = CentreAfter(first);
            for (var count = first + 1; count < centres.Length; count++)
            {
                var centre = CentreAfter(count);
                if (Utils.DistanceXZ(centre, pinAt) <= 1.5f) continue;
                for (var i = 0; i < count; i++) mined[order[i]] = true;
                minedCentre = centre;
                return true;
            }
            return false;
        }

        // Which of two deposits of the same kind takes a saved pin must not depend on the order a
        // static pass reaches them. Non-persistent copper deposits are spawned on the ground where
        // no pin is near (TryFindDepositSpots), with a saved automatic pin under their name and
        // icon. Both go through the static pass's mineral mapping, called directly in each order,
        // one then the other and the other way round, each order starting with neither deposit
        // mapped in the pass or holding a pin. First the deposits stand 4 m apart, and the pin
        // 1.5 m from the centre of one towards the other: that deposit takes it in both orders, and
        // the other never does. Then the two stand at one spot, so the pin, 0.5 m from their
        // centre, is as close to one as to the other: neither takes it, in either order.
        private static void CheckDepositsSharingPin(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            if (MineralMappingMethod == null || MineralsMappedField == null || WishboneField == null)
            {
                const string missing = "the static pass has no mineral mapping, with its per-pass state, to call";
                run.Fail(NearestDepositCheck, missing);
                run.Fail(TiedDepositsCheck, missing);
                return;
            }
            var reason = DepositPreflight(out var author);
            var spot = Vector3.zero;
            var beside = Vector3.zero;
            if (reason == null && !TryFindDepositSpots(player, float.PositiveInfinity, out spot, out beside))
                reason = NoDepositSpot;
            if (reason != null)
            {
                run.Skip(NearestDepositCheck, reason);
                run.Skip(TiedDepositsCheck, reason);
                return;
            }

            CheckDepositPair(run, NearestDepositCheck, map, spot, beside, author, created);
            CheckDepositPair(run, TiedDepositsCheck, map, spot, spot, author, created);
        }

        private static void CheckDepositPair(FeatureRun run, string check, Minimap map, Vector3 first,
            Vector3 second, PlatformUserID author, List<Minimap.PinData> created)
        {
            var tied = check == TiedDepositsCheck;
            using (var fixture = new DepositFixture(map, created))
            {
                var deposits = new GameObject[2];
                deposits[0] = fixture.Spawn(first, out var skip);
                if (deposits[0]) deposits[1] = fixture.Spawn(second, out skip);
                if (!deposits[1])
                {
                    run.Skip(check, skip);
                    return;
                }
                Physics.SyncTransforms();

                var centres = deposits.Select(HitAreaCentre).ToArray();
                var toward = Vector3.ProjectOnPlane(centres[1] - centres[0], Vector3.up);
                if (!tied && toward.magnitude < 3.5f)
                {
                    run.Skip(check, "the two deposits' centres are less than 3.5 m apart");
                    return;
                }
                var saved = fixture.Record(() => AddSavedDepositPin(map, deposits[0],
                    centres[0] + (tied ? Vector3.right * 0.5f : toward.normalized * 1.5f), author));
                if (!Map.IsAutomaticPin(saved) || StaticObjectMapping.OwnsPin(saved))
                {
                    run.Fail(check, "precondition failed: the test pin is not an automatic pin outside the static cache");
                    return;
                }

                var rocks = deposits.Select(x => (Component)x.GetComponent<MineRock5>()).ToArray();
                var forwards = MapInOrder(fixture, rocks, new[] { 0, 1 }, saved);
                var backwards = MapInOrder(fixture, rocks, new[] { 1, 0 }, saved);
                bool Correct(Minimap.PinData[] held) => tied
                    ? !ReferenceEquals(held[0], saved) && !ReferenceEquals(held[1], saved)
                    : ReferenceEquals(held[0], saved) && !ReferenceEquals(held[1], saved);
                string Holder(Minimap.PinData[] held) => ReferenceEquals(held[0], saved)
                    ? "the first deposit"
                    : ReferenceEquals(held[1], saved) ? "the second deposit" : "neither deposit";
                var detail = $"mapped first then second, {Holder(forwards)} took the pin; second then first, {Holder(backwards)} did";
                if (Correct(forwards) && Correct(backwards))
                    run.Pass(check, (tied
                        ? "two deposits at one spot, with the pin as close to one as to the other: "
                        : "two deposits 4 m apart, with the pin 1.5 m from the first: ") + detail);
                else
                    run.Fail(check, detail);
            }
        }

        // Maps the deposits through the static pass's mineral mapping in the given order and
        // returns the pin each then holds. Afterwards none holds a pin again, and the saved test pin
        // stays on the map for the next order (see DepositFixture.Release). What the mapping keeps
        // for the pass, the deposits it mapped and whether a wishbone is equipped, is put back as it
        // was, also when the mapping throws.
        private static Minimap.PinData[] MapInOrder(DepositFixture fixture, Component[] deposits, int[] order,
            Minimap.PinData saved)
        {
            var mapped = (HashSet<ZDOID>)MineralsMappedField.GetValue(null);
            var wishbone = WishboneField.GetValue(null);
            var ids = deposits.Select(x => Objects.GetZdoid(x, out var id) ? id : ZDOID.None).ToArray();
            var held = new Minimap.PinData[deposits.Length];
            try
            {
                fixture.Record(() =>
                {
                    foreach (var i in order)
                        MineralMappingMethod.Invoke(null, new object[] { deposits[i], Objects.GetName(deposits[i]) });
                    return true;
                });
                for (var i = 0; i < ids.Length; i++)
                    StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(ids[i]), out held[i]);
            }
            finally
            {
                fixture.Release(saved);
                foreach (var id in ids) mapped.Remove(id);
                WishboneField.SetValue(null, wishbone);
            }
            return held;
        }

        // A saved automatic pin under the name and icon a static pass gives the deposit, added
        // through Minimap.AddPin as the game adds each saved pin back when it loads the map.
        private static Minimap.PinData AddSavedDepositPin(Minimap map, GameObject deposit, Vector3 position,
            PlatformUserID author)
        {
            var sourceToken = Objects.GetName(deposit.GetComponent<MineRock5>());
            var label = ValheimObject.Mineral.GetIdentify(sourceToken, out var identifier) &&
                        ValheimObject.Mineral.GetName(identifier, out var registered)
                ? registered
                : sourceToken;
            var type = IconPack.GetPinType(new Target { name = label, prefabName = Objects.GetPrefabName(deposit) });
            return map.AddPin(position, type, Map.StoredPinName(label), true, false, 0L, author);
        }

        // The centre of a deposit's hit areas, all of them standing.
        private static Vector3 HitAreaCentre(GameObject deposit)
        {
            var colliders = deposit.GetComponentsInChildren<Collider>();
            return colliders.Aggregate(Vector3.zero, (sum, x) => sum + x.bounds.center) / colliders.Length;
        }

        // A multiplayer client has no world location list (the game keeps
        // ZoneSystem.m_locationInstances on the server), so its static pass classifies the Location
        // objects loaded around the player instead. A single-player world never takes that path, so
        // the check calls it directly for one test location: a bare Location component on a new
        // GameObject with no network view, named as the game names a spawned spot, 6 m beside the
        // player. No location prefab is spawned. The call's 1 m range leaves every real location
        // out, and the test location is destroyed before the check returns.
        private static void CheckLoadedLocationSpot(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            const string check = "pin_loaded_location_spot";
            var mapping = AccessTools.Method(typeof(StaticObjectMapping), "LoadedLocationMapping");
            if (mapping == null)
            {
                run.Fail(check, "the static pass has no mapping of loaded locations");
                return;
            }
            var spot = EarlyLocations.FirstOrDefault(x => MappingConfig.AllowPinningSpot.Contains(x) &&
                ValheimObject.Spot.GetIdentify(x, out var identifier) && identifier == x);
            if (spot == null || !ValheimObject.Spot.GetName(spot, out var label) || string.IsNullOrEmpty(label))
            {
                run.Skip(check, "no early-content spot is in allow_pinning_spot");
                return;
            }

            var origin = player.transform.position;
            var position = Vector3.zero;
            var found = false;
            for (var i = 0; i < 8 && !found; i++)
            {
                var angle = i * Mathf.PI / 4f;
                position = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 6f;
                found = Map.GetClosestPin(position, 2f, includeInactive: true) == null &&
                        !StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(position), out _) &&
                        (StaticObjectMapping.LoadedLocations == null || !StaticObjectMapping.LoadedLocations.Any(
                            location => location && Vector3.Distance(location.transform.position, position) < 2f));
            }
            if (!found)
            {
                run.Skip(check, "every test spot 6 m around you has a pin or a location within 2 m; move and retry");
                return;
            }
            if (!MappingConfig.SaveStaticObjectPins && Map.ShouldSuppressTransientAutomaticPin(position))
            {
                run.Skip(check, "the map filters unsaved automatic pins at the test spot, for example because unexplored automatic pins are hidden");
                return;
            }

            var go = new GameObject(spot + "(Clone)");
            Minimap.PinData pin = null;
            try
            {
                go.transform.position = position;
                // Awake adds it to the game's list of loaded locations.
                go.AddComponent<Location>();
                var key = new StaticObjectMapping.MapPinIdentify(position);
                List<Minimap.PinData> MapLoaded()
                {
                    var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
                    try
                    {
                        mapping.Invoke(null, new object[] { position, 1f });
                    }
                    finally
                    {
                        created.AddRange(Map.GetAllPins().Where(x => !before.Contains(x)));
                    }
                    return Map.GetAllPins().Where(x => !before.Contains(x)).ToList();
                }

                var added = MapLoaded();
                pin = added.FirstOrDefault();
                var expectedType = IconPack.GetPinType(new Target { name = label, prefabName = spot });
                if (added.Count != 1 || Utils.DistanceXZ(pin.m_pos, position) > 0.01f ||
                    pin.m_name != Map.StoredPinName(label) || pin.m_type != expectedType ||
                    !StaticObjectMapping.TryGetCachedPin(key, out var cached) || !ReferenceEquals(cached, pin) ||
                    !StaticObjectMapping.TryGetPinKind(pin, out var kind) || kind != PinKind.Spot)
                {
                    run.Fail(check, $"the loaded location '{go.name}' did not get exactly one spot pin with its name, icon and position in the static cache (added {added.Count})");
                    return;
                }

                var again = MapLoaded();
                if (again.Count == 0 && Map.GetAllPins().Contains(pin) &&
                    StaticObjectMapping.TryGetCachedPin(key, out cached) && ReferenceEquals(cached, pin))
                    run.Pass(check, $"the loaded location '{go.name}' got one spot pin at its position, and mapping it again kept that pin and added nothing");
                else
                    run.Fail(check, "mapping the same loaded location again added a pin or dropped its spot pin");
            }
            finally
            {
                if (pin != null && map) map.RemovePin(pin);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // Collects the warnings written to the BepInEx log while it is registered.
        private sealed class WarningListener : BepInEx.Logging.ILogListener
        {
            public readonly List<string> Messages = new List<string>();

            public void LogEvent(object sender, BepInEx.Logging.LogEventArgs eventArgs)
            {
                if (eventArgs.Level == BepInEx.Logging.LogLevel.Warning)
                    Messages.Add(eventArgs.Data?.ToString() ?? "");
            }

            public void Dispose()
            {
            }
        }

        // A single-player world lists every location instance, spawned or not, and the static
        // pass maps a dungeon from that list. The entrance belongs to the Location object the
        // game spawns later, so a dungeon without that object is left to a later pass: no pin and
        // no warning. The check calls the pass's dungeon mapping directly for one test instance
        // 6 m beside the player that is in no world list, first with no Location object there and
        // then with a bare one (no prefab is spawned), which has no entrance. The dungeon is the
        // first early-content one in allow_pinning_dungeon, and its 2 m radius leaves every real
        // location and pin out.
        private static void CheckDungeonWaitsForLocation(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            const string check = "pin_dungeon_waits_for_location";
            var mapping = AccessTools.Method(typeof(StaticObjectMapping), "DungeonMapping");
            if (mapping == null)
            {
                run.Fail(check, "the static pass has no mapping of a location instance's dungeon");
                return;
            }
            var dungeon = EarlyLocations.FirstOrDefault(x => ValheimObject.Dungeon.GetIdentify(x, out var identifier) &&
                identifier == x && MappingConfig.AllowPinningDungeon.Contains(x));
            if (dungeon == null || !ValheimObject.Dungeon.GetName(dungeon, out var label) || string.IsNullOrEmpty(label))
            {
                run.Skip(check, "no early-content dungeon is in allow_pinning_dungeon");
                return;
            }

            const float radius = 2f;
            var origin = player.transform.position;
            var position = Vector3.zero;
            var found = false;
            for (var i = 0; i < 8 && !found; i++)
            {
                var angle = i * Mathf.PI / 4f;
                position = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 6f;
                found = Map.GetClosestPin(position, radius * 2f, includeInactive: true) == null &&
                        !StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(position), out _) &&
                        !Physics.OverlapSphere(position, radius, LayerMask.GetMask("character_trigger"),
                            QueryTriggerInteraction.Collide).Any(collider => collider.GetComponent<Teleport>()) &&
                        (StaticObjectMapping.LoadedLocations == null || !StaticObjectMapping.LoadedLocations.Any(
                            location => location && Utils.DistanceXZ(location.transform.position, position) < 2f));
            }
            if (!found)
            {
                run.Skip(check, "every test spot 6 m around you has a pin, a dungeon entrance or a location within 2 m; move and retry");
                return;
            }
            if (!MappingConfig.SaveStaticObjectPins && Map.ShouldSuppressTransientAutomaticPin(position))
            {
                run.Skip(check, "the map filters unsaved automatic pins at the test spot, for example because unexplored automatic pins are hidden");
                return;
            }

            var instance = new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefabName = dungeon, m_exteriorRadius = radius },
                m_position = position,
                m_placed = true
            };
            var warnings = new WarningListener();
            var warningsLogged = global::Automatics.Config.IsLogEnabled(BepInEx.Logging.LogLevel.Warning);
            var key = new StaticObjectMapping.MapPinIdentify(position);
            GameObject go = null;
            Minimap.PinData pin = null;
            BepInEx.Logging.Logger.Listeners.Add(warnings);
            try
            {
                List<Minimap.PinData> MapDungeon(out bool handled)
                {
                    var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
                    try
                    {
                        handled = (bool)mapping.Invoke(null, new object[] { instance });
                    }
                    finally
                    {
                        created.AddRange(Map.GetAllPins().Where(x => !before.Contains(x)));
                    }
                    return Map.GetAllPins().Where(x => !before.Contains(x)).ToList();
                }

                var waiting = MapDungeon(out var handledWaiting);
                if (waiting.Count != 0 || StaticObjectMapping.TryGetCachedPin(key, out _) ||
                    warnings.Messages.Any(x => x.Contains("has no entrance")) || !handledWaiting)
                {
                    pin = waiting.FirstOrDefault();
                    run.Fail(check, $"a dungeon whose Location object has not spawned got {waiting.Count} pin(s), a cached pin, a no-entrance warning, or was not treated as a dungeon");
                    return;
                }

                go = new GameObject(dungeon + "(Clone)");
                go.transform.position = position;
                // Awake adds it to the game's list of loaded locations.
                go.AddComponent<Location>();
                var spawned = MapDungeon(out var handledSpawned);
                pin = spawned.FirstOrDefault();
                var expectedType = IconPack.GetPinType(new Target { name = label, prefabName = dungeon });
                if (spawned.Count != 1 || !handledSpawned || Utils.DistanceXZ(pin.m_pos, position) > 0.01f ||
                    pin.m_name != Map.StoredPinName(label) || pin.m_type != expectedType ||
                    !StaticObjectMapping.TryGetCachedPin(key, out var cached) || !ReferenceEquals(cached, pin) ||
                    !StaticObjectMapping.TryGetPinKind(pin, out var kind) || kind != PinKind.Dungeon)
                {
                    run.Fail(check, $"the dungeon whose Location object has spawned and has no entrance did not get exactly one pin with its name, icon and position in the static cache (added {spawned.Count})");
                    return;
                }
                if (warningsLogged && !warnings.Messages.Any(x => x.Contains("has no entrance")))
                {
                    run.Fail(check, "the dungeon with a spawned Location object and no entrance did not log that it has no entrance");
                    return;
                }

                run.Pass(check, "a dungeon with no spawned Location object got no pin and no warning; once the Location object stood there, it got one pin at its position" +
                    (warningsLogged ? " and the no-entrance warning" : " (the no-entrance warning was not checked: warnings are not logged with the current log settings)"));
            }
            finally
            {
                BepInEx.Logging.Logger.Listeners.Remove(warnings);
                if (pin != null && map) map.RemovePin(pin);
                if (go) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // An object with a name that can be changed, standing in for one whose hover name changes
        // while it lives.
        private sealed class SmokeNamedObject : MonoBehaviour, Hoverable
        {
            public string ObjectName = "";

            public string GetHoverText() => ObjectName;

            public string GetHoverName() => ObjectName;

            public float GetHoverOffset() => 0f;
        }

        private static readonly System.Reflection.MethodInfo CacheStaticObjectsMethod =
            AccessTools.Method(typeof(StaticObjectMapping), "CacheStaticObjects");

        private static readonly System.Reflection.MethodInfo ClassifyTokenMethod =
            AccessTools.Method(typeof(StaticObjectMapping), "ClassifyStaticComponentSourceToken");

        private static readonly System.Reflection.FieldInfo StaticCacheField =
            AccessTools.Field(typeof(StaticObjectMapping), "StaticObjectCache");

        // The static scan classifies a collider by the object it belongs to and that object's current
        // name, and a scan that follows another one must not keep what the earlier scan decided. One
        // collider is classified by scans in turn while its object is renamed to a name of another
        // kind, to a name no registry knows, and back, and while the collider moves under another
        // object with another name and back. Each scan is the static scan's own entry point with
        // its interval cleared, as a new pass of the static mapping starts, and the result is read
        // from the static cache. The objects hold no network view and are not mapped, so no pin is
        // added.
        private static void CheckStaticClassificationFollowsObject(FeatureRun run, Player player)
        {
            const string check = "pin_static_classification_follows_object";
            if (MappingConfig.StaticObjectMappingRange <= 0)
            {
                run.Skip(check, "static_object_mapping_range is 0, so the static scan does not run");
                return;
            }
            if (CacheStaticObjectsMethod == null || ClassifyTokenMethod == null || StaticCacheField == null)
            {
                run.Fail(check, "the static scan has no entry point, token classifier or cache to observe");
                return;
            }
            var cacheIncomplete = AccessTools.Field(typeof(StaticObjectMapping), "_cacheIncomplete");
            var lastScan = AccessTools.Field(typeof(StaticObjectMapping), "_lastCacheUpdateTime");
            if ((bool)cacheIncomplete.GetValue(null))
            {
                run.Skip(check, "the static scan here is incomplete; move away from dense builds and retry");
                return;
            }

            bool Classify(string name, out PinKind kind, out string identifier)
            {
                var args = new object[] { name, null, null };
                var found = (bool)ClassifyTokenMethod.Invoke(null, args);
                kind = found ? (PinKind)args[1] : default;
                identifier = found ? (string)args[2] : "";
                return found;
            }

            string FindName(ValheimObject registry, PinKind expected, out string id)
            {
                foreach (var element in registry.GetAllElements())
                foreach (var matcher in element.matches ?? new List<ObjectMatcher>())
                {
                    if (matcher == null || matcher.regex || string.IsNullOrEmpty(matcher.value)) continue;
                    if (Classify(matcher.value, out var kind, out var identifier) && kind == expected &&
                        identifier == element.identifier)
                    {
                        id = identifier;
                        return matcher.value;
                    }
                }
                id = "";
                return null;
            }

            var floraName = FindName(ValheimObject.Flora, PinKind.Flora, out var floraId);
            var mineralName = FindName(ValheimObject.Mineral, PinKind.Mineral, out var mineralId);
            const string unknownName = "AutomaticsSmokeUnclassifiedName";
            if (floraName == null || mineralName == null || Classify(unknownName, out _, out _))
            {
                run.Skip(check, "no exact flora name and exact mineral name were found in the registries, or the unclassified test name is known");
                return;
            }

            var center = player.transform.position + player.transform.right * 3f + Vector3.up;
            var hostA = new GameObject("AutomaticsSmokeHostA");
            var hostB = new GameObject("AutomaticsSmokeHostB");
            var holder = new GameObject("AutomaticsSmokeCollider");
            try
            {
                hostA.transform.position = center;
                hostB.transform.position = center + Vector3.up;
                var objectA = hostA.AddComponent<SmokeNamedObject>();
                var objectB = hostB.AddComponent<SmokeNamedObject>();
                objectA.ObjectName = floraName;
                objectB.ObjectName = mineralName;
                holder.transform.SetParent(hostA.transform, false);
                var collider = holder.AddComponent<BoxCollider>();
                collider.size = Vector3.one * 0.5f;

                // The result of one scan for the collider, or why the check cannot go on.
                bool Scan(out bool classified, out ClassifiedStaticObject result, out string problem)
                {
                    Physics.SyncTransforms();
                    lastScan.SetValue(null, 0f);
                    CacheStaticObjectsMethod.Invoke(null, new object[] { player.transform.position });
                    classified = false;
                    result = default;
                    problem = "";
                    if ((bool)cacheIncomplete.GetValue(null) || (float)lastScan.GetValue(null) <= 0f)
                    {
                        problem = "the static scan did not finish here (its collider buffer is full); move away from dense builds and retry";
                        return false;
                    }
                    var cache = (Dictionary<Collider, ClassifiedStaticObject>)StaticCacheField.GetValue(null);
                    classified = cache.TryGetValue(collider, out result);
                    return true;
                }

                string Expect(string step, string name, PinKind kind, string identifier, Component source)
                {
                    if (!Scan(out var classified, out var result, out var problem)) return "SKIP" + problem;
                    if (!classified)
                        return $"{step}: the collider was not classified, but its object is named as a {kind}";
                    return result.Kind == kind && result.Identifier == identifier &&
                           result.SourceToken == name && ReferenceEquals(result.Component, source)
                        ? null
                        : $"{step}: the collider was classified as {result.Kind} '{result.Identifier}' from '{result.SourceToken}', expected {kind} '{identifier}' from '{name}'";
                }

                string failure = Expect("first scan", floraName, PinKind.Flora, floraId, objectA);
                if (failure == null)
                {
                    objectA.ObjectName = mineralName;
                    failure = Expect("after the object was renamed to a mineral name", mineralName,
                        PinKind.Mineral, mineralId, objectA);
                }
                if (failure == null)
                {
                    objectA.ObjectName = unknownName;
                    if (!Scan(out var classified, out var result, out var problem)) failure = "SKIP" + problem;
                    else if (classified)
                        failure = $"after the object was renamed to an unknown name: the collider was still classified as {result.Kind} '{result.Identifier}' from '{result.SourceToken}'";
                }
                if (failure == null)
                {
                    objectA.ObjectName = floraName;
                    failure = Expect("after the object got a known name again", floraName, PinKind.Flora,
                        floraId, objectA);
                }
                if (failure == null)
                {
                    holder.transform.SetParent(hostB.transform, true);
                    failure = Expect("after the collider moved under an object with a mineral name", mineralName,
                        PinKind.Mineral, mineralId, objectB);
                }
                if (failure == null)
                {
                    holder.transform.SetParent(hostA.transform, true);
                    failure = Expect("after the collider moved back under the first object", floraName,
                        PinKind.Flora, floraId, objectA);
                }

                if (failure == null)
                    run.Pass(check, "one collider was classified as a flora, then a mineral after its object was renamed, not at all under an unknown name, a flora again under a known name, and a mineral and a flora again as it moved under other objects");
                else if (failure.StartsWith("SKIP"))
                    run.Skip(check, failure.Substring(4));
                else
                    run.Fail(check, failure);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
                UnityEngine.Object.DestroyImmediate(hostA);
                UnityEngine.Object.DestroyImmediate(hostB);
                // The destroyed collider leaves the cache with the next scan, not at the interval's end.
                try
                {
                    Physics.SyncTransforms();
                    lastScan.SetValue(null, 0f);
                    CacheStaticObjectsMethod.Invoke(null, new object[] { player.transform.position });
                }
                catch (Exception e)
                {
                    run.Fail(check + "_cleanup", "the scan that drops the test collider failed: " + e.Message);
                }
            }
        }

        private static readonly System.Reflection.MethodInfo FloraNetworkConstructionMethod =
            AccessTools.Method(typeof(FloraNode), "NetworkConstruction");

        // A flora patch is every plant of one name that a chain of plants, each within
        // flora_pins_merge_range of the next, links together, and nothing else. A plant looks for
        // its patch only among the plants of its name in the cells around it, so every pair the
        // range test accepts has to be found there. Plants of two flora names are spawned 60 m
        // above you, out of reach of every other plant: a chain of one name along a diagonal, its
        // plants 0.9 of the range apart and more than two of the largest range long on each axis,
        // so linked plants stand in different cells; a plant of the chain's name 1.1 of the range
        // past its end; a plant of the other name half the range beside it; and a pair of the
        // chain's name, half the range apart, 1.1 of the range above its first plant. As the game
        // can move or rename a plant right after it appears, two more come last: one of the
        // chain's name set up 60 m away and moved at once to half the range from the pair, and
        // one of the other name set up half the range beside the plant past the chain's end and
        // given the chain's name at once. Each plant's network is built at once, as the game
        // builds it a second or two after the plant appears, the chain's two ends first so that
        // its middle joins two networks, and the moved and renamed plants after the plants they
        // link to. The networks must be the groups that linking every two plants of one name
        // within the range gives. The plants are removed before the next check.
        private static void CheckFloraPatchGrouping(FeatureRun run, Player player)
        {
            const string check = "pin_flora_patches_group_plants_in_range";
            const float chainSpanPerAxis = 33f;
            var range = MappingConfig.FloraPinMergeRange;
            if (range < 4)
            {
                run.Skip(check, $"flora_pins_merge_range is {range} m; the test chain needs 4 m or more");
                return;
            }
            if (FloraNetworkConstructionMethod == null)
            {
                run.Fail(check, "the flora node has no network construction to call");
                return;
            }

            // The first two flora prefabs with different names.
            var kinds = new List<string>();
            var kindNames = new List<string>();
            foreach (var prefabName in new[]
                         { "Pickable_Mushroom", "Pickable_Dandelion", "Pickable_Thistle", "RaspberryBush", "BlueberryBush" })
            {
                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                var pickable = prefab ? prefab.GetComponent<Pickable>() : null;
                var name = pickable ? Objects.GetName(pickable) : "";
                if (string.IsNullOrEmpty(name) || !ValheimObject.Flora.IsDefined(name) || kindNames.Contains(name))
                    continue;
                kinds.Add(prefabName);
                kindNames.Add(name);
                if (kinds.Count == 2) break;
            }
            if (kinds.Count < 2)
            {
                run.Skip(check, "fewer than two flora prefabs with different names are available");
                return;
            }

            var spacing = 0.9f * range;
            var diagonal = new Vector3(1f, 0f, 1f).normalized;
            var beside = new Vector3(1f, 0f, -1f).normalized;
            var chainCount = Mathf.CeilToInt(chainSpanPerAxis * Mathf.Sqrt(2f) / spacing) + 1;
            var origin = player.transform.position + Vector3.up * 60f;
            var above = origin + Vector3.up * (1.1f * range);
            var pastEnd = origin + diagonal * ((chainCount - 1) * spacing + 1.1f * range);
            var movedTo = above - diagonal * (0.5f * range);
            // Each plant: its prefab, where it is set up, where it is moved to and the name it is
            // given right after (null for none).
            var layout = new List<(string Prefab, Vector3 At, Vector3? MovedTo, string Rename)>();
            for (var i = 0; i < chainCount; i++)
                layout.Add((kinds[0], origin + diagonal * (i * spacing), null, null));
            layout.Add((kinds[0], pastEnd, null, null));
            layout.Add((kinds[1], origin + diagonal * (chainCount / 2 * spacing) + beside * (0.5f * range), null, null));
            layout.Add((kinds[0], above, null, null));
            layout.Add((kinds[0], above + beside * (0.5f * range), null, null));
            var moved = layout.Count;
            layout.Add((kinds[0], movedTo + Vector3.left * 60f, movedTo, null));
            var renamed = layout.Count;
            layout.Add((kinds[1], pastEnd + beside * (0.5f * range), null, kindNames[0]));

            var spawned = new List<GameObject>();
            try
            {
                var nodes = new List<FloraNode>();
                foreach (var plant in layout)
                {
                    var go = SpawnTestObject(new[] { plant.Prefab }, plant.At, Quaternion.identity, out var skip,
                        prefab => prefab.GetComponent<Pickable>());
                    if (!go)
                    {
                        run.Skip(check, skip);
                        return;
                    }
                    spawned.Add(go);
                    var node = go.GetComponent<FloraNode>();
                    if (!node || node.ScopeGeneration != MappingModule.Generation)
                    {
                        run.Fail(check, $"a spawned {plant.Prefab} got no flora node from the running module");
                        return;
                    }
                    nodes.Add(node);
                    if (plant.MovedTo.HasValue) go.transform.position = plant.MovedTo.Value;
                    if (plant.Rename != null) go.GetComponent<Pickable>().m_overrideName = plant.Rename;
                }

                var reach = (range + 1f) * (range + 1f);
                var fixture = new HashSet<FloraNode>(nodes);
                foreach (var pickable in PickableCache.GetAllInstance())
                {
                    var other = pickable ? pickable.GetComponent<FloraNode>() : null;
                    if (!other || fixture.Contains(other)) continue;
                    if (nodes.Any(node => (node.Position - other.Position).sqrMagnitude <= reach))
                    {
                        run.Skip(check, "another flora plant is within reach of the test plants 60 m above you; move away from cliffs and retry");
                        return;
                    }
                }

                var order = new List<FloraNode> { nodes[0], nodes[chainCount - 1] };
                order.AddRange(nodes.Where((node, i) => i != 0 && i != chainCount - 1));
                foreach (var node in order) FloraNetworkConstructionMethod.Invoke(node, null);

                // The groups, from every two plants of one name within the range.
                var rangeSqr = (float)range * range;
                var group = Enumerable.Range(0, nodes.Count).ToArray();
                int Root(int i)
                {
                    while (group[i] != i) i = group[i];
                    return i;
                }
                for (var i = 0; i < nodes.Count; i++)
                for (var j = i + 1; j < nodes.Count; j++)
                {
                    var a = spawned[i];
                    var b = spawned[j];
                    if (Objects.GetName(a.GetComponent<Pickable>()) != Objects.GetName(b.GetComponent<Pickable>())) continue;
                    if ((a.transform.position - b.transform.position).sqrMagnitude > rangeSqr) continue;
                    group[Root(j)] = Root(i);
                }
                if (Enumerable.Range(0, nodes.Count).Select(Root).Distinct().Count() != 4 ||
                    Enumerable.Range(0, chainCount).Any(i => Root(i) != Root(0)) ||
                    Root(moved) != Root(chainCount + 2) || Root(renamed) != Root(chainCount))
                {
                    run.Skip(check, "the test plants did not stand where they were placed or take the name they were given");
                    return;
                }

                string failure = null;
                for (var i = 0; i < nodes.Count && failure == null; i++)
                {
                    var network = nodes[i].Network;
                    var size = Enumerable.Range(0, nodes.Count).Count(j => Root(j) == Root(i));
                    if (network == null)
                        failure = $"plant {i + 1} of {nodes.Count} is in no flora network";
                    else if (network.NodeCount != size)
                        failure = $"plant {i + 1}'s network holds {network.NodeCount} plant(s), but {size} test plant(s) link to it";
                    for (var j = i + 1; j < nodes.Count && failure == null; j++)
                        if (ReferenceEquals(nodes[i].Network, nodes[j].Network) != (Root(i) == Root(j)))
                            failure = Root(i) == Root(j)
                                ? $"plants {i + 1} and {j + 1} link through plants within {range} m but are in different networks"
                                : $"plants {i + 1} and {j + 1} do not link but share a network";
                }

                if (failure == null)
                    run.Pass(check, $"{nodes.Count} plants formed the four patches linking gives: a chain of {chainCount} plants {spacing:0.#} m apart, the plant {1.1f * range:0.#} m past its end with the plant renamed beside it, the plant of the other name beside the chain, and the pair above it with the plant moved next to it");
                else
                    run.Fail(check, failure);
            }
            finally
            {
                foreach (var go in spawned) RemoveTestObject(go);
            }
        }

        // A flora you add to custom_flora while Automatic Mapping runs reaches the plants that are
        // already loaded, without a reload or a module toggle. A pickable that is no flora (the
        // first of the flint, stone and branch pickables that qualifies) is spawned on the ground
        // beside you and gets no flora node. It is given a name of its own, so the flora entry the
        // check adds to custom_flora for that name, as the config screen's object editor adds one,
        // matches it and no plant of the world. The plant then gets its node from the running
        // activation, while every other plant keeps the one node it had. With the entry's
        // identifier added to allow_pinning_flora, the node's network is built at once, as the
        // game builds it a second or two later, and a static pass that starts with a fresh scan of
        // the area pins the plant. The plant and its pin are removed, and both settings put back,
        // before the next check.
        private static void CheckRuntimeFloraPinsLoadedPlant(FeatureRun run, Player player, Minimap map,
            List<Minimap.PinData> created)
        {
            const string check = "pin_runtime_flora_pins_loaded_plant";
            const string restoredCheck = "pin_runtime_flora_settings_restored";
            const string identifier = "AutomaticsSmokeRuntimeFlora";
            const string plantName = "AutomaticsSmokeRuntimeFloraPlant";
            var interval = MappingConfig.StaticObjectMappingInterval;
            var range = MappingConfig.StaticObjectMappingRange;
            if (!MappingConfig.EnableAutomaticMapping || interval <= 0f || range <= 0)
            {
                run.Skip(check, "enable_automatic_mapping is off, or static_object_mapping_interval or static_object_mapping_range is 0, so no periodic static pass runs");
                return;
            }
            if (FloraNetworkConstructionMethod == null || ClassifyTokenMethod == null || StaticCacheField == null)
            {
                run.Fail(check, "the flora node has no network construction, or the static scan no token classifier or cache, to observe");
                return;
            }
            var cacheIncomplete = AccessTools.Field(typeof(StaticObjectMapping), "_cacheIncomplete");
            var lastScan = AccessTools.Field(typeof(StaticObjectMapping), "_lastCacheUpdateTime");
            var passId = AccessTools.Field(typeof(StaticObjectMapping), "_currentPassId");
            if ((bool)cacheIncomplete.GetValue(null))
            {
                run.Skip(check, "the static scan here is incomplete, so it may not reach the test plant; move away from dense builds and retry");
                return;
            }
            var entries = global::Automatics.Automatics.Plugin?.Config.Select(x => x.Value).ToList();
            var customFlora = entries?.FirstOrDefault(x =>
                x.Definition.Key == "custom_flora" && x.SettingType == typeof(List<ObjectElement>));
            var allowFlora = entries?.FirstOrDefault(x =>
                x.Definition.Key == "allow_pinning_flora" && x.SettingType == typeof(StringList));
            if (customFlora == null || allowFlora == null)
            {
                run.Fail(check, "the custom_flora or allow_pinning_flora entry is not bound");
                return;
            }
            if (ValheimObject.Flora.IsDefined(identifier) || ValheimObject.Flora.IsDefined(plantName) ||
                (bool)ClassifyTokenMethod.Invoke(null, new object[] { plantName, null, null }))
            {
                run.Skip(check, $"the test identifier {identifier} or the test plant's name {plantName} is already known to a registry; remove it and retry");
                return;
            }

            // A pickable that is no flora, so it gets no flora node when it appears.
            string prefabName = null;
            foreach (var candidate in new[] { "Pickable_Flint", "Pickable_Stone", "Pickable_Branch" })
            {
                var prefab = ZNetScene.instance.GetPrefab(candidate);
                var pickable = prefab ? prefab.GetComponent<Pickable>() : null;
                if (!pickable || ValheimObject.Flora.IsDefined(Objects.GetName(pickable))) continue;
                prefabName = candidate;
                break;
            }
            if (prefabName == null)
            {
                run.Skip(check, "no flint, stone or branch pickable that is no flora is available");
                return;
            }

            var forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            var spot = player.transform.position + forward * 3f + player.transform.right * 3f;
            spot.y = ZoneSystem.instance.GetGroundHeight(spot);
            if (Map.GetClosestPin(spot, 8f, includeInactive: true) != null)
            {
                run.Skip(check, "a pin is within 8 m of the test spot beside you; move and retry");
                return;
            }
            if (!MappingConfig.SaveStaticObjectPins && Map.ShouldSuppressTransientAutomaticPin(spot))
            {
                run.Skip(check, "the map filters unsaved automatic pins at the test spot, for example because unexplored automatic pins are hidden");
                return;
            }

            var smokePins = new SmokePins();
            var go = SpawnTestObject(new[] { prefabName }, spot, Quaternion.identity, out var spawnSkip,
                prefab => prefab.GetComponent<Pickable>());
            Minimap.PinData pin = null;
            try
            {
                if (!go)
                {
                    run.Skip(check, spawnSkip);
                    return;
                }
                Physics.SyncTransforms();
                if (!Objects.GetZdoid(go.transform, out var id) ||
                    !go.GetComponentsInChildren<Collider>().Any(x => x.enabled))
                {
                    run.Skip(check, "the spawned plant has no ZDO or no enabled collider");
                    return;
                }
                if (go.GetComponent<FloraNode>())
                {
                    run.Fail(check, "precondition failed: the plant has a flora node before its name is a flora");
                    return;
                }
                var plant = go.GetComponent<Pickable>();
                plant.m_overrideName = plantName;
                if (Objects.GetName(plant) != plantName)
                {
                    run.Skip(check, $"the spawned {prefabName} does not go by the name it was given");
                    return;
                }

                if (!SmokeSession.BeginSetting(run.RunId, customFlora, out var floraSetting, out var refusal) ||
                    !SmokeSession.BeginSetting(run.RunId, allowFlora, out var allowSetting, out refusal))
                {
                    run.Skip(check, refusal);
                    return;
                }
                if (!ObjectElementList.TryCopyWithElement(
                        customFlora.BoxedValue as List<ObjectElement> ?? new List<ObjectElement>(), identifier,
                        "Automatics smoke runtime flora", plantName, out var withFlora))
                {
                    run.Fail(check, "precondition failed: the flora entry could not be made");
                    return;
                }
                if (!SmokeSession.WriteSetting(run.RunId, floraSetting, withFlora, out refusal))
                {
                    run.Skip(check, $"{customFlora.Definition.Key} could not be changed: {refusal}");
                    return;
                }

                var nodes = go.GetComponents<FloraNode>();
                var node = nodes.Length == 1 ? nodes[0] : null;
                var doubled = PickableCache.GetAllInstance().Count(x => x && x.GetComponents<FloraNode>().Length > 1);
                if (!node || node.ScopeGeneration != MappingModule.Generation || FloraNode.Find(id) != node ||
                    doubled > 0)
                {
                    run.Fail(check, $"after the flora was added the plant has {nodes.Length} flora node(s), " +
                                    $"owned by the running activation={node && node.ScopeGeneration == MappingModule.Generation}, " +
                                    $"found by its ZDO={node && FloraNode.Find(id) == node}, and {doubled} plant(s) have more than one");
                    return;
                }

                if (!SmokeSession.WriteSetting(run.RunId, allowSetting,
                        new StringList(MappingConfig.AllowPinningFlora) { identifier }, out refusal))
                {
                    run.Skip(check, $"{allowFlora.Definition.Key} could not be changed: {refusal}");
                    return;
                }
                FloraNetworkConstructionMethod.Invoke(node, null);
                if (node.Network == null)
                {
                    run.Fail(check, "the plant's flora node built no network");
                    return;
                }

                // A cleared scan time makes the pass scan the area again, as a classifier change does.
                lastScan.SetValue(null, 0f);
                var before = (int)passId.GetValue(null);
                global::Automatics.AutomaticMapping.AutomaticMapping.Mapping(player, interval, false);
                if ((int)passId.GetValue(null) == before || (bool)cacheIncomplete.GetValue(null))
                {
                    run.Skip(check, "the static pass did not run or did not finish its scan (the game is paused, the area around you is still loading, or the scan's collider buffer is full); retry");
                    return;
                }
                if (StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(id), out pin) &&
                    Map.GetAllPins().Contains(pin))
                {
                    if (smokePins.Created(pin)) created.Add(pin);
                    run.Pass(check, $"a {prefabName} loaded before its name became a flora got its node and its pin '{pin.m_name}' from the next static pass");
                    return;
                }

                var cache = (Dictionary<Collider, ClassifiedStaticObject>)StaticCacheField.GetValue(null);
                if (!cache.Any(pair => pair.Key && pair.Key.transform.IsChildOf(go.transform) &&
                                       pair.Value.Kind == PinKind.Flora && pair.Value.Identifier == identifier))
                    run.Skip(check, "the plant got its flora node, but the static scan did not classify its collider as the new flora; its collider may be on a layer the scan does not read");
                else
                    run.Fail(check, "the plant got its flora node and was classified as the new flora, but the static pass added no pin for it");
            }
            finally
            {
                // Whatever pin the plant holds goes by the rule before the plant does.
                if (go && Objects.GetZdoid(go.transform, out var heldId) &&
                    StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(heldId), out var held))
                    smokePins.Remove(map, held);
                smokePins.Remove(map, pin);
                RemoveTestObject(go);

                var results = SmokeSession.RestoreSettings(run.RunId);
                if (results.Count > 0)
                {
                    var detail = string.Join("; ", results.Select(x => x.Value));
                    if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                        run.Fail(restoredCheck, detail);
                    else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                              x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                        run.Skip(restoredCheck, "restoration skipped: " + detail);
                    else if (ValheimObject.Flora.IsDefined(identifier))
                        run.Fail(restoredCheck, "the settings were put back, but the test flora is still defined: " + detail);
                    else
                        run.Pass(restoredCheck, detail);
                }
            }
        }

        private const string DestroyedDepositCheck = "pin_destroyed_deposit_removed";
        private const string DestroyedKeepsOthersCheck = "pin_destroyed_manual_and_shared_kept";
        private const string DestroyedElsewhereCheck = "pin_destroyed_by_other_game_removed";
        private const string UnloadedDepositCheck = "pin_unloaded_deposit_kept";

        // What the game does with a destroyed object's ZDO on its next update (ZDOMan.SendDestroyed,
        // which also hands it to its own game), what it does when another game's destruction
        // arrives (ZDOMan.RPC_DestroyZDO), and the instances ZNetScene drops when it unloads objects.
        private static readonly System.Reflection.MethodInfo SendDestroyedMethod =
            AccessTools.Method(typeof(ZDOMan), "SendDestroyed");

        private static readonly System.Reflection.MethodInfo ReceiveDestroyedMethod =
            AccessTools.Method(typeof(ZDOMan), "RPC_DestroyZDO");

        private static readonly System.Reflection.FieldInfo SceneInstancesField =
            AccessTools.Field(typeof(ZNetScene), "m_instances");

        private static void SkipDestroyedObjectChecks(FeatureRun run, string reason)
        {
            run.Skip(DestroyedDepositCheck, reason);
            run.Skip(DestroyedKeepsOthersCheck, reason);
            run.Skip(DestroyedElsewhereCheck, reason);
            run.Skip(UnloadedDepositCheck, reason);
        }

        // A deposit with the ZDOID it was spawned with, which still finds its pin after the game
        // has reset the deposit's view, and the colliders it was spawned with, turned off until
        // the section's own pass. A disabled collider is still one of the deposit's hit areas.
        private sealed class TestDeposit
        {
            public GameObject Go;
            public ZDOID Id;
            public Collider[] Colliders;
        }

        // Three non-persistent tin deposits, 4 m apart on the ground 6 m in front of you, for the
        // destroyed-object checks. A tin deposit is a used-up deposit's simplest case: no destroy
        // callback of its own, only its ZDO going. False, with every check skipped for the reason,
        // when they cannot be set up; whatever was spawned is in spawned for the caller to remove.
        private static bool SpawnDestroyedObjectDeposits(FeatureRun run, Player player,
            List<TestDeposit> spawned)
        {
            var range = MappingConfig.StaticObjectMappingRange;
            string reason = null;
            if (!MappingConfig.EnableAutomaticMapping || MappingConfig.StaticObjectMappingInterval <= 0f || range <= 0)
                reason = "enable_automatic_mapping is off, or static_object_mapping_interval or static_object_mapping_range is 0, so no periodic static pass runs";
            else if (!MappingConfig.RemovePinsOfDestroyedObject)
                reason = "remove_pins_of_destroyed_object is off";
            else if (!MappingConfig.AllowPinningMineral.Contains("TinDeposit"))
                reason = "tin deposits are not in allow_pinning_mineral";
            else if (SendDestroyedMethod == null || ReceiveDestroyedMethod == null || SceneInstancesField == null)
                reason = "ZDOMan.SendDestroyed, ZDOMan.RPC_DestroyZDO or ZNetScene.m_instances was not found in this game version";
            else if ((bool)AccessTools.Field(typeof(StaticObjectMapping), "_cacheIncomplete").GetValue(null))
                reason = "the static scan here is incomplete, so it prunes no pin; move away from dense builds and retry";
            if (reason != null)
            {
                SkipDestroyedObjectChecks(run, reason);
                return false;
            }

            var forward = Vector3.ProjectOnPlane(GameCamera.instance.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = player.transform.forward;
            forward.Normalize();
            var right = Vector3.Cross(Vector3.up, forward);
            var origin = player.transform.position;
            for (var i = 0; i < 3; i++)
            {
                var spot = origin + forward * 6f + right * (4f * (i - 1));
                spot.y = ZoneSystem.instance.GetGroundHeight(spot);
                if (Vector3.Distance(origin, spot) > range - 1f)
                    reason = $"the test spots 6 m in front of you are outside static_object_mapping_range ({range} m)";
                else if (Map.GetClosestPin(spot, 3f, includeInactive: true) != null)
                    reason = "a pin is within 3 m of a test spot 6 m in front of you; turn or move and retry";
                else
                {
                    var go = SpawnTestObject(new[] { "MineRock_Tin" }, spot, Quaternion.identity, out var skip,
                        prefab => prefab.GetComponent<MineRock>());
                    if (go)
                    {
                        Objects.GetZdoid(go.transform, out var id);
                        var colliders = go.GetComponentsInChildren<Collider>();
                        foreach (var collider in colliders) collider.enabled = false;
                        spawned.Add(new TestDeposit { Go = go, Id = id, Colliders = colliders });
                        continue;
                    }
                    reason = skip;
                }

                SkipDestroyedObjectChecks(run, reason);
                return false;
            }

            Physics.SyncTransforms();
            return true;
        }

        // Each deposit lets go of its pin by the section's rule (SmokePins) before it goes: a pin
        // the section's pass added for it goes with it, and a pin of yours it took stays on the
        // map, so the deposit's destruction cannot take it off.
        private static void RemoveDeposits(Minimap map, SmokePins smokePins, List<TestDeposit> deposits)
        {
            foreach (var deposit in deposits)
            {
                if (StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(deposit.Id), out var pin))
                    smokePins.Remove(map, pin);
                RemoveTestObject(deposit.Go);
            }
            deposits.Clear();
        }

        // Once the deposits' colliders are on, a static pass that starts with a fresh scan of the
        // area pins the three deposits, through the entry point the minimap update calls, as
        // pin_static_loading_area_kept pins its deposit. Each deposit then leaves in its own way. The pass maps the real objects around
        // you as any pass does.
        private static void CheckDestroyedObjectPins(FeatureRun run, Player player, Minimap map,
            List<TestDeposit> deposits, List<Minimap.PinData> created)
        {
            foreach (var deposit in deposits)
            foreach (var collider in deposit.Colliders)
                if (collider)
                    collider.enabled = true;
            Physics.SyncTransforms();

            var lastScan = AccessTools.Field(typeof(StaticObjectMapping), "_lastCacheUpdateTime");
            var passId = AccessTools.Field(typeof(StaticObjectMapping), "_currentPassId");
            lastScan.SetValue(null, 0f);
            var before = (int)passId.GetValue(null);
            var smokePins = new SmokePins();
            global::Automatics.AutomaticMapping.AutomaticMapping.Mapping(player,
                MappingConfig.StaticObjectMappingInterval, false);
            if ((int)passId.GetValue(null) == before)
            {
                SkipDestroyedObjectChecks(run,
                    "the static pass did not run (the game is paused or the area around you is still loading); retry");
                return;
            }

            // A pin the pass added for a deposit is the section's own. A deposit can also take a
            // saved pin that was already on the map as its own; that pin is put back by the rule at
            // once, and the checks, which take the deposits' pins off the map, do not run.
            var pins = new Minimap.PinData[deposits.Count];
            var missing = false;
            var taken = false;
            for (var i = 0; i < deposits.Count; i++)
            {
                if (!StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(deposits[i].Id),
                        out pins[i]))
                    missing = true;
                else if (smokePins.Created(pins[i]))
                    created.Add(pins[i]);
                else
                {
                    taken = true;
                    smokePins.Remove(map, pins[i]);
                }
            }
            if (missing || taken)
            {
                SkipDestroyedObjectChecks(run, missing
                    ? "the static pass added no pin for a test deposit; the mineral category or unexplored automatic pins may be hidden, or your simulation distance does not reach the zones around it"
                    : "a test deposit took a saved pin that was already on the map as its own; move away from your saved deposit pins and retry");
                return;
            }

            CheckUsedUpDeposit(run, map, deposits[0].Go, deposits[0].Id, pins[0], created);
            CheckDestroyedByOtherGame(run, deposits[1].Go, deposits[1].Id, pins[1]);
            CheckUnloadedDeposit(run, deposits[2].Go, deposits[2].Id, pins[2]);
        }

        // The deposit goes as a used-up deposit removes itself: its owner destroys it through
        // ZNetView.Destroy, and the game sends the destruction on its next update. Here it is sent
        // at once, so the deposit's ZDO, persistent for this moment as a world object's is, is
        // gone before any save could see it. A pin you placed and a shared pin, both within 1 m
        // of the deposit's pin, stay.
        private static void CheckUsedUpDeposit(FeatureRun run, Minimap map, GameObject deposit, ZDOID id,
            Minimap.PinData pin, List<Minimap.PinData> created)
        {
            var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
            Minimap.PinData manual, shared;
            try
            {
                var side = Vector3.Cross(Vector3.up, deposit.transform.position - GameCamera.instance.transform.position);
                side = side.sqrMagnitude < 0.01f ? Vector3.right : side.normalized;
                manual = map.AddPin(pin.m_pos + side * 0.5f, Minimap.PinType.Icon3,
                    "Automatics smoke manual pin", true, false, 0L, PlatformUserID.None);
                shared = map.AddPin(pin.m_pos - side * 0.5f, Minimap.PinType.Icon3,
                    "Automatics smoke shared pin", true, false, long.MinValue, PlatformUserID.None);
            }
            finally
            {
                // Include the pins even if an AddPin postfix throws after adding them.
                created.AddRange(Map.GetAllPins().Where(x => !before.Contains(x)));
            }

            var nview = deposit.GetComponent<ZNetView>();
            try
            {
                nview.GetZDO().Persistent = true;
                nview.Destroy();
                SendDestroyedMethod.Invoke(ZDOMan.instance, null);
            }
            finally
            {
                var left = ZDOMan.instance.GetZDO(id);
                if (left != null) left.Persistent = false;
            }

            var identify = new StaticObjectMapping.MapPinIdentify(id);
            if (ZDOMan.instance.GetZDO(id) != null)
                run.Skip(DestroyedDepositCheck, "the deposit's ZDO was not destroyed, so this game did not own it");
            else if (!Map.GetAllPins().Contains(pin) && !StaticObjectMapping.TryGetCachedPin(identify, out _))
                run.Pass(DestroyedDepositCheck,
                    "the deposit's pin left the map and the static cache when the deposit's ZDO was destroyed");
            else
                run.Fail(DestroyedDepositCheck,
                    "the deposit's ZDO was destroyed, and its pin is still on the map or in the static cache");

            var pins = Map.GetAllPins();
            var gone = new List<string>();
            if (!pins.Contains(manual)) gone.Add("the pin you placed");
            if (!pins.Contains(shared)) gone.Add("the shared pin");
            if (gone.Count == 0)
                run.Pass(DestroyedKeepsOthersCheck,
                    "a pin you placed and a shared pin, within 1 m of the deposit's pin, are still on the map");
            else
                run.Fail(DestroyedKeepsOthersCheck,
                    $"the deposit's destruction removed {string.Join(" and ", gone)}");
        }

        // The deposit goes as one another game destroyed: its destruction arrives through the
        // game's handler of the routed DestroyZDO call, which removes the instance here. No
        // callback of the game that destroyed it runs in this game.
        private static void CheckDestroyedByOtherGame(FeatureRun run, GameObject deposit, ZDOID id,
            Minimap.PinData pin)
        {
            var package = new ZPackage();
            package.Write(1);
            package.Write(id);
            package.SetPos(0);
            var nview = deposit.GetComponent<ZNetView>();
            try
            {
                nview.GetZDO().Persistent = true;
                // The sender is not read; any other game's id does.
                ReceiveDestroyedMethod.Invoke(ZDOMan.instance, new object[] { long.MaxValue, package });
            }
            finally
            {
                var left = ZDOMan.instance.GetZDO(id);
                if (left != null) left.Persistent = false;
            }

            if (ZDOMan.instance.GetZDO(id) != null || nview.GetZDO() != null)
                run.Fail(DestroyedElsewhereCheck,
                    "the game did not destroy the deposit when its destruction arrived from another game");
            else if (!Map.GetAllPins().Contains(pin) &&
                     !StaticObjectMapping.TryGetCachedPin(new StaticObjectMapping.MapPinIdentify(id), out _))
                run.Pass(DestroyedElsewhereCheck,
                    "the deposit's pin left the map and the static cache when its destruction arrived from another game");
            else
                run.Fail(DestroyedElsewhereCheck,
                    "the deposit's destruction arrived from another game, and its pin is still on the map or in the static cache");
        }

        // The deposit goes as the game unloads an object whose zone leaves the active area
        // (ZNetScene.RemoveObjects), and the destruction that unloading leads to is sent at once:
        // the instance goes, and the deposit's ZDO, which is not persistent, is destroyed by its
        // owner, so the game raises its destruction as for any destroyed object. A world object's
        // persistent ZDO would stay instead. Unloading is not destruction, so the pin stays. The
        // finally destroys a ZDO the steps left, before the game could spawn the deposit from it
        // again.
        private static void CheckUnloadedDeposit(FeatureRun run, GameObject deposit, ZDOID id,
            Minimap.PinData pin)
        {
            var nview = deposit.GetComponent<ZNetView>();
            var zdo = nview.GetZDO();
            try
            {
                var instances = (Dictionary<ZDO, ZNetView>)SceneInstancesField.GetValue(ZNetScene.instance);
                nview.ResetZDO();
                deposit.SetActive(false);
                UnityEngine.Object.Destroy(deposit);
                if (!zdo.Persistent && zdo.IsOwner())
                    ZDOMan.instance.DestroyZDO(zdo);
                instances.Remove(zdo);
                SendDestroyedMethod.Invoke(ZDOMan.instance, null);

                var identify = new StaticObjectMapping.MapPinIdentify(id);
                if (ZDOMan.instance.GetZDO(id) != null)
                    run.Skip(UnloadedDepositCheck,
                        "the unloaded deposit's ZDO was not destroyed, so this game did not own it");
                else if (Map.GetAllPins().Contains(pin) &&
                         StaticObjectMapping.TryGetCachedPin(identify, out var cached) && ReferenceEquals(cached, pin))
                    run.Pass(UnloadedDepositCheck,
                        "the deposit's pin stayed on the map and in the static cache when the deposit was unloaded and the destruction of its non-persistent ZDO was raised");
                else
                    run.Fail(UnloadedDepositCheck, "unloading the deposit removed its pin");
            }
            finally
            {
                var left = ZDOMan.instance.GetZDO(id);
                if (left != null)
                {
                    if (!left.IsOwner()) left.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.DestroyZDO(left);
                    SendDestroyedMethod.Invoke(ZDOMan.instance, null);
                }
            }
        }

        private static bool FirstHit(Player player, Ray ray, out RaycastHit hit)
        {
            var mask = (int)AccessTools.Field(typeof(Player), "m_interactMask").GetValue(player);
            var hits = Physics.RaycastAll(ray, MappingConfig.PinObjectRange, mask)
                .Where(x => !x.collider.transform.IsChildOf(player.transform)).OrderBy(x => x.distance).ToArray();
            hit = hits.Length == 0 ? default(RaycastHit) : hits[0];
            return hits.Length != 0;
        }

        private static Minimap.PinData Invoke(Player player, Ray ray, List<Minimap.PinData> created)
        {
            var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
            try
            {
                return ObjectPinning.TryPinFromRay(player, ray);
            }
            finally
            {
                // Include a pin even if a message or an AddPin postfix throws after adding it.
                created.AddRange(Map.GetAllPins().Where(pin => !before.Contains(pin)));
            }
        }
    }
}

namespace Automatics.SmokeTest
{
    // Ray actions use a non-persistent creature and drop. The death checks wait for the live
    // death flag and the owner's ZDO destruction, then restore the navigation and shortcut.
    internal static class NavHotkeyFeatureSmoke
    {
        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            var map = Minimap.instance;
            if (!run.IsCurrent || !player || !map || !GameCamera.instance || !ZNetScene.instance ||
                ZDOMan.instance == null || !InventoryGui.instance || !ZNet.instance || !ZNet.instance.IsServer() ||
                ZNet.instance.GetPeerConnections() > 0 || Localization.instance == null)
            {
                run.Skip("navhotkey_section", "requires a loaded single-player world, minimap, inventory and camera");
                yield break;
            }
            if (!MappingModule.Running)
            {
                run.Skip("navhotkey_section", "Automatic Mapping is not running; enable the module first");
                yield break;
            }
            var creaturePrefab = ZNetScene.instance.GetPrefab("Boar");
            var dropPrefab = ZNetScene.instance.GetPrefab("Wood");
            if (!creaturePrefab || !dropPrefab)
            {
                run.Skip("navhotkey_section", "the test creature or Wood prefab is unavailable");
                yield break;
            }
            var key = MappingConfig.NavigateToAimedObjectKeyEntry;
            if (!SmokeSession.BeginSetting(run.RunId, key, out var keySetting, out var refusal) ||
                !SmokeSession.WriteSetting(run.RunId, keySetting,
                    new BepInEx.Configuration.KeyboardShortcut(KeyCode.F8), out refusal))
            {
                run.Skip("navhotkey_section", refusal);
                yield break;
            }

            var original = Navigation.CaptureTarget();
            var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
            var smokePins = new SmokePins();
            var added = new List<Minimap.PinData>();
            var createdMarkers = new HashSet<Minimap.PinData>();
            var creatureId = ZDOID.None;
            Action<Action> observe = action =>
            {
                var pins = new HashSet<Minimap.PinData>(Map.GetAllPins());
                try { action(); }
                finally
                {
                    createdMarkers.UnionWith(Map.GetAllPins().Where(pin => !pins.Contains(pin) &&
                        !pin.m_save && !Map.IsAutomaticPin(pin)));
                }
            };
            Action<Player, Ray, bool> navigate = (targetPlayer, ray, takeInput) =>
                observe(() => Navigation.TryNavigateFromRay(targetPlayer, ray, takeInput));
            Func<string, bool> handBack = _ =>
            {
                Navigation.ClearTarget();
                foreach (var pin in createdMarkers) smokePins.Remove(map, pin);
                foreach (var pin in added) smokePins.Remove(map, pin);
                smokePins.Remove(map, DynamicObjectMapping.GetCachedPin(creatureId));
                if (map) Navigation.RestoreTarget(original);
                return true;
            };
            SmokeSession.TrackHandBack(handBack);
            GameObject creature = null;
            ItemDrop drop = null;
            try
            {
                var camera = GameCamera.instance.transform;
                creature = UnityEngine.Object.Instantiate(creaturePrefab,
                    camera.position + camera.forward * 5f + camera.right * 3f, Quaternion.identity);
                SmokeSession.TrackSpawn(creature);
                var nview = creature.GetComponent<ZNetView>();
                if (!nview || !nview.IsValid())
                {
                    run.Skip("navhotkey_section", "the spawned creature has no ZDO");
                    yield break;
                }
                nview.GetZDO().Persistent = false;
                var character = creature.GetComponent<Character>();
                var id = nview.GetZDO().m_uid;
                creatureId = id;
                drop = FeaturesSmokeCommand.SpawnTestDrop(dropPrefab,
                    camera.position + camera.forward * 5f - camera.right * 3f, false);
                Physics.SyncTransforms();
                if (!drop || !TryRay(player, creature, out var creatureRay) ||
                    !TryRay(player, drop.gameObject, out var dropRay))
                {
                    run.Skip("navhotkey_section", "the camera rays to the test objects are obstructed");
                    yield break;
                }

                Navigation.ClearTarget();
                var name = Localization.instance.Localize(Objects.GetName(character));
                var control = map.AddPin(creature.transform.position + Vector3.right * 0.5f,
                    Minimap.PinType.Icon3, name, true, false);
                added.Add(control);
                AccessTools.Method(typeof(DynamicObjectMapping), "AddPin").Invoke(null, new object[]
                {
                    id, creature.transform.position, name,
                    new Target { name = Objects.GetName(character), prefabName = "Boar" }
                });
                var automatic = DynamicObjectMapping.GetCachedPin(id);
                if (automatic == null)
                    run.Skip("navhotkey_exact_pin", "automatic pins are hidden at the test creature's position");
                else
                {
                    added.Add(automatic);
                    navigate(player, creatureRay, true);
                    Check(run, "navhotkey_exact_pin", ReferenceEquals(Navigation.TargetPin, automatic) &&
                        !ReferenceEquals(Navigation.TargetPin, control) && MarkerCount(before, added) == 0,
                        "the creature's own automatic pin is selected, with no temporary marker");
                    navigate(player, dropRay, true);
                    var replaced = Navigation.TemporaryMarker;
                    navigate(player, creatureRay, true);
                    Check(run, "navhotkey_replace_with_pin", ReferenceEquals(Navigation.TargetPin, automatic) &&
                        MarkerCount(before, added) == 0 && !Map.GetAllPins().Contains(replaced),
                        "switching from a temporary marker to the automatic pin removes the marker");
                    var mapClickDetail = "navigation_start_key is unbound";
                    if (MappingConfig.NavigationStartKey.MainKey == KeyCode.None)
                        run.Skip("navhotkey_map_creature", "navigation_start_key is unbound");
                    else
                    {
                        Navigation.ClearTarget();
                        var clicked = GuideFeatureSmoke.ClickPin(map, automatic, out mapClickDetail);
                        Check(run, "navhotkey_map_creature", clicked && ReferenceEquals(Navigation.TargetPin, automatic) &&
                            Navigation.CaptureTarget().CreatureId == id,
                            $"map-click navigation stores the creature's ZDOID; {mapClickDetail}");
                    }
                    character.transform.position += camera.up * 2f;
                    Physics.SyncTransforms();
                    AccessTools.Field(typeof(Navigation), "_nextTargetPinValidationTime")
                        .SetValue(null, Time.time + 0.5f);
                    Navigation.Update(player);
                    Check(run, "navhotkey_pin_follows", Navigation.CaptureTarget().CreatureId == id &&
                        Vector3.Distance(Navigation.TargetPosition, character.transform.position) < 0.01f,
                        $"the pinned creature's destination follows before the next validation tick; {mapClickDetail}");
                    character.transform.position -= camera.up * 2f;
                    Physics.SyncTransforms();
                    Validate();
                    smokePins.Remove(map, automatic);
                    Check(run, "navhotkey_removed_pin_kept", Navigation.CaptureTarget().CreatureId == id &&
                        MarkerCount(before, added) == 1 && Vector3.Distance(Navigation.TargetPosition, character.transform.position) < 0.01f,
                        $"removing a tracked creature's dynamic pin leaves a temporary destination; {mapClickDetail}");
                    Navigation.ClearTarget();
                }

                CheckHiddenCategory(run, player, map, drop, dropRay, navigate, added);
                navigate(player, dropRay, true);
                var marker = Navigation.TemporaryMarker;
                Check(run, "navhotkey_temporary", marker != null && ReferenceEquals(Navigation.TargetPin, marker) &&
                    !marker.m_save && !Map.IsAutomaticPin(marker) && MarkerCount(before, added) == 1 &&
                    marker.m_name == Localization.instance.Localize(Objects.GetName(drop)) &&
                    Vector3.Distance(marker.m_pos, drop.transform.position) < 0.01f,
                    "an unpinned object gets exactly one named, unsaved, non-automatic marker");
                CheckSave(run, map, marker, control);
                var distanceMode = map.m_mode;
                try
                {
                    map.SetMapMode(Minimap.MapMode.Small);
                    Navigation.Update(player);
                    var label = Navigation.Label;
                    var textType = AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
                    var text = label ? (string)AccessTools.Property(textType, "text").GetValue(
                        label.transform.Find("Name").GetComponent(textType)) : "";
                    var suffix = " [" + Mathf.RoundToInt(Utils.DistanceXZ(player.transform.position, marker.m_pos)) + "m]";
                    Check(run, "navhotkey_distance_format", text == marker.m_name + suffix,
                        "navigation label did not append the bracketed horizontal distance: " + text);
                }
                finally { map.SetMapMode(distanceMode); }
                navigate(player, dropRay, true);
                Check(run, "navhotkey_repeat_clears", Navigation.TargetPin == null && MarkerCount(before, added) == 0 &&
                    !Map.GetAllPins().Contains(marker), "repeating the target removes its marker and clears navigation");

                navigate(player, dropRay, true);
                marker = Navigation.TemporaryMarker;
                navigate(player, creatureRay, true);
                Check(run, "navhotkey_replacement", MarkerCount(before, added) == 1 && !Map.GetAllPins().Contains(marker) &&
                    Navigation.CaptureTarget().CreatureId == id,
                    "switching to the unpinned creature removes the old marker and retains its identity");
                character.transform.position += camera.up * 2f;
                Physics.SyncTransforms();
                Validate();
                Check(run, "navhotkey_temporary_follows", MarkerCount(before, added) == 1 &&
                    Vector3.Distance(Navigation.TargetPosition, character.transform.position) < 0.01f &&
                    Vector3.Distance(Navigation.TemporaryMarker.m_pos, character.transform.position) < 0.01f,
                    "both the destination and temporary marker follow the creature");

                // The creature comes within the dynamic mapping range while it is followed by a marker.
                var followMarker = Navigation.TemporaryMarker;
                AccessTools.Method(typeof(DynamicObjectMapping), "AddPin").Invoke(null, new object[]
                {
                    id, creature.transform.position, name,
                    new Target { name = Objects.GetName(character), prefabName = "Boar" }
                });
                var adopted = DynamicObjectMapping.GetCachedPin(id);
                if (adopted == null)
                    run.Skip("navhotkey_marker_adopts_pin", "automatic pins are hidden at the test creature's position");
                else
                {
                    Validate();
                    Check(run, "navhotkey_marker_adopts_pin", ReferenceEquals(Navigation.TargetPin, adopted) &&
                        Navigation.TemporaryMarker == null && !Map.GetAllPins().Contains(followMarker) &&
                        MarkerCount(before, added) == 0 && Navigation.CaptureTarget().CreatureId == id,
                        "the creature's new automatic pin replaces the marker that followed it, which is removed");
                    smokePins.Remove(map, adopted);
                    Validate();
                    Check(run, "navhotkey_marker_returns", Navigation.TemporaryMarker != null &&
                        MarkerCount(before, added) == 1 && Navigation.CaptureTarget().CreatureId == id &&
                        Vector3.Distance(Navigation.TargetPosition, character.transform.position) < 0.01f,
                        "removing the adopted pin puts a marker back on the same creature");
                }

                var creatureTarget = Navigation.CaptureTarget();
                if (MappingConfig.NavigationStartKey.MainKey == KeyCode.None)
                    run.Skip("navhotkey_creature_marker_to_pin", "navigation_start_key is unbound");
                else
                {
                    var clicked = false;
                    var clickDetail = "";
                    observe(() => clicked = GuideFeatureSmoke.ClickPin(map, control, out clickDetail));
                    Check(run, "navhotkey_creature_marker_to_pin", clicked &&
                        ReferenceEquals(Navigation.TargetPin, control) && MarkerCount(before, added) == 0,
                        $"map-clicking a pinned target from a creature marker leaves no extra unsaved pin; {clickDetail}");
                    observe(() => Navigation.RestoreTarget(creatureTarget));
                }

                var target = Navigation.TargetPin;
                var empty = new Ray(new Vector3(0f, 10000f, 0f), Vector3.up);
                navigate(player, empty, true);
                CheckUnchanged(run, "navhotkey_empty", target, before, added);
                SmokeSession.WriteSetting(run.RunId, keySetting, new BepInEx.Configuration.KeyboardShortcut(), out refusal);
                navigate(player, dropRay, true);
                CheckUnchanged(run, "navhotkey_unbound", target, before, added);
                SmokeSession.WriteSetting(run.RunId, keySetting,
                    new BepInEx.Configuration.KeyboardShortcut(KeyCode.F8), out refusal);
                var stopped = MappingModule.StopRequested;
                try
                {
                    MappingModule.StopRequested = true;
                    navigate(player, dropRay, true);
                    CheckUnchanged(run, "navhotkey_stopped", target, before, added);
                }
                finally { MappingModule.StopRequested = stopped; }

                var gui = InventoryGui.instance;
                var inventoryOpen = InventoryGui.IsVisible();
                try
                {
                    if (!inventoryOpen) gui.Show(null);
                    var takeInput = (bool)AccessTools.Method(typeof(Player), "TakeInput").Invoke(player, null);
                    navigate(player, dropRay, takeInput);
                    Check(run, "navhotkey_blocked", InventoryGui.IsVisible() && !takeInput &&
                        ReferenceEquals(Navigation.TargetPin, target) && MarkerCount(before, added) == 1,
                        "an open inventory blocks the same input gate the shortcut uses");
                }
                finally { if (!inventoryOpen) gui.Hide(); }

                // Keep the test creature alive long enough to observe its real death flag,
                // without death effects or drops preceding the separate destruction check.
                character.GetComponent<BaseAI>().enabled = false;
                character.m_deathAnimation = true;
                character.m_deathAnimationStartDelay = 10f;
                character.SetHealth(0f);
                var deadline = Time.realtimeSinceStartup + 5f;
                while (run.IsCurrent && character && !character.IsDead() &&
                       Time.realtimeSinceStartup < deadline)
                    yield return new WaitForFixedUpdate();
                if (!run.IsCurrent) yield break;
                var dead = character && character.IsDead();
                Validate();
                Check(run, "navhotkey_dead_clears", dead && ZDOMan.instance.GetZDO(id) != null &&
                    Navigation.TargetPin == null && MarkerCount(before, added) == 0,
                    "the live death flag clears navigation before the creature's ZDO is destroyed");

                observe(() => Navigation.RestoreTarget(creatureTarget));
                var destroyMarker = Navigation.TemporaryMarker;
                var destroyTargetActive = destroyMarker != null &&
                    Navigation.CaptureTarget().CreatureId == id && MarkerCount(before, added) == 1;
                if (!nview.IsOwner()) nview.ClaimOwnership();
                ZNetScene.instance.Destroy(creature);
                deadline = Time.realtimeSinceStartup + 5f;
                while (run.IsCurrent && ZDOMan.instance.GetZDO(id) != null &&
                       Time.realtimeSinceStartup < deadline)
                    yield return null;
                if (!run.IsCurrent) yield break;
                Check(run, "navhotkey_destroyed_clears", destroyTargetActive &&
                    ZDOMan.instance.GetZDO(id) == null && Navigation.TargetPin == null &&
                    MarkerCount(before, added) == 0 && !Map.GetAllPins().Contains(destroyMarker),
                    "owner destruction of the target ZDO clears navigation and removes every temporary marker");
                SmokeSession.ReleaseSpawn(creature);
            }
            finally
            {
                try
                {
                    if (handBack("navigation hotkey smoke")) SmokeSession.ReleaseHandBack(handBack);
                }
                finally
                {
                    FeaturesSmokeCommand.RemoveTestDrop(drop, "navigation hotkey smoke");
                    if (creature && SmokeSession.DestroyTestObject(creature, 0, "navigation hotkey smoke"))
                        SmokeSession.ReleaseSpawn(creature);
                    var restored = SmokeSession.RestoreSettings(run.RunId);
                    Check(run, "navhotkey_settings_restored", restored.All(x =>
                            x.Key == SmokeSession.ToggleOutcome.Restored ||
                            x.Key == SmokeSession.ToggleOutcome.AlreadyOriginal ||
                            x.Key == SmokeSession.ToggleOutcome.NothingWritten),
                        string.Join("; ", restored.Select(x => x.Value)));
                }
            }
        }

        private static void CheckHiddenCategory(FeatureRun run, Player player, Minimap map,
            ItemDrop drop, Ray ray, Action<Player, Ray, bool> navigate, List<Minimap.PinData> added)
        {
            if (!Objects.GetZdoid(drop, out var id))
            {
                run.Fail("navhotkey_hidden_category_kept", "test-drop identity precondition failed: no ZDOID");
                return;
            }
            var changes = new[]
            {
                new KeyValuePair<BepInEx.Configuration.ConfigEntryBase, object>(MappingConfig.EnableMapEnhancementsEntry, true),
                new KeyValuePair<BepInEx.Configuration.ConfigEntryBase, object>(MappingConfig.MapFilterHiddenCategoriesEntry,
                    new StringList(new[] { "Other" })),
                new KeyValuePair<BepInEx.Configuration.ConfigEntryBase, object>(MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false)
            };
            var settings = new List<SmokeSession.SettingRestoration>();
            foreach (var change in changes)
            {
                if (!SmokeSession.BeginSetting(run.RunId, change.Key, out var setting, out var refusal))
                {
                    run.Fail("navhotkey_hidden_category_kept", "filter setting precondition failed: " + refusal);
                    return;
                }
                settings.Add(setting);
            }
            var smokePins = new SmokePins();
            Minimap.PinData automatic = null;
            try
            {
                for (var i = 0; i < changes.Length; i++)
                    if (!SmokeSession.WriteSetting(run.RunId, settings[i], changes[i].Value, out var refusal))
                    {
                        run.Fail("navhotkey_hidden_category_kept", "filter write precondition failed: " + refusal);
                        return;
                    }
                Navigation.ClearTarget();
                var name = Objects.GetName(drop);
                automatic = Map.AddPin(drop.transform.position, name, false, new Target { name = name });
                added.Add(automatic);
                AccessTools.Method(typeof(StaticObjectMapping), "CachePin", new[]
                {
                    typeof(StaticObjectMapping.MapPinIdentify), typeof(Minimap.PinData), typeof(PinKind),
                    typeof(string), typeof(string), typeof(PinSourceDomain)
                }).Invoke(null, new object[]
                {
                    new StaticObjectMapping.MapPinIdentify(id), automatic, PinKind.Other,
                    "smoke", "smoke", PinSourceDomain.Component
                });
                var hidden = Map.ShouldHideAutomaticPin(automatic);
                navigate(player, ray, true);
                var marker = Navigation.TemporaryMarker;
                var started = marker != null && !ReferenceEquals(marker, automatic) &&
                    ReferenceEquals(Navigation.TargetPin, marker) && !marker.m_save && !Map.IsAutomaticPin(marker);
                for (var i = 0; i < 3; i++) Validate();
                Check(run, "navhotkey_hidden_category_kept", hidden && started &&
                    ReferenceEquals(Navigation.TargetPin, marker) && Map.ContainsPin(marker) &&
                    Navigation.CaptureTarget().CreatureId == ZDOID.None,
                    "a non-creature's hidden Other pin uses a temporary marker and stays navigating through three validation ticks; " +
                    $"hidden-pin precondition={hidden}, temporary marker started={started}");
            }
            finally
            {
                Navigation.ClearTarget();
                smokePins.Remove(map, automatic);
                foreach (var setting in settings)
                    if (!SmokeSession.WriteSetting(run.RunId, setting, setting.Original, out var refusal))
                        run.Fail("navhotkey_hidden_category_settings", "filter restoration failed: " + refusal);
            }
        }

        private static bool TryRay(Player player, GameObject instance, out Ray ray)
        {
            foreach (var collider in instance.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled) continue;
                var origin = GameCamera.instance.transform.position;
                ray = new Ray(origin, collider.bounds.center - origin);
                if (ObjectPinning.TryResolveFromRay(player, ray, out var aimed) && aimed.Instance == instance)
                    return true;
            }
            ray = default;
            return false;
        }

        private static int MarkerCount(HashSet<Minimap.PinData> before, List<Minimap.PinData> added)
        {
            return Map.GetAllPins().Count(pin => !pin.m_save && !Map.IsAutomaticPin(pin) &&
                !before.Contains(pin) && !added.Contains(pin));
        }

        private static void Validate()
        {
            AccessTools.Field(typeof(Navigation), "_nextTargetPinValidationTime").SetValue(null, 0f);
            AccessTools.Method(typeof(Navigation), "ValidateTargetPin").Invoke(null, null);
        }

        private static void CheckUnchanged(FeatureRun run, string name, Minimap.PinData target,
            HashSet<Minimap.PinData> before, List<Minimap.PinData> added)
        {
            Check(run, name, ReferenceEquals(Navigation.TargetPin, target) && MarkerCount(before, added) == 1,
                "the current destination and single temporary marker are unchanged");
        }

        private static void Check(FeatureRun run, string name, bool passed, string detail)
        {
            if (passed) run.Pass(name, detail);
            else run.Fail(name, detail);
        }

        private static void CheckSave(FeatureRun run, Minimap map, Minimap.PinData marker, Minimap.PinData control)
        {
            var data = (byte[])AccessTools.Method(typeof(Minimap), "GetMapData").Invoke(map, null);
            var outer = new ZPackage(data);
            var version = outer.ReadInt();
            var pins = outer.ReadCompressedPackage();
            var size = pins.ReadInt();
            for (var i = 0; i < size * size * 2; i++) pins.ReadBool();
            var count = pins.ReadInt();
            var hasMarker = false;
            var hasControl = false;
            for (var i = 0; i < count; i++)
            {
                var name = pins.ReadString();
                var position = pins.ReadVector3();
                var type = pins.ReadInt();
                pins.ReadBool();
                pins.ReadLong();
                pins.ReadString();
                if (marker != null && name == marker.m_name && position == marker.m_pos && type == (int)marker.m_type)
                    hasMarker = true;
                if (name == control.m_name && position == control.m_pos && type == (int)control.m_type)
                    hasControl = true;
            }
            Check(run, "navhotkey_not_saved", version == 8 && marker != null && !hasMarker && hasControl,
                $"temporary marker in serialized map={hasMarker}, saved control={hasControl}");
        }
    }
}

namespace Automatics.SmokeTest
{
    internal static class AimBarFeatureSmoke
    {
        private const float TestRange = 20f;
        private const float HudStep = 0.25f;
        private static readonly System.Reflection.FieldInfo HudsField =
            AccessTools.Field(typeof(EnemyHud), "m_huds");
        private static readonly System.Reflection.MethodInfo LateUpdate =
            AccessTools.Method(typeof(EnemyHud), "LateUpdate");
        private static readonly System.Reflection.MethodInfo UpdateHuds =
            AccessTools.Method(typeof(EnemyHud), "UpdateHuds");

        internal static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !player.IsOwner() || !Minimap.instance || !EnemyHud.instance ||
                !GameCamera.instance || !ZNetScene.instance || Localization.instance == null ||
                !ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("aimbar_section", "requires a loaded single-player world, local owner, minimap, enemy HUD and camera");
                yield break;
            }
            if (!MappingModule.Running)
            {
                run.Skip("aimbar_section", "Automatic Mapping is not running; enable the module first");
                yield break;
            }
            if (Game.IsPaused() || !Hud.instance || Hud.IsUserHidden() ||
                Minimap.instance.m_mode == Minimap.MapMode.Large)
            {
                run.Skip("aimbar_section", "the game must be unpaused, HUD visible and large map closed");
                yield break;
            }

            var original = Navigation.CaptureTarget();
            var camera = GameCamera.instance.transform;
            var rotation = camera.rotation;
            var position = camera.position;
            var spawned = new List<GameObject>();
            var cleanup = new Func<string, bool>(_ =>
            {
                if (camera) camera.SetPositionAndRotation(position, rotation);
                AimedObjectBar.Cleanup();
                if (Minimap.instance) Navigation.RestoreTarget(original);
                return true;
            });
            SmokeSession.TrackHandBack(cleanup);
            try
            {
                if (!SmokeSession.BeginSetting(run.RunId, MappingConfig.EnableAimedObjectBarEntry,
                        out var enabled, out var refusal) ||
                    !SmokeSession.BeginSetting(run.RunId, MappingConfig.AimedObjectBarRangeEntry,
                        out var range, out refusal) ||
                    !SmokeSession.BeginSetting(run.RunId, MappingConfig.PinObjectRangeEntry,
                        out var rayRange, out refusal) ||
                    !SmokeSession.WriteSetting(run.RunId, enabled, true, out refusal) ||
                    !SmokeSession.WriteSetting(run.RunId, range, TestRange, out refusal) ||
                    !SmokeSession.WriteSetting(run.RunId, rayRange, 100f, out refusal))
                {
                    run.Fail("aimbar_settings", "setting ownership precondition failed: " + refusal);
                    yield break;
                }
                Navigation.ClearTarget();
                var forward = Vector3.ProjectOnPlane(camera.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.01f) forward = player.transform.forward;
                var near = position + forward * 5f;
                var far = position + forward * 45f;

                foreach (var prefabName in new[] { "Boar", "woodwall", "Pickable_Mushroom" })
                {
                    var go = Spawn(prefabName, near, Quaternion.LookRotation(-forward), spawned, out var error);
                    if (!go)
                    {
                        run.Fail("aimbar_" + prefabName + "_setup", error);
                        continue;
                    }
                    var character = go.GetComponent<Character>();
                    var piece = go.GetComponent<Piece>();
                    var expectedHealth = 1f;
                    if (character)
                    {
                        character.SetHealth(character.GetMaxHealth() * 0.4f);
                        expectedHealth = character.GetHealthPercentage();
                    }
                    if (piece)
                    {
                        piece.SetCreator(player.GetPlayerID(), default);
                        var wear = piece.GetComponent<WearNTear>();
                        if (!piece.IsPlacedByPlayer() || !wear)
                        {
                            run.Fail("aimbar_piece_setup", "the test piece must have a player creator and WearNTear");
                            Remove(go);
                            continue;
                        }
                        wear.m_noSupportWear = true;
                        var health = wear.m_health * 0.4f;
                        go.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_health, health);
                        AccessTools.Method(typeof(WearNTear), "RPC_HealthChanged")
                            .Invoke(wear, new object[] { 0L, health });
                        expectedHealth = wear.GetHealthPercentage();
                        Check(run, "aimbar_piece_health_source", Mathf.Abs(expectedHealth - 0.4f) < 0.01f,
                            $"player-built piece health precondition: expected 0.4, got {expectedHealth:F3}");
                    }
                    var name = character ? "creature" : piece ? "piece" : "pickable";
                    if (!AimAndUpdate(player, go, out error))
                    {
                        run.Fail("aimbar_" + name + "_near", error);
                        Remove(go);
                        continue;
                    }
                    CheckBar(run, player, go, "aimbar_" + name + "_near", expectedHealth);

                    if (!SmokeSession.WriteSetting(run.RunId, enabled, false, out refusal))
                        throw new InvalidOperationException(refusal);
                    Navigation.Update(player);
                    Check(run, "aimbar_" + name + "_off", !AimedObjectBar.Label &&
                        !AimedObjectBar.GetHudTarget(),
                        "option off clears the aimed label and releases the vanilla HUD target");
                    if (!SmokeSession.WriteSetting(run.RunId, enabled, true, out refusal))
                        throw new InvalidOperationException(refusal);

                    go.transform.position = far;
                    Physics.SyncTransforms();
                    if (!AimAndUpdate(player, go, out error)) run.Fail("aimbar_" + name + "_far", error);
                    else Check(run, "aimbar_" + name + "_far", Utils.DistanceXZ(player.transform.position, far) > TestRange &&
                        !LabelVisible() && !AimedObjectBar.GetHudTarget(),
                        "the resolver still hits the object, but the bar is hidden beyond the horizontal limit");
                    go.transform.position = near;
                    Physics.SyncTransforms();
                    if (!AimAndUpdate(player, go, out error)) run.Fail("aimbar_" + name + "_return", error);
                    else CheckBar(run, player, go, "aimbar_" + name + "_return", expectedHealth);

                    if (character)
                    {
                        CheckAimedCreatureRange(run, player, character, forward, range);
                        CheckCreatures(run, player, character, forward, spawned);
                    }
                    else if (piece) CheckSamePiece(run, player, go);
                    camera.rotation = Quaternion.LookRotation(Vector3.up, forward);
                    var empty = new Ray(camera.position, camera.forward);
                    var hit = ObjectPinning.TryResolveFromRay(player, empty, out _);
                    Navigation.ClearTarget();
                    Navigation.Update(player);
                    Check(run, "aimbar_" + name + "_away", !hit && !LabelVisible() &&
                        !AimedObjectBar.GetHudTarget(),
                        hit ? "empty-aim precondition failed: upward camera ray hit an object" : "aiming away hides the bar");
                    Remove(go);
                }
                CheckBareTerrain(run, player, forward);
                camera.SetPositionAndRotation(position, rotation);
            }
            finally
            {
                try
                {
                    foreach (var go in spawned) Remove(go);
                    if (cleanup("aimed bar smoke")) SmokeSession.ReleaseHandBack(cleanup);
                }
                finally
                {
                    var restored = SmokeSession.RestoreSettings(run.RunId);
                    Check(run, "aimbar_settings_restored", restored.All(x =>
                        x.Key == SmokeSession.ToggleOutcome.Restored || x.Key == SmokeSession.ToggleOutcome.NothingWritten ||
                        x.Key == SmokeSession.ToggleOutcome.AlreadyOriginal),
                        string.Join("; ", restored.Select(x => x.Value)));
                }
            }
        }

        private static void CheckBareTerrain(FeatureRun run, Player player, Vector3 forward)
        {
            Navigation.ClearTarget();
            var camera = GameCamera.instance.transform;
            var position = camera.position;
            var rotation = camera.rotation;
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    var angle = i * Mathf.PI / 4f;
                    var origin = player.transform.position + Vector3.up * 5f +
                        new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 3f;
                    var ray = new Ray(origin, Vector3.down);
                    if (!Physics.Raycast(ray, out var hit, MappingConfig.PinObjectRange, LayerMask.GetMask("terrain")) ||
                        !hit.collider.GetComponentInParent<Heightmap>() ||
                        !ObjectPinning.TryResolveFromRay(player, ray, out var aimed) ||
                        !aimed.Instance.GetComponentInParent<Heightmap>() ||
                        aimed.Component || aimed.Location || !string.IsNullOrEmpty(aimed.Name)) continue;
                    camera.SetPositionAndRotation(origin, Quaternion.LookRotation(Vector3.down, forward));
                    Navigation.Update(player);
                    var selected = AccessTools.Field(typeof(AimedObjectBar), "_aimed").GetValue(null);
                    Check(run, "aimbar_bare_terrain", !LabelVisible() && !AimedObjectBar.GetHudTarget() && selected == null,
                        "an unobstructed bare terrain hit selects no aimed object and shows no bar");
                    return;
                }
                run.Fail("aimbar_bare_terrain", "terrain precondition failed: no nearby downward ray hits bare terrain without a component, name or location");
            }
            finally { camera.SetPositionAndRotation(position, rotation); }
        }

        private static void CheckAimedCreatureRange(FeatureRun run, Player player, Character creature, Vector3 forward,
            SmokeSession.SettingRestoration range)
        {
            Navigation.ClearTarget();
            var position = creature.transform.position;
            try
            {
                // Just beyond the vanilla creature bar's reach, with the bar range widened to cover it
                // for this check only.
                var reach = EnemyHud.instance.m_maxShowDistance + 5f;
                if (!SmokeSession.WriteSetting(run.RunId, range, reach + 10f, out var refusal))
                {
                    run.Fail("aimbar_creature_extended_range", "setting ownership precondition failed: " + refusal);
                    return;
                }
                var target = player.transform.position + forward * reach;
                target.y = position.y;
                creature.transform.position = target;
                Physics.SyncTransforms();
                var distance = Vector3.Distance(player.transform.position, target);
                var horizontal = Utils.DistanceXZ(player.transform.position, target);
                if (distance <= EnemyHud.instance.m_maxShowDistance || horizontal > MappingConfig.AimedObjectBarRange)
                {
                    run.Fail("aimbar_creature_extended_range",
                        $"distance precondition failed: creature at {distance:F1} m (horizontal {horizontal:F1} m), vanilla range {EnemyHud.instance.m_maxShowDistance:F1} m, bar range {MappingConfig.AimedObjectBarRange:F1} m");
                    return;
                }
                if (!AimAndUpdate(player, creature.gameObject, out var error))
                {
                    run.Fail("aimbar_creature_extended_range", error);
                    return;
                }
                var data = HudData(creature);
                Check(run, "aimbar_creature_extended_range", Navigation.GetHudTarget() == null &&
                    AimedObjectBar.GetHudTarget() == creature && data != null && HudVisible(data) &&
                    HudName(data).EndsWith(DistanceText(player, creature.gameObject)) && !LabelVisible(),
                    "the aimed creature beyond vanilla range has a visible real HUD with distance, without navigation or a cloned label");
            }
            finally
            {
                creature.transform.position = position;
                SmokeSession.WriteSetting(run.RunId, range, TestRange, out _);
                Physics.SyncTransforms();
            }
        }

        private static void CheckCreatures(FeatureRun run, Player player, Character first, Vector3 forward,
            List<GameObject> spawned)
        {
            var navigation = new Navigation.TargetState
            {
                Pin = new Minimap.PinData { m_type = Minimap.PinType.Icon3, m_name = first.GetHoverName() },
                Temporary = true,
                CreatureId = first.GetComponent<ZNetView>().GetZDO().m_uid,
                LastPosition = first.transform.position
            };
            Navigation.RestoreTarget(navigation);
            if (!AimAndUpdate(player, first.gameObject, out var error))
            {
                run.Fail("aimbar_same_creature", error);
                return;
            }
            var data = HudData(first);
            Check(run, "aimbar_same_creature", data != null && HudVisible(data) && !LabelVisible() &&
                NamedLabels() == 0 && HudCount(first) == 1,
                "the same creature has exactly one real HUD and no cloned label");
            if (data == null)
            {
                run.Fail("aimbar_same_creature_timer", "the shared creature's real HUD must exist before saving its timer");
                return;
            }
            var hover = HoverField(data);
            hover.SetValue(data, 81f);
            UpdateHuds.Invoke(EnemyHud.instance, new object[] { player, null, HudStep });
            Check(run, "aimbar_same_creature_timer", Mathf.Abs((float)hover.GetValue(data) - (81f + HudStep)) < 0.01f,
                "a shared creature is processed once and its natural hover timer advances once");

            var camera = GameCamera.instance.transform;
            var second = Spawn("Boar", camera.position + forward * 35f + Vector3.Cross(Vector3.up, forward) * 5f,
                Quaternion.identity, spawned, out error);
            if (!second)
            {
                run.Fail("aimbar_two_creatures", error);
                return;
            }
            var other = second.GetComponent<Character>();
            navigation.CreatureId = second.GetComponent<ZNetView>().GetZDO().m_uid;
            navigation.LastPosition = second.transform.position;
            navigation.Pin.m_name = other.GetHoverName();
            Navigation.RestoreTarget(navigation);
            if (!AimAndUpdate(player, first.gameObject, out error))
            {
                run.Fail("aimbar_two_creatures", error);
                Remove(second);
                return;
            }
            var otherData = HudData(other);
            var beyond = Vector3.Distance(player.transform.position, other.transform.position) > EnemyHud.instance.m_maxShowDistance;
            Check(run, "aimbar_two_creatures", beyond && data != null && otherData != null &&
                HudVisible(data) && HudVisible(otherData) && !LabelVisible() && NamedLabels() == 0,
                $"two real HUDs are visible; second creature beyond vanilla range precondition={beyond}");
            if (data == null || otherData == null)
            {
                run.Fail("aimbar_two_timers", "both real HUD data entries must exist before saving their hover timers");
            }
            else
            {
                var otherHover = HoverField(otherData);
                hover.SetValue(data, 83f);
                otherHover.SetValue(otherData, 97f);
                UpdateHuds.Invoke(EnemyHud.instance, new object[] { player, null, HudStep });
                var timers = Mathf.Abs((float)hover.GetValue(data) - (83f + HudStep)) < 0.01f &&
                             Mathf.Abs((float)otherHover.GetValue(otherData) - (97f + HudStep)) < 0.01f;
                var names = HudName(data).EndsWith(DistanceText(player, first.gameObject)) &&
                            HudName(otherData).EndsWith(DistanceText(player, second));
                Check(run, "aimbar_two_timers", timers && names && HudVisible(data) && HudVisible(otherData),
                    $"independent hover timers restored with vanilla elapsed time={timers}, both names carry distance={names}");
            }
            Navigation.ClearTarget();
            first.m_hideHud = true;
            if (!AimAndUpdate(player, first.gameObject, out error)) run.Fail("aimbar_hidden_creature", error);
            else CheckBar(run, player, first.gameObject, "aimbar_hidden_creature", 1f);
            first.m_hideHud = false;
            var boss = first.m_boss;
            first.m_boss = true;
            try
            {
                if (!AimAndUpdate(player, first.gameObject, out error, false)) run.Fail("aimbar_boss", error);
                else CheckBar(run, player, first.gameObject, "aimbar_boss", 1f);
            }
            finally { first.m_boss = boss; }
            Remove(second);
        }

        private static void CheckSamePiece(FeatureRun run, Player player, GameObject go)
        {
            Navigation.RestoreTarget(new Navigation.TargetState
            {
                Pin = new Minimap.PinData { m_type = Minimap.PinType.Icon3, m_name = go.GetComponent<Piece>().m_name },
                Temporary = true,
                LastPosition = go.transform.position,
                AimedInstance = go,
                AimedId = go.GetComponent<ZNetView>().GetZDO().m_uid
            });
            if (!AimAndUpdate(player, go, out var error)) run.Fail("aimbar_same_piece", error);
            else
            {
                var label = Navigation.Label;
                var health = go.GetComponent<WearNTear>().GetHealthPercentage();
                var fast = label ? label.transform.Find("Health/health_fast").GetComponent<GuiBar>() : null;
                var slow = label ? label.transform.Find("Health/health_slow").GetComponent<GuiBar>() : null;
                var values = fast && slow && Mathf.Abs(BarValue(fast) - health) < 0.01f &&
                    Mathf.Abs(BarValue(slow) - health) < 0.01f;
                Check(run, "aimbar_same_piece", !LabelVisible() && NamedLabels() == 1 &&
                    label && label.activeInHierarchy && health < 1f && values,
                    $"only the navigation label is visible for the damaged player-built piece; health={health:F3}, both bars match={values}");
            }
            Navigation.ClearTarget();
        }

        internal static bool AimAndUpdate(Player player, GameObject go, out string error, bool updateEnemyHud = true)
        {
            error = "ray precondition failed: no enabled collider resolves to the test object before an obstruction";
            var camera = GameCamera.instance.transform;
            Physics.SyncTransforms();
            foreach (var collider in go.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled) continue;
                var ray = new Ray(camera.position, collider.bounds.center - camera.position);
                if (!ObjectPinning.TryResolveFromRay(player, ray, out var aimed) ||
                    aimed.Instance != go) continue;
                camera.rotation = Quaternion.LookRotation(ray.direction);
                Navigation.Update(player);
                if (updateEnemyHud) LateUpdate.Invoke(EnemyHud.instance, null);
                error = null;
                return true;
            }
            return false;
        }

        internal static bool ActivateExistingLabel(Player player, out string error)
        {
            error = "aimed label precondition failed: ";
            if (!GameCamera.instance) { error += "the game camera is missing"; return false; }
            if (!player || player != Player.m_localPlayer || !player.IsOwner())
            { error += "the player is not the local owner"; return false; }
            if (Game.IsPaused()) { error += "the game is paused"; return false; }
            if (!Hud.instance || Hud.IsUserHidden()) { error += "the HUD is missing or hidden"; return false; }
            if (!Minimap.instance || Minimap.instance.m_mode == Minimap.MapMode.Large)
            { error += "the minimap is missing or the large map is open"; return false; }
            var camera = GameCamera.instance.transform;
            var rotation = camera.rotation;
            try
            {
                foreach (var collider in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
                {
                    if (!collider.enabled || collider.isTrigger || collider.transform.IsChildOf(player.transform)) continue;
                    var ray = new Ray(camera.position, collider.bounds.center - camera.position);
                    if (!ObjectPinning.TryResolveFromRay(player, ray, out var aimed) ||
                        !aimed.Instance || aimed.Instance.GetComponent<Character>() ||
                        Utils.DistanceXZ(player.transform.position, aimed.Position) > MappingConfig.AimedObjectBarRange)
                        continue;
                    camera.rotation = Quaternion.LookRotation(ray.direction);
                    Navigation.Update(player);
                    if (LabelVisible())
                    {
                        error = null;
                        return true;
                    }
                }
                error = "no unobstructed loaded non-creature within the ray and bar ranges produces an on-screen label; face nearby vegetation or a piece and retry";
                return false;
            }
            finally { camera.rotation = rotation; }
        }

        private static GameObject Spawn(string prefabName, Vector3 position, Quaternion rotation,
            List<GameObject> spawned, out string error)
        {
            error = "spawn precondition failed: " + prefabName + " prefab is unavailable";
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (!prefab) return null;
            var go = UnityEngine.Object.Instantiate(prefab, position, rotation);
            SmokeSession.TrackSpawn(go);
            spawned.Add(go);
            var view = go.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || view.GetZDO() == null)
            {
                error = "spawn precondition failed: " + prefabName + " has no valid ZDO";
                Remove(go);
                return null;
            }
            view.GetZDO().Persistent = false;
            Physics.SyncTransforms();
            error = null;
            return go;
        }

        private static void Remove(GameObject go)
        {
            if (!go) return;
            go.SetActive(false);
            Physics.SyncTransforms();
            if (SmokeSession.DestroyTestObject(go, 0, "aimed bar smoke")) SmokeSession.ReleaseSpawn(go);
        }

        private static void CheckBar(FeatureRun run, Player player, GameObject go, string check, float health)
        {
            var character = go.GetComponent<Character>();
            var vanilla = character && !character.m_hideHud && !character.IsBoss();
            var data = vanilla ? HudData(character) : null;
            var label = AimedObjectBar.Label;
            var root = vanilla ? data == null ? null : HudRoot(data) : label;
            var name = vanilla ? data == null ? "" : HudName(data) : LabelName(label);
            var expectedName = Localization.instance.Localize(character ? character.GetHoverName() :
                go.GetComponent<Piece>() ? go.GetComponent<Piece>().m_name : go.GetComponent<Pickable>().GetHoverName());
            var fast = root ? root.transform.Find("Health/health_fast").GetComponent<GuiBar>() : null;
            var slow = root ? root.transform.Find("Health/health_slow").GetComponent<GuiBar>() : null;
            var friendly = root ? root.transform.Find("Health/health_fast_friendly") : null;
            var values = fast && slow && Mathf.Abs(BarValue(fast) - health) < 0.01f &&
                         Mathf.Abs(BarValue(slow) - health) < 0.01f &&
                         (!friendly || Mathf.Abs(BarValue(friendly.GetComponent<GuiBar>()) - health) < 0.01f);
            Check(run, check, root && root.activeInHierarchy && name == expectedName + DistanceText(player, go) &&
                values && (!vanilla || !LabelVisible()),
                $"visible={root && root.activeInHierarchy}, name='{name}', expected='{expectedName + DistanceText(player, go)}', health={health:F3}, bars match={values}, vanilla={vanilla}");
        }

        private static float BarValue(GuiBar bar) =>
            (float)AccessTools.Field(typeof(GuiBar), "m_value").GetValue(bar);
        private static string DistanceText(Player player, GameObject go) =>
            " [" + Mathf.RoundToInt(Utils.DistanceXZ(player.transform.position, go.transform.position)) + "m]";
        private static bool LabelVisible() => AimedObjectBar.Label &&
            AimedObjectBar.Label.activeInHierarchy;
        private static int NamedLabels() => UnityEngine.Object.FindObjectsByType<RectTransform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None).Count(x => x.gameObject.activeInHierarchy &&
            (x.name == "AutomaticsNavigationOverlay" || x.name == "AutomaticsAimedObjectOverlay"));
        private static IDictionary Huds() => (IDictionary)HudsField.GetValue(EnemyHud.instance);
        private static object HudData(Character character) => Huds().Contains(character) ? Huds()[character] : null;
        private static int HudCount(Character character) => Huds().Keys.Cast<Character>().Count(x => x == character);
        private static GameObject HudRoot(object data) =>
            (GameObject)AccessTools.Field(data.GetType(), "m_gui").GetValue(data);
        private static bool HudVisible(object data) => HudRoot(data) && HudRoot(data).activeInHierarchy;
        private static System.Reflection.FieldInfo HoverField(object data) =>
            AccessTools.Field(data.GetType(), "m_hoverTimer");
        private static string HudName(object data)
        {
            var name = AccessTools.Field(data.GetType(), "m_name").GetValue(data);
            return (string)AccessTools.Property(name.GetType(), "text").GetValue(name);
        }
        private static string LabelName(GameObject label)
        {
            if (!label) return "";
            var type = AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_name").FieldType;
            var name = label.transform.Find("Name").GetComponent(type);
            return (string)AccessTools.Property(type, "text").GetValue(name);
        }
        private static void Check(FeatureRun run, string name, bool passed, string detail)
        {
            if (passed) run.Pass(name, detail);
            else run.Fail(name, detail);
        }
    }
}
