using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using ModUtils;

namespace Automatics
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
        private static void Localization_SetupLanguage_Postfix(string language)
        {
            foreach (var directory in Automatics.GetAllResourcePath("Languages"))
                new TranslationsLoader(Automatics.L10N).LoadTranslations(directory, language);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "Awake")]
        private static void Player_Awake_Postfix(Player __instance)
        {
            if (Objects.GetZNetView(__instance, out var zNetView))
                Hooks.OnPlayerAwake?.Invoke(__instance, zNetView);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Player), "Update")]
        private static IEnumerable<CodeInstruction> Player_Update_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            /*
             *     this.UpdatePlacement(input, Time.deltaTime);
             * +   Hooks.OnPlayerUpdate?.Invoke(this, input);
             *   }
             */
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(new CodeMatch(OpCodes.Call,
                    AccessTools.Method(typeof(Player), "UpdatePlacement")))
                .Advance(1)
                .CreateLabel(out var ifNull)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    new CodeInstruction(OpCodes.Callvirt,
                        AccessTools.Method(typeof(Action<Player, bool>), "Invoke")))
                .CreateLabel(out var invoke)
                .MatchStartBackwards(new CodeMatch(OpCodes.Ldarg_0))
                .Insert(
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(Hooks), "get_OnPlayerUpdate")),
                    new CodeInstruction(OpCodes.Dup),
                    new CodeInstruction(OpCodes.Brtrue_S, invoke),
                    new CodeInstruction(OpCodes.Pop),
                    new CodeInstruction(OpCodes.Br_S, ifNull))
                .InstructionEnumeration();
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Player), "FixedUpdate")]
        private static IEnumerable<CodeInstruction> Player_FixedUpdate_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            /*
             *   this.UpdateStealth(fixedDeltaTime);
             * + Hooks.OnPlayerFixedUpdate(this, fixedDeltaTime);
             *   if ((bool) (UnityEngine.Object) GameCamera.instance && (double) Vector3.Distance(GameCamera.instance.transform.position, this.transform.position) < 2.0)
             */
            return new CodeMatcher(instructions, generator)
                .MatchStartForward(new CodeMatch(OpCodes.Call,
                    AccessTools.Method(typeof(Player), "UpdateStealth")))
                .Advance(1)
                .CreateLabel(out var skip)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Callvirt,
                        AccessTools.Method(typeof(Action<Player, float>), "Invoke")))
                .CreateLabel(out var exec)
                .MatchStartBackwards(new CodeMatch(OpCodes.Ldarg_0))
                .Insert(
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(Hooks), "get_OnPlayerFixedUpdate")),
                    new CodeInstruction(OpCodes.Dup),
                    new CodeInstruction(OpCodes.Brtrue_S, exec),
                    new CodeInstruction(OpCodes.Pop),
                    new CodeInstruction(OpCodes.Br_S, skip))
                .InstructionEnumeration();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static void Terminal_InitTerminal_Prefix(out bool __state,
            bool ___m_terminalInitialized)
        {
            __state = ___m_terminalInitialized;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static void Terminal_InitTerminal_Postfix(bool __state)
        {
            if (!__state)
                Hooks.OnInitTerminal?.Invoke();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Container), "Awake")]
        private static void Container_Awake_Postfix(Container __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<ContainerCache>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pickable), "Awake")]
        private static void Pickable_Awake_Postfix(Pickable __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<PickableCache>();
        }
    }
}