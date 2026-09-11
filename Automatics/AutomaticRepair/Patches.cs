using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Automatics.AutomaticRepair
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.Interact))]
        public static IEnumerable<CodeInstruction> CraftingStation_Interact_Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            /*
             *   Player player = user as Player;
             *   ...
             *   InventoryGui.instance.Show((Container) null, 3);
             * + ItemRepair.CraftingStationInteractHook(player, this);
             *   return false;
             */
            var code = instructions.ToList();
            var playerLocal = TranspilerLocals.RequireUniqueLocal(original, typeof(Player));
            var show = AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Show));
            TranspilerLocals.RequireUniqueShow(code.Select(instruction =>
                instruction.opcode == OpCodes.Callvirt && Equals(instruction.operand, show)));

            return new CodeMatcher(code)
                .MatchStartForward(new CodeMatch(OpCodes.Callvirt,
                    show))
                .ThrowIfInvalid("CraftingStation.Interact: InventoryGui.Show was not found")
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldloc, playerLocal),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(ItemRepair),
                            nameof(ItemRepair.CraftingStationInteractHook))))
                .InstructionEnumeration();
        }
    }
}
