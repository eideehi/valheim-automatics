using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticDoor
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Door), "Awake")]
        private static void Door_Awake_Postfix(Door __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<AutomaticDoor>();
        }
    }
}