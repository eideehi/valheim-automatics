using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticStorage
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGrid), "OnLeftDown")]
        private static bool InventoryGrid_OnLeftDown_Prefix(InventoryGrid __instance,
            UIInputHandler clickHandler)
        {
            return !SlotLocks.ToggleFromClick(__instance, clickHandler);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryGrid), "OnLeftClick")]
        private static bool InventoryGrid_OnLeftClick_Prefix(InventoryGrid __instance)
        {
            return !SlotLocks.IsLockGesture(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateInventory))]
        private static void InventoryGrid_UpdateInventory_Postfix(InventoryGrid __instance,
            Player player)
        {
            if (!Module.Running) return;
            var gui = InventoryGui.instance;
            if (gui && __instance == gui.m_playerGrid)
                SlotLocks.RefreshIcons(__instance, player);
        }
    }
}
