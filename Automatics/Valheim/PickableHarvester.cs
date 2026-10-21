using System.Collections.Generic;
using ModUtils;
using UnityEngine;

namespace Automatics.Valheim
{
    /// <summary>
    /// Shared owner-side <see cref="Pickable"/> harvest logic (amount scaling,
    /// inventory free-space/weight checks, bonus yield, extra drops, effects, and
    /// the <c>RPC_SetPicked</c> finalize). Both Automatic Pickup and Automatic
    /// Farming harvest crops through this helper so the two modules never derive
    /// the inventory-award behaviour independently.
    /// </summary>
    internal static class PickableHarvester
    {
        internal enum HarvestOutcome
        {
            /// <summary>Nothing was picked (already picked, disabled, no yield, or no inventory room).</summary>
            NotPicked,

            /// <summary>This peer owned the ZDO and awarded the yield directly.</summary>
            PickedByOwner,

            /// <summary>This peer did not own the ZDO; pickup was delegated to the owner via vanilla RPC.</summary>
            DelegatedToOwner
        }

        // Direct inventory awards are safe only while this peer owns the
        // pickable ZDO. Non-owners delegate to vanilla owner-side pickup RPCs.
        public static HarvestOutcome Harvest(Player player, Pickable pickable, ZNetView zNetView)
        {
            if (pickable.GetPicked() || pickable.GetEnabled == 0) return HarvestOutcome.NotPicked;

            var amount = GetPickableAmount(pickable);
            if (amount <= 0) return HarvestOutcome.NotPicked;

            if (!zNetView.IsOwner())
            {
                if (CanAddItem(player, pickable.m_itemPrefab, amount))
                {
                    pickable.Interact(player, false, false);
                    return HarvestOutcome.DelegatedToOwner;
                }

                return HarvestOutcome.NotPicked;
            }

            if (!CanAddItem(player, pickable.m_itemPrefab, amount)) return HarvestOutcome.NotPicked;

            var bonus = RollPickableBonus(player, pickable);

            var inventory = player.GetInventory();
            var offset = 0;
            var itemCount = amount + bonus;
            var addableItemCount = GetAddableStack(player, pickable.m_itemPrefab, itemCount);
            var addedItemCount = AddItem(inventory, pickable.m_itemPrefab, addableItemCount);
            DropRemaining(pickable, pickable.m_itemPrefab, ref offset, itemCount - addedItemCount);
            CreatePickableEffects(pickable, bonus);

            if (!pickable.m_extraDrops.IsEmpty())
            {
                foreach (var item in pickable.m_extraDrops.GetDropListItems())
                {
                    var addableStack = GetAddableStack(player, item.m_dropPrefab, item.m_stack);
                    var addedStack = AddItem(inventory, item.m_dropPrefab, addableStack);
                    DropRemaining(pickable, item.m_dropPrefab, ref offset, item.m_stack - addedStack);
                }
            }

            if (pickable.m_aggravateRange > 0f)
                BaseAI.AggravateAllInArea(pickable.transform.position, pickable.m_aggravateRange,
                    BaseAI.AggravatedReason.Theif);

            zNetView.InvokeRPC(ZNetView.Everybody, "RPC_SetPicked", true);
            return HarvestOutcome.PickedByOwner;
        }

        /// <summary>
        /// Harvests <paramref name="pickable"/> into <paramref name="cropContainers"/>
        /// (nearest-first, owner-only) instead of the player inventory, for
        /// container-based farming. The crop is picked only when the full yield
        /// (base amount + skill bonus + extra drops) fits; otherwise nothing is
        /// deposited, the crop is left standing, and <see cref="HarvestOutcome.NotPicked"/>
        /// is returned so a later pass can retry it. The player-award
        /// <see cref="Harvest"/> above is intentionally left untouched so Automatic
        /// Pickup's behaviour is byte-stable.
        /// </summary>
        public static HarvestOutcome HarvestToContainers(Player player, Pickable pickable,
            ZNetView zNetView, IReadOnlyList<Container> cropContainers,
            IReadOnlyList<Container> ownedElsewhere = null)
        {
            if (pickable.GetPicked() || pickable.GetEnabled == 0) return HarvestOutcome.NotPicked;

            // Depositing the yield and finalizing the pick must be done owner-side;
            // a crop this peer does not own is left to its owner rather than
            // delegated, since the vanilla pickup RPC would award the owner's
            // inventory, not a container.
            if (!zNetView.IsOwner()) return HarvestOutcome.NotPicked;

            // No destination: leave the crop standing before rolling the bonus, so a
            // field with no crop container in range never grants skill for a harvest
            // that cannot happen.
            if (cropContainers == null ||
                cropContainers.Count == 0 && (ownedElsewhere == null || ownedElsewhere.Count == 0))
                return HarvestOutcome.NotPicked;

            var amount = GetPickableAmount(pickable);
            if (amount <= 0) return HarvestOutcome.NotPicked;

            // Roll the bonus YIELD now so the all-or-nothing deposit sizes the full
            // (base + bonus) stack, but defer the pick-skill RAISE until the deposit
            // commits below. RollPickableBonus (used by the player-inventory Harvest
            // path, shared with Automatic Pickup) raises skill as a side effect; that
            // path is safe because once its CanAddItem gate passes the pick always
            // finalizes. This container path can still fail — full boxes leave the crop
            // standing — so raising skill before the deposit would let interval farming
            // grind pick skill off an unharvested crop by keeping the boxes full.
            var bonus = 0;
            if (pickable.m_pickRaiseSkill != Skills.SkillType.None)
            {
                var skillFactor = player.GetSkillFactor(pickable.m_pickRaiseSkill);
                bonus = Random.value < skillFactor * pickable.m_maxLevelBonusChance
                    ? pickable.m_bonusYieldAmount
                    : 0;
            }

            var yields = new List<KeyValuePair<GameObject, int>>();
            if (pickable.m_itemPrefab)
                yields.Add(new KeyValuePair<GameObject, int>(pickable.m_itemPrefab, amount + bonus));

            if (!pickable.m_extraDrops.IsEmpty())
                foreach (var item in pickable.m_extraDrops.GetDropListItems())
                    if (item.m_dropPrefab && item.m_stack > 0)
                        yields.Add(new KeyValuePair<GameObject, int>(item.m_dropPrefab, item.m_stack));

            // Nothing to deposit (no item prefab and no drops): leave the crop alone
            // rather than picking it for no yield.
            if (yields.Count == 0) return HarvestOutcome.NotPicked;

            if (!TryDepositYield(player, cropContainers, yields))
            {
                // The first box another game owns, in deposit order, that takes the whole yield
                // is asked for, and a later pass deposits into it.
                if (ownedElsewhere != null)
                    foreach (var container in ownedElsewhere)
                    {
                        if (!YieldFits(new[] { container }, yields)) continue;
                        ContainerAccess.AskFor(container);
                        break;
                    }

                return HarvestOutcome.NotPicked;
            }

            // Harvest finalized: grant the pick skill that the bonus roll above
            // withheld until the deposit committed, so a full-container failure never
            // awards skill for a crop left standing.
            if (pickable.m_pickRaiseSkill != Skills.SkillType.None)
                player.RaiseSkill(pickable.m_pickRaiseSkill);

            CreatePickableEffects(pickable, bonus);

            if (pickable.m_aggravateRange > 0f)
                BaseAI.AggravateAllInArea(pickable.transform.position, pickable.m_aggravateRange,
                    BaseAI.AggravatedReason.Theif);

            zNetView.InvokeRPC(ZNetView.Everybody, "RPC_SetPicked", true);
            return HarvestOutcome.PickedByOwner;
        }

        // All-or-nothing owner-only deposit. Adds every yield across the containers
        // (nearest-first), and if any item cannot be fully placed, removes
        // everything it added so the crop can be left standing and retried. Mirrors
        // Automatic Storage's count-delta add and rollback, so a deposit never
        // leaves a partial yield in a container while the crop stays unharvested.
        // The whole yield is first reserved in the containers (YieldFits), so a
        // crop whose yield cannot fit is left standing without any container being
        // written; the rollback remains for a shortfall the reservation cannot
        // foresee (a container the gate then refuses).
        private static bool TryDepositYield(Player player, IReadOnlyList<Container> containers,
            IReadOnlyList<KeyValuePair<GameObject, int>> yields)
        {
            if (!YieldFits(containers, yields)) return false;

            var deposited = new List<KeyValuePair<Container, KeyValuePair<GameObject, int>>>();
            var complete = true;

            foreach (var yield in yields)
            {
                var remaining = yield.Value;
                foreach (var container in containers)
                {
                    if (remaining <= 0) break;

                    var added = DepositIntoContainer(player, container, yield.Key, remaining);
                    if (added <= 0) continue;

                    deposited.Add(new KeyValuePair<Container, KeyValuePair<GameObject, int>>(
                        container, new KeyValuePair<GameObject, int>(yield.Key, added)));
                    remaining -= added;
                }

                if (remaining > 0)
                {
                    complete = false;
                    break;
                }
            }

            if (complete) return true;

            // The synchronous owner-only gates that let each deposit through still
            // hold for this same-frame rollback, so a shortfall is not expected; if
            // the all-or-nothing invariant is ever violated (leftover yield could be
            // re-harvested next pass), surface it at Debug level rather than silently.
            var depositedTotal = 0;
            var rolledBack = 0;
            foreach (var entry in deposited)
            {
                depositedTotal += entry.Value.Value;
                rolledBack += WithdrawFromContainer(player, entry.Key, entry.Value.Key,
                    entry.Value.Value);
            }

            if (rolledBack < depositedTotal)
                Automatics.Logger.Debug(() =>
                    $"Farming deposit rollback incomplete: restored {rolledBack} of {depositedTotal}");
            return false;
        }

        // Whether every yield fits the containers together. It reserves room in the
        // order the deposit adds (each yield in turn, nearest container first), as
        // Inventory.AddItem fills it: the space left in stacks of the same item
        // first, then whole empty slots, a slot's unused part becoming that item's
        // stack space. What one yield reserves is gone for the next, so yields that
        // compete for the same slot or stack are not both counted. Each container's
        // saved contents are loaded first; nothing is claimed or written.
        private static bool YieldFits(IReadOnlyList<Container> containers,
            IReadOnlyList<KeyValuePair<GameObject, int>> yields)
        {
            var emptySlots = new Dictionary<Container, int>();
            var stackSpace = new Dictionary<(Container, string, int), int>();
            foreach (var yield in yields)
            {
                var item = CreatePickupItemData(yield.Key, 1);
                if (item == null) return false;

                var maxStackSize = Mathf.Max(1, item.m_shared.m_maxStackSize);
                var remaining = yield.Value;
                foreach (var container in containers)
                {
                    if (remaining <= 0) break;

                    if (!emptySlots.TryGetValue(container, out var slots))
                    {
                        ContainerAccess.LoadSavedContents(container);
                        var contents = container ? container.GetInventory() : null;
                        slots = contents == null ? 0 : Mathf.Max(0, contents.GetEmptySlots());
                        emptySlots[container] = slots;
                    }

                    // Every yield is at the current world level, so name and quality
                    // tell which stacks it can fill.
                    var kind = (container, item.m_shared.m_name, item.m_quality);
                    if (!stackSpace.TryGetValue(kind, out var space))
                    {
                        var inventory = container ? container.GetInventory() : null;
                        space = inventory == null
                            ? 0
                            : Mathf.Max(0, GetFreeStackSpace(inventory, item) -
                                           inventory.GetEmptySlots() * maxStackSize);
                    }

                    var reserved = Mathf.Min(remaining, space + slots * maxStackSize);
                    var intoStacks = Mathf.Min(reserved, space);
                    var intoSlots = reserved - intoStacks;
                    var usedSlots = (intoSlots + maxStackSize - 1) / maxStackSize;
                    emptySlots[container] = slots - usedSlots;
                    stackSpace[kind] = space - intoStacks + usedSlots * maxStackSize - intoSlots;
                    remaining -= reserved;
                }

                if (remaining > 0) return false;
            }

            return true;
        }

        // Adds no more than the container has room for, and passes over a full one
        // before the gate, so a full container is not written.
        private static int DepositIntoContainer(Player player, Container container, GameObject prefab,
            int amount)
        {
            if (ContainerAccess.GetRoom(container, prefab) <= 0) return 0;
            if (!ContainerAccess.TryPrepareContainerMutation(player, container)) return 0;

            var inventory = container.GetInventory();
            return inventory == null
                ? 0
                : AddItem(inventory, prefab, Mathf.Min(amount, ContainerAccess.GetRoom(inventory, prefab)));
        }

        // Returns the number actually restored so the caller can verify the
        // all-or-nothing rollback completed; the owner-only gates re-checked here
        // match the deposit's, so under the synchronous single-frame model the
        // return always equals the requested amount.
        private static int WithdrawFromContainer(Player player, Container container,
            GameObject prefab, int amount)
        {
            if (amount <= 0) return 0;
            if (!ContainerAccess.TryPrepareContainerMutation(player, container)) return 0;

            var inventory = container.GetInventory();
            if (inventory == null) return 0;

            // Roll back by the exact item identity that was deposited (name + quality
            // + world level), not by name alone, so a failed all-or-nothing deposit
            // removes only the items it just added and never strips pre-existing
            // stock of the same crop stored at a different world level. The deposit
            // (AddItem/CountExactItems) counts that exact identity, so removing it
            // keeps the rollback symmetric with the add.
            var sample = CreatePickupItemData(prefab, 1);
            if (sample == null) return 0;

            var remaining = amount;
            foreach (var item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
            {
                if (remaining <= 0) break;
                if (item.m_shared.m_name != sample.m_shared.m_name) continue;
                if (item.m_quality != sample.m_quality) continue;
                if (item.m_worldLevel != sample.m_worldLevel) continue;

                var remove = Mathf.Min(remaining, item.m_stack);
                if (remove <= 0) continue;
                if (!inventory.RemoveItem(item, remove)) continue;

                remaining -= remove;
            }

            return amount - remaining;
        }

        public static bool CanAddItem(Player player, ItemDrop.ItemData itemData, int stack = -1)
        {
            var inventory = player.GetInventory();
            var itemStack = stack > 0 ? stack : itemData.m_stack;
            return GetFreeStackSpace(inventory, itemData) >= itemStack &&
                   CanCarryItem(player, itemData, itemStack);
        }

        public static bool CanAddItem(Player player, GameObject prefab, int stack = -1)
        {
            var itemData = CreatePickupItemData(prefab, stack);
            return itemData != null && CanAddItem(player, itemData, stack);
        }

        public static int AddItem(Inventory inventory, GameObject prefab, int amount)
        {
            if (amount <= 0) return 0;

            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null) return 0;

            var maxStackSize = Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxStackSize);
            var sample = CreatePickupItemData(prefab, 1);
            if (sample == null) return 0;

            var before = CountExactItems(inventory, sample);
            while (amount > 0)
            {
                var stack = Mathf.Min(amount, maxStackSize);
                var itemData = CreatePickupItemData(prefab, stack);
                if (itemData == null) break;

                if (!inventory.AddItem(itemData)) break;
                amount -= stack;
            }

            return CountExactItems(inventory, sample) - before;
        }

        public static ItemDrop.ItemData CreatePickupItemData(GameObject prefab, int stack)
        {
            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null) return null;

            var itemData = itemDrop.m_itemData.Clone();
            itemData.m_dropPrefab = prefab;
            itemData.m_stack = stack > 0 ? stack : itemDrop.m_itemData.m_stack;
            itemData.m_worldLevel = (byte)Game.m_worldLevel;
            return itemData;
        }

        public static int GetAddableStack(Player player, GameObject prefab, int stack)
        {
            var itemData = CreatePickupItemData(prefab, stack);
            if (itemData == null) return 0;

            return Mathf.Min(stack, GetFreeStackSpace(player.GetInventory(), itemData),
                GetCarryableStack(player, itemData));
        }

        public static int GetFreeStackSpace(Inventory inventory, ItemDrop.ItemData itemData)
        {
            var maxStackSize = Mathf.Max(1, itemData.m_shared.m_maxStackSize);
            var space = inventory.GetEmptySlots() * maxStackSize;

            foreach (var item in inventory.GetAllItems())
            {
                if (item.m_shared.m_name != itemData.m_shared.m_name) continue;
                if (item.m_quality != itemData.m_quality) continue;
                if (item.m_worldLevel != itemData.m_worldLevel) continue;
                if (item.m_stack >= item.m_shared.m_maxStackSize) continue;

                space += item.m_shared.m_maxStackSize - item.m_stack;
            }

            return space;
        }

        public static bool CanCarryItem(Player player, ItemDrop.ItemData itemData, int stack)
        {
            return GetCarryableStack(player, itemData) >= stack;
        }

        public static int GetCarryableStack(Player player, ItemDrop.ItemData itemData)
        {
            var itemWeight = itemData.GetWeight(1);
            if (itemWeight <= 0f) return int.MaxValue;

            var remainingWeight = player.GetMaxCarryWeight() - player.GetInventory().GetTotalWeight();
            return Mathf.Max(0, Mathf.FloorToInt(remainingWeight / itemWeight));
        }

        public static int CountExactItems(Inventory inventory, ItemDrop.ItemData itemData)
        {
            var count = 0;
            foreach (var item in inventory.GetAllItems())
            {
                if (item.m_shared.m_name != itemData.m_shared.m_name) continue;
                if (item.m_quality != itemData.m_quality) continue;
                if (item.m_worldLevel != itemData.m_worldLevel) continue;

                count += item.m_stack;
            }

            return count;
        }

        private static void DropRemaining(Pickable pickable, GameObject prefab, ref int offset,
            int stack)
        {
            if (stack <= 0) return;

            Reflections.InvokeMethod(pickable, "Drop", prefab, offset++, stack);
        }

        private static int GetPickableAmount(Pickable pickable)
        {
            return pickable.m_dontScale
                ? pickable.m_amount
                : Mathf.Max(pickable.m_minAmountScaled,
                    Game.instance.ScaleDrops(pickable.m_itemPrefab, pickable.m_amount));
        }

        private static int RollPickableBonus(Player player, Pickable pickable)
        {
            if (pickable.m_pickRaiseSkill == Skills.SkillType.None) return 0;

            player.RaiseSkill(pickable.m_pickRaiseSkill);
            var skillFactor = player.GetSkillFactor(pickable.m_pickRaiseSkill);
            return Random.value < skillFactor * pickable.m_maxLevelBonusChance
                ? pickable.m_bonusYieldAmount
                : 0;
        }

        private static void CreatePickableEffects(Pickable pickable, int bonus)
        {
            if (bonus > 0)
            {
                if (DamageText.instance)
                    DamageText.instance.ShowText(DamageText.TextType.Bonus,
                        pickable.transform.position + Vector3.up * pickable.m_spawnOffset,
                        $"+{bonus}", player: true);
                pickable.m_bonusEffect.Create(pickable.transform.position, Quaternion.identity);
                ZLog.Log("Bonus food picked!");
            }

            var basePos = pickable.m_pickEffectAtSpawnPoint
                ? pickable.transform.position + Vector3.up * pickable.m_spawnOffset
                : pickable.transform.position;
            pickable.m_pickEffector.Create(basePos, Quaternion.identity);
        }
    }
}
