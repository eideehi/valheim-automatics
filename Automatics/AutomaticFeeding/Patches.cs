using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticFeeding
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Tameable), "Awake")]
        private static void Tameable_Awake_Postfix(Tameable __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<AutomaticFeeding>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MonsterAI), "UpdateConsumeItem")]
        private static void MonsterAI_UpdateConsumeItem_Postfix(MonsterAI __instance,
            ref bool __result,
            Humanoid humanoid, float dt)
        {
            if (!Config.EnableAutomaticFeeding) return;

            if (!__result && AutomaticFeeding.Feeding(__instance, humanoid, dt))
                __result = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.CanSeeTarget), new[] { typeof(StaticTarget) })]
        private static void BaseAI_CanSeeTarget_Postfix(BaseAI __instance,
            ref bool __result,
            StaticTarget target)
        {
            if (!__result) return;
            if (!Config.EnableAutomaticFeeding) return;
            if (AutomaticFeeding.CancelAttackOnFeedBox(__instance, target))
                __result = false;
        }
    }
}