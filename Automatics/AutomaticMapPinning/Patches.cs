using HarmonyLib;

namespace Automatics.AutomaticMapPinning
{
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Minimap), "Update")]
        private static void MinimapUpdatePostfix()
        {
            if (!AutomaticMapPinning.IsEnabled()) return;

            AutomaticMapPinning.OnUpdate();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Minimap), "RemovePin", typeof(Minimap.PinData))]
        private static void MinimapRemovePinPostfix(Minimap.PinData pin)
        {
            if (!AutomaticMapPinning.IsEnabled()) return;

            AutomaticMapPinning.OnRemovePin(pin);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Pickable), "SetPicked")]
        private static void PickableSetPickedPostfix(Pickable __instance, bool picked)
        {
            if (!AutomaticMapPinning.IsEnabled()) return;

            if (picked && (__instance.m_respawnTimeMinutes <= 0 || !__instance.m_hideWhenPicked))
            {
                AutomaticMapPinning.RemoveStaticPin(__instance.transform.position);
            }
        }
    }
}