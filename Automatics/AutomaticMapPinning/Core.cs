using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class Core
    {
        public static void Initialize()
        {
            Config.Initialize();
            Automatics.OnInitTerminal += Command.Register;

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

        public static void OnUpdate()
        {
            if (Game.IsPaused() || !Player.m_localPlayer) return;

            var origin = Player.m_localPlayer.transform.position;

            var location = Location.GetLocation(origin);
            if (location && location.m_hasInterior) return;

            DynamicMapPinning.Run(origin, Time.deltaTime);
            StaticMapPinning.Run(origin);
        }
    }

    [DisallowMultipleComponent]
    public sealed class ShipCache : InstanceCache<Ship>
    {
    }
}