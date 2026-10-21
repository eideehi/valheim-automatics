using System;
using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticSupply
{
    /// <summary>
    ///     One player action that may take items from nearby containers: a craft, a placement, a
    ///     station intake or a manual planting grid. It records what it took and what the action
    ///     then removed from the player inventory, so that what it took and did not use can go back.
    /// </summary>
    internal sealed class SupplyAction
    {
        internal SupplyAction(Player player)
        {
            Player = player;
            Inventory = player.GetInventory();
        }

        internal Player Player { get; }
        internal Inventory Inventory { get; }

        /// <summary>One entry per stack moved: where it came from and how many units arrived.</summary>
        internal List<SupplyLedgerEntry> Ledger { get; } = new List<SupplyLedgerEntry>();

        /// <summary>Units removed from the player inventory while the action was open, per name.</summary>
        internal Dictionary<string, int> Debited { get; } = new Dictionary<string, int>();

        /// <summary>True when a take stopped short because the player inventory had no room.</summary>
        internal bool InventoryFull { get; set; }

        internal int Pulled(string name)
        {
            return Ledger.Where(x => x.Name == name).Sum(x => x.Units);
        }

        internal int DebitedUnits(string name)
        {
            return Debited.TryGetValue(name, out var units) ? units : 0;
        }

        /// <summary>Taken and not removed by the action again: what goes back on a normal end.</summary>
        internal int Unconsumed(string name)
        {
            var pulled = Pulled(name);
            return pulled - Mathf.Min(pulled, DebitedUnits(name));
        }
    }

    internal sealed class SupplyLedgerEntry
    {
        internal SupplyLedgerEntry(Container container, ItemDrop.ItemData template, int units)
        {
            Container = container;
            Template = template;
            Units = units;
        }

        internal Container Container { get; }

        /// <summary>A copy of the stack the units came from, metadata included.</summary>
        internal ItemDrop.ItemData Template { get; }

        internal string Name => Template.m_shared.m_name;
        internal int Units { get; set; }
    }

    /// <summary>
    ///     Moves items between nearby containers and the player inventory. Every count is measured
    ///     before and after the move, never taken from a preflight: an item leaves a container only
    ///     by a removal that is measured, and arrives in the player inventory only by an insertion
    ///     that is measured. What an insertion did not take goes straight back to the container it
    ///     came from. Nothing is dropped and nothing is created.
    /// </summary>
    internal static class SupplyTransfer
    {
        private static SupplyAction _current;
        private static int _transferring;

        /// <summary>The open action, if any. One is open at a time.</summary>
        internal static SupplyAction Current => _current;

        /// <summary>True while this class itself adds to or removes from an inventory.</summary>
        internal static bool Transferring => _transferring > 0;

        /// <summary>Opens an action, or returns null while another one is open.</summary>
        internal static SupplyAction Begin(Player player)
        {
            if (!player || _current != null) return null;
            _current = new SupplyAction(player);
            return _current;
        }

        /// <summary>
        ///     Opens an action for manual grid planting, which pays for its crops from the inventory:
        ///     null unless this module is running.
        /// </summary>
        internal static SupplyAction BeginIfRunning(Player player)
        {
            return Module.Running ? Begin(player) : null;
        }

        /// <summary>
        ///     Closes <paramref name="action" />. After a normal end, whatever it took and did not
        ///     use goes back to the containers it came from; what cannot go back stays in the player
        ///     inventory. After an exception nothing moves: the items stay in the player inventory,
        ///     so they are neither lost nor duplicated.
        /// </summary>
        internal static void End(SupplyAction action, bool completedNormally)
        {
            if (action == null || !ReferenceEquals(action, _current)) return;
            _current = null;
            if (!completedNormally || action.Ledger.Count == 0) return;

            foreach (var name in action.Ledger.Select(x => x.Name).Distinct().ToList())
            {
                var remaining = action.Unconsumed(name);
                for (var i = action.Ledger.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var entry = action.Ledger[i];
                    if (entry.Name != name || entry.Units <= 0) continue;
                    var returned = 0;
                    try
                    {
                        Return(action, entry, Mathf.Min(remaining, entry.Units), ref returned);
                    }
                    catch (Exception e)
                    {
                        // Called from finalizers: a failed return is logged, never rethrown. What
                        // did not go back stays in the player inventory.
                        Automatics.Logger.Error($"Automatic Supply could not return every item it took\n{e}");
                    }
                    finally
                    {
                        entry.Units -= returned;
                        remaining -= returned;
                    }
                }
            }
        }

        /// <summary>Drops an open action without moving anything (module off, new session).</summary>
        internal static void Abandon()
        {
            _current = null;
            _transferring = 0;
        }

        /// <summary>
        ///     Takes up to <paramref name="amount" /> units named <paramref name="name" /> from the
        ///     eligible containers, nearest first, into the player inventory. A quality below 0 takes
        ///     any quality. Only items at or above the world level are taken, as the vanilla
        ///     requirement checks count only those. Returns the units that arrived.
        /// </summary>
        internal static int Pull(SupplyAction action, string name, int quality, int amount)
        {
            if (action == null || !ReferenceEquals(action, _current) || amount <= 0 ||
                string.IsNullOrEmpty(name) || !action.Player || action.Inventory == null) return 0;

            var arrived = 0;
            foreach (var container in SupplyContainers.Eligible(action.Player))
            {
                if (arrived >= amount) break;
                // A container is taken through the gate only when it holds the item now.
                if (ContainerAccess.CountStock(container, name, quality) <= 0) continue;
                if (!ContainerAccess.TryPrepareContainerMutation(action.Player, container)) continue;
                var source = container.GetInventory();
                if (source == null) continue;

                foreach (var item in new List<ItemDrop.ItemData>(source.GetAllItems()))
                {
                    if (arrived >= amount) break;
                    if (item?.m_shared == null || item.m_shared.m_name != name || item.m_stack <= 0) continue;
                    if (quality >= 0 && item.m_quality != quality) continue;
                    if (item.m_worldLevel < Game.m_worldLevel) continue;

                    var wanted = Mathf.Min(amount - arrived, item.m_stack);
                    var room = Capacity(action.Inventory, item);
                    if (room < wanted) action.InventoryFull = true;
                    if (room <= 0) continue;

                    var take = Mathf.Min(wanted, room);
                    var template = item.Clone();
                    var moved = 0;
                    try
                    {
                        Move(item, template, source, action.Inventory, take, ref moved);
                    }
                    finally
                    {
                        // Registered even when the move threw: what arrived is in the ledger, so a
                        // normal end can still return it, and an exceptional end leaves it where
                        // it is, accounted for.
                        if (moved > 0)
                        {
                            action.Ledger.Add(new SupplyLedgerEntry(container, template, moved));
                            arrived += moved;
                            SupplyContainers.Invalidate();
                        }
                    }
                    if (moved < take) action.InventoryFull = true;
                }
            }

            return arrived;
        }

        /// <summary>
        ///     Takes what the player inventory is missing of every requirement of
        ///     <paramref name="piece" /> at its placement cost, counted as vanilla counts a placement
        ///     (every quality). Returns true when the inventory now holds every requirement.
        /// </summary>
        internal static bool EnsurePieceRequirements(SupplyAction action, Piece piece, bool reportFullInventory = false)
        {
            if (action == null || !piece) return false;
            var complete = true;
            foreach (var requirement in piece.m_resources)
            {
                if (!requirement.m_resItem) continue;
                var need = requirement.GetAmount(0);
                if (need <= 0) continue;
                var name = requirement.m_resItem.m_itemData.m_shared.m_name;
                var have = CountRaw(action.Inventory, name, -1);
                // A stop request ends the taking; a grid in progress then plants from the inventory.
                if (have < need && Module.Running) have += Pull(action, name, -1, need - have);
                if (have < need) complete = false;
            }
            if (!complete && reportFullInventory && action.InventoryFull) ShowShortage(action, false);
            return complete;
        }

        /// <summary>
        ///     The units of <paramref name="name" /> in <paramref name="inventory" /> at or above the
        ///     world level, of one quality or of all (quality below 0). Counted directly, so the
        ///     requirement counting patch never adds container stock to it.
        /// </summary>
        internal static int CountRaw(Inventory inventory, string name, int quality)
        {
            if (inventory == null) return 0;
            var total = 0;
            foreach (var item in inventory.GetAllItems())
            {
                if (item?.m_shared == null || item.m_shared.m_name != name) continue;
                if (quality >= 0 && item.m_quality != quality) continue;
                if (item.m_worldLevel < Game.m_worldLevel) continue;
                total += item.m_stack;
            }
            return total;
        }

        /// <summary>
        ///     Shows why an action could not be fully supplied: a full inventory, or missing items.
        /// </summary>
        internal static void ShowShortage(SupplyAction action, bool missing)
        {
            if (action?.Player == null || !action.Player) return;
            if (action.InventoryFull)
                action.Player.Message(MessageHud.MessageType.Center,
                    Automatics.L10N.Localize("@message_automatic_supply_inventory_full"));
            else if (missing)
                action.Player.Message(MessageHud.MessageType.Center,
                    Automatics.L10N.Localize("@message_automatic_supply_missing_materials"));
        }

        // Moves up to amount units of the entry's kind from the player inventory back to the
        // container it came from. returned counts what arrived there, also when a move throws.
        private static void Return(SupplyAction action, SupplyLedgerEntry entry, int amount, ref int returned)
        {
            var container = entry.Container;
            if (!container || amount <= 0) return;
            if (!ContainerAccess.TryPrepareContainerMutation(action.Player, container)) return;
            var target = container.GetInventory();
            if (target == null) return;

            try
            {
                foreach (var item in new List<ItemDrop.ItemData>(action.Inventory.GetAllItems()))
                {
                    if (returned >= amount) break;
                    if (!SameKind(item, entry.Template)) continue;
                    if (action.Player.IsItemEquiped(item)) continue;
                    var room = Capacity(target, item);
                    if (room <= 0) break;
                    var take = Mathf.Min(amount - returned, Mathf.Min(item.m_stack, room));
                    var moved = 0;
                    try
                    {
                        Move(item, item.Clone(), action.Inventory, target, take, ref moved);
                    }
                    finally
                    {
                        returned += moved;
                    }
                }
            }
            finally
            {
                if (returned > 0) SupplyContainers.Invalidate();
            }
        }

        // Moves up to take units of item from source to target, measured on both sides. The
        // removal comes first; a copy of the stack (template, taken before anything changed) goes
        // in; whatever the insertion did not take goes back into source. inserted is the units
        // that arrived in target, and it is right even when an inventory callback throws part
        // way through: the counts are measured again, the units that left source and did not
        // reach target are put back, and the exception goes on.
        private static void Move(ItemDrop.ItemData item, ItemDrop.ItemData template, Inventory source,
            Inventory target, int take, ref int inserted)
        {
            if (take <= 0) return;

            _transferring++;
            var sourceBefore = CountExact(source, template);
            var targetBefore = CountExact(target, template);
            try
            {
                source.RemoveItem(item, take);
                var removed = Mathf.Max(0, sourceBefore - CountExact(source, template));
                if (removed <= 0) return;

                var copy = template.Clone();
                copy.m_stack = removed;
                target.AddItem(copy);
                inserted = Mathf.Clamp(CountExact(target, template) - targetBefore, 0, removed);

                var remainder = removed - inserted;
                if (remainder <= 0) return;
                PutBack(source, target, template, remainder);
                // PutBack may have had to use target; what is there now counts as arrived.
                inserted = Mathf.Clamp(CountExact(target, template) - targetBefore, 0, removed);
            }
            catch
            {
                var removed = Mathf.Max(0, sourceBefore - CountExact(source, template));
                inserted = Mathf.Clamp(CountExact(target, template) - targetBefore, 0, removed);
                var outstanding = removed - inserted;
                if (outstanding > 0)
                {
                    try
                    {
                        PutBack(source, target, template, outstanding);
                    }
                    catch (Exception e)
                    {
                        Automatics.Logger.Error($"Automatic Supply could not put items back after a failed move\n{e}");
                    }
                    // The recovery may have had to use target; what is there now counts as arrived,
                    // so the ledger and any refund follow the final state.
                    inserted = Mathf.Clamp(CountExact(target, template) - targetBefore, 0, removed);
                }
                throw;
            }
            finally
            {
                _transferring--;
            }
        }

        // Units that left source and did not arrive in target go back to source, where the
        // removal just freed room; failing that, into target; only when both refuse are they
        // dropped at the player, never deleted, and the drop is logged as an error with its
        // position. Each step is measured, and a throwing callback only moves on to the next.
        private static void PutBack(Inventory source, Inventory target, ItemDrop.ItemData template, int units)
        {
            units -= Insert(source, template, units);
            if (units <= 0) return;
            units -= Insert(target, template, units);
            if (units <= 0) return;

            var player = Player.m_localPlayer;
            var position = player ? player.transform.position : Vector3.zero;
            Automatics.Logger.Error(
                $"Automatic Supply could put {units} x {template.m_shared.m_name} back in neither inventory; they are dropped at ({position.x:0.0}, {position.y:0.0}, {position.z:0.0})");
            if (!player || !template.m_dropPrefab) return;
            var rest = template.Clone();
            rest.m_stack = units;
            ItemDrop.DropItem(rest, rest.m_stack, position + Vector3.up, Quaternion.identity);
        }

        private static int Insert(Inventory inventory, ItemDrop.ItemData template, int units)
        {
            var before = CountExact(inventory, template);
            try
            {
                var copy = template.Clone();
                copy.m_stack = units;
                inventory.AddItem(copy);
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Automatic Supply: an inventory callback failed while putting items back\n{e}");
            }
            return Mathf.Clamp(CountExact(inventory, template) - before, 0, units);
        }

        // The free room for item in inventory, as Inventory.AddItem fills it: the space left in
        // stacks of the same name, quality and world level, plus one empty slot's worth. AddItem
        // puts everything that does not fit an existing stack into a single empty slot.
        private static int Capacity(Inventory inventory, ItemDrop.ItemData item)
        {
            var max = Mathf.Max(1, item.m_shared.m_maxStackSize);
            var room = 0;
            if (max > 1)
                foreach (var other in inventory.GetAllItems())
                    if (SameKind(other, item) && other.m_stack < max)
                        room += max - other.m_stack;
            if (inventory.GetEmptySlots() > 0) room += max;
            return room;
        }

        private static int CountExact(Inventory inventory, ItemDrop.ItemData sample)
        {
            var total = 0;
            foreach (var item in inventory.GetAllItems())
                if (SameKind(item, sample))
                    total += item.m_stack;
            return total;
        }

        private static bool SameKind(ItemDrop.ItemData item, ItemDrop.ItemData sample)
        {
            return item?.m_shared != null && item.m_shared.m_name == sample.m_shared.m_name &&
                   item.m_quality == sample.m_quality && item.m_worldLevel == sample.m_worldLevel;
        }

        /// <summary>
        ///     Records a removal from the open action's player inventory. Called by the consumption
        ///     patches with the count of <paramref name="name" /> before and after the removal.
        /// </summary>
        internal static void RecordDebit(Inventory inventory, string name, int before)
        {
            var action = _current;
            if (action == null || before < 0 || !ReferenceEquals(inventory, action.Inventory)) return;
            var removed = before - CountAll(inventory, name);
            if (removed <= 0) return;
            action.Debited[name] = action.DebitedUnits(name) + removed;
        }

        /// <summary>
        ///     The count of <paramref name="name" /> to measure a removal against, or -1 when the
        ///     removal is not the open action's to record.
        /// </summary>
        internal static int DebitBaseline(Inventory inventory, string name)
        {
            var action = _current;
            if (action == null || _transferring > 0 || string.IsNullOrEmpty(name) ||
                !ReferenceEquals(inventory, action.Inventory)) return -1;
            if (action.Ledger.All(x => x.Name != name)) return -1;
            return CountAll(inventory, name);
        }

        private static int CountAll(Inventory inventory, string name)
        {
            var total = 0;
            foreach (var item in inventory.GetAllItems())
                if (item?.m_shared != null && item.m_shared.m_name == name)
                    total += item.m_stack;
            return total;
        }
    }
}
