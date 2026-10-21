using Automatics.Valheim;
using ModUtils;

namespace Automatics.AutomaticFarming
{
    /// <summary>
    /// Per-container designation that decides how container-based farming treats a
    /// chest, named by farming purpose rather than by the item type it stores:
    /// <list type="bullet">
    /// <item><see cref="Cultivation"/> plants the seeds it holds to grow crops.</item>
    /// <item><see cref="SeedHarvest"/> plants the crops it holds to grow seeds.</item>
    /// <item><see cref="Storage"/> never plants; it is the priority destination for
    /// any harvested yield.</item>
    /// </list>
    /// Naming by purpose removes the ambiguity of seed/crop naming: growing crops
    /// plants seeds and harvests crops, while multiplying seeds plants crops and
    /// harvests seeds, so the same item type is an input in one box and a yield in
    /// the other.
    /// </summary>
    internal enum ContainerRole
    {
        None = 0,
        Cultivation = 1,
        SeedHarvest = 2,
        Storage = 3
    }

    /// <summary>
    /// Reads and writes a container's <see cref="ContainerRole"/>. The role lives in
    /// the container's ZDO under a stable custom key, so it persists with the save
    /// and replicates to peers the same way Automatic Processing stores its custom
    /// per-object state. Reads work for any loaded container (replicated ZDO);
    /// writes are owner-side and pass the same gate as any container mutation,
    /// on explicit designation only.
    /// </summary>
    internal static class ContainerRoles
    {
        // Stable custom ZDO key for the role. Custom ZDO keys persist across
        // save/reload and replicate to peers; never rename this without a
        // migration or existing designations would be lost.
        private const string RoleZdoKey = "automatics_farming_container_role";

        /// <summary>
        /// Role of <paramref name="container"/>, or <see cref="ContainerRole.None"/>
        /// when it has no valid ZDO. Safe to call for a container owned by another
        /// peer: reading a replicated ZDO does not mutate or claim it.
        /// </summary>
        public static ContainerRole GetRole(Container container)
        {
            if (container == null) return ContainerRole.None;
            if (!Objects.GetZNetView(container, out var nview)) return ContainerRole.None;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
                return ContainerRole.None;

            return Normalize(nview.GetZDO().GetInt(RoleZdoKey));
        }

        /// <summary>
        /// Advances <paramref name="container"/>'s role
        /// (<see cref="ContainerRole.None"/> → <see cref="ContainerRole.Cultivation"/> →
        /// <see cref="ContainerRole.SeedHarvest"/> → <see cref="ContainerRole.Storage"/> →
        /// <see cref="ContainerRole.None"/>) and persists it, returning the new role
        /// in <paramref name="role"/>. Returns
        /// false without writing when <paramref name="player"/> cannot access the
        /// container (ward/in-use) or the local game may not write it (another
        /// game owns it), so a chest a peer or a dedicated server holds is never
        /// silently modified.
        /// </summary>
        public static bool TryCycleRole(Player player, Container container, out ContainerRole role)
        {
            role = ContainerRole.None;
            if (player == null || container == null) return false;

            // Same gate used before any container mutation: usable (not in use by a
            // peer), within build access, and owned by this game or, on the server,
            // owned by nobody and claimed now.
            if (!ContainerAccess.TryPrepareContainerMutation(player, container)) return false;
            if (!Objects.GetZNetView(container, out var nview)) return false;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null) return false;
            if (!nview.IsOwner()) return false;

            role = Next(Normalize(nview.GetZDO().GetInt(RoleZdoKey)));
            nview.GetZDO().Set(RoleZdoKey, (int)role);
            return true;
        }

        /// <summary>Localization key for <paramref name="role"/>'s display label.</summary>
        public static string RoleLabelKey(ContainerRole role)
        {
            switch (role)
            {
                case ContainerRole.Cultivation: return "@message_automatic_farming_role_cultivation";
                case ContainerRole.SeedHarvest: return "@message_automatic_farming_role_seedharvest";
                case ContainerRole.Storage: return "@message_automatic_farming_role_storage";
                default: return "@message_automatic_farming_role_none";
            }
        }

        private static ContainerRole Next(ContainerRole role)
        {
            switch (role)
            {
                case ContainerRole.None: return ContainerRole.Cultivation;
                case ContainerRole.Cultivation: return ContainerRole.SeedHarvest;
                case ContainerRole.SeedHarvest: return ContainerRole.Storage;
                default: return ContainerRole.None;
            }
        }

        // Any unexpected stored value (e.g. from a future version) reads as None
        // rather than throwing, so an unknown role never breaks the hover or cycle.
        private static ContainerRole Normalize(int value)
        {
            switch (value)
            {
                case (int)ContainerRole.Cultivation: return ContainerRole.Cultivation;
                case (int)ContainerRole.SeedHarvest: return ContainerRole.SeedHarvest;
                case (int)ContainerRole.Storage: return ContainerRole.Storage;
                default: return ContainerRole.None;
            }
        }
    }
}
