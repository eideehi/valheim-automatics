using System.Collections.Generic;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    internal static class AutomaticFarming
    {
        // Per-axis cap on the proactive-sow grid (MaxSowStepsPerAxis^2 tiles per
        // crop per pass). Keeps a large farming_range from turning each sow pass
        // into a multi-thousand-tile physics sweep.
        private const int MaxSowStepsPerAxis = 64;

        public static void TryFarming(Player player)
        {
            // Inventory mode is player-centric: crops are searched, harvested, and
            // replanted around the player and seeds come from the player inventory, with
            // no container read or mutated. Containers mode is the fully container-based
            // path.
            // PlayerContainers farms around the player like Inventory mode, but keeps the yield
            // and the planting input in the role containers near each crop.
            switch (Config.SeedSource)
            {
                case SeedSource.Inventory:
                    FarmAroundPlayer(player);
                    return;
                case SeedSource.PlayerContainers:
                    FarmAroundPlayerWithContainers(player);
                    return;
                default:
                    FarmAroundContainers(player);
                    return;
            }
        }

        /// <summary>
        /// True when Automatic Farming owns the harvest of <paramref name="pickable"/>
        /// (module effectively running, feature on, and the crop is allowlisted).
        /// Automatic Pickup queries this to skip crops that farming will harvest and
        /// replant, so the answer has to track whether farming is actually running rather
        /// than what the module setting says: a crop claimed for a module that is not
        /// running would be skipped by pickup and harvested by nobody (R7.5). The answer
        /// starts from <see cref="Module.Running"/>, the predicate the farming hooks open
        /// with, so from the moment a stop is requested pickup collects these crops again,
        /// in the same frame farming stops harvesting them. A crop farming harvested just
        /// before is already marked picked by then, so neither module takes it twice.
        /// </summary>
        public static bool ClaimsCrop(Pickable pickable)
        {
            return ClaimsCrop(Player.m_localPlayer, pickable);
        }

        public static bool ClaimsCrop(Player player, Pickable pickable)
        {
            return ClaimsCrop(player, pickable, new CropClaimSnapshot());
        }

        /// <summary>
        /// The same answer as <see cref="ClaimsCrop(Player, Pickable)"/>, with the farming
        /// containers it needs taken from <paramref name="snapshot"/>: a pass that asks about
        /// many crops resolves them once, when the first crop gets past the cheap gates,
        /// instead of once per crop.
        /// </summary>
        public static bool ClaimsCrop(Player player, Pickable pickable, CropClaimSnapshot snapshot)
        {
            if (!Module.Running) return false;
            if (!pickable) return false;
            if (!Config.EnableAutomaticFarming) return false;

            // With no periodic runs and no key, farming never harvests, so pickup takes the crops.
            if (Config.FarmingInterval <= 0f && Config.FarmingKey.MainKey == KeyCode.None) return false;

            if (!TryGetCropIdentifier(pickable, out var identifier)) return false;
            if (player == null) return false;

            // Farming harvests only crops this peer owns; any other is left to pickup.
            if (!Objects.GetZNetView(pickable, out var zNetView) || !zNetView.IsValid() ||
                !zNetView.IsOwner())
                return false;

            // The player-centred passes harvest only within Farming Range of the player.
            var position = pickable.transform.position;
            var range = Config.FarmingRange;
            if (Config.SeedSource == SeedSource.Inventory)
                return Vector3.Distance(player.transform.position, position) <= range;

            var playerCentred = Config.SeedSource == SeedSource.PlayerContainers;
            if (playerCentred && Vector3.Distance(player.transform.position, position) > range)
                return false;

            var containers = snapshot.Get(player);
            // A box another peer owns counts: farming asks for it and harvests the crop on a
            // later pass, so pickup must not take the crop meanwhile.
            if (!containers.HasUsableBoxes) return false;

            if (!playerCentred && !TryNearestAnchorDistance(containers, position, range, out _))
                return false;

            // A crop is harvested only when a box near it can take the yield.
            var boxRange = Config.ContainerSearchRange;
            var isSeed = ValheimObject.Flora.HasTag(identifier, "seed");
            return containers.HasDepositNear(isSeed, position, boxRange);
        }

        /// <summary>
        /// The farming containers a run of <see cref="ClaimsCrop(Player, Pickable, CropClaimSnapshot)"/>
        /// calls shares, resolved the first time one of them needs it. A snapshot belongs to one
        /// synchronous pass and is dropped when it ends, since a later frame can see other
        /// owners, uses and roles.
        /// </summary>
        internal sealed class CropClaimSnapshot
        {
            private FarmingContainers _containers;

            public FarmingContainers Get(Player player)
            {
                if (_containers == null) _containers = FarmingContainers.Resolve(player);
                return _containers;
            }
        }

        // ---- Inventory mode (player-centric, unchanged behavior) ----------------

        private static void FarmAroundPlayer(Player player)
        {
            var origin = player.transform.position;
            var range = Config.FarmingRange;
            var seedPool = SeedPool.FromPlayerInventory(player);

            var crops = new List<(Pickable pickable, string identifier, float distance)>();
            foreach (var pickable in PickableCache.GetAllInstance())
            {
                if (!pickable) continue;
                if (pickable.GetPicked() || pickable.GetEnabled == 0) continue;

                var distance = Vector3.Distance(origin, pickable.transform.position);
                if (distance > range) continue;
                if (!TryGetCropIdentifier(pickable, out var identifier)) continue;

                crops.Add((pickable, identifier, distance));
            }

            crops.Sort((a, b) => a.distance.CompareTo(b.distance));

            foreach (var (pickable, identifier, _) in crops)
            {
                if (!pickable) continue;
                HarvestAndReplant(player, pickable, identifier, seedPool, null);
            }

            if (Config.EnableProactiveSowing)
                SowAroundPlayer(player, seedPool);
        }

        // Sows allowlisted seeds (kept above the reserve) into nearby empty
        // cultivated ground on a per-sapling spacing grid. Only ground that is
        // already cultivated is targeted; terrain is never modified.
        private static void SowAroundPlayer(Player player, SeedPool seedPool)
        {
            var origin = player.transform.position;
            var reserve = Config.SeedReserve;

            foreach (var identifier in Config.AllowFarmingCrops)
            {
                if (!CropPlanting.TryResolveSapling(identifier, out var sapling)) continue;

                var plant = sapling.GetComponent<Plant>();
                if (plant == null) continue;
                if (plant.m_attachDistance > 0f) continue;
                if (!CropPlanting.CanAffordPlanting(seedPool, sapling, reserve)) continue;

                var sown = SowGrid(player, sapling, plant, seedPool, origin, reserve);
                if (sown > 0)
                {
                    var cropId = identifier;
                    Automatics.Logger.Debug(() => $"Farming sowed {sown} [{cropId}]");
                }
            }
        }

        // ---- Containers mode (container-anchored, field-local) ------------------

        private static void FarmAroundContainers(Player player)
        {
            var containers = FarmingContainers.Resolve(player, forPass: true);
            if (!containers.HasAnchors) return;

            HarvestAndReplantNearContainers(player, containers);

            if (Config.EnableProactiveSowing)
                SowFromPlantingContainers(player, containers);
        }

        private static void HarvestAndReplantNearContainers(Player player,
            FarmingContainers containers)
        {
            // With no container of any role to mutate or ask for, every harvested yield
            // would have nowhere to go, so skip the whole crop search.
            if (!containers.HasUsableBoxes) return;

            var range = Config.FarmingRange;
            var boxRange = Config.ContainerSearchRange;

            // Only crops within range of a designated container are harvested by
            // container mode; a field with no designated container in range is left
            // to the player. Ordering nearest-first to the anchoring container keeps
            // the harvest deterministic and field-local.
            var crops = new List<(Pickable pickable, string identifier, float distance)>();
            foreach (var pickable in PickableCache.GetAllInstance())
            {
                if (!pickable) continue;
                if (pickable.GetPicked() || pickable.GetEnabled == 0) continue;

                var position = pickable.transform.position;
                if (!TryNearestAnchorDistance(containers, position, range, out var distance))
                    continue;
                if (!TryGetCropIdentifier(pickable, out var identifier)) continue;

                crops.Add((pickable, identifier, distance));
            }

            crops.Sort((a, b) => a.distance.CompareTo(b.distance));

            foreach (var (pickable, identifier, _) in crops)
            {
                if (!pickable) continue;
                HarvestIntoContainers(player, pickable, identifier, containers, boxRange);
            }
        }

        // Harvests one crop into the role containers within boxRange of it and replants it from
        // them.
        private static void HarvestIntoContainers(Player player, Pickable pickable, string identifier,
            FarmingContainers containers, float boxRange)
        {
            var position = pickable.transform.position;

            // Classify the yield from data: a seed-tagged Flora identifier routes
            // to seed-harvest boxes, anything else to cultivation boxes.
            var isSeed = ValheimObject.Flora.HasTag(identifier, "seed");
            var matchingPlanting = containers.MatchingPlantingNear(isSeed, position, boxRange);

            // Storage is the priority deposit target; the matching planting box is
            // the self-contained fallback. Storage and planting roles never
            // overlap, so the concatenation has no duplicates.
            // StorageNear returns a fresh caller-owned list, so AddRange can
            // extend it in place without the extra defensive copy.
            var depositTargets = containers.StorageNear(position, boxRange);
            depositTargets.AddRange(matchingPlanting);

            // Replant input comes only from the matching-role boxes near the spot.
            var seedPool = SeedPool.FromContainers(player, matchingPlanting);
            HarvestAndReplant(player, pickable, identifier, seedPool, depositTargets,
                containers.OwnedElsewhereDepositNear(isSeed, position, boxRange));
        }

        // ---- Player + Containers mode (player-centred, role containers) ---------

        private static void FarmAroundPlayerWithContainers(Player player)
        {
            var containers = FarmingContainers.Resolve(player, forPass: true);
            if (!containers.HasUsableBoxes) return;

            var origin = player.transform.position;
            var range = Config.FarmingRange;
            var boxRange = Config.ContainerSearchRange;

            var crops = new List<(Pickable pickable, string identifier, float distance)>();
            foreach (var pickable in PickableCache.GetAllInstance())
            {
                if (!pickable) continue;
                if (pickable.GetPicked() || pickable.GetEnabled == 0) continue;

                var distance = Vector3.Distance(origin, pickable.transform.position);
                if (distance > range) continue;
                if (!TryGetCropIdentifier(pickable, out var identifier)) continue;

                crops.Add((pickable, identifier, distance));
            }

            crops.Sort((a, b) => a.distance.CompareTo(b.distance));

            foreach (var (pickable, identifier, _) in crops)
            {
                if (!pickable) continue;
                HarvestIntoContainers(player, pickable, identifier, containers, boxRange);
            }

            if (!Config.EnableProactiveSowing) return;

            // Sows around the player from the planting boxes within boxRange of the player:
            // cultivation boxes plant crop-producing saplings, seed-harvest boxes seed-producing
            // ones.
            var reserve = Config.SeedReserve;
            foreach (var identifier in Config.AllowFarmingCrops)
            {
                var plantSeedProducing = ValheimObject.Flora.HasTag(identifier, "seed");
                if (!CropPlanting.TryResolveSapling(identifier, out var sapling)) continue;

                var plant = sapling.GetComponent<Plant>();
                if (plant == null) continue;
                if (plant.m_attachDistance > 0f) continue;

                var pool = plantSeedProducing
                    ? containers.SeedHarvestNear(origin, boxRange)
                    : containers.CultivationNear(origin, boxRange);
                var seedPool = SeedPool.FromContainers(player, pool);
                if (pool.Count == 0 || !CropPlanting.CanAffordPlanting(seedPool, sapling, reserve))
                {
                    // Your boxes nearby cannot pay for it: ask for the nearest one another peer
                    // owns that can.
                    if (!containers.HasOwnedElsewhere) continue;
                    var askable = containers.OwnedElsewhere(plantSeedProducing
                        ? ContainerRole.SeedHarvest
                        : ContainerRole.Cultivation);
                    foreach (var box in FarmingContainers.Near(askable, origin, boxRange))
                    {
                        var boxPool = SeedPool.FromContainers(player, new List<Container> { box });
                        if (!CropPlanting.CanAffordPlanting(boxPool, sapling, reserve)) continue;
                        ContainerAccess.AskFor(box);
                        break;
                    }

                    continue;
                }

                // Only where a box near the spot can take the grown crop's yield, so farming can
                // harvest what it sows instead of leaving it to pickup.
                var sown = SowGrid(player, sapling, plant, seedPool, origin, reserve,
                    pos => containers.HasStorageNear(pos, boxRange) ||
                           containers.HasMatchingPlantingNear(plantSeedProducing, pos, boxRange));
                if (sown > 0)
                {
                    var cropId = identifier;
                    Automatics.Logger.Debug(() => $"Farming sowed {sown} [{cropId}] from containers");
                }
            }
        }

        // Each planting box sows from the input it holds into empty cultivated ground
        // within range of that box: cultivation boxes plant crop-producing saplings
        // (output not tagged seed), seed-harvest boxes plant seed-producing saplings
        // (output tagged seed). Gating on the box's own stock keeps it from planting a
        // sapling whose input it does not hold; the consume pool stays field-local
        // (the same-role boxes around that box), so the reserve is enforced against the
        // local supply.
        private static void SowFromPlantingContainers(Player player, FarmingContainers containers)
        {
            var boxRange = Config.ContainerSearchRange;
            SowFromBoxes(player, containers.OwnedCultivation, plantSeedProducing: false,
                pos => containers.CultivationNear(pos, boxRange));
            SowFromBoxes(player, containers.OwnedSeedHarvest, plantSeedProducing: true,
                pos => containers.SeedHarvestNear(pos, boxRange));

            // A planting box another peer owns is asked for when it would sow this pass.
            foreach (var box in containers.OwnedElsewhere(ContainerRole.Cultivation))
                if (WouldSow(player, box, plantSeedProducing: false))
                    ContainerAccess.AskFor(box.Container);
            foreach (var box in containers.OwnedElsewhere(ContainerRole.SeedHarvest))
                if (WouldSow(player, box, plantSeedProducing: true))
                    ContainerAccess.AskFor(box.Container);
        }

        private static void SowFromBoxes(Player player, List<DesignatedContainer> boxes,
            bool plantSeedProducing, System.Func<Vector3, List<Container>> poolNear)
        {
            var reserve = Config.SeedReserve;

            foreach (var box in boxes)
            {
                var inventory = box.Container.GetInventory();
                if (inventory == null) continue;

                var origin = box.Position;
                var seedPool = SeedPool.FromContainers(player, poolNear(origin));

                foreach (var identifier in Config.AllowFarmingCrops)
                {
                    // A box only plants saplings whose grown output matches its role:
                    // cultivation grows crops (not seed-tagged), seed-harvest grows
                    // seeds (seed-tagged). This is what keeps ordinary seeds out of a
                    // seed-multiplying field and vice versa.
                    if (ValheimObject.Flora.HasTag(identifier, "seed") != plantSeedProducing)
                        continue;
                    if (!CropPlanting.TryResolveSapling(identifier, out var sapling)) continue;

                    var plant = sapling.GetComponent<Plant>();
                    if (plant == null) continue;
                    if (plant.m_attachDistance > 0f) continue;
                    if (!ContainerHoldsSeed(inventory, sapling)) continue;
                    if (!CropPlanting.CanAffordPlanting(seedPool, sapling, reserve)) continue;

                    var sown = SowGrid(player, sapling, plant, seedPool, origin, reserve);
                    if (sown > 0)
                    {
                        var cropId = identifier;
                        var roleName = plantSeedProducing ? "seed-harvest" : "cultivation";
                        Automatics.Logger.Debug(() =>
                            $"Farming sowed {sown} [{cropId}] from a {roleName} container");
                    }
                }
            }
        }

        // ---- Shared harvest/replant/sow primitives ------------------------------

        // Harvests one crop and replants its sapling. When depositTargets is null
        // (Inventory mode) the yield is awarded to the player inventory exactly as
        // the released module did; otherwise (Containers mode) it is deposited into
        // the field-local deposit targets (storage first, then the matching planting
        // box) and the crop is left standing when none has room.
        private static void HarvestAndReplant(Player player, Pickable pickable, string identifier,
            SeedPool seedPool, IReadOnlyList<Container> depositTargets,
            IReadOnlyList<Container> ownedElsewhere = null)
        {
            if (!Objects.GetZNetView(pickable, out var zNetView) || !zNetView.IsValid()) return;

            // Only harvest crops this peer owns, so farming never picks a crop it
            // cannot also replant. In single-player every nearby crop is locally
            // owned, so this is a no-op there; it only prevents a destructive
            // delegated harvest of another peer's crop in multiplayer.
            if (!zNetView.IsOwner()) return;

            var position = pickable.transform.position;
            var rotation = pickable.transform.rotation;

            var outcome = depositTargets == null
                ? PickableHarvester.Harvest(player, pickable, zNetView)
                : PickableHarvester.HarvestToContainers(player, pickable, zNetView, depositTargets,
                    ownedElsewhere);

            if (outcome != PickableHarvester.HarvestOutcome.PickedByOwner)
            {
                // The crop was not picked. In Inventory mode the player inventory is
                // full or over weight; in Containers mode no deposit target in range
                // had room (the crop is left standing for a later pass). Either way
                // there is nothing to replant; non-owned crops were already skipped by
                // the ownership guard above.
                if (depositTargets != null)
                {
                    var cropId = identifier;
                    Automatics.Logger.Debug(() =>
                        $"Farming harvest skipped [{cropId}] at {position}: " +
                        "no deposit target with room");
                }

                return;
            }

            Replant(player, identifier, position, rotation, seedPool);
        }

        private static void Replant(Player player, string identifier, Vector3 pos, Quaternion rot,
            SeedPool seedPool)
        {
            if (!CropPlanting.TryResolveSapling(identifier, out var sapling))
            {
                Automatics.Logger.Debug(() =>
                    $"Farming replant skipped [{identifier}]: no sapling regrows this crop");
                return;
            }

            // The grown crop stands at its planted yaw plus a random turn; the sapling goes back
            // at the planted yaw, so its row keeps its orientation from one harvest to the next.
            rot = Quaternion.Euler(0f, LatticeSnapping.PlantedYaw(rot.eulerAngles.y), 0f);

            // Replanting only puts back what was harvested, so it does not keep the reserve;
            // the reserve holds planting input back from new ground only.
            if (CropPlanting.TryPlant(player, sapling, seedPool, pos, rot, 0,
                    out var reason))
                Automatics.Logger.Debug(() => $"Farming replanted [{identifier}] at {pos}");
            else
                Automatics.Logger.Debug(() => $"Farming replant skipped [{identifier}]: {reason}");
        }

        private static bool TryGetCropIdentifier(Pickable pickable, out string identifier)
        {
            var name = Objects.GetName(pickable);
            return ValheimObject.Flora.GetIdentify(name, out identifier) &&
                   Config.AllowFarmingCrops.Contains(identifier);
        }

        // Walks a per-sapling spacing grid centred on origin, sowing into empty
        // cultivated ground. The grid is bounded to a tile budget so a large
        // farming_range cannot turn one sow pass into a multi-thousand-tile sweep;
        // when the budget would be exceeded the sown radius is shrunk rather than
        // skipping the crop, so sowing still works in the area nearest the origin.
        private static int SowGrid(Player player, Piece sapling, Plant plant, SeedPool seedPool,
            Vector3 origin, int reserve, System.Func<Vector3, bool> cellAllowed = null,
            bool probe = false)
        {
            var step = plant.m_growRadius * Config.SowingSpacingFactor;
            if (step < 0.1f) return 0;

            var range = (float)Config.FarmingRange;
            if (Mathf.CeilToInt(range * 2f / step) + 1 > MaxSowStepsPerAxis)
                range = step * (MaxSowStepsPerAxis - 1) / 2f;

            var sown = 0;
            var rangeSqr = range * range;
            var zone = ZoneSystem.instance;
            // Without ZoneSystem the ground height is unknown; probing at the origin's y
            // (player or container height) would mis-sample cultivation, so skip the pass.
            if (zone == null) return sown;

            // The grid follows the rows of a growing crop of the same kind within the range, with
            // its spacing origin and orientation, so sown crops line up with hand-planted grids;
            // without one it is aligned to the world axes.
            // The world-axis grid is anchored at the world origin, so it stays put as you move.
            if (!LatticeSnapping.TryFindLattice(sapling.gameObject, origin, range,
                    out var latticeOrigin, out var lattice))
            {
                latticeOrigin = Vector3.zero;
                lattice = Quaternion.identity;
            }
            var local = Quaternion.Inverse(lattice) * (origin - latticeOrigin);
            var startX = Mathf.Ceil((local.x - range) / step) * step;
            var startZ = Mathf.Ceil((local.z - range) / step) * step;
            var maxX = local.x + range;
            var maxZ = local.z + range;

            for (var x = startX; x <= maxX; x += step)
            for (var z = startZ; z <= maxZ; z += step)
            {
                // Cheapest checks first: a horizontal squared-distance disc cull,
                // then cultivated ground, which the heightmap answers from x and z
                // alone and so before the ground ray, then the seed reserve, before
                // the physics-heavy plantability checks in TryPlant, which turn a
                // crowded cell down before the ward and roof checks.
                var dx = x - local.x;
                var dz = z - local.z;
                if (dx * dx + dz * dz > rangeSqr) continue;

                var pos = latticeOrigin + lattice * new Vector3(x, 0f, z);
                if (!CropPlanting.IsCultivated(pos, out var heightmap)) continue;
                pos.y = zone.GetGroundHeight(pos);
                if (cellAllowed != null && !cellAllowed(pos)) continue;
                if (!CropPlanting.CanAffordPlanting(seedPool, sapling, reserve)) return sown;

                // A probe plants nothing; it reports the first cell the sapling can grow on.
                if (probe)
                {
                    if (CropPlanting.CanPlaceAt(plant, heightmap, pos, out _)) return 1;
                    continue;
                }

                if (CropPlanting.TryPlant(player, sapling, plant, heightmap, seedPool, pos, lattice,
                        reserve, out _))
                    sown++;
            }

            return sown;
        }

        // Whether a planting box would sow this pass as SowFromBoxes does: it holds the input of
        // an allowed sapling of its role above the seed reserve, and the ground around it has a
        // cell that sapling can grow on.
        private static bool WouldSow(Player player, DesignatedContainer box, bool plantSeedProducing)
        {
            var reserve = Config.SeedReserve;
            var seedPool = SeedPool.FromContainers(player, new List<Container> { box.Container });
            foreach (var identifier in Config.AllowFarmingCrops)
            {
                if (ValheimObject.Flora.HasTag(identifier, "seed") != plantSeedProducing) continue;
                if (!CropPlanting.TryResolveSapling(identifier, out var sapling)) continue;

                var plant = sapling.GetComponent<Plant>();
                if (plant == null || plant.m_attachDistance > 0f) continue;
                if (!CropPlanting.CanAffordPlanting(seedPool, sapling, reserve)) continue;
                if (SowGrid(player, sapling, plant, seedPool, box.Position, reserve, probe: true) > 0)
                    return true;
            }

            return false;
        }

        // True when the container inventory holds every seed type the sapling
        // requires, so a seed chest only drives the crops it can actually stock.
        private static bool ContainerHoldsSeed(Inventory inventory, Piece sapling)
        {
            var hasRequirement = false;
            foreach (var requirement in sapling.m_resources)
            {
                if (!requirement.m_resItem) continue;

                var amount = requirement.GetAmount(0);
                if (amount <= 0) continue;

                hasRequirement = true;
                var name = requirement.m_resItem.m_itemData.m_shared.m_name;
                if (inventory.CountItems(name) <= 0) return false;
            }

            return hasRequirement;
        }

        // Distance from position to the nearest designated container, or false when
        // none is within range.
        private static bool TryNearestAnchorDistance(FarmingContainers containers,
            Vector3 position, float range, out float distance)
        {
            distance = float.MaxValue;
            var rangeSqr = range * range;
            var found = false;

            foreach (var anchor in containers.Anchors)
            {
                var distanceSqr = (anchor - position).sqrMagnitude;
                if (distanceSqr > rangeSqr) continue;

                found = true;
                if (distanceSqr < distance) distance = distanceSqr;
            }

            if (!found) return false;

            distance = Mathf.Sqrt(distance);
            return true;
        }
    }
}
