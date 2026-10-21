using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics.Valheim
{
    /// <summary>
    ///     The game side of the requests Automatics sends for containers another game owns (see
    ///     <see cref="ContainerAccess" />). Always applied, whichever modules run: an answer can
    ///     arrive after the module that asked was turned off.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class ContainerRequestPatches
    {
        // An answer to a request of Automatics opens no inventory window and shows no in-use
        // message; the container is written on a later pass once its ownership has arrived.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Container), "RPC_OpenResponse")]
        private static bool Container_RPC_OpenResponse_Prefix(ZNetView ___m_nview, bool granted)
        {
            return !ContainerAccess.TakeRequestReply(___m_nview, granted);
        }

        // The player's own open keeps the game's handling of its answer.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
        private static void Container_Interact_Prefix(ZNetView ___m_nview, bool hold)
        {
            if (!hold) ContainerAccess.ForgetRequest(___m_nview);
        }

        // This game, as the owner, handed the container to another game that asked to open it.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Container), "RPC_RequestOpen")]
        private static void Container_RPC_RequestOpen_Postfix(ZNetView ___m_nview, long uid)
        {
            if (uid != ZNet.GetUID() && ___m_nview && ___m_nview.IsValid() &&
                ___m_nview.GetZDO().GetOwner() == uid)
                ContainerAccess.NoteHandedOver(___m_nview);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static void ZNet_OnDestroy_Postfix()
        {
            ContainerAccess.ClearRequests();
        }
    }
}
