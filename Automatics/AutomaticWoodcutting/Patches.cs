using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticWoodcutting
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TreeBase), "Awake")]
        private static void TreeBase_Awake_Postfix(TreeBase __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachTree(__instance.gameObject);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TreeLog), "Awake")]
        private static void TreeLog_Awake_Postfix(TreeLog __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachTree(__instance.gameObject);
        }

        // Only a tree's stump or a small tree: every other Destructible is left as it is.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Destructible), "Start")]
        private static void Destructible_Start_Postfix(Destructible __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachDestructible(__instance.gameObject, ___m_nview.GetZDO());
        }

        // __state is true only for the call whose prefix opened the swing context, so a melee
        // attack nested inside another neither expands nor clears the outer swing.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static void Attack_DoMeleeAttack_Prefix(Humanoid ___m_character,
            ItemDrop.ItemData ___m_weapon, ref bool __state)
        {
            if (!Module.Running) return;
            if (!Config.EnableAreaWoodcutting) return;
            if (!(___m_character is Player player) || player != Player.m_localPlayer) return;
            if (___m_weapon == null || ___m_weapon.m_shared.m_skillType != Skills.SkillType.Axes)
                return;

            __state = AreaWoodcutting.BeginSwing(player, ___m_weapon);
        }

        // Only a swing that completed expands; the finalizer runs either way.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static void Attack_DoMeleeAttack_Postfix(bool __state)
        {
            if (__state) AreaWoodcutting.Expand();
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static void Attack_DoMeleeAttack_Finalizer(bool __state)
        {
            if (__state) AreaWoodcutting.EndSwing();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.Damage))]
        private static void TreeBase_Damage_Prefix(TreeBase __instance, HitData hit)
        {
            AreaWoodcutting.RecordHit(__instance, hit);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.Damage))]
        private static void TreeLog_Damage_Prefix(TreeLog __instance, HitData hit)
        {
            AreaWoodcutting.RecordHit(__instance, hit);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Damage))]
        private static void Destructible_Damage_Prefix(Destructible __instance, HitData hit)
        {
            AreaWoodcutting.RecordHit(__instance, hit);
        }
    }
}
