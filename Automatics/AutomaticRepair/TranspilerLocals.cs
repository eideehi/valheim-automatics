using System;
using System.Collections.Generic;
using System.Reflection;

namespace Automatics.AutomaticRepair
{
    // Pure validation shared with the offline harness. An unfamiliar game body must fail at
    // patch time rather than load an unrelated local or silently omit the repair hook.
    internal static class TranspilerLocals
    {
        internal static int RequireUniqueLocal(MethodBase original, Type localType)
        {
            var body = original?.GetMethodBody();
            if (body == null)
                throw new InvalidOperationException("CraftingStation.Interact: no readable method body");
            if (localType == null) throw new ArgumentNullException(nameof(localType));

            var index = -1;
            foreach (var local in body.LocalVariables)
            {
                if (local.LocalType != localType) continue;
                if (index >= 0)
                    throw new InvalidOperationException("CraftingStation.Interact: multiple Player locals");
                index = local.LocalIndex;
            }

            if (index < 0)
                throw new InvalidOperationException("CraftingStation.Interact: no Player local");
            return index;
        }

        internal static int RequireUniqueShow(IEnumerable<bool> showCalls)
        {
            var found = -1;
            var position = 0;
            foreach (var isShow in showCalls)
            {
                if (isShow)
                {
                    if (found >= 0)
                        throw new InvalidOperationException("CraftingStation.Interact: multiple InventoryGui.Show calls");
                    found = position;
                }
                position++;
            }

            if (found < 0)
                throw new InvalidOperationException("CraftingStation.Interact: no InventoryGui.Show call");
            return found;
        }
    }
}
