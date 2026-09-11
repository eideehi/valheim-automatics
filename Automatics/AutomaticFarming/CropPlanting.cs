using System;
using System.Collections.Generic;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    /// <summary>
    /// Resolves which sapling regrows a harvested crop and replicates vanilla
    /// <see cref="Plant"/> placement validity so saplings are placed only where
    /// they would settle <see cref="Plant.Status.Healthy"/>.
    /// </summary>
    /// <remarks>
    /// The reverse map is the inverse of <c>Plant.Grow()</c>: a sapling prefab
    /// carries both a <see cref="Plant"/> and a <see cref="Piece"/>, and
    /// <see cref="Plant.m_grownPrefabs"/> lists the grown prefab that carries the
    /// harvested <see cref="Pickable"/>. Keying by the grown crop's Flora
    /// identifier lets a harvested world crop look up its sapling, and the
    /// sapling's <see cref="Piece.m_resources"/> drives seed consumption.
    /// </remarks>
    internal static class CropPlanting
    {
        // Vanilla rejects a tilting placement when the ground normal's y drops
        // below this (about a 37-degree slope). See Player.UpdatePlacementGhost.
        private const float MinTiltNormalY = 0.8f;

        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> SpaceMaskLazy;
        private static readonly Lazy<int> RoofMaskLazy;
        private static readonly Lazy<int> GroundMaskLazy;

        private static Dictionary<string, Piece> _saplingByCrop;

        static CropPlanting()
        {
            ColliderBuffer = new Collider[128];
            SpaceMaskLazy = new Lazy<int>(() => LayerMask.GetMask(
                "Default", "static_solid", "Default_small", "piece", "piece_nonsolid"));
            RoofMaskLazy = new Lazy<int>(() =>
                LayerMask.GetMask("Default", "static_solid", "piece"));
            // Terrain only: the slope that decides whether a crop withers is the
            // cultivated ground's, which lives on the "terrain" layer (the same
            // layer ZoneSystem.GetGroundHeight queries). Including "piece" would
            // let the downward ray read a neighbouring sapling or a build piece
            // normal instead of the ground.
            GroundMaskLazy = new Lazy<int>(() => LayerMask.GetMask("terrain"));
        }

        private static int SpaceMask => SpaceMaskLazy.Value;
        private static int RoofMask => RoofMaskLazy.Value;
        private static int GroundMask => GroundMaskLazy.Value;

        /// <summary>True once the sapling lookup has been built, for the lifecycle smoke check.</summary>
        internal static bool SaplingMapBuilt => _saplingByCrop != null;

        /// <summary>
        /// Drops the session caches when the module is turned off (R6.1): the sapling lookup,
        /// which the first farming pass after the next activation builds again from the prefabs
        /// loaded then, and the colliders the last grow-space check left in the shared buffer,
        /// so no scene object stays reachable from here while the module is off.
        /// </summary>
        internal static void ClearCaches()
        {
            _saplingByCrop = null;
            Array.Clear(ColliderBuffer, 0, ColliderBuffer.Length);
        }

        /// <summary>
        /// Looks up the sapling whose grown crop matches <paramref name="cropIdentifier"/>
        /// (a <see cref="ValheimObject.Flora"/> identifier).
        /// </summary>
        public static bool TryResolveSapling(string cropIdentifier, out Piece sapling)
        {
            EnsureSaplingMap();
            if (_saplingByCrop != null &&
                _saplingByCrop.TryGetValue(cropIdentifier, out sapling) && sapling)
                return true;

            sapling = null;
            return false;
        }

        /// <summary>
        /// Plants <paramref name="sapling"/> at <paramref name="pos"/>/<paramref name="rot"/>
        /// drawing seeds from <paramref name="seedPool"/> when the spot is plantable
        /// and the required seeds stay at or above <paramref name="reserve"/> after
        /// consumption.
        /// </summary>
        public static bool TryPlant(Player player, Piece sapling, SeedPool seedPool, Vector3 pos,
            Quaternion rot, int reserve, out string reason)
        {
            var plant = sapling.GetComponent<Plant>();
            if (plant == null)
            {
                reason = "sapling has no Plant component";
                return false;
            }

            return TryPlant(player, sapling, plant, seedPool, pos, rot, reserve, out reason);
        }

        // Overload taking a pre-resolved Plant so the proactive-sow grid does not
        // repeat GetComponent<Plant> on every candidate tile, and a pre-resolved
        // SeedPool so a sow pass enumerates nearby containers once instead of per
        // tile (the container set cannot change within a single synchronous pass).
        public static bool TryPlant(Player player, Piece sapling, Plant plant, SeedPool seedPool,
            Vector3 pos, Quaternion rot, int reserve, out string reason)
        {
            if (!PrivateArea.CheckAccess(pos, 0f, false))
            {
                reason = "no build access (PrivateArea)";
                return false;
            }

            if (!CanPlantAt(plant, pos, out var status))
            {
                reason = status.ToString();
                return false;
            }

            // Vanilla forbids placing a tilt-sensitive piece on a steep slope
            // (Player.UpdatePlacementGhost). Plant.UpdateHealth does not model
            // this, so without the guard a sapling planted on a slope edge wastes
            // a seed and withers.
            if (sapling.m_notOnTiltingSurface && IsTooTilted(pos))
            {
                reason = "tilting surface";
                return false;
            }

            // The reserve gate and the consume step below share the one resolved
            // pool, so they see the same container set (in Inventory mode no
            // container is touched at all).
            if (!HasSeedAboveReserve(seedPool, sapling, reserve, out var seedInfo))
            {
                reason = $"seed reserve ({seedInfo})";
                return false;
            }

            player.PlacePiece(sapling, pos, rot, false);

            // Flush the freshly instantiated sapling's collider into the physics
            // world. Without this, the grow-space OverlapSphere for the next crop
            // placed in the same farming pass would not see this one, so several
            // crops could be planted within each other's grow radius and all
            // settle NoSpace once grown. (The effect compounds in interval mode,
            // which runs every farming_interval.)
            Physics.SyncTransforms();

            // Mirror vanilla placement (Player.Update): seeds are only consumed
            // when the world does not grant free building for this piece.
            if (ZoneSystem.instance == null ||
                !ZoneSystem.instance.GetGlobalKey(sapling.FreeBuildKey()))
                ConsumeSeeds(player, seedPool, sapling);

            reason = "";
            return true;
        }

        /// <summary>
        /// Replicates <c>Plant.UpdateHealth</c> + <c>HaveRoof</c> + <c>HaveGrowSpace</c>
        /// for a freshly placed sapling so callers can reject spots that would
        /// settle anything other than <see cref="Plant.Status.Healthy"/>.
        /// </summary>
        public static bool CanPlantAt(Plant sapling, Vector3 pos, out Plant.Status reason)
        {
            reason = Plant.Status.Healthy;

            // Attach/vine crops (m_attachDistance > 0) need a build-piece support
            // pose that this automation does not compute; placed at a plain
            // position they settle NoAttachPiece. Skip them instead of wasting
            // seeds on a sapling that will never grow.
            if (sapling.m_attachDistance > 0f)
            {
                reason = Plant.Status.NoAttachPiece;
                return false;
            }

            var heightmap = Heightmap.FindHeightmap(pos);
            if (heightmap)
            {
                var biome = heightmap.GetBiome(pos);
                if ((biome & sapling.m_biome) == 0)
                {
                    reason = Plant.Status.WrongBiome;
                    return false;
                }

                if (sapling.m_needCultivatedGround && !heightmap.IsCultivated(pos))
                {
                    reason = Plant.Status.NotCultivated;
                    return false;
                }

                if (!sapling.m_tolerateHeat && biome == Heightmap.Biome.AshLands &&
                    !ShieldGenerator.IsInsideShield(pos))
                {
                    reason = Plant.Status.TooHot;
                    return false;
                }

                if (!sapling.m_tolerateCold &&
                    (biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain) &&
                    !ShieldGenerator.IsInsideShield(pos))
                {
                    reason = Plant.Status.TooCold;
                    return false;
                }
            }

            if (HasRoof(pos))
            {
                reason = Plant.Status.NoSun;
                return false;
            }

            if (!HasGrowSpace(pos, sapling.m_growRadius))
            {
                reason = Plant.Status.NoSpace;
                return false;
            }

            return true;
        }

        /// <summary>True when the required seeds for <paramref name="sapling"/> stay at or above <paramref name="reserve"/> after one planting.</summary>
        public static bool CanAffordPlanting(SeedPool seedPool, Piece sapling, int reserve)
        {
            return HasSeedAboveReserve(seedPool, sapling, reserve, out _);
        }

        /// <summary>True when <paramref name="pos"/> sits on cultivated ground.</summary>
        public static bool IsCultivated(Vector3 pos)
        {
            var heightmap = Heightmap.FindHeightmap(pos);
            return heightmap && heightmap.IsCultivated(pos);
        }

        // Samples the cultivated ground normal at the candidate spot with a short
        // downward raycast against the terrain layer and reports whether the slope
        // is too steep to plant a tilt-sensitive crop (vanilla rejects placement
        // when the ground normal's y is below MinTiltNormalY).
        private static bool IsTooTilted(Vector3 pos)
        {
            var origin = pos + Vector3.up * 1f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, 4f, GroundMask))
                return hit.normal.y < MinTiltNormalY;

            return false;
        }

        private static bool HasRoof(Vector3 pos)
        {
            // Vanilla Plant.HaveRoof casts a single infinitely-thin ray straight
            // up. At the edge or seam of a floor/roof piece that ray can flip
            // between hit and miss across frames (floating point + broadphase
            // ordering), so a sapling can pass this pre-check yet the grown plant's
            // own later raycast reads NoSun. Keep the vanilla ray for parity, then
            // add a slightly thicker upward SphereCast to catch the grazed
            // edge/seam.
            if (Physics.Raycast(pos, Vector3.up, 100f, RoofMask)) return true;

            // Lift the origin above the sphere radius so the cast does not start
            // already overlapping ground/piece geometry (a SphereCast that begins
            // inside a collider reports no hit, which would defeat the seam catch).
            const float radius = 0.25f;
            var origin = pos + Vector3.up * (radius + 0.1f);
            return Physics.SphereCast(origin, radius, Vector3.up, out _, 100f, RoofMask);
        }

        // Mirrors Plant.HaveGrowSpace, which treats any non-Plant collider and any
        // other healthy Plant in the grow radius as blocking. The one deliberate
        // difference: the just-harvested crop (an already-picked Pickable that is
        // despawning) is ignored so it does not self-block its own replant. A
        // still-standing crop (un-picked Pickable) DOES block, matching vanilla,
        // so a sapling is never placed where it would later settle NoSpace.
        private static bool HasGrowSpace(Vector3 pos, float radius)
        {
            var count = Physics.OverlapSphereNonAlloc(pos, radius, ColliderBuffer, SpaceMask);
            for (var i = 0; i < count; i++)
            {
                var collider = ColliderBuffer[i];

                var plant = collider.GetComponentInParent<Plant>();
                if (plant != null)
                {
                    if (plant.GetStatus() == Plant.Status.Healthy) return false;
                    continue;
                }

                var pickable = collider.GetComponentInParent<Pickable>();
                if (pickable != null && pickable.GetPicked()) continue;
                return false;
            }

            return true;
        }

        private static bool HasSeedAboveReserve(SeedPool pool, Piece sapling, int reserve,
            out string seedInfo)
        {
            foreach (var requirement in sapling.m_resources)
            {
                if (!requirement.m_resItem) continue;

                var amount = requirement.GetAmount(0);
                if (amount <= 0) continue;

                var name = requirement.m_resItem.m_itemData.m_shared.m_name;
                var have = pool.Count(name);
                if (have - amount < reserve)
                {
                    seedInfo = $"{name}: have {have}, need {amount} keeping {reserve}";
                    return false;
                }
            }

            seedInfo = "";
            return true;
        }

        // Consumes the sapling's seed requirements from the resolved pool. Inventory
        // mode keeps the exact vanilla consume path so default behavior is unchanged;
        // Containers mode removes by name from the field-local Seed-role container
        // pool (the player inventory is never charged).
        private static void ConsumeSeeds(Player player, SeedPool pool, Piece sapling)
        {
            if (pool.Source == SeedSource.Inventory)
            {
                player.ConsumeResources(sapling.m_resources, 0);
                return;
            }

            foreach (var requirement in sapling.m_resources)
            {
                if (!requirement.m_resItem) continue;

                var amount = requirement.GetAmount(0);
                if (amount <= 0) continue;

                var name = requirement.m_resItem.m_itemData.m_shared.m_name;
                var removed = pool.Consume(name, amount);

                // The reserve gate already proved the pool holds enough, so a
                // shortfall would mean the container set changed mid-pass; surface
                // it at Debug level rather than over-draw.
                if (removed < amount)
                    Automatics.Logger.Debug(() =>
                        $"Farming seed consume shortfall for {name}: removed {removed} of {amount}");
            }
        }

        private static void EnsureSaplingMap()
        {
            if (_saplingByCrop != null) return;

            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return;

            var map = new Dictionary<string, Piece>();
            foreach (var prefab in scene.m_prefabs)
            {
                if (!prefab) continue;

                var plant = prefab.GetComponent<Plant>();
                if (!plant || plant.m_grownPrefabs == null) continue;

                var piece = prefab.GetComponent<Piece>();
                if (!piece) continue;

                foreach (var grown in plant.m_grownPrefabs)
                {
                    if (!grown) continue;

                    var pickable = grown.GetComponent<Pickable>() ??
                                   grown.GetComponentInChildren<Pickable>();
                    if (!pickable || !pickable.m_itemPrefab) continue;

                    var itemDrop = pickable.m_itemPrefab.GetComponent<ItemDrop>();
                    if (itemDrop == null) continue;

                    var cropName = itemDrop.m_itemData.m_shared.m_name;
                    if (!ValheimObject.Flora.GetIdentify(cropName, out var identifier)) continue;
                    if (map.ContainsKey(identifier)) continue;

                    map[identifier] = piece;

                    var saplingName = Objects.GetPrefabName(prefab);
                    Automatics.Logger.Debug(() =>
                        $"Farming sapling resolved: {identifier} -> {saplingName} " +
                        $"(seed: {DescribeSeeds(piece)})");
                }
            }

            _saplingByCrop = map;
        }

        private static string DescribeSeeds(Piece sapling)
        {
            var parts = new List<string>();
            foreach (var requirement in sapling.m_resources)
            {
                if (!requirement.m_resItem) continue;

                var amount = requirement.GetAmount(0);
                if (amount <= 0) continue;

                parts.Add($"{requirement.m_resItem.m_itemData.m_shared.m_name} x{amount}");
            }

            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none";
        }
    }
}
