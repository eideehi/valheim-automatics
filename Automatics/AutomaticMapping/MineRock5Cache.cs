using System;
using System.Collections.Generic;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    // Per-MineRock5 snapshot of child colliders, pin center, and max
    // height taken once at Awake. Scan and destroy paths read the
    // snapshot instead of calling MineRock5.NonDestroyed and
    // GetComponentsInChildren<Collider> each tick. The snapshot stays
    // valid for the object's lifetime: vanilla only toggles m_destroyed
    // on hit areas, it never mutates the collider hierarchy. Freezing
    // the center at Awake also keeps the destroy path from reading
    // Collider.bounds after DamageArea has deactivated children —
    // inactive colliders report empty bounds at the origin, which would
    // skew Map.RemovePin toward the wrong pin.
    internal static class MineRock5Cache
    {
        internal readonly struct Snapshot
        {
            public readonly Collider[] Colliders;
            public readonly Vector3 Center;
            public readonly float MaxHeight;

            public Snapshot(Collider[] colliders, Vector3 center, float maxHeight)
            {
                Colliders = colliders;
                Center = center;
                MaxHeight = maxHeight;
            }

            public int ColliderCount => Colliders != null ? Colliders.Length : 0;
        }

        private static readonly Collider[] EmptyColliders = Array.Empty<Collider>();
        private static readonly Snapshot EmptySnapshot =
            new Snapshot(EmptyColliders, Vector3.zero, float.MinValue);

        private static readonly Dictionary<MineRock5, Snapshot> Snapshots
            = new Dictionary<MineRock5, Snapshot>();

        private static Func<MineRock5, bool> _nonDestroyed;
        private static bool _delegateInitialized;

        public static void InitializeDelegate()
        {
            if (_delegateInitialized) return;
            _delegateInitialized = true;

            try
            {
                var method = AccessTools.DeclaredMethod(typeof(MineRock5), "NonDestroyed");
                _nonDestroyed = method != null
                    ? AccessTools.MethodDelegate<Func<MineRock5, bool>>(method)
                    : null;
                if (_nonDestroyed == null)
                    Automatics.Logger.Warning(() =>
                        "MineRock5.NonDestroyed not found; falling back to reflection.");
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to bind MineRock5.NonDestroyed delegate; falling back to reflection: {e.Message}");
                _nonDestroyed = null;
            }
        }

        public static bool IsAlive(MineRock5 rock5)
        {
            if (!rock5) return false;
            return _nonDestroyed != null
                ? _nonDestroyed(rock5)
                : Reflections.InvokeMethod<bool>(rock5, "NonDestroyed");
        }

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

        // Scan-path helper: rebuild on miss only when the rock is still
        // alive (gated by IsAlive). The cold path exists for instances
        // that predate the Awake postfix binding (module init race).
        // Cache hits are also liveness-checked so a rock whose binding
        // OnDestroy has not yet fired (e.g. pre-destruction ZDO sync)
        // cannot feed the scan a stale Awake snapshot and spawn a pin
        // for a now-destroyed MineRock5.
        public static bool TryGetOrBuildSnapshotAlive(MineRock5 rock5, out Snapshot snapshot)
        {
            if (!rock5)
            {
                snapshot = EmptySnapshot;
                return false;
            }
            if (Snapshots.TryGetValue(rock5, out snapshot))
            {
                if (IsAlive(rock5)) return true;
                Snapshots.Remove(rock5);
                snapshot = EmptySnapshot;
                return false;
            }
            if (!IsAlive(rock5))
            {
                snapshot = EmptySnapshot;
                return false;
            }
            snapshot = BuildSnapshot(rock5);
            Snapshots[rock5] = snapshot;
            return true;
        }

        public static void Clear()
        {
            Snapshots.Clear();
        }

        private static Snapshot BuildSnapshot(MineRock5 rock5)
        {
            var colliders = rock5.gameObject.GetComponentsInChildren<Collider>() ?? EmptyColliders;
            if (colliders.Length == 0) return new Snapshot(EmptyColliders, Vector3.zero, float.MinValue);

            var sum = Vector3.zero;
            var max = float.MinValue;
            for (var i = 0; i < colliders.Length; i++)
            {
                var bounds = colliders[i].bounds;
                sum += bounds.center;
                if (bounds.max.y > max) max = bounds.max.y;
            }
            return new Snapshot(colliders, sum / colliders.Length, max);
        }
    }
}
