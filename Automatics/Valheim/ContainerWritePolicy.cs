namespace Automatics.Valheim
{
    /// <summary>What the container gate does next for one container.</summary>
    internal enum ContainerWriteStep
    {
        /// <summary>Leave the container alone this time.</summary>
        Skip,

        /// <summary>Take ownership of the container, then decide again.</summary>
        Claim,

        /// <summary>Load the container's saved contents into its inventory, then decide again.</summary>
        Load,

        /// <summary>The container may be written now.</summary>
        Write,

        /// <summary>Ask the game that owns the container for it, and write it later.</summary>
        Request
    }

    /// <summary>
    ///     What the container gate decides on, read from the game right before each decision.
    /// </summary>
    internal struct ContainerWriteState
    {
        /// <summary>The local game owns the container's ZNetView.</summary>
        public bool Owned;

        /// <summary>Some game owns the container's ZNetView.</summary>
        public bool HasOwner;

        /// <summary>The local game is the server: single-player, a host or a dedicated server.</summary>
        public bool IsServer;

        /// <summary>The write is made for a local player.</summary>
        public bool LocalPlayer;

        /// <summary>The container's own in-use flag, which only its owner sets.</summary>
        public bool InUseLocal;

        /// <summary>The in-use flag the owner shares through the ZDO.</summary>
        public bool InUseReplicated;

        /// <summary>The cart the container sits on is in use.</summary>
        public bool CartInUse;

        /// <summary>The container uses the ZNetView of the cart or ship it belongs to.</summary>
        public bool BorrowedView;

        /// <summary>The container's saved contents were loaded since the gate started.</summary>
        public bool Loaded;
    }

    /// <summary>The game side of the container gate for one container.</summary>
    internal interface IContainerWriteTarget
    {
        ContainerWriteState Read(bool loaded);

        void Claim();

        /// <summary>Loads the saved contents; false when they could not be loaded.</summary>
        bool Load();
    }

    /// <summary>
    ///     The rules for writing a container (adding to it, taking from it or changing its saved
    ///     state). The local game writes only a container it owns, that nobody has open, whose
    ///     cart is idle and whose saved contents were loaded right before the write was allowed,
    ///     because the game saves the whole inventory on every change and would otherwise save a
    ///     stale or empty copy over what is stored. Only the server takes ownership of a container
    ///     nobody owns; a client waits for the server to assign it. A container owned by another
    ///     game is asked for (its owner checks use and access and hands it over, as when a player
    ///     opens it) and written on a later pass once this game owns it; a cart's or ship's
    ///     container is never taken or asked for. Without a local player only the server writes
    ///     and nothing is asked for; which containers it may use then (privacy) is decided by the
    ///     access checks before the gate.
    /// </summary>
    internal static class ContainerWritePolicy
    {
        public static ContainerWriteStep Decide(ContainerWriteState state)
        {
            // A client between characters (loading, dead, respawning) has nobody to act for; the
            // server keeps working its own containers, as single-player always has.
            if (!state.LocalPlayer && !state.IsServer) return ContainerWriteStep.Skip;

            // The owner keeps the in-use flag; every other game reads the copy the owner shares.
            if (state.Owned ? state.InUseLocal : state.InUseReplicated)
                return ContainerWriteStep.Skip;
            if (state.CartInUse) return ContainerWriteStep.Skip;

            if (!state.Owned)
            {
                if (state.BorrowedView) return ContainerWriteStep.Skip;
                // Without a player the server would get it only to give it straight back: the
                // server hands a container near a player's character to that player's game.
                if (state.HasOwner)
                    return state.LocalPlayer ? ContainerWriteStep.Request : ContainerWriteStep.Skip;
                return state.IsServer ? ContainerWriteStep.Claim : ContainerWriteStep.Skip;
            }

            return state.Loaded ? ContainerWriteStep.Write : ContainerWriteStep.Load;
        }

        /// <summary>
        ///     Claims and loads <paramref name="target" /> as <see cref="Decide" /> says, each at
        ///     most once, reading the state again after each step. Write when the last decision,
        ///     made right after the load, allows the write; Request when another game owns the
        ///     container and the caller may ask for it; Skip otherwise.
        /// </summary>
        public static ContainerWriteStep TryAcquire<TTarget>(TTarget target)
            where TTarget : IContainerWriteTarget
        {
            var claimed = false;
            var loaded = false;
            while (true)
                switch (Decide(target.Read(loaded)))
                {
                    case ContainerWriteStep.Write:
                        return ContainerWriteStep.Write;
                    case ContainerWriteStep.Request:
                        return ContainerWriteStep.Request;
                    case ContainerWriteStep.Claim when !claimed:
                        claimed = true;
                        target.Claim();
                        break;
                    case ContainerWriteStep.Load when !loaded:
                        if (!target.Load()) return ContainerWriteStep.Skip;
                        loaded = true;
                        break;
                    default:
                        return ContainerWriteStep.Skip;
                }
        }
    }
}
