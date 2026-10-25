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
using Snapping = Automatics.AutomaticFarming.LatticeSnapping;
using Held = System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase,
    Automatics.SmokeTest.SmokeSession.SettingRestoration>;

namespace Automatics.SmokeTest
{
    // All planting and accounting happen synchronously. Only seeds absent from the player's
    // inventory are used (the player's own are put aside for the section), and every added item
    // is tracked by reference. No terrain is changed. Before that the section turns the module and
    // grid planting on for the run when they are off, teaches the player the sapling piece, fills
    // their stamina and, when no patch around them is plantable, moves them to one that is.
    // The extra crops raise no Farming skill and no build stats by design, so neither is checked.
    internal static class PlantingFeatureSmoke
    {
        private const string Label = "planting feature smoke";
        private const int Size = 3;
        private const float SpacingFactor = 2.5f;
        // Where the real-ghost checks aim, in cells from the first grid's centre along its axes:
        // past its +x edge, off the free lattice cell by a fraction of a cell; and beside its +x
        // edge cell, whose crop sends the ghost on to the nearest free corner.
        private static readonly Vector2Int FreeCell = new Vector2Int(Size / 2 + 2, 0);
        private static readonly Vector2 FreeAim = new Vector2(FreeCell.x + 0.35f, 0.3f);
        private static readonly Vector2 OccupiedAim = new Vector2(Size / 2 + 0.3f, 0.25f);
        private static List<GameObject> _created;
        private static string _prefabName;
        private static readonly System.Reflection.FieldInfo Stamina = AccessTools.Field(typeof(Player), "m_stamina");
        private static readonly System.Reflection.FieldInfo StaminaDelay = AccessTools.Field(typeof(Player), "m_staminaRegenTimer");
        private static readonly System.Reflection.FieldInfo NoPlacementCost = AccessTools.Field(typeof(Player), "m_noPlacementCost");
        private static readonly System.Reflection.MethodInfo AddKnownPiece = AccessTools.Method(typeof(Player), "AddKnownPiece");
        private static readonly System.Reflection.MethodInfo UpdateAvailablePieces = AccessTools.Method(typeof(Player), "UpdateAvailablePiecesList");

        private enum Limit
        {
            None,
            Seeds,
            Blocked,
            Stamina,
            Durability,
            ToolSwapped
        }

        // The settings go back and are reported when the section ends however it ends, and the
        // player is then moved back to where the command started when the section moved them.
        public static IEnumerator Run(FeatureRun run)
        {
            var steps = Section(run);
            try
            {
                while (steps.MoveNext()) yield return steps.Current;
            }
            finally
            {
                (steps as IDisposable)?.Dispose();
                SmokeSetup.ReportSettings(run, "planting_settings_restored");
            }

            var home = new SmokeSetup.SetupResult();
            var back = SmokeSetup.ReturnToStart(run.RunId, home);
            while (back.MoveNext()) yield return back.Current;
            if (!home.Ok)
                FeaturesSmokeCommand.Warn($"[Automatics Smoke] {Label}: {home.Reason}; the run's hand-back moves you back");
        }

        private static IEnumerator Section(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !ZNetScene.instance || !ZoneSystem.instance ||
                !ObjectDB.instance || !ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("planting_section", "requires a loaded single-player world");
                yield break;
            }

            var held = new Held();
            var setup = new List<string>();
            // Grid checks drive planting directly; ordinary harvest/replant must stay idle
            // during module activation, relocation and frame boundaries.
            if (!Set(run, held, "_farmingInterval", 0f, out _)) yield break;
            setup.Add("farming_interval set to 0 for the run");
            var module = new SmokeSetup.SetupResult();
            var enable = SmokeSetup.EnableModule(run.RunId, held, FarmingConfig.ModuleEntry, () => FarmingModule.Running, module);
            while (enable.MoveNext()) yield return enable.Current;
            if (!module.Ok)
            {
                run.Skip("planting_section", module.Reason);
                yield break;
            }
            setup.Add("module " + module.Reason);
            if (!FarmingConfig.EnableGridPlanting)
            {
                if (!Set(run, held, "_enableGridPlanting", true, out _)) yield break;
                setup.Add("enable_grid_planting turned on for the run");
            }
            if (!player || Grid.HasPending || player.IsDead() || player.InAttack() || Stamina == null || StaminaDelay == null)
            {
                run.Skip("planting_section", "player state is not ready or stamina fields could not be resolved");
                yield break;
            }

            var cultivator = ObjectDB.instance.GetItemPrefab("Cultivator")?.GetComponent<ItemDrop>();
            var table = cultivator?.m_itemData.m_shared.m_buildPieces;
            var biome = Heightmap.FindBiome(player.transform.position);
            var piece = SelectSapling(table, biome, out var candidates);
            var nonPlant = table?.m_pieces.Where(x => x).Select(x => x.GetComponent<Piece>())
                .FirstOrDefault(x => x && !x.GetComponent<Plant>());
            if (!piece)
            {
                run.Skip("planting_section", $"no usable tree sapling in biome {biome}: {candidates}");
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
            var staminaBack = false;
            var harmony = new Harmony("net.eidee.valheim.automatics.smoke.planting");
            Func<string, bool> handBack = _ =>
            {
                if (!player) return true;
                if (originalNoCost) NoPlacementCost.SetValue(player, true);
                if (originalGodMode) player.SetGodMode(true);
                if (!staminaBack)
                {
                    Stamina.SetValue(player, initialStamina);
                    StaminaDelay.SetValue(player, initialDelay);
                    staminaBack = true;
                }
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
            SmokeSetup.InventoryStash stash = null;
            try
            {
                // Room for the test cultivators and seed stacks, and none of the sapling's seed in
                // the inventory, so the accounting below counts only the section's own. What is
                // put aside goes back at the end.
                var seedName = piece.m_resources.First(r => r.m_resItem && r.GetAmount(0) > 0).m_resItem.m_itemData.m_shared.m_name;
                stash = SmokeSetup.Stash(player, 3, 0f, x => x.m_shared.m_name == seedName);
                // Registered after the stash's own hand-back and before anything it undoes is
                // changed: hand-backs run in reverse order, so a run cut short removes the test
                // items before the stash puts your items back into their slots.
                SmokeSession.TrackHandBack(handBack);
                var freeSlots = inventory.GetEmptySlots();
                var carried = inventory.CountItems(seedName);
                if (freeSlots < 3 || carried != 0)
                {
                    run.Skip("planting_section", $"could not put items aside: needs 3 free inventory slots, has {freeSlots}; " +
                        $"needs none of {seedName}, carries {carried}");
                    yield break;
                }
                setup.Add(stash.Report);

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
                if (!Set(run, held, "_gridPlantingSize", Size, out _) || !Set(run, held, "_sowingSpacingFactor", SpacingFactor, out _) ||
                    !Set(run, held, "_gridPlantingKey", new KeyboardShortcut(KeyCode.LeftAlt), out var keySetting)) yield break;
                var tool = inventory.AddItem("Cultivator", 1, 1, 0, 0L, "", false);
                if (tool != null) added.Add(tool);
                if (tool == null || !player.EquipItem(tool, false))
                {
                    run.Skip("planting_section", "could not add and equip the test cultivator");
                    yield break;
                }
                // The build table offers only known pieces, and the real-ghost check selects the
                // sapling there. Learning it is kept by the character, which the test character
                // accepts.
                if (!player.IsRecipeKnown(piece.m_name) && AddKnownPiece != null && UpdateAvailablePieces != null)
                {
                    AddKnownPiece.Invoke(player, new object[] { piece });
                    UpdateAvailablePieces.Invoke(player, null);
                    setup.Add($"learned the piece {piece.name}");
                }
                var cost = (float)AccessTools.Method(typeof(Player), "GetBuildStamina").Invoke(player, null);
                var maxStamina = player.GetMaxStamina();
                if (cost <= 0f || Game.m_staminaRate <= 0f || maxStamina <= cost * 8f * Mathf.Max(1f, Game.m_staminaRate))
                {
                    run.Skip("planting_section", $"even full stamina ({maxStamina:F0}) is not enough to plant eight saplings");
                    yield break;
                }
                // Full stamina for the grids; the hand-back puts back what you had.
                Stamina.SetValue(player, maxStamina);
                setup.Add($"stamina set to its maximum {maxStamina:F0} and put back afterwards");
                // A yaw the game's placement can produce (a multiple of its 22.5 degree step), as the
                // row snapping rounds an anchor's yaw to that step.
                var rotation = Quaternion.Euler(0f, Mathf.Round((player.transform.eulerAngles.y + 25f) / 22.5f) * 22.5f, 0f);
                var plant = piece.GetComponent<Plant>();
                var spacing = plant.m_growRadius * SpacingFactor;
                var yaw = player.transform.eulerAngles.y;
                // A patch whose cells, and the cells the real-ghost checks use beside it, are all
                // plantable. When no site has one, a patch whose own cells are plantable is used, and
                // ghostSite says why in the SKIPs of the real-ghost checks.
                string ghostSite = null;
                if (!FindPatch(player.transform.position, yaw, piece, plant, rotation, spacing, true, out var centre, out var cells, out var patchReport))
                {
                    // No such patch around the player: move to a spot that has one, then search again.
                    var site = new SmokeSetup.SetupResult();
                    var relocate = SmokeSetup.Relocate(run.RunId,
                        p => FindPatch(p, yaw, piece, plant, rotation, spacing, true, out _, out _, out _) ? null : "no plantable patch around it",
                        site);
                    while (relocate.MoveNext()) yield return relocate.Current;
                    var here = patchReport;
                    if (!player)
                    {
                        run.Skip("planting_section", "the local player went away during the setup");
                        yield break;
                    }
                    if (site.Ok &&
                        FindPatch(player.transform.position, yaw, piece, plant, rotation, spacing, true, out centre, out cells, out patchReport))
                        setup.Add(site.Reason);
                    else
                    {
                        var ghostReport = site.Ok ? $"after the player {site.Reason}: {patchReport}" : "relocation: " + site.Reason;
                        if (!FindPatch(player.transform.position, yaw, piece, plant, rotation, spacing, false, out centre, out cells, out patchReport))
                        {
                            // Nor a patch of the grid's own cells here: move to a site that has one.
                            var plain = new SmokeSetup.SetupResult();
                            var retry = SmokeSetup.Relocate(run.RunId,
                                p => FindPatch(p, yaw, piece, plant, rotation, spacing, false, out _, out _, out _) ? null : "no plantable patch around it",
                                plain);
                            while (retry.MoveNext()) yield return retry.Current;
                            if (!player)
                            {
                                run.Skip("planting_section", "the local player went away during the setup");
                                yield break;
                            }
                            if (!plain.Ok ||
                                !FindPatch(player.transform.position, yaw, piece, plant, rotation, spacing, false, out centre, out cells, out patchReport))
                            {
                                run.Skip("planting_section", $"no patch around the player where all {Size * Size - 1} cells are plantable " +
                                    $"(spacing {spacing:F2} m): {patchReport}; with the real-ghost cells: {here}; {ghostReport}; " +
                                    $"relocation for the grid's cells alone: {(plain.Ok ? patchReport : plain.Reason)}");
                                yield break;
                            }
                            site = plain;
                        }
                        ghostSite = $"no site was found where the cells of the real-ghost checks are plantable ({here}; {ghostReport})";
                        if (site.Ok) setup.Add(site.Reason);
                        setup.Add(ghostSite + ", so those checks may SKIP");
                    }
                }
                run.Pass("planting_setup", string.Join("; ", setup.ToArray()));
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
                CheckLatticeSnapping(run, player, piece, tool, requirement, added, created, centre, rotation, spacing, keySetting, harmony, ghostSite);
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
                        // The added items go first, so what was put aside has its room back.
                        if (handBack(Label)) SmokeSession.ReleaseHandBack(handBack);
                        else run.Fail("planting_equipment_restored", "previous equipment could not be restored; hand-back will retry");
                        stash?.Restore();
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
        // needs no cultivated ground, has no terrain modifier, takes one kind of seed, and grows in
        // the player's biome. Every Plant piece's verdict is described so a SKIP names what failed.
        private static Piece SelectSapling(PieceTable table, Heightmap.Biome biome, out string report)
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
                if ((biome & plant.m_biome) == 0) problems.Add("wrong biome");
                if (problems.Count == 0 && !chosen) chosen = candidate;
                if ((plant.m_biome & EarlyBiomes) == 0 && chosen != candidate)
                {
                    laterOnly++;
                    continue;
                }
                notes.Add($"{prefab.name} (biomes {plant.m_biome}): " +
                          (problems.Count == 0 ? (chosen == candidate ? "chosen" : "usable") : string.Join(", ", problems.ToArray())));
            }
            if (plants == 0)
                notes.Add($"the cultivator table has no Plant pieces among its {table.m_pieces.Count} pieces");
            if (laterOnly > 0) notes.Add($"{laterOnly} plant piece(s) for later biomes only, not listed");
            report = string.Join("; ", notes.ToArray());
            return chosen;
        }

        // Candidate grid centres: eight directions around a facing (yaw) from a spot the player
        // stands on or may be moved to, near to far, so the nearest cell stays at least 1.5 m from
        // the spot and the farthest centre is 12 m out. The first centre whose every cell passes
        // the product's own rules, and with ghost every cell the real-ghost checks use too, is used.
        private static bool FindPatch(Vector3 origin, float yaw, Piece piece, Plant plant, Quaternion rotation, float spacing,
            bool ghost, out Vector3 centre, out List<Vector3> cells, out string report)
        {
            var notes = new List<string>();
            var reach = spacing * 1.42f + 1.5f;
            var distances = new[] { 4f, 8f, 12f }.Select(d => Mathf.Max(d, reach)).Where(d => d <= Mathf.Max(12f, reach))
                .Distinct().ToArray();
            foreach (var distance in distances)
            for (var step = 0; step < 8; step++)
            {
                var angle = step * 45f;
                var direction = Quaternion.Euler(0f, yaw + angle, 0f) * Vector3.forward;
                centre = origin + direction * distance;
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
                if (failure == null && ghost) failure = GhostCellProblem(piece, plant, centre, rotation, spacing);
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

        // The cells the real-ghost checks plant at beyond the first grid, as they pick them: the
        // free lattice cell; the nearest corner outside the first grid of the square aimed beside
        // its edge cell, where the ghost moves on to; and the corners of the free cell's square,
        // the first square the blocker checks try. A world object taking their grow space would
        // make those checks SKIP.
        private static string GhostCellProblem(Piece piece, Plant plant, Vector3 centre, Quaternion rotation, float spacing)
        {
            Func<Vector2, Vector3> point = offset =>
            {
                var pos = centre + rotation * new Vector3(offset.x * spacing, 0f, offset.y * spacing);
                pos.y = ZoneSystem.instance.GetGroundHeight(pos);
                return pos;
            };
            var moved = SquareCorners(point(OccupiedAim), centre, rotation, spacing)
                .First(c => Mathf.Abs(c.x) > Size / 2 || Mathf.Abs(c.y) > Size / 2);
            foreach (var cell in new[] { FreeCell, moved }.Concat(SquareCorners(point(FreeAim), centre, rotation, spacing)).Distinct())
            {
                var problem = CellProblem(piece, plant, point(cell));
                if (problem != null) return $"real-ghost cell {cell}: {problem}";
            }
            return null;
        }

        // The rules CropPlanting.TryPlant applies before it places a crop, in its order: the ground
        // checks (biome, cultivation, heat, cold) and grow space, then build access, the roof check
        // and the slope limit for pieces that refuse tilting ground. Grass has no collider, so it
        // never blocks grow space.
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

        // The oracle for lattice snapping, written out here instead of asking LatticeSnapping: a
        // position lies on the first grid's lattice when its ground-plane offset from the first
        // grid's centre, turned back by the first grid's rotation, is a whole number of cells on
        // both axes; a yaw lies on it when it differs from the first grid's by whole quarter turns.
        private static bool OnLattice(Vector3 pos, Vector3 centre, Quaternion rotation, float spacing, out Vector2Int cell)
        {
            var local = Quaternion.Inverse(rotation) * new Vector3(pos.x - centre.x, 0f, pos.z - centre.z);
            var x = local.x / spacing;
            var z = local.z / spacing;
            cell = new Vector2Int(Mathf.RoundToInt(x), Mathf.RoundToInt(z));
            return Mathf.Abs(x - cell.x) * spacing < 0.05f && Mathf.Abs(z - cell.y) * spacing < 0.05f;
        }

        private static bool YawOnLattice(float yaw, Quaternion rotation)
        {
            var delta = Mathf.DeltaAngle(rotation.eulerAngles.y, yaw);
            return Mathf.Abs(delta - Mathf.Round(delta / 90f) * 90f) < 0.5f;
        }

        // Plants a first grid, then drives a second placement next to it through the snapping path:
        // the lattice pose directly, the real Player.UpdatePlacementGhost with the modifier active
        // and released, and a second grid planted at the snapped pose. Afterwards the first grid is
        // removed and the same aim must no longer snap. The key is unbound (always active) on entry.
        private static void CheckLatticeSnapping(FeatureRun run, Player player, Piece piece, ItemDrop.ItemData tool,
            Piece.Requirement requirement, List<ItemDrop.ItemData> added, List<GameObject> created,
            Vector3 centre, Quaternion rotation, float spacing, SmokeSession.SettingRestoration keySetting, Harmony harmony,
            string ghostSite)
        {
            var inventory = player.GetInventory();
            var seedName = requirement.m_resItem.m_itemData.m_shared.m_name;
            var seeds = requirement.m_resItem.m_itemData.Clone();
            seeds.m_stack = 2 * (Size * Size - 1) * requirement.GetAmount(0);
            seeds.m_dropPrefab = requirement.m_resItem.gameObject;
            seeds.m_worldLevel = (byte)Game.m_worldLevel;
            if (inventory.CountItems(seedName) != 0 || !inventory.AddItem(seeds))
            {
                run.Skip("planting_lattice_section", "could not add an isolated test seed stack");
                return;
            }
            added.Add(seeds);
            var stamina = (float)Stamina.GetValue(player);
            var delay = (float)StaminaDelay.GetValue(player);
            var durability = tool.m_durability;
            var start = created.Count;
            try
            {
                Stamina.SetValue(player, player.GetMaxStamina());
                Grid.RecordPlacement(player, piece, true, centre, rotation, true);
                Grid.ConsumePending(player);
                var first = created.Skip(start).Where(x => x && x.GetComponent<Plant>()).ToList();
                if (first.Count == 0)
                {
                    run.Skip("planting_lattice_section", "the first grid planted no crop");
                    return;
                }

                // Aimed past the first grid's +x edge and off the lattice by a fraction of a cell, with
                // a player rotation that is not a quarter turn from the grid's.
                var edge = Size / 2;
                var expectedCell = FreeCell;
                var raw = centre + rotation * new Vector3(FreeAim.x * spacing, 0f, FreeAim.y * spacing);
                raw.y = ZoneSystem.instance.GetGroundHeight(raw);
                var playerYaw = rotation.eulerAngles.y + 100f;
                var snapped = Snapping.TryLatticePose(piece, true, raw, playerYaw, out var pose, out var yaw);
                var onLattice = OnLattice(pose, centre, rotation, spacing, out var cell);
                Check(run, "planting_lattice_snap_pose",
                    snapped && onLattice && cell == expectedCell && YawOnLattice(yaw, rotation) &&
                    Mathf.Abs(Mathf.DeltaAngle(yaw, rotation.eulerAngles.y + 90f)) < 0.5f,
                    $"snapped={snapped}, onLattice={onLattice}, cell={cell}/{expectedCell}, yaw={yaw:F1} (grid {rotation.eulerAngles.y:F1}, player {playerYaw:F1})");

                var released = Snapping.TryLatticePose(piece, false, raw, playerYaw, out var releasedPose, out var releasedYaw);
                Check(run, "planting_lattice_no_modifier",
                    !released && releasedPose == raw && Mathf.Approximately(releasedYaw, playerYaw),
                    $"snapped={released}, moved={(releasedPose - raw).magnitude:F3} m");

                var far = centre + rotation * new Vector3((edge + 1 + Size + 0.6f) * spacing, 0f, 0f);
                far.y = ZoneSystem.instance.GetGroundHeight(far);
                var farSnapped = Snapping.TryLatticePose(piece, true, far, playerYaw, out _, out _);
                var nearest = first.Min(x => new Vector2(x.transform.position.x - far.x, x.transform.position.z - far.z).magnitude);
                Check(run, "planting_lattice_out_of_radius",
                    !farSnapped && nearest > Snapping.SearchRadius(spacing, Size),
                    $"snapped={farSnapped}, nearest crop {nearest:F2} m, radius {Snapping.SearchRadius(spacing, Size):F2} m");

                var ghostPose = CheckRealGhost(run, player, piece, harmony, keySetting, raw, centre, rotation, spacing, expectedCell, first,
                    ghostSite);
                var secondCentre = ghostPose.HasValue ? ghostPose.Value.Key : pose;
                var secondYaw = ghostPose.HasValue ? ghostPose.Value.Value : yaw;
                if (snapped)
                {
                    secondCentre.y = ZoneSystem.instance.GetGroundHeight(secondCentre);
                    var before = created.Count;
                    Stamina.SetValue(player, player.GetMaxStamina());
                    Grid.RecordPlacement(player, piece, true, secondCentre, Quaternion.Euler(0f, secondYaw, 0f), true);
                    Grid.ConsumePending(player);
                    var second = created.Skip(before).Where(x => x && x.GetComponent<Plant>()).ToList();
                    var off = second.Where(x => !OnLattice(x.transform.position, centre, rotation, spacing, out _) ||
                                                !YawOnLattice(x.transform.eulerAngles.y, rotation)).ToList();
                    if (second.Count == 0) run.Skip("planting_lattice_grid_aligned", "the second grid found no plantable cell");
                    else
                        Check(run, "planting_lattice_grid_aligned", off.Count == 0,
                            $"{second.Count} crop(s) of the second grid (centre from {(ghostPose.HasValue ? "the real ghost" : "the lattice pose")}) on the first grid's lattice, {off.Count} off it");
                }
                else run.Skip("planting_lattice_grid_aligned", "the aimed point did not snap");

                // Both grids go, so no crop of the kind is left anywhere near the aim.
                RemoveObjects(created.Skip(start).ToList());
                Physics.SyncTransforms();
                var gone = Snapping.TryLatticePose(piece, true, raw, playerYaw, out _, out _);
                Check(run, "planting_lattice_no_crop_near", !gone,
                    "with both grids removed the same aim does not snap", "the same aim still snapped with both grids removed");
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

        private static readonly System.Reflection.FieldInfo PlacementGhostField = AccessTools.Field(typeof(Player), "m_placementGhost");
        private static readonly System.Reflection.FieldInfo PlacementStatusField = AccessTools.Field(typeof(Player), "m_placementStatus");
        private static readonly System.Reflection.FieldInfo PlaceRotationField = AccessTools.Field(typeof(Player), "m_placeRotation");
        private static readonly System.Reflection.FieldInfo PlaceDegreesField = AccessTools.Field(typeof(Player), "m_placeRotationDegrees");
        private static readonly System.Reflection.FieldInfo BuildPiecesField = AccessTools.Field(typeof(Player), "m_buildPieces");
        private static readonly System.Reflection.MethodInfo UpdateGhost = AccessTools.Method(typeof(Player), "UpdatePlacementGhost");
        private static readonly System.Reflection.MethodInfo SetupGhost = AccessTools.Method(typeof(Player), "SetupPlacementGhost");

        // Runs the real Player.UpdatePlacementGhost with the camera briefly looking straight down at
        // an aimed point: with the unbound (always active) key at the free lattice cell (vanilla must
        // call the snapped pose Valid), next to an occupied cell (the ghost moves on to a free corner
        // of the aimed lattice square), with blockers taking the grow space of the aimed square's
        // corners (the ghost moves on, or stays refused as needing more space), and at a point whose
        // lattice point lies just beyond the reach (no snap); then with Left Alt released. The real
        // Player.TryPlacePiece is driven at the free cell, the moved ghost and the refused one with
        // PlacePiece intercepted. Nothing renders in between: the camera, the reach, the piece
        // selection and the rotation step are put back and the blockers removed in the same frame.
        // Returns the snapped ghost pose, or null when the path could not be driven. ghostSite says
        // why the setup found no site where the cells used here are plantable, when it did not.
        private static KeyValuePair<Vector3, float>? CheckRealGhost(FeatureRun run, Player player, Piece piece,
            Harmony harmony, SmokeSession.SettingRestoration keySetting, Vector3 raw, Vector3 centre,
            Quaternion rotation, float spacing, Vector2Int expectedCell, List<GameObject> first, string ghostSite)
        {
            Func<string, string> unplantable = reason => ghostSite == null ? reason : $"{reason}; {ghostSite}";
            const string name = "planting_lattice_real_ghost";
            var table = BuildPiecesField?.GetValue(player) as PieceTable;
            var camera = GameCamera.instance ? GameCamera.instance.transform : null;
            var place = AccessTools.Method(typeof(Player), nameof(Player.PlacePiece));
            var blockers = new List<GameObject>();
            if (PlacementGhostField == null || PlacementStatusField == null || PlaceRotationField == null ||
                PlaceDegreesField == null || UpdateGhost == null || SetupGhost == null || place == null || !table || !camera)
            {
                run.Skip(name, "placement fields, the build table or the camera could not be resolved");
                return null;
            }
            // Captured before the selection changes: selecting a piece can re-roll the rotation step.
            var originalSteps = (int)PlaceRotationField.GetValue(player);
            var originalDegrees = (float)PlaceDegreesField.GetValue(player);
            var category = table.GetSelectedCategory();
            var index = table.GetSelectedIndex();
            var cameraPosition = camera.position;
            var cameraRotation = camera.rotation;
            var reach = player.m_maxPlaceDistance;
            KeyValuePair<Vector3, float>? result = null;
            try
            {
                if (!player.SetSelectedPiece(piece))
                {
                    run.Skip(name, "the test sapling is not available in the build table (recipe unknown)");
                    return null;
                }
                var ghost = PlacementGhostField.GetValue(player) as GameObject;
                if (!ghost || !ghost.name.StartsWith(piece.name))
                {
                    SetupGhost.Invoke(player, null);
                    ghost = PlacementGhostField.GetValue(player) as GameObject;
                }
                if (!ghost || !ghost.GetComponent<Plant>())
                {
                    run.Skip(name, "no placement ghost of the test sapling");
                    return null;
                }
                harmony.Patch(place, prefix: new HarmonyMethod(typeof(PlantingFeatureSmoke), nameof(InterceptPlacePiece)));
                var steps = (int)PlaceRotationField.GetValue(player);
                var degrees = (float)PlaceDegreesField.GetValue(player);
                // Points the camera straight down at the point and runs one ghost update.
                Func<Vector3, float, string> aim = (point, maxDistance) =>
                {
                    camera.SetPositionAndRotation(point + Vector3.up * 3f,
                        Quaternion.LookRotation(Vector3.down, rotation * Vector3.forward));
                    player.m_maxPlaceDistance = maxDistance;
                    UpdateGhost.Invoke(player, new object[] { false });
                    return PlacementStatusField.GetValue(player).ToString();
                };
                Func<Vector3, float> roomy = point => Vector3.Distance(player.m_eye.position, point) + 4f;
                Func<Vector3, Vector3> ground = point =>
                {
                    point.y = ZoneSystem.instance.GetGroundHeight(point);
                    return point;
                };
                Func<Vector2Int, Vector3> cellPoint = cell => ground(centre + rotation * new Vector3(cell.x * spacing, 0f, cell.y * spacing));
                // A reach that takes in every corner of a lattice square, so none is passed over as out of reach.
                Func<List<Vector2Int>, float> squareReach = corners => corners.Max(c => roomy(cellPoint(c)));
                var plant = piece.GetComponent<Plant>();

                if (!Grid.IsModifierHeld(player, FarmingConfig.GridPlantingKey))
                    run.Skip(name, "release the alternative placement button to drive the real ghost");
                else
                {
                    var status = aim(raw, roomy(raw));
                    var restored = (int)PlaceRotationField.GetValue(player) == steps &&
                                   Mathf.Approximately((float)PlaceDegreesField.GetValue(player), degrees);
                    var freeCell = ground(centre + rotation * new Vector3(expectedCell.x * spacing, 0f, expectedCell.y * spacing));
                    var problem = CellProblem(piece, plant, freeCell);
                    var freeValid = false;
                    if (status == "NoRayHits") run.Skip(name, "the camera ray hit nothing at the aimed point");
                    else if (problem != null)
                    {
                        run.Skip(name, unplantable("the free lattice cell is not plantable here: " + problem));
                        run.Skip(name + "_try_place", unplantable("the free lattice cell is not plantable here"));
                    }
                    else
                    {
                        var pos = ghost.transform.position;
                        var yaw = ghost.transform.eulerAngles.y;
                        var onLattice = OnLattice(pos, centre, rotation, spacing, out var cell);
                        var grounded = Mathf.Abs(pos.y - ZoneSystem.instance.GetGroundHeight(pos)) < 0.5f;
                        var ok = onLattice && cell == expectedCell && YawOnLattice(yaw, rotation) && restored && grounded &&
                                 status == "Valid";
                        Check(run, name, ok,
                            $"ghost cell={cell}/{expectedCell}, onLattice={onLattice}, yaw={yaw:F1} (grid {rotation.eulerAngles.y:F1}), " +
                            $"rotation step restored={restored}, on ground={grounded}, vanilla status={status} (Valid expected)");
                        if (ok)
                        {
                            result = new KeyValuePair<Vector3, float>(pos, yaw);
                            freeValid = true;
                            CheckTryPlace(run, name + "_try_place", player, piece, true, pos, ghost.transform.rotation);
                        }
                        else run.Skip(name + "_try_place", "the snapped ghost at the free cell was not valid");
                    }

                    // Next to a cell of the first grid: that cell is the nearest corner of the aimed
                    // square, and its crop leaves no room there, so the ghost moves on to the nearest
                    // corner without a crop. The square's far column lies outside the first grid, so
                    // one always exists.
                    var occupiedCell = new Vector2Int(Size / 2, 0);
                    Func<Vector2Int, bool> occupied = cell => first.Any(x =>
                        x && OnLattice(x.transform.position, centre, rotation, spacing, out var c) && c == cell);
                    var aimed = ground(centre + rotation * new Vector3(OccupiedAim.x * spacing, 0f, OccupiedAim.y * spacing));
                    var aimedSquare = SquareCorners(aimed, centre, rotation, spacing);
                    var movedCell = aimedSquare.First(c => !occupied(c));
                    var movedProblem = CellProblem(piece, plant, cellPoint(movedCell));
                    string occupiedSkip = null;
                    if (!occupied(occupiedCell))
                        occupiedSkip = "the first grid has no crop in the cell next to the aim";
                    else if (movedProblem != null)
                        occupiedSkip = unplantable($"the nearest free corner {movedCell} of the aimed square is not plantable here: {movedProblem}");
                    else if ((status = aim(aimed, squareReach(aimedSquare))) == "NoRayHits")
                        occupiedSkip = "the camera ray hit nothing at the aimed point";
                    if (occupiedSkip != null)
                    {
                        run.Skip(name + "_occupied", occupiedSkip);
                        run.Skip(name + "_occupied_try_place", occupiedSkip);
                    }
                    else
                    {
                        var onLattice = OnLattice(ghost.transform.position, centre, rotation, spacing, out var cell);
                        var yaw = ghost.transform.eulerAngles.y;
                        var ok = onLattice && cell == movedCell && YawOnLattice(yaw, rotation) && status == "Valid";
                        Check(run, name + "_occupied", ok,
                            $"ghost cell={cell}/{movedCell} (occupied {occupiedCell} nearest), onLattice={onLattice}, " +
                            $"yaw={yaw:F1} (grid {rotation.eulerAngles.y:F1}), vanilla status={status} (Valid expected)");
                        if (ok)
                            CheckTryPlace(run, name + "_occupied_try_place", player, piece, true,
                                ghost.transform.position, ghost.transform.rotation);
                        else run.Skip(name + "_occupied_try_place", "the ghost did not move to the free corner as valid");
                    }

                    // The same aim as at the free cell, with blockers on a grow-space layer beside the
                    // aimed square's corners, outside the square: each lies inside its corner's grow
                    // sphere but clear of the ray down onto that corner, so vanilla alone would still
                    // call the corner Valid. A blocker beside the nearest corner moves the ghost to the
                    // next corner; one beside every corner keeps it on the nearest corner, refused as
                    // needing more space; with the blockers gone the nearest corner is Valid again.
                    // Natural rocks can keep a corner of one fixed square blocked, so the square is
                    // searched: the free cell's square first (the aim raw), then squares whose lowest
                    // corner lies two to four cells beyond either side of the first grid, rows -2 to 1,
                    // in a fixed order. Each candidate is aimed at the same fractional offsets (.35, .3)
                    // as raw, so its nearest corner is the lowest one and the second-nearest is the
                    // next along the grid's x axis. The first candidate whose four corners all pass the
                    // planting rules is used for every check below; every corner of every candidate lies
                    // outside the first grid.
                    var candidateAims = new List<Vector3> { raw };
                    foreach (var lowX in new[] { 2, 3, 4, -5, -4, -3 })
                        foreach (var lowZ in new[] { -2, -1, 0, 1 })
                            if (lowX != expectedCell.x || lowZ != expectedCell.y)
                                candidateAims.Add(ground(centre + rotation *
                                                         new Vector3((lowX + 0.35f) * spacing, 0f, (lowZ + 0.3f) * spacing)));
                    var squareAim = raw;
                    var square = SquareCorners(raw, centre, rotation, spacing);
                    var squareProblems = new List<string>();
                    var squareFound = false;
                    foreach (var candidateAim in candidateAims)
                    {
                        var candidateSquare = SquareCorners(candidateAim, centre, rotation, spacing);
                        var candidateProblem = candidateSquare
                            .Select(q => CellProblem(piece, plant, cellPoint(q))).FirstOrDefault(q => q != null);
                        if (candidateProblem == null)
                        {
                            squareAim = candidateAim;
                            square = candidateSquare;
                            squareFound = true;
                            break;
                        }
                        squareProblems.Add(candidateProblem);
                    }
                    var squareProblem = squareFound
                        ? null
                        : unplantable($"none of the {candidateAims.Count} lattice squares tried near the first grid has four plantable corners (first: " +
                                      string.Join("; ", squareProblems.Take(3)) + ")");
                    var low = new Vector2Int(square.Min(c => c.x), square.Min(c => c.y));
                    // True when the blocker took the grow space of the corner.
                    Func<Vector2Int, bool> block = corner =>
                    {
                        var away = rotation * new Vector3(corner.x == low.x ? -1f : 1f, 0f, corner.y == low.y ? -1f : 1f);
                        var blocker = new GameObject("Automatics smoke planting blocker");
                        blockers.Add(blocker);
                        SmokeSession.TrackSpawn(blocker);
                        blocker.layer = LayerMask.NameToLayer("piece");
                        blocker.transform.position = ground(cellPoint(corner) + away.normalized * (0.6f * plant.m_growRadius));
                        blocker.AddComponent<BoxCollider>().size = Vector3.one * (0.4f * plant.m_growRadius);
                        Physics.SyncTransforms();
                        return CellProblem(piece, plant, cellPoint(corner)) != null;
                    };
                    string blockedSkip = null;
                    if (!freeValid) blockedSkip = "the check at the free cell did not pass";
                    else if (squareProblem != null) blockedSkip = squareProblem;
                    else if (!block(square[0])) blockedSkip = "the test blocker did not take the grow space of the nearest corner";
                    var reachAll = squareReach(square);
                    var refused = false;
                    if (blockedSkip != null)
                    {
                        foreach (var suffix in new[] { "_blocked_nearest", "_blocked_square", "_blocked_square_try_place" })
                            run.Skip(name + suffix, blockedSkip);
                    }
                    else
                    {
                        status = aim(squareAim, reachAll);
                        var onLattice = OnLattice(ghost.transform.position, centre, rotation, spacing, out var cell);
                        var yaw = ghost.transform.eulerAngles.y;
                        if (status == "NoRayHits") run.Skip(name + "_blocked_nearest", "the camera ray hit nothing at the aimed point");
                        else
                            Check(run, name + "_blocked_nearest",
                                onLattice && cell == square[1] && YawOnLattice(yaw, rotation) && status == "Valid",
                                $"ghost cell={cell}/{square[1]} (blocked {square[0]} nearest), onLattice={onLattice}, " +
                                $"yaw={yaw:F1} (grid {rotation.eulerAngles.y:F1}), vanilla status={status} (Valid expected)");

                        if (!square.Skip(1).All(block))
                        {
                            run.Skip(name + "_blocked_square", "a test blocker did not take the grow space of its corner");
                            run.Skip(name + "_blocked_square_try_place", "a test blocker did not take the grow space of its corner");
                        }
                        else if ((status = aim(squareAim, reachAll)) == "NoRayHits")
                        {
                            run.Skip(name + "_blocked_square", "the camera ray hit nothing at the aimed point");
                            run.Skip(name + "_blocked_square_try_place", "the camera ray hit nothing at the aimed point");
                        }
                        else
                        {
                            refused = true;
                            onLattice = OnLattice(ghost.transform.position, centre, rotation, spacing, out cell);
                            yaw = ghost.transform.eulerAngles.y;
                            Check(run, name + "_blocked_square",
                                onLattice && cell == square[0] && YawOnLattice(yaw, rotation) && status == "MoreSpace",
                                $"ghost cell={cell}/{square[0]}, onLattice={onLattice}, yaw={yaw:F1} (grid {rotation.eulerAngles.y:F1}), " +
                                $"status={status} (MoreSpace expected)");
                            CheckTryPlace(run, name + "_blocked_square_try_place", player, piece, false,
                                ghost.transform.position, ghost.transform.rotation);
                        }
                    }
                    RemoveObjects(blockers);
                    blockers.Clear();
                    Physics.SyncTransforms();
                    if (!refused)
                        run.Skip(name + "_unblocked", blockedSkip ?? "the aimed square was not refused with every corner blocked");
                    else if ((status = aim(squareAim, reachAll)) == "NoRayHits")
                        run.Skip(name + "_unblocked", "the camera ray hit nothing at the aimed point");
                    else
                    {
                        var onLattice = OnLattice(ghost.transform.position, centre, rotation, spacing, out var cell);
                        Check(run, name + "_unblocked", onLattice && cell == square[0] && status == "Valid",
                            $"with the blockers removed: ghost cell={cell}/{square[0]}, onLattice={onLattice}, vanilla status={status} (Valid expected)");
                    }

                    // The free cell's lattice point just beyond the reach, the aimed point just within.
                    var lattice = ground(centre + rotation * new Vector3(expectedCell.x * spacing, 0f, expectedCell.y * spacing));
                    var toward = player.m_eye.position - lattice;
                    toward.y = 0f;
                    var aimedNear = ground(lattice + toward.normalized * (0.4f * spacing));
                    var nearCellOk = OnLattice(lattice, centre, rotation, spacing, out _) &&
                                     NearestCell(aimedNear, centre, rotation, spacing) == expectedCell;
                    var dAim = Vector3.Distance(player.m_eye.position, aimedNear);
                    var dLattice = Vector3.Distance(player.m_eye.position, lattice);
                    if (toward.sqrMagnitude < 0.01f || !nearCellOk || dLattice - dAim < 0.1f)
                        run.Skip(name + "_beyond_reach", $"no aim whose lattice point is farther (aim {dAim:F2} m, lattice point {dLattice:F2} m)");
                    else
                    {
                        var limit = (dAim + dLattice) * 0.5f - ghost.GetComponent<Piece>().m_extraPlacementDistance;
                        status = aim(aimedNear, limit);
                        var pos = ghost.transform.position;
                        var moved = new Vector2(pos.x - aimedNear.x, pos.z - aimedNear.z).magnitude;
                        var yawKept = Mathf.Abs(Mathf.DeltaAngle(ghost.transform.eulerAngles.y, degrees * steps)) < 0.5f;
                        if (status == "NoRayHits") run.Skip(name + "_beyond_reach", "the camera ray hit nothing at the aimed point");
                        else
                            Check(run, name + "_beyond_reach", moved < 0.05f && yawKept,
                                $"reach {limit:F2} m, aim {dAim:F2} m, lattice point {dLattice:F2} m: ghost {moved:F3} m from the aimed point, player rotation kept={yawKept}, vanilla status={status}");
                    }
                }

                if (!SmokeSession.WriteSetting(run.RunId, keySetting, new KeyboardShortcut(KeyCode.LeftAlt), out var refusal))
                    run.Skip(name + "_released", "could not bind Left Alt: " + refusal);
                else if (Grid.IsModifierHeld(player, FarmingConfig.GridPlantingKey))
                    run.Skip(name + "_released", "release Left Alt to check the real ghost without the modifier");
                else
                {
                    var status = aim(raw, roomy(raw));
                    var pos = ghost.transform.position;
                    var moved = new Vector2(pos.x - raw.x, pos.z - raw.z).magnitude;
                    var yawKept = Mathf.Abs(Mathf.DeltaAngle(ghost.transform.eulerAngles.y, degrees * steps)) < 0.5f;
                    if (status == "NoRayHits") run.Skip(name + "_released", "the camera ray hit nothing at the aimed point");
                    else
                        Check(run, name + "_released", moved < 0.05f && yawKept,
                            $"ghost {moved:F3} m from the aimed point, player rotation kept={yawKept}, vanilla status={status}");
                }
                if (!SmokeSession.WriteSetting(run.RunId, keySetting, new KeyboardShortcut(), out refusal))
                    run.Fail(name + "_released", "could not unbind the key again: " + refusal);
            }
            finally
            {
                _interceptPlace = false;
                Grid.ClearPending();
                RemoveObjects(blockers);
                Physics.SyncTransforms();
                camera.SetPositionAndRotation(cameraPosition, cameraRotation);
                player.m_maxPlaceDistance = reach;
                if (BuildPiecesField.GetValue(player) as PieceTable == table)
                {
                    player.SetBuildCategory(category);
                    table.SetSelected(index);
                    SetupGhost.Invoke(player, null);
                }
                PlaceRotationField.SetValue(player, originalSteps);
                PlaceDegreesField.SetValue(player, originalDegrees);
            }
            return result;
        }

        private static Vector2Int NearestCell(Vector3 pos, Vector3 centre, Quaternion rotation, float spacing)
        {
            OnLattice(pos, centre, rotation, spacing, out var cell);
            return cell;
        }

        // The cells at the four corners of the lattice square that holds the point, nearest first
        // on the ground plane: the order in which the snapped ghost tries them.
        private static List<Vector2Int> SquareCorners(Vector3 pos, Vector3 centre, Quaternion rotation, float spacing)
        {
            var local = Quaternion.Inverse(rotation) * new Vector3(pos.x - centre.x, 0f, pos.z - centre.z) / spacing;
            var x = Mathf.FloorToInt(local.x);
            var z = Mathf.FloorToInt(local.z);
            return new[] { new Vector2Int(x, z), new Vector2Int(x + 1, z), new Vector2Int(x, z + 1), new Vector2Int(x + 1, z + 1) }
                .OrderBy(c => new Vector2(c.x - local.x, c.y - local.z).sqrMagnitude).ToList();
        }

        private static bool _interceptPlace;
        private static int _placeCalls;
        private static Vector3 _placedPosition;
        private static Quaternion _placedRotation;

        private static bool InterceptPlacePiece(Vector3 pos, Quaternion rot)
        {
            if (!_interceptPlace) return true;
            _placeCalls++;
            _placedPosition = pos;
            _placedRotation = rot;
            return false;
        }

        // Drives the real Player.TryPlacePiece at the current aim with PlacePiece intercepted, so
        // nothing is placed. The player's no-cost toggle is on for the call, so vanilla counts the
        // placement as cheated and grants no achievement progress; the build statistics it still
        // increments are put back afterwards, and the grid request the placement records is dropped.
        private static void CheckTryPlace(FeatureRun run, string name, Player player, Piece piece, bool expectPlaced,
            Vector3 ghostPosition, Quaternion ghostRotation)
        {
            if (NoPlacementCost == null || PlayerProfile.s_bypassCheatChecks || !Game.instance ||
                Game.instance.GetPlayerProfile() == null)
            {
                run.Skip(name, "cannot mark the placement as cheated, so it could count toward achievements");
                return;
            }
            var stats = Game.instance.GetPlayerProfile().m_playerStats;
            var builds = new Dictionary<int, float?>();
            var placedStats = new Dictionary<int, float?>();
            for (var i = 0; i < stats.Length; i++)
            {
                if (stats[i] == null) continue;
                builds[i] = stats[i].m_stats.TryGetValue(PlayerStatType.Builds, out var b) ? b : (float?)null;
                placedStats[i] = stats[i].m_piecesPlacedStats.TryGetValue(piece.m_name, out var p) ? p : (float?)null;
            }
            var noCost = (bool)NoPlacementCost.GetValue(player);
            bool placed;
            _placeCalls = 0;
            NoPlacementCost.SetValue(player, true);
            _interceptPlace = true;
            try
            {
                placed = player.TryPlacePiece(piece);
            }
            finally
            {
                _interceptPlace = false;
                NoPlacementCost.SetValue(player, noCost);
                Grid.ClearPending();
                foreach (var entry in builds)
                {
                    if (entry.Value.HasValue) stats[entry.Key].m_stats[PlayerStatType.Builds] = entry.Value.Value;
                    else stats[entry.Key].m_stats.Remove(PlayerStatType.Builds);
                }
                foreach (var entry in placedStats)
                {
                    if (entry.Value.HasValue) stats[entry.Key].m_piecesPlacedStats[piece.m_name] = entry.Value.Value;
                    else stats[entry.Key].m_piecesPlacedStats.Remove(piece.m_name);
                }
            }
            if (expectPlaced)
            {
                var moved = Vector3.Distance(_placedPosition, ghostPosition);
                var turned = Quaternion.Angle(_placedRotation, ghostRotation);
                Check(run, name, placed && _placeCalls == 1 && moved < 0.01f && turned < 0.5f,
                    $"placed={placed}, PlacePiece calls={_placeCalls}, {moved:F3} m and {turned:F1}° from the snapped ghost pose");
            }
            else
                Check(run, name, !placed && _placeCalls == 0, $"placed={placed}, PlacePiece calls={_placeCalls} (rejected expected)");
        }

        private static void PlantAwake(Plant __instance)
        {
            // A placement ghost is instantiated with network init disabled; it belongs to the player.
            if (_created == null || !__instance || ZNetView.m_forceDisableInit ||
                __instance.gameObject.name != _prefabName + "(Clone)") return;
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

        private static bool Set<T>(FeatureRun run, Held held, string field, T value, out SmokeSession.SettingRestoration setting)
        {
            setting = null;
            var entry = SmokeSetup.Field<T>(typeof(FarmingConfig), field);
            var refusal = "the setting is not bound";
            if (entry != null && SmokeSetup.Write(run.RunId, held, entry, value, out refusal))
            {
                setting = held[entry];
                return true;
            }
            run.Skip("planting_section", $"could not record and apply test setting {field}: {refusal}");
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
