using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Automatics.AutomaticStorage
{
    internal static class ContainerSorter
    {
        // Vanilla has no public notification for direct stack/grid edits. Changed also
        // recalculates weight and invokes the container's save callback.
        private static readonly Action<Inventory, bool, bool> NotifyChanged =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(
                AccessTools.Method(typeof(Inventory), "Changed", new[] { typeof(bool), typeof(bool) }));

        public static void Sort(Inventory inventory)
        {
            var items = inventory.GetAllItems();
            var width = inventory.GetWidth();
            if (items.Count > width * inventory.GetHeight()) return;

            var retainedItems = new List<ItemDrop.ItemData>();
            foreach (var item in OrderItems(items).ToList())
            {
                if (item.m_shared.m_maxStackSize > 1)
                {
                    foreach (var target in retainedItems)
                    {
                        // Match Inventory.FindFreeStackItem, used by AddItem(ItemData).
                        if (target.m_shared.m_name != item.m_shared.m_name ||
                            target.m_quality != item.m_quality ||
                            target.m_worldLevel != item.m_worldLevel)
                            continue;

                        var space = target.m_shared.m_maxStackSize - target.m_stack;
                        if (space <= 0) continue;

                        var moved = Math.Min(space, item.m_stack);
                        target.m_stack += moved;
                        item.m_stack -= moved;
                        // As vanilla AddItem does when it merges a cheated item into a stack.
                        if (item.m_cheated) target.m_cheated = true;
                        if (item.m_stack == 0) break;
                    }
                }

                if (item.m_stack > 0)
                    retainedItems.Add(item);
                else
                    items.Remove(item);
            }

            var orderedItems = OrderItems(retainedItems).ToList();
            for (var index = 0; index < orderedItems.Count; index++)
                orderedItems[index].m_gridPos = new Vector2i(index % width, index / width);

            NotifyChanged(inventory, false, false);
        }

        private static IOrderedEnumerable<ItemDrop.ItemData> OrderItems(
            IEnumerable<ItemDrop.ItemData> items)
        {
            return items.OrderBy(item => item.m_shared.m_itemType)
                        .ThenBy(item => Localization.instance.Localize(item.m_shared.m_name),
                            StringComparer.CurrentCulture)
                        .ThenByDescending(item => item.m_quality)
                        .ThenByDescending(item => item.m_stack)
                        .ThenBy(item => item.m_shared.m_name, StringComparer.Ordinal)
                        .ThenByDescending(item => item.m_worldLevel)
                        .ThenBy(item => item.m_gridPos.y)
                        .ThenBy(item => item.m_gridPos.x);
        }
    }
}
