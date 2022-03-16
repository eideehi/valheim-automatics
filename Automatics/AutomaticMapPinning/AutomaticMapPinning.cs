using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class AutomaticMapPinning
    {
        public static void Initialize()
        {
            PickableCache.AddAwakeListener(pickable =>
            {
                if (StaticMapPinning.IsFlora(pickable))
                    pickable.gameObject.AddComponent<FloraObject>();
            });
        }

        public static bool IsActive()
        {
            return Config.AutomaticMapPinningEnabled && !Game.IsPaused() && Player.m_localPlayer;
        }

        public static void OnUpdate()
        {
            var origin = Player.m_localPlayer.transform.position;

            var location = Location.GetLocation(origin);
            if (location && location.m_hasInterior) return;

            DynamicMapPinning.Run(origin, Time.deltaTime);
            StaticMapPinning.Run(origin);
        }
    }
}