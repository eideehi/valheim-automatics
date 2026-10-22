using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using IlInstruction = Automatics.AutomaticProcessing.TranspilerLocals.IlInstruction;

namespace Automatics.AutomaticProcessing
{
    // The locals the player update hooks (Automatics/Patches.cs) and the gamepad save-flag branch
    // of Minimap.UpdateMap (Automatics/AutomaticMapping/Patches.cs) are handed, derived by the
    // shipped TranspilerLocals functions those transpilers call. The synthetic cases build
    // outlines of the game methods; the released cases decode the installed game's own bodies and
    // compare the derivation with what the transpilers inserted before: the hard-coded local and
    // the position their CodeMatcher search reached.
    internal static partial class Program
    {
        private static readonly MethodInfo UpdatePlacement = typeof(Game).GetMethod(nameof(Game.UpdatePlacement));
        private static readonly MethodInfo GetDeltaTime = typeof(Game).GetMethod(nameof(Game.GetDeltaTime));
        private static readonly MethodInfo UpdateStealth = typeof(Game).GetMethod(nameof(Game.UpdateStealth));

        private static readonly MethodInfo GetPinInteractRadius =
            typeof(Game).GetMethod(nameof(Game.GetPinInteractRadius));

        private static readonly MethodInfo GetClosestPin = typeof(Game).GetMethod(nameof(Game.GetClosestPin));

        // Player.Update declares [0] bool, [1] bool (takeInput), [2..3] string, [4] float64,
        // [5..10] bool, [11] int32 on the installed build.
        private static readonly Type[] PlayerUpdateLocals =
        {
            typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(double), typeof(bool),
            typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(int)
        };

        // Minimap.UpdateMap declares Vector3 locals at 1, 7 (the RemovePin branch's position),
        // 8 (the closest-pin branch's position) and later ones; 9 is the PinData.
        private static readonly Type[] UpdateMapLocals =
        {
            typeof(float), typeof(UnityVector3), typeof(OtherType), typeof(OtherType), typeof(OtherType),
            typeof(bool), typeof(OtherType), typeof(UnityVector3), typeof(UnityVector3), typeof(OtherType),
            typeof(OtherType), typeof(OtherType), typeof(UnityVector3)
        };

        private static void RunHookLocalCases()
        {
            Run("the player update hook takes the local UpdatePlacement's takeInput comes from, right after that call",
                PlayerUpdateInputOnTheReleasedShape);
            Run("a reordered takeInput local is followed rather than assumed", ReorderedTakeInputIsFollowed);
            Run("a takeInput local not declared as bool is refused", TakeInputOfWrongTypeIsRefused);
            Run("an UpdatePlacement call that matches twice is refused", UpdatePlacementTwiceIsRefused);
            Run("an UpdatePlacement call fed by no local is refused", UpdatePlacementWithoutLocalIsRefused);

            Run("the player fixed update hook takes the local UpdateStealth is fed, right after that call",
                PlayerFixedDeltaTimeOnTheReleasedShape);
            Run("a reordered fixedDeltaTime local is followed rather than assumed", ReorderedFixedDeltaTimeIsFollowed);
            Run("a fixedDeltaTime local not declared as float is refused", FixedDeltaTimeOfWrongTypeIsRefused);
            Run("an UpdateStealth call that matches twice is refused", UpdateStealthTwiceIsRefused);
            Run("an UpdateStealth call fed by no local is refused", UpdateStealthWithoutLocalIsRefused);

            Run("the gamepad save flag takes the position GetClosestPin is fed, in front of the null check",
                ClosestPinOnTheReleasedShape);
            Run("a reordered closest-pin position local is followed rather than assumed", ReorderedClosestPinIsFollowed);
            Run("a closest-pin position local not declared as Vector3 is refused", ClosestPinOfWrongTypeIsRefused);
            Run("a GetClosestPin lookup that matches twice is refused", ClosestPinTwiceIsRefused);
            Run("a GetClosestPin lookup without its null check is refused", ClosestPinWithoutNullCheckIsRefused);
            Run("a closest pin stored and tested through different locals is refused",
                ClosestPinThroughDifferentLocalsIsRefused);

            Run("the released Player.Update hands the hook TakeInput's local where the old chain did", ReleasedPlayerUpdate);
            Run("the released Player.FixedUpdate hands the hook fixedDeltaTime where the old chain did",
                ReleasedPlayerFixedUpdate);
            Run("the released Minimap.UpdateMap hands the save flag the JoyTabLeft position where the old chain did",
                ReleasedUpdateMap);
        }

        // --- Player.Update ----------------------------------------------------------------

        private static void PlayerUpdateInputOnTheReleasedShape()
        {
            var code = PlayerUpdateMethod(I(OpCodes.Ldloc_1), 1, out var hookAt);
            var site = TranspilerLocals.DerivePlayerUpdateInput(new FakeMethod(PlayerUpdateLocals), code,
                GetDeltaTime, UpdatePlacement);

            AssertEqual(1, site.Local, "takeInput");
            AssertEqual(hookAt, site.HookAt, "the hook goes right after UpdatePlacement");
        }

        private static void ReorderedTakeInputIsFollowed()
        {
            var code = PlayerUpdateMethod(I(OpCodes.Ldloc_S, LocalBuilderAt(6, typeof(bool))), 1, out _);
            var types = (Type[])PlayerUpdateLocals.Clone();
            types[1] = typeof(string);

            AssertEqual(6, TranspilerLocals.DerivePlayerUpdateInput(new FakeMethod(types), code,
                GetDeltaTime, UpdatePlacement).Local, "takeInput");
        }

        private static void TakeInputOfWrongTypeIsRefused()
        {
            var code = PlayerUpdateMethod(I(OpCodes.Ldloc_1), 1, out _);
            var types = (Type[])PlayerUpdateLocals.Clone();
            types[1] = typeof(string);

            ExpectRefusal(() => TranspilerLocals.DerivePlayerUpdateInput(new FakeMethod(types), code,
                GetDeltaTime, UpdatePlacement), "Player.Update: takeInput", "declared as String", "expected Boolean");
        }

        private static void UpdatePlacementTwiceIsRefused()
        {
            var code = PlayerUpdateMethod(I(OpCodes.Ldloc_1), 2, out _);

            ExpectRefusal(() => TranspilerLocals.DerivePlayerUpdateInput(new FakeMethod(PlayerUpdateLocals), code,
                GetDeltaTime, UpdatePlacement), "Player.Update", "exactly once", "found 2 matches");
        }

        private static void UpdatePlacementWithoutLocalIsRefused()
        {
            var code = PlayerUpdateMethod(I(OpCodes.Ldc_I4_1), 1, out _);

            ExpectRefusal(() => TranspilerLocals.DerivePlayerUpdateInput(new FakeMethod(PlayerUpdateLocals), code,
                GetDeltaTime, UpdatePlacement), "Player.Update", "exactly once", "found 0 matches");
        }

        /// <summary>
        ///     The outline of Player.Update: takeInput is stored from TakeInput, read by other calls,
        ///     then <paramref name="calls" /> copies of <c>UpdatePlacement(takeInput, Time.deltaTime)</c>
        ///     and <c>UpdateStats()</c>.
        /// </summary>
        private static List<IlInstruction> PlayerUpdateMethod(IlInstruction takeInputLoad, int calls, out int hookAt)
        {
            var code = new List<IlInstruction>
            {
                I(OpCodes.Call, "InventoryGui::IsVisible"), I(OpCodes.Stloc_0),
                I(OpCodes.Ldarg_0), I(OpCodes.Callvirt, "Character::TakeInput"), I(OpCodes.Stloc_1),
                I(OpCodes.Ldloc_1), I(OpCodes.Brfalse, "label"),
                I(OpCodes.Ldarg_0), I(OpCodes.Ldloc_1), I(OpCodes.Call, "Player::UpdateGyro"),
                I(OpCodes.Ldarg_0), I(OpCodes.Ldfld, "Player::m_blockReload"),
                I(OpCodes.Call, GetDeltaTime), I(OpCodes.Sub), I(OpCodes.Stfld, "Player::m_blockReload")
            };

            hookAt = -1;
            for (var call = 0; call < calls; call++)
            {
                code.AddRange(new[]
                {
                    I(OpCodes.Ldarg_0), takeInputLoad, I(OpCodes.Call, GetDeltaTime), I(OpCodes.Call, UpdatePlacement)
                });
                if (hookAt < 0) hookAt = code.Count;
            }

            code.AddRange(new[] { I(OpCodes.Ldarg_0), I(OpCodes.Call, "Player::UpdateStats"), I(OpCodes.Ret) });
            return code;
        }

        // --- Player.FixedUpdate -----------------------------------------------------------

        private static void PlayerFixedDeltaTimeOnTheReleasedShape()
        {
            var code = PlayerFixedUpdateMethod(I(OpCodes.Ldloc_0), 1, out var hookAt);
            var site = TranspilerLocals.DerivePlayerFixedDeltaTime(new FakeMethod(typeof(float)), code, UpdateStealth);

            AssertEqual(0, site.Local, "fixedDeltaTime");
            AssertEqual(hookAt, site.HookAt, "the hook goes right after UpdateStealth");
        }

        private static void ReorderedFixedDeltaTimeIsFollowed()
        {
            var code = PlayerFixedUpdateMethod(I(OpCodes.Ldloc_2), 1, out _);

            AssertEqual(2, TranspilerLocals.DerivePlayerFixedDeltaTime(
                new FakeMethod(typeof(bool), typeof(OtherType), typeof(float)), code, UpdateStealth).Local,
                "fixedDeltaTime");
        }

        private static void FixedDeltaTimeOfWrongTypeIsRefused()
        {
            var code = PlayerFixedUpdateMethod(I(OpCodes.Ldloc_0), 1, out _);

            ExpectRefusal(() => TranspilerLocals.DerivePlayerFixedDeltaTime(new FakeMethod(typeof(double)), code,
                UpdateStealth), "Player.FixedUpdate: fixedDeltaTime", "declared as Double", "expected Single");
        }

        private static void UpdateStealthTwiceIsRefused()
        {
            var code = PlayerFixedUpdateMethod(I(OpCodes.Ldloc_0), 2, out _);

            ExpectRefusal(() => TranspilerLocals.DerivePlayerFixedDeltaTime(new FakeMethod(typeof(float)), code,
                UpdateStealth), "Player.FixedUpdate", "exactly once", "found 2 matches");
        }

        private static void UpdateStealthWithoutLocalIsRefused()
        {
            var code = PlayerFixedUpdateMethod(I(OpCodes.Ldc_R4, 0.02f), 1, out _);

            ExpectRefusal(() => TranspilerLocals.DerivePlayerFixedDeltaTime(new FakeMethod(typeof(float)), code,
                UpdateStealth), "Player.FixedUpdate", "exactly once", "found 0 matches");
        }

        /// <summary>
        ///     The outline of Player.FixedUpdate: fixedDeltaTime is stored and handed to other
        ///     updates, then <paramref name="calls" /> copies of <c>UpdateStealth(fixedDeltaTime)</c>
        ///     and the camera check that follows.
        /// </summary>
        private static List<IlInstruction> PlayerFixedUpdateMethod(IlInstruction deltaLoad, int calls, out int hookAt)
        {
            var code = new List<IlInstruction>
            {
                I(OpCodes.Call, "Time::get_fixedDeltaTime"), I(OpCodes.Stloc_0),
                I(OpCodes.Ldarg_0), I(OpCodes.Ldloc_0), I(OpCodes.Call, "Player::UpdateAwake"),
                I(OpCodes.Ldarg_0), I(OpCodes.Ldloc_0), I(OpCodes.Call, "Player::UpdateBiome")
            };

            hookAt = -1;
            for (var call = 0; call < calls; call++)
            {
                code.AddRange(new[] { I(OpCodes.Ldarg_0), deltaLoad, I(OpCodes.Call, UpdateStealth) });
                if (hookAt < 0) hookAt = code.Count;
            }

            code.AddRange(new[]
            {
                I(OpCodes.Call, "GameCamera::get_instance"), I(OpCodes.Call, "Object::op_Implicit"),
                I(OpCodes.Brfalse, "label"), I(OpCodes.Ret)
            });
            return code;
        }

        // --- Minimap.UpdateMap ------------------------------------------------------------

        private static void ClosestPinOnTheReleasedShape()
        {
            var code = UpdateMapMethod(ReleasedClosestPinLookup(), 1, out var nullCheckAt);
            var site = DeriveClosestPin(code, UpdateMapLocals);

            AssertEqual(8, site.Position, "pos");
            AssertEqual(nullCheckAt, site.NullCheckAt, "the null check on closestPin");
        }

        private static void ReorderedClosestPinIsFollowed()
        {
            var code = UpdateMapMethod(ClosestPinLookup(
                I(OpCodes.Ldloc_S, LocalBuilderAt(12, typeof(UnityVector3))),
                I(OpCodes.Stloc_S, new FakeLocal(10, typeof(OtherType))), I(OpCodes.Ldloc_S, (byte)10)), 1, out _);

            AssertEqual(12, DeriveClosestPin(code, UpdateMapLocals).Position, "pos");
        }

        private static void ClosestPinOfWrongTypeIsRefused()
        {
            var code = UpdateMapMethod(ReleasedClosestPinLookup(), 1, out _);
            var types = (Type[])UpdateMapLocals.Clone();
            types[8] = typeof(OtherType);

            ExpectRefusal(() => DeriveClosestPin(code, types),
                "Minimap.UpdateMap: pos", "declared as OtherType", "expected UnityVector3");
        }

        private static void ClosestPinTwiceIsRefused()
        {
            var code = UpdateMapMethod(ReleasedClosestPinLookup(), 2, out _);

            ExpectRefusal(() => DeriveClosestPin(code, UpdateMapLocals),
                "Minimap.UpdateMap", "exactly once", "found 2 matches");
        }

        private static void ClosestPinWithoutNullCheckIsRefused()
        {
            var lookup = new List<IlInstruction>(ReleasedClosestPinLookup());
            lookup[lookup.Count - 1] = I(OpCodes.Brtrue, "label");
            var code = UpdateMapMethod(lookup, 1, out _);

            ExpectRefusal(() => DeriveClosestPin(code, UpdateMapLocals),
                "Minimap.UpdateMap", "exactly once", "found 0 matches");
        }

        private static void ClosestPinThroughDifferentLocalsIsRefused()
        {
            var code = UpdateMapMethod(ClosestPinLookup(
                I(OpCodes.Ldloc_S, (byte)8), I(OpCodes.Stloc_S, (byte)9), I(OpCodes.Ldloc_S, (byte)10)), 1, out _);

            ExpectRefusal(() => DeriveClosestPin(code, UpdateMapLocals),
                "Minimap.UpdateMap: closestPin", "must refer to the same local");
        }

        private static TranspilerLocals.ClosestPinSite DeriveClosestPin(IList<IlInstruction> code, Type[] localTypes)
        {
            return TranspilerLocals.DeriveGamepadClosestPin(new FakeMethod(localTypes), code, typeof(UnityVector3),
                GetPinInteractRadius, GetClosestPin);
        }

        private static IlInstruction[] ReleasedClosestPinLookup()
        {
            return ClosestPinLookup(I(OpCodes.Ldloc_S, (byte)8), I(OpCodes.Stloc_S, (byte)9), I(OpCodes.Ldloc_S, (byte)9));
        }

        /// <summary>
        ///     <c>PinData closestPin = GetClosestPin(pos, PinInteractRadius); if (closestPin != null)</c>
        /// </summary>
        private static IlInstruction[] ClosestPinLookup(IlInstruction positionLoad, IlInstruction pinStore,
            IlInstruction pinLoad)
        {
            return new[]
            {
                I(OpCodes.Ldarg_0), positionLoad, I(OpCodes.Ldarg_0), I(OpCodes.Call, GetPinInteractRadius),
                I(OpCodes.Ldc_I4_1), I(OpCodes.Call, GetClosestPin), pinStore, pinLoad, I(OpCodes.Brfalse, "label")
            };
        }

        /// <summary>
        ///     The outline of the gamepad branches of Minimap.UpdateMap: JoyTabRight removes the pin
        ///     at the screen centre through local 7 with the same radius, then <paramref name="lookups" />
        ///     copies of the JoyTabLeft branch, which looks up the closest pin and tests it for null,
        ///     then the next button test.
        /// </summary>
        private static List<IlInstruction> UpdateMapMethod(IList<IlInstruction> lookup, int lookups, out int nullCheckAt)
        {
            var code = new List<IlInstruction>
            {
                I(OpCodes.Ldarg_3), I(OpCodes.Brfalse, "label"),
                I(OpCodes.Ldarg_0), I(OpCodes.Newobj, "Vector3::.ctor"), I(OpCodes.Call, "Minimap::ScreenToWorldPoint"),
                I(OpCodes.Stloc_S, (byte)7),
                I(OpCodes.Ldarg_0), I(OpCodes.Ldloc_S, (byte)7), I(OpCodes.Ldarg_0), I(OpCodes.Call, GetPinInteractRadius),
                I(OpCodes.Call, "Minimap::RemovePin"), I(OpCodes.Pop)
            };

            nullCheckAt = -1;
            for (var copy = 0; copy < lookups; copy++)
            {
                code.AddRange(new[]
                {
                    I(OpCodes.Ldarg_0), I(OpCodes.Newobj, "Vector3::.ctor"), I(OpCodes.Call, "Minimap::ScreenToWorldPoint"),
                    I(OpCodes.Stloc_S, (byte)8)
                });
                if (nullCheckAt < 0) nullCheckAt = code.Count + lookup.Count - 1;
                code.AddRange(lookup);
                code.AddRange(new[]
                {
                    I(OpCodes.Ldloc_S, (byte)9), I(OpCodes.Ldfld, "PinData::m_ownerID"), I(OpCodes.Pop),
                    I(OpCodes.Ldarg_0), I(OpCodes.Ldc_I4_0), I(OpCodes.Call, "Minimap::HidePinTextInput")
                });
            }

            code.AddRange(new[]
            {
                I(OpCodes.Ldstr, "MapZoomOut"), I(OpCodes.Call, "ZInput::GetButtonDown"), I(OpCodes.Brfalse, "label"),
                I(OpCodes.Ret)
            });
            return code;
        }

        // --- The released game ------------------------------------------------------------

        private static void ReleasedPlayerUpdate()
        {
            var decoded = ReleasedMethod("Player", "Update");
            var code = ReleasedTranspilerInput("Player", "Update");
            var site = TranspilerLocals.DerivePlayerUpdateInput(decoded.Method, code, GetDeltaTime, UpdatePlacement);

            // What the transpiler inserted before: ldloc.1, after MatchEndForward(call UpdatePlacement).
            var oldHookAt = Match(code, 0, 1, true, Anchor(Op(OpCodes.Call, UpdatePlacement))) + 1;
            AssertEqual(1, site.Local, "released Player.Update takeInput");
            AssertEqual(oldHookAt, site.HookAt, "released Player.Update hook position");
            AssertEqual(454, site.HookAt, "released Player.Update hook position in the ilspycmd listing");
            AssertSameLoad(OpCodes.Ldloc_1, null, site.Local, "released Player.Update");
            // Local 1 holds TakeInput()'s result and nothing else.
            AssertPositions(new[] { Match(code, 0, 1, true, Anchor(Op(OpCodes.Callvirt, "Character::TakeInput"))) + 1 },
                MatchEnds(code, Anchor(Op(OpCodes.Stloc_1))), "released Player.Update stores into local 1");
        }

        private static void ReleasedPlayerFixedUpdate()
        {
            var decoded = ReleasedMethod("Player", "FixedUpdate");
            var code = ReleasedTranspilerInput("Player", "FixedUpdate");
            var site = TranspilerLocals.DerivePlayerFixedDeltaTime(decoded.Method, code, UpdateStealth);

            // What the transpiler inserted before: ldloc.0, after MatchStartForward(call UpdateStealth).
            var oldHookAt = Match(code, 0, 1, false, Anchor(Op(OpCodes.Call, UpdateStealth))) + 1;
            AssertEqual(0, site.Local, "released Player.FixedUpdate fixedDeltaTime");
            AssertEqual(oldHookAt, site.HookAt, "released Player.FixedUpdate hook position");
            AssertEqual(82, site.HookAt, "released Player.FixedUpdate hook position in the ilspycmd listing");
            AssertSameLoad(OpCodes.Ldloc_0, null, site.Local, "released Player.FixedUpdate");
            // Local 0 holds Time.fixedDeltaTime and nothing else.
            AssertPositions(new[] { 1 }, MatchEnds(code, Anchor(Op(OpCodes.Stloc_0))),
                "released Player.FixedUpdate stores into local 0");
            AssertTrue(Equals(code[0].Operand, "Time::get_fixedDeltaTime"),
                $"released Player.FixedUpdate: local 0 must be stored from Time.fixedDeltaTime, found {code[0]}.");
        }

        private static void ReleasedUpdateMap()
        {
            var decoded = ReleasedMethod("Minimap", "UpdateMap");
            var code = ReleasedTranspilerInput("Minimap", "UpdateMap");
            var site = TranspilerLocals.DeriveGamepadClosestPin(decoded.Method, code, typeof(UnityVector3),
                GetPinInteractRadius, GetClosestPin);

            // What the transpiler inserted before: ldloc.s 8, in front of the first brfalse after
            // MatchEndForward(call GetClosestPin, stloc.s).
            var store = Match(code, 0, 1, true, Anchor(Op(OpCodes.Call, GetClosestPin), Op(OpCodes.Stloc_S)));
            var oldNullCheckAt = Match(code, store, 1, false, Anchor(Op(OpCodes.Brfalse)));
            AssertEqual(8, site.Position, "released Minimap.UpdateMap pos");
            AssertEqual(oldNullCheckAt, site.NullCheckAt, "released Minimap.UpdateMap null check position");
            AssertEqual(248, site.NullCheckAt, "released Minimap.UpdateMap null check position in the ilspycmd listing");
            AssertSameLoad(OpCodes.Ldloc_S, 8, site.Position, "released Minimap.UpdateMap");
            // Local 8 holds the JoyTabLeft branch's screen-centre position, stored right before the
            // lookup, and nothing else.
            var lookup = site.NullCheckAt - 8;
            AssertPositions(new[] { lookup - 1 }, MatchEnds(code, Anchor(Op(OpCodes.Stloc_S, (byte)8))),
                "released Minimap.UpdateMap stores into local 8");
            AssertTrue(Equals(code[lookup - 2].Operand, "Minimap::ScreenToWorldPoint"),
                $"released Minimap.UpdateMap: local 8 must be stored from ScreenToWorldPoint, found {code[lookup - 2]}.");
        }

        private static DecodedMethod ReleasedMethod(string typeName, string methodName)
        {
            var valheim = Environment.GetEnvironmentVariable("VALHEIM_DIR");
            if (string.IsNullOrEmpty(valheim))
                throw new InvalidOperationException("set VALHEIM_DIR for the released game's method bodies");
            return DecodedMethod.Read(System.IO.Path.Combine(valheim, "valheim_Data", "Managed", "assembly_valheim.dll"),
                typeName, methodName);
        }

        /// <summary>
        ///     Requires the load the transpiler now emits for <paramref name="local" /> to be the
        ///     instruction it emitted before, <paramref name="opcode" /> with <paramref name="operand" />.
        /// </summary>
        private static void AssertSameLoad(OpCode opcode, object operand, int local, string what)
        {
            var load = TranspilerLocals.LoadLocal(local);
            AssertTrue(load.OpCode == opcode && Equals(load.Operand, operand),
                $"{what}: the hook must load the local as {opcode.Name} {operand}, the derivation loads it as {load}.");
        }

        /// <summary>Stands in for UnityEngine.Vector3 in the declared local types.</summary>
        private struct UnityVector3
        {
        }
    }
}
