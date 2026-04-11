using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticRepair
{
    internal static class PieceRepair
    {
        private static readonly MethodInfo GetAllPiecesInRadiusMethod =
            AccessTools.DeclaredMethod(typeof(Piece), "GetAllPiecesInRadius");

        private static bool _skipRadiusMethodLookup;

        private static void ShowRepairMessage(Player player, int repairCount)
        {
            if (repairCount == 0) return;

            Automatics.Logger.Debug(() => $"Repaired {repairCount} pieces");
            if (Config.PieceRepairMessage == Message.None) return;

            var type = Config.PieceRepairMessage == Message.Center
                ? MessageHud.MessageType.Center
                : MessageHud.MessageType.TopLeft;
            player.Message(type,
                Automatics.L10N.Localize("@message_automatic_repair_repaired_the_pieces",
                    repairCount));
        }

        private static bool CheckCanRepairPiece(Player player, Piece piece)
        {
            return player.NoCostCheat() || piece.m_craftingStation == null ||
                   CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name,
                       player.transform.position);
        }

        private static bool IsBuildTool(ItemDrop.ItemData item)
        {
            return item?.m_shared?.m_buildPieces != null;
        }

        private static bool TryGetBuildTool(Player player, out ItemDrop.ItemData tool)
        {
            tool = Reflections.InvokeMethod<ItemDrop.ItemData>(player, "GetRightItem");
            if (IsBuildTool(tool)) return true;

            tool = Reflections.InvokeMethod<ItemDrop.ItemData>(player, "GetLeftItem");
            if (IsBuildTool(tool)) return true;

            tool = player.GetCurrentWeapon();
            return IsBuildTool(tool);
        }

        private static object[] TryCreateRadiusMethodArguments(MethodInfo method, Vector3 origin,
            float range, List<Piece> resultBuffer)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;
                if (parameterType.IsByRef)
                    parameterType = parameterType.GetElementType();

                if (parameterType == typeof(Vector3))
                {
                    args[i] = origin;
                    continue;
                }

                if (parameterType == typeof(float))
                {
                    args[i] = range;
                    continue;
                }

                if (parameterType != null &&
                    typeof(ICollection<Piece>).IsAssignableFrom(parameterType))
                {
                    args[i] = resultBuffer;
                    continue;
                }

                if (parameterType == null) return null;

                if (!parameterType.IsValueType)
                {
                    args[i] = null;
                    continue;
                }

                args[i] = Activator.CreateInstance(parameterType);
            }

            return args;
        }

        private static IEnumerable<Piece> TryGetPiecesInRadius(Vector3 origin, float range)
        {
            if (_skipRadiusMethodLookup || GetAllPiecesInRadiusMethod == null) return null;

            var resultBuffer = new List<Piece>();
            try
            {
                var args =
                    TryCreateRadiusMethodArguments(GetAllPiecesInRadiusMethod, origin, range,
                        resultBuffer);
                if (args == null)
                {
                    _skipRadiusMethodLookup = true;
                    return null;
                }

                var result = GetAllPiecesInRadiusMethod.Invoke(null, args);
                if (result is IEnumerable<Piece> pieces)
                    return pieces;

                return resultBuffer;
            }
            catch (Exception e)
            {
                _skipRadiusMethodLookup = true;
                Automatics.Logger.Debug(() =>
                    $"Failed to query nearby pieces with {nameof(Piece)}.{GetAllPiecesInRadiusMethod.Name}: {e}");
                return null;
            }
        }

        private static IEnumerable<Piece> GetNearbyPieces(Vector3 origin, float range)
        {
            var pieces = TryGetPiecesInRadius(origin, range);
            if (pieces != null)
                return pieces;

            var nearbyPieces = new HashSet<Piece>();
            foreach (var collider in Physics.OverlapSphere(origin, range))
            {
                var piece = collider.GetComponentInParent<Piece>();
                if (piece != null)
                    nearbyPieces.Add(piece);
            }

            return nearbyPieces;
        }

        public static void Repair(Player player)
        {
            if (Config.PieceSearchRange <= 0) return;
            if (!TryGetBuildTool(player, out var tool)) return;

            var toolData = tool.m_shared;

            var origin = player.transform.position;
            var range = Config.PieceSearchRange;
            var count = 0;
            foreach (var piece in GetNearbyPieces(origin, range))
            {
                var position = piece.transform.position;

                if (!PrivateArea.CheckAccess(position) || !CheckCanRepairPiece(player, piece))
                    continue;

                var wearNTear = piece.GetComponent<WearNTear>();
                if (wearNTear == null || !wearNTear.Repair()) continue;

                piece.m_placeEffect.Create(position, piece.transform.rotation);
                if (toolData.m_useDurability)
                    tool.m_durability -= toolData.m_useDurabilityDrain;

                Automatics.Logger.Debug(() =>
                    $"Repair piece: [{piece.m_name}({Automatics.L10N.Translate(piece.m_name)}), pos: {piece.transform.position}]");
                count++;
            }

            if (count > 0)
            {
                var zSyncAnimation = Reflections.GetField<ZSyncAnimation>(player, "m_zanim");
                if (zSyncAnimation != null)
                    zSyncAnimation.SetTrigger(toolData.m_attack.m_attackAnimation);

                ShowRepairMessage(player, count);
            }
        }
    }
}
