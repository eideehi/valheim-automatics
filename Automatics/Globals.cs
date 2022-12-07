using System;
using ModUtils;
using UnityEngine;

namespace Automatics
{
    internal static class Hooks
    {
        public static Action<Player> OnPlayerAwake { get; set; }
        public static Action<Player, bool> OnPlayerUpdate { get; set; }
        public static Action<Player, float> OnPlayerFixedUpdate { get; set; }
        public static Action OnInitTerminal { get; set; }
    }

    [DisallowMultipleComponent]
    internal sealed class ContainerCache : InstanceCache<Container>
    {
    }

    [DisallowMultipleComponent]
    internal sealed class PickableCache : InstanceCache<Pickable>
    {
    }
}