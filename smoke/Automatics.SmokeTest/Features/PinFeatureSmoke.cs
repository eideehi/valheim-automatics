using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.AutomaticMapping;
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
                    camera.forward, out var pieceSkip);
                if (!piece)
                    run.Skip("pin_piece_name", pieceSkip);
                else
                {
                    Physics.SyncTransforms();
                    CheckPiece(run, player, piece, created);
                }

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
                !string.IsNullOrEmpty(name) && added.m_name == name && name != prefabName &&
                !name.Contains("$") && added.m_type == expectedType &&
                Vector3.Distance(added.m_pos, go.transform.position) < 0.01f &&
                !Map.IsAutomaticPin(added))
                run.Pass("pin_piece_name", $"'{name}' (prefab {prefabName}), type={expectedType}");
            else
                run.Fail("pin_piece_name",
                    $"expected one saved pin named '{name}' with type {expectedType}, got '{added?.m_name}' (prefab {prefabName})");
        }

        // A building piece kept out of every world save and recorded with the session, so a run
        // cut short still has it removed. Returns null with the reason when none could be set up.
        private static GameObject SpawnTestPiece(Vector3 position, Vector3 forward, out string skip)
        {
            skip = "no wood wall or workbench prefab is available";
            foreach (var prefabName in new[] { "woodwall", "piece_workbench" })
            {
                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (!prefab || !prefab.GetComponent<Piece>()) continue;

                var facing = Vector3.ProjectOnPlane(-forward, Vector3.up);
                var rotation = facing.sqrMagnitude > 0.001f ? Quaternion.LookRotation(facing) : Quaternion.identity;
                var go = UnityEngine.Object.Instantiate(prefab, position, rotation);
                SmokeSession.TrackSpawn(go);
                if (!Objects.GetZNetView(go.transform, out var nview) || nview == null ||
                    !nview.IsValid() || nview.GetZDO() == null)
                {
                    skip = $"the spawned {prefabName} has no ZDO";
                    if (SmokeSession.DestroyTestObject(go, 0, "pin feature smoke"))
                        SmokeSession.ReleaseSpawn(go);
                    return null;
                }
                if (!nview.IsOwner()) nview.ClaimOwnership();
                nview.GetZDO().Persistent = false;
                return go;
            }
            return null;
        }

        private static bool HasManualPin(Vector3 position, string name)
        {
            // Include pins without a live minimap marker, as the action does.
            return Map.GetClosestPin(position, 2f,
                pin => pin.m_save && !Map.IsAutomaticPin(pin) && pin.m_name == name,
                includeInactive: true) != null;
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
