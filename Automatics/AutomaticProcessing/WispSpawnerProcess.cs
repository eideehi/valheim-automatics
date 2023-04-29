using JetBrains.Annotations;
using ModUtils;

namespace Automatics.AutomaticProcessing
{
    internal static class WispSpawnerProcess
    {
        [UsedImplicitly]
        public static bool Store(WispSpawner wispSpawner, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return false;

            var wispSpawnerName = wispSpawner.m_name;
            if (!Logics.IsAllowProcessing(wispSpawnerName, Process.Store)) return false;

            var wispPrefab = wispSpawner.m_wispPrefab;
            var pickable = wispPrefab.GetComponent<Pickable>();
            if (!pickable) return false;

            var wispItem = pickable.m_itemPrefab.GetComponent<ItemDrop>();
            if (!wispItem) return false;

            var wispData = wispItem.m_itemData.m_shared;
            var wispName = wispData.m_name;

            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(wispSpawnerName);
            var origin = wispSpawner.transform.position;
            foreach (var (container, _) in Logics.GetNearbyContainers(wispSpawnerName, origin))
            {
                var inventory = container.GetInventory();

                var wispCountInContainer = inventory.CountItems(wispName);
                var stacks = wispCountInContainer / wispData.m_maxStackSize;

                if (maxProductStacks > 0)
                {
                    if (stacks >= maxProductStacks) continue;
                    var freeStackSpace = inventory.FindFreeStackSpace(wispName);
                    if (freeStackSpace == 0) continue;
                }

                if (Config.StoreOnlyIfProductExists(wispSpawnerName) &&
                    !Inventories.HaveItem(inventory, wispName, 1)) continue;
                if (!inventory.AddItem(wispItem.gameObject, 1)) continue;

                zNetView.GetZDO().Set("LastSpawn", ZNet.instance.GetTime().Ticks);
                Logics.StoreLog(wispName, 1, container.m_name, container.transform.position,
                    wispSpawnerName, origin);
                return true;
            }

            return false;
        }
    }
}