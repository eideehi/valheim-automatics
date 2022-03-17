using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Automatics.AutomaticMapPinning
{
    [SuppressMessage( "ReSharper", "InconsistentNaming" )]
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
                Map.RemovePin(flora.Cluster.Center);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(TeleportWorld), "Awake")]
        private static void TeleportWorldAwakePostfix(TeleportWorld __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() == null) return;

            var portal = __instance.GetComponent<WearNTear>();
            if (portal != null)
                portal.m_onDestroyed += () => { Map.RemovePin(__instance.transform.position); };
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Ship), "Awake")]
        private static void ShipAwakePostfix(Ship __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<ShipCache>();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Minimap), "Start")]
        private static void MinimapStartPostfix(Minimap __instance)
        {
            Map.Initialize();
        }
    }
}