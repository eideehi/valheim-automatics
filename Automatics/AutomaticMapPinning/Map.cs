using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class Map
    {
        public static IEnumerable<Minimap.PinData> Pins =>
            Reflection.GetField<List<Minimap.PinData>>(Minimap.instance, "m_pins");

        public static bool HavePinInRange(Vector3 pos, float radius)
        {
            return Pins.Any(data => Utils.DistanceXZ(data.m_pos, pos) <= radius);
        }

        public static Minimap.PinData AddPin(Vector3 pos, string name, bool save)
        {
            var data = Minimap.instance.AddPin(pos, Minimap.PinType.Icon3, name, save, false);
            Log.Debug(() => $"Add pin: [name: {data.m_name}, pos: {data.m_pos}]");
            return data;
        }

        public static void RemovePin(Minimap.PinData data)
        {
            Minimap.instance.RemovePin(data);
            Log.Debug(() => $"Remove pin: [name: {data.m_name}, pos: {data.m_pos}]");
        }
    }
}