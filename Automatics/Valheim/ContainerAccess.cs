using System;
using System.Collections.Generic;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.Valheim
{
    internal static class ContainerAccess
    {
        private const string PrivateAreaAllAreasField = "m_allAreas";
        private const string PrivateAreaPieceField = "m_piece";
        private const string PrivateAreaZNetViewField = "m_nview";

        // Container.Load is private. It reads the saved contents into the inventory when the
        // ZDO changed since the last load or save, and does nothing while the container is open
        // here. Without it no container is written (see ContainerWritePolicy).
        private static readonly Func<Container, bool> LoadContents;

        // Container.CheckAccess is private too; when it cannot be bound, the reflective call
        // below is used instead.
        private static readonly Func<Container, long, bool> CheckAccessInvoker;

        static ContainerAccess()
        {
            try
            {
                var method = AccessTools.Method(typeof(Container), "Load", Type.EmptyTypes);
                if (method != null)
                    LoadContents = AccessTools.MethodDelegate<Func<Container, bool>>(method);
                else
                    Automatics.Logger.Warning(() =>
                        "Container.Load not found; containers will not be written.");
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to bind Container.Load; containers will not be written: {e.Message}");
            }

            try
            {
                var method = AccessTools.Method(typeof(Container), "CheckAccess",
                    new[] { typeof(long) });
                if (method != null)
                    CheckAccessInvoker =
                        AccessTools.MethodDelegate<Func<Container, long, bool>>(method);
                else
                    Automatics.Logger.Warning(() =>
                        "Container.CheckAccess not found; falling back to reflection.");
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to bind Container.CheckAccess; falling back to reflection: {e.Message}");
            }
        }

        // Reads a container's saved contents into its inventory when they changed since its last
        // load or save, as the game itself does once a second. It needs no ownership; a container
        // open here keeps its current copy. For reads that must see what a container holds now.
        public static void LoadSavedContents(Container container)
        {
            if (LoadContents == null || container == null) return;
            if (!Objects.GetZNetView(container, out var nview) || nview == null ||
                !nview.IsValid() || nview.GetZDO() == null)
                return;

            LoadContents(container);
        }

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
            if (player == null || !IsIdle(container)) return false;
            if (container.m_checkGuardStone &&
                !CheckWardAccess(player, container.transform.position))
                return false;

            return HasContainerAccess(player, container);
        }

        // Without a local player (a dedicated server, or a server whose player is dead,
        // respawning or loading) there is nobody whose ward or privacy rights apply. In
        // single-player every idle container may be used, as before; a multiplayer server uses
        // only what the vanilla access rule allows for no player (public containers), so it
        // never writes another player's private chest.
        public static bool CanUseContainerServerAware(Player player, Container container)
        {
            if (player != null) return CanUseContainer(player, container);
            return IsIdle(container) && (ZNet.IsSinglePlayer || CheckAccess(container, 0L));
        }

        // Whether the gate below would let this game write the container, without claiming or
        // loading it: the container passes the access checks, and this game owns it or, as the
        // server, may claim it. A container another game owns is not writable here. A null
        // player is the context without a local player, as in the server-aware gate.
        public static bool CanWriteContainer(Player player, Container container)
        {
            return CanUseContainerServerAware(player, container) &&
                   Objects.GetZNetView(container, out var nview) &&
                   ContainerWritePolicy.Decide(ReadWriteState(player, container, nview, false)) !=
                   ContainerWriteStep.Skip;
        }

        // The gate before every container write: the player-aware access checks, then the
        // container rules of ContainerWritePolicy, which claim the container only where they
        // allow it and load its saved contents right before allowing the write. The load
        // replaces every item in the inventory, so callers read items, stock and room after
        // this returns true.
        public static bool TryPrepareContainerMutation(Player player, Container container)
        {
            return CanUseContainer(player, container) && TryAcquire(player, container);
        }

        // Contexts without a local player have no player permission model to evaluate, so the
        // checks of CanUseContainerServerAware apply there, and the container rules let only the
        // server write without a player. When a local player exists, enforce the full
        // player-aware access checks (ward, private/group, in-use, wagon).
        public static bool TryPrepareContainerMutationServerAware(Player player,
            Container container)
        {
            return CanUseContainerServerAware(player, container) && TryAcquire(player, container);
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

            return CheckAccess(container, playerId);
        }

        private static bool CheckAccess(Container container, long playerId)
        {
            return CheckAccessInvoker != null
                ? CheckAccessInvoker(container, playerId)
                : Reflections.InvokeMethod<bool>(container, "CheckAccess", playerId);
        }

        public static bool IsInUse(Container container)
        {
            if (container == null) return false;
            if (!Objects.GetZNetView(container, out var nview))
                return container.IsInUse();

            return IsInUse(container, nview);
        }

        // The access checks that need no player: a live container that is not a grave, not in
        // use and not on a wagon in use.
        private static bool IsIdle(Container container)
        {
            if (container == null || IsGrave(container)) return false;
            if (!Objects.GetZNetView(container, out var nview)) return false;
            if (nview == null || !nview.IsValid() || nview.GetZDO() == null) return false;
            if (container.GetInventory() == null) return false;
            if (IsInUse(container, nview)) return false;
            return !((bool)container.m_wagon && container.m_wagon.InUse());
        }

        // Callers have checked the ZNetView is valid (IsIdle).
        private static bool TryAcquire(Player player, Container container)
        {
            return Objects.GetZNetView(container, out var nview) &&
                   ContainerWritePolicy.TryAcquire(new WriteTarget(player, container, nview));
        }

        private static ContainerWriteState ReadWriteState(Player player, Container container,
            ZNetView nview, bool loaded)
        {
            var net = ZNet.instance;
            return new ContainerWriteState
            {
                Owned = nview.IsOwner(),
                HasOwner = nview.HasOwner(),
                IsServer = net && net.IsServer(),
                LocalPlayer = player != null,
                InUseLocal = container.IsInUse(),
                InUseReplicated = nview.GetZDO().GetInt(ZDOVars.s_inUse) == 1,
                CartInUse = (bool)container.m_wagon && container.m_wagon.InUse(),
                // Carts and ships give their containers the ZNetView of the vehicle.
                BorrowedView = (bool)container.m_rootObjectOverride,
                Loaded = loaded
            };
        }

        private readonly struct WriteTarget : IContainerWriteTarget
        {
            private readonly Player _player;
            private readonly Container _container;
            private readonly ZNetView _nview;

            public WriteTarget(Player player, Container container, ZNetView nview)
            {
                _player = player;
                _container = container;
                _nview = nview;
            }

            public ContainerWriteState Read(bool loaded)
            {
                return ReadWriteState(_player, _container, _nview, loaded);
            }

            public void Claim()
            {
                _nview.ClaimOwnership();
            }

            public bool Load()
            {
                if (LoadContents == null) return false;
                LoadContents(_container);
                return true;
            }
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
