using System;
using ModUtils;
using UnityEngine;

namespace Automatics
{
    internal static class Hooks
    {
        public static Action<Player, ZNetView> OnPlayerAwake { get; set; }
        public static Action<Player, ZNetView> OnLocalPlayerAwake { get; set; }
        public static Action<Player, bool> OnPlayerUpdate { get; set; }
        public static Action<Player, float> OnPlayerFixedUpdate { get; set; }
        public static Action<float> OnDedicatedServerFixedUpdate { get; set; }
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

    internal static class MessageExtensions
    {
        // Single source of truth for the Message-position -> MessageHud.MessageType
        // mapping that the repair, storage, and door modules each used to inline.
        // Callers handle Message.None (suppress the message) before calling this.
        public static MessageHud.MessageType ToMessageType(this Message message)
        {
            return message == Message.Center
                ? MessageHud.MessageType.Center
                : MessageHud.MessageType.TopLeft;
        }
    }
}