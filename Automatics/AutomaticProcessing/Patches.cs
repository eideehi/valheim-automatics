using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
            return new CodeMatcher(instructions)
                .MatchStartForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldloc_1),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(CookingStation), "SetFuel")))
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

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(CookingStation), "UpdateCooking")]
        public static IEnumerable<CodeInstruction> CookingStation_UpdateCooking_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            void InsertCodes(CodeMatcher codeMatcher, object label)
            {
                codeMatcher.Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(CookingStation), "m_nview")),
                    new CodeInstruction(OpCodes.Ldloc_2),
                    new CodeInstruction(OpCodes.Ldloc_S, 6),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CookingStationProcess), "Store")),
                    new CodeInstruction(OpCodes.Brtrue_S, label));
            }

            var matcher = new CodeMatcher(instructions)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldc_I4_2),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(CookingStation), "SetSlot")),
                    new CodeMatch(OpCodes.Br));

            var originalCodes = matcher.Operand;

            InsertCodes(matcher
                    .MatchStartBackwards(
                        new CodeMatch(OpCodes.Ldarg_0),
                        new CodeMatch(OpCodes.Ldfld,
                            AccessTools.Field(typeof(CookingStation), "m_overcookedEffect"))),
                originalCodes);

            InsertCodes(matcher
                    .MatchStartForward(
                        new CodeMatch(OpCodes.Ldarg_0),
                        new CodeMatch(OpCodes.Ldfld,
                            AccessTools.Field(typeof(CookingStation), "m_doneEffect"))),
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

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Fireplace), "UpdateFireplace")]
        public static IEnumerable<CodeInstruction> Fireplace_UpdateFireplace_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            return new CodeMatcher(instructions, generator)
                .End()
                .MatchEndBackwards(
                    new CodeMatch(OpCodes.Ldstr, "fuel"),
                    new CodeMatch(OpCodes.Ldloc_0),
                    new CodeMatch(OpCodes.Callvirt,
                        AccessTools.Method(typeof(ZDO), "Set",
                            new[] { typeof(string), typeof(float) })))
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Fireplace), "m_piece")),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Fireplace), "m_nview")),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(FireplaceProcess), "Refuel")))
                .CreateLabel(out var injectedCodes)
                .MatchEndBackwards(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Fireplace), "IsBurning")),
                    new CodeMatch(OpCodes.Brfalse))
                .SetOperandAndAdvance(injectedCodes)
                .InstructionEnumeration();
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        public static IEnumerable<CodeInstruction> Smelter_UpdateSmelter_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Smelter), "GetFuel")),
                    new CodeMatch(OpCodes.Stloc_3))
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
                    new CodeMatch(OpCodes.Stloc_S))
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Smelter), "m_nview")),
                    new CodeInstruction(OpCodes.Ldloc_1),
                    new CodeInstruction(OpCodes.Ldloc_S, 4),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(SmelterProcess), "QuickCraft")),
                    new CodeInstruction(OpCodes.Stloc_S, 4))
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
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(WispSpawner), "m_spawnPoint")))
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
    }
}