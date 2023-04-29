using System;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class FermenterProcess
    {
        private static Status GetStatus(Fermenter fermenter)
        {
            switch (Reflections.InvokeMethod<int>(fermenter, "GetStatus"))
            {
                case 0: return Status.Empty;
                case 1: return Status.Fermenting;
                case 2: return Status.Exposed;
                case 3: return Status.Ready;
                case int status:
                    throw new Exception($"Illegal Fermenter status: {status}");
            }
        }

        public static void Craft(Fermenter fermenter, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fermenterName = fermenter.m_name;
            if (!Logics.IsAllowProcessing(fermenterName, Process.Craft)) return;

            if (GetStatus(fermenter) != Status.Empty) return;
            if (Reflections.GetField<bool>(fermenter, "m_exposed")) return;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(fermenterName);
            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(fermenterName);

            var origin = fermenter.transform.position;
            var containersWithInventory = Logics
                .GetNearbyContainers(fermenterName, origin)
                .Select(x => (x.container, x.container.GetInventory())).ToList();

            foreach (var conversion in fermenter.m_conversion)
            {
                Container materialContainer = null;
                var productContainerFound = false;

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containersWithInventory)
                {
                    if (materialContainer && productContainerFound) break;

                    if (materialContainer == null)
                        if (Inventories.HaveItem(inventory, materialData.m_name,
                                minMaterialCount + 1))
                            materialContainer = container;

                    if (maxProductStacks > 0 && !productContainerFound)
                    {
                        var productCount = inventory.CountItems(productData.m_name);
                        var stacks = productCount / productData.m_maxStackSize;
                        if (stacks < maxProductStacks)
                            productContainerFound =
                                inventory.CanAddItem(productItem, conversion.m_producedItems);
                    }
                }

                if (materialContainer && (maxProductStacks == 0 || productContainerFound))
                {
                    var item = materialContainer.GetInventory().GetItem(materialData.m_name);
                    materialContainer.GetInventory().RemoveOneItem(item);
                    zNetView.InvokeRPC("AddItem", item.m_dropPrefab.name);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position,
                        fermenterName, origin, productData.m_name);
                    break;
                }
            }
        }

        public static void Store(Fermenter fermenter, ZNetView zNetView)
        {
            if (!Config.EnableAutomaticProcessing) return;
            if (!zNetView.IsValid() || !zNetView.IsOwner()) return;

            var fermenterName = fermenter.m_name;
            if (!Logics.IsAllowProcessing(fermenterName, Process.Store)) return;

            if (GetStatus(fermenter) != Status.Ready) return;

            var itemId = zNetView.GetZDO().GetString("Content");
            var conversion =
                fermenter.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == itemId);
            if (conversion == null) return;

            var origin = fermenter.transform.position;
            var item = conversion.m_to;
            var itemData = item.m_itemData.m_shared;
            var itemName = itemData.m_name;

            var productRemaining = conversion.m_producedItems;
            foreach (var (container, _) in Logics.GetNearbyContainers(fermenterName, origin))
            {
                if (productRemaining <= 0) break;

                var inventory = container.GetInventory();
                var itemCountBefore = inventory.CountItems(itemName);
                if (Config.StoreOnlyIfProductExists(fermenterName) &&
                    !Inventories.HaveItem(inventory, itemName, 1)) continue;
                if (!inventory.AddItem(item.gameObject, productRemaining)) continue;

                var storedItemCount = inventory.CountItems(itemName) - itemCountBefore;
                Logics.StoreLog(itemName, storedItemCount, container.m_name,
                    container.transform.position, fermenterName, origin);
                productRemaining -= storedItemCount;
            }

            if (productRemaining == conversion.m_producedItems) return;

            zNetView.GetZDO().Set("Content", "");
            zNetView.GetZDO().Set("StartTime", 0);

            fermenter.m_spawnEffects.Create(fermenter.m_outputPoint.transform.position,
                Quaternion.identity);
            while (productRemaining > 0)
            {
                var position = fermenter.m_outputPoint.position + Vector3.up * 0.3f;
                UnityEngine.Object.Instantiate(conversion.m_to, position, Quaternion.identity);
                productRemaining--;
            }
        }

        // Fermenter.Status
        private enum Status
        {
            Empty,
            Fermenting,
            Exposed,
            Ready
        }
    }
}