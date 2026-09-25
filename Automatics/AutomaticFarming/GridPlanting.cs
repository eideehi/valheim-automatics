using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    // Capture the ghost pose before vanilla can move it, but wait until vanilla has paid for
    // the manual crop before spending anything on its neighbours.
    internal static class GridPlanting
    {
        private static readonly Func<Humanoid, ItemDrop.ItemData> GetRightItem =
            AccessTools.MethodDelegate<Func<Humanoid, ItemDrop.ItemData>>(AccessTools.Method(typeof(Humanoid), "GetRightItem"));
        private static readonly Func<Player, float> GetBuildStamina =
            AccessTools.MethodDelegate<Func<Player, float>>(AccessTools.Method(typeof(Player), "GetBuildStamina"));
        private static readonly Func<Player, ItemDrop.ItemData, float> GetPlaceDurability =
            AccessTools.MethodDelegate<Func<Player, ItemDrop.ItemData, float>>(
                AccessTools.Method(typeof(Player), "GetPlaceDurability"));

        private sealed class Request
        {
            internal Player Player;
            internal Piece Piece;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal ItemDrop.ItemData Tool;
        }

        private static Request _pending;
        private static bool _planting;
        internal static bool HasPending => _pending != null;

        internal static void ClearPending()
        {
            _pending = null;
        }

        // KeyboardShortcut.IsPressed() is false while any other key is held (walking, jumping),
        // so read the bound keys directly. Alternative placement always wins over the grid, as
        // vanilla's own ghost reads it: the AltPlace button, or the gamepad toggle in the
        // non-classic layout.
        internal static bool IsModifierHeld(Player player, KeyboardShortcut key)
        {
            if (IsAlternativePlacement(player)) return false;
            return key.MainKey == KeyCode.None ||
                   (Input.GetKey(key.MainKey) && key.Modifiers.All(Input.GetKey));
        }

        private static bool IsAlternativePlacement(Player player)
        {
            if (ZInput.GetButton("AltPlace")) return true;
            return ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive()
                ? player && player.AlternativePlacementActive
                : ZInput.GetButton("JoyAltPlace") && !ZInput.GetButton("JoyRotate");
        }

        // The postfix supplies the key predicate separately so smoke can exercise a released
        // modifier without synthesizing keyboard input. The module gate belongs to the postfix.
        internal static void RecordPlacement(Player player, Piece piece, bool succeeded,
            Vector3 position, Quaternion rotation, bool modifierHeld)
        {
            if (_planting || !succeeded || !player || player != Player.m_localPlayer ||
                !player.IsOwner() || !piece || !piece.GetComponent<Plant>() ||
                !Config.EnableGridPlanting || Config.GridPlantingSize <= 1 || !modifierHeld) return;
            var tool = GetRightItem(player);
            if (tool == null || !tool.m_shared.m_buildPieces) return;
            _pending = new Request
            {
                Player = player, Piece = piece, Position = position, Rotation = rotation, Tool = tool
            };
        }

        internal static void ConsumePending(Player player)
        {
            var request = _pending;
            _pending = null;
            if (request == null || _planting || !player || player != Player.m_localPlayer ||
                request.Player != player || !player.IsOwner() || !request.Piece ||
                !ReferenceEquals(GetRightItem(player), request.Tool) ||
                !Config.EnableGridPlanting || Config.GridPlantingSize <= 1) return;

            var plant = request.Piece.GetComponent<Plant>();
            if (!plant) return;
            var spacing = plant.m_growRadius * Config.SowingSpacingFactor;
            if (spacing < 0.1f) return;
            // Vanilla skips the seed check when placement costs are off, so the extra crops do too.
            var costWaived = player.NoCostCheat() ||
                             (ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey(request.Piece.FreeBuildKey()));
            _planting = true;
            var completed = false;
            try
            {
                // Stamina and durability are charged as vanilla charges them, cost-free or not. The
                // extra crops raise no skill and no build stats: only the hand-placed crop does.
                foreach (var position in BuildCells(request.Position, request.Rotation, Config.GridPlantingSize, spacing))
                {
                    if (!player.HaveStamina(GetBuildStamina(player)) ||
                        (request.Tool.m_shared.m_useDurability && request.Tool.m_durability <= 0f)) break;
                    if (!costWaived && !EnsureSeedsInInventory(player, request.Piece)) break;
                    if (!CropPlanting.TryPlant(player, request.Piece, plant,
                            SeedPool.FromPlayerInventory(player), position, request.Rotation, 0, out _,
                            costWaived)) continue;
                    player.UseStamina(GetBuildStamina(player));
                    if (request.Tool.m_shared.m_useDurability)
                        request.Tool.m_durability -= GetPlaceDurability(player, request.Tool) * Game.m_durabilityRate;
                }
                completed = true;
            }
            finally
            {
                try { ReturnUnusedSeeds(completed); }
                finally { _planting = false; }
            }
        }

        internal static List<Vector3> BuildCells(Vector3 centre, Quaternion rotation, int size, float spacing)
        {
            var cells = new List<Vector3>();
            // Even sizes use the central cell on the positive side as the manual crop.
            var half = size / 2;
            for (var z = 0; z < size; z++)
            for (var x = 0; x < size; x++)
            {
                if (x == half && z == half) continue;
                var position = centre + rotation * new Vector3((x - half) * spacing, 0f, (z - half) * spacing);
                if (ZoneSystem.instance) position.y = ZoneSystem.instance.GetGroundHeight(position);
                cells.Add(position);
            }
            return cells;
        }

        // The seeds a grid took from nearby containers, while it runs.
        private static AutomaticSupply.SupplyAction _supplyAction;

        // The hand-placed crop's seed path: the inventory, plus Automatic Supply's containers when
        // that module is running. The full cost of the cell is in the inventory before it is
        // planted, so paying from the inventory cannot fall short.
        internal static bool EnsureSeedsInInventory(Player player, Piece piece)
        {
            if (_supplyAction == null) _supplyAction = AutomaticSupply.SupplyTransfer.BeginIfRunning(player);
            if (_supplyAction != null) AutomaticSupply.SupplyTransfer.EnsurePieceRequirements(_supplyAction, piece, true);
            return CropPlanting.CanAffordPlanting(SeedPool.FromPlayerInventory(player), piece, 0);
        }

        // Seeds taken from containers and not planted go back when the grid ends normally. After an
        // exception they stay in the inventory, so none is lost or duplicated.
        internal static void ReturnUnusedSeeds(bool completedNormally)
        {
            var action = _supplyAction;
            _supplyAction = null;
            AutomaticSupply.SupplyTransfer.End(action, completedNormally);
        }
    }
}
