using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
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
