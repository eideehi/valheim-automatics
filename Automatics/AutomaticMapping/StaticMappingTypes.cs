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
}
