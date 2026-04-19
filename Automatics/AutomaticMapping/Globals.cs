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

    // OnDestroy hook that evicts the MineRock5Cache entry on zone
    // unload. The MineRock5_DamageArea transpiler only covers
    // fully-mined rocks; unfinished rocks that despawn with their
    // zone would otherwise keep strong references in the cache until
    // the next world unload.
    [DisallowMultipleComponent]
    internal sealed class MineRock5CacheBinding : MonoBehaviour
    {
        private MineRock5 _rock;

        public void Initialize(MineRock5 rock)
        {
            _rock = rock;
            MineRock5Cache.Register(rock);
        }

        private void OnDestroy()
        {
            MineRock5Cache.Unregister(_rock);
            _rock = null;
        }
    }
}