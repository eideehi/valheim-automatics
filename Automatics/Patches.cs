using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ModUtils;
using TranspilerLocals = Automatics.AutomaticProcessing.TranspilerLocals;

namespace Automatics
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static void FejdStartup_Start_Postfix()
        {
            AutomaticStorage.SlotLocks.CaptureSprite();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "Awake")]
        private static void Player_Awake_Postfix(Player __instance)
        {
            if (!Objects.GetZNetView(__instance, out var zNetView)) return;

            Hooks.OnPlayerAwake?.Invoke(__instance, zNetView);

            // A character the local game spawns gets a new ZDO that it owns, while a remote
            // player's character arrives with the ZDO its own game owns. Player.m_localPlayer is
            // only assigned after Awake, so ownership is what tells the two apart here.
            if (Objects.HasValidOwnership(zNetView))
                Hooks.OnLocalPlayerAwake?.Invoke(__instance, zNetView);
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

        /// <summary>
        ///     The transpilers that raise the player update hooks. They are patched on their own,
        ///     after the patches above: when a game update changes what they anchor on, they refuse
        ///     at patch time, and the terminal and cache patches are already applied by then.
        /// </summary>
        [HarmonyPatch]
        internal static class PlayerUpdateHooks
        {
            [HarmonyTranspiler]
            [HarmonyPatch(typeof(Player), "Update")]
            private static IEnumerable<CodeInstruction> Player_Update_Transpiler(
                IEnumerable<CodeInstruction> instructions, ILGenerator generator,
                MethodBase original)
            {
                /*
                 *   this.UpdatePlacement(takeInput, Time.deltaTime);
                 * + Hooks.OnPlayerUpdate?.Invoke(this, takeInput);
                 *   this.UpdateStats();
                 */
                // takeInput and the hook position are derived, and checked, by
                // TranspilerLocals.DerivePlayerUpdateInput. Insert leaves the position on the
                // first inserted instruction, so the null test goes in front of the invocation.
                var codes = instructions.ToList();
                var site = TranspilerLocals.DerivePlayerUpdateInput(original, ToIl(codes),
                    AccessTools.Method(typeof(UnityEngine.Time), "get_deltaTime"),
                    AccessTools.Method(typeof(Player), "UpdatePlacement"));

                return new CodeMatcher(codes, generator)
                    .Start()
                    .Advance(site.HookAt)
                    .CreateLabel(out var ifNull)
                    .Insert(
                        new CodeInstruction(OpCodes.Ldarg_0),
                        ToCode(TranspilerLocals.LoadLocal(site.Local)),
                        new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.Method(typeof(Action<Player, bool>), "Invoke")))
                    .CreateLabel(out var invoke)
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
                IEnumerable<CodeInstruction> instructions, ILGenerator generator,
                MethodBase original)
            {
                /*
                 *   this.UpdateStealth(fixedDeltaTime);
                 * + Hooks.OnPlayerFixedUpdate(this, fixedDeltaTime);
                 *   if ((bool) (UnityEngine.Object) GameCamera.instance && (double) Vector3.Distance(GameCamera.instance.transform.position, this.transform.position) < 2.0)
                 */
                // fixedDeltaTime and the hook position are derived, and checked, by
                // TranspilerLocals.DerivePlayerFixedDeltaTime.
                var codes = instructions.ToList();
                var site = TranspilerLocals.DerivePlayerFixedDeltaTime(original, ToIl(codes),
                    AccessTools.Method(typeof(Player), "UpdateStealth"));

                return new CodeMatcher(codes, generator)
                    .Start()
                    .Advance(site.HookAt)
                    .CreateLabel(out var skip)
                    .Insert(
                        new CodeInstruction(OpCodes.Ldarg_0),
                        ToCode(TranspilerLocals.LoadLocal(site.Local)),
                        new CodeInstruction(OpCodes.Callvirt,
                            AccessTools.Method(typeof(Action<Player, float>), "Invoke")))
                    .CreateLabel(out var exec)
                    .Insert(
                        new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(Hooks), "get_OnPlayerFixedUpdate")),
                        new CodeInstruction(OpCodes.Dup),
                        new CodeInstruction(OpCodes.Brtrue_S, exec),
                        new CodeInstruction(OpCodes.Pop),
                        new CodeInstruction(OpCodes.Br_S, skip))
                    .InstructionEnumeration();
            }

            /// <summary>
            ///     The transpiler's instructions as opcode and operand pairs, for
            ///     <see cref="TranspilerLocals" /> to match against. Positions are shared with the
            ///     list the transpiler edits.
            /// </summary>
            private static List<TranspilerLocals.IlInstruction> ToIl(IEnumerable<CodeInstruction> codes)
            {
                return codes.Select(x => new TranspilerLocals.IlInstruction(x.opcode, x.operand))
                    .ToList();
            }

            private static CodeInstruction ToCode(TranspilerLocals.IlInstruction instruction)
            {
                return new CodeInstruction(instruction.OpCode, instruction.Operand);
            }
        }
    }
}
