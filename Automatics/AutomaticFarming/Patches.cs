using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
        internal static void Player_TryPlacePiece_Postfix(Player __instance, Piece piece,
            bool __result, GameObject ___m_placementGhost)
        {
            if (!Module.Running) return;
            if (!___m_placementGhost) return;
            GridPlanting.RecordPlacement(__instance, piece, __result,
                ___m_placementGhost.transform.position, ___m_placementGhost.transform.rotation,
                GridPlanting.IsModifierHeld(__instance, Config.GridPlantingKey));
        }

        // Lattice snapping opens its context here and acts in the ray test below, the one call in
        // this method that PieceRayTest has; vanilla then evaluates the placement status on the
        // snapped pose. __state is true only for the update that opened the context.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        internal static void Player_UpdatePlacementGhost_Prefix(Player __instance, ref bool __state)
        {
            if (!Module.Running) return;
            __state = LatticeSnapping.BeginGhostUpdate(__instance,
                GridPlanting.IsModifierHeld(__instance, Config.GridPlantingKey));
        }

        // Acts only inside a context the gated prefix opened in the same call.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "PieceRayTest")]
        private static void Player_PieceRayTest_Postfix(Player __instance, bool __result, ref Vector3 point,
            ref Vector3 normal, ref Piece piece, ref Heightmap heightmap, ref Collider waterSurface, bool water)
        {
            if (__result)
                LatticeSnapping.SnapRayResult(__instance, ref point, ref normal, ref piece, ref heightmap,
                    ref waterSurface, water);
        }

        // Runs as vanilla tints the ghost from its final status at the end of UpdatePlacementGhost,
        // so a snapped update without room to grow turns red the way any invalid placement does.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "SetPlacementGhostValid")]
        private static void Player_SetPlacementGhostValid_Prefix(Player __instance, ref bool valid)
        {
            if (valid && LatticeSnapping.RefuseWithoutRoom(__instance)) valid = false;
        }

        // Runs after a stop request too: the player's rotation step borrowed by a snapped update is
        // always put back, even when the update threw.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        private static void Player_UpdatePlacementGhost_Finalizer(bool __state)
        {
            if (__state) LatticeSnapping.EndGhostUpdate();
        }

        // Container.GetHoverText returns an already-localized string, so the
        // appended role line is localized here too (matching the Pickable hover
        // hints in Automatic Pickup). The role is read from the replicated ZDO, so
        // a designation set by another peer is visible without owning the chest.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Container), "GetHoverText")]
        private static void Container_GetHoverText_Postfix(Container __instance, ref string __result)
        {
            // Reachable after the module stopped: in the frames between the stop request and the
            // teardown, and for the rest of the session on a Harmony build that cannot remove
            // patches. Returning here leaves the game's hover text as it is.
            if (!Module.Running) return;
            if (!Config.EnableAutomaticFarming) return;

            var keyBound = Config.DesignateContainerKey.MainKey != KeyCode.None;
            var role = ContainerRoles.GetRole(__instance);

            // Nothing to surface: no way to designate (key unset) and no role to
            // show, so leave the vanilla hover text untouched.
            if (!keyBound && role == ContainerRole.None) return;

            var roleLabel = Automatics.L10N.Localize(ContainerRoles.RoleLabelKey(role));
            if (keyBound)
            {
                var keyCode = Config.DesignateContainerKey.ToString();
                var hint = Automatics.L10N.Localize(
                    "@message_automatic_farming_designate_container_hint", roleLabel);
                __result += $"\n[<color=yellow><b>{keyCode}</b></color>] {hint}";
            }
            else
            {
                __result += "\n" + Automatics.L10N.Localize(
                    "@message_automatic_farming_container_role_status", roleLabel);
            }
        }
    }
}
