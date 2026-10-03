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
            " " + Mathf.RoundToInt(Utils.DistanceXZ(player.transform.position, go.transform.position)) + "m";
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
