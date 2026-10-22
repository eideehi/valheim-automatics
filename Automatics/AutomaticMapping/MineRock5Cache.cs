using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    // Per-MineRock5 snapshot of vanilla hit-area colliders, pin center,
    // and max height taken once at Awake. Scan and destroy paths read the
    // snapshot instead of GetComponentsInChildren<Collider> each tick.
    // Freezing the bounds keeps scan-time reads away from colliders that
    // DamageArea later deactivates — inactive colliders report empty
    // bounds at the origin, which would skew the pin position.
    internal static class MineRock5Cache
    {
        internal readonly struct Snapshot
        {
            public readonly Collider[] Colliders;
            public readonly Vector3[] Centers;
            public readonly float[] MaxHeights;
            public readonly Vector3 Center;
            public readonly float MaxHeight;

            public Snapshot(Collider[] colliders, Vector3[] centers, float[] maxHeights,
                Vector3 center, float maxHeight)
            {
                Colliders = colliders;
                Centers = centers;
                MaxHeights = maxHeights;
                Center = center;
                MaxHeight = maxHeight;
            }

            public int ColliderCount => Colliders != null ? Colliders.Length : 0;
        }

        private static readonly Collider[] EmptyColliders = Array.Empty<Collider>();
        private static readonly Vector3[] EmptyCenters = Array.Empty<Vector3>();
        private static readonly float[] EmptyMaxHeights = Array.Empty<float>();
        private static readonly Snapshot EmptySnapshot =
            new Snapshot(EmptyColliders, EmptyCenters, EmptyMaxHeights, Vector3.zero,
                float.MinValue);

        private static readonly Dictionary<MineRock5, Snapshot> Snapshots
            = new Dictionary<MineRock5, Snapshot>();

        private static readonly Vector3[] ShapeCorners = new Vector3[8];

        public static void Register(MineRock5 rock5)
        {
            if (!rock5) return;
            // Indexer (not Add) so re-entrant Awake calls — e.g. a
            // pool-driven respawn on the same MonoBehaviour instance —
            // overwrite instead of throwing.
            Snapshots[rock5] = BuildSnapshot(rock5);
        }

        // C# reference null check (not the Unity destroyed-object check),
        // so the binding's OnDestroy can still evict the dictionary entry
        // after Unity has zeroed the native side but the C# reference is
        // still usable as a Dictionary key.
        public static void Unregister(MineRock5 rock5)
        {
            if ((object)rock5 == null) return;
            Snapshots.Remove(rock5);
        }

        // Pure cache lookup: returns false when no snapshot exists and
        // does NOT rebuild. Destroy-path callers use this so we never
        // snapshot already-deactivated hit areas — a post-damage
        // BuildSnapshot would skew the center toward the origin and
        // trick Map.RemovePin into missing the real pin.
        public static bool TryGetSnapshot(MineRock5 rock5, out Snapshot snapshot)
        {
            if (!rock5)
            {
                snapshot = EmptySnapshot;
                return false;
            }
            return Snapshots.TryGetValue(rock5, out snapshot);
        }

        public static bool TryGetLivePosition(MineRock5 rock5, out Vector3 position,
            out float maxHeight)
        {
            position = Vector3.zero;
            maxHeight = float.MinValue;

            if (!rock5) return false;

            var snapshot = GetOrBuildSnapshot(rock5);
            if (snapshot.ColliderCount == 0) return false;

            var healthData = GetHealthData(rock5);
            if (healthData.Length == 0)
                return TryGetLivePosition(snapshot, snapshot.ColliderCount, null, out position,
                    out maxHeight);

            try
            {
                var package = new ZPackage(Convert.FromBase64String(healthData));
                var healthCount = package.ReadInt();
                return TryGetLivePosition(snapshot, healthCount, package, out position,
                    out maxHeight);
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to read MineRock5 health state for mapping: {e.Message}");
                return false;
            }
        }

        // The centre over all hit areas, mined or not. Mining does not move it, so the static pass
        // looks for the rock's saved pin around it.
        public static bool TryGetCenter(MineRock5 rock5, out Vector3 center)
        {
            center = Vector3.zero;
            if (!rock5) return false;

            var snapshot = GetOrBuildSnapshot(rock5);
            if (snapshot.ColliderCount == 0) return false;

            center = snapshot.Center;
            return true;
        }

        public static void Clear()
        {
            Snapshots.Clear();
        }

        private static Snapshot GetOrBuildSnapshot(MineRock5 rock5)
        {
            if (!Snapshots.TryGetValue(rock5, out var snapshot))
            {
                snapshot = BuildSnapshot(rock5);
                Snapshots[rock5] = snapshot;
            }
            return snapshot;
        }

        private static string GetHealthData(MineRock5 rock5)
        {
            var zNetView = rock5.GetComponent<ZNetView>();
            var zdo = zNetView != null ? zNetView.GetZDO() : null;
            return zdo != null ? zdo.GetString(ZDOVars.s_health) : string.Empty;
        }

        private static bool TryGetLivePosition(Snapshot snapshot, int healthCount,
            ZPackage package, out Vector3 position, out float maxHeight)
        {
            var count = 0;
            var sum = Vector3.zero;
            maxHeight = float.MinValue;

            for (var i = 0; i < snapshot.ColliderCount; i++)
            {
                var alive = true;
                if (package != null && i < healthCount)
                    alive = package.ReadSingle() > 0f;
                if (!alive) continue;

                sum += snapshot.Centers[i];
                if (snapshot.MaxHeights[i] > maxHeight) maxHeight = snapshot.MaxHeights[i];
                count++;
            }

            // count > 0 already proves at least one live collider was aggregated;
            // a zero sum is a legitimate centroid (matches the sibling mineral paths).
            if (count == 0)
            {
                position = Vector3.zero;
                maxHeight = float.MinValue;
                return false;
            }

            position = sum / count;
            return true;
        }

        private static Snapshot BuildSnapshot(MineRock5 rock5)
        {
            var colliders = GetHitAreaColliders(rock5);
            if (colliders.Length == 0) return EmptySnapshot;

            var sum = Vector3.zero;
            var max = float.MinValue;
            var centers = new Vector3[colliders.Length];
            var maxHeights = new float[colliders.Length];
            for (var i = 0; i < colliders.Length; i++)
            {
                var bounds = GetBounds(colliders[i]);
                centers[i] = bounds.center;
                maxHeights[i] = bounds.max.y;
                sum += centers[i];
                if (maxHeights[i] > max) max = maxHeights[i];
            }
            return new Snapshot(colliders, centers, maxHeights, sum / colliders.Length, max);
        }

        // A hit area mined before the snapshot is off: the rock's own Awake turns off those mined
        // before it loaded. An inactive collider reports empty bounds at the origin, so these take
        // their bounds from the collider's shape, and the centre over all hit areas is the same
        // whenever the rock was mined.
        private static Bounds GetBounds(Collider collider)
        {
            if ((collider.enabled && collider.gameObject.activeInHierarchy) ||
                !PinTooltipContent.TryGetShapeCorners(collider, ShapeCorners))
                return collider.bounds;

            var bounds = new Bounds(ShapeCorners[0], Vector3.zero);
            for (var i = 1; i < ShapeCorners.Length; i++)
                bounds.Encapsulate(ShapeCorners[i]);
            return bounds;
        }

        private static Collider[] GetHitAreaColliders(MineRock5 rock5)
        {
            var hitAreas = Reflections.GetField<IList>(rock5, "m_hitAreas");
            if (hitAreas == null || hitAreas.Count == 0) return EmptyColliders;

            var colliders = new Collider[hitAreas.Count];
            var count = 0;
            for (var i = 0; i < hitAreas.Count; i++)
            {
                var hitArea = hitAreas[i];
                if (hitArea == null) continue;

                var colliderField = AccessTools.Field(hitArea.GetType(), "m_collider");
                var collider = colliderField?.GetValue(hitArea) as Collider;
                if (!collider) continue;

                colliders[count++] = collider;
            }

            if (count == 0) return EmptyColliders;
            if (count == colliders.Length) return colliders;

            Array.Resize(ref colliders, count);
            return colliders;
        }
    }
}
