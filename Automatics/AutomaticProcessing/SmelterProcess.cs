using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class SmelterProcess
    {
        private static readonly HashSet<ZDOID> SkipQuickCraft;
        private static readonly HashSet<ZDOID> FirstQuickCraft;
        private static readonly HashSet<ZDOID> SkipQuickRefuel;
        private static readonly HashSet<ZDOID> FirstQuickRefuel;

        // The per-frame Craft/Refuel postfixes (Smelter.UpdateSmelter runs every
        // frame on the owner) throttle their nearby-container scan: after a pass
        // that adds nothing, the smelter is skipped for ~1s instead of re-running
        // the Dictionary/List/LINQ scan every frame. Mirrors the QuickCraft path.
        private static readonly HashSet<ZDOID> SkipCraft;
        private static readonly HashSet<ZDOID> SkipRefuel;

        private static float _lastQuickCraftReset;
        private static float _lastQuickRefuelReset;
        private static float _lastCraftReset;
        private static float _lastRefuelReset;

        static SmelterProcess()
        {
            SkipQuickCraft = new HashSet<ZDOID>();
            FirstQuickCraft = new HashSet<ZDOID>();
            SkipQuickRefuel = new HashSet<ZDOID>();
            FirstQuickRefuel = new HashSet<ZDOID>();
            SkipCraft = new HashSet<ZDOID>();
            SkipRefuel = new HashSet<ZDOID>();
        }

        private static Smelter.ItemConversion GetItemConversion(Smelter smelter, string queuedOre)
        {
            return smelter.m_conversion.FirstOrDefault(x => x.m_from.gameObject.name == queuedOre);
        }

        // Smelter.QueueProcessed holds finished product back in the "SpawnOre"/"SpawnAmount"
        // ZDO values (ZDOVars.s_spawnOre/s_spawnAmount), one per processed ore, until a full
        // stack or an empty queue releases it through SpawnProcessed as the conversion's m_to.
        private static void CountHeldProduct(Smelter smelter, ZDO zdo,
            Dictionary<string, int> productCounts)
        {
            var amount = zdo.GetInt("SpawnAmount");
            if (amount <= 0) return;

            var conversion = GetItemConversion(smelter, zdo.GetString("SpawnOre"));
            if (conversion == null) return;

            var productName = conversion.m_to.m_itemData.m_shared.m_name;
            productCounts[productName] =
                productCounts.TryGetValue(productName, out var count) ? count + amount : amount;
        }

        public static void Cleanup()
        {
            SkipQuickCraft.Clear();
            FirstQuickCraft.Clear();
            SkipQuickRefuel.Clear();
            FirstQuickRefuel.Clear();
            SkipCraft.Clear();
            SkipRefuel.Clear();

            // The reset times go with the sets they pace, or the first pass after a disable or a
            // new session would inherit the previous one's cadence.
            _lastQuickCraftReset = 0f;
            _lastQuickRefuelReset = 0f;
            _lastCraftReset = 0f;
            _lastRefuelReset = 0f;
        }

        [UsedImplicitly]
        public static string QuickCraft(Smelter smelter, ZNetView zNetView, float loopCount,
            string ore)
        {
            if (!Module.Running) return ore;
            if (loopCount < 1f) return ore;
            if (!string.IsNullOrEmpty(ore)) return ore;
            if (!Objects.HasValidOwnership(zNetView)) return ore;

            if (Time.time - _lastQuickCraftReset > 1f)
            {
                _lastQuickCraftReset = Time.time;
                SkipQuickCraft.Clear();
                FirstQuickCraft.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipQuickCraft.Contains(uid)) return ore;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Craft)) return ore;

            // Smelter.OnAddOre refuses once the queue is full; QuickCraft is injected
            // ahead of UpdateSmelter's own m_maxOre gate, so enforce the same bound here
            // (notably m_maxOre == 0, where the station accepts no ore at all).
            if (zNetView.GetZDO().GetInt("queued") >= smelter.m_maxOre) return ore;

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(smelterName);
            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(smelterName);

            var transform = smelter.transform;
            var origin = transform.position;
            var containersWithInventory = Logics.GetNearbyContainers(smelterName, origin)
                .Select(x => (x.container, Logics.ReadInventory(x.container)))
                .ToList();
            var inventories = containersWithInventory.Select(x => x.Item2).ToList();

            var productCounts = new Dictionary<string, int>();
            CountHeldProduct(smelter, zNetView.GetZDO(), productCounts);

            var oreAdded = false;
            foreach (var conversion in smelter.m_conversion)
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

                // Finished product the station still holds counts toward the limit too.
                productCounts.TryGetValue(productData.m_name, out var pending);
                var productContainerFound = maxProductStacks > 0 && materialContainers.Count > 0 &&
                    Logics.CanTakeMoreProduct(inventories,
                        productItem, pending, 1, maxProductStacks);

                if (materialContainers.Count > 0 &&
                    (maxProductStacks == 0 || productContainerFound))
                {
                    // Walks the candidates in distance order, skipping any that holds no
                    // eligible item without claiming it.
                    if (!Logics.TryTakeMaterial(materialContainers, materialData.m_name,
                            minMaterialCount, conversion.m_from.gameObject.name,
                            out var materialContainer, out var prefabName, out var cheated))
                        continue;

                    // Smelter.QueueOre(string name, bool cheated): `cheated` records whether
                    // the queued ore was cheat-spawned, and is what RPC_AddOre forwards.
                    Reflections.InvokeMethod(smelter, "QueueOre", prefabName, cheated);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position, smelterName,
                        origin, productData.m_name);

                    oreAdded = true;
                    break;
                }
            }

            if (!oreAdded)
            {
                SkipQuickCraft.Add(uid);
                return ore;
            }

            if (FirstQuickCraft.Add(uid))
                smelter.m_oreAddedEffects.Create(origin, transform.rotation);

            return zNetView.GetZDO().GetString("item0");
        }

        [UsedImplicitly]
        public static void Craft(Smelter smelter, ZNetView zNetView)
        {
            if (!Module.Running) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Craft)) return;

            if (Time.time - _lastCraftReset > 1f)
            {
                _lastCraftReset = Time.time;
                SkipCraft.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipCraft.Contains(uid)) return;

            var oreCount = zNetView.GetZDO().GetInt("queued");
            if (oreCount >= smelter.m_maxOre) return;
            if (Config.SupplyOnlyWhenMaterialsRunOut(smelterName) && oreCount > 0) return;

            var productCounts = new Dictionary<string, int>();
            var conversions = new List<Smelter.ItemConversion>();
            for (var i = 0; i < oreCount; i++)
            {
                var queuedOre = zNetView.GetZDO().GetString("item" + i);
                if (string.IsNullOrEmpty(queuedOre)) continue;

                var conversion = GetItemConversion(smelter, queuedOre);
                if (conversion == null) continue;

                conversions.Add(conversion);
                var productName = conversion.m_to.m_itemData.m_shared.m_name;
                productCounts[productName] =
                    productCounts.TryGetValue(productName, out var count) ? count + 1 : 1;
            }
            CountHeldProduct(smelter, zNetView.GetZDO(), productCounts);

            conversions.Reverse();
            conversions.AddRange(smelter.m_conversion.Where(x => !conversions.Contains(x)));

            var minMaterialCount = Config.MaterialCountOfSuppressProcessing(smelterName);
            var maxProductStacks = Config.ProductStacksOfSuppressProcessing(smelterName);

            var origin = smelter.transform.position;
            var containersWithInventory = Logics.GetNearbyContainers(smelterName, origin)
                .Select(x => (x.container, Logics.ReadInventory(x.container)))
                .ToList();
            var inventories = containersWithInventory.Select(x => x.Item2).ToList();

            var oreAdded = false;
            foreach (var conversion in conversions)
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

                // Products already queued, cooking or held for release count toward the limit too.
                productCounts.TryGetValue(productData.m_name, out var pending);
                var productContainerFound = maxProductStacks > 0 && materialContainers.Count > 0 &&
                    Logics.CanTakeMoreProduct(inventories,
                        productItem, pending, 1, maxProductStacks);

                if (materialContainers.Count > 0 &&
                    (maxProductStacks == 0 || productContainerFound))
                {
                    // Walks the candidates in distance order, skipping any that holds no
                    // eligible item without claiming it.
                    if (!Logics.TryTakeMaterial(materialContainers, materialData.m_name,
                            minMaterialCount, conversion.m_from.gameObject.name,
                            out var materialContainer, out var prefabName, out var cheated))
                        continue;
                    // Smelter registers Register<string, bool>("RPC_AddOre", RPC_AddOre).
                    zNetView.InvokeRPC("RPC_AddOre", prefabName, cheated);

                    Logics.CraftingLog(materialData.m_name, 1,
                        materialContainer.m_name, materialContainer.transform.position, smelterName,
                        origin, productData.m_name);

                    oreAdded = true;
                    break;
                }
            }

            if (!oreAdded) SkipCraft.Add(uid);
        }

        [UsedImplicitly]
        public static float QuickRefuel(Smelter smelter, ZNetView zNetView, float loopCount,
            float fuel)
        {
            if (!Module.Running) return fuel;
            if (loopCount < 1f) return fuel;
            if (fuel > 0f) return fuel;
            if (!Objects.HasValidOwnership(zNetView)) return fuel;

            if (Time.time - _lastQuickRefuelReset > 1f)
            {
                _lastQuickRefuelReset = Time.time;
                SkipQuickRefuel.Clear();
                FirstQuickRefuel.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipQuickRefuel.Contains(uid)) return fuel;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Refuel)) return fuel;

            if (Config.RefuelOnlyWhenMaterialsSupplied(smelterName) &&
                zNetView.GetZDO().GetInt("queued") == 0)
                return fuel;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(smelterName);
            var transform = smelter.transform;
            var origin = transform.position;
            var fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;

            var fuelAdded = false;
            foreach (var (container, _) in Logics.GetNearbyContainers(smelterName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, 0, WorldLevelMatchMode.Ignore, minFuelCount + 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                if (!Logics.TryRemoveItem(inventory, fuelName, minFuelCount, out _)) continue;
                zNetView.InvokeRPC("RPC_AddFuel");

                Logics.RefuelLog(fuelName, 1, smelterName, origin, container.m_name,
                    container.transform.position);

                fuelAdded = true;
                break;
            }

            if (!fuelAdded)
            {
                SkipQuickRefuel.Add(uid);
                return fuel;
            }

            fuel += 1f;
            zNetView.GetZDO().Set("fuel", fuel);

            if (FirstQuickRefuel.Add(uid))
                smelter.m_fuelAddedEffects.Create(origin, transform.rotation, transform);

            return fuel;
        }

        [UsedImplicitly]
        public static void Refuel(Smelter smelter, ZNetView zNetView)
        {
            if (!Module.Running) return;
            if (!Objects.HasValidOwnership(zNetView)) return;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Refuel)) return;

            if (Time.time - _lastRefuelReset > 1f)
            {
                _lastRefuelReset = Time.time;
                SkipRefuel.Clear();
            }

            var uid = zNetView.GetZDO().m_uid;
            if (SkipRefuel.Contains(uid)) return;

            var fuel = zNetView.GetZDO().GetFloat("fuel");
            // The same limit as the game's own Smelter.OnAddFuel.
            if (fuel > smelter.m_maxFuel - 1) return;
            if (Config.RefuelOnlyWhenOutOfFuel(smelterName) && fuel > 0f) return;
            if (Config.RefuelOnlyWhenMaterialsSupplied(smelterName) &&
                zNetView.GetZDO().GetInt("queued") == 0)
                return;

            var minFuelCount = Config.FuelCountOfSuppressProcessing(smelterName);
            var transform = smelter.transform;
            var origin = transform.position;
            var fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;

            var fuelAdded = false;
            foreach (var (container, _) in Logics.GetNearbyContainers(smelterName, origin))
            {
                var inventory = container.GetInventory();
                if (!Inventories.HaveItem(inventory, fuelName, 0, WorldLevelMatchMode.Ignore, minFuelCount + 1)) continue;
                if (!Logics.TryClaimContainer(container)) continue;

                if (!Logics.TryRemoveItem(inventory, fuelName, minFuelCount, out _)) continue;
                zNetView.InvokeRPC("RPC_AddFuel");

                Logics.RefuelLog(fuelName, 1, smelterName, origin, container.m_name,
                    container.transform.position);

                fuelAdded = true;
                break;
            }

            if (!fuelAdded) SkipRefuel.Add(uid);
        }

        public static bool Store(Smelter smelter, string ore, int stack)
        {
            if (!Module.Running) return false;
            if (!Objects.HasValidOwnership(smelter, out var zNetView)) return false;

            var smelterName = smelter.m_name;
            if (!Logics.IsAllowProcessing(smelterName, Process.Store)) return false;

            var conversion = GetItemConversion(smelter, ore);
            if (conversion == null) return false;

            var origin = smelter.transform.position;
            var item = conversion.m_to;
            var itemData = item.m_itemData.m_shared;
            var itemName = itemData.m_name;

            var productRemaining = stack;
            foreach (var (container, _) in Logics.GetNearbyContainers(smelterName, origin))
            {
                if (productRemaining <= 0) break;

                var inventory = container.GetInventory();
                if (!Logics.AcceptsProduct(smelterName, inventory, itemName)) continue;
                if (!Logics.TryClaimContainer(container)) continue;
                if (!Logics.AcceptsProduct(smelterName, inventory, itemName)) continue;

                var storedItemCount = Logics.AddItemAndGetCountDelta(inventory, item.gameObject,
                    itemName, productRemaining);
                if (storedItemCount <= 0) continue;

                Logics.StoreLog(itemName, storedItemCount, container.m_name,
                    container.transform.position, smelterName, origin);
                productRemaining -= storedItemCount;
            }

            if (productRemaining == stack) return false;

            smelter.m_produceEffects.Create(origin, smelter.transform.rotation);
            if (productRemaining > 0)
            {
                var instance = Object.Instantiate(conversion.m_to.gameObject,
                    smelter.m_outputPoint.position, smelter.m_outputPoint.rotation);
                instance.GetComponent<ItemDrop>().m_itemData.m_stack = productRemaining;
            }

            return true;
        }
    }
}
