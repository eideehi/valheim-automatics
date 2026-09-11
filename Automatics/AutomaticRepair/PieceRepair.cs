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

        // Reused scratch buffers for the once/sec, single-local-player, fully
        // synchronous repair scan, matching the NonAlloc convention used elsewhere.
        // Each returned buffer is consumed entirely within Repair before the next
        // scan, and the scan is never reentrant, so sharing is safe.
        private static readonly List<Piece> PieceBuffer = new List<Piece>();
        private static readonly HashSet<Piece> PieceSet = new HashSet<Piece>();
        private static readonly Collider[] ColliderBuffer = new Collider[256];

        private static void ShowRepairMessage(Player player, int repairCount)
        {
            if (repairCount == 0) return;

            Automatics.Logger.Debug(() => $"Repaired {repairCount} pieces");
            if (Config.PieceRepairMessage == Message.None) return;

            var type = Config.PieceRepairMessage.ToMessageType();
            player.Message(type,
                Automatics.L10N.Localize("@message_automatic_repair_repaired_the_pieces",
                    repairCount));
        }

        private static bool CheckCanRepairPiece(Player player, Piece piece)
        {
            return player.NoCostCheat() || piece.m_craftingStation == null ||
                   CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name,
                       player.transform.position) ||
                   ZoneSystem.instance != null &&
                   ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench);
        }

        private static bool IsRepairAction(Player player, ItemDrop.ItemData item)
        {
            var selectedPiece = player.GetSelectedPiece();
            return item?.m_shared?.m_buildPieces != null && selectedPiece != null &&
                   selectedPiece.m_repairPiece;
        }

        private static bool TryGetRepairTool(Player player, out ItemDrop.ItemData tool)
        {
            // Humanoid.GetRightItem() is protected, so it must be reached reflectively.
            tool = Reflections.InvokeMethod<ItemDrop.ItemData>(player, "GetRightItem");
            return IsRepairAction(player, tool);
        }

        private static float GetBuildStamina(Player player, ItemDrop.ItemData tool)
        {
            var stamina = tool.m_shared.m_attack.m_attackStamina;
            stamina *= 1f + player.GetEquipmentHomeItemModifier();
            player.GetSEMan().ModifyHomeItemStaminaUsage(stamina, ref stamina);

            var skill = tool.m_shared.m_buildPieces.m_skill;
            if (skill == Skills.SkillType.None) return stamina;

            var skillFactor = player.GetSkillFactor(skill);
            stamina -= stamina * 0.5f * skillFactor;
            return stamina;
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

            PieceBuffer.Clear();
            var resultBuffer = PieceBuffer;
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

            PieceSet.Clear();
            var size = Physics.OverlapSphereNonAlloc(origin, range, ColliderBuffer);
            for (var i = 0; i < size; i++)
            {
                var piece = ColliderBuffer[i].GetComponentInParent<Piece>();
                if (piece != null)
                    PieceSet.Add(piece);
            }

            return PieceSet;
        }

        public static void Repair(Player player)
        {
            if (Config.PieceSearchRange <= 0) return;
            if (!TryGetRepairTool(player, out var tool)) return;

            var toolData = tool.m_shared;

            var origin = player.transform.position;
            var range = Config.PieceSearchRange;
            var count = 0;
            foreach (var piece in GetNearbyPieces(origin, range))
            {
                var position = piece.transform.position;

                if (!PrivateArea.CheckAccess(position) || !CheckCanRepairPiece(player, piece))
                    continue;

                if (!player.HaveStamina(toolData.m_attack.m_attackStamina)) break;

                var wearNTear = piece.GetComponent<WearNTear>();
                if (wearNTear == null || !wearNTear.Repair()) continue;

                piece.m_placeEffect.Create(position, piece.transform.rotation);

                player.UseStamina(GetBuildStamina(player, tool));
                player.UseEitr(toolData.m_attack.m_attackEitr);
                if (toolData.m_useDurability)
                    tool.m_durability -= toolData.m_useDurabilityDrain * Game.m_durabilityRate;

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
