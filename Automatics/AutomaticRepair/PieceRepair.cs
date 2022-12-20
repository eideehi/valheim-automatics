using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticRepair
{
    internal static class PieceRepair
    {
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

        private static IEnumerable<Piece> GetAllPieces()
        {
            return Reflections.GetStaticField<Piece, List<Piece>>("m_allPieces") ??
                   Enumerable.Empty<Piece>();
        }

        public static void Repair(Player player)
        {
            if (Config.PieceSearchRange <= 0) return;
            if (!player.InPlaceMode()) return;

            var tool = player.GetRightItem();
            var toolData = tool.m_shared;

            var origin = player.transform.position;
            var range = Config.PieceSearchRange;
            var count = 0;
            foreach (var piece in GetAllPieces().ToList())
            {
                var position = piece.transform.position;

                if (Vector3.Distance(origin, position) > range) continue;
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