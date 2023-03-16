using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "Interact")]
        private static bool Player_Interact_Prefix(GameObject go)
        {
            if (!go.GetComponentInParent<ItemPicker>()) return true;
            return !Config.PickupAllNearbyKey.IsPressed();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pickable), "Awake")]
        private static void Pickable_Awake_Postfix(Pickable __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<ItemPicker>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pickable), "GetHoverText")]
        private static void Pickable_GetHoverText_Postfix(Pickable __instance, ref string __result,
            bool ___m_picked)
        {
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
            if (___m_nview.GetZDO() != null)
            {
                __instance.gameObject.AddComponent<PickableItemCache>();
                __instance.gameObject.AddComponent<ItemPicker>();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PickableItem), "GetHoverText")]
        private static void PickableItem_GetHoverText_Postfix(PickableItem __instance,
            ref string __result, bool ___m_picked)
        {
            if (Config.PickupAllNearbyKey.MainKey == KeyCode.None) return;
            if (___m_picked || !__instance.m_itemPrefab) return;

            var keyCode = Config.PickupAllNearbyKey.ToString();
            var name = __instance.m_itemPrefab.m_itemData.m_shared.m_name;
            var message =
                Automatics.L10N.Localize("@message_automatic_pickup_pickup_all_nearby", name);
            __result += $"\n[<color=yellow><b>{keyCode}</b></color>] {message}";
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemDrop), "Awake")]
        private static void ItemDrop_Awake_Postfix(ItemDrop __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.IsValid())
                __instance.gameObject.AddComponent<ItemPicker>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemDrop), "GetHoverText")]
        private static void ItemDrop_GetHoverText_Postfix(ItemDrop __instance, ref string __result)
        {
            if (Config.PickupAllNearbyKey.MainKey == KeyCode.None) return;

            var keyCode = Config.PickupAllNearbyKey.ToString();
            var name = __instance.m_itemData.m_shared.m_name;
            var message =
                Automatics.L10N.Localize("@message_automatic_pickup_pickup_all_nearby", name);
            __result += $"\n[<color=yellow><b>{keyCode}</b></color>] {message}";
        }
    }
}