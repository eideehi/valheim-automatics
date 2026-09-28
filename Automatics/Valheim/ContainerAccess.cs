using System.Collections.Generic;
using ModUtils;
using UnityEngine;

namespace Automatics.Valheim
{
    internal static class ContainerAccess
    {
        private const string PrivateAreaAllAreasField = "m_allAreas";
        private const string PrivateAreaPieceField = "m_piece";
        private const string PrivateAreaZNetViewField = "m_nview";

        public static bool IsAllowed(Container container, ICollection<string> allowedIdentifiers)
        {
            return container != null &&
                   allowedIdentifiers != null &&
                   ValheimObject.Container.GetIdentify(Objects.GetName(container), out var identifier) &&
                   allowedIdentifiers.Contains(identifier);
        }

        // A grave holds a dead player's items; no module stores into it, takes from it or gives it
        // a role.
        public static bool IsGrave(Container container)
        {
            return container != null && container.GetComponent<TombStone>();
        }

        public static bool CanUseContainer(Player player, Container container)
        {
            if (player == null || container == null) return false;
            if (IsGrave(container)) return false;
            if (!Objects.GetZNetView(container, out var nview)) return false;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null) return false;
            if (container.GetInventory() == null) return false;
            if (IsInUse(container, nview)) return false;
            if ((bool)container.m_wagon && container.m_wagon.InUse()) return false;
            if (container.m_checkGuardStone &&
                !CheckWardAccess(player, container.transform.position))
                return false;

            return HasContainerAccess(player, container);
        }

        public static bool CanDirectlyMutateContainer(Player player, Container container)
        {
            return CanUseContainer(player, container) &&
                   Objects.GetZNetView(container, out var nview) &&
                   nview != null &&
                   nview.IsValid() &&
                   nview.GetZDO() != null &&
                   nview.IsOwner();
        }

        public static bool TryPrepareContainerMutation(Player player, Container container)
        {
            if (!CanUseContainer(player, container)) return false;
            if (!TryClaimContainer(container)) return false;
            return TryPrepareOwnedContainerMutation(container) &&
                   HasContainerAccess(player, container);
        }

        // Server/owner contexts (dedicated server, no local player) have no
        // player permission model to evaluate, so fall back to the pre-1.6
        // owner-only mutation gate that Automatic Processing and Automatic
        // Feeding relied on server-side. When a local player exists, enforce the
        // full player-aware access checks (ward, private/group, in-use, wagon).
        public static bool TryPrepareContainerMutationServerAware(Player player,
            Container container)
        {
            return player != null
                ? TryPrepareContainerMutation(player, container)
                : !IsGrave(container) && TryClaimContainer(container) &&
                  TryPrepareOwnedContainerMutation(container);
        }

        // Container.CheckAccess is private, but it is the vanilla ownership
        // rule for public/private/group containers.
        public static bool HasContainerAccess(Player player, Container container)
        {
            if (player == null || container == null) return false;

            var playerId = player.GetPlayerID();
            if (playerId == 0L && Game.instance != null)
            {
                var profile = Game.instance.GetPlayerProfile();
                if (profile != null)
                    playerId = profile.GetPlayerID();
            }
            if (playerId == 0L) return false;

            return Reflections.InvokeMethod<bool>(container, "CheckAccess", playerId);
        }

        public static bool IsInUse(Container container)
        {
            if (container == null) return false;
            if (!Objects.GetZNetView(container, out var nview))
                return container.IsInUse();

            return IsInUse(container, nview);
        }

        public static bool TryClaimContainer(Container container)
        {
            if (container == null) return false;
            if (!Objects.GetZNetView(container, out var nview)) return false;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null) return false;
            if (!nview.IsOwner()) nview.ClaimOwnership();
            return true;
        }

        public static bool TryPrepareOwnedContainerMutation(Container container)
        {
            if (container == null) return false;
            if (!Objects.GetZNetView(container, out var nview)) return false;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null) return false;
            if (!nview.IsOwner()) return false;
            if (IsInUse(container, nview)) return false;
            if ((bool)container.m_wagon && container.m_wagon.InUse()) return false;
            return true;
        }

        private static bool IsInUse(Container container, ZNetView nview)
        {
            if (container == null) return false;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null)
                return container.IsInUse();

            return nview.IsOwner()
                ? container.IsInUse()
                : nview.GetZDO().GetInt(ZDOVars.s_inUse) == 1;
        }

        private static bool CheckWardAccess(Player player, Vector3 point)
        {
            if (!player) return false;

            var areas =
                Reflections.GetStaticField<PrivateArea, List<PrivateArea>>(PrivateAreaAllAreasField);
            if (areas == null) return false;

            foreach (var area in areas)
            {
                if (!area || !IsInside(area, point, 0f) || !IsEnabled(area)) continue;
                if (!HasPlayerAccess(area, player)) return false;
            }

            return true;
        }

        private static bool IsEnabled(PrivateArea area)
        {
            if (!area) return false;

            var zNetView = Reflections.GetField<ZNetView>(area, PrivateAreaZNetViewField);
            return zNetView != null &&
                   zNetView.IsValid() &&
                   zNetView.GetZDO().GetBool(ZDOVars.s_enabled);
        }

        private static bool IsInside(PrivateArea area, Vector3 point, float radius)
        {
            return Utils.DistanceXZ(area.transform.position, point) < area.m_radius + radius;
        }

        private static bool HasPlayerAccess(PrivateArea area, Player player)
        {
            var playerId = player.GetPlayerID();
            if (playerId == 0L) return false;

            var piece = Reflections.GetField<Piece>(area, PrivateAreaPieceField);
            if (piece && piece.GetCreator() == playerId)
                return true;

            var zNetView = Reflections.GetField<ZNetView>(area, PrivateAreaZNetViewField);
            if (zNetView == null || !zNetView.IsValid()) return false;

            var zdo = zNetView.GetZDO();
            var permittedCount = zdo.GetInt(ZDOVars.s_permitted);
            for (var index = 0; index < permittedCount; index++)
                if (zdo.GetLong("pu_id" + index, 0L) == playerId)
                    return true;

            return false;
        }
    }
}
