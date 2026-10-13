using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.AutomaticFarming;
using Automatics.Valheim;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Farming = Automatics.AutomaticFarming.AutomaticFarming;
using FarmingConfig = Automatics.AutomaticFarming.Config;
using FarmingModule = Automatics.AutomaticFarming.Module;
using Planting = Automatics.AutomaticFarming.CropPlanting;
using Object = UnityEngine.Object;

namespace Automatics.SmokeTest
{
    // Drives one Automatic Farming pass at a time in Player With Containers mode, next to an empty
    // cultivated plot the player stands beside: a harvest goes into the role containers and is
    // replanted from them (also below Seed Reserve, which holds back only sowing), and sowing
    // continues the rows of a rotated crop. Everything runs synchronously on objects the section
    // spawns; the containers, crops and their contents are removed afterwards and no terrain is
    // changed. A harvest may raise the pick skill, as a harvest by hand does. First, and without
    // changing anything, it checks that the default crop list holds exactly the crops with a sapling.
    internal static class FarmingFeatureSmoke
    {
        private const string Label = "farming feature smoke";
        private const int FarmingRange = 4;
        private const int ContainerRange = 8;
        private const float LatticeYaw = 30f;
        // As in the planting section: at a factor of 1 a crop's own collider fills its neighbours' grow space.
        private const float SpacingFactor = 2.5f;
        private const Heightmap.Biome EarlyBiomes = Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest |
                                                   Heightmap.Biome.Swamp | Heightmap.Biome.Mountain |
                                                   Heightmap.Biome.Plains;

        private static List<GameObject> _created;
        private static string _saplingName;

        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !ZNetScene.instance || !ZoneSystem.instance || !ObjectDB.instance ||
                !ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("farming_section", "requires a loaded single-player world");
                yield break;
            }
            if (!FarmingModule.Running)
            {
                run.Skip("farming_section", "enable the Farming module");
                yield break;
            }

            CheckDefaultCrops(run);

            var origin = player.transform.position;
            if (!SelectCrop(origin, out var crop, out var cropReport))
            {
                run.Skip("farming_section", cropReport);
                yield break;
            }
            var foreign = ContainerCache.GetAllInstance().Count(x => x && ContainerRoles.GetRole(x) != ContainerRole.None &&
                Vector3.Distance(x.transform.position, origin) <= FarmingRange + ContainerRange + 1f);
            if (foreign > 0)
            {
                run.Skip("farming_section", $"{foreign} container(s) with a farming role within " +
                    $"{FarmingRange + ContainerRange + 1} m; stand where none is so the section uses only its own");
                yield break;
            }

            var created = new List<GameObject>();
            GameObject storage = null, cultivation = null;
            _ownSeeds = 0;
            _ownYield = 0;
            var harmony = new Harmony("net.eidee.valheim.automatics.smoke.farming");
            try
            {
                if (!Set(run, "_farmingInterval", 0f) || !Set(run, "_seedSource", SeedSource.PlayerContainers) ||
                    !Set(run, "_farmingRange", FarmingRange) || !Set(run, "_containerSearchRange", ContainerRange) ||
                    !Set(run, "_allowFarmingCrop", new StringList(new[] { crop.Identifier })) ||
                    !Set(run, "_sowingSpacingFactor", SpacingFactor) || !Set(run, "_enableProactiveSowing", false) ||
                    !Set(run, "_seedReserve", 0))
                    yield break;

                storage = SpawnChest(player, player.transform.right * 1.5f, created);
                cultivation = SpawnChest(player, -player.transform.right * 1.5f, created);
                if (!storage || !cultivation || !Designate(player, storage, ContainerRole.Storage) ||
                    !Designate(player, cultivation, ContainerRole.Cultivation))
                {
                    run.Skip("farming_section", "could not spawn and designate the two test containers");
                    yield break;
                }
                Physics.SyncTransforms();

                var cells = FindCells(player, crop, storage, cultivation);
                if (cells.Count < 3)
                {
                    run.Skip("farming_section", $"found {cells.Count} of 3 empty cultivated cells within " +
                        $"{FarmingRange - 1} m; stand beside an empty cultivated plot of at least 3 x 3 m");
                    yield break;
                }
                run.Pass("farming_patch_found", $"{crop.Identifier} on {cells.Count} free cells, spacing {crop.Step:F2} m");

                harmony.Patch(AccessTools.Method(typeof(Plant), "Awake"),
                    postfix: new HarmonyMethod(typeof(FarmingFeatureSmoke), nameof(PlantAwake)));
                _saplingName = crop.Sapling.name;
                _created = created;
                var freeBuild = ZoneSystem.instance.GetGlobalKey(crop.Sapling.FreeBuildKey());

                cultivation.GetComponent<Container>().GetInventory().AddItem(crop.SeedPrefab, 3, 1, 0, 0L, "", false);
                _ownSeeds += 3;
                CheckHarvest(run, "farming_player_containers_harvest", player, crop, cells[0], storage, cultivation,
                    created, freeBuild, cells.Skip(2).ToList());

                if (!Set(run, "_seedReserve", 999) || !Set(run, "_enableProactiveSowing", true)) yield break;
                CheckHarvest(run, "farming_replant_below_reserve", player, crop, cells[1], storage, cultivation,
                    created, freeBuild, cells.Skip(2).ToList());

                // The replanted crops would anchor the lattice; the rotated one below must be the
                // only growing crop of its kind in range.
                // Their colliders are turned off and their views invalidated at once, so the
                // lattice search no longer sees them; no frame passes while the settings or the
                // Plant patch are in place.
                RemoveObjects(created.Where(x => x && x.GetComponent<Plant>()).ToList());
                Physics.SyncTransforms();

                if (!Set(run, "_seedReserve", 0)) yield break;
                CheckSowing(run, player, crop, cells, cultivation, created);
            }
            finally
            {
                _created = null;
                try
                {
                    harmony.UnpatchSelf();
                }
                finally
                {
                    try
                    {
                        EmptyOwnItems(storage, crop.YieldName, _ownYield);
                        EmptyOwnItems(cultivation, crop.SeedName, _ownSeeds);
                        RemoveObjects(created.Where(x => x && !KeptChest(x)));
                    }
                    finally
                    {
                        var results = SmokeSession.RestoreSettings(run.RunId);
                        if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                            run.Fail("farming_settings_restored", string.Join("; ", results.Select(x => x.Value)));
                        else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                                  x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                            run.Skip("farming_settings_restored", "a setting was edited by the user and kept");
                        else run.Pass("farming_settings_restored");
                    }
                }
            }
        }

        private sealed class Crop
        {
            public string Identifier;
            public Piece Sapling;
            public Plant Plant;
            public GameObject Grown;
            public string YieldName;
            public string SeedPrefab;
            public string SeedName;
            public int SeedAmount;
            public float Step;
        }

        // Flora identifiers that predate the Mistlands update; a failure names only these on screen.
        private static readonly HashSet<string> EarlyFlora = new HashSet<string>(new[]
        {
            "Dandelion", "Mushroom", "Raspberries", "Blueberries", "Carrot", "CarrotSeeds", "YellowMushroom",
            "Thistle", "Turnip", "TurnipSeeds", "Onion", "OnionSeeds", "Barley", "Cloudberries", "Flax"
        }, StringComparer.Ordinal);

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Farming");

        // Read-only: the default of Allow Farming Crop is exactly the Flora identifiers the sapling
        // lookup resolves, so that farming targets by default only crops it can replant. A pass
        // gives counts; a failure names each early identifier that differs and counts the later
        // ones, whose names go to the log file only.
        private static void CheckDefaultCrops(FeatureRun run)
        {
            const string name = "farming_default_crops_have_saplings";
            var entry = AccessTools.Field(typeof(FarmingConfig), "_allowFarmingCrop")?.GetValue(null) as ConfigEntry<StringList>;
            if (entry == null)
            {
                run.Skip(name, "Allow Farming Crop is not bound");
                return;
            }

            var defaults = new HashSet<string>((StringList)entry.DefaultValue, StringComparer.Ordinal);
            var identifiers = ValheimObject.Flora.GetAllElements().Select(x => x.identifier).Union(defaults).ToList();
            var mismatches = new List<string>();
            var later = 0;
            foreach (var identifier in identifiers)
            {
                var resolves = Planting.TryResolveSapling(identifier, out _);
                if (resolves == defaults.Contains(identifier)) continue;
                var mismatch = identifier + (resolves ? ": has a sapling but is not in the default" : ": in the default but has no sapling");
                Log.LogWarning($"{name}: {mismatch}");
                if (EarlyFlora.Contains(identifier)) mismatches.Add(mismatch);
                else later++;
            }
            if (later > 0) mismatches.Add($"{later} later identifier(s) differ (named in the log file)");
            Check(run, name, mismatches.Count == 0, mismatches.Count == 0
                ? $"each of the {defaults.Count} default crop(s) has a sapling and none of the other " +
                  $"{identifiers.Count - defaults.Count} Flora identifier(s) has one"
                : string.Join("; ", mismatches.ToArray()));
        }

        // The first allowed crop that grows from seeds on cultivated ground in this biome, has no
        // growing or ripe plant within reach, and predates the Mistlands (later crops are counted,
        // not named).
        private static bool SelectCrop(Vector3 origin, out Crop crop, out string report)
        {
            crop = null;
            var biome = Heightmap.FindBiome(origin);
            var notes = new List<string>();
            var later = 0;
            foreach (var identifier in FarmingConfig.AllowFarmingCrops)
            {
                if (ValheimObject.Flora.HasTag(identifier, "seed")) continue;
                if (!Planting.TryResolveSapling(identifier, out var sapling)) continue;
                var plant = sapling.GetComponent<Plant>();
                if (!plant || !plant.m_needCultivatedGround || plant.m_attachDistance > 0f ||
                    plant.m_growRadius < 0.1f) continue;
                if ((plant.m_biome & EarlyBiomes) == 0)
                {
                    later++;
                    continue;
                }
                if ((plant.m_biome & biome) == 0)
                {
                    notes.Add($"{identifier}: not in {biome}");
                    continue;
                }
                var grown = plant.m_grownPrefabs?.FirstOrDefault(x => x && x.GetComponent<Pickable>());
                var pickable = grown ? grown.GetComponent<Pickable>() : null;
                var yieldDrop = pickable && pickable.m_itemPrefab ? pickable.m_itemPrefab.GetComponent<ItemDrop>() : null;
                var requirement = sapling.m_resources.FirstOrDefault(r => r.m_resItem && r.GetAmount(0) > 0);
                if (!yieldDrop || requirement == null || sapling.m_resources.Count(r => r.m_resItem && r.GetAmount(0) > 0) != 1)
                {
                    notes.Add($"{identifier}: not a one-seed crop with a pickable yield");
                    continue;
                }
                var nearby = Physics.OverlapSphere(origin, FarmingRange + 2f)
                    .Select(c => c.GetComponentInParent<Plant>() ? c.GetComponentInParent<Plant>().gameObject
                        : c.GetComponentInParent<Pickable>() ? c.GetComponentInParent<Pickable>().gameObject : null)
                    .Where(go => go)
                    .Select(go => Objects.GetPrefabName(go))
                    .Count(name => name == sapling.name || name == grown.name);
                nearby += PickableCache.GetAllInstance().Count(x => x && !x.GetPicked() &&
                    Vector3.Distance(x.transform.position, origin) <= FarmingRange + 2f &&
                    ValheimObject.Flora.GetIdentify(Objects.GetName(x), out var id) && id == identifier);
                if (nearby > 0)
                {
                    notes.Add($"{identifier}: {nearby} growing or ripe within {FarmingRange + 2} m");
                    continue;
                }

                crop = new Crop
                {
                    Identifier = identifier,
                    Sapling = sapling,
                    Plant = plant,
                    Grown = grown,
                    YieldName = yieldDrop.m_itemData.m_shared.m_name,
                    SeedPrefab = requirement.m_resItem.gameObject.name,
                    SeedName = requirement.m_resItem.m_itemData.m_shared.m_name,
                    SeedAmount = requirement.GetAmount(0),
                    Step = plant.m_growRadius * SpacingFactor
                };
                report = identifier;
                return true;
            }

            report = "no usable crop in Allow Farming Crop: " + string.Join("; ", notes.ToArray()) +
                     (later > 0 ? $"; {later} later-area crop(s) not used" : "");
            return false;
        }

        private static GameObject SpawnChest(Player player, Vector3 offset, List<GameObject> created)
        {
            var prefab = ZNetScene.instance.GetPrefab("piece_chest_wood");
            if (!prefab) return null;
            var position = player.transform.position + offset;
            position.y = ZoneSystem.instance.GetGroundHeight(position);
            var go = Object.Instantiate(prefab, position, Quaternion.identity);
            created.Add(go);
            SmokeSession.TrackSpawn(go);
            if (Objects.GetZNetView(go.transform, out var view) && view && view.IsValid())
            {
                view.GetZDO().Persistent = false;
                if (!view.IsOwner()) view.ClaimOwnership();
            }
            var container = go.GetComponent<Container>();
            return container && container.GetInventory() != null && container.GetInventory().NrOfItems() == 0 ? go : null;
        }

        // Through the real designation path: each cycle advances the role by one.
        private static bool Designate(Player player, GameObject go, ContainerRole target)
        {
            var container = go.GetComponent<Container>();
            for (var i = 0; i < 4; i++)
            {
                if (ContainerRoles.GetRole(container) == target) return true;
                if (!ContainerRoles.TryCycleRole(player, container, out _)) return false;
            }
            return ContainerRoles.GetRole(container) == target;
        }

        // Plantable cells within FarmingRange - 1 of the player, spaced for the crop, nearest first.
        private static List<Vector3> FindCells(Player player, Crop crop, params GameObject[] chests)
        {
            var origin = player.transform.position;
            var result = new List<Vector3>();
            var reach = FarmingRange - 1f;
            for (var x = -reach; x <= reach; x += 0.25f)
            for (var z = -reach; z <= reach; z += 0.25f)
            {
                var pos = origin + new Vector3(x, 0f, z);
                if (new Vector2(x, z).magnitude > reach || new Vector2(x, z).magnitude < 1f) continue;
                pos.y = ZoneSystem.instance.GetGroundHeight(pos);
                if (chests.Any(c => Vector3.Distance(c.transform.position, pos) < 1.5f)) continue;
                if (result.Any(r => Vector3.Distance(r, pos) < crop.Plant.m_growRadius * 2f)) continue;
                if (!Plantable(crop, pos)) continue;
                result.Add(pos);
            }
            return result.OrderBy(p => Vector3.Distance(p, origin)).ToList();
        }

        private static bool Plantable(Crop crop, Vector3 pos)
        {
            return Heightmap.FindHeightmap(pos) && PrivateArea.CheckAccess(pos, 0f, false) &&
                   Planting.CanPlantAt(crop.Plant, pos, out _);
        }

        private static void CheckHarvest(FeatureRun run, string name, Player player, Crop crop, Vector3 cell,
            GameObject storage, GameObject cultivation, List<GameObject> created, bool freeBuild, List<Vector3> free)
        {
            var ripe = Object.Instantiate(crop.Grown, cell, Quaternion.identity);
            created.Add(ripe);
            SmokeSession.TrackSpawn(ripe);
            if (Objects.GetZNetView(ripe.transform, out var view) && view && view.IsValid())
                view.GetZDO().Persistent = false;
            var pickable = ripe.GetComponent<Pickable>();

            var playerYield = player.GetInventory().CountItems(crop.YieldName);
            var playerSeeds = player.GetInventory().CountItems(crop.SeedName);
            var stored = storage.GetComponent<Container>().GetInventory().CountItems(crop.YieldName);
            var seeds = cultivation.GetComponent<Container>().GetInventory().CountItems(crop.SeedName);
            var before = created.Count;
            var sowable = free.Count(x => Plantable(crop, x));

            Farming.TryFarming(player);
            Physics.SyncTransforms();

            var picked = !pickable || pickable.GetPicked();
            var storedAfter = storage.GetComponent<Container>().GetInventory().CountItems(crop.YieldName);
            if (storedAfter > stored) _ownYield += storedAfter - stored;
            var seedsAfter = cultivation.GetComponent<Container>().GetInventory().CountItems(crop.SeedName);
            var plants = created.Skip(before).Where(x => x && x.GetComponent<Plant>()).ToList();
            var replanted = plants.Count == 1 && Flat(plants[0].transform.position - cell) < 0.05f;
            var expectedSeeds = freeBuild ? seeds : seeds - crop.SeedAmount;
            var inventoryKept = player.GetInventory().CountItems(crop.YieldName) == playerYield &&
                                player.GetInventory().CountItems(crop.SeedName) == playerSeeds;
            // With sowing on, a free cell was left for it, so a sow that ignored the reserve shows.
            var sowingHeld = !FarmingConfig.EnableProactiveSowing || sowable > 0;
            Check(run, name, picked && storedAfter > stored && replanted && seedsAfter == expectedSeeds && inventoryKept &&
                             sowingHeld,
                $"picked={picked}, storage {stored}->{storedAfter}, cultivation seeds {seeds}->{seedsAfter} " +
                $"(expected {expectedSeeds}), new plants {plants.Count} (at the spot {replanted}), " +
                $"inventory unchanged {inventoryKept}, free cells left for sowing {sowable}, " +
                (freeBuild ? "free-build world so seed use is not checked, " : "") + $"reserve {FarmingConfig.SeedReserve}, sowing {FarmingConfig.EnableProactiveSowing}");
        }

        private static void CheckSowing(FeatureRun run, Player player, Crop crop, List<Vector3> cells,
            GameObject cultivation, List<GameObject> created)
        {
            var inventory = cultivation.GetComponent<Container>().GetInventory();
            inventory.AddItem(crop.SeedPrefab, 10, 1, 0, 0L, "", false);
            _ownSeeds += 10;
            var rotation = Quaternion.Euler(0f, LatticeYaw, 0f);

            // An anchor with at least two free cells of its rotated lattice beside it.
            Vector3? anchor = null;
            foreach (var cell in cells)
            {
                var free = 0;
                foreach (var offset in new[] { new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f) })
                {
                    var pos = cell + rotation * (offset * crop.Step);
                    pos.y = ZoneSystem.instance.GetGroundHeight(pos);
                    if (Vector3.Distance(player.transform.position, pos) < FarmingRange - 0.5f && Plantable(crop, pos)) free++;
                }
                if (free < 2) continue;
                anchor = cell;
                break;
            }
            if (anchor == null)
            {
                run.Skip("farming_sow_rotated_lattice", "no cell with two free cells of a rotated lattice beside it; " +
                    "stand beside a larger empty cultivated plot");
                return;
            }

            var before = created.Count;
            var go = Object.Instantiate(crop.Sapling.gameObject, anchor.Value, rotation);
            if (!created.Contains(go))
            {
                created.Add(go);
                SmokeSession.TrackSpawn(go);
            }
            if (Objects.GetZNetView(go.transform, out var view) && view && view.IsValid())
                view.GetZDO().Persistent = false;
            Physics.SyncTransforms();
            var neighbours = new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back }
                .Select(o => anchor.Value + rotation * (o * crop.Step))
                .Select(p => new Vector3(p.x, ZoneSystem.instance.GetGroundHeight(p), p.z))
                .Count(p => Vector3.Distance(player.transform.position, p) < FarmingRange - 0.5f && Plantable(crop, p));
            if (neighbours == 0)
            {
                run.Skip("farming_sow_rotated_lattice", "no lattice cell beside the rotated crop is plantable once it stands; " +
                    "stand beside a larger empty cultivated plot");
                return;
            }

            Farming.TryFarming(player);

            var sown = created.Skip(before).Where(x => x && x != go && x.GetComponent<Plant>()).ToList();
            var off = new List<string>();
            foreach (var plant in sown)
            {
                var local = Quaternion.Inverse(rotation) * (plant.transform.position - anchor.Value);
                var dx = Mathf.Abs(local.x / crop.Step - Mathf.Round(local.x / crop.Step)) * crop.Step;
                var dz = Mathf.Abs(local.z / crop.Step - Mathf.Round(local.z / crop.Step)) * crop.Step;
                var yaw = Mathf.Abs(Mathf.DeltaAngle(plant.transform.eulerAngles.y, LatticeYaw)) % 90f;
                if (dx > 0.05f || dz > 0.05f || Mathf.Min(yaw, 90f - yaw) > 1f)
                    off.Add($"({local.x:F2}, {local.z:F2}) yaw {plant.transform.eulerAngles.y:F1}");
            }
            Check(run, "farming_sow_rotated_lattice", sown.Count > 0 && off.Count == 0,
                $"{sown.Count} sown beside a crop turned {LatticeYaw}°, off the lattice: " +
                (off.Count == 0 ? "none" : string.Join(", ", off.ToArray())));
        }

        private static int _ownSeeds;
        private static int _ownYield;
        private static readonly HashSet<GameObject> KeptChests = new HashSet<GameObject>();

        private static bool KeptChest(GameObject go)
        {
            return KeptChests.Remove(go);
        }

        // Takes out only what the section put in or harvested into the chest; a chest that still
        // holds anything else is kept in the world and the log says where.
        private static void EmptyOwnItems(GameObject chest, string itemName, int own)
        {
            var inventory = chest ? chest.GetComponent<Container>()?.GetInventory() : null;
            if (inventory == null) return;
            var remove = Mathf.Min(own, inventory.CountItems(itemName));
            if (remove > 0) inventory.RemoveItem(itemName, remove);
            if (inventory.NrOfItems() == 0) return;

            KeptChests.Add(chest);
            SmokeSession.ReleaseSpawn(chest);
            if (Objects.GetZNetView(chest.transform, out var view) && view && view.IsValid())
                view.GetZDO().Persistent = true;
            var p = chest.transform.position;
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Features").LogWarning(
                $"[Automatics Smoke] {Label}: a test chest at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) holds items the section did not put in; it was left in the world");
        }

        private static float Flat(Vector3 v)
        {
            return new Vector2(v.x, v.z).magnitude;
        }

        private static void PlantAwake(Plant __instance)
        {
            if (_created == null || !__instance || ZNetView.m_forceDisableInit ||
                __instance.gameObject.name != _saplingName + "(Clone)") return;
            var go = __instance.gameObject;
            if (_created.Contains(go)) return;
            _created.Add(go);
            SmokeSession.TrackSpawn(go);
            var view = go.GetComponent<ZNetView>();
            if (view && view.GetZDO() != null) view.GetZDO().Persistent = false;
        }

        private static void RemoveObjects(IEnumerable<GameObject> objects)
        {
            foreach (var go in objects)
            {
                if (!go) continue;
                foreach (var collider in go.GetComponentsInChildren<Collider>()) collider.enabled = false;
                if (SmokeSession.DestroyTestObject(go, 0, Label)) SmokeSession.ReleaseSpawn(go);
            }
        }

        private static bool Set<T>(FeatureRun run, string field, T value)
        {
            var entry = AccessTools.Field(typeof(FarmingConfig), field)?.GetValue(null) as ConfigEntry<T>;
            var setting = entry == null ? null : FindSetting(run, entry);
            string refusal = null;
            if (entry != null && setting == null && SmokeSession.BeginSetting(run.RunId, entry, out setting, out refusal))
                Settings[entry] = setting;
            if (setting != null && SmokeSession.WriteSetting(run.RunId, setting, value, out refusal)) return true;
            run.Skip("farming_section", $"could not record and apply test setting {field}: {refusal}");
            return false;
        }

        private static readonly Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> Settings =
            new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
        private static int _settingsRun = -1;

        // A setting written twice in one run keeps its first restoration record.
        private static SmokeSession.SettingRestoration FindSetting(FeatureRun run, ConfigEntryBase entry)
        {
            if (_settingsRun != run.RunId)
            {
                Settings.Clear();
                _settingsRun = run.RunId;
            }
            return Settings.TryGetValue(entry, out var setting) ? setting : null;
        }

        private static void Check(FeatureRun run, string name, bool ok, string detail)
        {
            if (ok) run.Pass(name, detail);
            else run.Fail(name, detail);
        }
    }
}
