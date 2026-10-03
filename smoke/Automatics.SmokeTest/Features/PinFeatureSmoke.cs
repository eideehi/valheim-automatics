using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.AutomaticMapping;
using Automatics.Valheim;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;

namespace Automatics.SmokeTest
{
    // Runs synchronously: no map save, item update or player input can occur between
    // adding a test pin and removing it. The drop also has the session's usual cleanup.
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

            var created = new List<Minimap.PinData>();
            Func<string, bool> cleanup = _ =>
            {
                foreach (var pin in created)
                    if (map) map.RemovePin(pin);
                return true;
            };
            SmokeSession.TrackHandBack(cleanup);
            ItemDrop drop = null;
            GameObject piece = null;
            try
            {
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
            }
            finally
            {
                // Only pins added during our synchronous action calls are in this list.
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
                        var count = Map.GetAllPins().Count;
                        var added = Invoke(player, ray, created);
                        var expectedType = IconPack.GetPinType(new Target
                        {
                            name = location.Label, prefabName = location.Prefab
                        });
                        if (!piece.IsPlacedByPlayer() && !worldPiece.GetComponentInParent<Location>() &&
                            added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                            added.m_name == location.Name && added.m_type == expectedType &&
                            !Map.IsAutomaticPin(added) &&
                            Vector3.Distance(added.m_pos, location.Position) < 0.01f)
                            run.Pass(check, "a detached piece with creator 0 saved the containing location's name, icon and position");
                        else
                            run.Fail(check, "the detached world-generated piece did not save exactly one location pin");
                        if (added != null) Minimap.instance.RemovePin(added);
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
                if (added != null) Minimap.instance.RemovePin(added);
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
                    if (added != null) Minimap.instance.RemovePin(added);
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

                var count = Map.GetAllPins().Count;
                var added = Invoke(player, ray, created);
                var expectedType = IconPack.GetPinType(new Target
                {
                    name = location.Label, prefabName = location.Prefab
                });
                if (added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                    added.m_name == location.Name && added.m_type == expectedType && !Map.IsAutomaticPin(added) &&
                    Vector3.Distance(added.m_pos, location.Position) < 0.01f)
                    run.Pass(check, "an interior surface saved its zone location's name, icon and entrance position");
                else
                    run.Fail(check, "the interior ray did not save exactly one pin for its zone location");
                if (added != null) Minimap.instance.RemovePin(added);
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
                pin => pin.m_save && !Map.IsAutomaticPin(pin) && pin.m_name == name,
                includeInactive: true) != null;
        }

        // Read existing locations only: spawning their prefab can generate terrain or entities.
        private static void CheckLocation(FeatureRun run, Player player,
            List<Minimap.PinData> created, bool ground, ref bool navigationChecked)
        {
            var check = ground ? "pin_location_ground" : "pin_location_wall";
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
                    var count = Map.GetAllPins().Count;
                    var added = Invoke(player, ray, created);
                    var expectedType = IconPack.GetPinType(new Target
                    {
                        name = location.Label, prefabName = location.Prefab
                    });
                    if (added != null && Map.GetAllPins().Count == count + 1 && added.m_save &&
                        added.m_name == location.Name && added.m_type == expectedType && !Map.IsAutomaticPin(added) &&
                        Vector3.Distance(added.m_pos, location.Position) < 0.01f &&
                        Invoke(player, ray, created) == null && Map.GetAllPins().Count == count + 1)
                        run.Pass(check, "one saved location pin with the recognized name, icon and position; repeat adds nothing");
                    else
                        run.Fail(check, "the location ray did not add exactly one recognized location pin or allowed a duplicate");
                    // Let the other ray check exercise the same location without a test duplicate.
                    if (added != null) Minimap.instance.RemovePin(added);
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
                foreach (var pin in createdMarkers) if (map) map.RemovePin(pin);
                foreach (var pin in added) if (map) map.RemovePin(pin);
                var creaturePin = DynamicObjectMapping.GetCachedPin(creatureId);
                if (creaturePin != null && map) map.RemovePin(creaturePin);
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
                    map.RemovePin(automatic);
                    Check(run, "navhotkey_removed_pin_kept", Navigation.CaptureTarget().CreatureId == id &&
                        MarkerCount(before, added) == 1 && Vector3.Distance(Navigation.TargetPosition, character.transform.position) < 0.01f,
                        $"removing a tracked creature's dynamic pin leaves a temporary destination; {mapClickDetail}");
                    Navigation.ClearTarget();
                }

                navigate(player, dropRay, true);
                var marker = Navigation.TemporaryMarker;
                Check(run, "navhotkey_temporary", marker != null && ReferenceEquals(Navigation.TargetPin, marker) &&
                    !marker.m_save && !Map.IsAutomaticPin(marker) && MarkerCount(before, added) == 1 &&
                    marker.m_name == Localization.instance.Localize(Objects.GetName(drop)) &&
                    Vector3.Distance(marker.m_pos, drop.transform.position) < 0.01f,
                    "an unpinned object gets exactly one named, unsaved, non-automatic marker");
                CheckSave(run, map, marker, control);
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
                            x.Key == SmokeSession.ToggleOutcome.Restored || x.Key == SmokeSession.ToggleOutcome.NothingWritten),
                        string.Join("; ", restored.Select(x => x.Value)));
                }
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
