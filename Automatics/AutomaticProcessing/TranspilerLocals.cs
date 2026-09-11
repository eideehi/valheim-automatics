using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Automatics.AutomaticProcessing
{
    /// <summary>
    ///     Resolves the IL locals the Automatic Processing transpilers read and write, and the
    ///     positions their injected calls go to, from the instructions they anchor on. This is for
    ///     locals whose declared type is shared with other locals of the same method and therefore
    ///     cannot be resolved by type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Local slot numbers are not part of a method's contract and shift whenever the game
    ///         adds or removes a local. Every resolution here fails loudly at patch time instead
    ///         of guessing: the anchor must match exactly once in the whole method, the derived
    ///         index must be a declared local, and that local must have the expected declared
    ///         type. Skipping the patch, or reading a neighbouring local, would leave the game
    ///         running IL that nobody checked.
    ///     </para>
    ///     <para>
    ///         The class depends on nothing but System.Reflection so that
    ///         tests/TranspilerLocalsHarness compiles this very file and executes the derivation
    ///         the game runs, on instruction lists shaped like the game's and on the released
    ///         game's own method bodies. Patches.cs only converts between Harmony's
    ///         CodeInstruction and <see cref="IlInstruction" />, and emits the injected calls at
    ///         the positions derived here.
    ///     </para>
    ///     <para>
    ///         A local slot may be spelled as a short-form opcode (ldloc.0-3, stloc.0-3) or as a
    ///         literal operand only inside the five opcode helpers <see cref="IsLocalLoad" />,
    ///         <see cref="IsLocalStore" />, <see cref="LocalIndexOf" />, <see cref="LoadLocal" />
    ///         and <see cref="StoreLocal" />. scripts/test-transpiler-static-invariants.sh
    ///         exempts them by name and rejects those forms everywhere else in the transpiler
    ///         files, so renaming or adding a helper is a deliberate change to that list.
    ///     </para>
    /// </remarks>
    internal static class TranspilerLocals
    {
        // Positions inside the loop head matched by SmelterLoopHead.
        private const int SmelterAccumulatorLoad = 0;
        private const int SmelterAccumulatorStore = 3;
        private const int SmelterFuelStore = 6;
        private const int SmelterQueuedOreStore = 9;

        // Position of the fuel local inside CookingStationFuelRead and CookingStationFuelWrite.
        private const int CookingStationFuelStore = 2;
        private const int CookingStationFuelLoad = 1;

        internal static readonly Func<IlInstruction, bool> AnyLocalLoad = x => IsLocalLoad(x.OpCode);
        internal static readonly Func<IlInstruction, bool> AnyLocalStore = x => IsLocalStore(x.OpCode);

        /// <summary>
        ///     Derives the locals of the <c>while (accumulator &gt;= 1f)</c> loop head in
        ///     Smelter.UpdateSmelter, <c>accumulator -= 1f; fuel = GetFuel(); queuedOre =
        ///     GetQueuedOre();</c>, and the positions right after the fuel and queuedOre stores
        ///     where QuickRefuel and QuickCraft go.
        /// </summary>
        /// <remarks>
        ///     accumulator, fuel and several scratch values are all float32 locals, so only
        ///     queuedOre could be resolved by its type. The loop head must occur exactly once in the
        ///     method: GetFuel and GetQueuedOre are called again after the loop, so the first match
        ///     from some position could anchor on the wrong block. The accumulator must be read and
        ///     written through the same local, each local must be declared with the expected type,
        ///     and queuedOre must also be the method's only string local.
        /// </remarks>
        internal static SmelterLoopLocals DeriveSmelterLoop(MethodBase original,
            IList<IlInstruction> code, MethodInfo getFuel, MethodInfo getQueuedOre)
        {
            const string where = "Smelter.UpdateSmelter";
            var body = original?.GetMethodBody() ?? throw new InvalidOperationException(
                $"{where}: method body is unavailable, cannot check the declared local types");
            var localTypes = DeclaredLocalTypes(body.LocalVariables, where);

            var head = FindSingle(code, SmelterLoopHead(getFuel, getQueuedOre),
                $"{where}: the loop head `accumulator -= 1f; fuel = GetFuel(); queuedOre = GetQueuedOre();`");
            var accumulator = RequireLocal(
                RequireSameLocal(code, head + SmelterAccumulatorLoad, head + SmelterAccumulatorStore,
                    $"{where}: accumulator"),
                localTypes, typeof(float), $"{where}: accumulator");
            var fuel = RequireLocal(LocalAt(code, head + SmelterFuelStore),
                localTypes, typeof(float), $"{where}: fuel");
            var queuedOre = RequireLocal(LocalAt(code, head + SmelterQueuedOreStore),
                localTypes, typeof(string), $"{where}: queuedOre");
            var onlyStringLocal = SingleLocalOfType(localTypes, typeof(string), where);
            if (queuedOre != onlyStringLocal)
                throw new InvalidOperationException(
                    $"{where}: the loop head stores GetQueuedOre into local {queuedOre}, " +
                    $"but the method's only string local is {onlyStringLocal}");

            return new SmelterLoopLocals(accumulator, fuel, queuedOre,
                head + SmelterFuelStore + 1, head + SmelterQueuedOreStore + 1);
        }

        /// <summary>
        ///     Derives the fuel local of CookingStation.UpdateFuel from <c>fuel = GetFuel();</c> and
        ///     <c>SetFuel(fuel);</c>, and the position where the Refuel call goes.
        /// </summary>
        /// <remarks>
        ///     Both locals of UpdateFuel are float32 (the per-frame burn and the fuel value), so the
        ///     fuel local cannot be resolved by type. It is read from the store that follows GetFuel;
        ///     the load that feeds SetFuel must refer to the same local, each of the two anchors must
        ///     occur exactly once in the method, and the local must be declared as float.
        /// </remarks>
        internal static OvenFuelLocal DeriveOvenFuel(MethodBase original, IList<IlInstruction> code,
            MethodInfo getFuel, MethodInfo setFuel)
        {
            const string where = "CookingStation.UpdateFuel";
            var body = original?.GetMethodBody() ?? throw new InvalidOperationException(
                $"{where}: method body is unavailable, cannot check the declared local types");
            var localTypes = DeclaredLocalTypes(body.LocalVariables, where);

            var read = FindSingle(code, CookingStationFuelRead(getFuel),
                $"{where}: the GetFuel call storing into the fuel local");
            var write = FindSingle(code, CookingStationFuelWrite(setFuel),
                $"{where}: the SetFuel call taking the fuel local");
            var fuel = RequireLocal(
                RequireSameLocal(code, read + CookingStationFuelStore, write + CookingStationFuelLoad,
                    $"{where}: fuel"),
                localTypes, typeof(float), $"{where}: fuel");

            // The Refuel call goes right behind the ldarg.0 that starts SetFuel's arguments, not in
            // front of it. That ldarg.0 is where the jump over the `fuel = 0` clamp lands (a branch
            // target in the 1.0.7 IL), and every jump to it would skip an instruction inserted in
            // front of it, so the unclamped path would never refuel. Behind it, that ldarg.0 becomes
            // Refuel's first argument and the injected block ends with a fresh ldarg.0 for SetFuel,
            // which is the instruction stream this method was patched with before (f1e7d90).
            return new OvenFuelLocal(fuel, write + 1);
        }

        /// <summary>
        ///     Inserts every block at the position it was derived for. The blocks are applied from
        ///     the back so that no insertion shifts a position another one was derived against, in
        ///     whatever order they are passed.
        /// </summary>
        internal static void InsertBackToFront<T>(List<T> code, params Insertion<T>[] insertions)
        {
            if (code == null)
                throw new ArgumentNullException(nameof(code));
            if (insertions == null)
                throw new ArgumentNullException(nameof(insertions));

            var ordered = (Insertion<T>[])insertions.Clone();
            Array.Sort(ordered, (x, y) => y.At.CompareTo(x.At));
            for (var i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].Instructions == null)
                    throw new ArgumentException("an insertion has no instructions", nameof(insertions));
                if (ordered[i].At < 0 || ordered[i].At > code.Count)
                    throw new ArgumentOutOfRangeException(nameof(insertions), ordered[i].At,
                        $"not a position in a method of {code.Count} instructions");
                if (i > 0 && ordered[i].At == ordered[i - 1].At)
                    throw new ArgumentException(
                        $"two insertions at position {ordered[i].At} would have no defined order",
                        nameof(insertions));
            }

            foreach (var insertion in ordered)
                code.InsertRange(insertion.At, insertion.Instructions);
        }

        internal static bool IsLocalLoad(OpCode opcode)
        {
            return opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1 ||
                   opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3 ||
                   opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc;
        }

        internal static bool IsLocalStore(OpCode opcode)
        {
            return opcode == OpCodes.Stloc_0 || opcode == OpCodes.Stloc_1 ||
                   opcode == OpCodes.Stloc_2 || opcode == OpCodes.Stloc_3 ||
                   opcode == OpCodes.Stloc_S || opcode == OpCodes.Stloc;
        }

        /// <summary>
        ///     Returns the index of the local that a local load or store refers to.
        /// </summary>
        /// <remarks>
        ///     The short forms carry the index in the opcode. The other forms carry it in the
        ///     operand, which may be a <see cref="LocalBuilder" />, a
        ///     <see cref="LocalVariableInfo" /> or an integer, depending on who produced the
        ///     instruction. Any other operand, a negative index, or an opcode that is not a
        ///     local load or store is refused rather than read as some local.
        /// </remarks>
        internal static int LocalIndexOf(OpCode opcode, object operand)
        {
            if (opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Stloc_0) return 0;
            if (opcode == OpCodes.Ldloc_1 || opcode == OpCodes.Stloc_1) return 1;
            if (opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Stloc_2) return 2;
            if (opcode == OpCodes.Ldloc_3 || opcode == OpCodes.Stloc_3) return 3;

            if (!IsLocalLoad(opcode) && !IsLocalStore(opcode))
                throw new InvalidOperationException(
                    $"{opcode.Name} does not load or store a local");

            int index;
            if (operand is LocalBuilder builder)
                index = builder.LocalIndex;
            else if (operand is LocalVariableInfo variable)
                index = variable.LocalIndex;
            else if (operand is byte asByte)
                index = asByte;
            else if (operand is short asShort)
                index = asShort;
            else if (operand is int asInt)
                index = asInt;
            else
                throw new InvalidOperationException(
                    $"{opcode.Name} has an operand of unknown form " +
                    $"({(operand == null ? "null" : operand.GetType().FullName)}), " +
                    "cannot tell which local it refers to");

            if (index < 0)
                throw new InvalidOperationException(
                    $"{opcode.Name} refers to a negative local index ({index})");

            return index;
        }

        /// <summary>
        ///     Returns the instruction that loads local <paramref name="index" />: the short form
        ///     for 0 to 3, the _S form with an int operand up to 255, the long form above that.
        /// </summary>
        internal static IlInstruction LoadLocal(int index)
        {
            switch (index)
            {
                case 0: return new IlInstruction(OpCodes.Ldloc_0, null);
                case 1: return new IlInstruction(OpCodes.Ldloc_1, null);
                case 2: return new IlInstruction(OpCodes.Ldloc_2, null);
                case 3: return new IlInstruction(OpCodes.Ldloc_3, null);
            }

            if (index < 0 || index > 65534)
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "not an index an IL local can have");

            return index <= byte.MaxValue
                ? new IlInstruction(OpCodes.Ldloc_S, index)
                : new IlInstruction(OpCodes.Ldloc, index);
        }

        /// <summary>
        ///     Returns the instruction that stores into local <paramref name="index" />, in the same
        ///     forms as <see cref="LoadLocal" />.
        /// </summary>
        internal static IlInstruction StoreLocal(int index)
        {
            switch (index)
            {
                case 0: return new IlInstruction(OpCodes.Stloc_0, null);
                case 1: return new IlInstruction(OpCodes.Stloc_1, null);
                case 2: return new IlInstruction(OpCodes.Stloc_2, null);
                case 3: return new IlInstruction(OpCodes.Stloc_3, null);
            }

            if (index < 0 || index > 65534)
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "not an index an IL local can have");

            return index <= byte.MaxValue
                ? new IlInstruction(OpCodes.Stloc_S, index)
                : new IlInstruction(OpCodes.Stloc, index);
        }

        /// <summary>Matches an instruction with <paramref name="opcode" />, whatever its operand.</summary>
        internal static Func<IlInstruction, bool> Is(OpCode opcode)
        {
            return x => x.OpCode == opcode;
        }

        /// <summary>Matches an instruction with <paramref name="opcode" /> and an equal operand.</summary>
        /// <remarks>
        ///     A null operand is refused: it is what a reflection lookup returns for a method the
        ///     game renamed or removed, and a pattern built from it would describe nothing.
        /// </remarks>
        internal static Func<IlInstruction, bool> Is(OpCode opcode, object operand)
        {
            if (operand == null)
                throw new ArgumentNullException(nameof(operand),
                    $"cannot match {opcode.Name} against a missing operand");

            return x => x.OpCode == opcode && operand.Equals(x.Operand);
        }

        /// <summary>
        ///     The ten consecutive instructions of <c>accumulator -= 1f; fuel = GetFuel();
        ///     queuedOre = GetQueuedOre();</c>.
        /// </summary>
        private static Func<IlInstruction, bool>[] SmelterLoopHead(MethodInfo getFuel,
            MethodInfo getQueuedOre)
        {
            return new[]
            {
                AnyLocalLoad, Is(OpCodes.Ldc_R4, 1f), Is(OpCodes.Sub), AnyLocalStore,
                Is(OpCodes.Ldarg_0), Is(OpCodes.Call, getFuel), AnyLocalStore,
                Is(OpCodes.Ldarg_0), Is(OpCodes.Call, getQueuedOre), AnyLocalStore
            };
        }

        /// <summary><c>fuel = this.GetFuel();</c></summary>
        private static Func<IlInstruction, bool>[] CookingStationFuelRead(MethodInfo getFuel)
        {
            return new[] { Is(OpCodes.Ldarg_0), Is(OpCodes.Call, getFuel), AnyLocalStore };
        }

        /// <summary><c>this.SetFuel(fuel);</c></summary>
        private static Func<IlInstruction, bool>[] CookingStationFuelWrite(MethodInfo setFuel)
        {
            return new[] { Is(OpCodes.Ldarg_0), AnyLocalLoad, Is(OpCodes.Call, setFuel) };
        }

        /// <summary>
        ///     Returns the position of the only place in <paramref name="code" /> where
        ///     <paramref name="pattern" /> matches consecutive instructions.
        /// </summary>
        /// <remarks>
        ///     The whole method is searched, and anything but exactly one match is refused. Taking
        ///     the first match from some position would silently anchor on a similar block
        ///     elsewhere in the method.
        /// </remarks>
        internal static int FindSingle(IList<IlInstruction> code,
            IList<Func<IlInstruction, bool>> pattern, string what)
        {
            if (code == null)
                throw new ArgumentNullException(nameof(code));
            if (pattern == null || pattern.Count == 0 || pattern.Contains(null))
                throw new ArgumentException($"{what}: the anchor pattern is empty or incomplete",
                    nameof(pattern));

            var start = -1;
            var count = 0;
            for (var i = 0; i + pattern.Count <= code.Count; i++)
            {
                if (!MatchesAt(code, i, pattern)) continue;
                if (count == 0) start = i;
                count++;
            }

            if (count != 1)
                throw new InvalidOperationException(
                    $"{what}: expected the anchor to match exactly once in the method, " +
                    $"found {count} matches");

            return start;
        }

        private static bool MatchesAt(IList<IlInstruction> code, int start,
            IList<Func<IlInstruction, bool>> pattern)
        {
            for (var k = 0; k < pattern.Count; k++)
                if (!pattern[k](code[start + k]))
                    return false;
            return true;
        }

        /// <summary>Returns the local that the instruction at <paramref name="position" /> refers to.</summary>
        internal static int LocalAt(IList<IlInstruction> code, int position)
        {
            var instruction = code[position];
            return LocalIndexOf(instruction.OpCode, instruction.Operand);
        }

        /// <summary>
        ///     Returns the local that both instructions refer to, and refuses when they refer to
        ///     different locals.
        /// </summary>
        internal static int RequireSameLocal(IList<IlInstruction> code, int first, int second,
            string what)
        {
            var firstLocal = LocalAt(code, first);
            var secondLocal = LocalAt(code, second);
            if (firstLocal != secondLocal)
                throw new InvalidOperationException(
                    $"{what}: the anchor refers to local {firstLocal} and to local {secondLocal} " +
                    "where it must refer to the same local");

            return firstLocal;
        }

        /// <summary>
        ///     Returns <paramref name="index" /> when it is a local the method declares with
        ///     <paramref name="expectedType" />, and refuses it otherwise.
        /// </summary>
        internal static int RequireLocal(int index, IList<Type> localTypes, Type expectedType,
            string what)
        {
            if (localTypes == null)
                throw new ArgumentNullException(nameof(localTypes));
            if (index < 0 || index >= localTypes.Count)
                throw new InvalidOperationException(
                    $"{what}: local {index} is not declared by the method " +
                    $"({localTypes.Count} locals are)");
            if (localTypes[index] != expectedType)
                throw new InvalidOperationException(
                    $"{what}: local {index} is declared as " +
                    $"{(localTypes[index] == null ? "<unknown>" : localTypes[index].Name)}, " +
                    $"expected {expectedType.Name}");

            return index;
        }

        /// <summary>
        ///     Returns the only local declared with <paramref name="type" />, and refuses when there
        ///     is none or more than one.
        /// </summary>
        internal static int SingleLocalOfType(IList<Type> localTypes, Type type, string what)
        {
            if (localTypes == null)
                throw new ArgumentNullException(nameof(localTypes));

            var found = -1;
            var count = 0;
            for (var i = 0; i < localTypes.Count; i++)
            {
                if (localTypes[i] != type) continue;
                if (count == 0) found = i;
                count++;
            }

            if (count != 1)
                throw new InvalidOperationException(
                    $"{what}: expected exactly one {type.Name} local, found {count}");

            return found;
        }

        /// <summary>
        ///     Returns the declared type of every local, indexed by the local's index.
        /// </summary>
        /// <remarks>
        ///     Refuses a list that does not number its locals 0 to n-1 exactly once each, because
        ///     <see cref="RequireLocal" /> would then compare against the wrong declaration.
        /// </remarks>
        internal static IList<Type> DeclaredLocalTypes(IList<LocalVariableInfo> locals, string what)
        {
            if (locals == null)
                throw new InvalidOperationException(
                    $"{what}: the method's local variables are unavailable");

            var types = new Type[locals.Count];
            var seen = new bool[locals.Count];
            foreach (var local in locals)
            {
                var index = local == null ? -1 : local.LocalIndex;
                if (index < 0 || index >= types.Length || seen[index])
                    throw new InvalidOperationException(
                        $"{what}: the declared locals are not numbered 0 to {types.Length - 1} " +
                        "exactly once each");

                seen[index] = true;
                types[index] = local.LocalType;
            }

            return types;
        }

        /// <summary>An IL instruction as an opcode and its operand, independent of Harmony.</summary>
        internal readonly struct IlInstruction
        {
            public readonly OpCode OpCode;
            public readonly object Operand;

            public IlInstruction(OpCode opCode, object operand)
            {
                OpCode = opCode;
                Operand = operand;
            }

            public override string ToString()
            {
                return Operand == null ? OpCode.Name : $"{OpCode.Name} {Operand}";
            }
        }

        /// <summary>A block of instructions to insert in front of position <see cref="At" />.</summary>
        internal readonly struct Insertion<T>
        {
            public readonly int At;
            public readonly IList<T> Instructions;

            public Insertion(int at, IList<T> instructions)
            {
                At = at;
                Instructions = instructions;
            }
        }

        /// <summary>The locals of the Smelter.UpdateSmelter loop head and where the injected calls go.</summary>
        internal sealed class SmelterLoopLocals
        {
            internal SmelterLoopLocals(int accumulator, int fuel, int queuedOre, int quickRefuelAt,
                int quickCraftAt)
            {
                Accumulator = accumulator;
                Fuel = fuel;
                QueuedOre = queuedOre;
                QuickRefuelAt = quickRefuelAt;
                QuickCraftAt = quickCraftAt;
            }

            internal int Accumulator { get; }
            internal int Fuel { get; }
            internal int QueuedOre { get; }

            /// <summary>Right after the store of GetFuel's result, before anything reads it.</summary>
            internal int QuickRefuelAt { get; }

            /// <summary>Right after the store of GetQueuedOre's result, before anything reads it.</summary>
            internal int QuickCraftAt { get; }
        }

        /// <summary>The fuel local of CookingStation.UpdateFuel and where the Refuel call goes.</summary>
        internal sealed class OvenFuelLocal
        {
            internal OvenFuelLocal(int fuel, int refuelAt)
            {
                Fuel = fuel;
                RefuelAt = refuelAt;
            }

            internal int Fuel { get; }

            /// <summary>
            ///     Right behind the ldarg.0 that starts SetFuel's arguments; see
            ///     <see cref="DeriveOvenFuel" /> for why it is behind it and not in front of it.
            /// </summary>
            internal int RefuelAt { get; }
        }
    }
}
