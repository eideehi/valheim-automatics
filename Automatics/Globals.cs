using ModUtils;
using UnityEngine;

namespace Automatics
{
    [DisallowMultipleComponent]
    public sealed class ContainerCache : InstanceCache<Container>
    {
    }

    [DisallowMultipleComponent]
    public sealed class PickableCache : InstanceCache<Pickable>
    {
    }
}