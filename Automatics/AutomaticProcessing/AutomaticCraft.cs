using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class AutomaticCraft
    {
        private static string LogMessage(string fromItem, int count, string toItem, Container container,
            string destName, Vector3 destPos)
        {
            return count == 1
                ? $"{L10N.Translate(fromItem)} was set from {L10N.Translate(container.m_name)} {container.transform.position} to {L10N.Translate(destName)} {destPos} for crafting {L10N.Translate(toItem)}"
                : $"{L10N.Translate(fromItem)} x{count} was set from {L10N.Translate(container.m_name)} {container.transform.position} to {L10N.Translate(destName)} {destPos} for crafting {L10N.Translate(toItem)}";
        }

        public static void Run(CookingStation piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var stationName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(stationName, Type.Craft)) return;

            if (piece.m_requireFire && !Reflection.InvokeMethod<bool>(piece, "IsFireLit")) return;

            var freeSlot = -1;
            for (var slot = 0; slot < piece.m_slots.Length; slot++)
            {
                if (zNetView.GetZDO().GetString("slot" + slot) != "") continue;
                freeSlot = slot;
                break;
            }

            if (freeSlot < 0) return;

            var origin = piece.transform.position;
            var containersWithInventory =
                (from x in Core.GetNearbyContainers(stationName, origin)
                    orderby x.Item2
                    select (x.Item1, x.Item1.GetInventory()))
                .ToList();
            if (containersWithInventory.Count == 0) return;

            foreach (var conversion in piece.m_conversion)
            {
                foreach (var (container, inventory) in containersWithInventory)
                {
                    var fromItem = inventory.GetItem(conversion.m_from.m_itemData.m_shared.m_name);
                    if (fromItem == null) continue;

                    inventory.RemoveOneItem(fromItem);
                    zNetView.InvokeRPC("AddItem", fromItem.m_dropPrefab.name);
                    Log.Debug(() => LogMessage(fromItem.m_shared.m_name, 1, conversion.m_to.m_itemData.m_shared.m_name,
                        container, stationName, origin));
                    goto CRAFT_DONE;
                }
            }

            CRAFT_DONE: ;
        }

        public static void Run(CraftingStation piece, ZNetView zNetView)
        {
            // Not yet implemented.
            /*
            if (!Config.AutomaticProcessingEnabled) return;
            if (!Config.IsAllowAutomaticProcessing(piece.m_name, Type.Craft)) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;
            */
        }

        public static void Run(Fermenter piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fermenterName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(fermenterName, Type.Craft)) return;

            if (Reflection.InvokeMethod<int>(piece, "GetStatus") != 0) return;

            var origin = piece.transform.position;
            foreach (var container in from x in Core.GetNearbyContainers(fermenterName, origin)
                     orderby x.Item2
                     select x.Item1)
            {
                var inventory = container.GetInventory();

                var (fromItem, toItem) = (from x in piece.m_conversion
                    let item = inventory.GetItem(x.m_from.m_itemData.m_shared.m_name)
                    where item != null
                    select (item, x.m_to)).FirstOrDefault();
                if (fromItem == null) continue;

                inventory.RemoveOneItem(fromItem);
                zNetView.InvokeRPC("AddItem", fromItem.m_dropPrefab.name);
                Log.Debug(() => LogMessage(fromItem.m_shared.m_name, 1, toItem.m_itemData.m_shared.m_name, container,
                    fermenterName, origin));
                break;
            }
        }

        public static void Run(Smelter piece, ZNetView zNetView)
        {
            if (!Config.AutomaticProcessingEnabled) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var smelterName = piece.m_name;
            if (!Config.IsAllowAutomaticProcessing(smelterName, Type.Craft)) return;

            var materialCount = zNetView.GetZDO().GetInt("queued");
            if (materialCount >= piece.m_maxOre) return;

            var origin = piece.transform.position;
            var tailMaterial = materialCount <= 0 ? "" : zNetView.GetZDO().GetString("item" + (materialCount - 1));
            var material = !string.IsNullOrEmpty(tailMaterial)
                ? piece.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == tailMaterial)
                : null;
            foreach (var container in from x in Core.GetNearbyContainers(smelterName, origin)
                     orderby x.Item2
                     select x.Item1)
            {
                var inventory = container.GetInventory();

                var (fromItem, toItem) = material != null
                    ? (inventory.GetItem(material.m_from.m_itemData.m_shared.m_name), material.m_to)
                    : (from x in piece.m_conversion
                        let item = inventory.GetItem(x.m_from.m_itemData.m_shared.m_name)
                        where item != null
                        select (item, x.m_to)).FirstOrDefault();
                if (fromItem == null) continue;

                inventory.RemoveOneItem(fromItem);
                zNetView.InvokeRPC("AddOre", fromItem.m_dropPrefab.name);
                Log.Debug(() => LogMessage(fromItem.m_shared.m_name, 1, toItem.m_itemData.m_shared.m_name, container,
                    smelterName, origin));
                break;
            }
        }
    }
}