using System;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    // While the grid planting modifier is held, the crop ghost moves onto the lattice of the
    // nearest growing crop of the same kind: that crop's spacing, orientation and cell positions.
    // Of the four lattice points at the corners of the lattice square under the aim, it takes the
    // nearest one where the crop has room to grow, which vanilla placement does not check (a crop
    // without room withers). When no corner has room it stays on the nearest one and the placement
    // is refused as needing more space.
    // The pose is handed to vanilla instead of being forced onto the ghost afterwards: the ray
    // result becomes the ground under the lattice point, and the rotation fields vanilla reads right
    // after the ray test give the lattice yaw. Every placement status check in
    // Player.UpdatePlacementGhost (ground, cultivation, slope, ward, no-build zones, players) then
    // runs on the snapped pose, and Player.TryPlacePiece places exactly what that call validated.
    internal static class LatticeSnapping
    {
        private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhost =
            AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly AccessTools.FieldRef<Player, Player.PlacementStatus> PlacementStatus =
            AccessTools.FieldRefAccess<Player, Player.PlacementStatus>("m_placementStatus");
        private static readonly AccessTools.FieldRef<Player, int> PlaceRotation =
            AccessTools.FieldRefAccess<Player, int>("m_placeRotation");
        private static readonly AccessTools.FieldRef<Player, float> PlaceRotationDegrees =
            AccessTools.FieldRefAccess<Player, float>("m_placeRotationDegrees");
        private static readonly AccessTools.FieldRef<Player, int> PlaceRayMask =
            AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");
        private static readonly AccessTools.FieldRef<Player, int> PlaceWaterRayMask =
            AccessTools.FieldRefAccess<Player, int>("m_placeWaterRayMask");

        // The layers Plant.HaveGrowSpace searches, so every growing crop that can block another
        // one can also anchor a lattice.
        private static readonly Lazy<int> PlantMask = new Lazy<int>(() => LayerMask.GetMask(
            "Default", "static_solid", "Default_small", "piece", "piece_nonsolid"));
        private static readonly Lazy<int> WaterLayer = new Lazy<int>(() => LayerMask.NameToLayer("Water"));
        // Grown on saturation, so the nearest crop is chosen from every candidate in the radius.
        private const int MaxColliderBuffer = 8192;
        private static Collider[] _colliderBuffer = new Collider[256];

        private sealed class GhostUpdate
        {
            internal Player Player;
            internal bool Snapped;
            // Snapped to the nearest lattice point because no corner of the square had room.
            internal bool NoRoom;
            internal int SavedRotation;
            internal float SavedDegrees;
        }

        private static GhostUpdate _update;

        // The search radius, in lattice cells, around the aimed point: one grid width. A grid laid
        // edge to edge with an earlier one has its centre about half a grid plus one cell from the
        // earlier grid's nearest crop, so aiming there, or up to about half a grid further out,
        // still finds it; a crop farther away is not "next to" the new grid.
        internal static float SearchRadius(float spacing, int size)
        {
            return spacing * Mathf.Max(2, size);
        }

        // Opens the snapping context for one ghost update of the local player. Returns true only
        // when this call opened it, so a nested update neither snaps twice nor closes the outer one.
        internal static bool BeginGhostUpdate(Player player, bool modifierHeld)
        {
            if (_update != null || !modifierHeld || !player || player != Player.m_localPlayer) return false;
            _update = new GhostUpdate { Player = player };
            return true;
        }

        // Puts back the player's own rotation step, which the snapped update borrowed, whether or
        // not the update completed.
        internal static void EndGhostUpdate()
        {
            var update = _update;
            _update = null;
            if (update == null || !update.Snapped || !update.Player) return;
            PlaceRotation(update.Player) = update.SavedRotation;
            PlaceRotationDegrees(update.Player) = update.SavedDegrees;
        }

        // Replaces the ray result of the open ghost update with the ground under a lattice point,
        // accepted by the same rules vanilla's own ray test applies (no rigidbody, within reach).
        // The corners of the lattice square under the aim are tried nearest first, and the first one
        // whose ground has room to grow (CropPlanting.HasGrowSpace) is taken; a farther corner whose
        // ground cannot be hit is passed over. When no corner has room, the nearest one is taken and
        // the update is marked for RefuseWithoutRoom. Without a lattice, or when the ground under the
        // nearest corner cannot be hit, the vanilla result stays.
        internal static void SnapRayResult(Player player, ref Vector3 point, ref Vector3 normal, ref Piece piece,
            ref Heightmap heightmap, ref Collider waterSurface, bool water)
        {
            var update = _update;
            if (update == null || update.Snapped || update.Player != player) return;
            var ghost = PlacementGhost(player);
            var ghostPiece = ghost ? ghost.GetComponent<Piece>() : null;
            var selected = player.GetSelectedPiece();
            if (!ghostPiece || !selected) return;
            var playerYaw = PlaceRotationDegrees(player) * PlaceRotation(player);
            if (!TryLatticePose(selected, true, point, playerYaw, out var nearest, out var yaw)) return;

            // The lattice yaw turns the anchor's axes by whole quarter turns, so its axes run along
            // the same rows. Along them the aim lies within half a cell of the nearest corner; the
            // next corners are one cell toward it along the axis on which it is farther from that
            // corner, then along the other axis, then along both.
            var growRadius = selected.GetComponent<Plant>().m_growRadius;
            var spacing = growRadius * Config.SowingSpacingFactor;
            var axes = Quaternion.Euler(0f, yaw, 0f);
            var offset = Quaternion.Inverse(axes) * (point - nearest);
            var stepX = axes * new Vector3(offset.x >= 0f ? spacing : -spacing, 0f, 0f);
            var stepZ = axes * new Vector3(0f, 0f, offset.z >= 0f ? spacing : -spacing);
            var xFirst = Mathf.Abs(offset.x) >= Mathf.Abs(offset.z);
            var mask = water ? PlaceWaterRayMask(player) : PlaceRayMask(player);
            var reach = player.m_maxPlaceDistance + ghostPiece.m_extraPlacementDistance;
            var hit = default(RaycastHit);
            var room = false;
            for (var i = 0; i < 4 && !room; i++)
            {
                var corner = nearest;
                if (i == 3 || (xFirst ? i == 1 : i == 2)) corner += stepX;
                if (i == 3 || (xFirst ? i == 2 : i == 1)) corner += stepZ;
                if (!TryGroundUnder(player, point, corner, mask, reach, out var ground))
                {
                    if (i == 0) return;
                    continue;
                }
                room = CropPlanting.HasGrowSpace(ground.point, growRadius);
                if (room || i == 0) hit = ground;
            }

            point = hit.point;
            normal = hit.normal;
            piece = hit.collider.GetComponentInParent<Piece>();
            heightmap = hit.collider.GetComponent<Heightmap>();
            waterSurface = hit.collider.gameObject.layer == WaterLayer.Value ? hit.collider : null;

            update.SavedRotation = PlaceRotation(player);
            update.SavedDegrees = PlaceRotationDegrees(player);
            update.Snapped = true;
            update.NoRoom = !room;
            PlaceRotation(player) = 1;
            PlaceRotationDegrees(player) = yaw;
        }

        // Refuses the placement of an update that snapped with no room to grow. It runs when
        // vanilla tints the ghost from its final status: Valid becomes MoreSpace, which
        // Player.TryPlacePiece refuses with the game's need-more-space message, and vanilla's own
        // tint then shows the ghost red. A status vanilla already set to something else is left as
        // it is. Returns true when it refused the placement.
        internal static bool RefuseWithoutRoom(Player player)
        {
            var update = _update;
            if (update == null || !update.NoRoom || update.Player != player ||
                PlacementStatus(player) != Player.PlacementStatus.Valid) return false;
            PlacementStatus(player) = Player.PlacementStatus.MoreSpace;
            return true;
        }

        // The ground under a lattice point, cast down from above the aimed height and accepted by
        // the rules of vanilla's own ray test: no rigidbody, within reach.
        private static bool TryGroundUnder(Player player, Vector3 aimed, Vector3 target, int mask, float reach,
            out RaycastHit hit)
        {
            var climb = Vector3.Distance(new Vector3(aimed.x, 0f, aimed.z), new Vector3(target.x, 0f, target.z)) + 1f;
            var origin = new Vector3(target.x, aimed.y + climb, target.z);
            return Physics.Raycast(origin, Vector3.down, out hit, climb * 2f, mask) && hit.collider &&
                   !hit.collider.attachedRigidbody && Vector3.Distance(player.m_eye.position, hit.point) < reach;
        }

        // The lattice pose for a crop aimed at rawPoint: the nearest lattice point of the nearest
        // growing crop of the same prefab (height left to the caller), and that crop's yaw turned by
        // the quarter turn closest to the player's own rotation, which leaves the square lattice
        // unchanged. False when snapping does not apply or no such crop is within the search radius.
        internal static bool TryLatticePose(Piece selected, bool modifierHeld, Vector3 rawPoint, float playerYaw,
            out Vector3 point, out float yaw)
        {
            point = rawPoint;
            yaw = playerYaw;
            if (!modifierHeld || !selected || !Config.EnableGridPlanting || Config.GridPlantingSize <= 1) return false;
            var plant = selected.GetComponent<Plant>();
            // A plant that attaches to a surface is not placed on a ground lattice.
            if (!plant || plant.m_attachDistance > 0f) return false;
            var spacing = plant.m_growRadius * Config.SowingSpacingFactor;
            if (spacing < 0.1f) return false;
            if (!TryFindAnchor(Objects.GetPrefabName(selected.gameObject), rawPoint,
                    SearchRadius(spacing, Config.GridPlantingSize), out var anchor)) return false;

            var anchorYaw = anchor.transform.rotation.eulerAngles.y;
            var lattice = Quaternion.Euler(0f, anchorYaw, 0f);
            var local = Quaternion.Inverse(lattice) * (rawPoint - anchor.transform.position);
            point = anchor.transform.position + lattice * new Vector3(
                Mathf.Round(local.x / spacing) * spacing, 0f, Mathf.Round(local.z / spacing) * spacing);
            point.y = rawPoint.y;
            yaw = anchorYaw + Mathf.Round(Mathf.DeltaAngle(anchorYaw, playerYaw) / 90f) * 90f;
            return true;
        }

        // The lattice of the nearest growing crop of the prefab within the radius: that crop's
        // position and its yaw. Proactive sowing uses it to continue the rows already planted.
        internal static bool TryFindLattice(string prefabName, Vector3 centre, float radius, out Vector3 origin,
            out Quaternion rotation)
        {
            origin = centre;
            rotation = Quaternion.identity;
            if (!TryFindAnchor(prefabName, centre, radius, out var anchor)) return false;

            origin = anchor.transform.position;
            rotation = Quaternion.Euler(0f, anchor.transform.rotation.eulerAngles.y, 0f);
            return true;
        }

        // The nearest growing crop of the prefab within the radius, measured on the ground plane.
        // A crop that has grown is a different object with a randomized yaw, so it anchors nothing.
        private static bool TryFindAnchor(string prefabName, Vector3 centre, float radius, out Plant anchor)
        {
            anchor = null;
            var best = float.MaxValue;
            int count;
            while ((count = Physics.OverlapSphereNonAlloc(centre, radius, _colliderBuffer, PlantMask.Value)) ==
                   _colliderBuffer.Length && _colliderBuffer.Length < MaxColliderBuffer)
                _colliderBuffer = new Collider[_colliderBuffer.Length * 2];
            for (var i = 0; i < count; i++)
            {
                var plant = _colliderBuffer[i].GetComponentInParent<Plant>();
                _colliderBuffer[i] = null;
                if (!plant || plant == anchor) continue;
                var view = plant.GetComponent<ZNetView>();
                if (!view || !view.IsValid() || Objects.GetPrefabName(plant.gameObject) != prefabName) continue;
                var offset = plant.transform.position - centre;
                var distance = new Vector2(offset.x, offset.z).sqrMagnitude;
                if (distance > radius * radius || distance >= best) continue;
                best = distance;
                anchor = plant;
            }
            return anchor;
        }
    }
}
