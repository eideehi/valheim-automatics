using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    /// <summary>
    ///     The patches reach this module's entry points, and the entry points carry the gates:
    ///     each one with an effect on the map opens with <see cref="Module.Running" />, and each one
    ///     that takes a new object into the activation's ownership with
    ///     <see cref="Module.Discovering" />. The gates sit in the entry points rather than in the
    ///     patch methods because the transpilers inject calls that run outside any patch method.
    ///     The Explore and AddSharedMapData postfixes, and the three that give an object its
    ///     destroy callback, carry the gate themselves.
    ///     The patches that only keep the pin index in step with the minimap - AddPin, RemovePin,
    ///     ClearPins, ResetSharedMapData and OnDestroy - and the Minimap.Start postfix are not
    ///     gated: they change no pin, and the index is rebuilt from the minimap when the module is
    ///     turned on anyway.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        private static readonly FieldInfo EnemyHudHudsField =
            AccessTools.Field(typeof(EnemyHud), "m_huds");

        private static readonly FieldInfo EnemyHudHoverTimerField =
            AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_hoverTimer");

        private static readonly FieldInfo EnemyHudNameField =
            AccessTools.Field(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_name");

        private static readonly PropertyInfo EnemyHudNameTextProperty =
            AccessTools.Property(EnemyHudNameField.FieldType, "text");

        private struct HudHoverState
        {
            public Character Character;
            public object Data;
            public float Timer;
        }

        private static readonly List<HudHoverState> HudHoverStates = new List<HudHoverState>(2);

        private static object GetTargetHudData(EnemyHud enemyHud, Character character)
        {
            if (!character) return null;
            var huds = EnemyHudHudsField.GetValue(enemyHud) as IDictionary;
            return huds != null && huds.Contains(character) ? huds[character] : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnemyHud), "TestShow")]
        private static void EnemyHud_TestShow_Postfix(Character c, ref bool __result)
        {
            if (!__result && (Navigation.GetHudTarget() == c || AimedObjectBar.GetHudTarget() == c))
                __result = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
        private static void EnemyHud_UpdateHuds_Prefix(EnemyHud __instance, Player player,
            out List<HudHoverState> __state)
        {
            __state = null;
            HudHoverStates.Clear();
            var navigation = Navigation.GetHudTarget();
            var aimed = AimedObjectBar.GetHudTarget();
            if (!navigation && !aimed) return;
            __state = HudHoverStates;
            SaveHoverTimer(__instance, player, navigation, __state);
            if (aimed != navigation) SaveHoverTimer(__instance, player, aimed, __state);
        }

        private static void SaveHoverTimer(EnemyHud enemyHud, Player player, Character character,
            List<HudHoverState> states)
        {
            var data = GetTargetHudData(enemyHud, character);
            if (data == null) return;
            states.Add(new HudHoverState
            {
                Character = character,
                Data = data,
                Timer = player && player.GetHoverCreature() == character
                    ? 0f : (float)EnemyHudHoverTimerField.GetValue(data)
            });
            EnemyHudHoverTimerField.SetValue(data, 0f);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
        private static void EnemyHud_UpdateHuds_Postfix(float dt, List<HudHoverState> __state)
        {
            if (__state == null) return;
            foreach (var state in __state)
            {
                // Vanilla updates health, placement and names first. Preserve the natural timer
                // so releasing either target returns its bar to vanilla visibility next frame.
                EnemyHudHoverTimerField.SetValue(state.Data, state.Timer + dt);
                var name = EnemyHudNameField.GetValue(state.Data) as UnityEngine.Object;
                if (!name || !state.Character) continue;
                var meters = Mathf.RoundToInt(Utils.DistanceXZ(
                    Player.m_localPlayer.transform.position, state.Character.transform.position));
                var text = (string)EnemyHudNameTextProperty.GetValue(name);
                EnemyHudNameTextProperty.SetValue(name, text + " " + meters + "m");
            }
            __state.Clear();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fish), "Start")]
        private static void Fish_Start_Postfix(Fish __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachCache<FishCache>(__instance.gameObject);
        }

        private static void AddBirdCache(RandomFlyingBird bird, ZNetView zNetView)
        {
            if (zNetView && zNetView.GetZDO() != null)
                Module.AttachCache<BirdCache>(bird.gameObject);
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
                Module.AttachCache<ShipCache>(__instance.gameObject);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MineRock5), "Awake")]
        private static void MineRock5_Awake_Postfix(MineRock5 __instance, ZNetView ___m_nview)
        {
            // The same test as the other attach postfixes. A rock without a ZDO is never pinned,
            // and an inactive template another mod keeps in the scene must not carry a binding
            // that every rock spawned from it would copy.
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachMineRock5(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "Start")]
        private static void Minimap_Start_Postfix(Minimap __instance)
        {
            IconPack.EnsureAttached(__instance);
            Map.Attach(__instance);
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
            // Local 0 is `pos` (Vector3). The slot stays hardcoded, but the anchor
            // requires the ScreenToWorldPoint result to be stored into local 0, so a
            // layout change breaks the match and ThrowIfInvalid reports it at patch time.
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint")),
                    new CodeMatch(OpCodes.Stloc_0))
                .ThrowIfInvalid(
                    "Minimap.OnMapLeftClick: the ScreenToWorldPoint call storing into local 0 (pos) was not found")
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
            // pos3 is local 8 (verified against the vanilla Minimap.UpdateMap IL). It is
            // hardcoded rather than captured dynamically because UpdateMap has several
            // ScreenToWorldPoint stores (the JoyTab branches), so a naive "first
            // ScreenToWorldPoint + Stloc_S" capture grabs the RemoveMap branch's local 7,
            // not the GetClosestPin branch's pos3. The ThrowIfInvalid guards below still
            // make a layout change fail loudly at patch time instead of silently.
            return new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(Minimap), "GetClosestPin")),
                    new CodeMatch(OpCodes.Stloc_S))
                .ThrowIfInvalid("Could not find Minimap.UpdateMap GetClosestPin assignment.")
                .MatchStartForward(
                    new CodeMatch(OpCodes.Brfalse))
                .ThrowIfInvalid("Could not find Minimap.UpdateMap closest-pin null check.")
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
             *   if (Map.ShouldHideAutomaticPin(pin) || !IsPointVisible(pin.m_pos, rawImage) || ...) {
             *       DestroyPinMarker(pin);
             *       continue;
             *   }
             *
             *   float size = (pin.m_doubleSize ? (num * 2f) : num);
             * + size = IconPack.ResizeIcon(pin, size);
             *   pin.m_uiElement.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size);
             * ...
             *   float num2 = (pin.m_doubleSize ? (num * 2f) : num);
             * + num2 = IconPack.ResizeIcon(pin, num2);
             *   num2 *= 0.8f + Mathf.Sin(Time.time * 5f) * 0.2f;
             */
            const int pinDataLocalIndex = 6;
            var destroyPinMarker = AccessTools.Method(typeof(Minimap), "DestroyPinMarker");
            var matcher = new CodeMatcher(instructions, generator)
                .MatchStartForward(
                    new CodeMatch(OpCodes.Ldarg_0),
                    new CodeMatch(x => IsLocal(x, OpCodes.Ldloc_S, pinDataLocalIndex)),
                    new CodeMatch(OpCodes.Call, destroyPinMarker))
                .ThrowIfInvalid("Could not find Minimap.UpdatePins DestroyPinMarker branch.")
                .CreateLabel(out var destroyHiddenPinLabel)
                .Start()
                .MatchEndForward(new CodeMatch(x =>
                    IsLocal(x, OpCodes.Stloc_S, pinDataLocalIndex)))
                .ThrowIfInvalid("Could not find Minimap.UpdatePins pin local assignment.")
                .Advance(1)
                .InsertAndAdvance(
                    new CodeInstruction(OpCodes.Ldloc_S, pinDataLocalIndex),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(Map), nameof(Map.ShouldHideAutomaticPin))),
                    new CodeInstruction(OpCodes.Brtrue_S, destroyHiddenPinLabel));

            // Repeat does nothing, and says nothing, when its first match fails, so the check
            // sits before it: at least one size computation must be found.
            return matcher
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldloc_3),
                    new CodeMatch(OpCodes.Ldc_R4, 2f),
                    new CodeMatch(OpCodes.Mul),
                    new CodeMatch(OpCodes.Stloc_S))
                .ThrowIfInvalid(
                    "Minimap.UpdatePins: the doubled pin size (local 3 * 2) stored into a local was not found")
                .Repeat(x =>
                {
                    var numIndex = x.Operand;
                    x.Advance(1);
                    x.InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldloc_S, 6),
                        new CodeInstruction(OpCodes.Ldloc_S, numIndex),
                        new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(IconPack), "ResizeIcon")),
                        new CodeInstruction(OpCodes.Stloc_S, numIndex));
                })
                .InstructionEnumeration();
        }

        private static bool IsLocal(CodeInstruction instruction, OpCode opcode, int index)
        {
            if (instruction.opcode != opcode) return false;
            switch (instruction.operand)
            {
                case LocalBuilder local:
                    return local.LocalIndex == index;
                case int localIndex:
                    return localIndex == index;
                case byte localIndex:
                    return localIndex == index;
                case sbyte localIndex:
                    return localIndex == index;
                default:
                    return false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static void Minimap_UpdatePins_Prefix()
        {
            AutomaticMapping.AnimatePins();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "UpdatePins")]
        private static void Minimap_UpdatePins_Postfix(Minimap __instance)
        {
            MapFilter.Update(__instance);
            PinTooltip.Update(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.InTextInput))]
        private static void Minimap_InTextInput_Postfix(ref bool __result)
        {
            __result |= MapFilter.InTextInput;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "Update")]
        private static void Minimap_Update_Postfix(Minimap __instance)
        {
            MapFilter.UpdateInput();
            PinTooltip.Update(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "OnLanguageChange")]
        private static void Minimap_OnLanguageChange_Postfix()
        {
            MapFilter.OnLanguageChange();
            PinTooltip.Invalidate();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
        private static void Minimap_SetMapMode_Postfix(Minimap __instance)
        {
            MapFilter.Update(__instance);
            PinTooltip.Update(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "Explore", new[] { typeof(Vector3), typeof(float) })]
        private static void Minimap_Explore_Postfix()
        {
            if (!Module.Running) return;
            if (Config.HideUnexploredAutomaticMappingPins)
                Map.RefreshPins();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.AddSharedMapData))]
        private static void Minimap_AddSharedMapData_Postfix(bool __result)
        {
            if (!Module.Running) return;
            if (__result && Config.HideUnexploredAutomaticMappingPins)
                Map.RefreshPins();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.SaveMapData))]
        private static void Minimap_SaveMapData_Prefix()
        {
            DynamicObjectMapping.FlushVehiclePins();
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
            // Local 0 is `pinData`. The slot stays hardcoded, but the anchor requires local 0
            // to be the pin whose m_name is tested, so a layout change breaks the match and
            // ThrowIfInvalid reports it at patch time.
            var matcher = new CodeMatcher(instructions, generator)
                .MatchEndForward(
                    new CodeMatch(OpCodes.Ldloc_0),
                    new CodeMatch(OpCodes.Ldfld,
                        AccessTools.Field(typeof(Minimap.PinData), "m_name")),
                    new CodeMatch(OpCodes.Call,
                        AccessTools.Method(typeof(string), nameof(string.IsNullOrEmpty))),
                    new CodeMatch(OpCodes.Brtrue))
                .ThrowIfInvalid(
                    "Minimap.AddPin: the IsNullOrEmpty check on local 0's (pinData) m_name was not found");
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
            {
                AutomaticMapping.OnRemovePin(pin);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Minimap), "ClearPins")]
        private static void Minimap_ClearPins_Postfix()
        {
            Map.ClearAutomaticPins();
            PinIndex.Clear();
            // Drain the module caches too: ClearPins destroys the markers without
            // calling Minimap.RemovePin per pin, so the per-pin OnRemovePin heal
            // never fires and these caches would otherwise keep orphaned PinData
            // that permanently suppresses re-pinning for the rest of the session.
            DynamicObjectMapping.Cleanup();
            StaticObjectMapping.Cleanup();
            MapFilter.Cleanup();
            PinTooltip.Cleanup();
            PinCategories.Cleanup();
            Navigation.ClearTarget();
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
                Map.ClearAutomaticPins();
                Navigation.Cleanup();
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
                .ThrowIfInvalid(
                    "Pickable.SetPicked: the ZNetView.Destroy call was not found")
                .MatchStartBackwards(
                    new CodeMatch(OpCodes.Ldarg_0))
                .ThrowIfInvalid(
                    "Pickable.SetPicked: the Ldarg_0 before the ZNetView.Destroy call was not found")
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
                .ThrowIfInvalid(
                    "MineRock5.DamageArea: the m_nview.Destroy() call was not found")
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
        // The three postfixes below give a new object its destroy callback. Each takes the object
        // into the activation's ownership, so they gate on discovery like the attach paths do.
        private static void Destructible_Awake_Postfix(Destructible __instance, ZNetView ___m_nview)
        {
            if (!Module.Discovering) return;
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.HookStaticObjectDestroy(__instance, ___m_nview);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TeleportWorld), "Awake")]
        private static void TeleportWorld_Awake_Postfix(TeleportWorld __instance,
            ZNetView ___m_nview)
        {
            if (!Module.Discovering) return;
            if (___m_nview == null || ___m_nview.GetZDO() == null) return;

            Module.HookPortalDestroy(__instance, ___m_nview);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Vagon), "Awake")]
        private static void Vagon_Awake_Postfix(Vagon __instance, ZNetView ___m_nview)
        {
            if (!Module.Discovering) return;
            if (___m_nview == null || ___m_nview.GetZDO() == null) return;

            Module.HookVehicleDestroy(__instance);
        }
    }
}
