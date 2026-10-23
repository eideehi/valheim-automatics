using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Automatics.Valheim;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;
using SupplyAction = Automatics.AutomaticSupply.SupplyAction;
using SupplyConfig = Automatics.AutomaticSupply.Config;
using SupplyContainers = Automatics.AutomaticSupply.SupplyContainers;
using SupplyModule = Automatics.AutomaticSupply.Module;
using SupplyPatches = Automatics.AutomaticSupply.Patches;
using SupplyScope = Automatics.AutomaticSupply.SupplyScope;
using SupplyTransfer = Automatics.AutomaticSupply.SupplyTransfer;

namespace Automatics.SmokeTest
{
    // Automatic Supply against a test chest it spawns next to the player, through the real game
    // code: the requirement check and the build panel, the craft (InventoryGui.DoCrafting with a
    // spawned workbench as the current station), a placement (Player.TryPlacePiece inside the
    // module's own placement bracket) and the station intakes, plus the take and return primitives.
    //
    // Only items the player carries none of are used, so every one of them in the inventory or in
    // the chest is the check's own; the log names none of them. The module is turned on for the
    // section when it is off, Supply Range is set to 4 m so only the test chest is in reach, and
    // the player's no-cost toggle is lifted; all of it is put back afterwards. When a container
    // stands within that range, the player is moved to clear ground first and returned at the
    // end of the section. The early materials and the station inputs the player carries, and
    // more items until four slots are free, are put aside for the section and put back
    // afterwards; a player who knows no recipe for the made-up recipes to show learns one (the
    // character keeps it). The chest is removed only while it holds nothing but test items;
    // otherwise it is kept and reported.
    internal static class SupplyFeatureSmoke
    {
        private const string Label = "supply feature smoke";
        private const int Range = 4;
        private const int FreeSlots = 4;

        // Materials from before the Mistlands update. Every test item is one of these.
        private static readonly HashSet<string> EarlyMaterials = new HashSet<string>(StringComparer.Ordinal)
        {
            "Wood", "Stone", "Resin", "LeatherScraps", "DeerHide", "Flint", "FineWood", "RoundLog", "Coal",
            "Copper", "Tin", "Bronze", "Iron", "BoneFragments", "Feathers", "GreydwarfEye", "SurtlingCore",
            "Chain", "Obsidian", "Silver", "Crystal", "WolfPelt", "LoxPelt", "BlackMetal", "Tar", "Guck",
            "Chitin", "Needle", "ElderBark", "TrollHide", "Ooze", "Entrails", "Bloodbag", "WitheredBone",
            "CopperOre", "TinOre", "IronScrap", "SilverOre", "Thistle", "Dandelion", "Raspberry",
            "Blueberries", "Mushroom", "Honey", "Flax", "LinenThread", "Barley", "BarleyFlour", "Carrot",
            "Turnip", "Onion", "CarrotSeeds", "TurnipSeeds", "OnionSeeds", "BeechSeeds", "PineCone",
            "FirCone", "BirchSeeds", "AncientSeed", "RawMeat", "DeerMeat", "NeckTail", "FishRaw", "WolfMeat",
            "LoxMeat", "SerpentMeat", "Acorn", "FreezeGland", "HardAntler", "TrophyDeer"
        };

        private static readonly FieldInfo CurrentStation = AccessTools.Field(typeof(Player), "m_currentStation");
        private static readonly FieldInfo PlacementStatus = AccessTools.Field(typeof(Player), "m_placementStatus");
        private static readonly FieldInfo PlacementGhost = AccessTools.Field(typeof(Player), "m_placementGhost");
        private static readonly FieldInfo NoPlacementCost = AccessTools.Field(typeof(Player), "m_noPlacementCost");
        private static readonly MethodInfo AddKnownRecipe = AccessTools.Method(typeof(Player), "AddKnownRecipe");
        private static readonly FieldInfo CraftRecipe = AccessTools.Field(typeof(InventoryGui), "m_craftRecipe");
        private static readonly FieldInfo CraftUpgradeItem = AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem");
        private static readonly FieldInfo MultiCrafting = AccessTools.Field(typeof(InventoryGui), "m_multiCrafting");
        private static readonly MethodInfo DoCrafting = AccessTools.Method(typeof(InventoryGui), "DoCrafting");
        private static readonly MethodInfo UpdateRecipe = AccessTools.Method(typeof(InventoryGui), "UpdateRecipe");
        private static readonly MethodInfo UpdateRecipeList = AccessTools.Method(typeof(InventoryGui), "UpdateRecipeList");
        private static readonly FieldInfo SelectedRecipe = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        private static readonly FieldInfo SelectedVariant = AccessTools.Field(typeof(InventoryGui), "m_selectedVariant");
        private static readonly MethodInfo SetRecipe = AccessTools.Method(typeof(InventoryGui), "SetRecipe");
        private static readonly FieldInfo AvailableRecipes = AccessTools.Field(typeof(InventoryGui), "m_availableRecipes");
        private static readonly FieldInfo InventoryAnimator = AccessTools.Field(typeof(InventoryGui), "m_animator");
        private static readonly MethodInfo AnimatorGetBool = InventoryAnimator == null
            ? null
            : AccessTools.Method(InventoryAnimator.FieldType, "GetBool", new[] { typeof(string) });
        private static readonly PropertyInfo PairRecipe =
            SelectedRecipe == null ? null : AccessTools.Property(SelectedRecipe.FieldType, "Recipe");
        private static readonly PropertyInfo PairCanCraft =
            SelectedRecipe == null ? null : AccessTools.Property(SelectedRecipe.FieldType, "CanCraft");
        private static readonly MethodInfo SetupPieceInfo = AccessTools.Method(typeof(Hud), "SetupPieceInfo");
        private static readonly MethodInfo SmelterOnAddOre = AccessTools.Method(typeof(Smelter), "OnAddOre");
        private static readonly MethodInfo SmelterOnAddFuel = AccessTools.Method(typeof(Smelter), "OnAddFuel");
        private static readonly MethodInfo SmelterQueueSize = AccessTools.Method(typeof(Smelter), "GetQueueSize");
        private static readonly MethodInfo CookingOnInteract = AccessTools.Method(typeof(CookingStation), "OnInteract");
        private static readonly MethodInfo CookingFireLit = AccessTools.Method(typeof(CookingStation), "IsFireLit");
        private static readonly MethodInfo CookingFreeSlot = AccessTools.Method(typeof(CookingStation), "GetFreeSlot");
        private static readonly MethodInfo PlacementPrefix = AccessTools.Method(typeof(SupplyPatches), "Player_UpdatePlacement_Prefix");
        private static readonly MethodInfo PlacementFinalizer = AccessTools.Method(typeof(SupplyPatches), "Player_UpdatePlacement_Finalizer");

        // What the recording postfix saw: (name, quality, result) of every CountItems call on the
        // local player's inventory for the watched name, after Automatic Supply's own postfix.
        private static string _watched;
        private static readonly List<KeyValuePair<int, int>> Recorded = new List<KeyValuePair<int, int>>();

        // Recipe list rebuilds the observer saw (InventoryGui.UpdateRecipeList, which only the
        // crafting panel's rebuild calls), and how many of them ran while Automatic Supply hid
        // the containers from a craft's own requirement check.
        private static bool _rebuildObserver;
        private static int _rebuilds;
        private static int _hiddenRebuilds;

        public static IEnumerator Run(FeatureRun run)
        {
            var steps = Checks(run);
            while (steps.MoveNext()) yield return steps.Current;
            if (!run.IsCurrent) yield break;

            // Back to where the section started when it moved the player; otherwise the
            // command's hand-back does it at its end.
            var back = new SmokeSetup.SetupResult();
            steps = SmokeSetup.ReturnToStart(run.RunId, back);
            while (steps.MoveNext()) yield return steps.Current;
            if (!back.Ok)
                FeaturesSmokeCommand.Warn(
                    $"[Automatics Smoke] {Label}: the player was not moved back yet ({back.Reason}); the command's hand-back tries again at its end");
        }

        private static IEnumerator Checks(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !ZNetScene.instance || !ZoneSystem.instance || !ObjectDB.instance ||
                !InventoryGui.instance || !Hud.instance || !ZNet.instance || !ZNet.instance.IsServer() ||
                ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("supply_section", "requires a loaded single-player world");
                yield break;
            }
            if (player.IsDead() || InventoryGui.IsVisible() || (PlacementGhost != null && PlacementGhost.GetValue(player) as GameObject))
            {
                run.Skip("supply_section", "close the inventory and leave build mode, then run it again");
                yield break;
            }
            if (CurrentStation == null || PlacementStatus == null || NoPlacementCost == null || CraftRecipe == null ||
                CraftUpgradeItem == null || MultiCrafting == null || DoCrafting == null || SetupPieceInfo == null ||
                PlacementPrefix == null || PlacementFinalizer == null)
            {
                run.Skip("supply_section", "a game or module member the section drives could not be resolved");
                yield break;
            }
            if (player.GetCurrentCraftingStation())
            {
                run.Skip("supply_section", "step away from the crafting station, then run it again");
                yield break;
            }

            var state = new SectionState(player);
            Func<string, bool> handBack = state.HandBack;
            SmokeSession.TrackHandBack(handBack);
            var harmony = new Harmony("net.eidee.valheim.automatics.smoke.supply");
            var stashes = new List<SmokeSetup.InventoryStash>();
            try
            {
                // Settings first, then wait for the module to be applied: nothing is spawned yet.
                if (!SupplyModule.Running)
                {
                    if (!Write(run, SupplyConfig.ModuleEntry, AutomaticsModule.Enabled))
                    {
                        run.Skip("supply_section", "could not turn Automatic Supply on for the section");
                        yield break;
                    }
                    state.BeginWait();
                    for (var frame = 0; frame < 600 && run.IsCurrent && !SupplyModule.Running; frame++)
                        yield return null;
                    state.Settled(out _);
                    if (!SupplyModule.Running)
                    {
                        run.Skip("supply_section", "Automatic Supply did not start within 600 frames");
                        yield break;
                    }
                    run.Pass("supply_module_enabled", "turned on for the section and put back afterwards");
                }
                var rangeEntry = AccessTools.Field(typeof(SupplyConfig), "_supplyRange")?.GetValue(null) as ConfigEntry<int>;
                if (rangeEntry == null || !Write(run, rangeEntry, Range))
                {
                    run.Skip("supply_section", "could not set Supply Range for the section");
                    yield break;
                }
                if (player.NoCostCheat())
                {
                    NoPlacementCost.SetValue(player, false);
                    state.NoCostLifted = true;
                    run.Pass("supply_no_cost_lifted", "the player's no-cost toggle is off for this section and restored afterwards");
                }

                // Clear ground before anything is spawned: no container within Supply Range.
                var site = OtherContainers(player.transform.position);
                if (site != null)
                {
                    var moved = new SmokeSetup.SetupResult();
                    var steps = SmokeSetup.Relocate(run.RunId, OtherContainers, moved);
                    while (steps.MoveNext()) yield return steps.Current;
                    if (!run.IsCurrent) yield break;
                    if (!moved.Ok)
                    {
                        run.Skip("supply_section", $"{site}; {moved.Reason}; run it away from other containers");
                        yield break;
                    }
                    run.Pass("supply_relocated", $"{site}, so the player {moved.Reason}; returned at the end of the section");
                }

                // Registered after the section's hand-back, so a run cut short takes the test
                // items out before these go back.
                stashes.Add(SmokeSetup.Stash(player, FreeSlots, 0f, PutAside()));
                if (state.Inventory.GetEmptySlots() < FreeSlots)
                {
                    run.Skip("supply_section",
                        $"needs {FreeSlots} free inventory slots; putting items aside left {state.Inventory.GetEmptySlots()} ({stashes[0].Report})");
                    yield break;
                }
                if (stashes[0].Taken > 0) run.Pass("supply_items_put_aside", stashes[0].Report + "; put back at the end of the section");
                if (!KnownResult(player) && LearnRecipe(player))
                    run.Pass("supply_recipe_learned", "you knew no recipe for the made-up recipes to show, so one was learned (the character keeps it)");

                CheckContainerPanel(run);

                if (!state.SpawnChest(out var problem))
                {
                    run.Skip("supply_section", problem);
                    yield break;
                }
                var eligible = SupplyContainers.Eligible(player);
                if (eligible.Count != 1 || eligible[0] != state.Chest)
                {
                    run.Skip("supply_section",
                        $"{eligible.Count(x => x != state.Chest)} other usable container(s) within {Range} m after the move to clear ground; run it away from other containers");
                    yield break;
                }
                run.Pass("supply_containers_eligible", "only the test chest is within Supply Range");

                harmony.Patch(AccessTools.Method(typeof(Inventory), nameof(Inventory.CountItems),
                        new[] { typeof(string), typeof(int), typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(SupplyFeatureSmoke), nameof(RecordCount)) { priority = Priority.Last });
                if (UpdateRecipeList != null)
                {
                    harmony.Patch(UpdateRecipeList, prefix: new HarmonyMethod(typeof(SupplyFeatureSmoke), nameof(RecordRebuild)));
                    _rebuildObserver = true;
                }
                InstallPlacementHooks(harmony);

                var recipe = state.SelectRecipe(out var recipeReport);
                var piece = state.SelectPiece(out var pieceReport);
                // The craft checks hold one more stack than the recipe's materials.
                if (recipe != null && state.Inventory.GetEmptySlots() < state.Needs(recipe).Count + 1)
                    stashes.Add(SmokeSetup.Stash(player, state.Needs(recipe).Count + 1));
                if (recipe == null) run.Skip("supply_crafting", recipeReport);
                if (piece == null) run.Skip("supply_placement", pieceReport);

                var workbench = recipe != null && state.SpawnWorkbench(recipe, out problem);
                if (recipe != null && !workbench) run.Skip("supply_crafting", problem);
                // A crafting station joins the build-range list in its Start, on the next frame.
                state.BeginWait();
                yield return null;
                if (!run.IsCurrent) yield break;
                if (!state.Settled(out problem))
                {
                    run.Skip("supply_section", problem);
                    yield break;
                }

                if (workbench)
                {
                    // The player drops a station it is not using at its next update, so the station
                    // is set here and every crafting check runs within this frame.
                    if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost))
                        run.Skip("supply_crafting", "the world has no crafting cost (not changed by the smoke)");
                    else if (!state.SetStation())
                        run.Skip("supply_crafting", "could not set the test station as the current station");
                    else
                    {
                        CheckScopedCount(run, state, recipe);
                        CheckPrimitives(run, state, recipe);
                        CheckCraft(run, state, recipe, false);
                        CheckCraft(run, state, recipe, true);
                        CheckCraftPanelRebuild(run, state, recipe);
                        state.ClearStation();
                        CheckChooseOneStockRefresh(run, state, state.Needs(recipe).Keys.First());
                    }
                }

                if (piece != null)
                {
                    if (ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
                        run.Skip("supply_placement", "the world has no build cost (not changed by the smoke)");
                    else
                    {
                        if (piece.m_craftingStation &&
                            !CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, player.transform.position))
                            run.Skip("supply_panel_matches_check", "the piece's crafting station is not in range");
                        else CheckPanel(run, state, piece);
                        CheckPlacement(run, state, piece, true);
                        CheckPlacement(run, state, piece, false);
                    }
                }

                CheckFireplace(run, state);
                CheckSmelter(run, state);

                var cooking = state.SetUpCookingStation(out problem);
                if (cooking != null)
                {
                    // The test fire lights its effect area over the next frames.
                    state.BeginWait();
                    for (var frame = 0; frame < 240 && run.IsCurrent && !(bool)CookingFireLit.Invoke(cooking, null); frame++)
                        yield return null;
                    if (!run.IsCurrent) yield break;
                    if (!state.Settled(out problem))
                    {
                        run.Skip("supply_section", problem);
                        yield break;
                    }
                    CheckCooking(run, state, cooking);
                }
                else run.Skip("supply_cooking_intake", problem);

                CheckBlockedChest(run, state, false);
                CheckBlockedChest(run, state, true);
                CheckOtherGamesChest(run, state);
                CheckTakeWithoutStock(run, state);
            }
            finally
            {
                _watched = null;
                _rebuildObserver = false;
                _interceptPlacement = false;
                _placementHooks = false;
                try
                {
                    harmony.UnpatchSelf();
                }
                finally
                {
                    try
                    {
                        if (handBack(Label)) SmokeSession.ReleaseHandBack(handBack);
                        else run.Fail("supply_player_restored", "a test object or item could not be handed back; the hand-back retries");
                        // After the test items are out, so these go back into their slots.
                        foreach (var stash in stashes) stash.Restore();
                        if (state.KeptChest) run.Skip("supply_chest_removed", "the test chest held items the check cannot prove it put there and was kept");
                        if (state.LeftItems) run.Skip("supply_test_items_removed", "some test items could not be accounted for and were left where they were");
                    }
                    finally
                    {
                        var results = SmokeSession.RestoreSettings(run.RunId);
                        if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                            run.Fail("supply_settings_restored", string.Join("; ", results.Select(x => x.Value)));
                        else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser || x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                            run.Skip("supply_settings_restored", "a setting was edited by the user and kept");
                        else run.Pass("supply_settings_restored");
                    }
                }
            }
        }

        // The container panel a chest leaves active is hidden in the same frame the inventory is
        // opened on its own (Show with no container), instead of drawing the last chest for a
        // frame or more. Closing (Hide) is left to vanilla, so the panel can fade out with the
        // inventory: after Hide alone the panel is as vanilla left it.
        private static void CheckContainerPanel(FeatureRun run)
        {
            const string name = "supply_container_panel_hidden";
            var gui = InventoryGui.instance;
            var panel = gui ? gui.m_container : null;
            if (!panel)
            {
                run.Skip(name, "the inventory's container panel was not found");
                return;
            }

            bool afterShow;
            bool afterHide;
            try
            {
                panel.gameObject.SetActive(true);
                gui.Show(null);
                afterShow = panel.gameObject.activeSelf;
                panel.gameObject.SetActive(true);
                gui.Hide();
                afterHide = panel.gameObject.activeSelf;
            }
            finally
            {
                if (InventoryGui.IsVisible()) gui.Hide();
                if (panel) panel.gameObject.SetActive(false);
            }
            Check(run, name, !afterShow && afterHide,
                $"panel active after Show(null)={afterShow} (expected false), after Hide={afterHide} (expected true: vanilla leaves it for the closing fade)");
        }

        private static bool Write<T>(FeatureRun run, ConfigEntry<T> entry, T value)
        {
            return entry != null && SmokeSession.BeginSetting(run.RunId, entry, out var setting, out _) &&
                   SmokeSession.WriteSetting(run.RunId, setting, value, out _);
        }

        [HarmonyPriority(Priority.Last)]
        private static void RecordCount(Inventory __instance, string name, int quality, int __result)
        {
            if (_watched == null || name != _watched || !Player.m_localPlayer ||
                !ReferenceEquals(__instance, Player.m_localPlayer.GetInventory())) return;
            Recorded.Add(new KeyValuePair<int, int>(quality, __result));
        }

        private static void RecordRebuild()
        {
            _rebuilds++;
            if (SupplyScope.Suppressed) _hiddenRebuilds++;
        }

        // One crafting panel update, as InventoryGui.Update runs it every frame while the
        // inventory is open. No time passes, so the stock refresh due time does not move either.
        private static void UpdatePanel(Player player, int times)
        {
            for (var i = 0; i < times; i++) UpdateRecipe.Invoke(InventoryGui.instance, new object[] { player, 0f });
        }

        // Whether the inventory is open now, by the flag Show and Hide set: InventoryGui.IsVisible()
        // stays true for a frame or two after Hide. The panel checks open the inventory with
        // Show(null), which would close one the player has open and leave its chest in use.
        private static bool InventoryOpen(InventoryGui gui)
        {
            if (gui.IsContainerOpen()) return true;
            var animator = InventoryAnimator.GetValue(gui) as Object;
            return animator && (bool)AnimatorGetBool.Invoke(animator, new object[] { "visible" });
        }

        // What the section puts aside, by prefab name: the early materials and everything the
        // test smelter and cooking station take, so the player carries none of them and each
        // can be a test item.
        private static Predicate<ItemDrop.ItemData> PutAside()
        {
            var names = new HashSet<string>(EarlyMaterials, StringComparer.Ordinal);
            var smelter = ZNetScene.instance.GetPrefab("smelter")?.GetComponent<Smelter>();
            if (smelter) names.UnionWith(smelter.m_conversion.Where(x => x?.m_from).Select(x => x.m_from.gameObject.name));
            var cooking = ZNetScene.instance.GetPrefab("piece_cookingstation")?.GetComponent<CookingStation>();
            if (cooking) names.UnionWith(cooking.m_conversion.Where(x => x?.m_from).Select(x => x.m_from.gameObject.name));
            return item => item.m_dropPrefab && names.Contains(item.m_dropPrefab.name);
        }

        // Why a spot will not do for the section: a container within Supply Range of it, which
        // the eligibility check would count. Null when none is.
        private static string OtherContainers(Vector3 site)
        {
            var count = ContainerCache.GetAllInstance()
                .Count(x => x && Vector3.Distance(site, x.transform.position) <= Range + 1f);
            return count == 0 ? null : $"{count} container(s) within {Range + 1} m";
        }

        // Teaches the player the first recipe a made-up recipe can show, when they know none.
        private static bool LearnRecipe(Player player)
        {
            var recipe = ObjectDB.instance.m_recipes.FirstOrDefault(x =>
                x && x.m_enabled && x.m_item && x.m_item.m_itemData.m_shared.m_dlc.Length == 0);
            if (!recipe || AddKnownRecipe == null) return false;
            AddKnownRecipe.Invoke(player, new object[] { recipe });
            return KnownResult(player);
        }

        // The result for a recipe made up for a check: an item whose recipe the player already
        // knows. The crafting panel lists a recipe by its result's name, and the discovery of new
        // recipes skips a result already known, so a made-up recipe teaches the player nothing.
        private static ItemDrop KnownResult(Player player)
        {
            foreach (var recipe in ObjectDB.instance.m_recipes)
            {
                if (!recipe || !recipe.m_item || recipe.m_item.m_itemData.m_shared.m_dlc.Length > 0) continue;
                if (player.IsRecipeKnown(recipe.m_item.m_itemData.m_shared.m_name)) return recipe.m_item;
            }
            return null;
        }

        // Adds a made-up recipe to the game's recipes, after the real ones, until RemoveRecipe.
        private static Recipe AddRecipe(string name, ItemDrop result, CraftingStation station, int stationLevel,
            bool chooseOne, Piece.Requirement[] resources)
        {
            var recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = name;
            recipe.m_item = result;
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = stationLevel;
            recipe.m_requireOnlyOneIngredient = chooseOne;
            recipe.m_resources = resources;
            ObjectDB.instance.m_recipes.Add(recipe);
            return recipe;
        }

        private static void RemoveRecipe(Recipe recipe)
        {
            if (!recipe) return;
            if (ObjectDB.instance) ObjectDB.instance.m_recipes.Remove(recipe);
            Object.Destroy(recipe);
        }

        // The index of recipe's row in the crafting panel's list, or -1.
        private static int RowIndex(InventoryGui gui, Recipe recipe)
        {
            if (!(AvailableRecipes.GetValue(gui) is IList rows)) return -1;
            for (var i = 0; i < rows.Count; i++)
                if (ReferenceEquals(PairRecipe.GetValue(rows[i], null), recipe))
                    return i;
            return -1;
        }

        // Whether recipe's row shows it craftable, or null when it has no row.
        private static bool? RowCraftable(InventoryGui gui, Recipe recipe)
        {
            var index = RowIndex(gui, recipe);
            if (index < 0) return null;
            return (bool)PairCanCraft.GetValue(((IList)AvailableRecipes.GetValue(gui))[index], null);
        }

        private static bool IsSelected(InventoryGui gui, Recipe recipe)
        {
            return ReferenceEquals(PairRecipe.GetValue(SelectedRecipe.GetValue(gui), null), recipe);
        }

        // -- Counting ---------------------------------------------------------------------------

        // With every requirement only in the chest, the real check passes, and each per-quality
        // count it makes is the inventory plus the chest for that quality.
        private static void CheckScopedCount(FeatureRun run, SectionState state, Recipe recipe)
        {
            const string name = "supply_scoped_count";
            var needs = state.Needs(recipe);
            state.FillChest(needs);
            var first = needs.Keys.First();
            var shared = state.Shared(first);
            Recorded.Clear();
            _watched = shared.m_name;
            bool passed;
            try
            {
                passed = state.Player.HaveRequirements(recipe, false, 1);
            }
            finally
            {
                _watched = null;
            }
            var mismatches = new List<string>();
            for (var q = 1; q <= shared.m_maxQuality; q++)
            {
                var expected = Count(state.Inventory, shared.m_name, q) + Count(state.ChestInventory, shared.m_name, q);
                foreach (var seen in Recorded.Where(x => x.Key == q && x.Value != expected))
                    mismatches.Add($"quality {q}: counted {seen.Value}, expected {expected}");
            }
            var outside = state.Inventory.CountItems(shared.m_name, 1);
            Check(run, name, passed && Recorded.Count > 0 && mismatches.Count == 0 && outside == Count(state.Inventory, shared.m_name, 1),
                $"check={passed}, counts made={Recorded.Count}, mismatches=[{string.Join("; ", mismatches.ToArray())}], count outside the check={outside}");
        }

        // The build panel and the build check count the same stock: the inventory plus the chest.
        private static void CheckPanel(FeatureRun run, SectionState state, Piece piece)
        {
            const string name = "supply_panel_matches_check";
            var needs = state.Needs(piece);
            state.FillChest(needs);
            var shared = state.Shared(needs.Keys.First());
            var expected = Count(state.Inventory, shared.m_name, -1) + Count(state.ChestInventory, shared.m_name, -1);

            Recorded.Clear();
            _watched = shared.m_name;
            try
            {
                SetupPieceInfo.Invoke(Hud.instance, new object[] { piece });
            }
            finally
            {
                _watched = null;
            }
            var panel = Recorded.Where(x => x.Key < 0).Select(x => x.Value).ToList();

            Recorded.Clear();
            _watched = shared.m_name;
            bool passed;
            try
            {
                passed = state.Player.HaveRequirements(piece, Player.RequirementMode.CanBuild);
            }
            finally
            {
                _watched = null;
            }
            var check = Recorded.Where(x => x.Key < 0).Select(x => x.Value).ToList();
            try
            {
                SetupPieceInfo.Invoke(Hud.instance, new object[] { null });
            }
            catch (Exception)
            {
                // The panel is redrawn the next time the build menu opens.
            }

            Check(run, name, passed && panel.Count > 0 && check.Count > 0 && panel.All(x => x == expected) && check.All(x => x == expected),
                $"check={passed}, panel counts=[{string.Join(",", panel.Select(x => x.ToString()).ToArray())}], check counts=[{string.Join(",", check.Select(x => x.ToString()).ToArray())}], inventory+chest={expected}");
        }

        // -- The take and return primitives ------------------------------------------------------

        private static void CheckPrimitives(FeatureRun run, SectionState state, Recipe recipe)
        {
            var prefab = state.Needs(recipe).Keys.First();
            var shared = state.Shared(prefab);
            if (shared.m_maxStackSize < 4)
            {
                run.Skip("supply_partial_insertion", "the test material does not stack to 4");
                run.Skip("supply_ledger_refund", "the test material does not stack to 4");
                run.Skip("supply_insertion_shortfall_returned", "the test material does not stack to 4");
                return;
            }

            try
            {
                // Room for exactly two: a stack two short of full, and no empty slot.
                state.ClearTestItems();
                state.FillChest(new Dictionary<GameObject, int> { { prefab, 6 } });
                var stack = state.GiveToInventory(prefab, shared.m_maxStackSize - 2);
                if (stack == null || !state.FillInventory(0))
                {
                    run.Skip("supply_partial_insertion", "could not leave room for exactly two items");
                    return;
                }

                var chestBefore = Count(state.ChestInventory, shared.m_name, -1);
                var inventoryBefore = Count(state.Inventory, shared.m_name, -1);
                var action = SupplyTransfer.Begin(state.Player);
                var arrived = SupplyTransfer.Pull(action, shared.m_name, -1, 5);
                var chestAfter = Count(state.ChestInventory, shared.m_name, -1);
                var inventoryAfter = Count(state.Inventory, shared.m_name, -1);
                var full = action != null && action.InventoryFull;
                SupplyTransfer.End(action, true);
                Check(run, "supply_partial_insertion",
                    action != null && arrived == 2 && full && chestBefore - chestAfter == 2 && inventoryAfter - inventoryBefore == 2 &&
                    Count(state.ChestInventory, shared.m_name, -1) == chestBefore && Count(state.Inventory, shared.m_name, -1) == inventoryBefore,
                    $"asked 5, arrived {arrived}, chest {chestBefore}->{chestAfter}, inventory {inventoryBefore}->{inventoryAfter}, full={full}; after the refund chest={Count(state.ChestInventory, shared.m_name, -1)}, inventory={Count(state.Inventory, shared.m_name, -1)}");

                // Take two, spend one the way vanilla does, end normally: one goes back.
                action = SupplyTransfer.Begin(state.Player);
                arrived = SupplyTransfer.Pull(action, shared.m_name, -1, 2);
                state.Inventory.RemoveItem(shared.m_name, 1);
                var pulled = action?.Pulled(shared.m_name) ?? -1;
                var debited = action?.DebitedUnits(shared.m_name) ?? -1;
                var unconsumed = action?.Unconsumed(shared.m_name) ?? -1;
                SupplyTransfer.End(action, true);
                Check(run, "supply_ledger_refund",
                    arrived == 2 && pulled == 2 && debited == 1 && unconsumed == 1 &&
                    Count(state.ChestInventory, shared.m_name, -1) == chestBefore - 1 &&
                    Count(state.Inventory, shared.m_name, -1) == inventoryBefore,
                    $"pulled={pulled}, debited={debited}, unconsumed={unconsumed}; chest {chestBefore}->{Count(state.ChestInventory, shared.m_name, -1)}, inventory {inventoryBefore}->{Count(state.Inventory, shared.m_name, -1)}");

                // The insertion takes less than the preflight allowed: the last free slot is filled
                // between the removal from the chest and the insertion, from the chest's own change
                // callback. What the inventory refused goes straight back to the chest.
                state.ClearTestItems();
                state.FillChest(new Dictionary<GameObject, int> { { prefab, 3 } });
                if (!state.FillInventory(1))
                {
                    run.Skip("supply_insertion_shortfall_returned", "could not leave exactly one free slot");
                    return;
                }
                chestBefore = Count(state.ChestInventory, shared.m_name, -1);
                var chestInventory = state.ChestInventory;
                var original = chestInventory.m_onChanged;
                var armed = true;
                var filled = false;
                chestInventory.m_onChanged = () =>
                {
                    original?.Invoke();
                    if (!armed) return;
                    armed = false;
                    filled = state.FillInventory(0);
                };
                SupplyAction shortAction;
                try
                {
                    shortAction = SupplyTransfer.Begin(state.Player);
                    arrived = SupplyTransfer.Pull(shortAction, shared.m_name, -1, 3);
                    full = shortAction != null && shortAction.InventoryFull;
                    SupplyTransfer.End(shortAction, true);
                }
                finally
                {
                    chestInventory.m_onChanged = original;
                }
                Check(run, "supply_insertion_shortfall_returned",
                    shortAction != null && filled && arrived == 0 && full &&
                    Count(state.ChestInventory, shared.m_name, -1) == chestBefore && Count(state.Inventory, shared.m_name, -1) == 0,
                    $"slot filled mid-move={filled}, arrived={arrived}, full={full}, chest {chestBefore}->{Count(state.ChestInventory, shared.m_name, -1)}, inventory={Count(state.Inventory, shared.m_name, -1)}");
            }
            finally
            {
                state.ClearTestItems();
            }
        }

        // -- Crafting ---------------------------------------------------------------------------

        // Complete: every requirement comes from the chest, the item is crafted, and the chest pays.
        // Incomplete: one requirement cannot fit in the inventory, so nothing is crafted, and what
        // was taken goes back to the chest.
        private static void CheckCraft(FeatureRun run, SectionState state, Recipe recipe, bool incomplete)
        {
            var name = incomplete ? "supply_craft_incomplete" : "supply_craft_complete";
            var needs = state.Needs(recipe);
            var output = recipe.m_item.m_itemData.m_shared.m_name;
            try
            {
                state.ClearTestItems();
                state.FillChest(needs);
                if (incomplete && !state.FillInventory(needs.Count > 1 ? 1 : 0))
                {
                    run.Skip(name, "could not fill the inventory for the check");
                    return;
                }
                if (!incomplete && state.Inventory.GetEmptySlots() < needs.Count + 1)
                {
                    run.Skip(name, $"needs {needs.Count + 1} free inventory slots");
                    return;
                }

                var chestBefore = needs.Keys.ToDictionary(x => x, x => Count(state.ChestInventory, state.Shared(x).m_name, -1));
                var outputBefore = Count(state.Inventory, output, -1);
                var previousRecipe = CraftRecipe.GetValue(InventoryGui.instance);
                var previousUpgrade = CraftUpgradeItem.GetValue(InventoryGui.instance);
                var previousMulti = MultiCrafting.GetValue(InventoryGui.instance);
                Exception thrown = null;
                try
                {
                    CraftRecipe.SetValue(InventoryGui.instance, recipe);
                    CraftUpgradeItem.SetValue(InventoryGui.instance, null);
                    MultiCrafting.SetValue(InventoryGui.instance, false);
                    DoCrafting.Invoke(InventoryGui.instance, new object[] { state.Player });
                }
                catch (TargetInvocationException e)
                {
                    thrown = e.InnerException ?? e;
                }
                finally
                {
                    CraftRecipe.SetValue(InventoryGui.instance, previousRecipe);
                    CraftUpgradeItem.SetValue(InventoryGui.instance, previousUpgrade);
                    MultiCrafting.SetValue(InventoryGui.instance, previousMulti);
                }

                var crafted = Count(state.Inventory, output, -1) - outputBefore;
                if (crafted > 0) state.CreatedByTest(output, crafted);
                var chest = needs.Keys.Select(x => $"{Count(state.ChestInventory, state.Shared(x).m_name, -1)}/{chestBefore[x]}").ToArray();
                var left = needs.Keys.Sum(x => Count(state.Inventory, state.Shared(x).m_name, -1));
                if (thrown != null)
                {
                    run.Fail(name, $"the craft threw {thrown.GetType().Name}: {thrown.Message}");
                    return;
                }

                if (incomplete)
                    Check(run, name,
                        crafted == 0 && left == 0 && needs.Keys.All(x => Count(state.ChestInventory, state.Shared(x).m_name, -1) == chestBefore[x]),
                        $"crafted={crafted}, chest (now/before)=[{string.Join(", ", chest)}], materials left in the inventory={left}");
                else
                    Check(run, name,
                        crafted > 0 && left == 0 && needs.All(x => Count(state.ChestInventory, state.Shared(x.Key).m_name, -1) == chestBefore[x.Key] - x.Value),
                        $"crafted={crafted}, chest (now/before)=[{string.Join(", ", chest)}], materials left in the inventory={left}");
            }
            finally
            {
                state.ClearTestItems();
            }
        }

        // With the inventory open at the workbench and twice the materials in the chest, the craft
        // rebuilds the recipe list once, with the chest counted: a recipe made up for the check
        // from the same materials shows craftable from what the chest still holds, the inventory
        // holding none of them. The panel updates after it rebuild nothing more: neither the next
        // ones nor those once the stock refresh is due again, because that one rebuild already
        // showed the stock the craft left. Opened and closed again within the frame, so the
        // station set for the section holds.
        private static void CheckCraftPanelRebuild(FeatureRun run, SectionState state, Recipe recipe)
        {
            const string name = "supply_craft_rebuilds_list_once";
            if (UpdateRecipe == null || !_rebuildObserver || AvailableRecipes == null || PairRecipe == null ||
                PairCanCraft == null || AnimatorGetBool == null || SelectedVariant == null)
            {
                run.Skip(name, "the crafting panel's list, its update or its rebuild could not be observed");
                return;
            }
            var gui = InventoryGui.instance;
            if (InventoryOpen(gui))
            {
                run.Skip(name, "close the inventory, then run it again");
                return;
            }
            var result = KnownResult(state.Player);
            if (!result)
            {
                run.Skip(name, "you know no recipe whose result a made-up recipe can show, and none could be learned");
                return;
            }

            var needs = state.Needs(recipe);
            var twice = needs.ToDictionary(x => x.Key, x => x.Value * 2);
            var output = recipe.m_item.m_itemData.m_shared.m_name;
            var previousSelection = SelectedRecipe.GetValue(gui);
            var previousVariant = SelectedVariant.GetValue(gui);
            Recipe probe = null;
            var opened = false;
            var craftTab = false;
            try
            {
                probe = AddRecipe("Automatics smoke probe recipe", result, recipe.m_craftingStation,
                    recipe.m_minStationLevel, false, recipe.m_resources);
                state.ClearTestItems();
                state.FillChest(twice);
                if (twice.Any(x => Count(state.ChestInventory, state.Shared(x.Key).m_name, -1) != x.Value))
                {
                    run.Skip(name, "the chest could not hold twice the recipe's materials");
                    return;
                }
                if (state.Inventory.GetEmptySlots() < needs.Count + 1)
                {
                    run.Skip(name, $"needs {needs.Count + 1} free inventory slots");
                    return;
                }
                gui.Show(null);
                opened = true;
                if (!InventoryGui.IsVisible())
                {
                    run.Skip(name, "the inventory did not open");
                    return;
                }
                if (!gui.InCraftTab())
                {
                    gui.OnTabCraftPressed();
                    craftTab = true;
                }
                if (RowCraftable(gui, probe) == null)
                {
                    run.Skip(name, "the made-up recipe was not listed at the workbench");
                    return;
                }
                // Whatever opening the panel left to do is done before counting.
                UpdatePanel(state.Player, 2);

                var outputBefore = Count(state.Inventory, output, -1);
                var previousRecipe = CraftRecipe.GetValue(gui);
                var previousUpgrade = CraftUpgradeItem.GetValue(gui);
                var previousMulti = MultiCrafting.GetValue(gui);
                _rebuilds = 0;
                _hiddenRebuilds = 0;
                Exception thrown = null;
                try
                {
                    CraftRecipe.SetValue(gui, recipe);
                    CraftUpgradeItem.SetValue(gui, null);
                    MultiCrafting.SetValue(gui, false);
                    DoCrafting.Invoke(gui, new object[] { state.Player });
                }
                catch (TargetInvocationException e)
                {
                    thrown = e.InnerException ?? e;
                }
                finally
                {
                    CraftRecipe.SetValue(gui, previousRecipe);
                    CraftUpgradeItem.SetValue(gui, previousUpgrade);
                    MultiCrafting.SetValue(gui, previousMulti);
                }
                var inCraft = _rebuilds;
                var hidden = _hiddenRebuilds;
                var row = RowCraftable(gui, probe);
                var held = needs.Keys.Sum(x => Count(state.Inventory, state.Shared(x).m_name, -1));
                var left = needs.Keys.Select(x => $"{Count(state.ChestInventory, state.Shared(x).m_name, -1)}/{needs[x]}").ToArray();

                UpdatePanel(state.Player, 3);
                var afterUpdates = _rebuilds;
                // As when the refresh interval has passed.
                SupplyContainers.Invalidate();
                UpdatePanel(state.Player, 3);
                var afterRefresh = _rebuilds;

                var crafted = Count(state.Inventory, output, -1) - outputBefore;
                if (crafted > 0) state.CreatedByTest(output, crafted);
                if (thrown != null)
                {
                    run.Fail(name, $"the craft threw {thrown.GetType().Name}: {thrown.Message}");
                    return;
                }
                Check(run, name,
                    crafted > 0 && inCraft == 1 && hidden == 0 && held == 0 && row == true && afterUpdates == 1 && afterRefresh == 1,
                    $"crafted={crafted}, rebuilds in the craft={inCraft} (expected 1), of them with the containers hidden={hidden} (expected 0), made-up recipe craftable after it={RowText(row)} (expected True; materials in the inventory={held}, chest/one craft=[{string.Join(", ", left)}]), after three panel updates={afterUpdates} (expected 1), after a due stock refresh and three more={afterRefresh} (expected 1)");
            }
            finally
            {
                if (craftTab) gui.OnTabUpgradePressed();
                if (opened) gui.Hide();
                SelectedRecipe.SetValue(gui, previousSelection);
                SelectedVariant.SetValue(gui, previousVariant);
                RemoveRecipe(probe);
                state.ClearTestItems();
            }
        }

        // Two recipes made up for the check from one test material are listed with the inventory
        // open and no station: one where the player chooses one ingredient, selected, and an
        // ordinary one that needs one of it. The chest gains the ingredient with no craft. While
        // the stock read in this frame holds, the panel updates rebuild nothing, the ordinary
        // recipe stays uncraftable, and every count they make of the ingredient for the selected
        // recipe is the inventory alone, as vanilla counts. Once the stock refresh is due, the
        // list is rebuilt once with the chest counted: the ordinary recipe turns craftable and
        // the selection stays on the other one. Your selection is put back afterwards.
        private static void CheckChooseOneStockRefresh(FeatureRun run, SectionState state, GameObject material)
        {
            const string countName = "supply_choose_one_recipe_vanilla_count";
            const string refreshName = "supply_recipe_list_follows_stock";
            if (UpdateRecipe == null || !_rebuildObserver || SetRecipe == null || SelectedVariant == null ||
                AvailableRecipes == null || PairRecipe == null || PairCanCraft == null || AnimatorGetBool == null)
            {
                run.Skip(countName, "the crafting panel's list, selection or update could not be driven");
                run.Skip(refreshName, "the crafting panel's list, selection or update could not be driven");
                return;
            }
            var gui = InventoryGui.instance;
            if (InventoryOpen(gui))
            {
                run.Skip(countName, "close the inventory, then run it again");
                run.Skip(refreshName, "close the inventory, then run it again");
                return;
            }
            if (state.Player.GetCurrentCraftingStation())
            {
                run.Skip(countName, "a crafting station is still the current station");
                run.Skip(refreshName, "a crafting station is still the current station");
                return;
            }
            var result = KnownResult(state.Player);
            if (!result)
            {
                run.Skip(countName, "you know no recipe whose result a made-up recipe can show, and none could be learned");
                run.Skip(refreshName, "you know no recipe whose result a made-up recipe can show, and none could be learned");
                return;
            }

            var shared = state.Shared(material);
            var drop = material.GetComponent<ItemDrop>();
            var previousSelection = SelectedRecipe.GetValue(gui);
            var previousVariant = SelectedVariant.GetValue(gui);
            Recipe single = null;
            Recipe ordinary = null;
            var opened = false;
            try
            {
                single = AddRecipe("Automatics smoke choose-one recipe", result, null, 1, true,
                    new[] { new Piece.Requirement { m_resItem = drop, m_amount = 1 } });
                ordinary = AddRecipe("Automatics smoke ordinary recipe", result, null, 1, false,
                    new[] { new Piece.Requirement { m_resItem = drop, m_amount = 1 } });

                state.ClearTestItems();
                gui.Show(null);
                opened = true;
                var index = RowIndex(gui, single);
                if (!InventoryGui.IsVisible() || index < 0 || RowCraftable(gui, ordinary) == null)
                {
                    run.Skip(countName, "the inventory did not open with both made-up recipes listed");
                    run.Skip(refreshName, "the inventory did not open with both made-up recipes listed");
                    return;
                }
                SetRecipe.Invoke(gui, new object[] { index, false });
                // Whatever opening the panel left to do is done before counting.
                UpdatePanel(state.Player, 2);
                var rowBefore = RowCraftable(gui, ordinary);
                _rebuilds = 0;
                _hiddenRebuilds = 0;

                // The stock was read in this frame, when the panel opened, so the next refresh is
                // not due yet.
                if (!state.AddToChest(material, 1))
                {
                    run.Skip(countName, "could not put the ingredient in the chest");
                    run.Skip(refreshName, "could not put the ingredient in the chest");
                    return;
                }
                Recorded.Clear();
                _watched = shared.m_name;
                try
                {
                    UpdatePanel(state.Player, 3);
                }
                finally
                {
                    _watched = null;
                }
                var early = _rebuilds;
                var rowEarly = RowCraftable(gui, ordinary);
                var selected = IsSelected(gui, single);
                var inChest = Count(state.ChestInventory, shared.m_name, -1);
                var mismatches = Recorded.Where(x => x.Value != Count(state.Inventory, shared.m_name, x.Key))
                    .Select(x => $"quality {x.Key}: counted {x.Value}, inventory {Count(state.Inventory, shared.m_name, x.Key)}")
                    .ToArray();

                // As when the refresh interval has passed.
                SupplyContainers.Invalidate();
                UpdatePanel(state.Player, 3);
                var late = _rebuilds - early;
                var rowAfter = RowCraftable(gui, ordinary);
                var stillSelected = IsSelected(gui, single);

                Check(run, countName, selected && inChest > 0 && Recorded.Count > 0 && mismatches.Length == 0,
                    $"recipe selected={selected}, in the chest={inChest}, counts made={Recorded.Count}, mismatches=[{string.Join("; ", mismatches)}]");
                Check(run, refreshName,
                    rowBefore == false && early == 0 && rowEarly == false && late == 1 && _hiddenRebuilds == 0 &&
                    rowAfter == true && stillSelected,
                    $"ordinary recipe craftable before={RowText(rowBefore)} (expected False), rebuilds before the refresh was due={early} (expected 0) and craftable then={RowText(rowEarly)} (expected False), rebuilds after it={late} (expected 1) and craftable then={RowText(rowAfter)} (expected True), rebuilds with the containers hidden={_hiddenRebuilds} (expected 0), choose-one recipe still selected={stillSelected}");
            }
            finally
            {
                _watched = null;
                if (opened) gui.Hide();
                SelectedRecipe.SetValue(gui, previousSelection);
                SelectedVariant.SetValue(gui, previousVariant);
                RemoveRecipe(single);
                RemoveRecipe(ordinary);
                state.ClearTestItems();
            }
        }

        private static string RowText(bool? craftable)
        {
            return craftable?.ToString() ?? "not listed";
        }

        // -- Placement --------------------------------------------------------------------------

        // Runs Player.TryPlacePiece inside the module's placement bracket.
        // Incomplete (no inventory room): the placement status is held at Valid, the ghost update
        // is skipped and a stand-in ghost is set, so a broken guard would reach
        // Player.PlacePiece; that call is intercepted and recorded instead of placing. The guard
        // must refuse on its own (its rejection counter moves) before anything moves.
        // Complete: the status is held at Invalid, everything is taken, vanilla refuses the spot,
        // and what was taken goes back when the bracket closes.
        private static void CheckPlacement(FeatureRun run, SectionState state, Piece piece, bool incomplete)
        {
            var name = incomplete ? "supply_place_incomplete" : "supply_place_failed_refund";
            var needs = state.Needs(piece);
            var previousStatus = PlacementStatus.GetValue(state.Player);
            var previousGhost = PlacementGhost.GetValue(state.Player);
            GameObject standIn = null;
            try
            {
                state.ClearTestItems();
                state.FillChest(needs);
                if (incomplete && !state.FillInventory(0))
                {
                    run.Skip(name, "could not fill the inventory for the check");
                    return;
                }
                if (incomplete)
                {
                    if (!_placementHooks)
                    {
                        run.Skip(name, "the placement interception hooks could not be installed");
                        return;
                    }
                    standIn = new GameObject("Automatics smoke supply stand-in ghost");
                    standIn.transform.position = state.Player.transform.position + state.Player.transform.forward * 3f;
                    PlacementGhost.SetValue(state.Player, standIn);
                }

                PlacementStatus.SetValue(state.Player, Enum.Parse(PlacementStatus.FieldType, incomplete ? "Valid" : "Invalid"));
                var chestBefore = needs.Keys.ToDictionary(x => x, x => Count(state.ChestInventory, state.Shared(x).m_name, -1));
                var rejectionsBefore = SupplyPatches.PlacementRejections;
                _placeCalls = 0;
                var args = new object[] { state.Player, false };
                PlacementPrefix.Invoke(null, args);
                bool placed;
                var heldDuring = 0;
                _interceptPlacement = true;
                try
                {
                    placed = state.Player.TryPlacePiece(piece);
                    heldDuring = needs.Keys.Sum(x => Count(state.Inventory, state.Shared(x).m_name, -1));
                }
                finally
                {
                    _interceptPlacement = false;
                    PlacementFinalizer.Invoke(null, new[] { null, args[1] });
                }

                var rejected = SupplyPatches.PlacementRejections - rejectionsBefore;
                var left = needs.Keys.Sum(x => Count(state.Inventory, state.Shared(x).m_name, -1));
                var unchanged = needs.Keys.All(x => Count(state.ChestInventory, state.Shared(x).m_name, -1) == chestBefore[x]);
                var detail = $"placed={placed}, guard rejections={rejected}, placement reached={_placeCalls}, bracket={args[1]}, held while placing={heldDuring}/{needs.Values.Sum()}, left in the inventory={left}, chest unchanged={unchanged}";
                if (incomplete)
                    Check(run, name, !placed && rejected == 1 && _placeCalls == 0 && (bool)args[1] && heldDuring == 0 && left == 0 && unchanged, detail);
                else
                    Check(run, name, !placed && rejected == 0 && (bool)args[1] && heldDuring == needs.Values.Sum() && left == 0 && unchanged, detail);
            }
            finally
            {
                _interceptPlacement = false;
                PlacementStatus.SetValue(state.Player, previousStatus);
                PlacementGhost.SetValue(state.Player, previousGhost);
                if (standIn) Object.Destroy(standIn);
                state.ClearTestItems();
            }
        }

        private static bool _placementHooks;
        private static bool _interceptPlacement;
        private static int _placeCalls;

        // Installed for the section only. While a placement check runs, vanilla's ghost update is
        // skipped (so the status set by the check holds) and PlacePiece is recorded, not run.
        private static void InstallPlacementHooks(Harmony harmony)
        {
            var ghost = AccessTools.Method(typeof(Player), "UpdatePlacementGhost");
            var place = AccessTools.Method(typeof(Player), nameof(Player.PlacePiece));
            if (ghost == null || place == null) return;
            harmony.Patch(ghost, prefix: new HarmonyMethod(typeof(SupplyFeatureSmoke), nameof(SkipGhostUpdate)));
            harmony.Patch(place, prefix: new HarmonyMethod(typeof(SupplyFeatureSmoke), nameof(InterceptPlacePiece)));
            _placementHooks = true;
        }

        private static bool SkipGhostUpdate()
        {
            return !_interceptPlacement;
        }

        private static bool InterceptPlacePiece()
        {
            if (!_interceptPlacement) return true;
            _placeCalls++;
            return false;
        }

        // -- Stations ---------------------------------------------------------------------------

        private static void CheckFireplace(FeatureRun run, SectionState state)
        {
            const string name = "supply_fireplace_intake";
            var fireplace = state.SpawnFireplace(out var problem);
            if (!fireplace)
            {
                run.Skip(name, problem);
                return;
            }

            CheckIntake(run, state, name, fireplace.m_fuelItem.gameObject,
                () => fireplace.Interact(state.Player, false, false));
            CheckInjectedException(run, state, fireplace);
        }

        // A real exception in the middle of a take: the player inventory's change callback throws
        // once, right after the fuel arrived and before the fireplace saw it. Nothing is lost or
        // duplicated, the action is closed without a refund, and no counting scope is left up.
        private static void CheckInjectedException(FeatureRun run, SectionState state, Fireplace fireplace)
        {
            const string name = "supply_exception_no_refund";
            var shared = fireplace.m_fuelItem.m_itemData.m_shared;
            var inventory = state.Inventory;
            var original = inventory.m_onChanged;
            try
            {
                state.ClearTestItems();
                state.FillChest(new Dictionary<GameObject, int> { { fireplace.m_fuelItem.gameObject, 2 } });
                var chestBefore = Count(state.ChestInventory, shared.m_name, -1);
                var totalBefore = chestBefore + Count(inventory, shared.m_name, -1);
                var armed = true;
                inventory.m_onChanged = () =>
                {
                    original?.Invoke();
                    if (!armed) return;
                    armed = false;
                    throw new InvalidOperationException("Automatics smoke: injected inventory callback failure");
                };
                Exception thrown = null;
                try
                {
                    fireplace.Interact(state.Player, false, false);
                }
                catch (Exception e)
                {
                    thrown = e;
                }
                finally
                {
                    inventory.m_onChanged = original;
                }

                var chestAfter = Count(state.ChestInventory, shared.m_name, -1);
                var totalAfter = chestAfter + Count(inventory, shared.m_name, -1);
                var closed = SupplyTransfer.Current == null && !SupplyScope.Counting &&
                             !SupplyScope.Suppressed;
                Check(run, name, thrown is InvalidOperationException && totalAfter == totalBefore && chestAfter == chestBefore - 1 && closed,
                    $"threw={thrown?.GetType().Name ?? "nothing"}, chest {chestBefore}->{chestAfter}, chest+inventory {totalBefore}->{totalAfter}, action and scope closed={closed}");
            }
            finally
            {
                inventory.m_onChanged = original;
                state.ClearTestItems();
            }
        }

        private static void CheckSmelter(FeatureRun run, SectionState state)
        {
            var smelter = state.SpawnSmelter(out var ore, out var problem);
            if (!smelter)
            {
                run.Skip("supply_smelter_ore_intake", problem);
                run.Skip("supply_smelter_fuel_intake", problem);
                return;
            }

            if (!ore || SmelterOnAddOre == null) run.Skip("supply_smelter_ore_intake", ore ? "OnAddOre could not be resolved" : problem);
            else
                CheckIntake(run, state, "supply_smelter_ore_intake", ore,
                    () => (bool)SmelterOnAddOre.Invoke(smelter, new object[] { null, state.Player, null }));

            var fuel = smelter.m_fuelItem ? smelter.m_fuelItem.gameObject : null;
            if (!fuel || SmelterOnAddFuel == null || !state.Claim(fuel))
                run.Skip("supply_smelter_fuel_intake", "the smelter takes no fuel, or you carry its fuel");
            else
                CheckIntake(run, state, "supply_smelter_fuel_intake", fuel,
                    () => (bool)SmelterOnAddFuel.Invoke(smelter, new object[] { null, state.Player, null }));
        }

        private static void CheckCooking(FeatureRun run, SectionState state, CookingStation cooking)
        {
            const string name = "supply_cooking_intake";
            if (!(bool)CookingFireLit.Invoke(cooking, null))
            {
                run.Skip(name, "the test fire did not light the cooking station");
                return;
            }

            var item = state.CookingItem;
            var freeBefore = (int)CookingFreeSlot.Invoke(cooking, null);
            CheckIntake(run, state, name, item,
                () => (bool)CookingOnInteract.Invoke(cooking, new object[] { state.Player }),
                () => (int)CookingFreeSlot.Invoke(cooking, null) != freeBefore);
        }

        // The inventory holds nothing the station takes and the chest holds two of one accepted
        // item: the real intake takes one from the chest and the inventory ends as it began.
        private static void CheckIntake(FeatureRun run, SectionState state, string name, GameObject item,
            Func<bool> intake, Func<bool> stationChanged = null)
        {
            var shared = item.GetComponent<ItemDrop>().m_itemData.m_shared;
            try
            {
                state.ClearTestItems();
                state.FillChest(new Dictionary<GameObject, int> { { item, 2 } });
                var chestBefore = Count(state.ChestInventory, shared.m_name, -1);
                bool accepted;
                try
                {
                    accepted = intake();
                }
                catch (TargetInvocationException e)
                {
                    run.Fail(name, $"the intake threw {(e.InnerException ?? e).GetType().Name}: {(e.InnerException ?? e).Message}");
                    return;
                }
                var chestAfter = Count(state.ChestInventory, shared.m_name, -1);
                var held = Count(state.Inventory, shared.m_name, -1);
                var changed = stationChanged == null || stationChanged();
                Check(run, name, accepted && chestBefore - chestAfter == 1 && held == 0 && changed,
                    $"accepted={accepted}, chest {chestBefore}->{chestAfter}, left in the inventory={held}, station changed={changed}");
            }
            finally
            {
                state.ClearTestItems();
            }
        }

        // An in-use or warded chest is never touched: the intake finds nothing to take.
        private static void CheckBlockedChest(FeatureRun run, SectionState state, bool warded)
        {
            var name = warded ? "supply_warded_chest_untouched" : "supply_in_use_chest_untouched";
            var fireplace = state.Fireplace;
            if (!fireplace)
            {
                run.Skip(name, "no test fireplace to drive");
                return;
            }

            var shared = fireplace.m_fuelItem.m_itemData.m_shared;
            try
            {
                state.ClearTestItems();
                state.FillChest(new Dictionary<GameObject, int> { { fireplace.m_fuelItem.gameObject, 2 } });
                if (warded)
                {
                    if (!state.RaiseWard(out var problem))
                    {
                        run.Skip(name, problem);
                        return;
                    }
                }
                else state.Chest.SetInUse(true);

                SupplyContainers.Invalidate();
                var blocked = !ContainerAccess.CanUseContainer(state.Player, state.Chest);
                if (!blocked)
                {
                    run.Skip(name, warded ? "the test ward did not deny access to the chest" : "the chest did not report itself in use");
                    return;
                }

                var accepted = fireplace.Interact(state.Player, false, false);
                Check(run, name, !accepted && Count(state.ChestInventory, shared.m_name, -1) == 2 && Count(state.Inventory, shared.m_name, -1) == 0,
                    $"accepted={accepted}, chest={Count(state.ChestInventory, shared.m_name, -1)}/2, inventory={Count(state.Inventory, shared.m_name, -1)}");
            }
            finally
            {
                if (warded) state.LowerWard();
                else if (state.Chest) state.Chest.SetInUse(false);
                SupplyContainers.Invalidate();
                state.ClearTestItems();
            }
        }

        // A chest another player's game owns offers no stock: Supply neither counts it nor takes
        // from it, and does not take the chest over.
        private static void CheckOtherGamesChest(FeatureRun run, SectionState state)
        {
            const string name = "supply_other_games_chest_untouched";
            var fireplace = state.Fireplace;
            if (!fireplace)
            {
                run.Skip(name, "no test fireplace to drive");
                return;
            }
            if (!Objects.GetZNetView(state.Chest, out var view) || !view || !view.IsValid())
            {
                run.Skip(name, "the test chest has no network view");
                return;
            }

            var shared = fireplace.m_fuelItem.m_itemData.m_shared;
            try
            {
                state.ClearTestItems();
                state.FillChest(new Dictionary<GameObject, int> { { fireplace.m_fuelItem.gameObject, 2 } });
                // As when the server has given the chest to another player's game.
                view.GetZDO().SetOwner(ZDOMan.GetSessionID() + 1);

                SupplyContainers.Invalidate();
                var eligible = SupplyContainers.Eligible(state.Player).Contains(state.Chest);
                var counted = SupplyContainers.Count(state.Player, shared.m_name, -1, false);
                var accepted = fireplace.Interact(state.Player, false, false);
                var taken = view.IsOwner();
                Check(run, name, !eligible && counted == 0 && !accepted && !taken &&
                                 Count(state.ChestInventory, shared.m_name, -1) == 2 && Count(state.Inventory, shared.m_name, -1) == 0,
                    $"eligible={eligible}, counted={counted}, accepted={accepted}, taken={taken}, chest={Count(state.ChestInventory, shared.m_name, -1)}/2, inventory={Count(state.Inventory, shared.m_name, -1)}");
            }
            finally
            {
                // Only the owner removes the chest's saved data with it.
                if (view && view.IsValid() && !view.IsOwner()) view.ClaimOwnership();
                SupplyContainers.Invalidate();
                state.ClearTestItems();
            }
        }

        // A take passes over a chest that does not hold the item without taking the chest. The
        // chest is left without an owner, which the server claims before it writes a chest, so a
        // take that went through the container gate first would show as the chest becoming this
        // game's.
        private static void CheckTakeWithoutStock(FeatureRun run, SectionState state)
        {
            const string name = "supply_take_leaves_chest_without_item_unclaimed";
            var fireplace = state.Fireplace;
            if (!fireplace)
            {
                run.Skip(name, "no test fireplace to name the item");
                return;
            }
            if (!Objects.GetZNetView(state.Chest, out var view) || !view || !view.IsValid())
            {
                run.Skip(name, "the test chest has no network view");
                return;
            }

            var shared = fireplace.m_fuelItem.m_itemData.m_shared;
            try
            {
                state.ClearTestItems();
                if (Count(state.ChestInventory, shared.m_name, -1) != 0)
                {
                    run.Skip(name, "the test chest still holds the item");
                    return;
                }
                // As when the chest has no owner yet.
                view.GetZDO().SetOwner(0L);

                SupplyContainers.Invalidate();
                if (!SupplyContainers.Eligible(state.Player).Contains(state.Chest))
                {
                    run.Skip(name, "the chest without an owner is not one Supply may take from");
                    return;
                }
                var action = SupplyTransfer.Begin(state.Player);
                var arrived = SupplyTransfer.Pull(action, shared.m_name, -1, 1);
                SupplyTransfer.End(action, true);
                var owned = view.HasOwner();
                Check(run, name, action != null && arrived == 0 && !owned,
                    $"opened={action != null}, arrived={arrived}, chest owned afterwards={owned}");
            }
            finally
            {
                // Only the owner removes the chest's saved data with it.
                if (view && view.IsValid() && !view.IsOwner()) view.ClaimOwnership();
                SupplyContainers.Invalidate();
                state.ClearTestItems();
            }
        }

        // -- Helpers ----------------------------------------------------------------------------

        // Every unit of the name in the inventory, any quality or world level when quality < 0.
        private static int Count(Inventory inventory, string name, int quality)
        {
            if (inventory == null) return 0;
            return inventory.GetAllItems()
                .Where(x => x.m_shared.m_name == name && (quality < 0 || x.m_quality == quality))
                .Sum(x => x.m_stack);
        }

        private static void Check(FeatureRun run, string name, bool ok, string detail)
        {
            if (ok) run.Pass(name, detail);
            else run.Fail(name, detail);
        }

        // Everything the section spawned, added or changed, and how to hand it back.
        private sealed class SectionState
        {
            private readonly List<GameObject> _spawned = new List<GameObject>();
            private readonly HashSet<string> _testNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _used = new HashSet<string>(StringComparer.Ordinal);
            private readonly Skills.Skill _crafting;
            private readonly Skills.Skill _cooking;
            private readonly float[] _skillValues;
            private GameObject _chestGo;
            private GameObject _wardGo;
            private GameObject _filler;
            private CraftingStation _station;
            private bool _done;

            // Units of each test item the section put into the world (given, put in the chest or
            // crafted) and has not taken out again: the most it may ever remove of that item.
            private readonly Dictionary<string, int> _budget = new Dictionary<string, int>(StringComparer.Ordinal);

            // Units of a test item found in the inventory or the chest when a frame boundary had
            // passed. Every check ends with its test items taken out, so these arrived from
            // elsewhere (a pickup, another mod) and are never removed.
            private readonly Dictionary<string, int> _inventoryKeep = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _chestKeep = new Dictionary<string, int>(StringComparer.Ordinal);

            public bool LeftItems { get; private set; }

            // Set before every frame boundary and cleared by Settled(): while it is set, anything
            // with a test item's name may have arrived from elsewhere, so the hand-back records it
            // as not the section's own before it removes anything.
            private bool _waiting;

            public void BeginWait()
            {
                _waiting = true;
            }

            public SectionState(Player player)
            {
                Player = player;
                Inventory = player.GetInventory();
                _crafting = Skill(Skills.SkillType.Crafting);
                _cooking = Skill(Skills.SkillType.Cooking);
                _skillValues = new[]
                {
                    _crafting?.m_level ?? 0f, _crafting?.m_accumulator ?? 0f,
                    _cooking?.m_level ?? 0f, _cooking?.m_accumulator ?? 0f
                };
            }

            public Player Player { get; }
            public Inventory Inventory { get; }
            public Container Chest { get; private set; }
            public Inventory ChestInventory => Chest ? Chest.GetInventory() : null;
            public Fireplace Fireplace { get; private set; }
            public GameObject CookingItem { get; private set; }
            public bool NoCostLifted { get; set; }
            public bool KeptChest { get; private set; }

            public ItemDrop.ItemData.SharedData Shared(GameObject prefab)
            {
                return prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
            }

            public bool Carried(GameObject prefab)
            {
                var name = Shared(prefab).m_name;
                return !_testNames.Contains(name) && Inventory.GetAllItems().Any(x => x.m_shared.m_name == name);
            }

            // A test item: an early material the player carries none of. Every unit of it in the
            // inventory or the chest from now on is the check's own.
            public bool Claim(GameObject prefab)
            {
                if (!prefab || !prefab.GetComponent<ItemDrop>() || !EarlyMaterials.Contains(prefab.name)) return false;
                if (Carried(prefab)) return false;
                _testNames.Add(Shared(prefab).m_name);
                return true;
            }

            public Dictionary<GameObject, int> Needs(Recipe recipe)
            {
                return recipe.m_resources.Where(r => r.m_resItem && !r.m_upgraderResource && r.GetAmount(1) > 0)
                    .GroupBy(r => r.m_resItem.gameObject)
                    .ToDictionary(g => g.Key, g => g.Sum(r => r.GetAmount(1)));
            }

            public Dictionary<GameObject, int> Needs(Piece piece)
            {
                return piece.m_resources.Where(r => r.m_resItem && r.m_amount > 0)
                    .GroupBy(r => r.m_resItem.gameObject)
                    .ToDictionary(g => g.Key, g => g.Sum(r => r.m_amount));
            }

            public bool SpawnChest(out string problem)
            {
                problem = null;
                var prefab = ZNetScene.instance.GetPrefab("piece_chest_wood");
                if (!prefab)
                {
                    problem = "the wooden chest prefab was not found";
                    return false;
                }
                _chestGo = Spawn(prefab, Player.transform.right * 1.5f, false);
                Chest = _chestGo ? _chestGo.GetComponent<Container>() : null;
                if (!Chest || ChestInventory == null)
                {
                    problem = "the spawned chest has no inventory";
                    return false;
                }
                if (ChestInventory.NrOfItems() != 0)
                {
                    problem = "the spawned chest was not empty";
                    return false;
                }
                SupplyContainers.Invalidate();
                return true;
            }

            public bool SpawnWorkbench(Recipe recipe, out string problem)
            {
                problem = null;
                var prefab = recipe.m_craftingStation ? recipe.m_craftingStation.gameObject : null;
                var go = prefab ? Spawn(ZNetScene.instance.GetPrefab(prefab.name) ?? prefab, Player.transform.forward * 3f, true) : null;
                var station = go ? go.GetComponentInChildren<CraftingStation>() : null;
                if (!station)
                {
                    problem = "could not spawn the recipe's crafting station";
                    return false;
                }
                _station = station;
                return true;
            }

            public bool SetStation()
            {
                if (!_station) return false;
                CurrentStation.SetValue(Player, _station);
                return Player.GetCurrentCraftingStation() == _station;
            }

            public void ClearStation()
            {
                if (_station && Player && Player.GetCurrentCraftingStation() == _station)
                    CurrentStation.SetValue(Player, null);
            }

            // A workbench recipe of early materials the player carries none of, whose result the
            // player carries none of either. Two or more materials are preferred, so the
            // incomplete craft can take one and fail on the next.
            public Recipe SelectRecipe(out string report)
            {
                report = "no workbench recipe of early materials you carry none of";
                var workbench = ZNetScene.instance.GetPrefab("piece_workbench")?.GetComponent<CraftingStation>();
                if (!workbench) return null;
                Recipe single = null;
                foreach (var recipe in ObjectDB.instance.m_recipes)
                {
                    if (!recipe || !recipe.m_item || !recipe.m_enabled || recipe.m_requireOnlyOneIngredient) continue;
                    if (!recipe.m_craftingStation || recipe.m_craftingStation.m_name != workbench.m_name ||
                        recipe.m_minStationLevel > 1) continue;
                    if (recipe.m_item.m_itemData.m_shared.m_dlc.Length > 0) continue;
                    if (Inventory.GetAllItems().Any(x => x.m_shared.m_name == recipe.m_item.m_itemData.m_shared.m_name)) continue;
                    var needs = Needs(recipe);
                    if (needs.Count == 0 || needs.Any(x => !EarlyMaterials.Contains(x.Key.name) || Carried(x.Key) ||
                                                           x.Value > Shared(x.Key).m_maxStackSize)) continue;
                    if (needs.Count == 1)
                    {
                        if (!single) single = recipe;
                        continue;
                    }
                    return Use(recipe);
                }
                return single ? Use(single) : null;
            }

            private Recipe Use(Recipe recipe)
            {
                foreach (var prefab in Needs(recipe).Keys) Claim(prefab);
                _testNames.Add(recipe.m_item.m_itemData.m_shared.m_name);
                return recipe;
            }

            // A hammer piece of early materials the player carries none of.
            public Piece SelectPiece(out string report)
            {
                report = "no hammer piece of early materials you carry none of";
                var table = ObjectDB.instance.GetItemPrefab("Hammer")?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces;
                if (!table) return null;
                foreach (var prefab in table.m_pieces)
                {
                    var piece = prefab ? prefab.GetComponent<Piece>() : null;
                    if (!piece || piece.m_dlc.Length > 0) continue;
                    var needs = Needs(piece);
                    if (needs.Count == 0 || needs.Count > 3 || needs.Any(x => !EarlyMaterials.Contains(x.Key.name) ||
                                                                               Carried(x.Key) || x.Value > Shared(x.Key).m_maxStackSize)) continue;
                    foreach (var material in needs.Keys) Claim(material);
                    return piece;
                }
                return null;
            }

            public Fireplace SpawnFireplace(out string problem)
            {
                problem = "no buildable fireplace that takes an early fuel you carry none of";
                foreach (var prefab in ZNetScene.instance.m_prefabs)
                {
                    var fireplace = prefab ? prefab.GetComponent<Fireplace>() : null;
                    if (!fireplace || !prefab.GetComponent<Piece>() || !fireplace.m_canRefill || fireplace.m_infiniteFuel ||
                        fireplace.m_canTurnOff || !fireplace.m_fuelItem || fireplace.m_startFuel + 3f > fireplace.m_maxFuel) continue;
                    if (!Claim(fireplace.m_fuelItem.gameObject)) continue;
                    var go = Spawn(prefab, -Player.transform.right * 2f + Player.transform.forward * 2f, true);
                    Fireplace = go ? go.GetComponent<Fireplace>() : null;
                    if (!Fireplace) problem = "the spawned fireplace has no Fireplace";
                    return Fireplace;
                }
                return null;
            }

            public Smelter SpawnSmelter(out GameObject ore, out string problem)
            {
                ore = null;
                problem = "the smelter prefab was not found";
                var prefab = ZNetScene.instance.GetPrefab("smelter");
                var template = prefab ? prefab.GetComponent<Smelter>() : null;
                if (!template) return null;
                var accepted = template.m_conversion.Where(x => x?.m_from).Select(x => x.m_from.gameObject).ToList();
                if (accepted.Any(Carried))
                    problem = "you carry an item the smelter takes";
                else
                {
                    ore = accepted.FirstOrDefault(x => EarlyMaterials.Contains(x.name));
                    if (ore) Claim(ore);
                    else problem = "the smelter takes no early material";
                }
                var go = Spawn(prefab, -Player.transform.forward * 3f, true);
                var smelter = go ? go.GetComponent<Smelter>() : null;
                if (!smelter) problem = "the spawned smelter has no Smelter";
                return smelter;
            }

            public CookingStation SetUpCookingStation(out string problem)
            {
                problem = null;
                var prefab = ZNetScene.instance.GetPrefab("piece_cookingstation");
                var firePrefab = ZNetScene.instance.GetPrefab("fire_pit");
                var template = prefab ? prefab.GetComponent<CookingStation>() : null;
                if (!template || !firePrefab || CookingOnInteract == null || CookingFireLit == null || CookingFreeSlot == null)
                {
                    problem = "the cooking station, the fire pit or a member the check drives was not found";
                    return null;
                }
                var accepted = template.m_conversion.Where(x => x?.m_from).Select(x => x.m_from.gameObject).ToList();
                if (accepted.Any(Carried))
                {
                    problem = "you carry an item the cooking station takes";
                    return null;
                }
                CookingItem = accepted.FirstOrDefault(x => EarlyMaterials.Contains(x.name));
                if (!CookingItem || !Claim(CookingItem))
                {
                    problem = "the cooking station takes no early material";
                    return null;
                }

                var offset = -Player.transform.right * 3.5f - Player.transform.forward * 1f;
                var fire = Spawn(firePrefab, offset, true);
                if (fire && Objects.GetZNetView(fire.transform, out var fireView) && fireView && fireView.IsValid())
                    fireView.GetZDO().Set(ZDOVars.s_fuel, 5f);
                var go = Spawn(prefab, offset, true);
                var cooking = go ? go.GetComponent<CookingStation>() : null;
                if (!cooking) problem = "the spawned cooking station has no CookingStation";
                return cooking;
            }

            // A ward owned by nobody the player knows, switched on over the chest.
            public bool RaiseWard(out string problem)
            {
                problem = null;
                var prefab = ZNetScene.instance.GetPrefab("guard_stone");
                if (!prefab || !Chest.m_checkGuardStone)
                {
                    problem = prefab ? "the test chest does not check wards" : "the ward prefab was not found";
                    return false;
                }
                _wardGo = Spawn(prefab, Chest.transform.position - Player.transform.position + Player.transform.forward, true);
                var piece = _wardGo ? _wardGo.GetComponent<Piece>() : null;
                if (!piece || !Objects.GetZNetView(_wardGo.transform, out var view) || !view || !view.IsValid())
                {
                    problem = "the spawned ward is not usable";
                    return false;
                }
                // Built by a player id no one has, so the player has no access through it.
                var creator = AccessTools.Field(typeof(Piece), "m_creator");
                if (creator == null)
                {
                    problem = "the ward's creator field could not be resolved";
                    return false;
                }
                creator.SetValue(piece, -424242L);
                view.GetZDO().Set(ZDOVars.s_creator, -424242L);
                view.GetZDO().Set(ZDOVars.s_enabled, true);
                return true;
            }

            public bool LowerWard()
            {
                if (!_wardGo) return true;
                if (!Remove(_wardGo)) return false;
                _wardGo = null;
                return true;
            }

            // Puts exactly these amounts in the chest, after taking out what the section put there.
            public void FillChest(Dictionary<GameObject, int> amounts)
            {
                ClearChest();
                foreach (var pair in amounts)
                {
                    // Only a test item ever goes in, so everything the chest holds stays provably
                    // the check's own.
                    if (!Claim(pair.Key)) continue;
                    var name = Shared(pair.Key).m_name;
                    var before = Count(ChestInventory, name, -1);
                    var left = pair.Value;
                    try
                    {
                        while (left > 0)
                        {
                            var stack = Mathf.Min(left, Shared(pair.Key).m_maxStackSize);
                            if (!ChestInventory.AddItem(pair.Key, stack)) break;
                            left -= stack;
                        }
                    }
                    finally
                    {
                        CreatedByTest(name, Count(ChestInventory, name, -1) - before);
                    }
                }
                SupplyContainers.Invalidate();
            }

            // Adds units of a test item to the chest and leaves Automatic Supply's stock as it
            // was read, as when another game or module changes the chest.
            public bool AddToChest(GameObject prefab, int units)
            {
                if (ChestInventory == null || !Claim(prefab)) return false;
                var name = Shared(prefab).m_name;
                var before = Count(ChestInventory, name, -1);
                try
                {
                    ChestInventory.AddItem(prefab, units);
                }
                finally
                {
                    CreatedByTest(name, Count(ChestInventory, name, -1) - before);
                }
                return Count(ChestInventory, name, -1) - before == units;
            }

            public void CreatedByTest(string name, int units)
            {
                if (units <= 0) return;
                _budget[name] = (_budget.TryGetValue(name, out var budget) ? budget : 0) + units;
            }

            // Gives the player a stack of a test item, counted into what the section may remove.
            public ItemDrop.ItemData GiveToInventory(GameObject prefab, int stack)
            {
                if (!Claim(prefab)) return null;
                var name = Shared(prefab).m_name;
                var before = Count(Inventory, name, -1);
                try
                {
                    return Inventory.AddItem(prefab.name, stack, 1, 0, 0L, "", false);
                }
                finally
                {
                    CreatedByTest(name, Count(Inventory, name, -1) - before);
                }
            }

            // Called after every frame boundary: records any test item already in the inventory or
            // the chest as not the section's own, and reports whether there was any.
            public bool Settled(out string problem)
            {
                problem = null;
                var found = 0;
                foreach (var name in _testNames)
                {
                    var inInventory = Count(Inventory, name, -1);
                    var inChest = Count(ChestInventory, name, -1);
                    _inventoryKeep[name] = inInventory;
                    _chestKeep[name] = inChest;
                    found += inInventory + inChest;
                }
                _waiting = false;
                if (found > 0)
                    problem = $"{found} unit(s) of the section's test items arrived from elsewhere while it waited; they are left where they are and the section stops";
                return found == 0;
            }

            // Fills the inventory with test filler stacks until only emptySlots slots are free.
            public bool FillInventory(int emptySlots)
            {
                if (!_filler)
                    _filler = ObjectDB.instance.m_items.FirstOrDefault(x => x && EarlyMaterials.Contains(x.name) &&
                                                                            !_testNames.Contains(Shared(x).m_name) && Claim(x));
                if (!_filler) return false;
                var max = Shared(_filler).m_maxStackSize;
                for (var guard = 0; guard < 200 && Inventory.GetEmptySlots() > emptySlots; guard++)
                    if (GiveToInventory(_filler, max) == null) break;
                return Inventory.GetEmptySlots() == emptySlots;
            }

            // Takes every test item out of the inventory and the chest: the player carried none of
            // them when the section started.
            public void ClearTestItems()
            {
                if (Player) RemoveTestUnits(Inventory, _inventoryKeep, true);
                ClearChest();
                // Both sides are now clear of the section's units; what it consumed (crafting
                // inputs, station intake) or could not account for is not its to remove later, so
                // the budget is the net the section has left behind: nothing.
                foreach (var name in _testNames) _budget[name] = 0;
            }

            private void ClearChest()
            {
                var chest = ChestInventory;
                if (chest == null) return;
                RemoveTestUnits(chest, _chestKeep, false);
                SupplyContainers.Invalidate();
            }

            // Removes, for every test item, what exceeds the units recorded as not the section's
            // own, and never more than the section put in and has not removed yet. When the excess
            // is larger than that, the count is ambiguous: nothing of that item is removed, and it
            // is reported.
            private void RemoveTestUnits(Inventory inventory, Dictionary<string, int> keep, bool player)
            {
                if (inventory == null) return;
                foreach (var name in _testNames)
                {
                    var excess = Count(inventory, name, -1) - (keep.TryGetValue(name, out var kept) ? kept : 0);
                    if (excess <= 0) continue;
                    var budget = _budget.TryGetValue(name, out var b) ? b : 0;
                    if (excess > budget)
                    {
                        LeftItems = true;
                        FeaturesSmokeCommand.Warn(
                            $"[Automatics Smoke] {Label}: {excess} unit(s) of a test item are more than the {budget} the check can account for, so none of them was removed from the {(player ? "inventory" : "test chest")}");
                        continue;
                    }

                    var removed = 0;
                    foreach (var item in inventory.GetAllItems().Where(x => x.m_shared.m_name == name).ToList())
                    {
                        if (removed >= excess) break;
                        if (player && Player.IsItemEquiped(item)) Player.UnequipItem(item, false);
                        var take = Mathf.Min(excess - removed, item.m_stack);
                        var before = Count(inventory, name, -1);
                        inventory.RemoveItem(item, take);
                        removed += Mathf.Max(0, before - Count(inventory, name, -1));
                    }
                    _budget[name] = budget - removed;
                }
            }

            public bool HandBack(string reason)
            {
                if (_done) return true;
                var complete = true;
                try
                {
                    // Cut short during a wait: what arrived meanwhile is recorded as not the
                    // section's own before anything is removed.
                    if (_waiting && Player) Settled(out _);
                    if (Player)
                    {
                        if (NoCostLifted) NoPlacementCost.SetValue(Player, true);
                        NoCostLifted = false;
                        ClearStation();
                        RestoreSkill(_crafting, Skills.SkillType.Crafting, _skillValues[0], _skillValues[1]);
                        RestoreSkill(_cooking, Skills.SkillType.Cooking, _skillValues[2], _skillValues[3]);
                        RemoveTestUnits(Inventory, _inventoryKeep, true);
                    }
                    if (!LowerWard()) complete = false;
                    if (Chest && Chest.IsInUse()) Chest.SetInUse(false);
                    foreach (var go in _spawned.Where(x => x && x != _chestGo && x != _wardGo).ToList())
                    {
                        if (SmokeSession.DestroyTestObject(go, 0, reason)) SmokeSession.ReleaseSpawn(go);
                        else complete = false;
                    }
                    if (_chestGo)
                    {
                        // Only the section's own units are taken out; the chest goes only when that
                        // leaves it empty. Anything else in it is not provably the check's own.
                        ClearChest();
                        var rest = ChestInventory?.GetAllItems().ToList() ?? new List<ItemDrop.ItemData>();
                        if (rest.Count > 0)
                        {
                            KeptChest = true;
                            var p = _chestGo.transform.position;
                            if (Objects.GetZNetView(_chestGo.transform, out var view) && view && view.IsValid())
                                view.GetZDO().Persistent = true;
                            FeaturesSmokeCommand.Warn(
                                $"[Automatics Smoke] {reason}: the test chest at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) was left in the world because it holds {rest.Count} stack(s), {rest.Sum(x => x.m_stack)} unit(s), the check cannot prove it put there; it is now saved with your world, so take out what is yours and then remove it");
                            _chestGo = null;
                        }
                        else if (Remove(_chestGo))
                        {
                            _chestGo = null;
                        }
                        else
                        {
                            // Kept for the next hand-back trigger.
                            complete = false;
                        }
                    }
                }
                catch (Exception e)
                {
                    FeaturesSmokeCommand.Warn($"[Automatics Smoke] {reason}: hand-back failed ({e.GetType().Name}: {e.Message}); it is tried again");
                    return false;
                }
                _done = complete;
                return complete;
            }

            private GameObject Spawn(GameObject prefab, Vector3 offset, bool track)
            {
                var position = Player.transform.position + offset;
                position.y = ZoneSystem.instance.GetGroundHeight(position);
                var go = Object.Instantiate(prefab, position, Quaternion.identity);
                _spawned.Add(go);
                if (track) SmokeSession.TrackSpawn(go);
                if (Objects.GetZNetView(go.transform, out var view) && view && view.IsValid())
                {
                    view.GetZDO().Persistent = false;
                    if (!view.IsOwner()) view.ClaimOwnership();
                }
                var wear = go.GetComponent<WearNTear>();
                if (wear)
                {
                    wear.m_noSupportWear = false;
                    wear.m_noRoofWear = false;
                }
                return go;
            }

            private static bool Remove(GameObject go)
            {
                if (!SmokeSession.DestroyTestObject(go, 0, Label)) return false;
                SmokeSession.ReleaseSpawn(go);
                return true;
            }

            private Skills.Skill Skill(Skills.SkillType type)
            {
                return Player.GetSkills()?.GetSkillList().FirstOrDefault(x => x.m_info != null && x.m_info.m_skill == type);
            }

            private void RestoreSkill(Skills.Skill before, Skills.SkillType type, float level, float accumulator)
            {
                var skill = Skill(type);
                if (skill == null) return;
                skill.m_level = before != null ? level : 0f;
                skill.m_accumulator = before != null ? accumulator : 0f;
            }
        }
    }
}
