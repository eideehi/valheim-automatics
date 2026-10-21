using System.Collections.Generic;
using Automatics.Valheim;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    /// <summary>
    /// A designated container resolved for a farming pass, paired with its position
    /// so range filtering does not re-read the transform.
    /// </summary>
    internal readonly struct DesignatedContainer
    {
        public DesignatedContainer(Container container, Vector3 position)
        {
            Container = container;
            Position = position;
        }

        public Container Container { get; }
        public Vector3 Position { get; }
    }

    /// <summary>
    /// The designated containers in the loaded world, resolved once per farming pass
    /// so container-anchored harvesting and field-local planting-input sourcing share
    /// one scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Anchors"/> holds every designated container regardless of role or
    /// owner: a role is read from a replicated ZDO, and harvesting is a read-only
    /// spatial search whose own owner gate lives on the crop, so a peer's chest may
    /// still mark a field as managed.
    /// </para>
    /// <para>
    /// <see cref="OwnedCultivation"/>, <see cref="OwnedSeedHarvest"/>, and
    /// <see cref="OwnedStorage"/> are the subsets farming may actually mutate:
    /// containers of that role this peer can mutate right now (it owns them, or as
    /// the server may claim them; a box another peer owns is left out). Counting an
    /// input the peer could never consume — or a slot it could never fill — would
    /// let a gate pass and the mutation fall short, so the sourcing and deposit
    /// pools are restricted to owned containers up front. A box another peer
    /// owns (<see cref="OwnedElsewhere"/>) is asked for only where this peer's
    /// own boxes leave a need unmet, and used on a later pass.
    /// </para>
    /// </remarks>
    internal sealed class FarmingContainers
    {
        // By role, Cultivation first; filled for a farming pass only.
        private readonly List<DesignatedContainer>[] _ownedElsewhere;

        private FarmingContainers(List<DesignatedContainer> ownedCultivation,
            List<DesignatedContainer> ownedSeedHarvest, List<DesignatedContainer> ownedStorage,
            List<Vector3> anchors, List<DesignatedContainer>[] ownedElsewhere)
        {
            OwnedCultivation = ownedCultivation;
            OwnedSeedHarvest = ownedSeedHarvest;
            OwnedStorage = ownedStorage;
            Anchors = anchors;
            _ownedElsewhere = ownedElsewhere;
        }

        /// <summary>Cultivation-role containers this peer can mutate (grow crops from seeds).</summary>
        public List<DesignatedContainer> OwnedCultivation { get; }

        /// <summary>Seed-harvest-role containers this peer can mutate (grow seeds from crops).</summary>
        public List<DesignatedContainer> OwnedSeedHarvest { get; }

        /// <summary>Storage-role containers this peer can mutate (priority harvest deposit).</summary>
        public List<DesignatedContainer> OwnedStorage { get; }

        /// <summary>Positions of all designated containers, any role or owner (harvest anchor).</summary>
        public List<Vector3> Anchors { get; }

        public bool HasAnchors => Anchors.Count > 0;

        public bool HasOwnedElsewhere => _ownedElsewhere[0].Count > 0 ||
                                         _ownedElsewhere[1].Count > 0 ||
                                         _ownedElsewhere[2].Count > 0;

        /// <summary>Whether the pass has a box to mutate, or one to ask another peer for.</summary>
        public bool HasUsableBoxes => OwnedStorage.Count > 0 || OwnedCultivation.Count > 0 ||
                                      OwnedSeedHarvest.Count > 0 || HasOwnedElsewhere;

        /// <summary>Containers of <paramref name="role"/> another peer owns, which may be asked for.</summary>
        public List<DesignatedContainer> OwnedElsewhere(ContainerRole role)
        {
            return _ownedElsewhere[(int)role - 1];
        }

        public static FarmingContainers Resolve(Player player, bool forPass = false)
        {
            var ownedCultivation = new List<DesignatedContainer>();
            var ownedSeedHarvest = new List<DesignatedContainer>();
            var ownedStorage = new List<DesignatedContainer>();
            var anchors = new List<Vector3>();
            var ownedElsewhere = new[]
            {
                new List<DesignatedContainer>(), new List<DesignatedContainer>(),
                new List<DesignatedContainer>()
            };

            foreach (var container in ContainerCache.GetAllInstance())
            {
                if (container == null || ContainerAccess.IsGrave(container)) continue;

                var role = ContainerRoles.GetRole(container);
                if (role == ContainerRole.None) continue;

                var position = container.transform.position;
                anchors.Add(position);

                // The gate at the deposit and the input take claims a box; a pass only loads it.
                var designated = new DesignatedContainer(container, position);
                var step = ContainerAccess.GetWriteStep(player, container);
                if (!ContainerAccess.IsWritable(step))
                {
                    if (forPass && step == ContainerWriteStep.Request)
                        ownedElsewhere[(int)role - 1].Add(designated);
                    continue;
                }

                if (forPass) ContainerAccess.LoadSavedContents(container);
                switch (role)
                {
                    case ContainerRole.Cultivation:
                        ownedCultivation.Add(designated);
                        break;
                    case ContainerRole.SeedHarvest:
                        ownedSeedHarvest.Add(designated);
                        break;
                    case ContainerRole.Storage:
                        ownedStorage.Add(designated);
                        break;
                }
            }

            return new FarmingContainers(ownedCultivation, ownedSeedHarvest, ownedStorage, anchors,
                ownedElsewhere);
        }

        /// <summary>
        /// The deposit targets of a yield another peer owns (storage boxes, then the matching
        /// planting boxes) within <paramref name="range"/> of <paramref name="position"/>,
        /// nearest-first; null when no box is owned elsewhere.
        /// </summary>
        public List<Container> OwnedElsewhereDepositNear(bool isSeed, Vector3 position, float range)
        {
            if (!HasOwnedElsewhere) return null;

            var targets = Near(OwnedElsewhere(ContainerRole.Storage), position, range);
            targets.AddRange(Near(OwnedElsewhere(isSeed ? ContainerRole.SeedHarvest
                : ContainerRole.Cultivation), position, range));
            return targets;
        }

        /// <summary>
        /// Cultivation-role containers this peer can mutate within
        /// <paramref name="range"/> of <paramref name="position"/>, ordered
        /// nearest-first.
        /// </summary>
        public List<Container> CultivationNear(Vector3 position, float range)
        {
            return Near(OwnedCultivation, position, range);
        }

        /// <summary>
        /// Seed-harvest-role containers this peer can mutate within
        /// <paramref name="range"/> of <paramref name="position"/>, ordered
        /// nearest-first.
        /// </summary>
        public List<Container> SeedHarvestNear(Vector3 position, float range)
        {
            return Near(OwnedSeedHarvest, position, range);
        }

        /// <summary>
        /// Storage-role containers this peer can mutate within
        /// <paramref name="range"/> of <paramref name="position"/>, ordered
        /// nearest-first (priority harvest deposit targets).
        /// </summary>
        public List<Container> StorageNear(Vector3 position, float range)
        {
            return Near(OwnedStorage, position, range);
        }

        /// <summary>
        /// The planting boxes that match a yield: seed-harvest boxes when the yield
        /// is a seed, cultivation boxes otherwise. Used both as the harvest-deposit
        /// fallback (after storage) and as the replant input pool, so a seed yield is
        /// only ever stocked from and replanted into seed-harvest boxes, and a crop
        /// yield only from and into cultivation boxes.
        /// </summary>
        public List<Container> MatchingPlantingNear(bool isSeed, Vector3 position, float range)
        {
            return isSeed ? SeedHarvestNear(position, range) : CultivationNear(position, range);
        }

        // Called several times per crop per farming pass, so this stays LINQ-free:
        // the query chain allocated an anonymous-type object per container plus
        // enumerators and sort buffers for every call.
        public static List<Container> Near(List<DesignatedContainer> source, Vector3 position,
            float range)
        {
            var rangeSqr = range * range;
            var hits = new List<(float DistanceSqr, Container Container)>();
            foreach (var designated in source)
            {
                var distanceSqr = (designated.Position - position).sqrMagnitude;
                if (distanceSqr <= rangeSqr)
                    hits.Add((distanceSqr, designated.Container));
            }

            hits.Sort((a, b) => a.DistanceSqr.CompareTo(b.DistanceSqr));

            var result = new List<Container>(hits.Count);
            foreach (var hit in hits)
                result.Add(hit.Container);
            return result;
        }
    }
}
