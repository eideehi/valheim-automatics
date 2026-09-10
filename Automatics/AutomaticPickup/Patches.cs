using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    /// <summary>
    ///     Every patch below opens with the module's effective-state gate. Two different windows
    ///     need it: the frame between a disable request and the pump that removes the patches,
    ///     and - on a Harmony build that cannot unpatch - the rest of the session, where the
    ///     patches stay applied and the gate is the whole of "off" (R1.5 / R1.5a / R3.2).
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Interact")]
        private static bool Player_Interact_Prefix(GameObject go)
        {
            if (!Module.Running) return true;
            if (!go) return true;

            if (!go.GetComponentInParent<Pickable>() &&
                !go.GetComponentInParent<PickableItem>() &&
                !go.GetComponentInParent<ItemDrop>())
                return true;

            return !Config.PickupAllNearbyKey.IsPressed();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pickable), "GetHoverText")]
        private static void Pickable_GetHoverText_Postfix(Pickable __instance, ref string __result,
            bool ___m_picked)
        {
            if (!Module.Running) return;
            if (Config.PickupAllNearbyKey.MainKey == KeyCode.None) return;
            if (___m_picked) return;

            var keyCode = Config.PickupAllNearbyKey.ToString();
            var name = __instance.GetHoverName();
            var message =
                Automatics.L10N.Localize("@message_automatic_pickup_pickup_all_nearby", name);
            __result += $"\n[<color=yellow><b>{keyCode}</b></color>] {message}";
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PickableItem), "Awake")]
        private static void PickableItem_Awake_Postfix(PickableItem __instance, ZNetView ___m_nview)
        {
            // Discovering, not Running: this patch has no side effect on the game, it only
            // takes a new object into the activation's ownership. Gating discovery on the stop
            // request would drop every PickableItem that awakes in the frame between a disable
            // request and the pump, and nothing would ever come back for them if the user
            // changed their mind before the pump ran.
            if (!Module.Discovering) return;
            if (___m_nview == null || ___m_nview.GetZDO() == null) return;

            // Through the scope, never AddComponent: a component this module creates while it
            // runs has to be owned by the activation that created it, or the next disable leaves
            // it in the scene (MAJOR-4).
            Module.AttachCache(__instance.gameObject);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PickableItem), "GetHoverText")]
        private static void PickableItem_GetHoverText_Postfix(PickableItem __instance,
            ref string __result, bool ___m_picked)
        {
            if (!Module.Running) return;
            if (Config.PickupAllNearbyKey.MainKey == KeyCode.None) return;
            if (___m_picked || !__instance.m_itemPrefab) return;

            var keyCode = Config.PickupAllNearbyKey.ToString();
            var name = __instance.m_itemPrefab.m_itemData.m_shared.m_name;
            var message =
                Automatics.L10N.Localize("@message_automatic_pickup_pickup_all_nearby", name);
            __result += $"\n[<color=yellow><b>{keyCode}</b></color>] {message}";
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemDrop), "GetHoverText")]
        private static void ItemDrop_GetHoverText_Postfix(ItemDrop __instance, ref string __result)
        {
            if (!Module.Running) return;
            if (Config.PickupAllNearbyKey.MainKey == KeyCode.None) return;

            var keyCode = Config.PickupAllNearbyKey.ToString();
            var name = __instance.m_itemData.m_shared.m_name;
            var message =
                Automatics.L10N.Localize("@message_automatic_pickup_pickup_all_nearby", name);
            __result += $"\n[<color=yellow><b>{keyCode}</b></color>] {message}";
        }
    }
}