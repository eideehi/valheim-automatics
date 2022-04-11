using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using Automatics.ModUtils;
using HarmonyLib;

namespace Automatics
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Player), "Update")]
        private static void PlayerUpdatePostfix(Player __instance, ZNetView ___m_nview)
        {
            if (!___m_nview.IsValid() || !___m_nview.IsOwner()) return;

            var takeInput = Reflection.InvokeMethod<bool>(__instance, "TakeInput");
            Automatics.OnPlayerUpdate?.Invoke(__instance, takeInput);
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static IEnumerable<CodeInstruction> TerminalInitTerminalTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var Hook = AccessTools.Method(typeof(Patches), nameof(TerminalInitTerminalHook));

            var codes = new List<CodeInstruction>(instructions);

            var index = codes.FindLastIndex(x => x.opcode == OpCodes.Ret);
            if (index != -1)
                codes.Insert(index - 1, new CodeInstruction(OpCodes.Call, Hook));

            return codes;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Container), "Awake")]
        private static void ContainerAwakePostfix(Container __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<ContainerCache>();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Pickable), "Awake")]
        private static void PickableAwakePostfix(Pickable __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<PickableCache>();
        }

        private static void TerminalInitTerminalHook() => Automatics.OnInitTerminal?.Invoke();
    }
}