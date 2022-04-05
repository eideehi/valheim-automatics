using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticFeeding
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(MonsterAI), "Awake")]
        private static void MonsterAIAwakePostfix(MonsterAI __instance)
        {
            if (__instance.GetComponent<Tameable>())
                __instance.gameObject.AddComponent<AutomaticFeeding>();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(MonsterAI), "UpdateConsumeItem")]
        private static void MonsterAIUpdateConsumeItemPostfix(MonsterAI __instance, ref bool __result,
            Humanoid humanoid, float dt)
        {
            if (!Config.AutomaticFeedingEnabled) return;
            if (Config.FeedSearchRange == 0f) return;

            if (!__result && AutomaticFeeding.Feeding(__instance, humanoid, dt))
            {
                __result = true;
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(BaseAI), "FindClosestStaticPriorityTarget")]
        private static void BaseAIFindClosestStaticPriorityTargetPostfix(BaseAI __instance, ref StaticTarget __result)
        {
            if (!Config.AutomaticFeedingEnabled) return;
            if ((Config.AllowToFeedFromContainer & Animal.Wild) == 0) return;

            if (__result != null && AutomaticFeeding.IsFeedBox(__instance, __result))
            {
                __result = null;
            }
        }
    }
}