using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticSupply
{
    /// <summary>
    ///     While a vanilla requirement check or requirement panel runs for the local player, the
    ///     inventory counts it asks for include what the nearby containers hold. Everything lives on
    ///     the Unity main thread; every raise is undone by a finalizer.
    /// </summary>
    internal static class SupplyScope
    {
        private static int _depth;
        private static int _suppression;
        private static object _context;

        /// <summary>
        ///     True while container stock is added to the local player's counts: a scope is raised,
        ///     no craft is re-checking its requirements, and the module runs.
        /// </summary>
        internal static bool Counting => _depth > 0 && _suppression == 0 && Module.Running;

        internal static bool Suppressed => _suppression > 0;

        /// <summary>The recipe or piece whose requirements are being counted.</summary>
        internal static object Context => _context;

        internal static Frame Raise(object context)
        {
            var frame = new Frame(true, _context);
            _depth++;
            _context = context;
            return frame;
        }

        internal static void Lower(Frame frame)
        {
            if (!frame.Raised) return;
            if (_depth > 0) _depth--;
            _context = frame.PreviousContext;
        }

        internal static void Suppress()
        {
            _suppression++;
        }

        internal static void Unsuppress()
        {
            if (_suppression > 0) _suppression--;
        }

        internal static void Reset()
        {
            _depth = 0;
            _suppression = 0;
            _context = null;
        }

        /// <summary>
        ///     Whether <paramref name="name" /> is a requirement of the current context, so that only
        ///     the items the check or panel is about are counted from containers.
        /// </summary>
        internal static bool IsRequirement(string name)
        {
            Piece.Requirement[] requirements;
            switch (_context)
            {
                case Recipe recipe:
                    requirements = recipe.m_resources;
                    break;
                case Piece piece:
                    requirements = piece.m_resources;
                    break;
                default:
                    return false;
            }

            if (requirements == null) return false;
            foreach (var requirement in requirements)
                if (requirement != null && requirement.m_resItem &&
                    requirement.m_resItem.m_itemData.m_shared.m_name == name)
                    return true;
            return false;
        }

        internal readonly struct Frame
        {
            public Frame(bool raised, object previousContext)
            {
                Raised = raised;
                PreviousContext = previousContext;
            }

            public bool Raised { get; }
            public object PreviousContext { get; }
        }
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        private static readonly FieldInfo SelectedRecipeField =
            AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");

        private static readonly Func<Smelter, int> SmelterQueueSize =
            AccessTools.MethodDelegate<Func<Smelter, int>>(AccessTools.Method(typeof(Smelter), "GetQueueSize"));

        private static readonly Func<Smelter, float> SmelterFuel =
            AccessTools.MethodDelegate<Func<Smelter, float>>(AccessTools.Method(typeof(Smelter), "GetFuel"));

        private static readonly Func<CookingStation, float> CookingStationFuel =
            AccessTools.MethodDelegate<Func<CookingStation, float>>(AccessTools.Method(typeof(CookingStation), "GetFuel"));

        private static readonly Func<CookingStation, bool> CookingStationHasDoneItem =
            AccessTools.MethodDelegate<Func<CookingStation, bool>>(AccessTools.Method(typeof(CookingStation), "HaveDoneItem"));

        private static readonly Func<CookingStation, bool> CookingStationFireLit =
            AccessTools.MethodDelegate<Func<CookingStation, bool>>(AccessTools.Method(typeof(CookingStation), "IsFireLit"));

        private static readonly Func<CookingStation, int> CookingStationFreeSlot =
            AccessTools.MethodDelegate<Func<CookingStation, int>>(AccessTools.Method(typeof(CookingStation), "GetFreeSlot"));

        private static PropertyInfo _selectedRecipeProperty;

        // Placement: UpdatePlacement is the only caller of TryPlacePiece, and it pays after
        // TryPlacePiece returns without checking again, so the placement's action is opened in
        // the TryPlacePiece prefix and closed when UpdatePlacement returns.
        private static int _placementDepth;
        private static SupplyAction _placementAction;

        /// <summary>How many placements this module refused because their cost could not be secured.</summary>
        internal static int PlacementRejections { get; private set; }

        internal static void ResetSession()
        {
            _placementDepth = 0;
            _placementAction = null;
            _removalDepth = 0;
            _panelStockVersion = -1;
            _panelRebuildPending = false;
            _panelRebuildHeld = false;
        }

        private static bool IsLocalPlayer(Humanoid character)
        {
            return character && ReferenceEquals(character, Player.m_localPlayer);
        }

        private static bool IsLocalInventory(Inventory inventory)
        {
            var player = Player.m_localPlayer;
            return player && inventory != null && ReferenceEquals(inventory, player.GetInventory());
        }

        // -- Counting ---------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "HaveRequirementItems")]
        private static void Player_HaveRequirementItems_Prefix(Player __instance, Recipe piece, bool discover,
            out SupplyScope.Frame __state)
        {
            __state = default;
            if (!Module.Running) return;
            // Recipes where the player chooses one ingredient keep the vanilla count.
            if (discover || !piece || piece.m_requireOnlyOneIngredient || SupplyScope.Suppressed) return;
            if (!IsLocalPlayer(__instance)) return;
            __state = SupplyScope.Raise(piece);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Player), "HaveRequirementItems")]
        private static void Player_HaveRequirementItems_Finalizer(SupplyScope.Frame __state)
        {
            SupplyScope.Lower(__state);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
        private static void Player_HaveRequirements_Prefix(Player __instance, Piece piece,
            Player.RequirementMode mode, out SupplyScope.Frame __state)
        {
            __state = default;
            if (!Module.Running) return;
            if (mode != Player.RequirementMode.CanBuild && mode != Player.RequirementMode.CanAlmostBuild) return;
            if (!piece || SupplyScope.Suppressed || !IsLocalPlayer(__instance)) return;
            __state = SupplyScope.Raise(piece);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), typeof(Piece), typeof(Player.RequirementMode))]
        private static void Player_HaveRequirements_Finalizer(SupplyScope.Frame __state)
        {
            SupplyScope.Lower(__state);
        }

        // The crafting panel counts each requirement itself, so it follows the check: the same
        // container stock, and the same exclusion of recipes where the player chooses one
        // ingredient.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGui), "SetupRequirementList")]
        private static void InventoryGui_SetupRequirementList_Prefix(InventoryGui __instance, Player player,
            out SupplyScope.Frame __state)
        {
            __state = default;
            if (!Module.Running) return;
            if (!IsLocalPlayer(player) || SupplyScope.Suppressed) return;
            var recipe = SelectedRecipe(__instance);
            if (!recipe || recipe.m_requireOnlyOneIngredient) return;
            __state = SupplyScope.Raise(recipe);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(InventoryGui), "SetupRequirementList")]
        private static void InventoryGui_SetupRequirementList_Finalizer(SupplyScope.Frame __state)
        {
            SupplyScope.Lower(__state);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        private static void Hud_SetupPieceInfo_Prefix(Piece piece, out SupplyScope.Frame __state)
        {
            __state = default;
            if (!Module.Running) return;
            if (!piece || !Player.m_localPlayer || SupplyScope.Suppressed) return;
            __state = SupplyScope.Raise(piece);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
        private static void Hud_SetupPieceInfo_Finalizer(SupplyScope.Frame __state)
        {
            SupplyScope.Lower(__state);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems), typeof(string), typeof(int), typeof(bool))]
        private static void Inventory_CountItems_Postfix(Inventory __instance, string name, int quality,
            bool matchWorldLevel, ref int __result)
        {
            if (!SupplyScope.Counting || name == null) return;
            if (!IsLocalInventory(__instance) || !SupplyScope.IsRequirement(name)) return;
            __result += SupplyContainers.Count(Player.m_localPlayer, name, quality, matchWorldLevel);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem), typeof(string), typeof(bool))]
        private static void Inventory_HaveItem_Postfix(Inventory __instance, string name, bool matchWorldLevel,
            ref bool __result)
        {
            if (__result || !SupplyScope.Counting || name == null) return;
            if (!IsLocalInventory(__instance) || !SupplyScope.IsRequirement(name)) return;
            __result = SupplyContainers.Count(Player.m_localPlayer, name, -1, matchWorldLevel) > 0;
        }

        private static Recipe SelectedRecipe(InventoryGui gui)
        {
            if (SelectedRecipeField == null) return null;
            var pair = SelectedRecipeField.GetValue(gui);
            if (pair == null) return null;
            if (_selectedRecipeProperty == null)
                _selectedRecipeProperty = AccessTools.Property(pair.GetType(), "Recipe");
            return _selectedRecipeProperty?.GetValue(pair, null) as Recipe;
        }

        // -- Consumption accounting -------------------------------------------------------------

        // Every removal overload records what it took from the open action's player inventory.
        // Overloads call each other, so only the outermost one measures.
        private static int _removalDepth;

        private static int BeginRemoval(Inventory inventory, string name)
        {
            if (_removalDepth++ > 0) return -1;
            return SupplyTransfer.DebitBaseline(inventory, name);
        }

        private static void EndRemoval(Inventory inventory, string name, int before)
        {
            if (_removalDepth > 0) _removalDepth--;
            if (before >= 0) SupplyTransfer.RecordDebit(inventory, name, before);
        }

        private static string NameOf(ItemDrop.ItemData item)
        {
            return item?.m_shared?.m_name;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(string), typeof(int), typeof(int), typeof(bool))]
        private static void Inventory_RemoveItemByName_Prefix(Inventory __instance, string name, out int __state)
        {
            __state = BeginRemoval(__instance, name);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(string), typeof(int), typeof(int), typeof(bool))]
        private static void Inventory_RemoveItemByName_Finalizer(Inventory __instance, string name, int __state)
        {
            EndRemoval(__instance, name, __state);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(ItemDrop.ItemData))]
        private static void Inventory_RemoveItem_Prefix(Inventory __instance, ItemDrop.ItemData item,
            out KeyValuePair<string, int> __state)
        {
            var name = NameOf(item);
            __state = new KeyValuePair<string, int>(name, BeginRemoval(__instance, name));
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(ItemDrop.ItemData))]
        private static void Inventory_RemoveItem_Finalizer(Inventory __instance, KeyValuePair<string, int> __state)
        {
            EndRemoval(__instance, __state.Key, __state.Value);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(ItemDrop.ItemData), typeof(int))]
        private static void Inventory_RemoveItemAmount_Prefix(Inventory __instance, ItemDrop.ItemData item,
            out KeyValuePair<string, int> __state)
        {
            var name = NameOf(item);
            __state = new KeyValuePair<string, int>(name, BeginRemoval(__instance, name));
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(ItemDrop.ItemData), typeof(int))]
        private static void Inventory_RemoveItemAmount_Finalizer(Inventory __instance,
            KeyValuePair<string, int> __state)
        {
            EndRemoval(__instance, __state.Key, __state.Value);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem), typeof(ItemDrop.ItemData))]
        private static void Inventory_RemoveOneItem_Prefix(Inventory __instance, ItemDrop.ItemData item,
            out KeyValuePair<string, int> __state)
        {
            var name = NameOf(item);
            __state = new KeyValuePair<string, int>(name, BeginRemoval(__instance, name));
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem), typeof(ItemDrop.ItemData))]
        private static void Inventory_RemoveOneItem_Finalizer(Inventory __instance, KeyValuePair<string, int> __state)
        {
            EndRemoval(__instance, __state.Key, __state.Value);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(int))]
        private static void Inventory_RemoveItemAt_Prefix(Inventory __instance, int index,
            out KeyValuePair<string, int> __state)
        {
            var name = NameOf(index >= 0 && index < __instance.NrOfItems() ? __instance.GetItem(index) : null);
            __state = new KeyValuePair<string, int>(name, BeginRemoval(__instance, name));
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(int))]
        private static void Inventory_RemoveItemAt_Finalizer(Inventory __instance, KeyValuePair<string, int> __state)
        {
            EndRemoval(__instance, __state.Key, __state.Value);
        }

        // -- Crafting ---------------------------------------------------------------------------

        internal readonly struct CraftFrame
        {
            public CraftFrame(bool suppressed, SupplyAction action)
            {
                Suppressed = suppressed;
                Action = action;
            }

            public bool Suppressed { get; }
            public SupplyAction Action { get; }
        }

        // Takes what the inventory is missing before vanilla checks the requirements again, then
        // hides the containers from that check: an incomplete take fails vanilla's own check, and
        // nothing is crafted. Vanilla consumes the requirements after it creates the item, and the
        // finalizer returns whatever was taken and not consumed.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static void InventoryGui_DoCrafting_Prefix(Player player, Recipe ___m_craftRecipe,
            ItemDrop.ItemData ___m_craftUpgradeItem, bool ___m_multiCrafting, int ___m_multiCraftAmount,
            out CraftFrame __state)
        {
            __state = default;
            if (!Module.Running || !IsLocalPlayer(player)) return;
            SupplyScope.Suppress();
            __state = new CraftFrame(true, null);

            var recipe = ___m_craftRecipe;
            if (!recipe || !recipe.m_item || recipe.m_requireOnlyOneIngredient) return;
            if (player.NoCostCheat() ||
                (ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost))) return;

            var quality = ___m_craftUpgradeItem == null ? 1 : ___m_craftUpgradeItem.m_quality + 1;
            var station = player.GetCurrentCraftingStation();
            if (quality > recipe.m_item.m_itemData.m_shared.m_maxQuality && !(station && station.m_upgrader)) return;
            var multiplier = ___m_multiCrafting ? ___m_multiCraftAmount : 1;
            var plan = PlanCraft(player, recipe, quality, multiplier);
            if (plan == null || plan.Count == 0) return;

            var action = SupplyTransfer.Begin(player);
            if (action == null) return;
            __state = new CraftFrame(true, action);

            var complete = true;
            foreach (var take in plan)
                if (SupplyTransfer.Pull(action, take.Name, take.Quality, take.Amount) < take.Amount)
                    complete = false;
            if (!complete) SupplyTransfer.ShowShortage(action, true);
        }

        // An exception after vanilla created the item and before it consumed the requirements
        // leaves the item unpaid, exactly as it would without this module: that window is
        // vanilla's own. The items taken stay in the inventory, so nothing is lost either.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static void InventoryGui_DoCrafting_Finalizer(InventoryGui __instance, Exception __exception,
            CraftFrame __state)
        {
            if (__state.Suppressed) SupplyScope.Unsuppress();
            var moved = __state.Action != null && __state.Action.Ledger.Count > 0;
            SupplyTransfer.End(__state.Action, __exception == null);
            // The rebuild vanilla asked for inside the craft was held back while the containers
            // were hidden from its check; the recipe list is rebuilt once now that they count
            // again, after what the craft did not use went back.
            var held = _panelRebuildHeld;
            _panelRebuildHeld = false;
            if (__state.Suppressed && (held || moved)) RefreshCraftingPanel(__instance);
        }

        private static readonly Action<InventoryGui, bool> UpdateCraftingPanel =
            AccessTools.MethodDelegate<Action<InventoryGui, bool>>(
                AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel"));

        private static int _panelStockVersion = -1;
        private static bool _panelRebuildHeld;

        private static void RefreshCraftingPanel(InventoryGui gui)
        {
            if (!gui || !InventoryGui.IsVisible() || !Player.m_localPlayer) return;
            try
            {
                // Taken before the rebuild too, so one that throws is not tried again until the
                // stock changes.
                _panelStockVersion = SupplyContainers.Version;
                UpdateCraftingPanel(gui, false);
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Automatic Supply could not refresh the crafting panel\n{e}");
            }
        }

        // Inside a craft the containers are hidden from its requirement check, and vanilla
        // rebuilds the recipe list there; that rebuild is held back for the craft's finalizer.
        // Not gated on Running: it is part of a craft already under way.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
        private static bool InventoryGui_UpdateCraftingPanel_Prefix()
        {
            if (!SupplyScope.Suppressed) return true;
            _panelRebuildHeld = true;
            return false;
        }

        // Every rebuild, vanilla's own included (opening the inventory, a tab, a drag), shows
        // the stock it read, so the stock version is taken after it. A rebuild in which no recipe
        // counts the containers reads none; the stock is read here then, at most every half
        // second like any read. A call a prefix skipped, this module's or another mod's, rebuilt
        // nothing and records nothing.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
        private static void InventoryGui_UpdateCraftingPanel_Postfix(bool __runOriginal)
        {
            if (!__runOriginal || !Module.Running || SupplyScope.Suppressed) return;
            var player = Player.m_localPlayer;
            if (!player) return;
            SupplyContainers.Refresh(player);
            _panelStockVersion = SupplyContainers.Version;
            _panelRebuildPending = false;
        }

        // The recipe list is built when the panel opens and after a craft, so it would keep a
        // container change until then. UpdateRecipe runs while the panel is shown; the stock is
        // refreshed here at most every half second, whichever recipe is selected (one where the
        // player chooses one ingredient counts nothing from the containers). The list is rebuilt
        // only when that refresh found the stock changed, and one tick later, so the rebuild
        // never shares a frame with the refresh or with the panel opening.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipe")]
        private static void InventoryGui_UpdateRecipe_Postfix(InventoryGui __instance, Player player)
        {
            if (!Module.Running || !IsLocalPlayer(player) || SupplyScope.Suppressed) return;
            SupplyContainers.Refresh(player);
            if (_panelStockVersion == SupplyContainers.Version)
            {
                _panelRebuildPending = false;
                return;
            }
            if (!_panelRebuildPending)
            {
                _panelRebuildPending = true;
                return;
            }
            _panelRebuildPending = false;
            RefreshCraftingPanel(__instance);
        }

        private static bool _panelRebuildPending;

        // Vanilla leaves the container panel of the last chest active after Hide, so it can fade
        // out with the inventory, and only hides it in the Update after the next Show, so opening
        // the inventory alone draws that stale panel for a frame or more. With no container shown
        // the panel is hidden at Show; Hide and a real container are left entirely to vanilla.
        // The recipe list Show builds is recorded by the UpdateCraftingPanel postfix.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show), typeof(Container), typeof(int))]
        private static void InventoryGui_Show_Postfix(InventoryGui __instance, Container container)
        {
            if (!Module.Running) return;
            if (container == null) HideStaleContainerPanel(__instance);
        }

        private static void HideStaleContainerPanel(InventoryGui gui)
        {
            if (gui && gui.m_container && gui.m_container.gameObject.activeSelf)
                gui.m_container.gameObject.SetActive(false);
        }

        internal readonly struct Take
        {
            public Take(string name, int quality, int amount)
            {
                Name = name;
                Quality = quality;
                Amount = amount;
            }

            public string Name { get; }
            public int Quality { get; }
            public int Amount { get; }
        }

        /// <summary>
        ///     What a craft must take, requirement by requirement, as vanilla's requirement check
        ///     counts it: the requirements the current station uses (upgrade materials only at an
        ///     upgrading station), the amount for the quality being crafted times the craft
        ///     multiplier, and one quality holding the whole amount. For each requirement short in
        ///     the inventory, the quality the inventory holds most of that the containers can make
        ///     up. Null when a requirement cannot be made up at all, so nothing is taken.
        /// </summary>
        internal static List<Take> PlanCraft(Player player, Recipe recipe, int quality, int multiplier)
        {
            var inventory = player.GetInventory();
            var station = player.GetCurrentCraftingStation();
            var plan = new List<Take>();
            foreach (var requirement in recipe.m_resources)
            {
                if ((station != null && station.m_upgrader != requirement.m_upgraderResource) ||
                    (station == null && requirement.m_upgraderResource) || !requirement.m_resItem) continue;

                var need = requirement.GetAmount(quality) * multiplier;
                if (need <= 0) continue;
                var shared = requirement.m_resItem.m_itemData.m_shared;
                var best = 0;
                for (var q = 1; q <= shared.m_maxQuality; q++)
                    best = Mathf.Max(best, SupplyTransfer.CountRaw(inventory, shared.m_name, q));
                if (best >= need) continue;

                var chosen = -1;
                var chosenHave = -1;
                for (var q = 1; q <= shared.m_maxQuality; q++)
                {
                    var have = SupplyTransfer.CountRaw(inventory, shared.m_name, q);
                    var stock = SupplyContainers.Count(player, shared.m_name, q, true);
                    if (have + stock < need || have <= chosenHave) continue;
                    chosen = q;
                    chosenHave = have;
                }

                if (chosen < 0) return null;
                plan.Add(new Take(shared.m_name, chosen, need - chosenHave));
            }
            return plan;
        }

        // -- Placement --------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static void Player_UpdatePlacement_Prefix(Player __instance, out bool __state)
        {
            __state = false;
            if (!Module.Running || !IsLocalPlayer(__instance)) return;
            _placementDepth++;
            __state = true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static void Player_UpdatePlacement_Finalizer(Exception __exception, bool __state)
        {
            // As with crafting, an exception between the placement and vanilla's payment is
            // vanilla's own window; the items taken stay in the inventory.
            if (!__state) return;
            if (_placementDepth > 0) _placementDepth--;
            if (_placementDepth > 0) return;
            var action = _placementAction;
            _placementAction = null;
            SupplyTransfer.End(action, __exception == null);
        }

        // The piece is placed inside TryPlacePiece and paid for after it returns, with no second
        // check, so the whole cost is secured here: take what the inventory is missing, and refuse
        // the placement when anything is still missing.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        private static bool Player_TryPlacePiece_Prefix(Player __instance, Piece piece, ref bool __result,
            bool ___m_noPlacementCost)
        {
            if (!Module.Running || _placementDepth == 0 || _placementAction != null) return true;
            if (!piece || !IsLocalPlayer(__instance) || ___m_noPlacementCost) return true;
            if (ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return true;

            var inventory = __instance.GetInventory();
            var missing = false;
            foreach (var requirement in piece.m_resources)
            {
                if (!requirement.m_resItem || requirement.m_amount <= 0) continue;
                if (SupplyTransfer.CountRaw(inventory, requirement.m_resItem.m_itemData.m_shared.m_name, -1) <
                    requirement.m_amount) missing = true;
            }
            if (!missing) return true;

            var action = SupplyTransfer.Begin(__instance);
            if (action == null)
            {
                PlacementRejections++;
                __result = false;
                return false;
            }
            // Published before anything moves, so an exception still reaches the finalizer.
            _placementAction = action;
            if (SupplyTransfer.EnsurePieceRequirements(action, piece)) return true;

            PlacementRejections++;
            SupplyTransfer.ShowShortage(action, true);
            _placementAction = null;
            SupplyTransfer.End(action, true);
            __result = false;
            return false;
        }

        // -- Stations ---------------------------------------------------------------------------

        // When the inventory holds nothing the station takes and the station would take an item
        // now, one accepted item comes from a container first, in the station's own order. The
        // finalizer returns it unless the intake removed it from the inventory, whether or not the
        // station then accepted it: that keeps vanilla's own intake as it is.

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        private static void Fireplace_Interact_Prefix(Fireplace __instance, Humanoid user, bool hold, bool alt,
            ZNetView ___m_nview, out SupplyAction __state)
        {
            __state = null;
            if (!Module.Running || hold || !IsLocalPlayer(user)) return;
            if (!__instance.m_canRefill || __instance.m_infiniteFuel || !__instance.m_fuelItem) return;
            if (!___m_nview || !___m_nview.IsValid() || ___m_nview.GetZDO() == null) return;
            var fuel = ___m_nview.GetZDO().GetFloat(ZDOVars.s_fuel);
            if (__instance.m_canTurnOff && !alt && fuel > 0f) return;
            if (Mathf.CeilToInt(fuel) >= __instance.m_maxFuel) return;
            PullOne((Player)user, new[] { __instance.m_fuelItem }, ref __state);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
        private static void Fireplace_Interact_Finalizer(Exception __exception, SupplyAction __state)
        {
            SupplyTransfer.End(__state, __exception == null);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        private static void Smelter_OnAddOre_Prefix(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
            ZNetView ___m_nview, out SupplyAction __state)
        {
            __state = null;
            if (!Module.Running || item != null || !IsLocalPlayer(user)) return;
            if (!___m_nview || !___m_nview.IsValid() || ___m_nview.GetZDO() == null) return;
            if (SmelterQueueSize(__instance) >= __instance.m_maxOre) return;
            var accepted = new List<ItemDrop>();
            foreach (var conversion in __instance.m_conversion)
                if (conversion?.m_from != null)
                    accepted.Add(conversion.m_from);
            PullOne((Player)user, accepted, ref __state);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        private static void Smelter_OnAddOre_Finalizer(Exception __exception, SupplyAction __state)
        {
            SupplyTransfer.End(__state, __exception == null);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        private static void Smelter_OnAddFuel_Prefix(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
            ZNetView ___m_nview, out SupplyAction __state)
        {
            __state = null;
            if (!Module.Running || item != null || !IsLocalPlayer(user) || !__instance.m_fuelItem) return;
            if (!___m_nview || !___m_nview.IsValid() || ___m_nview.GetZDO() == null) return;
            if (SmelterFuel(__instance) > __instance.m_maxFuel - 1) return;
            PullOne((Player)user, new[] { __instance.m_fuelItem }, ref __state);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Smelter), "OnAddFuel")]
        private static void Smelter_OnAddFuel_Finalizer(Exception __exception, SupplyAction __state)
        {
            SupplyTransfer.End(__state, __exception == null);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CookingStation), "OnInteract")]
        private static void CookingStation_OnInteract_Prefix(CookingStation __instance, Humanoid user,
            ZNetView ___m_nview, out SupplyAction __state)
        {
            __state = null;
            if (!Module.Running || !IsLocalPlayer(user)) return;
            if (!___m_nview || !___m_nview.IsValid() || ___m_nview.GetZDO() == null) return;
            // Collecting a finished item comes first in vanilla; that press adds nothing.
            if (CookingStationHasDoneItem(__instance)) return;
            if (__instance.m_requireFire && !CookingStationFireLit(__instance)) return;
            if (CookingStationFreeSlot(__instance) == -1) return;
            var accepted = new List<ItemDrop>();
            foreach (var conversion in __instance.m_conversion)
                if (conversion?.m_from != null)
                    accepted.Add(conversion.m_from);
            PullOne((Player)user, accepted, ref __state);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(CookingStation), "OnInteract")]
        private static void CookingStation_OnInteract_Finalizer(Exception __exception, SupplyAction __state)
        {
            SupplyTransfer.End(__state, __exception == null);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        private static void CookingStation_OnAddFuelSwitch_Prefix(CookingStation __instance, Humanoid user,
            ItemDrop.ItemData item, ZNetView ___m_nview, out SupplyAction __state)
        {
            __state = null;
            if (!Module.Running || item != null || !IsLocalPlayer(user) || !__instance.m_fuelItem) return;
            if (!___m_nview || !___m_nview.IsValid() || ___m_nview.GetZDO() == null) return;
            if (CookingStationFuel(__instance) > __instance.m_maxFuel - 1) return;
            PullOne((Player)user, new[] { __instance.m_fuelItem }, ref __state);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(CookingStation), "OnAddFuelSwitch")]
        private static void CookingStation_OnAddFuelSwitch_Finalizer(Exception __exception, SupplyAction __state)
        {
            SupplyTransfer.End(__state, __exception == null);
        }

        // Takes one of the first accepted item a container holds, in the station's order, when
        // the inventory holds none of them. The open action is published to the caller's
        // finalizer state before anything moves, so a throw still closes it; it is left there
        // only when an item was taken.
        private static void PullOne(Player player, IEnumerable<ItemDrop> accepted, ref SupplyAction published)
        {
            var inventory = player.GetInventory();
            var names = new List<string>();
            foreach (var drop in accepted)
            {
                if (!drop) continue;
                var name = drop.m_itemData.m_shared.m_name;
                if (SupplyTransfer.CountRaw(inventory, name, -1) > 0) return;
                if (!names.Contains(name)) names.Add(name);
            }

            foreach (var name in names)
            {
                if (SupplyContainers.Count(player, name, -1, true) <= 0) continue;
                var action = SupplyTransfer.Begin(player);
                if (action == null) return;
                published = action;
                if (SupplyTransfer.Pull(action, name, -1, 1) > 0) return;
                published = null;
                SupplyTransfer.ShowShortage(action, false);
                SupplyTransfer.End(action, true);
                if (action.InventoryFull) return;
            }
        }
    }
}
