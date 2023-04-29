using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    using ContainerList = List<(Container container, float distance)>;

    internal static class Globals
    {
        public static ValheimObject Container { get; } = new ValheimObject("container");
    }

    internal static class Logics
    {
        private static readonly Dictionary<int, ContainerList> Containers;
        private static float _lastContainersReset;

        static Logics()
        {
            Containers = new Dictionary<int, ContainerList>();
        }

        public static void Cleanup()
        {
            Containers.Clear();
            _lastContainersReset = 0f;
        }

        public static void CraftingLog(string materialName, int count, string fromName,
            Vector3 fromPos, string toName, Vector3 toPos, string productName)
        {
            const string format = "{0} x{1} was set from {2}{3} to {4}{5} for crafting {6}";
            Automatics.Logger.Debug(() =>
                string.Format(format, Automatics.L10N.Translate(materialName), count,
                    Automatics.L10N.Translate(fromName), fromPos, Automatics.L10N.Translate(toName),
                    toPos, Automatics.L10N.Translate(productName)));
        }

        public static void RefuelLog(string fuelName, int count, string toName, Vector3 toPos,
            string fromName, Vector3 fromPos)
        {
            const string format = "Refueled {0} x{1} in {2}{3} from {4}{5}";
            Automatics.Logger.Debug(() =>
                string.Format(format, Automatics.L10N.Translate(fuelName), count,
                    Automatics.L10N.Translate(toName), toPos,
                    Automatics.L10N.Translate(fromName), fromPos));
        }

        public static void StoreLog(string productName, int count, string toName, Vector3 toPos,
            string fromName, Vector3 fromPos)
        {
            const string format = "Stored {0} x{1} in {2}{3} from {4}{5}";
            Automatics.Logger.Debug(() =>
                string.Format(format, Automatics.L10N.Translate(productName), count,
                    Automatics.L10N.Translate(toName), toPos,
                    Automatics.L10N.Translate(fromName), fromPos));
        }

        public static void ChargeLog(string itemName, int count, string toName, Vector3 toPos,
            string fromName, Vector3 fromPos)
        {
            const string format = "Charge {0} x{1} to {2}{3} from {4}{5}";
            Automatics.Logger.Debug(() =>
                string.Format(format, Automatics.L10N.Translate(itemName), count,
                    Automatics.L10N.Translate(toName), toPos,
                    Automatics.L10N.Translate(fromName), fromPos));
        }

        public static bool IsAllowContainer(Container container)
        {
            return Globals.Container.GetIdentify(Objects.GetName(container), out var identifier) &&
                   Config.AllowContainer.Contains(identifier);
        }

        public static bool IsAllowProcessing(string target, Process type)
        {
            return (Config.AllowProcessing(target) & type) != 0;
        }

        public static IEnumerable<(Container container, float distance)> GetNearbyContainers(
            string target, Vector3 origin)
        {
            if (Time.time - _lastContainersReset > 1f)
            {
                _lastContainersReset = Time.time;
                Containers.Clear();
            }

            var hash = origin.GetHashCode();
            if (Containers.TryGetValue(hash, out var cache)) return cache.Where(x => x.container);

            var containers = new ContainerList();
            Containers[hash] = containers;

            var range = Config.ContainerSearchRange(target);
            if (range > 0)
                containers.AddRange(from x in ContainerCache.GetAllInstance()
                    let distance = Vector3.Distance(origin, x.transform.position)
                    where distance <= range && IsAllowContainer(x)
                    orderby distance
                    select (x, distance));

            return containers;
        }
    }
}