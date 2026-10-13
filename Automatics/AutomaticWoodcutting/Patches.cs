using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticWoodcutting
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TreeBase), "Awake")]
        private static void TreeBase_Awake_Postfix(TreeBase __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachTree(__instance.gameObject);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TreeLog), "Awake")]
        private static void TreeLog_Awake_Postfix(TreeLog __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachTree(__instance.gameObject);
        }
    }
}
