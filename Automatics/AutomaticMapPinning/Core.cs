using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class Core
    {
        public static void Initialize()
        {
            Config.Initialize();

            PickableCache.AddAwakeListener(pickable =>
            {
                if (StaticMapPinning.IsFlora(pickable))
                    pickable.gameObject.AddComponent<FloraObject>();
            });

            ShipCache.AddAwakeListener(ship =>
            {
                var shipObj = ship.GetComponent<WearNTear>();
                if (shipObj == null) return;

                shipObj.m_onDestroyed += () =>
                {
                    if (Map.FindPinInRange(ship.transform.position, 4f, out var data))
                        Map.RemovePin(data);
                };
            });
        }

        public static bool IsActive()
        {
            return Config.AutomaticMapPinningEnabled && !Game.IsPaused() && Player.m_localPlayer;
        }

        public static void OnUpdate(Player player, float delta, bool takeInput)
        {
            if (Game.IsPaused()) return;
            if (player.InInterior()) return;

            var origin = player.transform.position;
            DynamicMapPinning.Run(origin, delta);
            StaticMapPinning.Run(origin, takeInput);
        }
    }

    [DisallowMultipleComponent]
    public sealed class ShipCache : InstanceCache<Ship>
    {
    }
}