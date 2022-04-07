using System.Collections.Generic;
using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticRepair
{
    using MessageType = MessageHud.MessageType;

    internal static class RepairPieces
    {
        private static float _lastRunningTime;

        private static bool Runnable()
        {
            return Time.time - _lastRunningTime >= 1f;
        }

        private static void ShowRepairMessage(Player player, int repairCount)
        {
            if (repairCount == 0) return;

            Log.Debug(() => $"Repaired {repairCount} pieces");
            if (Config.PieceRepairMessage == RepairMessage.None) return;

            var type = Config.PieceRepairMessage == RepairMessage.Center ? MessageType.Center : MessageType.TopLeft;
            player.Message(type, L10N.Localize("@message_repair_pieces", repairCount));
        }

        private static bool CheckCanRemovePiece(Player player, Piece piece)
        {
            return Reflection.InvokeMethod<bool>(player, "CheckCanRemovePiece", piece);
        }

        private static IEnumerable<Piece> GetAllPieces()
        {
            return Reflection.GetStaticField<Piece, List<Piece>>("m_allPieces") ?? Enumerable.Empty<Piece>();
        }

        public static void Run(Player player, bool takeInput)
        {
            if (!player.InPlaceMode()) return;
            if (!Config.AutomaticRepairEnabled) return;
            if (Config.PieceSearchRange <= 0) return;
            if (!Runnable()) return;

            var tool = player.GetRightItem();
            var toolData = tool.m_shared;

            var origin = player.transform.position;
            var range = Config.PieceSearchRange;
            var count = 0;
            foreach (var piece in GetAllPieces().ToList())
            {
                var position = piece.transform.position;

                if (Vector3.Distance(origin, position) > range) continue;
                if (!CheckCanRemovePiece(player, piece) || !PrivateArea.CheckAccess(position)) continue;

                var wearNTear = piece.GetComponent<WearNTear>();
                if (wearNTear == null || !wearNTear.Repair()) continue;

                piece.m_placeEffect.Create(position, piece.transform.rotation);
                if (toolData.m_useDurability)
                    tool.m_durability -= toolData.m_useDurabilityDrain;

                Log.Debug(() => $"Repair piece: [{piece.m_name}({L10N.Translate(piece.m_name)}), pos: {piece.transform.position}]");
                count++;
            }

            if (count > 0)
            {
                var zSyncAnimation = Reflection.GetField<ZSyncAnimation>(player, "m_zanim");
                if (zSyncAnimation != null)
                    zSyncAnimation.SetTrigger(toolData.m_attack.m_attackAnimation);

                ShowRepairMessage(player, count);
            }

            _lastRunningTime = Time.time;
        }
    }
}