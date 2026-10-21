using System;
using System.Collections.Generic;
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
            if (!Module.Running) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

            var fermenterName = fermenter.m_name;
            if (!Logics.IsAllowProcessing(fermenterName, Process.Craft)) return;

            if (GetStatus(fermenter) != Status.Empty) return;
            if (Reflections.GetField<bool>(fermenter, "m_exposed")) return;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(fermenterName);
            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(fermenterName);

            var origin = fermenter.transform.position;
            var containersWithInventory = Logics
                .GetNearbyContainers(fermenterName, origin)
                .Select(x => (x.container, Logics.ReadInventory(x.container))).ToList();
            var inventories = containersWithInventory.Select(x => x.Item2).ToList();

            Container askable = null;
            foreach (var conversion in fermenter.m_conversion)
            {
                var materialContainers = new List<Container>();

                var materialItem = conversion.m_from.m_itemData;
                var materialData = materialItem.m_shared;
                var productItem = conversion.m_to.m_itemData;
                var productData = productItem.m_shared;
                foreach (var (container, inventory) in containersWithInventory)
                {
                    // Keep every container that satisfies the material predicate, not just
                    // the first: the pick is by shared display name while the removal is by
                    // drop prefab name, so the first match is not guaranteed to hold an item
                    // this conversion accepts. Latching only the first lets a permanent
                    // display-name decoy starve the conversion for good.
                    if (Inventories.HaveItem(inventory, materialData.m_name,
                            0, WorldLevelMatchMode.Ignore, minMaterialCount + 1))
                        materialContainers.Add(container);
                }

                var productContainerFound = maxProductStacks > 0 && materialContainers.Count > 0 &&
                    Logics.CanTakeMoreProduct(inventories,
                        productItem, 0, conversion.m_producedItems, maxProductStacks);

                if (materialContainers.Count > 0 &&
                    (maxProductStacks == 0 || productContainerFound))
                {
                    // Walks the candidates in distance order, skipping any that holds no
                    // eligible item without claiming it.
                    if (!Logics.TryTakeMaterial(materialContainers, materialData.m_name,
                            minMaterialCount, conversion.m_from.gameObject.name, origin,
                            ref askable, out var materialContainer, out var prefabName,
                            out var cheated))
                        continue;
                    // Fermenter registers Register<int, bool>("RPC_AddItem", RPC_AddItem) and
                    // Fermenter.AddItem sends item.m_dropPrefab.name.GetStableHashCode().
                    zNetView.InvokeRPC("RPC_AddItem", prefabName.GetStableHashCode(), cheated);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position,
                        fermenterName, origin, productData.m_name);
                    return;
                }
            }

            Logics.AskFor(askable);
        }

        public static void Store(Fermenter fermenter, ZNetView zNetView)
        {
            if (!Module.Running) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

            var fermenterName = fermenter.m_name;
            if (!Logics.IsAllowProcessing(fermenterName, Process.Store)) return;

            if (GetStatus(fermenter) != Status.Ready) return;

            // Fermenter stores its content as the drop prefab name's stable hash
            // (RPC_AddItem does ZDO.Set(ZDOVars.s_content, nameHash)) and resolves it back
            // with GetItemConversion(nameHash). ZDOVars.s_content is "Content"
            // .GetStableHashCode() and ZDO's string-key overloads hash the key the same
            // way, so this reads the very slot Valheim writes.
            var contentHash = zNetView.GetZDO().GetInt("Content");
            var conversion = fermenter.m_conversion.FirstOrDefault(
                x => x.m_from.gameObject.name.GetStableHashCode() == contentHash);
            if (conversion == null) return;

            var origin = fermenter.transform.position;
            var item = conversion.m_to;
            var itemData = item.m_itemData.m_shared;
            var itemName = itemData.m_name;

            var productRemaining = conversion.m_producedItems;
            Container askable = null;
            foreach (var (container, _) in Logics.GetNearbyContainers(fermenterName, origin))
            {
                if (productRemaining <= 0) break;

                if (!Logics.CanStore(fermenterName, container, item.gameObject, itemName)) continue;
                if (!Logics.TryClaimContainer(container, ref askable)) continue;
                var inventory = container.GetInventory();
                if (!Logics.AcceptsProduct(fermenterName, inventory, itemName)) continue;

                var storedItemCount = Logics.AddItemAndGetCountDelta(inventory, item.gameObject,
                    itemName, productRemaining);
                if (storedItemCount <= 0) continue;

                Logics.StoreLog(itemName, storedItemCount, container.m_name,
                    container.transform.position, fermenterName, origin);
                productRemaining -= storedItemCount;
            }

            if (productRemaining > 0) Logics.AskFor(askable);
            if (productRemaining == conversion.m_producedItems) return;

            // Fermenter.RPC_Tap/DropAllItems empty the barrel with Set(s_content, 0);
            // 0 is the value GetStatus() reads as Status.Empty.
            zNetView.GetZDO().Set("Content", 0);
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
