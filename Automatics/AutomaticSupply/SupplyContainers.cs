using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using UnityEngine;

namespace Automatics.AutomaticSupply
{
    /// <summary>
    ///     The containers Automatic Supply may use and what they hold. A container counts when it is
    ///     within <see cref="Config.SupplyRange" /> of the player, passes the player-aware access
    ///     check (no ward the player lacks access to, not private or group-restricted, not in use,
    ///     not on a wagon in use) and is one the local game may write: in multiplayer, a container
    ///     another player's game owns is neither counted nor taken from. Graves are left alone.
    /// </summary>
    internal static class SupplyContainers
    {
        private const float RefreshSeconds = 0.5f;

        // name -> one entry per (quality, world level) held by the eligible containers.
        private static readonly Dictionary<string, List<StockEntry>> Stock =
            new Dictionary<string, List<StockEntry>>();

        private static float _stockTime = float.NegativeInfinity;
        private static int _stockHash;

        /// <summary>Raised whenever a refresh finds the stock different from the last one.</summary>
        internal static int Version { get; private set; }
        private static Player _stockPlayer;

        /// <summary>The containers taken from, nearest first. Always read fresh.</summary>
        public static List<Container> Eligible(Player player)
        {
            if (!player) return new List<Container>();

            var origin = player.transform.position;
            var range = Config.SupplyRange;
            return ContainerCache.GetAllInstance()
                .Where(x => x)
                .Select(x => new { Container = x, Distance = Vector3.Distance(origin, x.transform.position) })
                .Where(x => x.Distance <= range)
                .Where(x => ContainerAccess.CanWriteContainer(player, x.Container))
                .OrderBy(x => x.Distance)
                .Select(x => x.Container)
                .ToList();
        }

        /// <summary>
        ///     The stock of <paramref name="name" /> in the eligible containers, with the filters of
        ///     <see cref="Inventory.CountItems" />: a quality below 0 counts every quality, and
        ///     <paramref name="matchWorldLevel" /> skips items below the world level. Read from a
        ///     cache refreshed at most every half second and dropped after every transfer.
        /// </summary>
        public static int Count(Player player, string name, int quality, bool matchWorldLevel)
        {
            if (!player || string.IsNullOrEmpty(name)) return 0;
            Refresh(player);
            if (!Stock.TryGetValue(name, out var entries)) return 0;

            var total = 0;
            foreach (var entry in entries)
            {
                if (quality >= 0 && entry.Quality != quality) continue;
                if (matchWorldLevel && entry.WorldLevel < Game.m_worldLevel) continue;
                total += entry.Count;
            }
            return total;
        }

        public static void Invalidate()
        {
            _stockTime = float.NegativeInfinity;
        }

        public static void Clear()
        {
            Stock.Clear();
            _stockPlayer = null;
            Invalidate();
        }

        /// <summary>
        ///     Reads the stock of the eligible containers again, unless it was read for
        ///     <paramref name="player" /> less than half a second ago and no transfer dropped it
        ///     since; <see cref="Version" /> is raised when the stock differs.
        /// </summary>
        public static void Refresh(Player player)
        {
            var now = Time.time;
            if (ReferenceEquals(_stockPlayer, player) && now - _stockTime < RefreshSeconds &&
                now >= _stockTime) return;

            Stock.Clear();
            _stockPlayer = player;
            _stockTime = now;
            var hash = 0;
            foreach (var container in Eligible(player))
            {
                // The stock offered is what the container holds now, not a copy older than its
                // saved contents.
                ContainerAccess.LoadSavedContents(container);
                var inventory = container.GetInventory();
                if (inventory == null) continue;
                foreach (var item in inventory.GetAllItems())
                {
                    if (item?.m_shared == null || item.m_stack <= 0) continue;
                    if (!Stock.TryGetValue(item.m_shared.m_name, out var entries))
                    {
                        entries = new List<StockEntry>();
                        Stock.Add(item.m_shared.m_name, entries);
                    }

                    unchecked
                    {
                        // Order-independent, so the same stock in another order hashes the same.
                        hash += (item.m_shared.m_name.GetHashCode() * 397 ^ (item.m_quality * 31 + item.m_worldLevel)) *
                                (item.m_stack + 1);
                    }

                    var index = entries.FindIndex(x => x.Quality == item.m_quality && x.WorldLevel == item.m_worldLevel);
                    if (index < 0)
                        entries.Add(new StockEntry(item.m_quality, item.m_worldLevel, item.m_stack));
                    else
                        entries[index] = new StockEntry(item.m_quality, item.m_worldLevel,
                            entries[index].Count + item.m_stack);
                }
            }

            if (hash != _stockHash)
            {
                _stockHash = hash;
                Version++;
            }
        }

        private readonly struct StockEntry
        {
            public StockEntry(int quality, int worldLevel, int count)
            {
                Quality = quality;
                WorldLevel = worldLevel;
                Count = count;
            }

            public int Quality { get; }
            public int WorldLevel { get; }
            public int Count { get; }
        }
    }
}
