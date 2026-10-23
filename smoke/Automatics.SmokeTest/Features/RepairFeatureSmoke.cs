using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using RepairConfig = Automatics.AutomaticRepair.Config;
using RepairItems = Automatics.AutomaticRepair.ItemRepair;
using RepairModule = Automatics.AutomaticRepair.Module;
using RepairPieces = Automatics.AutomaticRepair.PieceRepair;

namespace Automatics.SmokeTest
{
    // The `repair` section of automatics_smoke_features: Automatic Repair's periodic piece pass
    // and its two item paths (the periodic pass and the crafting-station interaction). It calls
    // the module's own entries, `PieceRepair.Repair`, `ItemRepair.Repair` and
    // `ItemRepair.CraftingStationInteractHook`, against non-persistent objects it spawns next to
    // the player, and every check runs in one frame, so the module's own timers cannot act on
    // the test objects between the set-up and the call.
    //
    // Pieces: stationless pieces and wood walls (which need a workbench in range) are spawned at
    // a health below their maximum, and undamaged ones of both kinds beside them. The checks
    // read which pieces the pass repaired, whether an undamaged piece caused a repair call, a
    // ward access check or a station lookup, how many times it looked up the build station,
    // whether a ward that does not grant you access was flashed (a test ward, with a control
    // flash that proves the observer sees one), and whether it stopped once the repair tool's
    // durability was used up. The repair tool is a hammer item
    // data the observer hands to the module in place of your equipped one, so your hands and
    // build selection are not touched.
    //
    // Items: two test workbenches are spawned a frame before the item checks, so that they are
    // in the game's station list. One worn test item that a workbench repairs and one that only
    // another station repairs are added to your inventory. The checks read the Crafting skill
    // raises, how many times each test station was asked whether it is usable, and how many times
    // the item's recipe was looked up. An item check shows the pass only its test items: no item
    // of yours is repaired, charged or counted.
    //
    // An observer patch, removed at the end of the section, records these calls only while a
    // check has it armed, drops the repair calls on pieces other than the test pieces while armed,
    // drops the flash of every ward but the test ward, and is registered with the session before its patches go on, so a run cut short removes
    // it. The previously held skill levels, your stamina, the settings and the test objects are
    // put back at the end or by the session's hand-back.
    //
    // The section sets up what it needs for the run: Automatic Repair is turned on when it is
    // off, piece_search_range and crafting_station_search_range are set to RequiredRange (so a
    // station of yours farther away is out of the item pass's reach), the player's no-cost
    // toggle is lifted, and items are put aside until two inventory slots are free. All of it is
    // put back at the end or by the session's hand-back.
    internal static class RepairFeatureSmoke
    {
        private const string Label = "repair feature smoke";
        private const string SectionCheck = "repair_section";
        private const string RestoreCheck = "repair_settings_restored";

        private const string HammerPrefab = "Hammer";
        private const string WorkbenchPrefab = "piece_workbench";
        private const string WardPrefab = "guard_stone";
        private const string StationedPiecePrefab = "woodwall";

        // Items of the earlier biomes. The first one whose recipe the test workbench repairs is
        // the worn item it repairs; the first one whose recipe is at some other station is the
        // worn item it does not. They are checked against ObjectDB at run time.
        private static readonly string[] WorkbenchItems = { "AxeStone", "AxeFlint", "Club", "KnifeFlint" };

        private static readonly string[] OtherStationItems =
            { "AxeBronze", "SwordBronze", "MaceBronze", "AxeIron", "SwordIron", "KnifeCopper" };

        // The ground distance at which test objects are placed; every one is well within the
        // module's search ranges, which the section sets to RequiredRange for the run.
        private const float Ring = 2.2f;
        private const int RequiredRange = 4;
        private const float WornFraction = 0.37f;
        private const int FreeSlots = 2;

        private static readonly FieldInfo NoPlacementCost = AccessTools.Field(typeof(Player), "m_noPlacementCost");

        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !ZNetScene.instance || !ObjectDB.instance ||
                !ZoneSystem.instance || !ZNet.instance || !ZNet.instance.IsServer() ||
                ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip(SectionCheck, "requires a loaded single-player world");
                yield break;
            }

            if (!ModuleRegistry.RuntimeToggleSupported)
            {
                run.Skip(SectionCheck,
                    "this Harmony build cannot remove patches at runtime, so the observer is not added");
                yield break;
            }

            if (player.IsDead() || player.IsTeleporting())
            {
                run.Skip(SectionCheck, "stand alive in the world, not in a teleport");
                yield break;
            }

            if (player.NoCostCheat() && NoPlacementCost == null)
            {
                run.Skip(SectionCheck,
                    "the no-cost cheat is on and Player.m_noPlacementCost was not found to lift it; turn the cheat off");
                yield break;
            }

            var hammerPrefab = ObjectDB.instance.GetItemPrefab(HammerPrefab);
            var hammer = hammerPrefab ? hammerPrefab.GetComponent<ItemDrop>() : null;
            if (hammer == null || hammer.m_itemData.m_shared.m_buildPieces == null)
            {
                run.Skip(SectionCheck, $"the {HammerPrefab} item is not available");
                yield break;
            }

            var stationed = FindStationedPiece();
            var stationless = FindStationlessPiece(hammer.m_itemData.m_shared.m_buildPieces);
            var workbenchPrefab = ZNetScene.instance.GetPrefab(WorkbenchPrefab);
            var workbenchStation = workbenchPrefab ? workbenchPrefab.GetComponent<CraftingStation>() : null;
            if (stationless == null || workbenchStation == null)
            {
                run.Skip(SectionCheck,
                    "no stationless building piece of the hammer or no workbench prefab is available");
                yield break;
            }

            var held = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            var spawned = new List<GameObject>();
            WoodcuttingFeatureSmoke.PlayerState state = null;
            SmokeSetup.InventoryStash stash = null;
            var noCostLifted = false;
            Func<string, bool> handBack = null;
            try
            {
                var module = new SmokeSetup.SetupResult();
                var steps = SmokeSetup.EnableModule(run.RunId, held, RepairConfig.ModuleEntry,
                    () => RepairModule.Running, module);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent) yield break;
                if (!module.Ok)
                {
                    run.Skip(SectionCheck, "Automatic Repair is not running: " + module.Reason);
                    yield break;
                }

                var pieceRange = SmokeSetup.Field<int>(typeof(RepairConfig), "_pieceSearchRange");
                var stationRange = SmokeSetup.Field<int>(typeof(RepairConfig), "_craftingStationSearchRange");
                if (pieceRange == null || stationRange == null ||
                    !SmokeSetup.Write(run, held, pieceRange, RequiredRange) ||
                    !SmokeSetup.Write(run, held, stationRange, RequiredRange))
                {
                    run.Skip(SectionCheck,
                        $"piece_search_range and crafting_station_search_range could not be set to {RequiredRange} for the run");
                    yield break;
                }

                state = new WoodcuttingFeatureSmoke.PlayerState(player,
                    new[] { Skills.SkillType.Crafting });
                handBack = _ =>
                {
                    if (noCostLifted && player) NoPlacementCost.SetValue(player, true);
                    noCostLifted = false;
                    return state.Restore();
                };
                SmokeSession.TrackHandBack(handBack);

                if (player.NoCostCheat())
                {
                    NoPlacementCost.SetValue(player, false);
                    noCostLifted = true;
                    run.Pass("repair_no_cost_lifted",
                        "the player's no-cost toggle is off for this section and restored afterwards");
                }

                // Registered after the hand-back above, so a run cut short takes the test items
                // out before the stashed ones go back.
                stash = SmokeSetup.Stash(player, FreeSlots);
                if (player.GetInventory().GetEmptySlots() < FreeSlots)
                {
                    run.Skip(SectionCheck,
                        $"needs {FreeSlots} free inventory slots for the test items; putting items aside left {player.GetInventory().GetEmptySlots()} ({stash.Report})");
                    yield break;
                }

                run.Pass("repair_setup",
                    $"Automatic Repair {module.Reason}; piece_search_range and crafting_station_search_range set to {RequiredRange} for the run; {stash.Report}");

                if (!RepairObserver.Attach(out var problem))
                {
                    run.Skip(SectionCheck, problem);
                    yield break;
                }

                var tool = hammer.m_itemData.Clone();
                var origin = player.transform.position;

                CheckPieceDamageFirst(run, player, state, tool, stationless, stationed, spawned, origin);
                CheckPieceWardNoFlash(run, player, state, tool, stationless, spawned, origin);
                if (stationed != null)
                    CheckPieceStationLookup(run, player, state, tool, stationed, spawned, origin);
                else
                    run.Skip("repair_piece_station_lookup_once",
                        $"the {StationedPiecePrefab} prefab is not a station-requiring piece");
                CheckPieceToolUsedUp(run, player, state, tool, stationless, spawned, origin);

                // The stations join the game's station list in their Start, a frame from now.
                var stations = new List<CraftingStation>();
                for (var i = 0; i < 2; i++)
                {
                    var spot = Spot(origin, 6 + i, 1.5f);
                    var go = WoodcuttingFeatureSmoke.SpawnTestObject(WorkbenchPrefab, spot,
                        Quaternion.identity, out var skip);
                    if (!go)
                    {
                        run.Skip("repair_item_section", skip);
                        break;
                    }

                    spawned.Add(go);
                    var station = go.GetComponent<CraftingStation>();
                    station.m_craftRequireRoof = false;
                    station.m_craftRequireFire = false;
                    stations.Add(station);
                }

                yield return null;
                if (!run.IsCurrent || stations.Count < 2) yield break;

                CheckInRangeStation(run, player, state, tool, stationed, spawned, stations[0],
                    player.transform.position);
                CheckItems(run, held, player, state, stations);
            }
            finally
            {
                try
                {
                    if (!RepairObserver.Detach(out var failure))
                        run.Fail("repair_observer_removed", failure);
                }
                finally
                {
                    try
                    {
                        if (state != null)
                        {
                            if (handBack(Label)) SmokeSession.ReleaseHandBack(handBack);
                            else
                                run.Fail("repair_player_restored",
                                    "the previously equipped items could not all be equipped again; it is retried at the next hand-back");
                        }
                    }
                    finally
                    {
                        // After the test items are out, so the stashed ones go back into their slots.
                        stash?.Restore();
                        try
                        {
                            if (held.Count > 0) ReportRestore(run);
                        }
                        finally
                        {
                            foreach (var go in spawned) WoodcuttingFeatureSmoke.RemoveTestObject(go);
                        }
                    }
                }
            }
        }

        // -- Piece checks -----------------------------------------------------------------------

        // Damaged pieces are repaired. An undamaged piece, one that needs no build station and
        // one that does, causes no repair call, no ward access check and no station lookup.
        private static void CheckPieceDamageFirst(FeatureRun run, Player player,
            WoodcuttingFeatureSmoke.PlayerState state, ItemDrop.ItemData tool, GameObject stationless,
            GameObject stationed, List<GameObject> spawned, Vector3 origin)
        {
            const string name = "repair_piece_damaged_only";
            var healthy = SpawnPiece(stationless, Spot(origin, 0, Ring), false, spawned, out var skip);
            var first = healthy ? SpawnPiece(stationless, Spot(origin, 1, Ring), true, spawned, out skip) : null;
            var second = first ? SpawnPiece(stationless, Spot(origin, 2, Ring), true, spawned, out skip) : null;
            var healthyStationed = second && stationed != null
                ? SpawnPiece(stationed, Spot(origin, 4, Ring), false, spawned, out skip)
                : null;
            if (!second || stationed != null && !healthyStationed)
            {
                run.Skip(name, skip);
                Retire(healthy, first, second, healthyStationed);
                return;
            }

            Stage(player, state);
            RunPieces(player, tool);
            var asked = RepairObserver.PieceCalls.Select(x => x.Piece).ToList();
            var repaired = RepairedOf(healthy, first, second, healthyStationed);
            var undamaged = new[] { healthy, healthyStationed }.Where(x => x).ToList();
            var accessed = undamaged.Count(x => WasAccessChecked(x));
            var lookups = healthyStationed
                ? RepairObserver.StationLookups.Count(x =>
                    x == healthyStationed.GetComponent<Piece>().m_craftingStation.m_name)
                : 0;

            if (!repaired.Contains(first) || !repaired.Contains(second) ||
                undamaged.Any(x => asked.Contains(x)) || accessed != 0 || lookups != 0)
                run.Fail(name,
                    $"repaired {repaired.Count} of the 2 damaged test pieces; {undamaged.Count(x => asked.Contains(x))} of {undamaged.Count} undamaged piece(s) asked to repair, {accessed} given a ward access check, {lookups} station lookup(s) for them");
            else if (!WasAccessChecked(first) || !WasAccessChecked(second))
                run.Skip(name, "the observer did not see the access checks of the damaged pieces");
            else
                run.Pass(name,
                    $"both damaged test pieces were repaired; {undamaged.Count} undamaged piece(s) caused no repair call, no ward access check and no station lookup");
            Retire(healthy, first, second, healthyStationed);
        }

        // Whether the pass made a ward access check at the piece's position.
        private static bool WasAccessChecked(WearNTear piece)
        {
            var position = piece.transform.position;
            return RepairObserver.AccessChecks.Any(x => (x - position).sqrMagnitude < 0.0001f);
        }

        // A damaged piece inside an enabled ward that does not grant you access is left alone
        // without flashing the ward, and a damaged piece outside it is repaired.
        private static void CheckPieceWardNoFlash(FeatureRun run, Player player,
            WoodcuttingFeatureSmoke.PlayerState state, ItemDrop.ItemData tool, GameObject stationless,
            List<GameObject> spawned, Vector3 origin)
        {
            const string name = "repair_piece_ward_no_flash";
            var wardSpot = Spot(origin, 3, Ring);
            var wardObject = WoodcuttingFeatureSmoke.SpawnTestObject(WardPrefab, wardSpot,
                Quaternion.identity, out var skip);
            if (!wardObject)
            {
                run.Skip(name, skip);
                return;
            }

            spawned.Add(wardObject);
            var ward = wardObject.GetComponent<PrivateArea>();
            if (!ward || !Objects.GetZNetView(wardObject.transform, out var wardView))
            {
                run.Skip(name, $"the {WardPrefab} prefab is not a ward");
                return;
            }

            // Only this ward may flash while the observer is armed; the control flash below
            // would otherwise also flash any ward of yours that covers the spot.
            RepairObserver.FixtureWard = ward;

            // A small area around the test ward, so that only the piece beside it is inside.
            ward.m_radius = 1f;
            wardView.GetZDO().Set(ZDOVars.s_enabled, true);

            var inside = SpawnPiece(stationless, wardSpot + Vector3.right * 0.3f, true, spawned, out skip);
            var outside = inside ? SpawnPiece(stationless, Spot(origin, 7, Ring), true, spawned, out skip) : null;
            try
            {
                if (!outside)
                {
                    run.Skip(name, skip);
                    return;
                }

                // A control: a check with flashing on is seen by the observer, and denied.
                RepairObserver.Arm(null);
                var granted = PrivateArea.CheckAccess(inside.transform.position, 0f, true);
                RepairObserver.Disarm();
                if (granted || !RepairObserver.Flashes.Contains(ward))
                {
                    run.Skip(name,
                        granted
                            ? "the test ward grants you access, so no denial can be observed"
                            : "the observer did not see the control flash");
                    return;
                }

                Stage(player, state);
                RunPieces(player, tool);
                var flashes = RepairObserver.Flashes.Count(x => x == ward);
                var repaired = RepairedOf(inside, outside);
                if (flashes > 0 || repaired.Contains(inside) || !repaired.Contains(outside))
                    run.Fail(name,
                        $"the pass flashed the ward {flashes} time(s); the piece inside the ward was {(repaired.Contains(inside) ? "repaired" : "left alone")} and the one outside it was {(repaired.Contains(outside) ? "repaired" : "left alone")}");
                else
                    run.Pass(name,
                        "the ward was not flashed, the piece inside it was left alone and the one outside it was repaired");
            }
            finally
            {
                RepairObserver.FixtureWard = null;
                Retire(inside, outside);
                wardView.GetZDO().Set(ZDOVars.s_enabled, false);
                spawned.Remove(wardObject);
                WoodcuttingFeatureSmoke.RemoveTestObject(wardObject);
            }
        }

        // Several station-requiring pieces cost one station lookup, and the pass repairs them all
        // or none, as the build station requirement says.
        private static void CheckPieceStationLookup(FeatureRun run, Player player,
            WoodcuttingFeatureSmoke.PlayerState state, ItemDrop.ItemData tool, GameObject stationed,
            List<GameObject> spawned, Vector3 origin)
        {
            const string name = "repair_piece_station_lookup_once";
            var pieces = new List<WearNTear>();
            for (var i = 0; i < 3; i++)
            {
                var piece = SpawnPiece(stationed, Spot(origin, i, Ring), true, spawned, out var skip);
                if (!piece)
                {
                    run.Skip(name, skip);
                    Retire(pieces.ToArray());
                    return;
                }

                pieces.Add(piece);
            }

            var stationName = stationed.GetComponent<Piece>().m_craftingStation.m_name;
            var expectRepaired = CraftingStation.HaveBuildStationInRange(stationName, player.transform.position) ||
                                 ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench);
            Stage(player, state);
            RunPieces(player, tool);
            var repaired = RepairedOf(pieces.ToArray());
            var lookups = RepairObserver.StationLookups.Count(x => x == stationName);
            var expected = expectRepaired ? pieces.Count : 0;
            if (lookups != 1 || repaired.Count != expected)
                run.Fail(name,
                    $"{lookups} station lookup(s) for 3 pieces, {repaired.Count} repaired, expected 1 lookup and {expected} repaired");
            else
                run.Pass(name,
                    $"one station lookup for 3 pieces; {(expectRepaired ? "all repaired, a build station is in range" : "none repaired, no build station is in range")}");
            Retire(pieces.ToArray());
        }

        // With a workbench in range the station-requiring pieces are repaired.
        private static void CheckInRangeStation(FeatureRun run, Player player,
            WoodcuttingFeatureSmoke.PlayerState state, ItemDrop.ItemData tool, GameObject stationed,
            List<GameObject> spawned, CraftingStation station, Vector3 origin)
        {
            const string name = "repair_piece_in_range_station_repairs";
            if (stationed == null)
            {
                run.Skip(name, $"the {StationedPiecePrefab} prefab is not a station-requiring piece");
                return;
            }

            var stationName = stationed.GetComponent<Piece>().m_craftingStation.m_name;
            if (stationName != station.m_name ||
                !CraftingStation.HaveBuildStationInRange(stationName, player.transform.position))
            {
                run.Skip(name, "the test workbench is not the build station of the test piece or is not in range");
                return;
            }

            var pieces = new List<WearNTear>();
            for (var i = 0; i < 2; i++)
            {
                var piece = SpawnPiece(stationed, Spot(origin, i, Ring), true, spawned, out var skip);
                if (!piece)
                {
                    run.Skip(name, skip);
                    Retire(pieces.ToArray());
                    return;
                }

                pieces.Add(piece);
            }

            Stage(player, state);
            RunPieces(player, tool);
            var repaired = RepairedOf(pieces.ToArray());
            var lookups = RepairObserver.StationLookups.Count(x => x == stationName);
            if (repaired.Count != pieces.Count || lookups != 1)
                run.Fail(name, $"{repaired.Count} of 2 pieces repaired with a workbench in range, {lookups} station lookup(s)");
            else
                run.Pass(name, "both pieces were repaired with the workbench in range, after one station lookup");
            Retire(pieces.ToArray());
        }

        // The pass stops once the repair tool's durability is used up: the repair that uses it
        // up is the last, and a tool that is already used up repairs nothing.
        private static void CheckPieceToolUsedUp(FeatureRun run, Player player,
            WoodcuttingFeatureSmoke.PlayerState state, ItemDrop.ItemData tool, GameObject stationless,
            List<GameObject> spawned, Vector3 origin)
        {
            const string name = "repair_piece_stops_when_tool_used_up";
            var shared = tool.m_shared;
            var cost = shared.m_useDurabilityDrain * Game.m_durabilityRate;
            if (!shared.m_useDurability || !(cost > 0f))
            {
                run.Skip(name, "the repair tool does not use durability");
                return;
            }

            var pieces = new List<WearNTear>();
            for (var i = 0; i < 3; i++)
            {
                var piece = SpawnPiece(stationless, Spot(origin, i, Ring), true, spawned, out var skip);
                if (!piece)
                {
                    run.Skip(name, skip);
                    Retire(pieces.ToArray());
                    return;
                }

                pieces.Add(piece);
            }

            var original = tool.m_durability;
            try
            {
                // Enough for one repair, which uses it up.
                tool.m_durability = cost * 0.5f;
                Stage(player, state);
                RunPieces(player, tool);
                var firstRun = RepairedOf(pieces.ToArray()).Count;
                var afterFirst = tool.m_durability;

                Stage(player, state);
                RunPieces(player, tool);
                var secondRun = RepairedOf(pieces.ToArray()).Count;
                if (firstRun != 1 || secondRun != 0 || afterFirst > 0f || !Mathf.Approximately(tool.m_durability, afterFirst))
                    run.Fail(name,
                        $"a tool with one repair left repaired {firstRun} piece(s) (durability {afterFirst:0.##} afterwards), and the used-up tool then repaired {secondRun}");
                else
                    run.Pass(name, "one repair used the durability up, and the used-up tool repaired nothing and lost nothing");
            }
            finally
            {
                tool.m_durability = original;
                Retire(pieces.ToArray());
            }
        }

        // -- Item checks ------------------------------------------------------------------------

        private static void CheckItems(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Player player,
            WoodcuttingFeatureSmoke.PlayerState state, List<CraftingStation> stations)
        {
            var first = stations[0];
            var second = stations[1];
            var stationName = first.m_name;

            var workbenchItem = AddWornItem(state, WorkbenchItems, stationName, true);
            var otherItem = AddWornItem(state, OtherStationItems, stationName, false);

            // The periodic pass visits every station in range, so a station of yours in range
            // could repair the test items before the test stations do.
            var foreign = ForeignStationInRange(player, stations);
            if (foreign != null)
            {
                var reason = $"a crafting station of yours is within {RepairConfig.CraftingStationSearchRange} m of you, the crafting_station_search_range set for the run; stand farther from it";
                foreach (var periodic in new[]
                         {
                             "repair_item_none_worn_touches_no_station", "repair_item_periodic_raises_crafting",
                             "repair_item_station_checked_once", "repair_item_recipe_resolved_once",
                             "repair_item_unusable_station_excluded"
                         })
                    run.Skip(periodic, reason);
            }
            else
            {
                CheckNoneWorn(run, player);
                if (workbenchItem == null)
                {
                    foreach (var periodic in new[]
                             {
                                 "repair_item_periodic_raises_crafting", "repair_item_station_checked_once",
                                 "repair_item_unusable_station_excluded"
                             })
                        run.Skip(periodic, "no earlier-biome item is repaired at a workbench");
                }
                else
                {
                    CheckPeriodicRepair(run, player, workbenchItem, first, second);
                    CheckUnusableStations(run, player, workbenchItem, first, second);
                }

                if (otherItem == null)
                    run.Skip("repair_item_recipe_resolved_once", "no earlier-biome item is repaired only at another station");
                else
                    CheckRecipeOnce(run, player, otherItem);
            }

            if (workbenchItem == null)
            {
                run.Skip("repair_item_interaction_raises_crafting", "no earlier-biome item is repaired at a workbench");
                run.Skip("repair_item_interaction_unusable_station_excluded",
                    "no earlier-biome item is repaired at a workbench");
                return;
            }

            var entry = AccessTools.Field(typeof(RepairConfig), "_repairItemsWhenAccessingTheCraftingStation")
                ?.GetValue(null) as ConfigEntry<bool>;
            if (entry == null || !WoodcuttingFeatureSmoke.Write(run, held, entry, true))
            {
                run.Skip("repair_item_interaction_raises_crafting",
                    "repair_items_when_accessing_the_crafting_station could not be turned on for the run");
                run.Skip("repair_item_interaction_unusable_station_excluded",
                    "repair_items_when_accessing_the_crafting_station could not be turned on for the run");
                return;
            }

            CheckInteraction(run, player, workbenchItem, first);
        }

        // With nothing worn, a pass does not ask any station whether it is usable, and looks up
        // no recipe.
        private static void CheckNoneWorn(FeatureRun run, Player player)
        {
            const string name = "repair_item_none_worn_touches_no_station";
            // Only the test items are visible to the pass, and none is worn.
            RepairObserver.Arm(null, new ItemDrop.ItemData[0]);
            RepairItems.Repair(player);
            RepairObserver.Disarm();
            var usable = RepairObserver.UsableChecks.Count;
            var recipes = RepairObserver.RecipeLookups.Count;
            if (usable != 0 || recipes != 0)
                run.Fail(name, $"with nothing worn the pass asked {usable} station(s) and looked up {recipes} recipe(s)");
            else
                run.Pass(name, "with nothing worn the pass asked no station and looked up no recipe");
        }

        // A worn item a workbench repairs is repaired by the periodic pass and raises the Crafting
        // skill by the wear it had, as a hand repair does; each station is asked once.
        private static void CheckPeriodicRepair(FeatureRun run, Player player,
            ItemDrop.ItemData item, CraftingStation first, CraftingStation second)
        {
            Wear(item);
            var expected = 1f - item.m_durability / item.GetMaxDurability();
            RepairObserver.Arm(null, new[] { item });
            RepairItems.Repair(player);
            RepairObserver.Disarm();

            var repaired = item.m_durability >= item.GetMaxDurability();
            var raised = RepairObserver.Raises.Any(x =>
                x.Skill == Skills.SkillType.Crafting && Mathf.Abs(x.Value - expected) < 0.001f);
            if (!repaired || !raised)
                run.Fail("repair_item_periodic_raises_crafting",
                    $"the worn item was {(repaired ? "repaired" : "not repaired")} and the Crafting skill was {(raised ? "raised by the wear" : $"not raised by {expected:0.###}")}");
            else
                run.Pass("repair_item_periodic_raises_crafting",
                    $"the worn item was repaired and the Crafting skill was raised by its wear, {expected:0.###}");

            var firstChecks = RepairObserver.UsableChecks.Count(x => x == first);
            var secondChecks = RepairObserver.UsableChecks.Count(x => x == second);
            if (firstChecks != 1 || secondChecks != 1)
                run.Fail("repair_item_station_checked_once",
                    $"the two stations in range were asked {firstChecks} and {secondChecks} time(s)");
            else
                run.Pass("repair_item_station_checked_once", "each station in range was asked once");
        }

        // An item that no station in range repairs costs one recipe lookup, however many
        // stations are in range, and stays worn.
        private static void CheckRecipeOnce(FeatureRun run, Player player, ItemDrop.ItemData item)
        {
            const string name = "repair_item_recipe_resolved_once";
            Wear(item);
            var before = item.m_durability;
            RepairObserver.Arm(null, new[] { item });
            RepairItems.Repair(player);
            RepairObserver.Disarm();
            var lookups = RepairObserver.RecipeLookups.Count(x => ReferenceEquals(x, item));
            var left = Mathf.Approximately(item.m_durability, before);
            if (lookups != 1 || !left)
                run.Fail(name,
                    $"{lookups} recipe lookup(s) for one item with two stations in range; the item was {(left ? "left worn" : "repaired")}");
            else
                run.Pass(name, "one recipe lookup for the item with two stations in range, and it stays worn");
        }

        // A station that is not usable repairs nothing, on the periodic pass and with the
        // interaction, and is still asked once.
        private static void CheckUnusableStations(FeatureRun run, Player player,
            ItemDrop.ItemData item, CraftingStation first, CraftingStation second)
        {
            const string name = "repair_item_unusable_station_excluded";
            first.m_craftRequireFire = true;
            second.m_craftRequireFire = true;
            try
            {
                Wear(item);
                var before = item.m_durability;
                RepairObserver.Arm(null, new[] { item });
                RepairItems.Repair(player);
                RepairObserver.Disarm();
                var left = Mathf.Approximately(item.m_durability, before);
                var raises = RepairObserver.Raises.Count(x => x.Skill == Skills.SkillType.Crafting);
                var asked = RepairObserver.UsableChecks.Count(x => x == first || x == second);
                if (!left || raises != 0 || asked != 2)
                    run.Fail(name,
                        $"with both stations unusable the item was {(left ? "left worn" : "repaired")}, {raises} skill raise(s), {asked} usability check(s)");
                else
                    run.Pass(name, "with both stations unusable nothing was repaired or raised, and each was asked once");
            }
            finally
            {
                first.m_craftRequireFire = false;
                second.m_craftRequireFire = false;
            }
        }

        // The interaction repairs the worn item at the station you open and raises the Crafting
        // skill by its wear, and keeps its own usability check, also on an unusable station.
        private static void CheckInteraction(FeatureRun run, Player player, ItemDrop.ItemData item,
            CraftingStation station)
        {
            Wear(item);
            var expected = 1f - item.m_durability / item.GetMaxDurability();
            RepairObserver.Arm(null, new[] { item });
            RepairItems.CraftingStationInteractHook(player, station);
            RepairObserver.Disarm();
            var repaired = item.m_durability >= item.GetMaxDurability();
            var raised = RepairObserver.Raises.Any(x =>
                x.Skill == Skills.SkillType.Crafting && Mathf.Abs(x.Value - expected) < 0.001f);
            var checks = RepairObserver.UsableChecks.Count(x => x == station);
            if (!repaired || !raised || checks != 1)
                run.Fail("repair_item_interaction_raises_crafting",
                    $"the worn item was {(repaired ? "repaired" : "not repaired")}, the Crafting skill was {(raised ? "raised by the wear" : $"not raised by {expected:0.###}")}, the station was asked {checks} time(s)");
            else
                run.Pass("repair_item_interaction_raises_crafting",
                    $"the worn item was repaired, the Crafting skill was raised by its wear, {expected:0.###}, and the station was asked once");

            station.m_craftRequireFire = true;
            try
            {
                Wear(item);
                var before = item.m_durability;
                RepairObserver.Arm(null, new[] { item });
                RepairItems.CraftingStationInteractHook(player, station);
                RepairObserver.Disarm();
                var left = Mathf.Approximately(item.m_durability, before);
                var raises = RepairObserver.Raises.Count(x => x.Skill == Skills.SkillType.Crafting);
                if (!left || raises != 0)
                    run.Fail("repair_item_interaction_unusable_station_excluded",
                        $"at an unusable station the item was {(left ? "left worn" : "repaired")} and {raises} skill raise(s) were made");
                else
                    run.Pass("repair_item_interaction_unusable_station_excluded",
                        "at an unusable station nothing was repaired or raised");
            }
            finally
            {
                station.m_craftRequireFire = false;
            }
        }

        // -- Helpers ----------------------------------------------------------------------------

        // The first item that the test workbench repairs (repairable true), or that no workbench
        // repairs (false), added worn to the inventory; null when no candidate fits.
        private static ItemDrop.ItemData AddWornItem(WoodcuttingFeatureSmoke.PlayerState state,
            string[] candidates, string workbenchName, bool repairable)
        {
            foreach (var candidate in candidates)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(candidate);
                var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null || !drop.m_itemData.m_shared.m_useDurability ||
                    !drop.m_itemData.m_shared.m_canBeReparied)
                    continue;

                var recipe = ObjectDB.instance.GetRecipe(drop.m_itemData);
                if (recipe == null || recipe.m_craftingStation == null && recipe.m_repairStation == null) continue;

                var atWorkbench = recipe.m_repairStation != null && recipe.m_repairStation.m_name == workbenchName ||
                                  recipe.m_craftingStation != null && recipe.m_craftingStation.m_name == workbenchName;
                if (atWorkbench != repairable) continue;
                if (repairable && recipe.m_minStationLevel > 1) continue;

                var item = state.Add(candidate);
                if (item == null) continue;

                // Repaired anywhere only at a station of its own recipe unless it is older than
                // the world level.
                item.m_worldLevel = (byte)Game.m_worldLevel;
                return item;
            }

            return null;
        }

        private static void Wear(ItemDrop.ItemData item)
        {
            item.m_durability = item.GetMaxDurability() * WornFraction;
        }

        // A station of yours, not one of the test stations, within the module's search range.
        private static CraftingStation ForeignStationInRange(Player player, List<CraftingStation> tests)
        {
            var all = Reflections.GetStaticField<CraftingStation, List<CraftingStation>>("m_allStations");
            if (all == null) return null;
            var range = RepairConfig.CraftingStationSearchRange;
            foreach (var station in all)
            {
                if (!station || tests.Contains(station)) continue;
                if (Vector3.Distance(player.transform.position, station.transform.position) <= range)
                    return station;
            }

            return null;
        }

        private static void RunPieces(Player player, ItemDrop.ItemData tool)
        {
            RepairObserver.Arm(tool);
            RepairPieces.Repair(player);
            RepairObserver.Disarm();
        }

        // Full stamina, so the pass is never stopped by the stamina gate.
        private static void Stage(Player player, WoodcuttingFeatureSmoke.PlayerState state)
        {
            if (state.CanSetStamina) state.SetStamina(player.GetMaxStamina(), 0f);
        }

        private static Vector3 Spot(Vector3 origin, int index, float distance)
        {
            var angle = index * 47f * Mathf.Deg2Rad;
            var spot = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
            spot.y = ZoneSystem.instance.GetGroundHeight(spot);
            return spot;
        }

        private static List<WearNTear> RepairedOf(params WearNTear[] pieces)
        {
            return RepairObserver.PieceCalls.Where(x => x.Result && pieces.Contains(x.Piece))
                .Select(x => x.Piece).ToList();
        }

        private static GameObject FindStationedPiece()
        {
            var prefab = ZNetScene.instance.GetPrefab(StationedPiecePrefab);
            var piece = prefab ? prefab.GetComponent<Piece>() : null;
            return piece && piece.m_craftingStation && prefab.GetComponent<WearNTear>() ? prefab : null;
        }

        // The first piece of the hammer's table that a hand can repair and that needs no build
        // station, with nothing on it that does more than stand there.
        private static GameObject FindStationlessPiece(PieceTable table)
        {
            foreach (var prefab in table.m_pieces)
            {
                var piece = prefab ? prefab.GetComponent<Piece>() : null;
                if (piece == null || piece.m_craftingStation != null || piece.m_repairPiece ||
                    !prefab.GetComponent<WearNTear>() || !prefab.GetComponent<ZNetView>()) continue;
                if (prefab.GetComponent<CraftingStation>() || prefab.GetComponent<PrivateArea>() ||
                    prefab.GetComponent<Container>() || prefab.GetComponent<Door>() ||
                    prefab.GetComponent<Ship>() || prefab.GetComponent<Vagon>() ||
                    prefab.GetComponent<Fireplace>() || prefab.GetComponent<Smelter>() ||
                    prefab.GetComponent<Bed>() || prefab.GetComponent<TeleportWorld>())
                    continue;
                return prefab;
            }

            return null;
        }

        private static WearNTear SpawnPiece(GameObject prefab, Vector3 position, bool damaged,
            List<GameObject> spawned, out string skip)
        {
            var go = WoodcuttingFeatureSmoke.SpawnTestObject(prefab.name, position, Quaternion.identity, out skip);
            if (!go) return null;

            spawned.Add(go);
            var wear = go.GetComponent<WearNTear>();
            if (!wear || !Objects.GetZNetView(go.transform, out var view))
            {
                skip = $"the spawned {prefab.name} has no WearNTear";
                return null;
            }

            view.GetZDO().Set(ZDOVars.s_health, damaged ? wear.m_health * 0.5f : wear.m_health);
            RepairObserver.Watch(wear);
            return wear;
        }

        // A piece a check is done with is made undamaged, so no later pass of the frame takes
        // it for a piece to repair before the game removes it at the end of the frame.
        private static void Retire(params WearNTear[] pieces)
        {
            foreach (var wear in pieces)
            {
                if (!wear) continue;
                if (Objects.GetZNetView(wear.transform, out var view) && view.GetZDO() != null)
                    view.GetZDO().Set(ZDOVars.s_health, wear.m_health);
                WoodcuttingFeatureSmoke.RemoveTestObject(wear.gameObject);
            }
        }

        private static void ReportRestore(FeatureRun run)
        {
            var results = SmokeSession.RestoreSettings(run.RunId);
            if (results.Count == 0) return;

            var detail = string.Join("; ", results.Select(x => x.Value));
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                run.Fail(RestoreCheck, detail);
            else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                      x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                run.Skip(RestoreCheck, "restoration skipped: " + detail);
            else
                run.Pass(RestoreCheck, detail);
        }

        // -- Observation ------------------------------------------------------------------------

        // Patches added for the section only and removed at its end. While armed, they hand the
        // piece pass the substitute repair tool, record each repair call on a test piece and
        // refuse it on any other piece, show an item pass only the test items it is armed with,
        // let only the test ward flash, and record the ward flashes, the access checks, the
        // skill raises, the station usability checks, the recipe lookups and the build station
        // lookups.
        private static class RepairObserver
        {
            private const string HarmonyId = "net.eidee.automatics.smoke.features.repair";

            private static Harmony _harmony;
            private static bool _armed;
            private static ItemDrop.ItemData _tool;

            // The only items the worn-item collection returns while armed; null leaves it alone.
            private static HashSet<ItemDrop.ItemData> _items;

            // Registered with the session before any patch goes on, so a run cut short still
            // takes the patches off; released once they are off.
            private static readonly Func<string, bool> DetachHandBack = reason => Detach(out _);

            private static readonly HashSet<WearNTear> Watched = new HashSet<WearNTear>();

            public static readonly List<(WearNTear Piece, bool Result)> PieceCalls =
                new List<(WearNTear, bool)>();

            // The one ward whose flash is let through and recorded; every other ward's flash is
            // dropped while armed, so no ward of yours flashes.
            public static PrivateArea FixtureWard;

            public static readonly List<PrivateArea> Flashes = new List<PrivateArea>();
            public static readonly List<Vector3> AccessChecks = new List<Vector3>();

            public static readonly List<(Skills.SkillType Skill, float Value)> Raises =
                new List<(Skills.SkillType, float)>();

            public static readonly List<CraftingStation> UsableChecks = new List<CraftingStation>();
            public static readonly List<ItemDrop.ItemData> RecipeLookups = new List<ItemDrop.ItemData>();
            public static readonly List<string> StationLookups = new List<string>();

            public static void Watch(WearNTear piece)
            {
                Watched.Add(piece);
            }

            public static void Arm(ItemDrop.ItemData tool, ItemDrop.ItemData[] items = null)
            {
                PieceCalls.Clear();
                Flashes.Clear();
                AccessChecks.Clear();
                Raises.Clear();
                UsableChecks.Clear();
                RecipeLookups.Clear();
                StationLookups.Clear();
                _tool = tool;
                _items = items != null ? new HashSet<ItemDrop.ItemData>(items) : null;
                _armed = true;
            }

            public static void Disarm()
            {
                _armed = false;
                _tool = null;
                _items = null;
            }

            public static bool Attach(out string problem)
            {
                problem = null;
                var tool = AccessTools.Method(typeof(RepairPieces), "TryGetRepairTool");
                var repair = AccessTools.Method(typeof(WearNTear), nameof(WearNTear.Repair));
                var flash = AccessTools.Method(typeof(PrivateArea), "FlashShield");
                var raise = AccessTools.Method(typeof(Player), nameof(Player.RaiseSkill),
                    new[] { typeof(Skills.SkillType), typeof(float) });
                var usable = AccessTools.Method(typeof(CraftingStation), nameof(CraftingStation.CheckUsable));
                var recipe = AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.GetRecipe),
                    new[] { typeof(ItemDrop.ItemData) });
                var lookup = AccessTools.Method(typeof(CraftingStation),
                    nameof(CraftingStation.HaveBuildStationInRange));
                var worn = AccessTools.Method(typeof(Inventory), nameof(Inventory.GetWornItems));
                var access = AccessTools.Method(typeof(PrivateArea), nameof(PrivateArea.CheckAccess));
                if (tool == null || repair == null || flash == null || raise == null || usable == null ||
                    recipe == null || lookup == null || worn == null || access == null)
                {
                    problem = "a method the observer patches was not found";
                    return false;
                }

                if (_harmony != null && !Detach(out var stale))
                {
                    problem = $"an earlier observer could not be removed ({stale})";
                    return false;
                }

                SmokeSession.TrackHandBack(DetachHandBack);
                Watched.Clear();
                Disarm();
                _harmony = new Harmony(HarmonyId);
                try
                {
                    _harmony.Patch(tool, new HarmonyMethod(typeof(RepairObserver), nameof(ToolPrefix)));
                    _harmony.Patch(repair, new HarmonyMethod(typeof(RepairObserver), nameof(RepairPrefix)),
                        new HarmonyMethod(typeof(RepairObserver), nameof(RepairPostfix)));
                    _harmony.Patch(flash, new HarmonyMethod(typeof(RepairObserver), nameof(FlashPrefix)));
                    _harmony.Patch(raise, new HarmonyMethod(typeof(RepairObserver), nameof(RaisePrefix)));
                    _harmony.Patch(usable, new HarmonyMethod(typeof(RepairObserver), nameof(UsablePrefix)));
                    _harmony.Patch(recipe, new HarmonyMethod(typeof(RepairObserver), nameof(RecipePrefix)));
                    _harmony.Patch(lookup, new HarmonyMethod(typeof(RepairObserver), nameof(LookupPrefix)));
                    _harmony.Patch(worn, postfix: new HarmonyMethod(typeof(RepairObserver), nameof(WornPostfix)));
                    _harmony.Patch(access, new HarmonyMethod(typeof(RepairObserver), nameof(AccessPrefix)));
                }
                catch (Exception e)
                {
                    problem = $"the observer could not be added ({e.GetType().Name}: {e.Message})";
                    Detach(out _);
                    return false;
                }

                return true;
            }

            public static bool Detach(out string failure)
            {
                failure = null;
                Disarm();
                Watched.Clear();
                FixtureWard = null;
                if (_harmony != null)
                {
                    try
                    {
                        Unpatch(_harmony);
                    }
                    catch (Exception e)
                    {
                        failure = $"{e.GetType().Name}: {e.Message}";
                        return false;
                    }

                    _harmony = null;
                }

                SmokeSession.ReleaseHandBack(DetachHandBack);
                return true;
            }

            // Apart from Detach, so the call is only compiled where a patch was made.
            private static void Unpatch(Harmony harmony)
            {
                harmony.UnpatchSelf();
            }

            // The substitute tool stands for the hammer in your hand with the repair piece
            // selected, which the pass looks up first.
            private static bool ToolPrefix(out ItemDrop.ItemData tool, ref bool __result)
            {
                tool = null;
                if (!_armed || _tool == null) return true;
                tool = _tool;
                __result = true;
                return false;
            }

            private static bool RepairPrefix(WearNTear __instance, ref bool __result)
            {
                if (!_armed || Watched.Contains(__instance)) return true;
                __result = false;
                return false;
            }

            private static void RepairPostfix(WearNTear __instance, bool __result)
            {
                if (_armed && Watched.Contains(__instance)) PieceCalls.Add((__instance, __result));
            }

            // Only the test ward flashes while armed; a ward of yours never does.
            private static bool FlashPrefix(PrivateArea __instance)
            {
                if (!_armed) return true;
                if (__instance != FixtureWard) return false;
                Flashes.Add(__instance);
                return true;
            }

            private static void AccessPrefix(Vector3 point)
            {
                if (_armed) AccessChecks.Add(point);
            }

            // An item pass sees only the items it was armed with, so no item of yours is
            // repaired or charged.
            private static void WornPostfix(List<ItemDrop.ItemData> worn)
            {
                if (_armed && _items != null) worn.RemoveAll(x => !_items.Contains(x));
            }

            private static void RaisePrefix(Skills.SkillType skill, float value)
            {
                if (_armed) Raises.Add((skill, value));
            }

            private static void UsablePrefix(CraftingStation __instance)
            {
                if (_armed) UsableChecks.Add(__instance);
            }

            private static void RecipePrefix(ItemDrop.ItemData item)
            {
                if (_armed) RecipeLookups.Add(item);
            }

            private static void LookupPrefix(string name)
            {
                if (_armed) StationLookups.Add(name);
            }
        }
    }
}
