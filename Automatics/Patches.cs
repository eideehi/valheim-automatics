using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;

namespace Automatics
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Game), "Awake")]
        private static void GameAwakePostfix() => Automatics.OnGameAwake?.Invoke();

        [HarmonyTranspiler, HarmonyPatch(typeof(Player), "Update")]
        private static IEnumerable<CodeInstruction> PlayerUpdateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var UpdatePlacement = AccessTools.Method(typeof(Player), "UpdatePlacement");
            var Hook = AccessTools.Method(typeof(Patches), nameof(PlayerUpdateHook));

            var codes = new List<CodeInstruction>(instructions);

            var index = codes.FindIndex(x => x.Calls(UpdatePlacement));
            if (index != -1)
            {
                codes.InsertRange(index, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Call, Hook),
                });
            }

            return codes;
        }

        [HarmonyTranspiler, HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static IEnumerable<CodeInstruction> TerminalInitTerminalTranspiler(
            IEnumerable<CodeInstruction> instructions)
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

        private static void PlayerUpdateHook(Player player, bool takeInput) =>
            Automatics.OnPlayerUpdate?.Invoke(player, takeInput);

        private static void TerminalInitTerminalHook() => Automatics.OnInitTerminal?.Invoke();
    }
}