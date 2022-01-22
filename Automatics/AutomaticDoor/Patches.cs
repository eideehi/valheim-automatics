using HarmonyLib;

namespace Automatics.AutomaticDoor
{
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Door), "Awake")]
        private static void DoorAwakePostfix(Door __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<AutomaticDoor>();
        }
    }
}