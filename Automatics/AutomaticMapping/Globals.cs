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

    // The three caches below own no coroutine and no timer, and the base class removes the
    // instance from its shared set in its own OnDestroy, so nothing of theirs outlives the frame
    // they are destroyed in. OnScopeDetaching exists because the scope destroys every component it
    // owns through the same two steps.

    [DisallowMultipleComponent]
    public sealed class FishCache : InstanceCache<Fish>, IScopedComponent
    {
        public int ScopeGeneration { get; set; }

        public void OnScopeDetaching()
        {
        }
    }

    [DisallowMultipleComponent]
    public sealed class BirdCache : InstanceCache<RandomFlyingBird>, IScopedComponent
    {
        public int ScopeGeneration { get; set; }

        public void OnScopeDetaching()
        {
        }
    }

    [DisallowMultipleComponent]
    public sealed class ShipCache : InstanceCache<Piece>, IScopedComponent
    {
        public int ScopeGeneration { get; set; }

        public void OnScopeDetaching()
        {
        }
    }

    // OnDestroy hook that evicts the MineRock5Cache entry on zone
    // unload. The MineRock5_DamageArea transpiler only covers
    // fully-mined rocks; unfinished rocks that despawn with their
    // zone would otherwise keep strong references in the cache until
    // the next world unload.
    [DisallowMultipleComponent]
    internal sealed class MineRock5CacheBinding : MonoBehaviour, IScopedComponent
    {
        private MineRock5 _rock;

        public int ScopeGeneration { get; set; }

        public void Initialize(MineRock5 rock)
        {
            _rock = rock;
            MineRock5Cache.Register(rock);
        }

        // A disable clears the whole snapshot cache before the scope destroys this binding, so
        // the eviction in OnDestroy is all there is to do, and it can wait for the destruction.
        public void OnScopeDetaching()
        {
        }

        private void OnDestroy()
        {
            MineRock5Cache.Unregister(_rock);
            _rock = null;
        }
    }
}
