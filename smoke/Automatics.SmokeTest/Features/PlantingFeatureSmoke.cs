using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using FarmingConfig = Automatics.AutomaticFarming.Config;
using FarmingModule = Automatics.AutomaticFarming.Module;
using FarmingPatches = Automatics.AutomaticFarming.Patches;
using Grid = Automatics.AutomaticFarming.GridPlanting;
using Planting = Automatics.AutomaticFarming.CropPlanting;

namespace Automatics.SmokeTest
{
    // All planting and accounting happen synchronously. Only seeds absent from the player's
    // inventory are used, and every added item is tracked by reference. No terrain is changed.
    // The extra crops raise no Farming skill and no build stats by design, so neither is checked.
    internal static class PlantingFeatureSmoke
    {
        private const string Label = "planting feature smoke";
        private const int Size = 3;
        private const float SpacingFactor = 2.5f;
        private static List<GameObject> _created;
        private static string _prefabName;
        private static readonly System.Reflection.FieldInfo Stamina = AccessTools.Field(typeof(Player), "m_stamina");
        private static readonly System.Reflection.FieldInfo StaminaDelay = AccessTools.Field(typeof(Player), "m_staminaRegenTimer");
        private static readonly System.Reflection.FieldInfo NoPlacementCost = AccessTools.Field(typeof(Player), "m_noPlacementCost");

        private enum Limit
        {
            None,
            Seeds,
            Blocked,
            Stamina,
            Durability,
            ToolSwapped
        }

        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !ZNetScene.instance || !ZoneSystem.instance ||
                !ObjectDB.instance || !ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("planting_section", "requires a loaded single-player world");
                yield break;
            }
            if (!FarmingModule.Running || !FarmingConfig.EnableGridPlanting)
            {
                run.Skip("planting_section", "enable the Farming module and enable_grid_planting");
                yield break;
            }
            if (Grid.HasPending || player.IsDead() || player.InAttack() || Stamina == null || StaminaDelay == null)
            {
                run.Skip("planting_section", "player state is not ready or stamina fields could not be resolved");
                yield break;
            }

            var cultivator = ObjectDB.instance.GetItemPrefab("Cultivator")?.GetComponent<ItemDrop>();
            var table = cultivator?.m_itemData.m_shared.m_buildPieces;
            var biome = Heightmap.FindBiome(player.transform.position);
            var piece = SelectSapling(player, table, biome, out var candidates);
            var nonPlant = table?.m_pieces.Where(x => x).Select(x => x.GetComponent<Piece>())
                .FirstOrDefault(x => x && !x.GetComponent<Plant>());
            var freeSlots = player.GetInventory().GetEmptySlots();
            if (!piece || freeSlots < 3)
            {
                var reasons = new List<string>();
                if (freeSlots < 3) reasons.Add($"needs 3 free inventory slots, has {freeSlots}");
                if (!piece) reasons.Add($"no usable tree sapling in biome {biome}: {candidates}");
                run.Skip("planting_section", string.Join("; ", reasons.ToArray()));
                yield break;
            }
            // A world modifier is never changed; the player's own no-cost toggle is lifted for the
            // section below and put back afterwards. Vanilla charges stamina and durability either
            // way, so only the seed accounting depends on it.
            var freeBuildKey = piece.FreeBuildKey();
            var freeBuild = ZoneSystem.instance.GetGlobalKey(freeBuildKey);
            var originalNoCost = player.NoCostCheat();
            // The Debug build's own Debug patch skips Player.UseStamina in god mode (vanilla does
            // not), so the stamina accounting below needs god mode off for the section.
            var originalGodMode = player.InGodMode();
            if (freeBuild || (originalNoCost && NoPlacementCost == null))
            {
                run.Skip("planting_section", "seed accounting requires placement costs enabled: " +
                    $"world global key {freeBuildKey}={freeBuild} (not changed by the smoke), " +
                    $"player no-cost toggle m_noPlacementCost={originalNoCost}" +
                    (NoPlacementCost == null ? " (field not found, cannot lift it)" : ""));
                yield break;
            }

            var inventory = player.GetInventory();
            var equipped = inventory.GetEquippedItems().ToList();
            var added = new List<ItemDrop.ItemData>();
            var created = new List<GameObject>();
            var initialStamina = (float)Stamina.GetValue(player);
            var initialDelay = (float)StaminaDelay.GetValue(player);
            var harmony = new Harmony("net.eidee.valheim.automatics.smoke.planting");
            Func<string, bool> handBack = _ =>
            {
                if (!player) return true;
                if (originalNoCost) NoPlacementCost.SetValue(player, true);
                if (originalGodMode) player.SetGodMode(true);
                foreach (var item in added)
                {
                    if (player.IsItemEquiped(item)) player.UnequipItem(item, false);
                    if (inventory.ContainsItem(item)) inventory.RemoveItem(item);
                }
                var restored = true;
                foreach (var item in equipped)
                    if (inventory.ContainsItem(item) && !player.IsItemEquiped(item) && !player.EquipItem(item, false))
                        restored = false;
                return restored;
            };
            SmokeSession.TrackHandBack(handBack);
            try
            {
                if (originalNoCost)
                {
                    NoPlacementCost.SetValue(player, false);
                    run.Pass("planting_no_cost_lifted", "the player's no-cost toggle is off for this section and restored afterwards");
                }
                if (originalGodMode)
                {
                    player.SetGodMode(false);
                    run.Pass("planting_god_mode_lifted", "god mode is off for this section (the Debug build skips stamina use in god mode) and restored afterwards");
                }
                if (!Set(run, "_gridPlantingSize", Size, out _) || !Set(run, "_sowingSpacingFactor", SpacingFactor, out _) ||
                    !Set(run, "_gridPlantingKey", new KeyboardShortcut(KeyCode.LeftAlt), out var keySetting)) yield break;
                var tool = inventory.AddItem("Cultivator", 1, 1, 0, 0L, "", false);
                if (tool != null) added.Add(tool);
                if (tool == null || !player.EquipItem(tool, false))
                {
                    run.Skip("planting_section", "could not add and equip the test cultivator");
                    yield break;
                }
                var cost = (float)AccessTools.Method(typeof(Player), "GetBuildStamina").Invoke(player, null);
                if (cost <= 0f || Game.m_staminaRate <= 0f || initialStamina <= cost * 8f * Mathf.Max(1f, Game.m_staminaRate))
                {
                    run.Skip("planting_section", "needs enough current stamina to plant eight saplings");
                    yield break;
                }
                var rotation = Quaternion.Euler(0f, player.transform.eulerAngles.y + 25f, 0f);
                var plant = piece.GetComponent<Plant>();
                var spacing = plant.m_growRadius * SpacingFactor;
                if (!FindPatch(player, piece, plant, rotation, spacing, out var centre, out var cells, out var patchReport))
                {
                    run.Skip("planting_section", $"no patch around the player where all {Size * Size - 1} cells are plantable " +
                        $"(spacing {spacing:F2} m): {patchReport}");
                    yield break;
                }
                run.Pass("planting_patch_found", patchReport);
                run.Pass("planting_sapling_verified", $"{piece.name} in biome {biome}; candidates: {candidates}");

                CheckEvenSize(run, centre, rotation, spacing);

                var ghost = new GameObject("Automatics smoke planting ghost");
                created.Add(ghost);
                SmokeSession.TrackSpawn(ghost);
                ghost.transform.SetPositionAndRotation(centre, rotation);
                if (Grid.IsModifierHeld(player, FarmingConfig.GridPlantingKey))
                    run.Skip("planting_without_modifier", "release Left Alt to check the real postfix predicate");
                else
                {
                    FarmingPatches.Player_TryPlacePiece_Postfix(player, piece, true, ghost);
                    Check(run, "planting_without_modifier", !Grid.HasPending, "a released bound modifier recorded no request", "a released bound modifier recorded a request");
                }
                Grid.ClearPending();

                if (!SmokeSession.WriteSetting(run.RunId, keySetting, new KeyboardShortcut(), out var refusal))
                    run.Skip("planting_unbound_key", "could not unbind the key: " + refusal);
                else if (!Grid.IsModifierHeld(player, FarmingConfig.GridPlantingKey))
                    run.Skip("planting_unbound_key", "release the alternative placement button to check the unbound key");
                else
                {
                    FarmingPatches.Player_TryPlacePiece_Postfix(player, piece, true, ghost);
                    Check(run, "planting_unbound_key", Grid.HasPending, "an unbound key recorded a request", "an unbound key did not record a request");
                }
                Grid.ClearPending();

                Grid.RecordPlacement(player, piece, false, centre, rotation, true);
                Check(run, "planting_failed_placement", !Grid.HasPending, "a failed placement recorded no request", "a failed placement recorded a request");
                Grid.ClearPending();
                if (!nonPlant) run.Skip("planting_non_plant_piece", "the cultivator table has no piece without a Plant");
                else
                {
                    Grid.RecordPlacement(player, nonPlant, true, centre, rotation, true);
                    Check(run, "planting_non_plant_piece", !Grid.HasPending, $"{nonPlant.name} recorded no request", $"{nonPlant.name} recorded a request");
                }
                Grid.ClearPending();

                harmony.Patch(AccessTools.Method(typeof(Plant), "Awake"),
                    postfix: new HarmonyMethod(typeof(PlantingFeatureSmoke), nameof(PlantAwake)));
                _prefabName = piece.name;
                _created = created;
                var requirement = piece.m_resources.First(r => r.m_resItem && r.GetAmount(0) > 0);
                foreach (var limit in new[] { Limit.None, Limit.Seeds, Limit.Blocked, Limit.Stamina, Limit.Durability, Limit.ToolSwapped })
                    CheckGrid(run, player, piece, tool, requirement, added, created, cells, centre, rotation, limit);
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
                    Grid.ClearPending();
                    try
                    {
                        RemoveObjects(created);
                    }
                    finally
                    {
                        if (player)
                        {
                            Stamina.SetValue(player, initialStamina);
                            StaminaDelay.SetValue(player, initialDelay);
                        }
                        try
                        {
                            if (handBack(Label)) SmokeSession.ReleaseHandBack(handBack);
                            else run.Fail("planting_equipment_restored", "previous equipment could not be restored; hand-back will retry");
                        }
                        finally
                        {
                            var results = SmokeSession.RestoreSettings(run.RunId);
                            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                                run.Fail("planting_settings_restored", string.Join("; ", results.Select(x => x.Value)));
                            else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser || x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                                run.Skip("planting_settings_restored", "a setting was edited by the user and kept");
                            else run.Pass("planting_settings_restored");
                        }
                    }
                }
            }
        }

        // Biomes whose content predates the Mistlands update. Plant pieces that grow only elsewhere
        // are counted in the report but not named.
        private const Heightmap.Biome EarlyBiomes = Heightmap.Biome.Meadows | Heightmap.Biome.BlackForest |
                                                   Heightmap.Biome.Swamp | Heightmap.Biome.Mountain |
                                                   Heightmap.Biome.Plains;

        // Walks the cultivator's own piece table in order and takes the first Plant piece that
        // needs no cultivated ground, has no terrain modifier, takes one kind of seed the player
        // does not carry, and grows in the player's biome. Every Plant piece's verdict is
        // described so a SKIP names what failed.
        private static Piece SelectSapling(Player player, PieceTable table, Heightmap.Biome biome, out string report)
        {
            var notes = new List<string>();
            Piece chosen = null;
            if (!table || table.m_pieces == null)
            {
                report = "the cultivator has no piece table";
                return null;
            }
            var plants = 0;
            var laterOnly = 0;
            foreach (var prefab in table.m_pieces)
            {
                if (!prefab) continue;
                var candidate = prefab.GetComponent<Piece>();
                var plant = prefab.GetComponent<Plant>();
                if (!candidate || !plant) continue;
                plants++;
                var seeds = candidate.m_resources.Where(r => r.m_resItem && r.GetAmount(0) > 0).ToList();
                var problems = new List<string>();
                if (plant.m_needCultivatedGround) problems.Add("needs cultivated ground");
                if (prefab.GetComponentsInChildren<TerrainModifier>(true).Length != 0 ||
                    prefab.GetComponentsInChildren<TerrainOp>(true).Length != 0) problems.Add("has a terrain modifier");
                if (seeds.Count != 1) problems.Add($"{seeds.Count} seed kinds");
                if (seeds.Any(r => player.GetInventory().CountItems(r.m_resItem.m_itemData.m_shared.m_name) != 0))
                    problems.Add("seed already carried");
                if ((biome & plant.m_biome) == 0) problems.Add("wrong biome");
                if (problems.Count == 0 && !chosen) chosen = candidate;
                if ((plant.m_biome & EarlyBiomes) == 0 && chosen != candidate)
                {
                    laterOnly++;
                    continue;
                }
                var carried = seeds.Select(r => $"{r.m_resItem.m_itemData.m_shared.m_name} carried {player.GetInventory().CountItems(r.m_resItem.m_itemData.m_shared.m_name)}");
                notes.Add($"{prefab.name} (biomes {plant.m_biome}; {string.Join(", ", carried.ToArray())}): " +
                          (problems.Count == 0 ? (chosen == candidate ? "chosen" : "usable") : string.Join(", ", problems.ToArray())));
            }
            if (plants == 0)
                notes.Add($"the cultivator table has no Plant pieces among its {table.m_pieces.Count} pieces");
            if (laterOnly > 0) notes.Add($"{laterOnly} plant piece(s) for later biomes only, not listed");
            report = string.Join("; ", notes.ToArray());
            return chosen;
        }

        // Candidate grid centres: eight directions around the player's facing, near to far, so the
        // nearest cell stays at least 1.5 m from the player and the farthest centre is 12 m out.
        // The first centre whose every cell passes the product's own rules is used.
        private static bool FindPatch(Player player, Piece piece, Plant plant, Quaternion rotation, float spacing,
            out Vector3 centre, out List<Vector3> cells, out string report)
        {
            var notes = new List<string>();
            var reach = spacing * 1.42f + 1.5f;
            var distances = new[] { 4f, 8f, 12f }.Select(d => Mathf.Max(d, reach)).Where(d => d <= Mathf.Max(12f, reach))
                .Distinct().ToArray();
            foreach (var distance in distances)
            for (var step = 0; step < 8; step++)
            {
                var angle = step * 45f;
                var direction = Quaternion.Euler(0f, player.transform.eulerAngles.y + angle, 0f) * Vector3.forward;
                centre = player.transform.position + direction * distance;
                centre.y = ZoneSystem.instance.GetGroundHeight(centre);
                cells = ExpectedCells(centre, rotation, Size, spacing);
                var passed = 0;
                string failure = null;
                foreach (var cell in cells)
                {
                    var pos = cell;
                    pos.y = ZoneSystem.instance.GetGroundHeight(pos);
                    var reason = CellProblem(piece, plant, pos);
                    if (reason == null) passed++;
                    else if (failure == null) failure = reason;
                }
                if (failure == null)
                {
                    report = $"{angle:F0}° from facing at {distance:F1} m";
                    return true;
                }
                notes.Add($"{angle:F0}°/{distance:F1}m {passed}/{cells.Count} ({failure})");
            }
            centre = Vector3.zero;
            cells = null;
            report = string.Join("; ", notes.ToArray());
            return false;
        }

        // The rules CropPlanting.TryPlant applies before it places a crop, in its order: build
        // access, CanPlantAt (biome, cultivation, roof, grow space) and the slope limit for pieces
        // that refuse tilting ground. Grass has no collider, so it never blocks grow space.
        private static string CellProblem(Piece piece, Plant plant, Vector3 pos)
        {
            if (!Heightmap.FindHeightmap(pos)) return "no terrain";
            if (!PrivateArea.CheckAccess(pos, 0f, false)) return "no build access";
            if (!Planting.CanPlantAt(plant, pos, out var status))
            {
                if (status != Plant.Status.NoSpace) return status.ToString();
                var blockers = Physics.OverlapSphere(pos, plant.m_growRadius,
                    LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid"));
                var blocker = blockers.Select(c => c.GetComponentInParent<Plant>() ? null : c.transform.root.name)
                    .FirstOrDefault(n => n != null);
                return "grow space blocked by " + (blocker ?? "a growing plant");
            }
            if (piece.m_notOnTiltingSurface &&
                Physics.Raycast(pos + Vector3.up, Vector3.down, out var hit, 4f, LayerMask.GetMask("terrain")) &&
                hit.normal.y < 0.8f)
                return $"slope {hit.normal.y:F2} < 0.80";
            return null;
        }

        // The oracle for the grid shape, written out here instead of asking GridPlanting: every
        // other cell of a size x size lattice around the hand-placed crop, rotated with it.
        private static List<Vector3> ExpectedCells(Vector3 centre, Quaternion rotation, int size, float spacing)
        {
            var cells = new List<Vector3>();
            var low = -(size / 2);
            for (var dz = low; dz < low + size; dz++)
            for (var dx = low; dx < low + size; dx++)
                if (dx != 0 || dz != 0)
                    cells.Add(centre + rotation * new Vector3(dx * spacing, 0f, dz * spacing));
            return cells;
        }

        // An even size has no centre cell: the hand-placed crop stays one lattice cell and the
        // grid as a whole sits half a cell off it on both axes.
        private static void CheckEvenSize(FeatureRun run, Vector3 centre, Quaternion rotation, float spacing)
        {
            const int size = 4;
            var cells = Grid.BuildCells(centre, rotation, size, spacing);
            var inverse = Quaternion.Inverse(rotation);
            var offsets = cells.Select(pos =>
            {
                var local = inverse * (pos - centre);
                return new Vector2(local.x / spacing, local.z / spacing);
            }).ToList();
            var onLattice = offsets.All(o => Mathf.Abs(o.x - Mathf.Round(o.x)) < 0.01f && Mathf.Abs(o.y - Mathf.Round(o.y)) < 0.01f);
            var distinct = offsets.Select(o => new Vector2Int(Mathf.RoundToInt(o.x), Mathf.RoundToInt(o.y))).Distinct().ToList();
            var all = distinct.Concat(new[] { Vector2Int.zero }).ToList();
            var meanX = (float)all.Average(o => o.x);
            var meanZ = (float)all.Average(o => o.y);
            var span = all.Max(o => o.x) - all.Min(o => o.x) == size - 1 && all.Max(o => o.y) - all.Min(o => o.y) == size - 1;
            Check(run, "planting_even_size_offset",
                cells.Count == size * size - 1 && distinct.Count == cells.Count && !distinct.Contains(Vector2Int.zero) &&
                onLattice && span && Mathf.Abs(Mathf.Abs(meanX) - 0.5f) < 0.01f && Mathf.Abs(Mathf.Abs(meanZ) - 0.5f) < 0.01f,
                $"cells={cells.Count}, distinct={distinct.Count}, lattice={onLattice}, span={span}, mean offset=({meanX}, {meanZ}) cells");
        }

        private static void CheckGrid(FeatureRun run, Player player, Piece piece, ItemDrop.ItemData tool,
            Piece.Requirement requirement, List<ItemDrop.ItemData> added, List<GameObject> created,
            List<Vector3> cells, Vector3 centre, Quaternion rotation, Limit limit)
        {
            string name;
            switch (limit)
            {
                case Limit.Seeds: name = "planting_seed_limit"; break;
                case Limit.Blocked: name = "planting_invalid_cell_skipped"; break;
                case Limit.Stamina: name = "planting_stamina_limit"; break;
                case Limit.Durability: name = "planting_durability_limit"; break;
                case Limit.ToolSwapped: name = "planting_tool_swapped"; break;
                default: name = "planting_grid_costs"; break;
            }
            var inventory = player.GetInventory();
            var seedName = requirement.m_resItem.m_itemData.m_shared.m_name;
            var perCrop = requirement.GetAmount(0);
            var affordable = limit == Limit.Seeds ? 2 : cells.Count;
            var seeds = requirement.m_resItem.m_itemData.Clone();
            seeds.m_stack = affordable * perCrop;
            seeds.m_dropPrefab = requirement.m_resItem.gameObject;
            seeds.m_worldLevel = (byte)Game.m_worldLevel;
            if (inventory.CountItems(seedName) != 0 || !inventory.AddItem(seeds))
            {
                run.Skip(name, "could not add an isolated test seed stack");
                return;
            }
            added.Add(seeds);
            var stamina = (float)Stamina.GetValue(player);
            var delay = (float)StaminaDelay.GetValue(player);
            var durability = tool.m_durability;
            var staminaBefore = stamina;
            var durabilityBefore = durability;
            var staminaCost = (float)AccessTools.Method(typeof(Player), "GetBuildStamina").Invoke(player, null);
            var durabilityCost = (float)AccessTools.Method(typeof(Player), "GetPlaceDurability")
                .Invoke(player, new object[] { tool }) * Game.m_durabilityRate;
            var start = created.Count;
            try
            {
                var expectedCells = cells;
                var exact = true;
                int expected;
                switch (limit)
                {
                    case Limit.Seeds:
                        expected = 2;
                        exact = false;
                        break;
                    case Limit.Blocked:
                    {
                        var blocked = cells[0];
                        blocked.y = ZoneSystem.instance.GetGroundHeight(blocked);
                        var blocker = new GameObject("Automatics smoke planting blocker");
                        created.Add(blocker);
                        SmokeSession.TrackSpawn(blocker);
                        blocker.layer = LayerMask.NameToLayer("piece");
                        blocker.transform.position = blocked;
                        blocker.AddComponent<BoxCollider>().size = Vector3.one * 0.5f;
                        Physics.SyncTransforms();
                        if (Planting.CanPlantAt(piece.GetComponent<Plant>(), blocked, out _))
                        {
                            run.Skip(name, "the test blocker did not invalidate its cell");
                            return;
                        }
                        expectedCells = cells.Skip(1).ToList();
                        expected = expectedCells.Count;
                        break;
                    }
                    case Limit.Stamina:
                        // HaveStamina compares the raw cost and UseStamina spends it scaled, so this
                        // leaves room for exactly two charges.
                        Stamina.SetValue(player, staminaCost + 1.5f * staminaCost * Game.m_staminaRate);
                        staminaBefore = (float)Stamina.GetValue(player);
                        expected = 2;
                        exact = false;
                        break;
                    case Limit.Durability:
                        if (!tool.m_shared.m_useDurability || durabilityCost <= 0f)
                        {
                            run.Skip(name, "the test cultivator does not wear on placement");
                            return;
                        }
                        // Any durability above zero allows one more crop; half a charge allows one.
                        tool.m_durability = durabilityCost * 0.5f;
                        durabilityBefore = tool.m_durability;
                        expected = 1;
                        exact = false;
                        break;
                    case Limit.ToolSwapped:
                        expected = 0;
                        expectedCells = new List<Vector3>();
                        break;
                    default:
                        expected = cells.Count;
                        break;
                }

                Grid.RecordPlacement(player, piece, true, centre, rotation, true);
                var recorded = Grid.HasPending;
                ItemDrop.ItemData other = null;
                if (limit == Limit.ToolSwapped)
                {
                    other = inventory.AddItem("Cultivator", 1, 1, 0, 0L, "", false);
                    if (other != null) added.Add(other);
                    if (other == null || !player.EquipItem(other, false))
                    {
                        Grid.ClearPending();
                        run.Skip(name, "could not equip a second test cultivator");
                        return;
                    }
                }
                Grid.ConsumePending(player);
                if (other != null) player.EquipItem(tool, false);
                var plants = created.Skip(start).Where(x => x && x.GetComponent<Plant>()).ToArray();
                // Compare on the ground plane: the oracle knows nothing of terrain height.
                Func<Vector3, Vector3, bool> same = (a, b) => new Vector2(a.x - b.x, a.z - b.z).magnitude < 0.05f;
                var positionsMatch = plants.All(x => expectedCells.Count(pos => same(x.transform.position, pos)) == 1) &&
                                     expectedCells.All(pos => plants.Count(x => same(x.transform.position, pos)) <= 1) &&
                                     (!exact || expectedCells.All(pos => plants.Any(x => same(x.transform.position, pos))));
                var seedsSpent = affordable * perCrop - inventory.CountItems(seedName);
                var staminaSpent = staminaBefore - (float)Stamina.GetValue(player);
                var durabilitySpent = durabilityBefore - tool.m_durability;
                var expectedStamina = expected * staminaCost * Game.m_staminaRate;
                Check(run, name, recorded && !Grid.HasPending && plants.Length == expected && positionsMatch &&
                    seedsSpent == expected * perCrop &&
                    Mathf.Abs(staminaSpent - expectedStamina) < 0.02f &&
                    Mathf.Abs(durabilitySpent - expected * durabilityCost) < 0.02f,
                    $"plants={plants.Length}/{expected}, seeds={seedsSpent}, stamina={staminaSpent}/{expectedStamina}, durability={durabilitySpent}/{expected * durabilityCost}, positions={positionsMatch}");
                Grid.ConsumePending(player);
                Check(run, name + "_consumed_once", created.Skip(start).Count(x => x && x.GetComponent<Plant>()) == plants.Length,
                    "a second consume planted nothing", "a consumed request planted again");
            }
            finally
            {
                Grid.ClearPending();
                if (!player.IsItemEquiped(tool)) player.EquipItem(tool, false);
                RemoveObjects(created.Skip(start).ToList());
                created.RemoveRange(start, created.Count - start);
                if (inventory.ContainsItem(seeds)) inventory.RemoveItem(seeds);
                Stamina.SetValue(player, stamina);
                StaminaDelay.SetValue(player, delay);
                tool.m_durability = durability;
                Physics.SyncTransforms();
            }
        }

        private static void PlantAwake(Plant __instance)
        {
            if (_created == null || !__instance || __instance.gameObject.name != _prefabName + "(Clone)") return;
            var go = __instance.gameObject;
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
                // Destroy is deferred until the frame ends; turn off the colliders before the
                // next grid so it cannot see the previous run's test plants.
                foreach (var collider in go.GetComponentsInChildren<Collider>()) collider.enabled = false;
                if (SmokeSession.DestroyTestObject(go, 0, Label)) SmokeSession.ReleaseSpawn(go);
            }
        }

        private static bool Set<T>(FeatureRun run, string field, T value, out SmokeSession.SettingRestoration setting)
        {
            setting = null;
            var entry = AccessTools.Field(typeof(FarmingConfig), field)?.GetValue(null) as ConfigEntry<T>;
            if (entry != null && SmokeSession.BeginSetting(run.RunId, entry, out setting, out var refusal) &&
                SmokeSession.WriteSetting(run.RunId, setting, value, out refusal)) return true;
            run.Skip("planting_section", "could not record and apply test setting " + field);
            return false;
        }

        private static void Check(FeatureRun run, string name, bool ok, string detail)
        {
            if (ok) run.Pass(name, detail);
            else run.Fail(name, detail);
        }

        private static void Check(FeatureRun run, string name, bool ok, string passDetail, string failDetail)
        {
            if (ok) run.Pass(name, passDetail);
            else run.Fail(name, failDetail);
        }
    }
}
