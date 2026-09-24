using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.AutomaticMining
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    public static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Destructible), "Start")]
        private static void Destructible_Awake_Postfix(Destructible __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachMineral(__instance.gameObject);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MineRock), "Start")]
        private static void MineRock_Start_Postfix(MineRock __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachMineral(__instance.gameObject);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MineRock5), "Awake")]
        private static void MineRock5_Awake_Postfix(MineRock5 __instance, ZNetView ___m_nview)
        {
            if (___m_nview && ___m_nview.GetZDO() != null)
                Module.AttachMineral(__instance.gameObject);
        }

        // __state is true only for the call whose prefix opened the swing context, so a melee
        // attack nested inside another neither expands nor clears the outer swing.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static void Attack_DoMeleeAttack_Prefix(Humanoid ___m_character,
            ItemDrop.ItemData ___m_weapon, ref bool __state)
        {
            if (!Module.Running) return;
            if (!Config.EnableAreaMining) return;
            if (!(___m_character is Player player) || player != Player.m_localPlayer) return;
            if (___m_weapon == null || ___m_weapon.m_shared.m_skillType != Skills.SkillType.Pickaxes)
                return;

            __state = AreaMining.BeginSwing(player, ___m_weapon);
        }

        // Only a swing that completed expands; the finalizer runs either way.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static void Attack_DoMeleeAttack_Postfix(bool __state)
        {
            if (__state) AreaMining.Expand();
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static void Attack_DoMeleeAttack_Finalizer(bool __state)
        {
            if (__state) AreaMining.EndSwing();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MineRock5), nameof(MineRock5.Damage))]
        private static void MineRock5_Damage_Prefix(MineRock5 __instance, HitData hit)
        {
            AreaMining.RecordHit(__instance, hit);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MineRock), nameof(MineRock.Damage))]
        private static void MineRock_Damage_Prefix(MineRock __instance, HitData hit)
        {
            AreaMining.RecordHit(__instance, hit);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Damage))]
        private static void Destructible_Damage_Prefix(Destructible __instance, HitData hit)
        {
            AreaMining.RecordHit(__instance, hit);
        }
    }
}
