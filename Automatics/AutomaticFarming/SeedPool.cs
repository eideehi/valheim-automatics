using System.Collections.Generic;
using Automatics.Valheim;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    /// <summary>
    /// Resolves where planting inputs are counted and consumed from according to
    /// <see cref="Config.SeedSource"/>: the player inventory, or the matching-role
    /// planting boxes near a planting location.
    /// </summary>
    /// <remarks>
    /// In the container modes (<see cref="SeedSource.Containers"/> and
    /// <see cref="SeedSource.PlayerContainers"/>) the pool wraps a pre-filtered,
    /// nearest-first list of the matching-role planting boxes for a location
    /// (cultivation boxes for crop saplings, seed-harvest boxes for seed saplings),
    /// so the input for a spot comes only from same-role boxes near that spot and the
    /// player inventory is never charged; its own source is then
    /// <see cref="SeedSource.Containers"/>. In <see cref="SeedSource.Inventory"/> mode
    /// no container is enumerated, read, or mutated.
    /// </remarks>
    internal sealed class SeedPool
    {
        private readonly SeedSource _source;
        private readonly Player _player;
        private readonly Inventory _playerInventory;
        private readonly List<Container> _containers;

        private SeedPool(SeedSource source, Player player, Inventory playerInventory,
            List<Container> containers)
        {
            _source = source;
            _player = player;
            _playerInventory = playerInventory;
            _containers = containers;
        }

        public SeedSource Source => _source;

        /// <summary>Pool backed by the player inventory (Inventory mode).</summary>
        public static SeedPool FromPlayerInventory(Player player)
        {
            return new SeedPool(SeedSource.Inventory, player, player.GetInventory(), null);
        }

        /// <summary>
        /// Pool backed by <paramref name="containers"/> (Containers mode), a
        /// pre-filtered, nearest-first list of the matching-role planting boxes for a
        /// location. The candidates are already restricted to containers this peer can
        /// mutate, so counting and consuming agree.
        /// </summary>
        public static SeedPool FromContainers(Player player, List<Container> containers)
        {
            return new SeedPool(SeedSource.Containers, player, null, containers);
        }

        /// <summary>
        /// Total count of the input named <paramref name="name"/> across the active
        /// pool (player inventory in Inventory mode, the field-local matching-role
        /// planting boxes in Containers mode).
        /// </summary>
        public int Count(string name)
        {
            var total = 0;
            if (_playerInventory != null)
                total += _playerInventory.CountItems(name);

            if (_containers != null)
                foreach (var container in _containers)
                {
                    var inventory = container.GetInventory();
                    if (inventory != null) total += inventory.CountItems(name);
                }

            return total;
        }

        /// <summary>
        /// Removes up to <paramref name="amount"/> of the input named
        /// <paramref name="name"/> from the active pool (matching-role planting boxes
        /// nearest-first in Containers mode, the player inventory in Inventory mode)
        /// and returns the number actually removed. The reserve gate guarantees the
        /// pool holds at least <paramref name="amount"/>, so a shortfall is not
        /// expected.
        /// </summary>
        public int Consume(string name, int amount)
        {
            if (amount <= 0) return 0;

            var removed = 0;
            if (_containers != null)
                foreach (var container in _containers)
                {
                    if (removed >= amount) break;
                    removed += RemoveFromContainer(container, name, amount - removed);
                }

            if (removed < amount && _playerInventory != null)
                removed += RemoveFromInventory(_playerInventory, name, amount - removed);

            return removed;
        }

        // Owner-only removal: skip the container unless this peer can mutate it
        // right now, so seeds are never removed from a chest another peer or a
        // dedicated server owns (which would desync the container). Re-checks both
        // access gates at consume time, matching AutomaticStorage's transfer path,
        // so a ward raised or access revoked since selection is also honored.
        private int RemoveFromContainer(Container container, string name, int amount)
        {
            if (!ContainerAccess.TryPrepareContainerMutation(_player, container)) return 0;
            return RemoveFromInventory(container.GetInventory(), name, amount);
        }

        // Removes by the vanilla name-based API and reports the real delta via a
        // before/after count rather than trusting the boolean, mirroring the
        // count-delta safety Automatic Storage uses for its transfers.
        private static int RemoveFromInventory(Inventory inventory, string name, int amount)
        {
            if (inventory == null || amount <= 0) return 0;

            var before = inventory.CountItems(name);
            if (before <= 0) return 0;

            inventory.RemoveItem(name, Mathf.Min(amount, before));
            return Mathf.Max(0, before - inventory.CountItems(name));
        }
    }
}
