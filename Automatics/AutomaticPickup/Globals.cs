using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    [DisallowMultipleComponent]
    internal sealed class PickableItemCache : InstanceCache<PickableItem>, IScopedComponent
    {
        public int ScopeGeneration { get; set; }

        /// <summary>
        ///     Nothing here outlives the frame it is destroyed in: the cache owns no coroutine
        ///     and no timer, and the base class removes the instance from the shared set in its
        ///     own OnDestroy. The hook exists because the scope destroys every component it owns
        ///     through the same two steps.
        /// </summary>
        public void OnScopeDetaching()
        {
        }
    }
}