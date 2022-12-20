using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
            IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)
                .MatchStartForward(new CodeMatch(OpCodes.Callvirt,
                    AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Show))))
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(ItemRepair),
                            nameof(ItemRepair.CraftingStationInteractHook))))
                .InstructionEnumeration();
        }
    }
}