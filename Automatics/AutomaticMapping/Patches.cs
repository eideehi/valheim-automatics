using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using Automatics.ModUtils;
using HarmonyLib;

namespace Automatics.AutomaticMapping
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Minimap), "UpdateMap")]
        private static void MinimapUpdateMapPostfix(Player player, float dt, bool takeInput)
        {
            Core.OnUpdateMap(player, dt, takeInput);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Minimap), "RemovePin", typeof(Minimap.PinData))]
        private static void MinimapRemovePinPostfix(Minimap.PinData pin)
        {
            DynamicPinning.RemoveDynamicPin(pin);
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
            if (!Core.IsActive()) return;

            var flora = instance.GetComponent<FloraObject>();
            if (flora != null)
                Map.RemovePin(flora.Network.Center);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(TeleportWorld), "Awake")]
        private static void TeleportWorldAwakePostfix(TeleportWorld __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() == null) return;

            var portal = __instance.GetComponent<WearNTear>();
            if (portal == null) return;

            portal.m_onDestroyed += () =>
            {
                if (Core.IsActive())
                    Map.RemovePin(__instance.transform.position);
            };
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Destructible), "Awake")]
        private static void DestructibleAwakePostfix(Destructible __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() == null) return;
            if (Core.IsMineralDeposit(Obj.GetName(__instance), out _)) return;

            __instance.m_onDestroyed += () =>
            {
                if (Core.IsActive())
                    Map.RemovePin(__instance.transform.position);
            };
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