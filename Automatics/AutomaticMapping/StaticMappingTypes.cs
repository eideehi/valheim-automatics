using UnityEngine;

namespace Automatics.AutomaticMapping
{
    /// <summary>
    /// Unified classification for pins handled by the static mapping path plus
    /// location-only kinds (Dungeon/Spot). The first five values are reachable
    /// from component-based classification (Flora/Mineral/Spawner/Other/Portal)
    /// while Dungeon and Spot only originate from ZoneSystem location scans.
    /// </summary>
    internal enum PinKind
    {
        Flora,
        Mineral,
        Spawner,
        Other,
        Portal,
        Dungeon,
        Spot
    }

    /// <summary>
    /// Identifies which classification domain produced a pin so targeted
    /// invalidation can apply the right re-classifier (component source-token
    /// vs location prefab name).
    /// </summary>
    internal enum PinSourceDomain
    {
        Component,
        Location
    }

    /// <summary>
    /// Value type stored in StaticObjectCache (and its pending-fill companion).
    /// Produced by ClassifyStaticObject and is deliberately self-contained:
    /// Mapping() iteration should never need to re-run Objects.GetName or a
    /// ValheimObject.*.GetIdentify lookup to act on a cached entry.
    ///
    /// Collider keys are retained to let the two-phase fill in A-12 dedupe
    /// overlapping OverlapBox results idempotently via indexer assignment.
    /// </summary>
    internal struct ClassifiedStaticObject
    {
        public Component Component;
        public PinKind Kind;
        public string Identifier;
        public Vector3 Position;
        public string SourceToken;
    }

    /// <summary>
    /// Static-mapping PinDataCache entry. Carries the classification metadata
    /// needed for targeted invalidation (Kind/Identifier/SourceToken/Domain)
    /// and the sweep generation used for stale deletion. Flora clusters
    /// register the same <see cref="PinData"/> under multiple
    /// <c>MapPinIdentify</c> keys (one per FloraNode) — in that case each
    /// per-key entry tracks its own <see cref="LastSeenSweep"/> and stale
    /// judgement uses the max across all keys pointing at a given pin.
    /// </summary>
    internal sealed class PinCacheEntry
    {
        public Minimap.PinData PinData;
        public PinKind Kind;
        public string Identifier;
        public string SourceToken;
        public PinSourceDomain Domain;
        public int LastSeenSweep;
    }

    /// <summary>
    /// Records the scan parameters that produced the in-progress pending
    /// StaticObjectCache. Retry ticks compare the current scan parameters
    /// against the snapshot using a shared origin tolerance; any mismatch
    /// invalidates the carry-over and the scan restarts from scratch.
    /// </summary>
    internal struct PendingScanSnapshot
    {
        public Vector3 Origin;
        public float Range;
        public int Mask;
        public int StaticClassifierVersion;
    }

    /// <summary>
    /// One rectangular sub-region scheduled for the OverlapBox tile-split
    /// fallback when the single-sphere OverlapSphereNonAlloc saturates at
    /// the buffer ceiling. <see cref="Center"/> stores the full 3D center
    /// (y equals the snapshot origin y); <see cref="HalfXZ"/> is the XZ
    /// half-side before margin, kept so that saturated tiles can recurse
    /// into four equal sub-tiles. Y extent is shared across all tiles and
    /// derives from the snapshot range.
    /// </summary>
    internal struct PendingTile
    {
        public Vector3 Center;
        public float HalfXZ;
        public int Depth;
    }
}
