using static Automatics.ValheimObject;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class AutomaticMapPinning
    {
        public static bool IsActive()
        {
            return Config.AutomaticMapPinningEnabled && !Game.IsPaused() && Player.m_localPlayer;
        }

        public static bool IsFlora(Pickable pickable)
        {
            return Flora.GetFlag(Utility.GetName(pickable), out _);
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