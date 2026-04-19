using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fish), "Start")]
        private static void Fish_Start_Postfix(Fish __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<FishCache>();
        }

        private static void AddBirdCache(RandomFlyingBird bird, ZNetView zNetView)
        {
            if (zNetView && zNetView.GetZDO() != null && bird.GetComponent<BirdCache>() == null)
                bird.gameObject.AddComponent<BirdCache>();
        }

        [HarmonyPatch]
        internal static class RandomFlyingBird_Initialize_Patch
        {
            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
            {
                return AccessTools.DeclaredMethod(typeof(RandomFlyingBird), "Awake") ??
                       AccessTools.DeclaredMethod(typeof(RandomFlyingBird), "Start");
            }

            [HarmonyPostfix]
            private static void Postfix(RandomFlyingBird __instance, ZNetView ___m_nview)
            {
                AddBirdCache(__instance, ___m_nview);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Ship), "Awake")]
        private static void Ship_Awake_Postfix(Ship __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<ShipCache>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MineRock5), "Awake")]
        private static void MineRock5_Awake_Postfix(MineRock5 __instance)
        {
            var binding = __instance.gameObject.GetComponent<MineRock5CacheBinding>()
                          ?? __instance.gameObject.AddComponent<MineRock5CacheBinding>();
            binding.Initialize(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "Start")]
        private static void Minimap_Start_Postfix()
        {
            IconPack.Initialize();
            Map.OnMinimapStart();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "UpdateMap")]
        private static void Minimap_UpdateMap_Postfix(Player player, float dt, bool takeInput)
        {
            AutomaticMapping.Mapping(player, dt, takeInput);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
        private static bool Minimap_OnMapLeftClick_Prefix(Minimap __instance)
        {
            // Keep this aligned with the transpiler below: navigation selection should short-circuit
            // before the existing save-flag handling in OnMapLeftClick runs.
            return !Navigation.TryHandleMapClick(__instance);
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
        private static IEnumerable<CodeInstruction> Minimap_OnMapLeftClick_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            /*
             *   Vector3 pos = this.ScreenToWorldPoint(ZInput.mousePosition);
             * + if (AutomaticMapping.SetSaveFlag(Map.GetClosestPin(pos, this.m_removeRadius * (this.m_largeZoom * 2f)))
             * +   return;
             *   PinData closestPin = this.GetClosestPin(pos, this.m_removeRadius * (this.m_largeZoom * 2f));
             */
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint")),
                    new CodeMatch(OpCodes.Stloc_0))
                .Advance(1)
                .CreateLabel(out var originalCodes)
                .Insert(
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Minimap), "m_removeRadius")),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Minimap), "m_largeZoom")),
                    new CodeInstruction(OpCodes.Ldc_R4, 2f),
                    new CodeInstruction(OpCodes.Mul),
                    new CodeInstruction(OpCodes.Mul),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(AutomaticMapping), "SetSaveFlag")),
                    new CodeInstruction(OpCodes.Brfalse_S, originalCodes),
                    new CodeInstruction(OpCodes.Ret))
                .InstructionEnumeration();
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Minimap), "UpdateMap")]
        private static IEnumerable<CodeInstruction> Minimap_UpdateMap_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            /*
             *   Vector3 pos3 = this.ScreenToWorldPoint(new Vector3(Screen.width / 2, Screen.height / 2));
             *   PinData closestPin = this.GetClosestPin(pos3, this.m_removeRadius * (this.m_largeZoom * 2f));
             *   if (closestPin != null)
             *     if (closestPin.m_ownerID != 0L)
             *       closestPin.m_ownerID = 0L;
             *     else
             *       closestPin.m_checked = !closestPin.m_checked;
             * + else
             * +   AutomaticMapping.SetSaveFlag(Map.GetClosestPin(pos3, this.m_removeRadius * (this.m_largeZoom * 2f));
             */
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Minimap), "GetClosestPin")),
                    new CodeMatch(OpCodes.Stloc_S))
                .MatchStartForward(
                    new CodeMatch(OpCodes.Brfalse))
                .Advance(1)
                .CreateLabel(out var ifPinNotNull)
                .Advance(-1)
                .Insert(
                    new CodeInstruction(OpCodes.Brtrue_S, ifPinNotNull),
                    new CodeInstruction(OpCodes.Ldloc_S, 8),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Minimap), "m_removeRadius")),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Minimap), "m_largeZoom")),
                    new CodeInstruction(OpCodes.Ldc_R4, 2f),
                    new CodeInstruction(OpCodes.Mul),
                    new CodeInstruction(OpCodes.Mul),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(AutomaticMapping), "SetSaveFlag")),
                    new CodeInstruction(OpCodes.Pop),
                    new CodeInstruction(OpCodes.Ldc_I4_0))
                .InstructionEnumeration();
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static IEnumerable<CodeInstruction> Minimap_UpdatePins_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            /*
             *   float size = (pin.m_doubleSize ? (num * 2f) : num);
             * + size = Map.ResizeIcon(pin, size);
             *   pin.m_uiElement.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size);
             * ...
             *   float num2 = (pin.m_doubleSize ? (num * 2f) : num);
             * + num2 = Map.ResizeIcon(pin, num2);
             *   num2 *= 0.8f + Mathf.Sin(Time.time * 5f) * 0.2f;
             */
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldloc_1),
                    new CodeMatch(OpCodes.Ldc_R4, 2f),
                    new CodeMatch(OpCodes.Mul),
                    new CodeMatch(OpCodes.Stloc_S))
                .Repeat(x =>
                {
                    var numIndex = x.Operand;
                    x.Advance(1);
                    x.InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldloc_S, 4),
                        new CodeInstruction(OpCodes.Ldloc_S, numIndex),
                        new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(IconPack), "ResizeIcon")),
                        new CodeInstruction(OpCodes.Stloc_S, numIndex));
                })
                .InstructionEnumeration();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static void Minimap_UpdatePins_Prefix()
        {
            AutomaticMapping.AnimatePins();
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddPin))]
        private static IEnumerable<CodeInstruction> Minimap_AddPin_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            /*
             * - if (!string.IsNullOrEmpty(pinData.m_name)) {
             * + if (!string.IsNullOrEmpty(pinData.m_name) && !Map.IsNameTagHidden(pinData)) {
             *       pinData.m_NamePinData = new PinNameData(pinData);
             *   }
             */
            var matcher = new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldloc_0),
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Minimap.PinData), "m_name")),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(string), nameof(string.IsNullOrEmpty))),
                    new CodeMatch(OpCodes.Brtrue));
            var skipLabel = matcher.Operand;
            matcher.Set(OpCodes.Brtrue_S, skipLabel);
            return matcher
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(IconPack), "IsNameTagHidden")),
                    new CodeInstruction(OpCodes.Brtrue_S, skipLabel))
                .InstructionEnumeration();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddPin))]
        private static void Minimap_AddPin_Postfix(Minimap.PinData __result)
        {
            PinIndex.Track(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "RemovePin", new[] { typeof(Minimap.PinData) })]
        private static void Minimap_RemovePin_Postfix(Minimap.PinData pin)
        {
            if (pin != null)
                AutomaticMapping.OnRemovePin(pin);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "ClearPins")]
        private static void Minimap_ClearPins_Postfix()
        {
            PinIndex.Clear();
        }

        // Clears PinIndex on world unload. Without this, the index keeps
        // PinData refs from the destroyed minimap until the next
        // Minimap.Start rebuild, and Map.ContainsPin (no UI-active filter)
        // could answer queries during the load window from stale state.
        //
        // MonoBehaviour lifecycle methods are optional, so HarmonyPrepare
        // skips the patch when Minimap.OnDestroy is absent rather than
        // crashing at PatchAll.
        [HarmonyPatch]
        internal static class Minimap_OnDestroy_Patch
        {
            [HarmonyPrepare]
            private static bool Prepare(MethodBase original)
            {
                return original != null ||
                       AccessTools.DeclaredMethod(typeof(Minimap), "OnDestroy") != null;
            }

            [HarmonyTargetMethod]
            private static MethodBase TargetMethod()
            {
                return AccessTools.DeclaredMethod(typeof(Minimap), "OnDestroy");
            }

            [HarmonyPostfix]
            private static void Postfix()
            {
                PinIndex.Clear();
                MineRock5Cache.Clear();
            }
        }

        // Snapshot ownerID != 0 pins before vanilla removes them from
        // m_pins, so the postfix can untrack the exact same set.
        private static readonly List<Minimap.PinData> ResetSharedMapDataSnapshot =
            new List<Minimap.PinData>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Minimap), "ResetSharedMapData")]
        private static void Minimap_ResetSharedMapData_Prefix()
        {
            ResetSharedMapDataSnapshot.Clear();
            var pins = Map.GetAllPins();
            for (var i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                if (pin != null && pin.m_ownerID != 0L)
                    ResetSharedMapDataSnapshot.Add(pin);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "ResetSharedMapData")]
        private static void Minimap_ResetSharedMapData_Postfix()
        {
            for (var i = 0; i < ResetSharedMapDataSnapshot.Count; i++)
                PinIndex.Untrack(ResetSharedMapDataSnapshot[i]);
            ResetSharedMapDataSnapshot.Clear();
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(Pickable), "SetPicked")]
        private static IEnumerable<CodeInstruction> Pickable_SetPicked_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            /*
             *   if (!picked)
             *     return;
             * + StaticObjectMapping.OnObjectDestroy(this, this.m_nview);
             *   this.m_nview.Destroy();
             */
            return new CodeMatcher(instructions)
                .End()
                .MatchStartBackwards(
                    new CodeMatch(OpCodes.Callvirt,
                        AccessTools.Method(typeof(ZNetView), "Destroy")))
                .MatchStartBackwards(
                    new CodeMatch(OpCodes.Ldarg_0))
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Pickable), "m_nview")),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(StaticObjectMapping), "OnObjectDestroy")))
                .InstructionEnumeration();
        }

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MineRock5), "DamageArea")]
        private static IEnumerable<CodeInstruction> MineRock5_DamageArea_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            /*
             *   if (this.AllDestroyed()) {
             * +   StaticObjectMapping.OnObjectDestroy(this, this.m_nview);
             *     this.m_nview.Destroy();
             *   }
             *   return true;
             */
            return new CodeMatcher(instructions)
                .End()
                .MatchStartBackwards(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(MineRock5), "m_nview")),
                    new CodeMatch(OpCodes.Callvirt,
                        AccessTools.Method(typeof(ZNetView), "Destroy")))
                .Advance(1)
                .Insert(
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld,
                        AccessTools.Field(typeof(MineRock5), "m_nview")),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(StaticObjectMapping), "OnObjectDestroy")))
                .InstructionEnumeration();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Destructible), "Awake")]
        private static void Destructible_Awake_Postfix(Destructible __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                __instance.m_onDestroyed += () =>
                    StaticObjectMapping.OnObjectDestroy(__instance, ___m_nview);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TeleportWorld), "Awake")]
        private static void TeleportWorld_Awake_Postfix(TeleportWorld __instance,
            ZNetView ___m_nview)
        {
            if (___m_nview == null || ___m_nview.GetZDO() == null) return;

            var portal = __instance.GetComponent<WearNTear>();
            if (portal)
                portal.m_onDestroyed += () =>
                {
                    if (!Config.EnableAutomaticMapping) return;
                    if (!Config.AllowPinningPortal) return;
                    Map.RemovePin(__instance.transform.position);
                };
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Vagon), "Awake")]
        private static void Vagon_Awake_Postfix(Vagon __instance, ZNetView ___m_nview)
        {
            if (___m_nview == null || ___m_nview.GetZDO() == null) return;

            var wearNTear = __instance.GetComponent<WearNTear>();
            if (wearNTear)
                wearNTear.m_onDestroyed += () =>
                    DynamicObjectMapping.OnObjectDestroy(__instance);
        }
    }
}
