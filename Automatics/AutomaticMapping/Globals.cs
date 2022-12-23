using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class MappingObject
    {
        public static ValheimObject Vehicle { get; } = new ValheimObject("vehicle");
        public static ValheimObject Other { get; } = new ValheimObject("other");
    }

    [DisallowMultipleComponent]
    public sealed class FishCache : InstanceCache<Fish>
    {
    }

    [DisallowMultipleComponent]
    public sealed class BirdCache : InstanceCache<RandomFlyingBird>
    {
    }

    [DisallowMultipleComponent]
    public sealed class ShipCache : InstanceCache<Piece>
    {
    }
}