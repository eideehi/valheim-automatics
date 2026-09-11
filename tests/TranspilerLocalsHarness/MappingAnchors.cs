using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using IlInstruction = Automatics.AutomaticProcessing.TranspilerLocals.IlInstruction;

namespace Automatics.AutomaticProcessing
{
    // The anchors of the Automatic Mapping transpilers (Automatics/AutomaticMapping/Patches.cs),
    // checked against the released game's own method bodies. Unlike the cases in Program.cs these
    // run no shipped code: the transpilers need Harmony and the game's types, so each anchor is
    // spelled again here, instruction for instruction, and searched the way HarmonyX 2.9.0's
    // CodeMatcher searches (from the current position, stepping forward or backward, a MatchEnd
    // stopping on the anchor's last instruction). Every search in those transpilers is followed by
    // ThrowIfInvalid, which throws only when the search found nothing and otherwise leaves the
    // instructions alone, so an anchor that matches here where it matched before means the guard
    // stays silent on this release and the patched method is the one it always was. The expected
    // positions were read from an ilspycmd listing of Valheim 1.0.7, not from this search.
    internal static partial class Program
    {
        // HarmonyX 2.9.0 (ILManipulator.NormalizeInstructions) hands a transpiler every short branch
        // in its long form, which is the form the anchors are written against.
        private static readonly Dictionary<OpCode, OpCode> LongBranchForms = new Dictionary<OpCode, OpCode>
        {
            [OpCodes.Beq_S] = OpCodes.Beq, [OpCodes.Bge_S] = OpCodes.Bge, [OpCodes.Bge_Un_S] = OpCodes.Bge_Un,
            [OpCodes.Bgt_S] = OpCodes.Bgt, [OpCodes.Bgt_Un_S] = OpCodes.Bgt_Un, [OpCodes.Ble_S] = OpCodes.Ble,
            [OpCodes.Ble_Un_S] = OpCodes.Ble_Un, [OpCodes.Blt_S] = OpCodes.Blt, [OpCodes.Blt_Un_S] = OpCodes.Blt_Un,
            [OpCodes.Bne_Un_S] = OpCodes.Bne_Un, [OpCodes.Brfalse_S] = OpCodes.Brfalse,
            [OpCodes.Brtrue_S] = OpCodes.Brtrue, [OpCodes.Br_S] = OpCodes.Br, [OpCodes.Leave_S] = OpCodes.Leave
        };

        private static void RunMappingAnchorCases()
        {
            Run("the released 1.0.7 Minimap.OnMapLeftClick stores ScreenToWorldPoint into local 0 once, at instruction 12",
                MapClickPositionAnchor);
            Run("the released 1.0.7 Minimap.UpdatePins doubles the pin size into a local twice, at instructions 143 and 249",
                PinSizeAnchor);
            Run("the released 1.0.7 Minimap.AddPin tests local 0's m_name with IsNullOrEmpty once, ending at instruction 51",
                PinNameTagAnchor);
            Run("the released 1.0.7 Pickable.SetPicked calls ZNetView.Destroy once, at instruction 58",
                PickedDestroyCallAnchor);
            Run("the released 1.0.7 Pickable.SetPicked starts that Destroy call with the ldarg.0 at instruction 56",
                PickedDestroyReceiverAnchor);
            Run("the released 1.0.7 MineRock5.DamageArea calls m_nview.Destroy() once, from instruction 207",
                MineRockDestroyAnchor);
        }

        private static void MapClickPositionAnchor()
        {
            var code = ReleasedTranspilerInput("Minimap", "OnMapLeftClick");
            var anchor = Anchor(Op(OpCodes.Call, "Minimap::ScreenToWorldPoint"), Op(OpCodes.Stloc_0));

            AssertEqual(12, Match(code, 0, 1, true, anchor), "1.0.7 Minimap.OnMapLeftClick: MatchEndForward from the start");
            AssertPositions(new[] { 12 }, MatchEnds(code, anchor), "1.0.7 Minimap.OnMapLeftClick: every match");
        }

        private static void PinSizeAnchor()
        {
            var code = ReleasedTranspilerInput("Minimap", "UpdatePins");
            var anchor = Anchor(Op(OpCodes.Ldloc_3), Op(OpCodes.Ldc_R4, 2f), Op(OpCodes.Mul), Op(OpCodes.Stloc_S));

            // The transpiler searches for this anchor only after the store of the pin local (6).
            var pinStore = Match(code, 0, 1, true, Anchor(Op(OpCodes.Stloc_S, (byte)6)));
            AssertEqual(54, pinStore, "1.0.7 Minimap.UpdatePins: the first store into the pin local");
            AssertPositions(new[] { 143, 249 }, MatchEnds(code, anchor), "1.0.7 Minimap.UpdatePins: every match");
            AssertEqual(143, Match(code, pinStore + 1, 1, true, anchor),
                "1.0.7 Minimap.UpdatePins: MatchEndForward after the pin local is stored");
            AssertEqual((object)(byte)14, code[143].Operand, "1.0.7 Minimap.UpdatePins: the local the pin size is stored into");
            AssertEqual((object)(byte)15, code[249].Operand, "1.0.7 Minimap.UpdatePins: the local the animated size is stored into");
        }

        private static void PinNameTagAnchor()
        {
            var code = ReleasedTranspilerInput("Minimap", "AddPin");
            var anchor = Anchor(Op(OpCodes.Ldloc_0), Op(OpCodes.Ldfld, "PinData::m_name"),
                Op(OpCodes.Call, "String::IsNullOrEmpty"), Op(OpCodes.Brtrue));

            AssertEqual(51, Match(code, 0, 1, true, anchor), "1.0.7 Minimap.AddPin: MatchEndForward from the start");
            AssertPositions(new[] { 51 }, MatchEnds(code, anchor), "1.0.7 Minimap.AddPin: every match");
        }

        private static void PickedDestroyCallAnchor()
        {
            var code = ReleasedTranspilerInput("Pickable", "SetPicked");
            var anchor = Anchor(Op(OpCodes.Callvirt, "ZNetView::Destroy"));

            AssertEqual(58, Match(code, code.Count - 1, -1, false, anchor), "1.0.7 Pickable.SetPicked: MatchStartBackwards from the end");
            AssertPositions(new[] { 58 }, MatchEnds(code, anchor), "1.0.7 Pickable.SetPicked: every match");
        }

        private static void PickedDestroyReceiverAnchor()
        {
            var code = ReleasedTranspilerInput("Pickable", "SetPicked");
            var destroy = Match(code, code.Count - 1, -1, false, Anchor(Op(OpCodes.Callvirt, "ZNetView::Destroy")));

            AssertEqual(56, Match(code, destroy, -1, false, Anchor(Op(OpCodes.Ldarg_0))),
                "1.0.7 Pickable.SetPicked: MatchStartBackwards from the Destroy call");
            AssertTrue(Equals(code[57].Operand, "Pickable::m_nview"),
                $"1.0.7 Pickable.SetPicked: the ldarg.0 found must load the receiver of this.m_nview.Destroy(), found {code[57]}.");
        }

        private static void MineRockDestroyAnchor()
        {
            var code = ReleasedTranspilerInput("MineRock5", "DamageArea");
            var anchor = Anchor(Op(OpCodes.Ldarg_0), Op(OpCodes.Ldfld, "MineRock5::m_nview"),
                Op(OpCodes.Callvirt, "ZNetView::Destroy"));

            AssertEqual(207, Match(code, code.Count - 1, -1, false, anchor), "1.0.7 MineRock5.DamageArea: MatchStartBackwards from the end");
            AssertPositions(new[] { 209 }, MatchEnds(code, anchor), "1.0.7 MineRock5.DamageArea: every match (its last instruction)");
        }

        // --- Searching as CodeMatcher does -------------------------------------------------

        /// <summary>The instructions of a released game method as a transpiler is handed them.</summary>
        private static List<IlInstruction> ReleasedTranspilerInput(string typeName, string methodName)
        {
            var valheim = Environment.GetEnvironmentVariable("VALHEIM_DIR");
            var assembly = string.IsNullOrEmpty(valheim)
                ? null
                : Path.Combine(valheim, "valheim_Data", "Managed", "assembly_valheim.dll");
            if (assembly == null || !File.Exists(assembly))
                throw new InvalidOperationException(
                    "set VALHEIM_DIR to the Valheim install scripts/build.sh builds against; this case reads " +
                    $"the released game's method bodies from valheim_Data/Managed/assembly_valheim.dll (VALHEIM_DIR={valheim ?? "<unset>"})");

            return DecodedMethod.Read(assembly, typeName, methodName).Code
                .Select(instruction => LongBranchForms.TryGetValue(instruction.OpCode, out var longForm)
                    ? new IlInstruction(longForm, instruction.Operand)
                    : instruction)
                .ToList();
        }

        /// <summary>An opcode, and the operand it must have; a null operand matches any operand.</summary>
        private static (OpCode OpCode, object Operand) Op(OpCode opcode, object operand = null)
        {
            return (opcode, operand);
        }

        private static (OpCode OpCode, object Operand)[] Anchor(params (OpCode OpCode, object Operand)[] instructions)
        {
            return instructions;
        }

        /// <summary>
        ///     CodeMatcher.Match: from <paramref name="from" /> (never before 0), try the anchor at each
        ///     position, stepping by <paramref name="direction" />, and stop at the first position where
        ///     it matches - on its last instruction when <paramref name="useEnd" />. -1 is the invalid
        ///     state that ThrowIfInvalid reports.
        /// </summary>
        private static int Match(IList<IlInstruction> code, int from, int direction, bool useEnd,
            (OpCode OpCode, object Operand)[] anchor)
        {
            for (var pos = Math.Max(0, from); pos >= 0 && pos < code.Count; pos += direction)
                if (MatchesAt(code, pos, anchor))
                    return useEnd ? pos + anchor.Length - 1 : pos;

            return -1;
        }

        /// <summary>The last instruction of every match in the method, in order.</summary>
        private static List<int> MatchEnds(IList<IlInstruction> code, (OpCode OpCode, object Operand)[] anchor)
        {
            return Enumerable.Range(0, code.Count)
                .Where(pos => MatchesAt(code, pos, anchor))
                .Select(pos => pos + anchor.Length - 1)
                .ToList();
        }

        private static bool MatchesAt(IList<IlInstruction> code, int start, (OpCode OpCode, object Operand)[] anchor)
        {
            for (var k = 0; k < anchor.Length; k++)
            {
                if (start + k >= code.Count)
                    return false;
                var instruction = code[start + k];
                if (instruction.OpCode != anchor[k].OpCode)
                    return false;
                if (anchor[k].Operand != null && !Equals(anchor[k].Operand, instruction.Operand))
                    return false;
            }

            return true;
        }

        private static void AssertPositions(IEnumerable<int> expected, IEnumerable<int> actual, string what)
        {
            var wanted = expected.ToList();
            var found = actual.ToList();
            if (!wanted.SequenceEqual(found))
                throw new InvalidOperationException(
                    $"{what}: expected [{string.Join(", ", wanted)}], got [{string.Join(", ", found)}].");
        }
    }
}
