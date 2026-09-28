using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using IlInstruction = Automatics.AutomaticProcessing.TranspilerLocals.IlInstruction;

namespace Automatics.AutomaticProcessing
{
    /// <summary>
    ///     Offline checks for the derivation of IL locals and injection points that the Automatic
    ///     Processing transpilers use. The project compiles
    ///     <c>Automatics/AutomaticProcessing/TranspilerLocals.cs</c> directly and references
    ///     nothing but the base class library, so every case calls the same
    ///     <c>TranspilerLocals.DeriveSmelterLoop</c> / <c>InsertBackToFront</c> that the
    ///     transpilers in Patches.cs call.
    ///     Most cases synthesise (opcode, operand) lists shaped like the Valheim 1.0.7 IL of
    ///     Smelter.UpdateSmelter, spelled out here rather than
    ///     produced by the code under test, and declare the method's locals through a stand-in
    ///     MethodInfo. One case decodes the released game's own method bodies from
    ///     $VALHEIM_DIR/valheim_Data/Managed/assembly_valheim.dll, the install scripts/build.sh
    ///     compiles against. Whether Harmony hands the transpilers operands of these forms, and
    ///     whether the patched methods then run, is only visible in the game.
    ///     Every refusal case checks the reason it was refused, so a case cannot pass on an
    ///     exception that an earlier, unrelated check happened to throw.
    /// </summary>
    internal static partial class Program
    {
        private static int _failures;

        private static readonly MethodInfo GetFuel = typeof(Game).GetMethod(nameof(Game.GetFuel));

        private static readonly MethodInfo GetQueuedOre =
            typeof(Game).GetMethod(nameof(Game.GetQueuedOre));

        private static readonly MethodInfo GetAccumulator =
            typeof(Game).GetMethod(nameof(Game.GetAccumulator));

        private static readonly MethodInfo SetAccumulator =
            typeof(Game).GetMethod(nameof(Game.SetAccumulator));

        private static readonly MethodInfo StringEquality =
            typeof(string).GetMethod("op_Equality", new[] { typeof(string), typeof(string) });

        // Smelter.UpdateSmelter in Valheim 1.0.7 declares
        // [0] float64, [1] float32, [2] float32, [3] float32, [4] string, [5..7] float32.
        private static readonly Type[] SmelterLocals =
        {
            typeof(double), typeof(float), typeof(float), typeof(float),
            typeof(string), typeof(float), typeof(float), typeof(float)
        };

        private static int Main()
        {
            Run("the smelter loop head resolves accumulator=1, fuel=3, queuedOre=4 on the 1.0.7 shape",
                SmelterLoopHeadOnTheReleasedShape);
            Run("reordered smelter locals are followed rather than assumed",
                ReorderedSmelterLocalsAreFollowed);
            Run("a derived local with the wrong declared type is refused",
                WrongDeclaredTypeIsRefused);
            Run("an anchor that matches twice in the method is refused",
                AnchorMatchingTwiceIsRefused);
            Run("an anchor that does not match is refused",
                AnchorMatchingNothingIsRefused);
            Run("an accumulator read and written through different locals is refused",
                AccumulatorThroughDifferentLocalsIsRefused);
            Run("local operands of unknown form, negative or undeclared are refused",
                MalformedLocalOperandsAreRefused);
            Run("a derived index beyond the declared locals is refused",
                IndexBeyondTheDeclaredLocalsIsRefused);
            Run("queuedOre must be the method's only string local",
                QueuedOreMustBeTheOnlyStringLocal);
            Run("a method without a readable body is refused",
                MethodWithoutBodyIsRefused);
            Run("LoadLocal and StoreLocal round-trip through LocalIndexOf",
                EncodedLocalsRoundTrip);
            Run("LocalBuilder and LocalVariableInfo operands resolve to their LocalIndex",
                ReflectionOperandsResolve);
            Run("declared local types are ordered by index and must be complete",
                DeclaredLocalTypesAreIndexed);
            Run("a pattern built from a missing game method is refused",
                PatternFromMissingMethodIsRefused);
            Run("insertions land at the positions they were derived for, applied back to front",
                InsertionsLandWhereTheyWereDerived);
            Run("the released Valheim 1.0.7 method bodies resolve as f1e7d90 patched them",
                ReleasedGameMethodBodies);
            RunMappingAnchorCases();
            RunRepairAnchorCases();

            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"ok: {name}");
            }
            catch (Exception e)
            {
                _failures++;
                Console.Error.WriteLine($"FAIL: {name}");
                Console.Error.WriteLine(e.Message);
            }
        }

        // --- Smelter.UpdateSmelter -------------------------------------------------------

        private static void SmelterLoopHeadOnTheReleasedShape()
        {
            var code = SmelterMethod(ReleasedLoopHead(), 1, out var loopHead);
            var loop = TranspilerLocals.DeriveSmelterLoop(new FakeMethod(SmelterLocals), code, GetFuel, GetQueuedOre);

            AssertEqual(1, loop.Accumulator, "accumulator");
            AssertEqual(3, loop.Fuel, "fuel");
            AssertEqual(4, loop.QueuedOre, "queuedOre");
            // Positions come from the loop head this method was built with, not from the ones
            // after the loop where GetFuel and GetQueuedOre are read again.
            AssertEqual(loopHead + 7, loop.QuickRefuelAt, "QuickRefuel goes right after the fuel store");
            AssertEqual(loopHead + 10, loop.QuickCraftAt, "QuickCraft goes right after the queuedOre store");
        }

        private static void ReorderedSmelterLocalsAreFollowed()
        {
            // The accumulator load and store use different operand forms for the same local.
            var code = SmelterMethod(LoopHead(
                I(OpCodes.Ldloc_S, LocalBuilderAt(5, typeof(float))),
                I(OpCodes.Stloc_S, (byte)5),
                I(OpCodes.Stloc_2),
                I(OpCodes.Stloc_S, new FakeLocal(7, typeof(string)))), 1, out _);
            var types = new[]
            {
                typeof(double), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(string)
            };

            var loop = TranspilerLocals.DeriveSmelterLoop(new FakeMethod(types), code, GetFuel, GetQueuedOre);

            AssertEqual(5, loop.Accumulator, "accumulator");
            AssertEqual(2, loop.Fuel, "fuel");
            AssertEqual(7, loop.QueuedOre, "queuedOre");
        }

        private static void WrongDeclaredTypeIsRefused()
        {
            var code = SmelterMethod(ReleasedLoopHead(), 1, out _);
            var types = (Type[])SmelterLocals.Clone();
            types[3] = typeof(string); // the local the GetFuel result is stored into

            ExpectRefusal(() => DeriveSmelter(code, types), "fuel", "declared as String", "expected Single");
        }

        private static void AnchorMatchingTwiceIsRefused()
        {
            var code = SmelterMethod(ReleasedLoopHead(), 2, out _);

            ExpectRefusal(() => DeriveSmelter(code, SmelterLocals), "exactly once", "found 2 matches");
        }

        private static void AnchorMatchingNothingIsRefused()
        {
            var code = SmelterMethod(LoopHead(
                I(OpCodes.Ldloc_1), I(OpCodes.Stloc_1), I(OpCodes.Stloc_3), I(OpCodes.Stloc_S, (byte)4),
                OpCodes.Add), 1, out _);

            ExpectRefusal(() => DeriveSmelter(code, SmelterLocals), "exactly once", "found 0 matches");
        }

        private static void AccumulatorThroughDifferentLocalsIsRefused()
        {
            var code = SmelterMethod(LoopHead(
                I(OpCodes.Ldloc_1), I(OpCodes.Stloc_2), I(OpCodes.Stloc_3), I(OpCodes.Stloc_S, (byte)4)), 1, out _);

            ExpectRefusal(() => DeriveSmelter(code, SmelterLocals), "accumulator", "must refer to the same local");
        }

        private static void MalformedLocalOperandsAreRefused()
        {
            var malformed = new (object Operand, string Reason)[]
            {
                (null, "unknown form (null)"),
                ("x", "unknown form (System.String)"),
                (-1, "negative local index (-1)")
            };
            foreach (var (operand, reason) in malformed)
            {
                var code = SmelterMethod(LoopHead(
                    I(OpCodes.Ldloc_S, operand), I(OpCodes.Stloc_1), I(OpCodes.Stloc_3),
                    I(OpCodes.Stloc_S, (byte)4)), 1, out _);
                ExpectRefusal(() => DeriveSmelter(code, SmelterLocals), reason);
            }

            // A LocalBuilder is a known form, but this one is local 9 of a method that declares 8.
            var undeclared = LocalBuilderAt(9, typeof(float));
            var beyond = SmelterMethod(LoopHead(
                I(OpCodes.Ldloc_S, undeclared), I(OpCodes.Stloc_S, undeclared), I(OpCodes.Stloc_3),
                I(OpCodes.Stloc_S, (byte)4)), 1, out _);
            ExpectRefusal(() => DeriveSmelter(beyond, SmelterLocals), "accumulator", "local 9 is not declared");

            ExpectRefusal(() => TranspilerLocals.LocalIndexOf(OpCodes.Ldarg_0, null),
                "does not load or store a local");
        }

        private static void IndexBeyondTheDeclaredLocalsIsRefused()
        {
            var code = SmelterMethod(ReleasedLoopHead(), 1, out _);
            var types = new[] { typeof(double), typeof(float), typeof(float), typeof(float) };

            ExpectRefusal(() => DeriveSmelter(code, types), "queuedOre", "local 4 is not declared");
        }

        private static void QueuedOreMustBeTheOnlyStringLocal()
        {
            var code = SmelterMethod(ReleasedLoopHead(), 1, out _);
            var types = (Type[])SmelterLocals.Clone();
            types[7] = typeof(string);

            ExpectRefusal(() => DeriveSmelter(code, types), "Smelter.UpdateSmelter", "exactly one String local, found 2");
        }

        private static void MethodWithoutBodyIsRefused()
        {
            var smelter = SmelterMethod(ReleasedLoopHead(), 1, out _);

            ExpectRefusal(() => TranspilerLocals.DeriveSmelterLoop(FakeMethod.WithoutBody(), smelter, GetFuel, GetQueuedOre),
                "Smelter.UpdateSmelter", "method body is unavailable");
        }

        // --- Opcode helpers --------------------------------------------------------------

        private static void EncodedLocalsRoundTrip()
        {
            var expected = new (int Index, OpCode Load, OpCode Store)[]
            {
                (0, OpCodes.Ldloc_0, OpCodes.Stloc_0),
                (3, OpCodes.Ldloc_3, OpCodes.Stloc_3),
                (4, OpCodes.Ldloc_S, OpCodes.Stloc_S),
                (255, OpCodes.Ldloc_S, OpCodes.Stloc_S),
                (256, OpCodes.Ldloc, OpCodes.Stloc)
            };
            foreach (var (index, loadOpCode, storeOpCode) in expected)
            {
                var load = TranspilerLocals.LoadLocal(index);
                var store = TranspilerLocals.StoreLocal(index);

                AssertTrue(load.OpCode == loadOpCode, $"LoadLocal({index}) should be {loadOpCode.Name}, was {load}.");
                AssertTrue(store.OpCode == storeOpCode, $"StoreLocal({index}) should be {storeOpCode.Name}, was {store}.");
                AssertTrue(TranspilerLocals.IsLocalLoad(load.OpCode) && !TranspilerLocals.IsLocalStore(load.OpCode),
                    $"LoadLocal({index}) must be a local load and nothing else.");
                AssertTrue(TranspilerLocals.IsLocalStore(store.OpCode) && !TranspilerLocals.IsLocalLoad(store.OpCode),
                    $"StoreLocal({index}) must be a local store and nothing else.");
                AssertTrue(index <= 3 ? load.Operand == null && store.Operand == null
                        : load.Operand is int && store.Operand is int,
                    $"Local {index} should carry {(index <= 3 ? "no operand" : "an int operand")}.");
                AssertEqual(index, TranspilerLocals.LocalIndexOf(load.OpCode, load.Operand), $"LoadLocal({index})");
                AssertEqual(index, TranspilerLocals.LocalIndexOf(store.OpCode, store.Operand), $"StoreLocal({index})");
            }

            ExpectThrows<ArgumentOutOfRangeException>(() => TranspilerLocals.LoadLocal(-1), "LoadLocal(-1)");
            ExpectThrows<ArgumentOutOfRangeException>(() => TranspilerLocals.StoreLocal(65535), "StoreLocal(65535)");
        }

        private static void ReflectionOperandsResolve()
        {
            var builder = LocalBuilderAt(6, typeof(float));
            AssertEqual(6, TranspilerLocals.LocalIndexOf(OpCodes.Ldloc_S, builder), "ldloc.s LocalBuilder");
            AssertEqual(6, TranspilerLocals.LocalIndexOf(OpCodes.Stloc, builder), "stloc LocalBuilder");
            AssertEqual(7, TranspilerLocals.LocalIndexOf(OpCodes.Stloc_S, new FakeLocal(7, typeof(string))),
                "stloc.s LocalVariableInfo");
            AssertEqual(300, TranspilerLocals.LocalIndexOf(OpCodes.Ldloc, new FakeLocal(300, typeof(int))),
                "ldloc LocalVariableInfo");
            AssertEqual(200, TranspilerLocals.LocalIndexOf(OpCodes.Ldloc_S, (byte)200), "ldloc.s byte");
            AssertEqual(300, TranspilerLocals.LocalIndexOf(OpCodes.Ldloc, (short)300), "ldloc short");
            AssertEqual(4, TranspilerLocals.LocalIndexOf(OpCodes.Stloc_S, 4), "stloc.s int");
        }

        // --- Declared locals, patterns and insertion -------------------------------------

        private static void DeclaredLocalTypesAreIndexed()
        {
            var types = TranspilerLocals.DeclaredLocalTypes(
                new LocalVariableInfo[] { new FakeLocal(1, typeof(float)), new FakeLocal(0, typeof(double)) },
                "out of order");
            AssertTrue(types.Count == 2 && types[0] == typeof(double) && types[1] == typeof(float),
                "Declared types must be placed at their local's index, not in list order.");

            ExpectRefusal(() => TranspilerLocals.DeclaredLocalTypes(
                    new LocalVariableInfo[] { new FakeLocal(0, typeof(float)), new FakeLocal(2, typeof(float)) },
                    "gap"),
                "gap", "exactly once each");
            ExpectRefusal(() => TranspilerLocals.DeclaredLocalTypes(
                    new LocalVariableInfo[] { new FakeLocal(0, typeof(float)), new FakeLocal(0, typeof(float)) },
                    "duplicate"),
                "duplicate", "exactly once each");
            ExpectRefusal(() => TranspilerLocals.DeclaredLocalTypes(null, "missing"), "missing", "unavailable");
        }

        private static void PatternFromMissingMethodIsRefused()
        {
            // What a reflection lookup returns for a game method that was renamed or removed.
            var smelter = SmelterMethod(ReleasedLoopHead(), 1, out _);

            ExpectThrows<ArgumentNullException>(
                () => TranspilerLocals.DeriveSmelterLoop(new FakeMethod(SmelterLocals), smelter, null, GetQueuedOre),
                "DeriveSmelterLoop without GetFuel");
        }

        private static void InsertionsLandWhereTheyWereDerived()
        {
            var code = SmelterMethod(ReleasedLoopHead(), 1, out var loopHead);
            var loop = TranspilerLocals.DeriveSmelterLoop(new FakeMethod(SmelterLocals), code, GetFuel, GetQueuedOre);

            // Passed front to back on purpose: the positions must hold anyway.
            TranspilerLocals.InsertBackToFront(code,
                new TranspilerLocals.Insertion<IlInstruction>(loop.QuickRefuelAt,
                    new[] { I(OpCodes.Nop, "QuickRefuel") }),
                new TranspilerLocals.Insertion<IlInstruction>(loop.QuickCraftAt,
                    new[] { I(OpCodes.Nop, "QuickCraft"), I(OpCodes.Nop, "QuickCraft") }));

            AssertTrue(code[loopHead + 6].OpCode == OpCodes.Stloc_3 && Equals(code[loopHead + 7].Operand, "QuickRefuel"),
                $"QuickRefuel must follow the fuel store, found {code[loopHead + 6]} / {code[loopHead + 7]}.");
            AssertTrue(code[loopHead + 8].OpCode == OpCodes.Ldarg_0 && code[loopHead + 9].Operand as MethodInfo == GetQueuedOre,
                $"The GetQueuedOre read must follow QuickRefuel, found {code[loopHead + 8]} / {code[loopHead + 9]}.");
            AssertTrue(code[loopHead + 10].OpCode == OpCodes.Stloc_S &&
                       Equals(code[loopHead + 11].Operand, "QuickCraft") && Equals(code[loopHead + 12].Operand, "QuickCraft"),
                $"QuickCraft must follow the queuedOre store, found {code[loopHead + 10]} / {code[loopHead + 11]}.");

            var list = new List<int> { 0, 1, 2 };
            ExpectThrows<ArgumentException>(() => TranspilerLocals.InsertBackToFront(list,
                    new TranspilerLocals.Insertion<int>(1, new[] { 10 }), new TranspilerLocals.Insertion<int>(1, new[] { 11 })),
                "two insertions at one position");
            ExpectThrows<ArgumentOutOfRangeException>(() => TranspilerLocals.InsertBackToFront(list,
                new TranspilerLocals.Insertion<int>(4, new[] { 10 })), "an insertion past the end");
            AssertTrue(list.SequenceEqual(new[] { 0, 1, 2 }), "A refused insertion must not have inserted anything.");
        }

        // --- The released game ------------------------------------------------------------

        private static void ReleasedGameMethodBodies()
        {
            var valheim = Environment.GetEnvironmentVariable("VALHEIM_DIR");
            var assembly = string.IsNullOrEmpty(valheim)
                ? null
                : Path.Combine(valheim, "valheim_Data", "Managed", "assembly_valheim.dll");
            if (assembly == null || !File.Exists(assembly))
                throw new InvalidOperationException(
                    "set VALHEIM_DIR to the Valheim install scripts/build.sh builds against; this case reads " +
                    $"the released game's method bodies from valheim_Data/Managed/assembly_valheim.dll (VALHEIM_DIR={valheim ?? "<unset>"})");

            // The positions f1e7d90's CodeMatcher chains inserted at in Valheim 1.0.7, measured
            // independently of this derivation: the patched instruction stream must not change.
            var smelter = DecodedMethod.Read(assembly, "Smelter", "UpdateSmelter");
            var loop = TranspilerLocals.DeriveSmelterLoop(smelter.Method, smelter.Code, GetFuel, GetQueuedOre);
            AssertEqual(1, loop.Accumulator, "1.0.7 Smelter.UpdateSmelter accumulator");
            AssertEqual(3, loop.Fuel, "1.0.7 Smelter.UpdateSmelter fuel");
            AssertEqual(4, loop.QueuedOre, "1.0.7 Smelter.UpdateSmelter queuedOre");
            AssertEqual(50, loop.QuickRefuelAt, "1.0.7 Smelter.UpdateSmelter QuickRefuel position");
            AssertEqual(53, loop.QuickCraftAt, "1.0.7 Smelter.UpdateSmelter QuickCraft position");
            AssertTrue(!smelter.BranchTargets.Contains(loop.QuickRefuelAt) && !smelter.BranchTargets.Contains(loop.QuickCraftAt),
                "No jump may land where the smelter calls are inserted, or it would skip them.");

        }

        // --- Method shapes ---------------------------------------------------------------

        private static (int Accumulator, int Fuel, int QueuedOre) DeriveSmelter(IList<IlInstruction> code,
            Type[] localTypes)
        {
            var loop = TranspilerLocals.DeriveSmelterLoop(new FakeMethod(localTypes), code, GetFuel, GetQueuedOre);
            return (loop.Accumulator, loop.Fuel, loop.QueuedOre);
        }

        private static IlInstruction[] ReleasedLoopHead()
        {
            return LoopHead(I(OpCodes.Ldloc_1), I(OpCodes.Stloc_1), I(OpCodes.Stloc_3), I(OpCodes.Stloc_S, (byte)4));
        }

        /// <summary>
        ///     <c>accumulator -= 1f; fuel = GetFuel(); queuedOre = GetQueuedOre();</c>
        /// </summary>
        private static IlInstruction[] LoopHead(IlInstruction accumulatorLoad, IlInstruction accumulatorStore,
            IlInstruction fuelStore, IlInstruction queuedOreStore, OpCode? arithmetic = null)
        {
            return new[]
            {
                accumulatorLoad, I(OpCodes.Ldc_R4, 1f), I(arithmetic ?? OpCodes.Sub), accumulatorStore,
                I(OpCodes.Ldarg_0), I(OpCodes.Call, GetFuel), fuelStore,
                I(OpCodes.Ldarg_0), I(OpCodes.Call, GetQueuedOre), queuedOreStore
            };
        }

        /// <summary>
        ///     The outline of Smelter.UpdateSmelter: the accumulator is read and clamped, then
        ///     <paramref name="loops" /> copies of <c>while (accumulator &gt;= 1f) { head; ... }</c>,
        ///     then GetQueuedOre and GetFuel are read again after the loop and the accumulator is
        ///     saved. Operands that only the game can supply (fields, labels) are placeholders.
        /// </summary>
        private static List<IlInstruction> SmelterMethod(IList<IlInstruction> loopHead, int loops,
            out int firstLoopHead)
        {
            var code = new List<IlInstruction>
            {
                I(OpCodes.Ldarg_0), I(OpCodes.Call, GetAccumulator), I(OpCodes.Stloc_1),
                I(OpCodes.Ldloc_1), I(OpCodes.Ldloc_0), I(OpCodes.Conv_R4), I(OpCodes.Add), I(OpCodes.Stloc_1),
                I(OpCodes.Ldc_R4, 1f), I(OpCodes.Stloc_2)
            };

            firstLoopHead = -1;
            for (var loop = 0; loop < loops; loop++)
            {
                code.Add(I(OpCodes.Br_S, 0));
                if (firstLoopHead < 0) firstLoopHead = code.Count;
                code.AddRange(loopHead);
                code.Add(I(OpCodes.Nop));
                code.AddRange(new[] { I(OpCodes.Ldloc_1), I(OpCodes.Ldc_R4, 1f), I(OpCodes.Bge_S, 0) });
            }

            code.AddRange(new[]
            {
                I(OpCodes.Ldarg_0), I(OpCodes.Call, GetQueuedOre), I(OpCodes.Ldstr, ""),
                I(OpCodes.Call, StringEquality), I(OpCodes.Brtrue_S, 0),
                I(OpCodes.Ldarg_0), I(OpCodes.Call, GetFuel), I(OpCodes.Ldc_R4, 0f), I(OpCodes.Ceq), I(OpCodes.Pop),
                I(OpCodes.Ldarg_0), I(OpCodes.Ldloc_1), I(OpCodes.Call, SetAccumulator), I(OpCodes.Ret)
            });
            return code;
        }

        private static IlInstruction I(OpCode opcode, object operand = null)
        {
            return new IlInstruction(opcode, operand);
        }

        private static LocalBuilder LocalBuilderAt(int index, Type type)
        {
            var generator = new DynamicMethod("locals", typeof(void), Type.EmptyTypes).GetILGenerator();
            LocalBuilder local = null;
            for (var i = 0; i <= index; i++)
                local = generator.DeclareLocal(i == index ? type : typeof(object));

            if (local == null || local.LocalIndex != index)
                throw new InvalidOperationException(
                    $"harness: DeclareLocal did not number the locals as expected ({local?.LocalIndex})");
            return local;
        }

        // --- Assertions ------------------------------------------------------------------

        private static void AssertEqual<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"{what}: expected {expected}, got {actual}.");
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        /// <summary>
        ///     Requires <paramref name="action" /> to be refused with an InvalidOperationException
        ///     whose message names every one of <paramref name="reasons" />.
        /// </summary>
        private static void ExpectRefusal(Action action, params string[] reasons)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException e)
            {
                foreach (var reason in reasons)
                    if (!e.Message.Contains(reason))
                        throw new InvalidOperationException(
                            $"refused for another reason: expected \"{reason}\" in \"{e.Message}\"");
                return;
            }

            throw new InvalidOperationException(
                $"expected a refusal ({string.Join(", ", reasons)}), but the input was accepted");
        }

        private static void ExpectThrows<TException>(Action action, string what) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}, nothing was thrown");
        }

        // --- Stand-ins -------------------------------------------------------------------

        private sealed class FakeLocal : LocalVariableInfo
        {
            private readonly int _index;
            private readonly Type _type;

            public FakeLocal(int index, Type type)
            {
                _index = index;
                _type = type;
            }

            public override int LocalIndex => _index;
            public override Type LocalType => _type;
        }

        /// <summary>A method whose body declares the given locals, as the transpilers' original.</summary>
        private sealed class FakeMethod : MethodInfo
        {
            private readonly MethodBody _body;

            public FakeMethod(params Type[] locals)
            {
                _body = new FakeBody(locals.Select((type, index) => (LocalVariableInfo)new FakeLocal(index, type)).ToList());
            }

            private FakeMethod(MethodBody body)
            {
                _body = body;
            }

            public static FakeMethod WithoutBody()
            {
                return new FakeMethod((MethodBody)null);
            }

            public override MethodBody GetMethodBody() => _body;
            public override string Name => "Original";
            public override Type DeclaringType => null;
            public override Type ReflectedType => null;
            public override MethodAttributes Attributes => MethodAttributes.Private;
            public override RuntimeMethodHandle MethodHandle => throw new NotSupportedException();
            public override ICustomAttributeProvider ReturnTypeCustomAttributes => throw new NotSupportedException();
            public override MethodInfo GetBaseDefinition() => this;
            public override object[] GetCustomAttributes(bool inherit) => Array.Empty<object>();
            public override object[] GetCustomAttributes(Type attributeType, bool inherit) => Array.Empty<object>();
            public override bool IsDefined(Type attributeType, bool inherit) => false;
            public override MethodImplAttributes GetMethodImplementationFlags() => MethodImplAttributes.IL;
            public override ParameterInfo[] GetParameters() => Array.Empty<ParameterInfo>();

            public override object Invoke(object obj, BindingFlags invokeAttr, Binder binder, object[] parameters,
                CultureInfo culture) => throw new NotSupportedException();
        }

        private sealed class FakeBody : MethodBody
        {
            private readonly IList<LocalVariableInfo> _locals;

            public FakeBody(IList<LocalVariableInfo> locals)
            {
                _locals = locals;
            }

            public override IList<LocalVariableInfo> LocalVariables => _locals;
        }

        /// <summary>
        ///     A game method read straight from the released assembly with System.Reflection.Metadata:
        ///     its instructions as (opcode, operand) pairs, its declared local types, and the
        ///     instructions some jump lands on. Calls to GetFuel and GetQueuedOre of the
        ///     method's own type become the stand-ins the derivation is given; every other operand
        ///     that the derivation does not read stays a placeholder.
        /// </summary>
        private sealed class DecodedMethod
        {
            private static readonly Dictionary<int, OpCode> OpCodesByValue = typeof(OpCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(field => (OpCode)field.GetValue(null))
                .GroupBy(opcode => (int)(ushort)opcode.Value)
                .ToDictionary(group => group.Key, group => group.First());

            private static readonly Dictionary<string, MethodInfo> StandIns = new Dictionary<string, MethodInfo>
            {
                ["GetFuel"] = GetFuel, ["GetQueuedOre"] = GetQueuedOre
            };

            public List<IlInstruction> Code { get; private set; }
            public HashSet<int> BranchTargets { get; private set; }
            public MethodInfo Method { get; private set; }

            public static DecodedMethod Read(string assemblyPath, string typeName, string methodName)
            {
                using var stream = File.OpenRead(assemblyPath);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                var definition = reader.TypeDefinitions.Select(reader.GetTypeDefinition)
                    .Where(type => reader.GetString(type.Name) == typeName && reader.GetString(type.Namespace) == "")
                    .SelectMany(type => type.GetMethods()).Select(reader.GetMethodDefinition)
                    .Single(method => reader.GetString(method.Name) == methodName);
                var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
                var locals = body.LocalSignature.IsNil
                    ? Array.Empty<Type>()
                    : reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(new LocalTypes(), null).ToArray();

                var il = body.GetILBytes();
                var code = new List<IlInstruction>();
                var indexByOffset = new Dictionary<int, int>();
                var targetOffsets = new List<int>();
                var pos = 0;
                while (pos < il.Length)
                {
                    indexByOffset[pos] = code.Count;
                    var key = il[pos] == 0xFE ? 0xFE00 | il[pos + 1] : il[pos];
                    pos += il[pos] == 0xFE ? 2 : 1;
                    var opcode = OpCodesByValue[key];
                    object operand = null;
                    switch (opcode.OperandType)
                    {
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineVar:
                            operand = il[pos];
                            pos += 1;
                            break;
                        case OperandType.ShortInlineI:
                            operand = (sbyte)il[pos];
                            pos += 1;
                            break;
                        case OperandType.ShortInlineBrTarget:
                            targetOffsets.Add(pos + 1 + (sbyte)il[pos]);
                            operand = "label";
                            pos += 1;
                            break;
                        case OperandType.InlineVar:
                            operand = (short)BitConverter.ToUInt16(il, pos);
                            pos += 2;
                            break;
                        case OperandType.ShortInlineR:
                            operand = BitConverter.ToSingle(il, pos);
                            pos += 4;
                            break;
                        case OperandType.InlineBrTarget:
                            targetOffsets.Add(pos + 4 + BitConverter.ToInt32(il, pos));
                            operand = "label";
                            pos += 4;
                            break;
                        case OperandType.InlineMethod:
                            operand = ResolveCall(reader, BitConverter.ToInt32(il, pos), typeName);
                            pos += 4;
                            break;
                        case OperandType.InlineField:
                            operand = ResolveField(reader, BitConverter.ToInt32(il, pos));
                            pos += 4;
                            break;
                        case OperandType.InlineSwitch:
                            var count = BitConverter.ToInt32(il, pos);
                            var next = pos + 4 + 4 * count;
                            for (var k = 0; k < count; k++)
                                targetOffsets.Add(next + BitConverter.ToInt32(il, pos + 4 + 4 * k));
                            operand = "switch";
                            pos = next;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            pos += 8;
                            break;
                        default:
                            operand = BitConverter.ToInt32(il, pos);
                            pos += 4;
                            break;
                    }

                    code.Add(new IlInstruction(opcode, operand));
                }

                return new DecodedMethod
                {
                    Code = code,
                    BranchTargets = new HashSet<int>(targetOffsets.Select(offset => indexByOffset[offset])),
                    Method = new FakeMethod(locals)
                };
            }

            private static object ResolveCall(MetadataReader reader, int token, string typeName)
            {
                var handle = MetadataTokens.EntityHandle(token);
                string name;
                string owner;
                if (handle.Kind == HandleKind.MethodDefinition)
                {
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    name = reader.GetString(method.Name);
                    owner = reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);
                }
                else if (handle.Kind == HandleKind.MemberReference)
                {
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    name = reader.GetString(member.Name);
                    owner = member.Parent.Kind == HandleKind.TypeReference
                        ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name)
                        : member.Parent.Kind == HandleKind.TypeDefinition
                            ? reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)member.Parent).Name)
                            : "?";
                }
                else
                {
                    return "call";
                }

                return owner == typeName && StandIns.TryGetValue(name, out var standIn)
                    ? standIn
                    : (object)$"{owner}::{name}";
            }

            /// <summary>A field operand as <c>Owner::name</c>, the owner being the declaring type's own name.</summary>
            private static object ResolveField(MetadataReader reader, int token)
            {
                var handle = MetadataTokens.EntityHandle(token);
                if (handle.Kind == HandleKind.FieldDefinition)
                {
                    var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                    var owner = reader.GetString(reader.GetTypeDefinition(field.GetDeclaringType()).Name);
                    return $"{owner}::{reader.GetString(field.Name)}";
                }

                if (handle.Kind == HandleKind.MemberReference)
                {
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    var owner = member.Parent.Kind == HandleKind.TypeReference
                        ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name)
                        : "?";
                    return $"{owner}::{reader.GetString(member.Name)}";
                }

                return "field";
            }
        }

        /// <summary>Maps the primitive local types the derivation checks; everything else is <see cref="OtherType" />.</summary>
        private sealed class LocalTypes : ISignatureTypeProvider<Type, object>
        {
            public Type GetPrimitiveType(PrimitiveTypeCode typeCode)
            {
                switch (typeCode)
                {
                    case PrimitiveTypeCode.Single: return typeof(float);
                    case PrimitiveTypeCode.Double: return typeof(double);
                    case PrimitiveTypeCode.String: return typeof(string);
                    case PrimitiveTypeCode.Int32: return typeof(int);
                    case PrimitiveTypeCode.Boolean: return typeof(bool);
                    default: return typeof(OtherType);
                }
            }

            public Type GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                var type = reader.GetTypeDefinition(handle);
                return NamedType(reader.GetString(type.Namespace), reader.GetString(type.Name));
            }

            public Type GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var type = reader.GetTypeReference(handle);
                return NamedType(reader.GetString(type.Namespace), reader.GetString(type.Name));
            }

            internal static Type NamedType(string space, string name)
            {
                return space == "" && name == "Player" ? typeof(RepairPlayer) : typeof(OtherType);
            }
            public Type GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => typeof(OtherType);
            public Type GetSZArrayType(Type elementType) => typeof(OtherType);
            public Type GetArrayType(Type elementType, ArrayShape shape) => typeof(OtherType);
            public Type GetByReferenceType(Type elementType) => typeof(OtherType);
            public Type GetPointerType(Type elementType) => typeof(OtherType);
            public Type GetGenericInstantiation(Type genericType, ImmutableArray<Type> typeArguments) => typeof(OtherType);
            public Type GetGenericMethodParameter(object genericContext, int index) => typeof(OtherType);
            public Type GetGenericTypeParameter(object genericContext, int index) => typeof(OtherType);
            public Type GetFunctionPointerType(MethodSignature<Type> signature) => typeof(OtherType);
            public Type GetModifiedType(Type modifier, Type unmodifiedType, bool isRequired) => unmodifiedType;
            public Type GetPinnedType(Type elementType) => elementType;
        }

        private sealed class OtherType
        {
        }

        /// <summary>Stand-ins whose MethodInfo plays the game's methods in the patterns.</summary>
        private static class Game
        {
            public static float GetFuel()
            {
                return 0f;
            }

            public static string GetQueuedOre()
            {
                return "";
            }

            public static float GetAccumulator()
            {
                return 0f;
            }

            public static void SetAccumulator(float accumulator)
            {
            }
        }
    }
}
