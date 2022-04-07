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
        [HarmonyTranspiler, HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.Interact))]
        public static IEnumerable<CodeInstruction> Interact(IEnumerable<CodeInstruction> instructions)
        {
            var Show = AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Show));
            var Hook = AccessTools.Method(typeof(RepairItems), nameof(RepairItems.CraftingStationInteractHook));

            var codes = new List<CodeInstruction>(instructions);

            var index = codes.FindIndex(x => x.Calls(Show));
            codes.InsertRange(index, new[]
            {
                new CodeInstruction(OpCodes.Ldloc_0),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, Hook),
            });

            return codes;
        }
    }
}