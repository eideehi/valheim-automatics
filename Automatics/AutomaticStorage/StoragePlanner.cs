using System.Collections.Generic;
using System.Linq;

namespace Automatics.AutomaticStorage
{
    internal enum StorageRouteKind
    {
        ExactStack,
        SameItem,
        CategoryAffinity,
        Empty,
        Other
    }

    internal sealed class StorageRoute
    {
        public StorageRoute(int containerIndex, StorageRouteKind kind)
        {
            ContainerIndex = containerIndex;
            Kind = kind;
        }

        public int ContainerIndex { get; }
        public StorageRouteKind Kind { get; }
    }

    internal sealed class StorageItemSnapshot
    {
        public StorageItemSnapshot(string name, int quality, int worldLevel, int itemType,
            int stack, int maxStackSize)
        {
            Name = name;
            Quality = quality;
            WorldLevel = worldLevel;
            ItemType = itemType;
            Stack = stack;
            MaxStackSize = maxStackSize;
        }

        public string Name { get; }
        public int Quality { get; }
        public int WorldLevel { get; }
        public int ItemType { get; }
        public int Stack { get; }
        public int MaxStackSize { get; }
    }

    internal sealed class StorageContainerSnapshot
    {
        public StorageContainerSnapshot(int index, int emptySlots,
            IEnumerable<StorageItemSnapshot> items)
        {
            Index = index;
            EmptySlots = emptySlots;
            Items = (items ?? Enumerable.Empty<StorageItemSnapshot>()).ToList();
        }

        public int Index { get; }
        public int EmptySlots { get; }
        public IReadOnlyList<StorageItemSnapshot> Items { get; }

        public bool IsEmpty => Items.Count == 0;

        public int ExactFreeStackSpace(StorageItemSnapshot item)
        {
            if (item == null) return 0;

            return Items.Where(x => IsSameStack(x, item) && x.Stack < x.MaxStackSize)
                        .Sum(x => x.MaxStackSize - x.Stack);
        }

        public bool HasCategory(StorageItemSnapshot item)
        {
            return item != null && Items.Any(x => x.ItemType == item.ItemType);
        }

        public bool HasSameItem(StorageItemSnapshot item)
        {
            return item != null && Items.Any(x => x.Name == item.Name);
        }

        public bool HasCapacityFor(StorageItemSnapshot item)
        {
            return ExactFreeStackSpace(item) > 0 || EmptySlots > 0;
        }

        private static bool IsSameStack(StorageItemSnapshot a, StorageItemSnapshot b)
        {
            return a.Name == b.Name &&
                   a.Quality == b.Quality &&
                   a.WorldLevel == b.WorldLevel;
        }
    }

    internal static class StoragePlanner
    {
        public static List<StorageContainerSnapshot> ApplyReferenceLimit(
            IEnumerable<StorageContainerSnapshot> containers, int referenceLimit)
        {
            var ordered = (containers ?? Enumerable.Empty<StorageContainerSnapshot>())
                .OrderBy(x => x.Index);

            return referenceLimit > 0
                ? ordered.Take(referenceLimit).ToList()
                : ordered.ToList();
        }

        public static List<StorageRoute> Plan(StorageItemSnapshot item,
            IEnumerable<StorageContainerSnapshot> containers, int referenceLimit = 0,
            bool storeIntoAnyContainer = false)
        {
            var limited = ApplyReferenceLimit(containers, referenceLimit);
            var routes = new List<StorageRoute>();

            AddRoutes(routes, limited.Where(x => x.ExactFreeStackSpace(item) > 0),
                StorageRouteKind.ExactStack);
            if (!storeIntoAnyContainer)
            {
                AddRoutes(routes, limited.Where(x => x.EmptySlots > 0 && x.HasSameItem(item)),
                    StorageRouteKind.SameItem);
                return routes;
            }

            AddRoutes(routes, limited.Where(x => x.HasCapacityFor(item) && x.HasCategory(item)),
                StorageRouteKind.CategoryAffinity);
            AddRoutes(routes, limited.Where(x => x.HasCapacityFor(item) && x.IsEmpty),
                StorageRouteKind.Empty);
            AddRoutes(routes,
                limited.Where(x => x.HasCapacityFor(item) && !x.IsEmpty && !x.HasCategory(item)),
                StorageRouteKind.Other);

            return routes;
        }

        private static void AddRoutes(ICollection<StorageRoute> routes,
            IEnumerable<StorageContainerSnapshot> containers, StorageRouteKind kind)
        {
            foreach (var container in containers.OrderBy(x => x.Index))
                routes.Add(new StorageRoute(container.Index, kind));
        }
    }
}
