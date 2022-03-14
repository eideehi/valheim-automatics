using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace Automatics.AutomaticMapPinning
{
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Minimap), "Update")]
        private static void MinimapUpdatePostfix()
        {
            if (!AutomaticMapPinning.IsActive()) return;

            AutomaticMapPinning.OnUpdate();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Minimap), "RemovePin", typeof(Minimap.PinData))]
        private static void MinimapRemovePinPostfix(Minimap.PinData pin)
        {
            if (!AutomaticMapPinning.IsActive()) return;

            DynamicMapPinning.RemovePin(pin);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Pickable), "Awake")]
        private static void PickableAwakePostfix(Pickable __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null && AutomaticMapPinning.IsFlora(__instance))
                __instance.gameObject.AddComponent<FloraObject>();
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(Pickable), "SetPicked")]
        private static IEnumerable<CodeInstruction> PickableSetPickedTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var zNetViewDestroy = AccessTools.Method(typeof(ZNetView), "Destroy");
            var pickablePreDestroyHook = AccessTools.Method(typeof(Patches), "PickablePreDestroyHook");

            var codes = new List<CodeInstruction>(instructions);

            for (var i = 0; i < codes.Count; i++)
            {
                if (!codes[i].Calls(zNetViewDestroy)) continue;

                codes.Insert(i - 2, new CodeInstruction(OpCodes.Ldarg_0));
                codes.Insert(i - 1, new CodeInstruction(OpCodes.Call, pickablePreDestroyHook));
                break;
            }

            return codes.AsEnumerable();
        }

        [SuppressMessage("ReSharper", "UnusedMember.Local")]
        private static void PickablePreDestroyHook(Pickable instance)
        {
            if (!AutomaticMapPinning.IsActive()) return;

            var flora = instance.GetComponent<FloraObject>();
            if (flora != null)
                StaticMapPinning.RemovePin(flora.Cluster.Center);
        }
    }
}