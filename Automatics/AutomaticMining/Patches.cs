using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticMining
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    public static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Destructible), "Start")]
        private static void Destructible_Awake_Postfix(Destructible __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<AutomaticMining>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MineRock), "Start")]
        private static void MineRock_Start_Postfix(MineRock __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<AutomaticMining>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MineRock5), "Start")]
        private static void MineRock5_Start_Postfix(MineRock5 __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<AutomaticMining>();
        }
    }
}