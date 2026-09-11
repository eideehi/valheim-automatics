using System;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using RepairLocals = Automatics.AutomaticRepair.TranspilerLocals;

namespace Automatics.AutomaticProcessing
{
    internal static partial class Program
    {
        private sealed class RepairPlayer { }

        private static void RunRepairAnchorCases()
        {
            Run("repair resolves the released Player local and unique Show anchor", ReleasedRepairAnchor);
            Run("repair follows reordered Player locals", () =>
                AssertEqual(2, RepairLocals.RequireUniqueLocal(
                    new FakeMethod(typeof(int), typeof(string), typeof(RepairPlayer)), typeof(RepairPlayer)), "Player local"));
            Run("repair refuses a missing Player local", () =>
                ExpectThrows<InvalidOperationException>(() => RepairLocals.RequireUniqueLocal(
                    new FakeMethod(typeof(int)), typeof(RepairPlayer)), "missing Player"));
            Run("repair refuses multiple Player locals", () =>
                ExpectThrows<InvalidOperationException>(() => RepairLocals.RequireUniqueLocal(
                    new FakeMethod(typeof(RepairPlayer), typeof(RepairPlayer)), typeof(RepairPlayer)), "multiple Players"));
            Run("repair refuses an unreadable body", () =>
                ExpectThrows<InvalidOperationException>(() => RepairLocals.RequireUniqueLocal(
                    FakeMethod.WithoutBody(), typeof(RepairPlayer)), "unreadable body"));
            Run("repair refuses a missing Show anchor", () =>
                ExpectThrows<InvalidOperationException>(() => RepairLocals.RequireUniqueShow(new[] { false, false }), "missing Show"));
            Run("repair refuses duplicate Show anchors", () =>
                ExpectThrows<InvalidOperationException>(() => RepairLocals.RequireUniqueShow(new[] { false, true, true }), "duplicate Show"));
            Run("repair Player decoding distinguishes the namespace", () =>
            {
                AssertEqual(typeof(RepairPlayer), LocalTypes.NamedType("", "Player"), "global Player");
                AssertEqual(typeof(OtherType), LocalTypes.NamedType("OtherMod", "Player"), "namespaced Player");
            });
        }

        private static void ReleasedRepairAnchor()
        {
            var root = Environment.GetEnvironmentVariable("VALHEIM_DIR");
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("set VALHEIM_DIR for the released Repair body");
            var decoded = DecodedMethod.Read(Path.Combine(root, "valheim_Data", "Managed", "assembly_valheim.dll"),
                "CraftingStation", "Interact");
            var player = RepairLocals.RequireUniqueLocal(decoded.Method, typeof(RepairPlayer));
            var code = ReleasedTranspilerInput("CraftingStation", "Interact");
            var show = RepairLocals.RequireUniqueShow(code.Select(i =>
                i.OpCode == OpCodes.Callvirt && Equals(i.Operand, "InventoryGui::Show")));
            // Positions observed from the decoded 1.0.7 body before changing the transpiler.
            AssertEqual(0, player, "released Player local");
            AssertEqual(28, show, "released Show anchor");
            AssertEqual(33, code.Count, "released instruction count");
            AssertPositions(new[] { 4, 14, 31 }, decoded.BranchTargets.OrderBy(i => i), "released branch targets");
            AssertPositions(new[] { 16 }, MatchEnds(code, Anchor(Op(OpCodes.Stloc_0))), "Player initialized once");
            AssertTrue(MatchesAt(code, 14, Anchor(Op(OpCodes.Ldarg_1), Op(OpCodes.Isinst), Op(OpCodes.Stloc_0))),
                "Player is initialized from user before use");
            AssertTrue(MatchesAt(code, 17, Anchor(Op(OpCodes.Ldarg_0), Op(OpCodes.Ldloc_0), Op(OpCodes.Ldc_I4_1),
                Op(OpCodes.Call, "CraftingStation::CheckUsable"), Op(OpCodes.Brfalse))), "Show follows CheckUsable");
            AssertTrue(MatchesAt(code, 22, Anchor(Op(OpCodes.Ldloc_0), Op(OpCodes.Ldarg_0),
                Op(OpCodes.Callvirt, "Player::SetCraftingStation"), Op(OpCodes.Call, "InventoryGui::get_instance"),
                Op(OpCodes.Ldnull), Op(OpCodes.Ldc_I4_3), Op(OpCodes.Callvirt, "InventoryGui::Show"),
                Op(OpCodes.Ldc_I4_0), Op(OpCodes.Ret))), "Show is in the successful interaction branch");

            var insertAt = show + 1;
            AssertEqual(29, insertAt, "repair hook goes immediately after Show");
            AssertTrue(!decoded.BranchTargets.Contains(insertAt), "no label can skip the inserted hook");
            code.InsertRange(insertAt, new[] { I(OpCodes.Ldloc, player), I(OpCodes.Ldarg_0),
                I(OpCodes.Call, "ItemRepair::CraftingStationInteractHook") });
            AssertTrue(MatchesAt(code, 28, Anchor(Op(OpCodes.Callvirt, "InventoryGui::Show"),
                Op(OpCodes.Ldloc, 0), Op(OpCodes.Ldarg_0), Op(OpCodes.Call, "ItemRepair::CraftingStationInteractHook"),
                Op(OpCodes.Ldc_I4_0), Op(OpCodes.Ret))), "injected hook receives the Player after Show");
            AssertPositions(new[] { 4, 14, 34 }, decoded.BranchTargets.Select(i => i >= insertAt ? i + 3 : i).OrderBy(i => i),
                "original labels stay on original instructions");
        }
    }
}
