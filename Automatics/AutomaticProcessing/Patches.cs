using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Automatics.AutomaticProcessing
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Beehive), "IncreseLevel")]
        private static bool Beehive_IncreseLevel_Prefix(Beehive __instance, ZNetView ___m_nview,
            int i)
        {
            return !BeehiveProcess.Store(__instance, ___m_nview, i);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CookingStation), "UpdateFuel")]
        public static IEnumerable<CodeInstruction> CookingStation_UpdateFuel_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            /*
             *   if (fuel < 0.0)
             *     fuel = 0.0f;
             * + fuel = CookingStationProcess.Refuel(this, this.m_nview, fuel);
             *   this.SetFuel(fuel);
             */
            // Local 1 is `fuel` (float). CookingStation.UpdateFuel declares
            // `.locals init ([0] float32, [1] float32)` in 1.0.7: slot 0 is the
            // per-frame fuel burn (dt / m_secPerFuel), slot 1 is the value read
            // from GetFuel and handed to SetFuel. ADR-0001 asks for the local to
            // be resolved by type, but System.Single is not unique here, so
            // GetSingleLocalIndex would throw on a method it can never resolve.
            // Instead the slot is pinned by the match itself: the anchor below
            // requires Ldloc_1 to be the value flowing into SetFuel, so a layout
            // change breaks the match and ThrowIfInvalid reports it at patch
            // time rather than emitting IL that reads the wrong local.
            return new CodeMatcher(instructions)
                .MatchStartForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldloc_1),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(CookingStation), "SetFuel")))
                .ThrowIfInvalid(
                    "CookingStation.UpdateFuel: the SetFuel call taking local 1 (fuel) was not found")
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(CookingStation), "m_nview")),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CookingStationProcess), "Refuel")),
                    new CodeInstruction(OpCodes.Stloc_1),
                    new CodeInstruction(OpCodes.Ldarg_0))
                .InstructionEnumeration();
        }

        /// <summary>
        ///     Returns the index of the only local variable of <paramref name="localType" />
        ///     declared by <paramref name="original" />.
        /// </summary>
        /// <remarks>
        ///     Local slot numbers are not part of a method's public contract and shift whenever
        ///     the game adds or removes a local, so they must never be hardcoded in a transpiler.
        ///     Resolving them by type makes a future layout change fail loudly here instead of
        ///     emitting IL that pushes the wrong stack type.
        /// </remarks>
        private static int GetSingleLocalIndex(MethodBase original, Type localType)
        {
            var body = original?.GetMethodBody();
            if (body == null)
                throw new InvalidOperationException(
                    $"{original?.Name ?? "<unknown>"}: method body is unavailable, " +
                    $"cannot resolve the {localType.Name} local");

            var indexes = body.LocalVariables
                .Where(x => x.LocalType == localType)
                .Select(x => x.LocalIndex)
                .ToList();
            if (indexes.Count != 1)
                throw new InvalidOperationException(
                    $"{original.Name}: expected exactly one {localType.Name} local, " +
                    $"found {indexes.Count}");

            return indexes[0];
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CookingStation), "UpdateCooking")]
        public static IEnumerable<CodeInstruction> CookingStation_UpdateCooking_Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            /*
             *   ItemConversion itemConversion = this.GetItemConversion(itemName);
             *   ...
             * + if (!CookingStationProcess.Store(this, this.m_nview, slot, itemConversion))
             *   this.SetSlot(slot, this.m_overCookedItem.name, cookedTime, CookingStation.Status.Burnt, cheated);
             *   ...
             * + if (!CookingStationProcess.Store(this, this.m_nview, slot, itemConversion))
             *   this.SetSlot(slot, itemConversion.m_to.name, cookedTime, CookingStation.Status.Done, cheated);
             *
             *   Both injections jump to the loop-continue target shared by every SetSlot branch
             *   in the slot loop when CookingStationProcess.Store handled the item.
             */
            var slotIndex = GetSingleLocalIndex(original, typeof(int));
            var itemConversionIndex =
                GetSingleLocalIndex(original, typeof(CookingStation.ItemConversion));

            void InsertCodes(CodeMatcher codeMatcher, object label)
            {
                codeMatcher.Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(CookingStation), "m_nview")),
                    new CodeInstruction(OpCodes.Ldloc_S, slotIndex),
                    new CodeInstruction(OpCodes.Ldloc_S, itemConversionIndex),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CookingStationProcess), "Store")),
                    new CodeInstruction(OpCodes.Brtrue_S, label));
            }

            // The burnt-item SetSlot call is anchored through the m_overCookedItem load that
            // supplies its itemName argument, then through the branch that follows the call.
            // Anchoring on the Status argument is not stable: SetSlot gained a trailing
            // `cheated` argument, so the status constant is no longer adjacent to the call.
            var matcher = new CodeMatcher(instructions)
                .MatchStartForward(
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(CookingStation), "m_overCookedItem")))
                .ThrowIfInvalid(
                    "CookingStation.UpdateCooking: CookingStation.m_overCookedItem load not found")
                .MatchStartForward(
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(CookingStation), "SetSlot")),
                    new CodeMatch(x => x.opcode == OpCodes.Br || x.opcode == OpCodes.Br_S))
                .ThrowIfInvalid(
                    "CookingStation.UpdateCooking: the burnt item SetSlot call followed by a branch to the slot loop continuation was not found")
                .Advance(1);

            var originalCodes = matcher.Operand;

            InsertCodes(matcher
                    .MatchStartBackwards(
                        new CodeMatch(OpCodes.Ldarg_0),
                        new CodeMatch(OpCodes.Ldfld,
                            AccessTools.Field(typeof(CookingStation), "m_overcookedEffect")))
                    .ThrowIfInvalid(
                        "CookingStation.UpdateCooking: CookingStation.m_overcookedEffect load not found"),
                originalCodes);

            InsertCodes(matcher
                    .MatchStartForward(
                        new CodeMatch(OpCodes.Ldarg_0),
                        new CodeMatch(OpCodes.Ldfld,
                            AccessTools.Field(typeof(CookingStation), "m_doneEffect")))
                    .ThrowIfInvalid(
                        "CookingStation.UpdateCooking: CookingStation.m_doneEffect load not found"),
                originalCodes);

            return matcher.InstructionEnumeration();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CookingStation), "UpdateCooking")]
        private static void CookingStation_UpdateCooking_Postfix(CookingStation __instance,
            ZNetView ___m_nview)
        {
            CookingStationProcess.Craft(__instance, ___m_nview);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fermenter), "SlowUpdate")]
        private static void Fermenter_SlowUpdate_Postfix(Fermenter __instance, ZNetView ___m_nview)
        {
            FermenterProcess.Store(__instance, ___m_nview);
            FermenterProcess.Craft(__instance, ___m_nview);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fireplace), "UpdateFireplace")]
        private static void Fireplace_UpdateFireplace_Postfix(Fireplace __instance,
            Piece ___m_piece, ZNetView ___m_nview)
        {
            FireplaceProcess.Refuel(__instance, ___m_piece, ___m_nview);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        public static IEnumerable<CodeInstruction> Smelter_UpdateSmelter_Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            /*
             *   while (accumulator >= 1f) {
             *     accumulator -= 1f;
             *     float fuel = this.GetFuel();
             * +   fuel = SmelterProcess.QuickRefuel(this, this.m_nview, accumulator, fuel);
             *     string queuedOre = this.GetQueuedOre();
             * +   queuedOre = SmelterProcess.QuickCraft(this, this.m_nview, accumulator, queuedOre);
             */
            // Smelter.UpdateSmelter declares in 1.0.7:
            //   [0] float64  deltaTime
            //   [1] float32  accumulator
            //   [2] float32  windmill power output
            //   [3] float32  fuel
            //   [4] string   queuedOre
            //   [5..7] float32  scratch (num2, num3, bakeTimer)
            // Only `queuedOre` has a type that is unique among those locals, so
            // it is the only one ADR-0001's by-type resolution can handle here.
            // The two float locals stay pinned by their anchors instead:
            // `fuel` is required to be the destination of the store that follows
            // GetFuel, and a layout change therefore breaks the match and is
            // reported by ThrowIfInvalid at patch time. `accumulator` (local 1)
            // has no such anchor and remains a hardcoded slot; see F2/ADR-0001.
            var queuedOreIndex = GetSingleLocalIndex(original, typeof(string));

            return new CodeMatcher(instructions)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Smelter), "GetFuel")),
                    new CodeMatch(OpCodes.Stloc_3))
                .ThrowIfInvalid(
                    "Smelter.UpdateSmelter: the GetFuel call storing into local 3 (fuel) was not found")
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Smelter), "m_nview")),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    new CodeInstruction(OpCodes.Ldloc_3),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(SmelterProcess), "QuickRefuel")),
                    new CodeInstruction(OpCodes.Stloc_3))
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Smelter), "GetQueuedOre")),
                    new CodeMatch(x => x.IsStloc()))
                .ThrowIfInvalid(
                    "Smelter.UpdateSmelter: the GetQueuedOre call storing into a local was not found")
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Smelter), "m_nview")),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    new CodeInstruction(OpCodes.Ldloc_S, queuedOreIndex),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(SmelterProcess), "QuickCraft")),
                    new CodeInstruction(OpCodes.Stloc_S, queuedOreIndex))
                .InstructionEnumeration();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        private static void Smelter_UpdateSmelter_Postfix(Smelter __instance, ZNetView ___m_nview)
        {
            SmelterProcess.Craft(__instance, ___m_nview);
            SmelterProcess.Refuel(__instance, ___m_nview);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Smelter), "Spawn")]
        private static bool Smelter_Spawn_Prefix(Smelter __instance, string ore, int stack)
        {
            return !SmelterProcess.Store(__instance, ore, stack);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SapCollector), "IncreseLevel")]
        private static bool SapCollector_IncreseLevel_Prefix(SapCollector __instance,
            ZNetView ___m_nview,
            int i)
        {
            return !SapCollectorProcess.Store(__instance, ___m_nview, i);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(WispSpawner), "TrySpawn")]
        public static IEnumerable<CodeInstruction> WispSpawner_TrySpawn_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            /*
             *   Vector3 position = this.m_spawnPoint.position + Quaternion.Euler(0f, UnityEngine.Random.Range(0, 360), 0f) * Vector3.forward * this.m_spawnDistance;
             * + if (WispSpawnerProcess.Store(this, this.m_nview)) return;
             *   UnityEngine.Object.Instantiate(this.m_wispPrefab, position, Quaternion.identity);
             *   this.m_nview.GetZDO().Set("LastSpawn", ZNet.instance.GetTime().Ticks);
             *   TODO: If other mods add code to this location, if the return value of WispSpawnerProcess.Store is true, the code added by other mods will no longer be called.
             */
            // No IL local is referenced here: the injection is anchored purely on
            // the `this.m_spawnPoint` field load that opens the spawn-position
            // statement, and the original Ldarg_0 of that load is reused as the
            // first argument of WispSpawnerProcess.Store (the inserted Ldarg_0
            // below replaces it for the field load and carries the skip label).
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(WispSpawner), "m_spawnPoint")))
                .ThrowIfInvalid(
                    "WispSpawner.TrySpawn: WispSpawner.m_spawnPoint load not found")
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0))
                .CreateLabel(out var originalCodes)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(WispSpawner), "m_nview")),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(WispSpawnerProcess), "Store")),
                    new CodeInstruction(OpCodes.Brfalse_S, originalCodes),
                    new CodeInstruction(OpCodes.Ret))
                .Instructions();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Turret), "UpdateAttack")]
        private static void Turret_UpdateAttack_Postfix(Turret __instance, ZNetView ___m_nview, float dt)
        {
            TurretProcess.Charge(__instance, ___m_nview, dt);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Turret), "OnDestroyed")]
        private static void Turret_OnDestroyed_Postfix(Turret __instance)
        {
            TurretProcess.ClearTimer(__instance);
        }
    }
}